using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using VisualControlsBinder.Inputs;

namespace VisualControlsBinder.Games;

internal sealed class MsfsBindingProfile
{
    private const int MaxSize = 16 * 1024 * 1024;
    private const int AnalogOrAxis = 5;
    private const int Digital = 2;
    private const int Axis = 4;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly byte[] Bom = Encoding.UTF8.GetPreamble();
    private static readonly int[] AxisCodes = [1026, 1042, 1058, 770, 786, 802, 514, 530];
    private static readonly string[] AxisNames =
    [
        "Joystick L-Axis X", "Joystick L-Axis Y", "Joystick L-Axis Z", "Joystick R-Axis X",
        "Joystick R-Axis Y", "Joystick R-Axis Z", "Joystick Slider X", "Joystick Slider Y"
    ];
    private static readonly string[] HatDirections = ["up", "right", "down", "left"];
    private static readonly string[] HatNames = ["Joystick Pov Up", "Joystick Pov Right", "Joystick Pov Down", "JoystickPov Left"];
    private static readonly string[] Slots = ["Primary", "Secondary"];

    private readonly byte[] _original;
    private readonly bool _hasBom;
    private readonly bool _usesCrlf;
    private readonly string _declaration;
    private readonly XElement _fragment;
    private readonly XElement _device;
    private readonly ushort _productId;
    private bool _changed;

