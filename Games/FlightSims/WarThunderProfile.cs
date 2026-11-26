using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VisualControlsBinder.Games;

public sealed record WarThunderDevice(int VendorId, int ProductId, string Name, bool Connected,
    int ButtonsOffset, int ButtonsCount, int AxesOffset, int AxesCount)
{
    public int NativeButton(int oneBasedButton) => NativeIndex(oneBasedButton, ButtonsOffset, ButtonsCount, "button");
    public int NativeAxisSlot(int oneBasedSlot) => NativeIndex(oneBasedSlot, AxesOffset, AxesCount, "axis slot");
    public int NativeDirectInputAxis(int index)
    {
        if (AxesCount != 8)
            throw new InvalidOperationException($"War Thunder compacts the {AxesCount} axes on {Name}. This profile does not identify their DirectInput axis names.");
        return NativeAxisSlot(index);
    }

    public int? DirectInputAxis(int nativeAxisId) => AxesCount == 8 && AxesOffset >= 0 &&
        nativeAxisId >= AxesOffset && nativeAxisId - AxesOffset < AxesCount ? nativeAxisId - AxesOffset + 1 : null;

    private int NativeIndex(int index, int offset, int count, string label) => index >= 1 && index <= count && offset >= 0
        ? checked(offset + index - 1)
        : throw new InvalidOperationException($"{Name} has no {label} {index} in the exported War Thunder profile.");
}

public sealed partial class WarThunderProfile
{
    private string text;
    private byte[] sourceBytes;
    private readonly string newLine;
    private readonly bool hasBom;
    private readonly bool hasNamedBasePreset;

    private WarThunderProfile(string sourcePath, byte[] sourceBytes, string text, bool hasBom)
    {
        SourcePath = sourcePath;
        this.sourceBytes = sourceBytes;
        this.text = text;
        this.hasBom = hasBom;
        newLine = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var (code, sections) = ReadSections(out _, out bool machine);
        IsMachineProfile = machine;
        hasNamedBasePreset = sections.TryGetValue("basePresetPaths", out var presets) &&
            IgnoredText().Matches(text[(presets.Open + 1)..presets.Close])
                .Any(match => match.Value[0] == '"' && !string.IsNullOrWhiteSpace(match.Value[1..^1]));
        Devices = sections.TryGetValue("deviceMapping", out var mapping)
            ? Blocks(code, mapping.Open + 1, mapping.Close).Where(block => block.Name == "joystick")
                .Select(ReadDevice).OfType<WarThunderDevice>().ToArray()
            : [];
    }

    public IReadOnlyList<WarThunderDevice> Devices { get; }
    public string SourcePath { get; }
    public bool IsMachineProfile { get; }

    public IReadOnlyList<(string Map, string Id)> ActionIds => ReadControls().Select(item => (item.Map, item.Block.Name)).Distinct().ToArray();

    public IReadOnlyList<GameBinding> Bindings => ReadControls().SelectMany(item =>
        item.Map == "hotkeys" ? ReadButtonBinding(item.Block) :
            Fields(item.Block, AxisField()).Where(field => !field.Value.StartsWith('-'))
                .Select(field => new GameBinding(item.Map, item.Block.Name, $"axisId:{field.Value}"))).ToArray();

