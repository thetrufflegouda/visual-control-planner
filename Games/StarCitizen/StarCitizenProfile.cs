using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace VisualControlsBinder.Games;

public sealed record StarCitizenDevice(int Instance, string Product)
{
    public Guid? ProductGuid
    {
        get
        {
            int start = Product.LastIndexOf('{');
            return start >= 0 && Guid.TryParse(Product.AsSpan(start), out var guid) ? guid : null;
        }
    }
}

public sealed class StarCitizenProfile
{
    private const string FilePrefix = "layout_";
    private const string FileSuffix = "_exported";
    private readonly XDocument document;
    private readonly XElement root;
    private byte[] sourceBytes;
    private bool hasChanges;

    internal StarCitizenProfile(string path)
    {
        SourcePath = Path.GetFullPath(path);
        sourceBytes = File.ReadAllBytes(SourcePath);
        using var original = new MemoryStream(sourceBytes, writable: false);
        document = XDocument.Load(original, LoadOptions.PreserveWhitespace);
        root = document.Root ?? throw new InvalidDataException("The Star Citizen profile is empty.");
        if (root.Name != "ActionMaps" || root.Element("ActionProfiles") is not null)
            throw new InvalidDataException("Choose a Star Citizen Control Profiles export from Controls\\Mappings.");
    }

    public string SourcePath { get; }

    public IReadOnlyList<StarCitizenDevice> Devices => JoystickOptions()
        .Where(option => !string.IsNullOrWhiteSpace(option.Attribute("Product")?.Value))
        .Select(option => new StarCitizenDevice((int)option.Attribute("instance")!, (string)option.Attribute("Product")!))
        .OrderBy(device => device.Instance)
        .ToArray();

    internal bool IsVJoyOnly => Devices is [var device] && device.ProductGuid == StarCitizenVJoyBindings.ProductId &&
        HasUniqueJoystickSlot(device.Instance) && JoystickOptions().Count() == 1 &&
        Bindings.All(binding => !StarCitizenGame.IsJoystickInput(binding.Input.Trim()) ||
            binding.Input.Trim().StartsWith($"js{device.Instance}_", StringComparison.OrdinalIgnoreCase));

    internal bool HasUniqueJoystickSlot(int slot) =>
        JoystickOptions().Count(option => (int)option.Attribute("instance")! == slot) == 1;

    public IReadOnlyList<GameBinding> Bindings =>
        (from map in root.Elements("actionmap")
         let mapId = map.Attribute("name")?.Value
         where !string.IsNullOrWhiteSpace(mapId)
         from action in map.Elements("action")
         let actionId = action.Attribute("name")?.Value
         where !string.IsNullOrWhiteSpace(actionId)
         from rebind in action.Elements("rebind")
         let input = rebind.Attribute("input")?.Value
         where input is not null
         let mode = ReadActivationMode(rebind)
         select new GameBinding(mapId, actionId, input)
         {
             Activation = mode switch
             {
                 "tap" => BindingActivation.Tap,
                 "double_tap" => BindingActivation.DoubleTap,
                 "delayed_press" => BindingActivation.Hold,
                 _ => BindingActivation.Default
             },
             NativeActivation = mode
         }).ToArray();

    public bool RegisterDevice(int instance, string product)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(instance);
        ArgumentException.ThrowIfNullOrWhiteSpace(product);
        var options = JoystickOptions().FirstOrDefault(option => (int)option.Attribute("instance")! == instance);
        if (options?.Attribute("Product")?.Value is { Length: > 0 } registered)
        {
            if (string.Equals(registered, product, StringComparison.OrdinalIgnoreCase))
                return false;
            throw new InvalidOperationException($"Star Citizen profile joystick slot {instance} is assigned to {registered}.");
        }

        if (options is null)
        {
            options = new XElement("options", new XAttribute("type", "joystick"),
                new XAttribute("instance", instance));
            AddChild(root, options, root.Elements("options").LastOrDefault()?.ElementsAfterSelf().FirstOrDefault()
                ?? root.Elements("actionmap").FirstOrDefault());
        }
        options.SetAttributeValue("Product", product);

