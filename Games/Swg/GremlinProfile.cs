using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace VisualControlsBinder.Games;

public sealed record GremlinDevice(Guid Id, string Name);
public sealed record GremlinOutput(int DeviceId, string Kind, int InputId)
{
    private static readonly string[] AxisNames = ["X", "Y", "Z", "Rx", "Ry", "Rz", "Slider 1", "Slider 2"];

    public string Label => Kind switch
    {
        "axis" when InputId >= 1 && InputId <= AxisNames.Length => $"Axis {AxisNames[InputId - 1]}",
        "button" => $"Button {InputId}",
        "hat" => $"POV {InputId}",
        _ => $"{Kind} {InputId}"
    };

    public override string ToString() => $"vJoy {DeviceId} · {Label}";
}

public sealed record SwgKey(int ScanCode, bool IsExtended = false)
{
    public override string ToString() => $"Key scan {(IsExtended ? "E0 " : "")}{ScanCode:X2}";
}

public sealed record GremlinInputMapping(Guid DeviceId, string Kind, int InputId, string Mode, IReadOnlyList<SwgTarget> Targets);
public sealed record GremlinRouteRemoval(Guid DeviceId, string Kind, int InputId, string Mode, GremlinOutput Output);

public sealed record GremlinAssignment(
    Guid DeviceId, string Kind, int InputId, GameAction? Action,
    GremlinOutput? Output = null, SwgKey? Keyboard = null, string Mode = "Default", string? SourceLabel = null)
{
    internal string Label => Action?.Name ?? SourceLabel ?? $"{Kind} {InputId}";
}

public sealed partial class GremlinProfile
{
    private XDocument template;
    private IReadOnlyList<GremlinInputMapping> mappings;
    private HashSet<GremlinOutput> routedOutputs;
    private readonly byte[]? sourceBytes;
    private readonly XDocument? originalDocument;
    internal string? SourcePath { get; }

    private GremlinProfile(XDocument template, Func<IReadOnlyList<GremlinOutput>?> readOutputs,
        string? sourcePath = null, byte[]? bytes = null)
    {
        this.template = template;
        SourcePath = sourcePath;
        sourceBytes = bytes;
        originalDocument = bytes is null ? null : new XDocument(template);
        XElement profile = template.Root!;
        Devices = profile.Element("devices")?.Elements("device")
            .Where(device => Guid.TryParse(Value(device, "device-id"), out _))
            .Select(device => new GremlinDevice(Guid.Parse(Value(device, "device-id")), Value(device, "device-name").Trim()))
            .ToArray() ?? [];
        Modes = ReadModes(profile);
        var library = ActionLibrary(profile);
        ActiveShifts = FindOwnedShifts(profile, library).Distinct().ToArray();
        mappings = ReadMappings(profile, library);
        routedOutputs = ReadRoutedOutputs(profile, library);
        ReferencedOutputs = profile.Element("library")!.Elements("action")
            .Select(ReadOutput).OfType<GremlinOutput>().Distinct().ToArray();
        IReadOnlyList<GremlinOutput>? liveOutputs = readOutputs();
        HasLiveOutputCapabilities = liveOutputs is not null;
        Outputs = (liveOutputs ?? ReferencedOutputs)
            .OrderBy(output => output.DeviceId).ThenBy(output => output.Kind).ThenBy(output => output.InputId)
            .ToArray();
    }

    public IReadOnlyList<GremlinDevice> Devices { get; private set; }
    public IReadOnlyList<GremlinOutput> Outputs { get; }
    internal IReadOnlyList<GremlinOutput> ReferencedOutputs { get; }
    public IReadOnlyList<string> Modes { get; private set; }
    public bool HasLiveOutputCapabilities { get; }

