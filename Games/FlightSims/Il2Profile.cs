using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VisualControlsBinder.Games;

public sealed partial class Il2Profile
{
    private const string ActionMap = "Key Mapping";
    private readonly ProfileText _actions;
    private readonly ProfileText _map;
    private readonly ActionRow[] _sourceActions;
    private readonly RuntimeRow[] _sourceRuntime;
    private readonly Dictionary<string, string> _runtimeDevices;

    private Il2Profile(ProfileText actions, ProfileText map)
    {
        _actions = actions;
        _map = map;
        _sourceActions = ActionRows().ToArray();
        _sourceRuntime = RuntimeRows().ToArray();
        _runtimeDevices = VerifyDeviceSlots(_sourceActions, _sourceRuntime);
    }

    public string SourcePath => _actions.Path;
    public string MapPath => _map.Path;
    public IReadOnlyList<GameBinding> Bindings => ActionRows().Where(row => JoystickToken().IsMatch(row.Input))
        .Select(row => new GameBinding(ActionMap, row.Action, row.Input)).ToArray();

    public static Il2Profile Load(string actionsPath)
    {
        string path = Path.GetFullPath(actionsPath);
        if (!Path.GetExtension(path).Equals(".actions", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose an IL-2 Great Battles .actions file.", nameof(actionsPath));
        string mapPath = CompanionPath(path);
        if (!File.Exists(mapPath))
            throw new FileNotFoundException($"The IL-2 profile needs its companion {Path.GetFileName(mapPath)}.", mapPath);
        var actions = ProfileText.Read(path);
        var map = ProfileText.Read(mapPath);
        if (!actions.Lines.Any(line => line.TrimStart().StartsWith("&actions=action,command,invert|", StringComparison.Ordinal))
            || !map.Lines.Any(IsMainKeyset))
            throw new InvalidDataException("Choose a Great Battles .actions and generated .map pair with a main keyset.");
        return new Il2Profile(actions, map);
    }

    public bool Apply(GameAction action, string input, bool remove = false)
    {
        input = input.Trim().ToLowerInvariant();
        Match signal = JoystickToken().Match(input);
        bool axis = signal.Groups["axis"].Success;
        if (!signal.Success || action.Map != ActionMap || (!remove && axis != action.IsAxis))
            throw new InvalidOperationException($"{action.Name}: choose a compatible IL-2 joystick button, axis, or POV direction.");
        var rows = ActionRows().Where(row => row.Action == action.Id).ToArray();
        if (rows.Length == 0)
            throw new InvalidOperationException($"{action.Name} is unavailable in this .actions profile.");
        var same = rows.Where(row => row.Input.Equals(input, StringComparison.OrdinalIgnoreCase)).ToArray();
        if ((!remove && same.Length > 0) || (remove && same.Length == 0))
            return false;

        string device = signal.Groups["device"].Value;
        if (!_runtimeDevices.TryGetValue(device, out string? runtimeDevice))
            throw new InvalidOperationException($"{action.Name}: The profile does not identify a joystick slot for {device}. Save two different button bindings for this controller in IL-2 and reload the profile.");
        string runtimeInput = $"{runtimeDevice}_{signal.Groups["gesture"].Value}";
        var runtime = RuntimeRows().Where(row => IsHandler(row.Action, action.Id)).ToArray();
        if (remove)
        {
            var matching = runtime.Where(row => row.Input.TrimStart('_').Equals(runtimeInput, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length != 1 || same.Length != 1)
                throw new InvalidOperationException($"{action.Name}: the existing input cannot be matched to a unique runtime handler.");

            _actions.Lines[same[0].Index] = ReplaceInput(same[0], "", "0");
            _map.Lines.RemoveAt(matching[0].Index);
            return true;
        }

        if (rows.Count(row => row.Input.Length > 0) >= 3)
            throw new InvalidOperationException($"{action.Name} already has three inputs. Remove one before adding another.");
        Recipe recipe = FindRecipe(action, axis);
        string runtimeToken = recipe.InvertedToken ? "_" + runtimeInput : runtimeInput;
        if (!runtime.Any(row => row.Action == recipe.Handler && row.Type == recipe.Type
            && row.Input.Equals(runtimeToken, StringComparison.OrdinalIgnoreCase)))
        {
            int insert = runtime.LastOrDefault(row => row.Action == recipe.Handler) is { } last
                ? last.Index + 1 : EndOfMainKeyset();
            _map.Lines.Insert(insert, $"action(\"{recipe.Handler}\",\"{recipe.Type}\",\"{runtimeToken}\");{_map.Newline}");
        }

        var empty = rows.FirstOrDefault(row => row.Input.Length == 0);
        if (empty is not null)
            _actions.Lines[empty.Index] = ReplaceInput(empty, input, recipe.Invert);
        else
            _actions.Lines.Insert(rows[^1].Index + 1, $"{action.Id}, {input}, {recipe.Invert}|{_actions.Newline}");
        return true;
    }

    public void SaveAs(string destinationActionsPath)
    {
        string path = Path.GetFullPath(destinationActionsPath);
        if (!Path.GetExtension(path).Equals(".actions", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Save the new IL-2 profile with an .actions extension.", nameof(destinationActionsPath));
        if (!Directory.Exists(Path.GetDirectoryName(path)))
            throw new DirectoryNotFoundException($"Choose an existing folder for the IL-2 profile: {path}.");
        string companion = CompanionPath(path);
        NativeBindingWrites.Commit([new(path, null, _actions.Bytes()), new(companion, null, _map.Bytes())]);
    }

    public void SaveChanges()
    {
        byte[] actions = _actions.Bytes();
        byte[] map = _map.Bytes();
        if (actions.AsSpan().SequenceEqual(_actions.Original) && map.AsSpan().SequenceEqual(_map.Original))
            return;

        NativeBindingWrites.Commit([new(SourcePath, _actions.Original, actions), new(MapPath, _map.Original, map)],
            EnsureClosed, createBackups: false);
        _actions.Accept(actions);
        _map.Accept(map);
    }

    private IEnumerable<ActionRow> ActionRows() => _actions.Lines.Select((line, index) => (Line: line, Index: index))
        .Select(row => (row.Line, row.Index, Match: StoredAction().Match(row.Line))).Where(row => row.Match.Success)
        .Select(row => new ActionRow(row.Index, row.Line, row.Match.Groups["action"].Value,
            row.Match.Groups["input"], row.Match.Groups["invert"]));

    private IEnumerable<RuntimeRow> RuntimeRows()
    {
        int start = _map.Lines.FindIndex(IsMainKeyset);
        int end = EndOfMainKeyset();
        for (int index = start + 1; index < end; index++)
        {
            Match row = GeneratedAction().Match(_map.Lines[index].TrimStart());
            if (row.Success)
                yield return new RuntimeRow(index, row.Groups["action"].Value, row.Groups["type"].Value, row.Groups["input"].Value);
        }
    }

    private int EndOfMainKeyset()
    {
        int start = _map.Lines.FindIndex(IsMainKeyset);
        int end = _map.Lines.FindIndex(start + 1, line => line.TrimStart().StartsWith("KEYSET(", StringComparison.Ordinal)
            || line.TrimStart().StartsWith("// KEYSET ACTIONS END", StringComparison.Ordinal));
        return end >= 0 ? end : throw new InvalidDataException("The generated IL-2 profile is missing the main keyset's end marker.");
    }

    private Recipe FindRecipe(GameAction action, bool axis)
    {
        var runtime = _sourceRuntime.Where(row => IsHandler(row.Action, action.Id)).ToArray();
        Recipe[] recipes;
        if (axis)
        {
            recipes = (from stored in _sourceActions
                       let input = JoystickToken().Match(stored.Input)
                       where stored.Action == action.Id && input.Groups["axis"].Success
                           && _runtimeDevices.ContainsKey(input.Groups["device"].Value)
                       let translated = _runtimeDevices[input.Groups["device"].Value] + "_" + input.Groups["gesture"].Value
                       from generated in runtime
                       where generated.Input.TrimStart('_').Equals(translated, StringComparison.OrdinalIgnoreCase)
                       select new Recipe(generated.Action, generated.Type, stored.Invert.Value, generated.Input.StartsWith('_')))
                .Distinct().Take(2).ToArray();
        }
        else
        {
            recipes = runtime.Where(row => row.Action == action.Id && !row.Input.Contains("_axis_", StringComparison.OrdinalIgnoreCase))
                .Select(row => new Recipe(row.Action, row.Type, "0", false)).Distinct().Take(2).ToArray();
        }
        if (recipes.Length != 1)
            throw new InvalidOperationException($"{action.Name} has no single verified {(axis ? "joystick axis" : "button")} handler in the main keyset. Assign it once in IL-2 and reload the files.");
        return recipes[0];
    }

    private static Dictionary<string, string> VerifyDeviceSlots(ActionRow[] actions, RuntimeRow[] runtime)
    {
        var known = actions.Select(row => row.Action).ToHashSet(StringComparer.Ordinal);
        var stored = actions.SelectMany(row => row.Input.Split('/').Select(token => (row.Action, Token: JoystickToken().Match(token))))
            .Where(row => row.Token.Success).ToLookup(row => (row.Action, Gesture: row.Token.Groups["gesture"].Value));
        var generated = runtime.Select(row =>
        {
            string action = row.Action;
            if (!known.Contains(action) && (action.EndsWith("_p", StringComparison.Ordinal) || action.EndsWith("_n", StringComparison.Ordinal)))
                action = action[..^2];
            return (Action: action, Token: JoystickToken().Match(row.Input.TrimStart('_')));
        }).Where(row => row.Token.Success).ToLookup(row => (row.Action, Gesture: row.Token.Groups["gesture"].Value));

        var supported = new List<(string Device, string Runtime, string Action)>();
        foreach (var group in stored)
        {
            string[] devices = group.Select(row => row.Token.Groups["device"].Value).Distinct().Take(2).ToArray();
            string[] mapped = generated[group.Key].Select(row => row.Token.Groups["device"].Value).Distinct().Take(2).ToArray();
            if (devices.Length == 1 && mapped.Length == 1)
                supported.Add((devices[0], mapped[0], group.Key.Action));
        }
        return supported.GroupBy(row => row.Device)
            .Where(group => group.Select(row => row.Runtime).Distinct().Count() == 1 &&
                group.Select(row => row.Action).Distinct().Count() >= 2)
            .Select(group => group.First()).GroupBy(row => row.Runtime).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Single().Device, group => group.Key, StringComparer.Ordinal);
    }

    private static bool IsMainKeyset(string line) => line.Trim() == "KEYSET(\"keyset_main\");";
    private static bool IsHandler(string handler, string action) =>
        handler == action || handler == action + "_p" || handler == action + "_n";
    public static string CompanionPath(string actionsPath)
    {
        string actions = Path.GetFullPath(actionsPath);
        string sibling = Path.ChangeExtension(actions, ".map");
        if (!Path.GetFileName(actions).Equals("global.actions", StringComparison.OrdinalIgnoreCase))
            return sibling;

        string current = Path.Combine(Path.GetDirectoryName(actions)!, "current.map");
        return File.Exists(current) || !File.Exists(sibling) ? current : sibling;
    }

    private static string ReplaceInput(ActionRow row, string input, string invert)
    {
        var padded = (" " + input).PadRight(row.InputField.Length);
        return row.Line[..row.InputField.Index] + padded
            + row.Line[(row.InputField.Index + row.InputField.Length)..row.Invert.Index]
            + invert + row.Line[(row.Invert.Index + row.Invert.Length)..];
    }

    private void EnsureClosed() => EnsureClosed(SourcePath);

    internal static void EnsureClosed(string path)
    {
        DirectoryInfo? directory = new FileInfo(path).Directory;
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "bin", "game", "Il-2.exe")))
            directory = directory.Parent;
        if (directory is null)
            return; // An exported copy cannot be active in the simulator.
        var clients = Process.GetProcessesByName("Il-2");
        foreach (var client in clients)
            client.Dispose();
        if (clients.Length > 0)
            throw new InvalidOperationException("Close IL-2 Great Battles before updating its live binding files.");
    }

    private sealed record ActionRow(int Index, string Line, string Action, Group InputField, Group Invert)
    {
        public string Input => InputField.Value.Trim();
    }
    private sealed record RuntimeRow(int Index, string Action, string Type, string Input);
    private sealed record Recipe(string Handler, string Type, string Invert, bool InvertedToken);

    private sealed class ProfileText(string path, byte[] original, bool bom, List<string> lines)
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        public string Path { get; } = path;
        public List<string> Lines { get; } = lines;
        public string Newline { get; } = lines.FirstOrDefault(line => line.EndsWith('\n'))?.EndsWith("\r\n", StringComparison.Ordinal) == true
            ? "\r\n" : "\n";
        public byte[] Original { get; private set; } = original;

        public static ProfileText Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            bool bom = bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble);
            string text = Utf8.GetString(bytes.AsSpan(bom ? Encoding.UTF8.Preamble.Length : 0));
            return new ProfileText(path, bytes, bom, SplitLines().Split(text).ToList());
        }
        public byte[] Bytes() => bom
            ? [.. Encoding.UTF8.Preamble, .. Utf8.GetBytes(string.Concat(Lines))]
            : Utf8.GetBytes(string.Concat(Lines));
        public void Accept(byte[] bytes) => Original = bytes;
    }

    [GeneratedRegex(@"(?<=\n)|(?<=\r)(?!\n)")]
    private static partial Regex SplitLines();

    [GeneratedRegex(@"^[ \t]*(?<action>[A-Za-z0-9_]+)[ \t]*,(?<input>[^,|\r\n]*),[ \t]*(?<invert>[01])[ \t]*\|")]
    private static partial Regex StoredAction();

    [GeneratedRegex("^action\\(\\\"(?<action>[A-Za-z0-9_]+)\\\",\\\"(?<type>State|Event)\\\",\\\"(?<input>[^\\\"]*)\\\"\\);")]
    private static partial Regex GeneratedAction();

    [GeneratedRegex(@"\A(?<device>joy\d+)_(?<gesture>b(?:[0-9]|[1-9][0-9]|1[01][0-9]|12[0-7])|(?<axis>axis_[a-z])|pov[0-3]_(?:0|90|180|270))\z", RegexOptions.IgnoreCase)]
    private static partial Regex JoystickToken();
}