    private MsfsBindingProfile(string path, byte[] original)
    {
        Path = path;
        _original = original;
        _hasBom = original.AsSpan().StartsWith(Bom);
        string text = Utf8.GetString(original.AsSpan(_hasBom ? Bom.Length : 0));
        _usesCrlf = text.Contains("\r\n", StringComparison.Ordinal);
        var declaration = Regex.Match(text, @"\A<\?xml\s[^?]*\?>", RegexOptions.CultureInvariant);
        _declaration = declaration.Value;
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxSize + 80
        };
        // Retail exports use adjacent top-level elements, while SDK profiles wrap the same entries.
        using var reader = XmlReader.Create(new StringReader($"<VcpProfile>{text[_declaration.Length..]}</VcpProfile>"), settings);
        _fragment = XElement.Load(reader, LoadOptions.PreserveWhitespace);
        var devices = Children(_fragment, "Device").Concat(_fragment.Elements()
            .Where(node => node.Name.LocalName is "DefaultInput" or "DefaulftInput").SelectMany(node => Children(node, "Device")))
            .Take(2).ToArray();
        if (devices.Length != 1 || string.IsNullOrWhiteSpace((string?)devices[0].Attribute("DeviceName")) ||
            !ReadProductId((string?)devices[0].Attribute("ProductID"), out _productId))
            throw new InvalidDataException("Choose an MSFS control profile for one named device with its product ID.");
        _device = devices[0];
    }

    public string Path { get; }
    public bool IsCloudSave => IsCloudPath(Path);

    public static MsfsBindingProfile Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxSize)
            throw new InvalidDataException("The Microsoft Flight Simulator control profile exceeds 16 MB.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        try { return new MsfsBindingProfile(System.IO.Path.GetFullPath(path), bytes); }
        catch (Exception ex) when (ex is XmlException or DecoderFallbackException)
        {
            throw new InvalidDataException("Choose a valid Microsoft Flight Simulator UTF-8 control profile.", ex);
        }
    }

    public bool Matches(JoystickDevice device)
    {
        ushort productId = (ushort)(BitConverter.ToUInt32(device.Product.ToByteArray()) >> 16);
        return productId == _productId && string.Equals(device.Name.Trim(),
            ((string?)_device.Attribute("DeviceName"))?.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<GameBinding> Bindings => Actions().Where(item => SupportsPlainRow(item.Action)).SelectMany(item =>
        Inputs(item.Action).Select(slot => ReadInput(slot))
            .OfType<InputSignal>().Select(signal => new GameBinding(item.Map, item.Id, signal.Token)))
        .Distinct().ToArray();

    public bool Apply(GameAction action, InputSignal signal, bool remove)
    {
        bool axis = signal.Kind == "axis";
        if (action.IsAxis != axis)
            throw new InvalidOperationException($"{action.Name} needs a matching physical input.");
        var rows = Actions().Where(row => row.Map == action.Map && row.Id == action.Id).Select(row => row.Action).ToArray();
        var matching = rows.Where(SupportsPlainRow).SelectMany(Inputs).Where(slot => ReadInput(slot) == signal).ToArray();
        if (remove)
        {
            foreach (var slot in matching)
                Children(slot, "KEY").Single().Remove();
            bool removed = matching.Length > 0;
            _changed |= removed;
            return removed;
        }
        if (matching.Length > 0)
            return false;

        var candidates = rows.Where(row => HasCompatibleFlag(row, axis) && FreeSlot(row) is not null).ToArray();
        if (candidates.Length != 1)
            throw new InvalidOperationException($"{action.Name} needs one available matching binding row in the selected MSFS profile. Clear a slot or select the correct profile type.");
        var target = candidates[0];
        var (slotName, slotNode) = FreeSlot(target)!.Value;
        var (code, information) = Encode(signal);
        var key = new XElement(target.Name.Namespace + "KEY", new XAttribute("Information", information), code);
        if (axis && ReadFlags(target) == Digital)
            target.SetAttributeValue("Flag", Axis);
        if (slotNode is null)
        {
            var created = new XElement(target.Name.Namespace + slotName, key);
            if (slotName == Slots[0] && Children(target, Slots[1]).FirstOrDefault() is { } secondary)
                secondary.AddBeforeSelf(created);
            else
                target.Add(created);
        }
        else
            slotNode.Add(key);
        _changed = true;
        return true;
    }

    public NativeBindingWrite ToWrite()
    {
        if (IsCloudSave)
            throw new InvalidOperationException("Save the controls as an importable XML copy outside Xbox Cloud Saves.");
        return new NativeBindingWrite(Path, _original, Content());
    }

    public NativeBindingWrite ToCloudWrite()
    {
        if (!IsCloudSave)
            throw new InvalidOperationException("This controller profile is not in Microsoft Store/Xbox storage.");
        return new NativeBindingWrite(Path, _original, Content());
    }

    private byte[] Content()
    {
        if (!_changed)
            return _original;
        string body = string.Concat(_fragment.Nodes().Select(node => node.ToString(SaveOptions.DisableFormatting)));
        string text = (_declaration + body).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (_usesCrlf)
            text = text.Replace("\n", "\r\n", StringComparison.Ordinal);
        byte[] bytes = Utf8.GetBytes(text);
        return _hasBom ? [.. Bom, .. bytes] : bytes;
    }

    public static void EnsureGameClosed()
    {
        foreach (string name in new[] { "FlightSimulator", "FlightSimulator2024" })
            foreach (var process in Process.GetProcessesByName(name))
                using (process)
                    if (!process.HasExited)
                        throw new InvalidOperationException("Close Microsoft Flight Simulator before writing its active controls.");
    }

    private static bool IsCloudPath(string path)
    {
        string[] parts = System.IO.Path.GetFullPath(path)
            .Split(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        for (int index = 1; index < parts.Length; index++)
            if (parts[index - 1].Equals("SystemAppData", StringComparison.OrdinalIgnoreCase) &&
                parts[index].Equals("wgs", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private IEnumerable<(string Map, string Id, XElement Action)> Actions() =>
        from context in Children(_device, "Context")
        let map = ((string?)context.Attribute("ContextName"))?.Trim()
        where !string.IsNullOrEmpty(map)
        from action in Children(context, "Action")
        let name = ((string?)action.Attribute("ActionName"))?.Trim()
        where !string.IsNullOrEmpty(name)
        select (map, name, action);

    private static IEnumerable<XElement> Inputs(XElement action) => action.Elements().Where(node => Slots.Contains(node.Name.LocalName));

    private static (string Name, XElement? Node)? FreeSlot(XElement row)
    {
        // Do not use a duplicate or conditional slot when the profile does not identify a unique target.
        if (Slots.Any(name => Children(row, name).Skip(1).Any()))
            return null;
        foreach (var name in Slots)
        {
            var slot = Children(row, name).FirstOrDefault();
            if (slot is null || IsEmptySlot(slot))
                return (name, slot);
        }
        return null;
    }

    private static bool HasCompatibleFlag(XElement row, bool axis)
    {
        int flags = ReadFlags(row);
        if (!SupportsPlainFlags(flags))
            return false;
        if ((flags & AnalogOrAxis) != 0)
            return axis;
        // A game-generated empty row starts digital even when the exact catalog action is an axis.
        return axis ? flags == Digital && Inputs(row).All(IsEmptySlot) : (flags & Digital) != 0;
    }

    private static bool IsEmptySlot(XElement slot) => !slot.HasElements && !slot.HasAttributes && string.IsNullOrWhiteSpace(slot.Value);

    private static bool SupportsPlainRow(XElement row) => SupportsPlainFlags(ReadFlags(row));

    private static bool SupportsPlainFlags(int flags) => flags >= 0 && (flags & (16 | 32 | 64)) == 0;

    private static int ReadFlags(XElement row) =>
        int.TryParse((string?)row.Attribute("Flag"), NumberStyles.None, CultureInfo.InvariantCulture, out int flags) ? flags : -1;

    private static InputSignal? ReadInput(XElement slot)
    {
        var keys = slot.Elements().Take(2).ToArray();
        if (keys.Length != 1 || keys[0].Name.LocalName != "KEY" || keys[0].HasElements || slot.HasAttributes ||
            keys[0].Attributes().Any(attribute => attribute.Name != "Information"))
            return null;
        if ((string?)keys[0].Attribute("Information") is { Length: > 0 } information &&
            !information.StartsWith("Joystick", StringComparison.OrdinalIgnoreCase))
            return null;
        if (!int.TryParse(keys[0].Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int code))
            return null;
        int axis = Array.IndexOf(AxisCodes, code);
        if (axis >= 0)
            return InputSignal.Axis(axis + 1);
        return code switch
        {
            >= 0 and <= 127 => InputSignal.Button(code + 1),
            >= 256 and <= 259 => InputSignal.Hat(1, HatDirections[code - 256]),
            _ => null
        };
    }

    private static (int Code, string Information) Encode(InputSignal signal)
    {
        if (signal.Kind == "axis" && signal.Index is >= 1 and <= 8)
            return (AxisCodes[signal.Index - 1], AxisNames[signal.Index - 1]);
        if (signal.Kind == "button" && signal.Index is >= 1 and <= 128)
            return (signal.Index - 1, $"Joystick Button {signal.Index}");
        if (signal.Kind == "hat" && signal.Index == 1 && Array.IndexOf(HatDirections, signal.Direction) is var direction && direction >= 0)
            return (256 + direction, HatNames[direction]);
        throw new InvalidOperationException("The MSFS native mapping supports numbered buttons, eight axes, and the first POV hat.");
    }

    private static IEnumerable<XElement> Children(XElement parent, string name) =>
        parent.Elements().Where(child => child.Name.LocalName == name);

    private static bool ReadProductId(string? value, out ushort productId)
    {
        string text = value?.Trim() ?? "";
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out productId);
        return ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out productId) ||
            ushort.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out productId);
    }
}