    public static WarThunderProfile Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (new FileInfo(fullPath).Length > WarThunderGame.ProfileSizeLimit)
            throw new InvalidDataException("The War Thunder controls file exceeds 16 MB.");
        var bytes = File.ReadAllBytes(fullPath);
        bool hasBom = bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble());
        string text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
        return new WarThunderProfile(fullPath, bytes, text, hasBom);
    }

    internal static bool IsControlExport(string path)
    {
        using var reader = File.OpenText(path);
        Span<char> start = stackalloc char[256];
        int chars = reader.ReadBlock(start);
        ReadOnlySpan<char> header = start[..chars];
        return header.TrimStart().StartsWith("controls{", StringComparison.OrdinalIgnoreCase);
    }

    public WarThunderDevice? FindDevice(int vendorId, int productId)
    {
        var matches = Devices.Where(device => device.VendorId == vendorId && device.ProductId == productId).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public bool ApplyAxis(string actionId, WarThunderDevice device, int directInputAxis, bool remove = false) =>
        ApplyAxisBinding(actionId, device.NativeDirectInputAxis(directInputAxis), remove, device);

    public bool Apply(string map, string actionId, int nativeInputId, bool remove = false) => map switch
    {
        "hotkeys" => ApplyButton(actionId, nativeInputId, BindingActivation.Tap, remove),
        "axes" => ApplyAxisBinding(actionId, nativeInputId, remove),
        _ => throw new ArgumentException("Choose a War Thunder hotkey or axis.", nameof(map))
    };

    public bool ApplyButton(string actionId, int nativeButtonId, BindingActivation activation, bool remove = false)
    {
        // ActivationCondition in War Thunder's stock controls runtime. DEFAULT is omitted on export.
        string field = activation switch
        {
            BindingActivation.Tap => "",
            BindingActivation.DoubleTap => $"{newLine}      activationType:i=1",
            BindingActivation.Hold => $"{newLine}      activationType:i=2",
            _ => throw new ArgumentOutOfRangeException(nameof(activation))
        };
        var (section, matches) = ActionBlocks("hotkeys", actionId, nativeButtonId);
        var exact = matches.Where(block => ReadButton(block) == (nativeButtonId, activation)).ToArray();
        if (remove)
        {
            foreach (var block in exact.Reverse())
            {
                // An empty hotkey block keeps the action unbound when a base preset supplies a default.
                if (exact.Length == matches.Length && block == exact[0])
                {
                    text = text.Remove(block.Open + 1, block.Close - block.Open - 1);
                    continue;
                }
                text = text.Remove(block.Start, block.Close - block.Start + 1);
            }
            return exact.Length > 0;
        }
        if (exact.Length != 0)
            return false;
        if (matches.Length == 0)
            RequireExplicitOverride(actionId);
        string input = nativeButtonId.ToString(CultureInfo.InvariantCulture);
        string binding = $"{newLine}      joyButton:i={input}{field}{newLine}    ";
        var empty = matches.FirstOrDefault(block => string.IsNullOrWhiteSpace(text[(block.Open + 1)..block.Close]));
        if (empty is null)
            InsertIn(section, $"    {actionId}{{{binding}}}", "  ");
        else
            text = text.Remove(empty.Open + 1, empty.Close - empty.Open - 1).Insert(empty.Open + 1, binding);
        return true;
    }

    private bool ApplyAxisBinding(string actionId, int nativeInputId, bool remove, WarThunderDevice? device = null)
    {
        var (section, matches) = ActionBlocks("axes", actionId, nativeInputId);
        string input = nativeInputId.ToString(CultureInfo.InvariantCulture);
        if (matches.Length > 1)
            throw new InvalidDataException($"{actionId} has multiple axis blocks. Choose a profile with one axis definition.");
        if (matches.Length == 0)
        {
            if (remove)
                return false;
            RequireExplicitOverride(actionId);
            InsertIn(section, $"    {actionId}{{{newLine}      axisId:i={input}{newLine}    }}", "  ");
            return true;
        }

        var axis = matches[0];
        var fields = Fields(axis, AxisField()).ToArray();
        if (fields.Length > 1)
            throw new InvalidDataException($"{actionId} has multiple axis IDs. Choose an unambiguous axis entry.");
        if (remove)
        {
            if (fields.Length == 0 || fields[0].Value != input)
                return false;
            text = text.Remove(fields[0].Index, fields[0].Length).Insert(fields[0].Index, "-1");
            return true;
        }
        if (fields.Length == 0)
            InsertIn(axis, $"      axisId:i={input}", "    ");
        else if (fields[0].Value == input)
            return false;
        else
        {
            if (device is not null && (!int.TryParse(fields[0].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int prior) ||
                (prior >= 0 && device.DirectInputAxis(prior) is null)))
                throw new InvalidOperationException($"{actionId} already uses an axis on a different controller. Remove that assignment first.");
            text = text.Remove(fields[0].Index, fields[0].Length).Insert(fields[0].Index, input);
        }
        return true;
    }

    public void SaveAs(string destination)
    {
        NativeBindingWrites.Commit(ToWrites(destination));
    }

    public void SaveChanges()
    {
        var writes = ToWrites();
        NativeBindingWrites.Commit(writes, EnsureGameClosed);
        if (writes.Count > 0)
            sourceBytes = writes[0].Content;
    }

    internal IReadOnlyList<NativeBindingWrite> ToWrites(string? copyPath = null)
    {
        if (copyPath is not null)
        {
            ValidateExportPath(copyPath);
            return [new NativeBindingWrite(Path.GetFullPath(copyPath), null, EncodeControls())];
        }
        var output = Encode();
        return sourceBytes.AsSpan().SequenceEqual(output) ? [] : PrepareUpdate(output);
    }

    private IReadOnlyList<NativeBindingWrite> PrepareUpdate(byte[] output)
    {
        if (!IsMachineProfile)
            return [new NativeBindingWrite(SourcePath, sourceBytes, output)];
        var active = WarThunderGame.FindActivePair(WarThunderGame.SavesDirectory);
        if (active is null || !SourcePath.Equals(active.AccountPath, StringComparison.OrdinalIgnoreCase) ||
            !sourceBytes.AsSpan().SequenceEqual(active.Snapshot))
            throw new InvalidOperationException("War Thunder’s last-used account profile could not be uniquely verified. Close the game and reload its active controls.");
        return [new NativeBindingWrite(SourcePath, sourceBytes, output), new NativeBindingWrite(active.MirrorPath, active.Snapshot, output)];
    }

    private byte[] EncodeControls()
    {
        if (!IsMachineProfile)
            return Encode();
        ReadSections(out var controls, out _);
        return Encode(text[controls.Start..(controls.Close + 1)] + newLine);
    }

    private static void ValidateExportPath(string destination)
    {
        if (!Path.GetExtension(destination).Equals(".blk", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Save the new War Thunder controls preset with a .blk extension.", nameof(destination));
    }

    private byte[] Encode() => Encode(text);

    private byte[] Encode(string contents)
    {
        var content = Encoding.UTF8.GetBytes(contents);
        return hasBom ? [..Encoding.UTF8.GetPreamble(), ..content] : content;
    }

    internal void EnsureGameClosed()
    {
        string install = Path.TrimEndingDirectorySeparator(WarThunderGame.DefaultFolder) + Path.DirectorySeparatorChar;
        string saves = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "WarThunder") +
            Path.DirectorySeparatorChar;
        if (!IsMachineProfile && !SourcePath.StartsWith(install, StringComparison.OrdinalIgnoreCase)
            && !SourcePath.StartsWith(saves, StringComparison.OrdinalIgnoreCase))
            return;

        EnsureWarThunderClosed();
    }

    internal static void EnsureWarThunderClosed()
    {
        var games = Process.GetProcessesByName("aces");
        foreach (var game in games)
            game.Dispose();
        if (games.Length > 0)
            throw new InvalidOperationException("Close War Thunder before updating its loaded preset. You can still export a new copy.");
    }

    private (Block Section, Block[] Matches) ActionBlocks(string map, string actionId, int inputId)
    {
        if (!ValidId().IsMatch(actionId) || inputId < 0)
            throw new ArgumentException("Choose a War Thunder control and a valid global joystick index.");
        var (code, sections) = ReadSections();
        if (!sections.TryGetValue(map, out var section))
            throw new InvalidDataException($"This Controls export has no {map} section. Export a new profile from War Thunder first.");
        return (section, Blocks(code, section.Open + 1, section.Close).Where(block => block.Name == actionId).ToArray());
    }

    private void RequireExplicitOverride(string actionId)
    {
        if (hasNamedBasePreset)
            throw new InvalidOperationException($"{actionId} may inherit bindings from the base preset. Add a binding for this action in War Thunder, save the controls, and reload the profile in VCP to preserve its inherited bindings.");
    }

    private IEnumerable<GameBinding> ReadButtonBinding(Block block)
    {
        if (ReadButton(block) is { } button)
            yield return new GameBinding("hotkeys", block.Name,
                $"joyButton:{button.Id.ToString(CultureInfo.InvariantCulture)}") { Activation = button.Activation };
    }

    private (int Id, BindingActivation Activation)? ReadButton(Block block)
    {
        var match = SingleButton().Match(text[(block.Open + 1)..block.Close]);
        var before = match.Groups["before"];
        var after = match.Groups["after"];
        if (!match.Success || (before.Success && after.Success) ||
            !int.TryParse(match.Groups["button"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            return null;
        return (id, (before.Success ? before.Value : after.Value) switch
        {
            "1" => BindingActivation.DoubleTap,
            "2" => BindingActivation.Hold,
            _ => BindingActivation.Tap
        });
    }

    private void InsertIn(Block block, string lines, string closingIndent) =>
        text = text.Insert(block.Close, newLine + lines + newLine + closingIndent);

    private WarThunderDevice? ReadDevice(Block block)
    {
        string source = text[(block.Open + 1)..block.Close];
        var identity = DeviceId().Match(source);
        if (!int.TryParse(identity.Groups["vid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int vid)
            || !int.TryParse(identity.Groups["pid"].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int pid))
            return null;

        return new WarThunderDevice(vid, pid, DeviceName().Match(source).Groups["name"].Value.Trim(),
            ConnectedField().IsMatch(source), IntValue(source, "buttonsOffset"), IntValue(source, "buttonsCount"),
            IntValue(source, "axesOffset"), IntValue(source, "axesCount"));
    }

    private static int IntValue(string source, string name)
    {
        var field = Regex.Match(source, $@"(?m)^\s*{name}:i\s*=\s*(?<number>-?\d+)");
        return int.TryParse(field.Groups["number"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : -1;
    }

    private IEnumerable<(string Map, Block Block)> ReadControls()
    {
        var (code, sections) = ReadSections();
        foreach (string map in new[] { "hotkeys", "axes" })
            if (sections.TryGetValue(map, out var section))
                foreach (var block in Blocks(code, section.Open + 1, section.Close))
                    yield return (map, block);
    }

    private (string Code, Dictionary<string, Block> Sections) ReadSections() => ReadSections(out _, out _);

    private (string Code, Dictionary<string, Block> Sections) ReadSections(out Block controls, out bool machine)
    {
        string code = IgnoredText().Replace(text, match => new string(' ', match.Length));
        var roots = Blocks(code, 0, code.Length).ToArray();
        var export = roots.SingleOrDefault(block => block.Name == "controls");
        var content = roots.SingleOrDefault(block => block.Name == "content");
        var wrapped = export is null && content is not null
            ? Blocks(code, content.Open + 1, content.Close).SingleOrDefault(block => block.Name == "controls") : null;
        controls = export ?? wrapped ?? throw new InvalidDataException("Choose a War Thunder file containing version 5 controls.");
        machine = wrapped is not null;
        if (IntValue(code[(controls.Open + 1)..controls.Close], "version") != 5)
            throw new InvalidDataException("Choose a War Thunder file containing version 5 controls.");
        return (code, Blocks(code, controls.Open + 1, controls.Close).ToDictionary(block => block.Name, StringComparer.Ordinal));
    }

    private IEnumerable<(int Index, int Length, string Value)> Fields(Block block, Regex pattern)
    {
        int start = block.Open + 1;
        string body = IgnoredText().Replace(text[start..block.Close], match => new string(' ', match.Length));
        foreach (Match match in pattern.Matches(body))
        {
            var value = match.Groups["value"];
            yield return (start + value.Index, value.Length, value.Value);
        }
    }

    private static IEnumerable<Block> Blocks(string code, int start, int end)
    {
        int position = start;
        while (position < end)
        {
            Match match = BlockStart().Match(code, position, end - position);
            if (!match.Success)
                yield break;
            int open = match.Index + match.Length - 1;
            int depth = 1;
            int next = open + 1;
            while (next < end && depth != 0)
            {
                if (code[next] == '{') depth++;
                else if (code[next] == '}') depth--;
                next++;
            }
            if (depth != 0)
                throw new InvalidDataException("The War Thunder Controls file has an unfinished block.");

            yield return new Block(match.Groups["name"].Value, match.Index, open, next - 1);
            position = next;
        }
    }

    private sealed record Block(string Name, int Start, int Open, int Close);

    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex ValidId();

    [GeneratedRegex(@"(?<name>[A-Za-z0-9_./-]+)\s*\{")]
    private static partial Regex BlockStart();

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"|//[^\r\n]*|/\\*[\\s\\S]*?\\*/")]
    private static partial Regex IgnoredText();

    [GeneratedRegex(@"\baxisId:i\s*=\s*(?<value>-?\d+)")]
    private static partial Regex AxisField();

    [GeneratedRegex(@"\A\s*(?:activationType:i\s*=\s*(?<before>[012])\s+)?joyButton:i\s*=\s*(?<button>[0-9]+)(?:\s+activationType:i\s*=\s*(?<after>[012]))?\s*\z")]
    private static partial Regex SingleButton();

    [GeneratedRegex("devId:t\\s*=\\s*\"(?<vid>[a-fA-F0-9]{4}):(?<pid>[a-fA-F0-9]{4})\"")]
    private static partial Regex DeviceId();

    [GeneratedRegex("name:t\\s*=\\s*\"(?<name>[^\"]+)\"")]
    private static partial Regex DeviceName();

    [GeneratedRegex(@"\bconnected:b\s*=\s*yes\b")]
    private static partial Regex ConnectedField();
}
