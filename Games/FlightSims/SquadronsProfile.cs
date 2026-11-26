using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using VisualControlsBinder.Inputs;

namespace VisualControlsBinder.Games;

internal sealed record SquadronsBinding(string Map, string Action, int DeviceSlot, string Token);

// The game stores independent positive and negative rows for an analog action.
// Numeric PC HOTAS tokens were measured in the original Squadrons profile reader:
// https://github.com/moff-wildfire/squadrons_config/blob/main/squadrons_config.py
internal sealed class SquadronsProfile
{
    private const int Hotas = 3;
    private const int UnboundAxis = 26;
    private const int UnboundButton = 174;
    private const string BindingPrefix = "GstKeyBinding.";
    private const string DevicePrefix = "GstInput.JoystickDevice";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Fields = ["altbutton", "axis", "button", "deviceid", "identifier", "modifier", "negate", "type"];
    private static readonly string[] Directions = ["up", "down", "left", "right"];
    private static readonly Channel[] PositiveAxes =
    [
        new(8, UnboundButton), new(11, UnboundButton), new(UnboundAxis, 40), new(UnboundAxis, 42),
        new(UnboundAxis, 44), new(UnboundAxis, 46), new(UnboundAxis, 14), new(UnboundAxis, 16)
    ];
    private static readonly Channel[] NegativeAxes =
    [
        new(10, UnboundButton), new(9, UnboundButton), new(UnboundAxis, 41), new(UnboundAxis, 43),
        new(UnboundAxis, 45), new(UnboundAxis, 47), new(UnboundAxis, 15), new(UnboundAxis, 17)
    ];

    private readonly byte[] _original;
    private readonly string[] _lines;
    private readonly bool _hasBom;
    private readonly Row[] _rows;
    private readonly HashSet<Row> _assigned = [];

    private SquadronsProfile(string sourcePath, byte[] original)
    {
        SourcePath = sourcePath;
        _original = original;
        _hasBom = original.AsSpan().StartsWith(Encoding.UTF8.GetPreamble());
        string contents = Utf8.GetString(original.AsSpan(_hasBom ? Encoding.UTF8.GetPreamble().Length : 0));
        _lines = Regex.Split(contents, @"(?<=\n)");
        var devices = new Dictionary<int, string>();
        var rows = new Dictionary<(string Map, string Concept, int Index), Dictionary<string, Field>>();
        int? version = null;

        for (int lineIndex = 0; lineIndex < _lines.Length; lineIndex++)
        {
            string line = _lines[lineIndex].TrimEnd('\r', '\n');
            int split = line.IndexOf(' ');
            if (split <= 0)
                continue;
            string key = line[..split];
            string value = line[(split + 1)..].Trim();
            if (key == BindingPrefix + "InputDataVersion")
            {
                if (version is not null || !Integer(value, out int parsed))
                    throw new InvalidDataException("Squadrons has an invalid input data version.");
                version = parsed;
            }
            else if (key.StartsWith(DevicePrefix, StringComparison.Ordinal) &&
                Integer(key[DevicePrefix.Length..], out int number) && number is > 0 and <= 512 && value.Length > 0)
            {
                if (!devices.TryAdd(number - 1, value))
                    throw new InvalidDataException("Squadrons defines the same joystick slot more than once.");
            }
            else if (key.StartsWith(BindingPrefix, StringComparison.Ordinal))
            {
                string[] parts = key[BindingPrefix.Length..].Split('.');
                if (parts.Length != 4 || !parts[1].StartsWith("Concept", StringComparison.Ordinal) ||
                    !Integer(parts[2], out int row) || !Fields.Contains(parts[3]) || !Integer(value, out int field))
                    continue;
                var rowKey = (parts[0], parts[1], row);
                if (!rows.TryGetValue(rowKey, out var entries))
                    rows.Add(rowKey, entries = new Dictionary<string, Field>(StringComparer.Ordinal));
                if (!entries.TryAdd(parts[3], new Field(lineIndex, field)))
                    throw new InvalidDataException("Squadrons defines the same binding field more than once.");
            }
        }
        if (version != 4 || rows.Count == 0)
            throw new InvalidDataException("Choose a Squadrons settings file with input data version 4.");
        _rows = rows.Where(entry => Fields.All(entry.Value.ContainsKey) && entry.Value["type"].Value == Hotas)
            .Select(entry => new Row(entry.Key.Map, entry.Key.Concept, entry.Value)).ToArray();
        Devices = devices;
    }