        var header = root.Element("CustomisationUIHeader");
        var devices = header?.Element("devices");
        if (devices is null && header is not null)
        {
            devices = new XElement("devices");
            AddChild(header, devices, header.Elements().FirstOrDefault());
        }
        if (devices is not null && !devices.Elements("joystick").Any(device => (int?)device.Attribute("instance") == instance))
            AddChild(devices, new XElement("joystick", new XAttribute("instance", instance)));
        hasChanges = true;
        return true;
    }

    public bool Apply(string map, string action, string input) =>
        ApplyCore(map, action, input, mode: null, exactMode: false);

    internal bool ApplyActivation(string map, string action, string input, BindingActivation activation) =>
        ApplyCore(map, action, input, activation switch
        {
            BindingActivation.Default => null,
            BindingActivation.Tap => "tap",
            BindingActivation.DoubleTap => "double_tap",
            BindingActivation.Hold => "delayed_press",
            _ => throw new ArgumentOutOfRangeException(nameof(activation))
        }, exactMode: true);

    private bool ApplyCore(string map, string action, string input, string? mode, bool exactMode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(map);
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        if (input is null || !StarCitizenGame.IsJoystickInput(input))
            throw new ArgumentException("Expected a Star Citizen joystick button, hat direction, or axis.", nameof(input));
        int separator = input.IndexOf('_') + 1;
        if (mode is not null && StarCitizenGame.IsJoystickAxis(input[separator..]))
            throw new InvalidOperationException("Tap, double tap, and hold are available on buttons and hat directions.");

        XElement? mapElement = FindMap(map);
        if (mapElement is null)
        {
            mapElement = new XElement("actionmap", new XAttribute("name", map));
            AddChild(root, mapElement);
        }
        var actionElement = FindAction(mapElement, action);
        if (actionElement is null)
        {
            actionElement = new XElement("action", new XAttribute("name", action));
            AddChild(mapElement, actionElement);
        }

        var rebinds = actionElement.Elements("rebind").ToArray();
        if (rebinds.Any(rebind => InputEquals(rebind, input) && MatchesMode(rebind)))
            return false;

        string prefix = input[..separator];
        var slots = rebinds.Where(rebind => UsesSlot(rebind, prefix)).ToArray();
        var unbound = slots.FirstOrDefault(rebind => InputEquals(rebind, prefix) && MatchesMode(rebind));
        if (unbound is null && slots is [var onlySlot] && InputEquals(onlySlot, prefix))
            unbound = onlySlot;
        if (unbound is not null)
        {
            unbound.SetAttributeValue("input", input);
            if (exactMode)
                unbound.SetAttributeValue("activationMode", mode);
        }
        else
        {
            var rebind = new XElement("rebind", new XAttribute("input", input));
            rebind.SetAttributeValue("activationMode", mode);
            AddChild(actionElement, rebind);
        }
        hasChanges = true;
        return true;

        bool MatchesMode(XElement rebind) => !exactMode || ReadActivationMode(rebind) == mode;
    }

    internal bool RemoveActivationForDevice(string map, string action, string input, Guid? product, string? nativeActivation)
    {
        VerifyRemovalDevice(input, product);
        return RemoveCore(map, action, input, nativeActivation, exactMode: true);
    }

    private void VerifyRemovalDevice(string input, Guid? product)
    {
        var devices = Devices;
        var matches = devices.Where(device => product.HasValue && device.ProductGuid == product).Take(2).ToArray();
        if (matches is not [var device] ||
            devices.Count(candidate => candidate.Instance == device.Instance) != 1 ||
            !input.StartsWith($"js{device.Instance}_", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The selected Star Citizen profile uses different joystick slots. Undo the staged removal and unassign it again in this profile.");
    }

    public bool Remove(string map, string action, string input) =>
        RemoveCore(map, action, input, mode: null, exactMode: false);

    private bool RemoveCore(string map, string action, string input, string? mode, bool exactMode)
    {
        if (input is null || !StarCitizenGame.IsJoystickInput(input))
            return false;
        var rebinds = FindAction(FindMap(map), action)?.Elements("rebind").ToArray();
        if (rebinds is null)
            return false;
        var selected = rebinds.Where(rebind => InputEquals(rebind, input) &&
            (!exactMode || ReadActivationMode(rebind) == mode)).ToArray();
        if (selected.Length == 0)
            return false;

        string prefix = input[..(input.IndexOf('_') + 1)];
        if (!rebinds.Except(selected).Any(rebind => UsesSlot(rebind, prefix)))
        {
            // An empty binding prevents the original game default from reappearing.
            selected[0].SetAttributeValue("input", prefix + " ");
            selected = selected[1..];
        }
        foreach (var rebind in selected)
        {
            if (rebind.PreviousNode is XText text && string.IsNullOrWhiteSpace(text.Value))
                text.Remove();
            rebind.Remove();
        }
        hasChanges = true;
        return true;
    }

    public void SaveAs(string path)
    {
        var write = ToWrite(path)!;
        WriteNew(write.Path, write.Content);
    }

    internal NativeBindingWrite? ToWrite(string? copyPath = null)
    {
        if (copyPath is null)
            return hasChanges ? new NativeBindingWrite(SourcePath, sourceBytes, Serialize(document)) : null;

        string path = Path.GetFullPath(copyPath);
        string file = Path.GetFileNameWithoutExtension(path);
        if (!Path.GetExtension(path).Equals(".xml", StringComparison.OrdinalIgnoreCase) ||
            !file.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) ||
            !file.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase) ||
            file.Length <= FilePrefix.Length + FileSuffix.Length)
            throw new ArgumentException("Use the game’s export filename format: layout_Name_exported.xml.", nameof(path));

        string profileName = file[FilePrefix.Length..^FileSuffix.Length];
        var copy = new XDocument(document);
        copy.Root!.SetAttributeValue("profileName", profileName);
        copy.Root.Element("CustomisationUIHeader")?.SetAttributeValue("label", profileName);
        return new NativeBindingWrite(path, null, Serialize(copy));
    }

    public void SaveChanges()
    {
        if (ToWrite() is not { } write)
            return;
        NativeBindingWrites.Commit([write], createBackups: false);
        sourceBytes = write.Content;
        hasChanges = false;
    }

    private static byte[] Serialize(XDocument contents)
    {
        using var bytes = new MemoryStream();
        using (var writer = XmlWriter.Create(bytes, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            OmitXmlDeclaration = contents.Declaration is null,
            NewLineHandling = NewLineHandling.None
        }))
            contents.Save(writer);
        return bytes.ToArray();
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private XElement? FindMap(string map) => root.Elements("actionmap")
        .FirstOrDefault(element => element.Attribute("name")?.Value == map);

    private static XElement? FindAction(XElement? map, string action) => map?.Elements("action")
        .FirstOrDefault(element => element.Attribute("name")?.Value == action);

    private IEnumerable<XElement> JoystickOptions() => root.Elements("options")
        .Where(option => option.Attribute("type")?.Value == "joystick" &&
            int.TryParse(option.Attribute("instance")?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int instance) &&
            instance > 0);

    private static bool InputEquals(XElement element, string input) =>
        string.Equals(element.Attribute("input")?.Value.Trim(), input, StringComparison.OrdinalIgnoreCase);

    private static bool UsesSlot(XElement element, string prefix) =>
        element.Attribute("input")?.Value.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true;

    private static string? ReadActivationMode(XElement element) => element.Attribute("activationMode")?.Value;

    private static void AddChild(XElement parent, XElement child, XElement? before = null)
    {
        int depth = parent.Ancestors().Count();
        var whitespace = new XText("\n" + new string(' ', depth + 1));
        if (before is not null)
        {
            before.AddBeforeSelf(child, whitespace);
            return;
        }
        if (parent.LastNode is XText tail && string.IsNullOrWhiteSpace(tail.Value))
            tail.AddBeforeSelf(whitespace, child);
        else
            parent.Add(whitespace, child, new XText("\n" + new string(' ', depth)));
    }
}