    public static GremlinProfile CreateBlank(string destination, IReadOnlyList<(Guid Id, string Name)> devices)
    {
        if (!Path.GetExtension(destination).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Use the .xml extension for a Joystick Gremlin profile.", nameof(destination));

        var document = new XDocument(new XElement("profile", new XAttribute("version", 14),
            new XElement("inputs"),
            new XElement("settings",
                new XElement("startup-mode", "Use Heuristic"),
                new XElement("macro-default-delay", 0.05)),
            new XElement("logical-device"),
            new XElement("library"),
            new XElement("modes", new XElement("mode", "Default")),
            new XElement("scripts"),
            new XElement("devices")));
        // Gremlin discovers available vJoy output devices from the driver. Its vJoy settings store overrides only.
        var profile = new GremlinProfile(document, VJoyOutputs.Read);
        foreach (var (id, name) in devices)
            profile.RegisterDevice(id, name);
        SaveNew(document, destination);
        return profile;
    }

    public static GremlinProfile Load(string path) => Load(path, VJoyOutputs.Read);

    internal static GremlinProfile Load(string path, Func<IReadOnlyList<GremlinOutput>?> readOutputs)
    {
        path = Path.GetFullPath(path);
        byte[] bytes = File.ReadAllBytes(path);
        using var stream = new MemoryStream(bytes, writable: false);
        XDocument document = XDocument.Load(stream);
        if (document.Root is not { } profile || profile.Name != "profile"
            || (string?)profile.Attribute("version") != "14" || profile.Element("inputs") is null
            || profile.Element("library") is null)
        {
            throw new InvalidDataException("Choose a Joystick Gremlin schema 14 XML profile, such as one saved by Gremlin 16.2.");
        }

        return new GremlinProfile(document, readOutputs, path, bytes);
    }

    public GremlinDevice? FindDevice(string name)
    {
        GremlinDevice[] matches = Devices
            .Where(device => string.Equals(device.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public void RegisterDevice(Guid id, string name)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A Gremlin source device needs its DirectInput device ID and name.");
        }
        if (Devices.Any(device => device.Id == id))
        {
            return;
        }

        XElement profile = template.Root!;
        XElement? devices = profile.Element("devices");
        if (devices is null)
        {
            devices = new XElement("devices");
            profile.Add(devices);
        }
        devices.Add(new XElement("device", new XElement("device-id", id), new XElement("device-name", name.Trim())));
        Devices = [..Devices, new GremlinDevice(id, name.Trim())];
    }

    public GremlinOutput? GetOutput(Guid deviceId, string kind, int inputId, string mode = "Default")
    {
        return SingleOutput(GetOutputs(deviceId, kind, inputId, mode).Select(target => target.Output));
    }

    private static GremlinOutput? SingleOutput(IEnumerable<GremlinOutput?> candidates) =>
        candidates.OfType<GremlinOutput>().Distinct().Take(2).ToArray() is [var only] ? only : null;

    public IReadOnlyList<SwgTarget> GetOutputs(Guid deviceId, string kind, int inputId, string mode = "Default") =>
        mappings.Where(mapping => mapping.DeviceId == deviceId && mapping.Kind == kind
                && mapping.InputId == inputId && mapping.Mode == mode)
            .SelectMany(mapping => mapping.Targets).Distinct().ToArray();

    public IReadOnlyList<GremlinInputMapping> GetBoundInputs(Guid deviceId, string? mode = null) =>
        mappings.Where(mapping => mapping.Targets.Count > 0 && mapping.DeviceId == deviceId && (mode is null || mapping.Mode == mode)).ToArray();

    internal IEnumerable<GremlinInputMapping> ReadInputOverrides(Guid deviceId) => mappings.Where(mapping => mapping.DeviceId == deviceId);

    internal IReadOnlyList<SwgTarget> GetEffectiveOutputs(Guid deviceId, string kind, int inputId, string mode)
    {
        var inputs = mappings.Where(mapping => mapping.DeviceId == deviceId && mapping.Kind == kind && mapping.InputId == inputId)
            .ToLookup(mapping => mapping.Mode);
        string? sourceMode = ModeChain(mode).FirstOrDefault(inputs.Contains);
        return sourceMode is null ? [] : inputs[sourceMode].SelectMany(mapping => mapping.Targets).Distinct().ToArray();
    }

    public bool ContainsRoute(GremlinRouteRemoval removal)
    {
        var profile = template.Root!;
        var input = FindInput(profile, removal.DeviceId, removal.Kind, removal.InputId, removal.Mode);
        if (input is null)
            return false;
        var library = ActionLibrary(profile);
        return RouteRoots(input, library).Any(item => MatchingRouteReferences(item.Root, library, removal.Output).Any());
    }

    public GremlinOutput? FindFreeOutput(string kind, IEnumerable<GremlinOutput>? reserved = null,
        int vJoyDeviceId = 1, string mode = "Default", int maxButton = 128)
    {
        if (!Modes.Contains(mode))
        {
            return null;
        }

        return FirstFreeOutput(kind, reserved?.ToHashSet(), maxButton, vJoyDeviceId);
    }

    private GremlinOutput? FirstFreeOutput(string kind, HashSet<GremlinOutput>? reserved, int maxButton,
        int? vJoyDeviceId = null) =>
        Outputs.FirstOrDefault(output => (vJoyDeviceId is null || output.DeviceId == vJoyDeviceId)
            && output.Kind == kind && (kind != "button" || output.InputId <= maxButton)
            && !routedOutputs.Contains(output) && reserved?.Contains(output) != true);

    internal SwgTarget ChooseTarget((Guid DeviceId, string Kind, int InputId, string Mode) source,
        GremlinOutput? explicitOutput, SwgTarget gameTarget, IEnumerable<GremlinOutput> reserved, int maxButton)
    {
        if (explicitOutput is not null)
            return new SwgTarget(explicitOutput, null);
        if (gameTarget.Output is { } saved && saved.Kind == source.Kind && Outputs.Contains(saved))
            return new SwgTarget(saved, null);
        if (source.Kind == "button" && gameTarget.Keyboard is { } key)
            return new SwgTarget(null, key);

        var blocked = reserved.ToHashSet();
        var current = GetOutput(source.DeviceId, source.Kind, source.InputId, source.Mode);
        if (current is not null && Outputs.Contains(current) && !blocked.Contains(current) &&
            (current.Kind != "button" || current.InputId <= maxButton))
            return new SwgTarget(current, null);

        var free = Modes.Contains(source.Mode) ? FirstFreeOutput(source.Kind, blocked, maxButton) : null;
        return new SwgTarget(free, null);
    }

    /// <summary>Writes a new schema 14 profile and returns any game setup instructions and routing conflicts.</summary>
    public IReadOnlyList<string> Export(string destination, IEnumerable<GremlinAssignment> assignments, int maxButton = 128,
        IEnumerable<GremlinRouteRemoval>? removals = null)
    {
        var (document, notes) = PrepareDocument(assignments, maxButton, removals);
        SaveNew(document, destination);
        return notes;
    }

    internal (NativeBindingWrite? Write, IReadOnlyList<string> Notes) PrepareUpdate(IEnumerable<GremlinAssignment> assignments,
        int maxButton, IEnumerable<GremlinRouteRemoval> removals)
    {
        if (SourcePath is null || sourceBytes is null)
            throw new InvalidOperationException("Load the selected Gremlin XML before updating it.");
        var (document, notes) = PrepareDocument(assignments, maxButton, removals, allowNoChanges: true);
        if (XNode.DeepEquals(originalDocument, document))
            return (null, notes);
        using var contents = new MemoryStream();
        document.Save(contents);
        return (new NativeBindingWrite(SourcePath, sourceBytes, contents.ToArray()), notes);
    }

    internal void EnsureSourceUnchanged()
    {
        if (SourcePath is null || sourceBytes is null || !File.Exists(SourcePath) ||
            !File.ReadAllBytes(SourcePath).AsSpan().SequenceEqual(sourceBytes))
            throw new IOException("The selected Gremlin profile changed. Reload it and review the routes before exporting.");
    }

    private (XDocument Document, IReadOnlyList<string> Notes) PrepareDocument(IEnumerable<GremlinAssignment> assignments,
        int maxButton, IEnumerable<GremlinRouteRemoval>? removals, bool allowNoChanges = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxButton, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxButton, 128);
        GremlinAssignment[] edits = assignments.ToArray();
        var disconnected = removals?.Distinct().ToArray() ?? [];
        if (!allowNoChanges && edits.Length == 0 && disconnected.Length == 0 && !shiftChanged)
        {
            throw new InvalidOperationException("Select at least one route change or assignment to export.");
        }

        if (edits.GroupBy(item => (item.DeviceId, item.Kind, item.InputId, item.Mode)).Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("More than one planned action uses the same physical input in a Gremlin mode.");
        }

        var document = new XDocument(template);
        XElement profile = document.Root!;
        var library = ActionLibrary(profile);
        var notes = new List<string>();
        var destinations = new List<(GremlinAssignment Assignment, GremlinOutput Output)>();
        foreach (var removal in disconnected)
        {
            if (removal.DeviceId == Guid.Empty || removal.Kind is not ("button" or "axis") || removal.InputId <= 0
                || !Modes.Contains(removal.Mode) || removal.Output.Kind != removal.Kind)
                throw new InvalidOperationException("A vJoy route removal needs an exact physical input, Gremlin mode and output.");

            var input = FindInput(profile, removal.DeviceId, removal.Kind, removal.InputId, removal.Mode);
            if (input is null)
                continue;
            foreach (var (reference, root) in RouteRoots(input, library))
            {
                var copy = new XElement(root);
                var removed = MatchingRouteReferences(copy, library, removal.Output).ToArray();
                if (removed.Length == 0)
                    continue;
                foreach (var route in removed)
                    route.Remove();
                // A root or route action can also be referenced by another input or mode.
                reference.Value = AddAction(profile, library, copy).ToString();
            }
        }
        foreach (GremlinAssignment edit in edits)
        {
            ValidateSource(edit);
            XElement? input = FindInput(profile, edit.DeviceId, edit.Kind, edit.InputId, edit.Mode);
            var currentOutput = input is null ? null : SingleOutput(OutputActions(input, library).Select(ReadOutput));
            GremlinOutput? output = edit.Output;
            if (output is null && edit.Keyboard is null)
            {
                if (edit.Action is null)
                    throw new InvalidOperationException($"{edit.Label} needs an explicit vJoy destination.");
                output = currentOutput;
                if (output is null || !Outputs.Contains(output) || (output.Kind == "button" && output.InputId > maxButton))
                {
                    throw new InvalidOperationException(
                        $"{edit.Label} needs an explicit vJoy destination or a saved game key. This input has no usable vJoy route in the template.");
                }

                notes.Add($"{edit.Label}: assign {output} in the game's key mappings if it is not already assigned.");
                destinations.Add((edit, output));
                continue;
            }

            if (output is not null)
            {
                if (edit.Keyboard is not null || output.Kind != edit.Kind || !Outputs.Contains(output))
                {
                    throw new InvalidOperationException($"{edit.Label}: choose an available vJoy output of the matching input type.");
                }
                if (output.Kind == "button" && output.InputId > maxButton)
                {
                    throw new InvalidOperationException($"{edit.Label}: choose vJoy button 1–{maxButton} or a saved game key.");
                }

                if (!allowNoChanges || currentOutput != output)
                    destinations.Add((edit, output));
                if (edit.Action is not null)
                    notes.Add($"{edit.Label}: verify {output} in the game's key mappings.");
                if (currentOutput == output)
                {
                    continue;
                }
            }
            else if (edit.Kind != "button" || edit.Keyboard!.ScanCode is < 1 or > 127)
            {
                throw new InvalidOperationException($"{edit.Label}: keyboard output needs a button input and a valid key scan code.");
            }

            SetRoute(profile, library, input, edit, output);
        }

        foreach (var (assignment, output) in destinations)
        {
            int otherSources = profile.Element("inputs")!.Elements("input").Count(input =>
                Value(input, "mode") == assignment.Mode
                && !MatchesSource(input, assignment.DeviceId, assignment.Kind, assignment.InputId, assignment.Mode)
                && OutputActions(input, library).Any(action => ReadOutput(action) == output));
            if (otherSources > 0)
            {
                notes.Add($"{assignment.Label}: {output} is also driven by {otherSources} other input(s) in {assignment.Mode}.");
            }
        }

        return (document, notes);
    }

    private static void SaveNew(XDocument document, string destination)
    {
        string target = Path.GetFullPath(destination);
        if (File.Exists(target) || Directory.Exists(target))
            throw new IOException("Choose a new filename for the Joystick Gremlin profile.");

        string staged = Path.Combine(Path.GetDirectoryName(target)!, $".visual-controls-{Guid.NewGuid():N}.tmp");
        using (var file = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            document.Save(file);
            file.Flush(flushToDisk: true);
        }
        File.Move(staged, target);
    }

    private void ValidateSource(GremlinAssignment assignment)
    {
        if (assignment.Kind is not ("button" or "axis") || assignment.InputId <= 0
            || !Devices.Any(device => device.Id == assignment.DeviceId) || !Modes.Contains(assignment.Mode))
        {
            throw new InvalidOperationException($"{assignment.Label}: choose a button or axis on a device and mode in the Gremlin template.");
        }

        if (assignment.Action is { } action && action.IsAxis != (assignment.Kind == "axis"))
        {
            throw new InvalidOperationException($"{assignment.Label}: the selected input does not match the action type.");
        }
    }

    private static void SetRoute(
        XElement profile, Dictionary<Guid, XElement> library, XElement? input, GremlinAssignment edit, GremlinOutput? output)
    {
        XElement root;

        if (input is null)
        {
            root = CreateRootInput(profile, library, edit.DeviceId, edit.Kind, edit.InputId, edit.Mode);
        }
        else
        {
            XElement[] configurations = input.Elements("action-configuration").Take(2).ToArray();
            if (configurations.Length != 1 || !Guid.TryParse(Value(configurations[0], "root-action"), out Guid rootId)
                || Value(configurations[0], "behavior") != edit.Kind
                || !library.TryGetValue(rootId, out XElement? existingRoot) || (string?)existingRoot.Attribute("type") != "root")
            {
                throw new InvalidOperationException($"{edit.Label}: the input has a complex Gremlin configuration. Keep its existing route or choose another input.");
            }

            root = new XElement(existingRoot);
            configurations[0].Element("root-action")!.Value = AddAction(profile, library, root).ToString();
        }

        XElement references = root.Element("actions") ?? throw new InvalidDataException("A Gremlin action has no child action list.");
        XElement[] actionReferences = references.Elements("action-id").ToArray();
        if (actionReferences.Any(reference => Resolve(reference, library) is not { } child
            || (!IsOutput(child) && (string?)child.Attribute("type") != "response-curve")))
        {
            throw new InvalidOperationException($"{edit.Label}: the input uses custom Gremlin actions. Keep its existing route or choose another input.");
        }
        XElement[] existingOutputs = actionReferences.Where(reference => IsOutput(Resolve(reference, library)!)).Take(2).ToArray();
        if (existingOutputs.Length > 1)
        {
            throw new InvalidOperationException($"{edit.Label}: the input has multiple output routes. Keep its existing route or choose another input.");
        }

        XElement? existingReference = existingOutputs.SingleOrDefault();
        XElement? previous = existingReference is null ? null : Resolve(existingReference, library);
        XElement action;
        if (output is null)
        {
            action = CreateKeyboardAction(edit.Keyboard!);
        }
        else if (previous is not null && ReadOutput(previous) is { } oldOutput && oldOutput.Kind == output.Kind)
        {
            // Preserve inversion and axis settings from this physical input's current route.
            action = new XElement(previous);
            SetNumber(action, "vjoy-device-id", output.DeviceId);
            SetNumber(action, "vjoy-input-id", output.InputId);
        }
        else
        {
            action = CreateVJoyAction(output);
        }

        Guid actionId = AddAction(profile, library, action);
        if (existingReference is null)
        {
            references.Add(new XElement("action-id", actionId));
        }
        else
        {
            existingReference.Value = actionId.ToString();
        }
    }

    private static XElement CreateRootInput(XElement profile, Dictionary<Guid, XElement> library,
        Guid deviceId, string kind, int inputId, string mode)
    {
        var root = new XElement("action", new XAttribute("type", "root"),
            new XElement("actions"), Property("string", "action-label", "Root"),
            Property("activation-mode", "activation-mode", "disallowed"));
        Guid rootId = AddAction(profile, library, root);
        profile.Element("inputs")!.Add(new XElement("input",
            new XElement("device-id", deviceId), new XElement("input-type", kind),
            new XElement("mode", mode), new XElement("input-id", inputId),
            new XElement("action-configuration",
                new XElement("root-action", rootId), new XElement("behavior", kind))));
        return root;
    }

    private static Guid AddAction(XElement profile, Dictionary<Guid, XElement> library, XElement action)
    {
        Guid id = Guid.NewGuid();
        action.SetAttributeValue("id", id);
        profile.Element("library")!.Add(action);
        library.Add(id, action);
        return id;
    }

    private static XElement CreateVJoyAction(GremlinOutput output)
    {
        var action = new XElement("action", new XAttribute("type", "map-to-vjoy"),
            Property("int", "vjoy-device-id", output.DeviceId), Property("int", "vjoy-input-id", output.InputId),
            Property("input_type", "vjoy-input-type", output.Kind));
        if (output.Kind == "axis")
        {
            action.Add(Property("axis_mode", "axis-mode", "absolute"), Property("float", "axis-scaling", "1.0"));
        }
        else
        {
            action.Add(Property("bool", "button-inverted", "False"));
        }
        action.Add(Property("string", "action-label", "Map to vJoy"), Property("activation-mode", "activation-mode", "both"));
        return action;
    }

    private static XElement CreateKeyboardAction(SwgKey key) => new("action", new XAttribute("type", "map-to-keyboard"),
        new XElement("input", Property("int", "scan-code", key.ScanCode),
            Property("bool", "is-extended", key.IsExtended ? "True" : "False")),
        Property("string", "action-label", "Map to Keyboard"), Property("activation-mode", "activation-mode", "both"));

    private static XElement Property(string type, string name, object value) =>
        new("property", new XAttribute("type", type), new XElement("name", name), new XElement("value", value));

    private static string Value(XElement node, string name) => node.Element(name)?.Value ?? "";

    private static string? PropertyValue(XElement node, string name) =>
        node.Elements("property").FirstOrDefault(property => Value(property, "name") == name)?.Element("value")?.Value;

    private static void SetNumber(XElement action, string name, int number) =>
        action.Elements("property").First(property => Value(property, "name") == name).Element("value")!.Value =
            number.ToString(CultureInfo.InvariantCulture);

    private static GremlinOutput? ReadOutput(XElement action)
    {
        if ((string?)action.Attribute("type") == "map-to-vjoy"
            && int.TryParse(PropertyValue(action, "vjoy-device-id"), out int device)
            && int.TryParse(PropertyValue(action, "vjoy-input-id"), out int input)
            && PropertyValue(action, "vjoy-input-type") is { } kind)
        {
            return new GremlinOutput(device, kind, input);
        }
        return null;
    }

    private static bool IsOutput(XElement action) => (string?)action.Attribute("type") is "map-to-vjoy" or "map-to-keyboard";

    private static GremlinInputMapping? ReadMapping(XElement input, Dictionary<Guid, XElement> library)
    {
        if (!input.Elements("action-configuration").Any() || !Guid.TryParse(Value(input, "device-id"), out Guid deviceId)
            || !int.TryParse(Value(input, "input-id"), out int inputId))
        {
            return null;
        }

        SwgTarget[] targets = OutputActions(input, library).Select(ReadTarget).OfType<SwgTarget>().Distinct().ToArray();
        return new GremlinInputMapping(deviceId, Value(input, "input-type"), inputId, Value(input, "mode"), targets);
    }

    private static GremlinInputMapping[] ReadMappings(XElement profile, Dictionary<Guid, XElement> library) =>
        profile.Element("inputs")!.Elements("input").Select(input => ReadMapping(input, library))
            .OfType<GremlinInputMapping>().ToArray();

    private static SwgTarget? ReadTarget(XElement action)
    {
        if (ReadOutput(action) is { } output)
        {
            return new SwgTarget(output, null);
        }
        if ((string?)action.Attribute("type") != "map-to-keyboard" || action.Elements("input").Take(2).ToArray() is not [var key]
            || !int.TryParse(PropertyValue(key, "scan-code"), out int scanCode) || scanCode is < 1 or > 127
            || !bool.TryParse(PropertyValue(key, "is-extended"), out bool extended))
        {
            return null;
        }

        return new SwgTarget(null, new SwgKey(scanCode, extended));
    }

    private static Dictionary<Guid, XElement> ActionLibrary(XElement profile) => profile.Element("library")!.Elements("action")
        .Where(action => Guid.TryParse((string?)action.Attribute("id"), out _))
        .ToDictionary(action => Guid.Parse((string)action.Attribute("id")!));

    private static XElement? Resolve(XElement reference, Dictionary<Guid, XElement> library) =>
        Guid.TryParse(reference.Value, out Guid id) && library.TryGetValue(id, out XElement? action) ? action : null;

    private static HashSet<GremlinOutput> ReadRoutedOutputs(XElement profile, Dictionary<Guid, XElement> library)
    {
        var outputs = new HashSet<GremlinOutput>();
        var matches = new Stack<XElement>(profile.Element("inputs")!.Descendants("root-action"));
        var visited = new HashSet<Guid>();
        while (matches.TryPop(out var reference))
        {
            if (!Guid.TryParse(reference.Value, out Guid id) || !visited.Add(id) || !library.TryGetValue(id, out var action))
                continue;
            if (ReadOutput(action) is { } output)
                outputs.Add(output);
            foreach (var child in action.Descendants("action-id"))
                matches.Push(child);
        }
        return outputs;
    }

    private static IEnumerable<(XElement Reference, XElement Root)> RouteRoots(XElement input, Dictionary<Guid, XElement> library)
    {
        foreach (var configuration in input.Elements("action-configuration"))
            if (configuration.Element("root-action") is { } reference && Resolve(reference, library) is { } root
                && (string?)root.Attribute("type") == "root")
                yield return (reference, root);
    }

    private static IEnumerable<XElement> MatchingRouteReferences(XElement root, Dictionary<Guid, XElement> library, GremlinOutput output) =>
        (root.Element("actions")?.Elements("action-id") ?? [])
        .Where(reference => Resolve(reference, library) is { } action && ReadOutput(action) == output);

    private static IEnumerable<XElement> OutputActions(XElement input, Dictionary<Guid, XElement> library)
    {
        foreach (XElement configuration in input.Elements("action-configuration"))
        {
            if (configuration.Element("root-action") is { } reference && Resolve(reference, library) is { } root)
            {
                foreach (XElement child in root.Element("actions")?.Elements("action-id") ?? [])
                {
                    if (Resolve(child, library) is { } action && IsOutput(action))
                    {
                        yield return action;
                    }
                }
            }
        }
    }

    private static XElement? FindInput(XElement profile, Guid device, string kind, int inputId, string mode) =>
        profile.Element("inputs")!.Elements("input").SingleOrDefault(input => MatchesSource(input, device, kind, inputId, mode));

    private static bool MatchesSource(XElement input, Guid device, string kind, int inputId, string mode) =>
        Guid.TryParse(Value(input, "device-id"), out Guid actualDevice) && actualDevice == device
        && Value(input, "input-type") == kind && Value(input, "mode") == mode
        && int.TryParse(Value(input, "input-id"), out int actualInputId) && actualInputId == inputId;
}