    public string SourcePath { get; }
    public IReadOnlyDictionary<int, string> Devices { get; }

    public static SquadronsProfile Load(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Choose the Squadrons ProfileOptions_profile_synced settings file.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        try { return new SquadronsProfile(Path.GetFullPath(path), bytes); }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Squadrons settings must use UTF-8 text.", ex);
        }
    }

    public int? FindDeviceSlot(string liveName)
    {
        string name = liveName.Trim();
        var matches = Devices.Where(entry => DeviceNameMatches(entry.Value, name)).Select(entry => entry.Key).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static bool DeviceNameMatches(string profileName, string deviceName) =>
        deviceName.Length > 0 && (profileName.Equals(deviceName, StringComparison.OrdinalIgnoreCase) ||
            (profileName.StartsWith(deviceName + " ", StringComparison.OrdinalIgnoreCase) &&
            Integer(profileName[deviceName.Length..].Trim(), out int occurrence) && occurrence > 0));

    public IEnumerable<SquadronsBinding> Bindings(IReadOnlyList<GameAction> catalog)
    {
        var actions = catalog.Select(action => (action.Map, action.Id, action.IsAxis)).ToHashSet();
        return _rows.Where(Editable).Select(row =>
        {
            if (!Devices.ContainsKey(row.Slot) || Decode(row, out _) is not { } signal)
                return null;
            bool axis = signal.Kind == "axis";
            string action = row.Concept;
            string signed = action + (row.Negative ? "/negative" : "/positive");
            if (!axis && actions.Contains((row.Map, signed, false)))
                action = signed;
            return actions.Contains((row.Map, action, axis))
                ? new SquadronsBinding(row.Map, action, row.Slot, signal.Token) : null;
        }).OfType<SquadronsBinding>().Distinct();
    }

    public bool Apply(GameAction action, int deviceSlot, string inputToken, bool remove)
    {
        if (!Devices.ContainsKey(deviceSlot))
            throw new InvalidOperationException("Connect this controller and let Squadrons register it in its joystick settings.");
        var signal = InputSignal.Parse(inputToken);
        if (signal is null || action.IsAxis != (signal.Kind == "axis"))
            throw new InvalidOperationException($"{action.Name} needs a matching physical input.");

        bool signedNegative = action.Id.EndsWith("/negative", StringComparison.Ordinal);
        bool directional = signedNegative || action.Id.EndsWith("/positive", StringComparison.Ordinal);
        string concept = directional ? action.Id[..action.Id.LastIndexOf('/')] : action.Id;
        var rows = _rows.Where(row => row.Map == action.Map && row.Concept == concept && Editable(row) &&
            (action.IsAxis || row.Negative == signedNegative)).ToArray();
        if (remove)
        {
            var matches = rows.Where(row => row.Slot == deviceSlot && Decode(row, out _) == signal).ToArray();
            foreach (var row in matches)
                Change(row, row.Slot, new Channel(UnboundAxis, UnboundButton));
            return matches.Length > 0;
        }

        if (action.IsAxis)
        {
            var positive = PickRow(rows.Where(row => !row.Negative), deviceSlot, signal, action.Name);
            var negative = PickRow(rows.Where(row => row.Negative), deviceSlot, signal, action.Name);
            var priorPositive = Decode(positive, out bool wasNegative);
            var priorNegative = Decode(negative, out bool wasPositive);
            bool invert = priorPositive is { Kind: "axis" } && priorPositive == priorNegative && wasNegative && !wasPositive;
            return Assign(deviceSlot, action.Name,
                (positive, Encode(signal, invert)), (negative, Encode(signal, !invert)));
        }
        var target = PickRow(rows, deviceSlot, signal, action.Name);
        return Assign(deviceSlot, action.Name, (target, Encode(signal, false)));
    }

    private static bool Editable(Row row) => row["altbutton"] == 0 && row["modifier"] == 0 &&
        row["negate"] is 0 or 1 && (row.Unbound || Decode(row, out _) is not null);

    private static Row PickRow(IEnumerable<Row> rows, int slot, InputSignal signal, string label)
    {
        var candidates = rows.ToArray();
        var exact = candidates.Where(row => !row.Unbound && row.Slot == slot && Decode(row, out _) == signal).ToArray();
        if (exact.Length == 1)
            return exact[0];
        var matching = candidates.Where(row => !row.Unbound && row.Slot == slot).ToArray();
        if (matching.Length == 1)
            return matching[0];
        var empty = candidates.Where(row => row.Unbound).ToArray();
        if (matching.Length == 0 && empty.Length == 1)
            return empty[0];
        if (matching.Length == 0 && empty.Length == 0 && candidates.Length > 0)
            throw new InvalidOperationException($"{label} is already assigned to a different controller. Remove that assignment first.");
        throw new InvalidOperationException($"{label} needs one unambiguous Flight Stick row per direction in the Squadrons file.");
    }

    private bool Assign(int slot, string label, params (Row Row, Channel Channel)[] targets)
    {
        var changes = targets.Where(target => target.Row.Slot != slot || target.Row.Channel != target.Channel).ToArray();
        if (changes.Any(target => _assigned.Contains(target.Row)))
            throw new InvalidOperationException($"Two planned changes target the same Squadrons binding for {label}.");
        foreach (var target in targets)
            _assigned.Add(target.Row);
        foreach (var target in changes)
            Change(target.Row, slot, target.Channel);
        return changes.Length > 0;
    }

    private static InputSignal? Decode(Row row, out bool negative)
    {
        var channel = row.Channel;
        int axis = Array.IndexOf(PositiveAxes, channel);
        negative = false;
        if (axis >= 0)
            return InputSignal.Axis(axis + 1);
        axis = Array.IndexOf(NegativeAxes, channel);
        if (axis >= 0)
        {
            negative = true;
            return InputSignal.Axis(axis + 1);
        }
        if (channel.Axis != UnboundAxis)
            return null;
        return channel.Button switch
        {
            >= 22 and <= 39 => InputSignal.Button(channel.Button - 21),
            >= 64 and <= 173 => InputSignal.Button(channel.Button - 45),
            >= 48 and <= 63 => InputSignal.Hat((channel.Button - 48) / 4 + 1, Directions[(channel.Button - 48) % 4]),
            _ => null
        };
    }

    private static Channel Encode(InputSignal signal, bool negative)
    {
        if (signal.Kind == "axis")
            return (negative ? NegativeAxes : PositiveAxes)[signal.Index - 1];
        int button = signal.Kind == "hat" ? 48 + 4 * (signal.Index - 1) + Array.IndexOf(Directions, signal.Direction)
            : signal.Index + (signal.Index <= 18 ? 21 : 45);
        return new Channel(UnboundAxis, button);
    }

    private void Change(Row row, int slot, Channel channel)
    {
        Set("axis", channel.Axis);
        Set("button", channel.Button);
        Set("deviceid", slot);

        void Set(string key, int value)
        {
            var field = row.Fields[key];
            if (field.Value == value)
                return;
            string line = _lines[field.Line];
            int start = line.IndexOf(' ') + 1;
            while (char.IsWhiteSpace(line[start]))
                start++;
            int end = start + 1;
            while (end < line.Length && !char.IsWhiteSpace(line[end]))
                end++;
            _lines[field.Line] = line[..start] + value.ToString(CultureInfo.InvariantCulture) + line[end..];
            row.Fields[key] = field with { Value = value };
        }
    }

    public void SaveChanges()
    {
        var encoded = Utf8.GetBytes(string.Concat(_lines));
        byte[] bytes = _hasBom ? [.. Encoding.UTF8.GetPreamble(), .. encoded] : encoded;
        if (!bytes.AsSpan().SequenceEqual(_original))
            NativeBindingWrites.Commit([new NativeBindingWrite(SourcePath, _original, bytes)], EnsureGameClosed);
    }

    private static void EnsureGameClosed()
    {
        var games = Process.GetProcessesByName("starwarssquadrons");
        try
        {
            if (games.Length > 0)
                throw new InvalidOperationException("Close STAR WARS: Squadrons before writing its control settings.");
        }
        finally
        {
            foreach (var game in games)
                game.Dispose();
        }
    }

    private static bool Integer(string value, out int number) =>
        int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number);

    private readonly record struct Field(int Line, int Value);
    private readonly record struct Channel(int Axis, int Button);
    private sealed record Row(string Map, string Concept, Dictionary<string, Field> Fields)
    {
        public int this[string key] => Fields[key].Value;
        public int Slot => this["deviceid"];
        public bool Negative => this["negate"] == 1;
        public Channel Channel => new(this["axis"], this["button"]);
        public bool Unbound => Channel == new Channel(UnboundAxis, UnboundButton);
    }
}
