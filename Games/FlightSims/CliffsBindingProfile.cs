using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SharpGen.Runtime;
using VisualControlsBinder.Inputs;
using Vortice.DirectInput;

namespace VisualControlsBinder.Games;

internal sealed partial class CliffsBindingProfile
{
    private static readonly string[] AxisKeys = ["AXE_X", "AXE_Y", "AXE_Z", "AXE_RX", "AXE_RY", "AXE_RZ", "AXE_U", "AXE_V"];
    private static readonly string[] PovDirections = ["up", "right", "down", "left"];
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly uint[] FingerprintTable = BuildFingerprintTable();
    private readonly byte[] _original;
    private readonly byte[] _preamble;
    private readonly Encoding _encoding;
    private readonly List<IniLine> _lines;
    private readonly string _newLine;

    private CliffsBindingProfile(string path, byte[] bytes)
    {
        Path = path;
        _original = bytes;
        var (encoding, preamble) = ReadEncoding(bytes);
        _encoding = encoding;
        _preamble = preamble;
        _lines = ReadLines(encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length));
        _newLine = _lines.FirstOrDefault(line => line.End.Length != 0)?.End ?? Environment.NewLine;
        if (!_lines.Any(line => SectionName(line.Text) is { } name && SectionMap(name) is not null))
            throw new InvalidDataException("Choose a Cliffs of Dover confUser.ini or a saved Controls INI.");
    }

    public string Path { get; }

    public IReadOnlyList<GameBinding> Bindings => ReadBindings().Where(row => row.Command is not null).Select(row =>
        new GameBinding(row.Map, row.Command!, row.Input)).ToArray();

    public IReadOnlyList<string> DeviceSlots => ReadBindings().Select(row => row.Slot)
        .Distinct(IgnoreCase).Order(IgnoreCase).ToArray();

    public static CliffsBindingProfile Load(string path)
    {
        string file = System.IO.Path.GetFullPath(path);
        using var stream = File.OpenRead(file);
        if (stream.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("The Cliffs of Dover controls file exceeds 16 MB.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return new CliffsBindingProfile(file, bytes);
    }

    public string? FindDevice(JoystickDevice device)
    {
        if (device.Id == Guid.Empty)
            return null;
        try
        {
            using var input = DInput.DirectInput8Create();
            using var controller = input.CreateDevice(device.Id);
            string? slot = StableDeviceSlot(controller.DeviceInfo.ProductName, controller.Properties.InterfacePath);
            return slot is null ? null : DeviceSlots.FirstOrDefault(existing => IgnoreCase.Equals(existing, slot)) ?? slot;
        }
        catch (SharpGenException) { return null; }
    }

    internal static string? StableDeviceSlot(string name, string interfacePath)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrEmpty(interfacePath) ||
            interfacePath.Any(character => !char.IsAscii(character) || char.IsControl(character)))
            return null;
        // Blitz uses the DirectInput device interface path, rather than either DirectInput GUID.
        uint fingerprint = 0;
        foreach (char character in interfacePath)
            fingerprint = ((fingerprint << 8) | character) ^ FingerprintTable[fingerprint >> 24];
        string slot = FormattableString.Invariant($"{name.Replace(' ', '_').Replace('+', '_')}-{fingerprint:X}");
        return IsDeviceSlot(slot) ? slot : null;
    }

    private static uint[] BuildFingerprintTable()
    {
        uint[] basis = [0x23788D5E, 0x46F11ABC, 0x0DE23578, 0x38BCE7AE, 0x7179CF5C, 0x62F39EB8, 0x669FB02E, 0x6E47ED02];
        var table = new uint[256];
        for (int index = 1; index < table.Length; index++)
        {
            int bit = System.Numerics.BitOperations.TrailingZeroCount(index);
            table[index] = table[index & (index - 1)] ^ basis[bit];
        }
        return table;
    }

    public static string? EncodeInput(string slot, InputSignal signal)
    {
        if (!IsDeviceSlot(slot))
            return null;
        if (signal.Kind == "axis" && signal.Index >= 1 && signal.Index <= AxisKeys.Length)
            return $"{slot}+{AxisKeys[signal.Index - 1]}";
        if (signal.Kind == "button" && signal.Index is >= 1 and <= 128)
            return FormattableString.Invariant($"{slot}+Key{signal.Index - 1}");
        if (signal.Kind != "hat" || signal.Index is < 1 or > 4)
            return null;
        int direction = Array.IndexOf(PovDirections, signal.Direction);
        if (direction < 0)
            return null;
        string pov = signal.Index == 1 ? "Pov" : FormattableString.Invariant($"Pov{signal.Index - 1}");
        return FormattableString.Invariant($"Pov{direction * 90} {slot}+{pov}");
    }

    public static (string Slot, InputSignal Signal)? DecodeInput(string native)
    {
        var match = InputPattern().Match(native.Trim());
        if (!match.Success || !IsDeviceSlot(match.Groups["slot"].Value))
            return null;
        string slot = match.Groups["slot"].Value;
        string control = match.Groups["control"].Value;
        if (TryIndex(match.Groups["angle"].Value, out int angle))
        {
            if (angle is not (0 or 90 or 180 or 270) || !control.StartsWith("Pov", StringComparison.OrdinalIgnoreCase))
                return null;
            int hat = 0;
            if (control.Length > 3 && (!TryIndex(control[3..], out hat) || hat is < 1 or > 3))
                return null;
            return (slot, InputSignal.Hat(hat + 1, PovDirections[angle / 90]));
        }
        if (control.StartsWith("Key", StringComparison.OrdinalIgnoreCase) &&
            TryIndex(control[3..], out int button) && button is >= 0 and < 128)
            return (slot, InputSignal.Button(button + 1));
        int axis = Array.FindIndex(AxisKeys, key => IgnoreCase.Equals(key, control));
        return axis >= 0 ? (slot, InputSignal.Axis(axis + 1)) : null;
    }

    public bool Apply(GameAction action, string nativeInput, bool remove)
    {
        var mapped = DecodeInput(nativeInput);
        if (mapped is null || (mapped.Value.Signal.Kind == "axis") != action.IsAxis)
            throw new InvalidOperationException($"{action.Name}: choose a compatible controller input.");
        if (!MapPattern().IsMatch(action.Map) || CliffsOfDoverGame.CreateAction(action.Map, action.Id) is not { } known ||
            known.IsAxis != action.IsAxis || known.Id != action.Id)
            throw new InvalidDataException($"{action.Name}: unrecognized Cliffs of Dover command.");

        var rows = ReadBindings().Where(row => IgnoreCase.Equals(row.Map, action.Map) &&
            SameSource(row.Input, mapped.Value)).ToArray();
        if (remove)
        {
            var targets = rows.Where(row => IgnoreCase.Equals(row.Command, action.Id)).ToArray();
            foreach (var row in targets.Reverse())
                RemoveLine(row);
            return targets.Length != 0;
        }
        if (rows.Length > 1)
            throw new InvalidDataException($"{nativeInput} is listed more than once in {action.Map}. Resolve those bindings in Cliffs of Dover first.");
        string command = action.IsAxis ? $"1{action.Id}" : action.Id;
        if (rows is [var existing])
        {
            if (existing.Command is null)
                throw new InvalidDataException($"{nativeInput} has an unrecognized binding in {action.Map}.");
            if (IgnoreCase.Equals(existing.Command, action.Id))
                return false;
            var line = _lines[existing.Line];
            _lines[existing.Line] = line with { Text = line.Text[..existing.ValueStart] + command + line.Text[existing.ValueEnd..] };
        }
        else
            InsertBinding(action.Map, $"{nativeInput}={command}");
        return true;
    }

    public NativeBindingWrite ToWrite(string? copyPath = null)
    {
        string text = string.Concat(_lines.Select(line => line.Text + line.End));
        return new NativeBindingWrite(copyPath ?? Path, copyPath is null ? _original : null, [.. _preamble, .. _encoding.GetBytes(text)]);
    }

    public static void EnsureGameClosed(string? installDirectory = null)
    {
        var gameRoot = installDirectory is null ? null : System.IO.Path.TrimEndingDirectorySeparator(installDirectory);
        foreach (var process in Process.GetProcessesByName("Launcher64"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } image && System.IO.Path.GetDirectoryName(image) is { } running &&
                        !IgnoreCase.Equals(running, gameRoot) && !File.Exists(CliffsInstalledControls.GameAssembly(running)))
                        continue;
                }
                catch (InvalidOperationException) { continue; }
                catch (Win32Exception) { }
                throw new InvalidOperationException("Close Cliffs of Dover before updating its active control profile.");
            }
        }
    }

    private IEnumerable<BindingLine> ReadBindings()
    {
        string? map = null;
        for (int index = 0; index < _lines.Count; index++)
        {
            string text = _lines[index].Text;
            if (SectionName(text) is { } section)
            {
                map = SectionMap(section);
                continue;
            }
            if (map is null || text.TrimStart() is [';' or '#', ..])
                continue;
            int equals = text.IndexOf('=');
            if (equals <= 0)
                continue;
            string input = text[..equals].Trim();
            if (DecodeInput(input) is not { } native)
                continue;
            int valueStart = equals + 1;
            while (valueStart < text.Length && char.IsWhiteSpace(text[valueStart]))
                valueStart++;
            int comment = text.IndexOfAny([';', '#'], valueStart);
            int valueEnd = comment >= 0 ? comment : text.Length;
            while (valueEnd > valueStart && char.IsWhiteSpace(text[valueEnd - 1]))
                valueEnd--;
            var action = CliffsOfDoverGame.CreateAction(map, text[valueStart..valueEnd]);
            string? command = action?.IsAxis == (native.Signal.Kind == "axis") ? action.Id : null;
            yield return new BindingLine(index, map, input, command, native.Slot, valueStart, valueEnd, comment);
        }
    }

    private void RemoveLine(BindingLine target)
    {
        if (target.Comment < 0)
        {
            _lines.RemoveAt(target.Line);
            return;
        }
        IniLine line = _lines[target.Line];
        int indent = line.Text.Length - line.Text.TrimStart().Length;
        _lines[target.Line] = line with { Text = line.Text[..indent] + line.Text[target.Comment..] };
    }

    private void InsertBinding(string map, string binding)
    {
        var sections = _lines.Select((line, index) => (Name: SectionName(line.Text), Index: index))
            .Where(row => row.Name is not null).ToArray();
        int section = Array.FindIndex(sections, row => IgnoreCase.Equals(SectionMap(row.Name!), map));
        if (section < 0)
        {
            string header = map.Equals("FreeFly", StringComparison.OrdinalIgnoreCase) ||
                map.Equals("FreeFlyXYZ", StringComparison.OrdinalIgnoreCase) ? map : $"HotKey {map}";
            InsertLine(_lines.Count, $"[{header}]");
            InsertLine(_lines.Count, binding);
        }
        else
        {
            if (sections.Skip(section + 1).Any(row => IgnoreCase.Equals(SectionMap(row.Name!), map)))
                throw new InvalidDataException($"The {map} controls section appears more than once.");
            int end = section + 1 < sections.Length ? sections[section + 1].Index : _lines.Count;
            InsertLine(end, binding);
        }
    }

    private void InsertLine(int index, string text)
    {
        if (index > 0 && _lines[index - 1].End.Length == 0)
            _lines[index - 1] = _lines[index - 1] with { End = _newLine };
        _lines.Insert(index, new IniLine(text, _newLine));
    }

    private static string? SectionName(string line)
    {
        var match = SectionPattern().Match(line);
        return match.Success ? match.Groups["name"].Value.Trim() : null;
    }

    private static string? SectionMap(string section)
    {
        var (map, calibration) = CliffsOfDoverGame.GetMap(section);
        return !calibration && map.Length > 0 ? map : null;
    }

    private static bool SameSource(string input, (string Slot, InputSignal Signal) native) =>
        DecodeInput(input) is { } read && IgnoreCase.Equals(read.Slot, native.Slot) && read.Signal == native.Signal;

    private static bool IsDeviceSlot(string slot) => LegacySlot().IsMatch(slot) || NamedSlot().IsMatch(slot);

    private static bool TryIndex(string text, out int value) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);

    private static (Encoding Encoding, byte[] Preamble) ReadEncoding(byte[] bytes)
    {
        foreach (Encoding encoding in new[] { Encoding.UTF8, Encoding.Unicode, Encoding.BigEndianUnicode })
            if (encoding.GetPreamble() is { Length: > 0 } bom && bytes.AsSpan().StartsWith(bom))
                return (encoding, bom);
        try
        {
            Utf8.GetCharCount(bytes);
            return (Utf8, []);
        }
        catch (DecoderFallbackException) { return (Encoding.Latin1, []); }
    }

    private static List<IniLine> ReadLines(string text)
    {
        var lines = new List<IniLine>();
        for (int start = 0; start < text.Length;)
        {
            int end = text.AsSpan(start).IndexOfAny('\r', '\n');
            if (end < 0)
            {
                lines.Add(new IniLine(text[start..], ""));
                break;
            }
            end += start;
            int next = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
            lines.Add(new IniLine(text[start..end], text[end..next]));
            start = next;
        }
        return lines;
    }

    private sealed record IniLine(string Text, string End);
    private sealed record BindingLine(int Line, string Map, string Input, string? Command, string Slot, int ValueStart, int ValueEnd, int Comment);

    [GeneratedRegex(@"^(?:Pov(?<angle>[0-9]{1,3})\s+)?(?<slot>[^\s+]+)\+(?<control>[^\s+]+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InputPattern();

    [GeneratedRegex(@"^Joystick(?:[1-9]|1[0-5])?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacySlot();

    [GeneratedRegex(@"^[^\s=+\[\];#\p{C}]{1,260}-[0-9a-f]{1,8}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedSlot();

    [GeneratedRegex(@"^\s*\[(?<name>[^\[\]]+)\]\s*(?:[;#].*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SectionPattern();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex MapPattern();
}
