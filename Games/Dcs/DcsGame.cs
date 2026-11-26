using System.IO;
using System.Runtime.InteropServices;

namespace VisualControlsBinder.Games;

public sealed record DcsModule(
    string Id, string Name, string InstallDirectory, string SavedGamesDirectory,
    string ModuleDirectory, string InputDirectory, bool IsUserMod);

public sealed record DcsSavedBinding(string ActionId, string ActionName, string Key,
    bool IsAxis, string Change, IReadOnlyList<string> Reformers);

public static class DcsGame
{
    // Visible system control layers with joystick definitions in DCS's ProfileDatabase.
    private static readonly (string Id, string Folder)[] SystemInputs =
    [
        ("Default", "Aircrafts/Default"), ("UiLayer", "UiLayer"),
        ("CommandMenu", "CommandMenu"), ("VoiceChat", "VoiceChat")
    ];

    public static string DefaultInstallDirectory { get; } = FindDefaultInstall();
    public static string DefaultSavedGamesDirectory { get; } = Path.Combine(WindowsSavedGames(), "DCS");

    private static string WindowsSavedGames()
    {
        var id = new Guid("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAAA4");
        if (OperatingSystem.IsWindows() && SHGetKnownFolderPath(in id, 0, IntPtr.Zero, out var path) == 0)
        {
            try
            {
                if (Marshal.PtrToStringUni(path) is { Length: > 0 } folder)
                    return folder;
            }
            finally { Marshal.FreeCoTaskMem(path); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Saved Games");
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(in Guid folder, uint flags, IntPtr token, out IntPtr path);

    private static string FindDefaultInstall()
    {
        if (DcsInstall.FindStandalone() is { } registered)
            return registered;
        string standalone = GameInstallations.StandaloneGame(Path.Combine("Eagle Dynamics", "DCS World"), DcsInstall.IsInstallation);
        return DcsInstall.IsInstallation(standalone) ? standalone : GameInstallations.SteamGame("DCSWorld", DcsInstall.IsInstallation,
            () => standalone);
    }

    public static IReadOnlyList<DcsModule> DiscoverModules(string installDirectory, string? savedGamesDirectory = null)
    {
        var install = Path.GetFullPath(installDirectory);
        var saved = Path.GetFullPath(savedGamesDirectory ?? SavedGamesDirectory(install));
        var modules = new Dictionary<string, DcsModule>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, relative) in SystemInputs)
        {
            string input = Path.GetFullPath(Path.Combine(install, "Config", "Input", relative));
            if (HasJoystick(input))
                modules[id] = new DcsModule(id, ReadProfileName(input) ?? id, install, saved, input, input, false);
        }
        AddModules(install, false);
        AddModules(saved, true);
        return modules.Values.OrderBy(module => SystemInputs.Any(input => string.Equals(input.Id, module.Id, StringComparison.OrdinalIgnoreCase)))
            .ThenBy(module => module.Name, StringComparer.OrdinalIgnoreCase).ToArray();

        void AddModules(string root, bool userMod)
        {
            foreach (var kind in new[] { "aircraft", "tech" })
            {
                var folder = Path.Combine(root, "Mods", kind);
                if (!Directory.Exists(folder))
                    continue;
                foreach (var directory in Directory.EnumerateDirectories(folder))
                    foreach (var (id, input) in FindInputs(directory))
                        modules[id] = new DcsModule(id, ReadProfileName(input) ?? id, install, saved, directory, input, userMod);
            }
        }
    }

    public static string? FindSavedJoystickProfile(DcsModule module, Guid instanceId)
    {
        if (instanceId == Guid.Empty)
            return null;
        var matches = SavedJoystickProfiles(module).Where(path =>
        {
            const string suffix = ".diff.lua";
            var filename = Path.GetFileName(path);
            int end = filename.Length - suffix.Length;
            return end >= 38 && filename.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParseExact(filename.AsSpan(end - 38, 38), "B", out var fileId) && fileId == instanceId;
        }).Take(2).ToArray();
        if (matches.Length > 1)
            throw new InvalidDataException("More than one saved DCS file matches this controller and aircraft.");
        return matches.SingleOrDefault();
    }

    public static IReadOnlyList<string> SavedJoystickProfiles(DcsModule module)
    {
        if (!IsFolderName(module.Id))
            return [];
        var folder = SavedJoystickDirectory(module);
        return Directory.Exists(folder)
            ? Directory.GetFiles(folder, "*.diff.lua").Order(StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
    }

    public static string SavedJoystickDirectory(DcsModule module) => IsFolderName(module.Id)
        ? Path.Combine(module.SavedGamesDirectory, "Config", "Input", module.Id, "joystick")
        : throw new ArgumentException("Invalid DCS input profile name.", nameof(module));

    private static string SavedGamesDirectory(string installDirectory)
    {
        var folder = Path.GetDirectoryName(DefaultSavedGamesDirectory)!;
        var variantFile = Path.Combine(installDirectory, "dcs_variant.txt");
        if (File.Exists(variantFile))
        {
            var variant = File.ReadAllText(variantFile).Trim();
            if (IsFolderName(variant))
                return Path.Combine(folder, $"DCS.{variant}");
        }
        var legacy = Path.Combine(folder, "DCS.openbeta");
        return !Directory.Exists(DefaultSavedGamesDirectory) && Directory.Exists(legacy) ? legacy : DefaultSavedGamesDirectory;
    }

    // DCS saves differences against the device defaults, including explicit removals.
    public static IReadOnlyList<DcsSavedBinding> LoadSavedBindings(string path) => LoadSavedBindings(DcsLua.Read(path));

    internal static IReadOnlyList<DcsSavedBinding> LoadSavedBindings(DcsLua lua)
    {
        var bindings = new List<DcsSavedBinding>();
        for (int i = 0; i < lua.Count - 2; i++)
        {
            if (!lua[i].Is("diff") || !lua[i + 1].Is("=") || !lua[i + 2].Is("{"))
                continue;
            foreach (var (diffs, actions) in lua.Fields(i + 2))
            {
                if (diffs is not ("axisDiffs" or "keyDiffs") || !lua[actions.Start].Is("{"))
                    continue;
                foreach (var (actionId, action) in lua.Fields(actions.Start))
                {
                    if (!lua[action.Start].Is("{"))
                        continue;
                    var fields = lua.Fields(action.Start).ToArray();
                    var name = lua.Label(fields.FirstOrDefault(field => field.Key == "name").Value) ?? actionId;
                    foreach (var (change, inputs) in fields)
                    {
                        if (change is not ("added" or "changed" or "removed") || !lua[inputs.Start].Is("{"))
                            continue;
                        foreach (var part in lua.Parts(inputs.Start))
                        {
                            var input = lua.Field(part)?.Value ?? part;
                            if (!lua[input.Start].Is("{"))
                                continue;
                            var inputFields = lua.Fields(input.Start).ToArray();
                            var key = lua.Label(inputFields.FirstOrDefault(field => field.Key == "key").Value);
                            if (key is null)
                                continue;
                            var reformers = inputFields.FirstOrDefault(field => field.Key == "reformers").Value;
                            string[] modifiers = reformers.Length > 0 && lua[reformers.Start].Is("{")
                                ? lua.Labels(reformers).Concat(lua.Fields(reformers.Start)
                                    .Select(field => lua.Label(field.Value)).OfType<string>()).ToArray()
                                : [];
                            bindings.Add(new DcsSavedBinding(actionId, name, key, diffs == "axisDiffs", change,
                                modifiers));
                        }
                    }
                }
            }
        }
        return bindings;
    }

    public static IReadOnlyList<GameAction> LoadActions(DcsModule module) => ReadActions(module)
        .Select(entries => entries[^1].Action).OrderBy(action => action.Category, StringComparer.OrdinalIgnoreCase)
        .ThenBy(action => action.Section, StringComparer.OrdinalIgnoreCase)
        .ThenBy(action => action.Name, StringComparer.OrdinalIgnoreCase).ToArray();

    internal static IEnumerable<(GameAction Action, string Id)> ReadSourceCommandIds(DcsModule module)
    {
        var hashes = new DcsSourceCommands(module.InstallDirectory);
        foreach (var entries in ReadActions(module, hashes))
            if (entries.Select(entry => entry.NativeId).Distinct().Take(2).ToArray() is [string id])
                yield return (entries[^1].Action, id);
    }

    private sealed record InputAction(GameAction Action, string[] Features, string? NativeId);

    private static IEnumerable<List<InputAction>> ReadActions(DcsModule module, DcsSourceCommands? hashes = null)
    {
        var source = Path.Combine(module.InputDirectory, "joystick", "default.lua");
        var actions = new Dictionary<(bool Axis, string Name), List<InputAction>>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Visit(source);
        return actions.Values.Where(entries => entries.Count > 0);

        void Visit(string path)
        {
            if (!visited.Add(path))
                return;
            var lua = DcsLua.Read(path);
            var variables = ResolveVariables(lua, path, module.ModuleDirectory);
            var labels = lua.LocalLabels();
            foreach (var (_, args) in lua.Calls("external_profile"))
                if (args.Length > 0 && lua.PathExpression(args[0], variables) is { } reference &&
                    FindReference(module, path, reference) is { } included)
                    Visit(included);

            foreach (var (_, args) in lua.Calls("ignore_features"))
            {
                if (args.Length <= 1)
                    continue;
                var commands = lua[args[0].End - 1];
                if (!commands.Is("keyCommands") && !commands.Is("axisCommands"))
                    continue;
                var ignored = lua.Labels(args[1], labels).ToHashSet(StringComparer.Ordinal);
                foreach (var (key, entries) in actions)
                    if (key.Axis == commands.Is("axisCommands"))
                        entries.RemoveAll(entry => entry.Features.Any(ignored.Contains));
            }

            var inputTables = lua.Calls("applyInputs").Where(call => call.Args.Length > 1 &&
                    lua[call.Args[1].Start].Is("true") && call.Args[0].Length == 1)
                .Select(call => lua[call.Args[0].Start].Text).ToHashSet(StringComparer.Ordinal);
            if (inputTables.Count > 0)
                foreach (var (_, args) in lua.Calls("dofile"))
                    if (args.Length > 0 && lua.PathExpression(args[0], variables) is { } reference &&
                        Path.GetFileName(reference).Equals("inputTables.lua", StringComparison.OrdinalIgnoreCase) &&
                        FindReference(module, path, reference) is { } definitions)
                        foreach (var (name, categories) in DcsGeneratedInputs.Read(definitions, inputTables))
                            Add(name, categories, axis: false);

            foreach (int opening in lua.EnabledTables())
            {
                var fields = lua.Fields(opening).GroupBy(field => field.Key)
                    .ToDictionary(group => group.Key, group => group.Last().Value);
                var name = fields.TryGetValue("name", out var label) ? lua.Label(label, labels) : null;
                bool axis = fields.ContainsKey("action");
                if (string.IsNullOrWhiteSpace(name) || (!axis && !fields.ContainsKey("down") &&
                    !fields.ContainsKey("up") && !fields.ContainsKey("pressed")))
                    continue;
                Add(name, fields.TryGetValue("category", out var category) ? lua.Labels(category, labels) : [], axis,
                    fields.TryGetValue("features", out var features) ? lua.Labels(features, labels) : [],
                    hashes?.Hash(lua, fields, axis));
            }
        }

        void Add(string name, string[] categories, bool axis, string[]? features = null, string? nativeId = null)
        {
            string category = categories.FirstOrDefault() ?? (axis ? "Axis Commands" : "");
            string section = string.Join(" / ", categories.Skip(1));
            string id = $"{(axis ? "axis" : "button")}:{name}";
            if (!actions.TryGetValue((axis, name), out var entries))
                actions[(axis, name)] = entries = [];
            entries.Add(new InputAction(new GameAction(module.Id, id, category, section, name, axis), features ?? [], nativeId));
        }
    }

    private static IEnumerable<(string Id, string Input)> FindInputs(string module)
    {
        string entry = Path.Combine(module, "entry.lua");
        if (File.Exists(entry))
        {
            var lua = DcsLua.Read(entry);
            var variables = ResolveVariables(lua, entry, module);
            var registered = new List<(string, string)>();
            for (int i = 0; i < lua.Count - 2; i++)
            {
                if (!lua[i].Is("InputProfiles") || !lua[i + 1].Is("=") || !lua[i + 2].Is("{"))
                    continue;
                foreach (var (id, expression) in lua.Fields(i + 2))
                {
                    if (!IsFolderName(id) || lua.PathExpression(expression, variables) is not { } reference)
                        continue;
                    var folder = Path.GetFullPath(reference, module);
                    if (IsWithin(folder, module) && HasJoystick(folder))
                        registered.Add((id, folder));
                }
            }
            if (registered.Count > 0)
                return registered;
        }

        string input = Path.Combine(module, "Input");
        if (HasJoystick(input))
            return [(Path.GetFileName(module), input)];
        return Directory.Exists(input)
            ? Directory.EnumerateDirectories(input).Where(HasJoystick).Select(folder => (Path.GetFileName(folder), folder))
            : [];
    }

    private static bool HasJoystick(string path) => File.Exists(Path.Combine(path, "joystick", "default.lua"));

    private static bool IsFolderName(string name) => !string.IsNullOrWhiteSpace(name) && name is not ("." or "..") &&
        name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static string? ReadProfileName(string folder)
    {
        string namePath = Path.Combine(folder, "name.lua");
        if (!File.Exists(namePath))
            return null;
        var lua = DcsLua.Read(namePath);
        for (int i = 0; i < lua.Count - 1; i++)
            if (lua[i].Is("return"))
            {
                int start = i + 1;
                int end = start;
                while (end < lua.Count && !lua[end].Is(","))
                    end = Math.Max(end, lua.Match(end)) + 1;
                return lua.Label(new DcsLua.Slice(start, end));
            }
        return null;
    }

    private static Dictionary<string, string> ResolveVariables(DcsLua lua, string file, string module)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["folder"] = Path.GetDirectoryName(file)! + Path.DirectorySeparatorChar,
            ["current_mod_path"] = module
        };
        for (int i = 0; i < lua.Count - 2; i++)
        {
            var name = lua[i];
            if (name.Literal || !(char.IsAsciiLetter(name.Text[0]) || name.Text[0] == '_') || !lua[i + 1].Is("="))
                continue;
            int end = i + 3;
            while (end < lua.Count - 1 && lua[end].Is(".."))
                end += 2;
            if (lua.PathExpression(new DcsLua.Slice(i + 2, end), variables) is { } value)
                variables[name.Text] = value;
        }
        return variables;
    }

    private static string? FindReference(DcsModule module, string source, string reference)
    {
        if (!string.Equals(Path.GetExtension(reference), ".lua", StringComparison.OrdinalIgnoreCase))
            return null;
        var candidates = Path.IsPathFullyQualified(reference) ? [reference] : new[]
        {
            Path.Combine(Path.GetDirectoryName(source)!, reference),
            Path.Combine(module.InstallDirectory, reference)
        };
        var shared = Path.Combine(module.InstallDirectory, "Config", "Input");
        return candidates.Select(Path.GetFullPath)
            .FirstOrDefault(path => (IsWithin(path, module.ModuleDirectory) || IsWithin(path, shared)) && File.Exists(path));
    }

    private static bool IsWithin(string path, string folder)
    {
        string prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            path.Equals(folder, StringComparison.OrdinalIgnoreCase);
    }
}
