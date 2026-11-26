using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VisualControlsBinder.Games;

public sealed record SwgTarget(GremlinOutput? Output, SwgKey? Keyboard);

public sealed partial class SwgCatalog
{
    private readonly Dictionary<(string Map, string Id), SavedInput[]> savedInputs;

    private SwgCatalog(string[] files, string[] profileRoots)
    {
        SourceFiles = files;
        var actions = new List<GameAction>();
        savedInputs = new();
        foreach (string file in files)
        {
            string map = IsSpaceProfile(file) ? "Space" : "Ground";
            var (definition, knownCommands, inputs) = ReadMap(file);
            foreach (var group in inputs.GroupBy(input => input.Command))
            {
                savedInputs.Add((map, group.Key), group.ToArray());
            }

            // Some clients omit KNWN. Read missing command names from another copy of the same
            // character's keymap and keep the bindings from the selected file.
            var alternate = knownCommands.Count == 0 && definition is not null
                ? AlternateCommands(file, definition, profileRoots)
                : Array.Empty<string>();
            foreach (string id in knownCommands.Concat(inputs.Select(input => input.Command)).Concat(alternate).Distinct())
            {
                actions.Add(new GameAction(map, id, map, "", DisplayName(id),
                    AxisCommands.Contains(id)
                    || (savedInputs.TryGetValue((map, id), out SavedInput[]? bindings)
                        && bindings.Any(input => input.Type is "JOYX" or "JOYS"))));
            }
        }

        Actions = actions.OrderBy(action => action.Category).ThenBy(action => action.Name).ToArray();
    }

    public IReadOnlyList<string> SourceFiles { get; }
    public IReadOnlyList<GameAction> Actions { get; }

    internal static string[] ProfileFolders(string gameDirectory) =>
        [Path.Combine(gameDirectory, "profiles"), Path.Combine(gameDirectory, "x64", "profiles")];

    public static SwgCatalog Load(string gameDirectory, string? characterProfile = null)
    {
        string[] profileRoots = ProfileFolders(gameDirectory);
        if (characterProfile is null)
        {
            string[] files = profileRoots
                .Where(Directory.Exists)
                .SelectMany(folder => Directory.EnumerateFiles(folder, "*.inp", SearchOption.AllDirectories))
                .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
            characterProfile = files.FirstOrDefault(IsSpaceProfile) ?? files.FirstOrDefault();
        }
        if (characterProfile is null || !File.Exists(characterProfile))
        {
            throw new FileNotFoundException("No saved SWG keymaps were found. Choose a game folder with a saved character profile.");
        }

        string stem = Path.GetFileNameWithoutExtension(characterProfile);
        string directory = Path.GetDirectoryName(Path.GetFullPath(characterProfile))!;
        if (IsSpaceProfile(characterProfile))
        {
            stem = stem[..^"_space".Length];
        }
        string[] sources = new[] { Path.Combine(directory, stem + ".inp"), Path.Combine(directory, stem + "_space.inp") }
            .Where(File.Exists).ToArray();
        return new SwgCatalog(sources, profileRoots);
    }

    private static IReadOnlyList<string> AlternateCommands(string source, string definition, string[] profileRoots)
    {
        string? relative = profileRoots.Select(root => Path.GetRelativePath(root, source))
            .FirstOrDefault(path => !Path.IsPathRooted(path) && path != ".." &&
                !path.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        if (relative is null)
            return [];

        var commands = new List<string>();
        foreach (string root in profileRoots)
        {
            string alternate = Path.GetFullPath(Path.Combine(root, relative));
            if (alternate.Equals(source, StringComparison.OrdinalIgnoreCase) || !File.Exists(alternate))
                continue;
            try
            {
                var (alternateDefinition, known, inputs) = ReadMap(alternate);
                if (string.Equals(alternateDefinition, definition, StringComparison.OrdinalIgnoreCase))
                {
                    commands.AddRange(known);
                    commands.AddRange(inputs.Select(input => input.Command));
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
            {
                // An unreadable optional copy must not prevent opening a valid selected keymap.
            }
        }
        return commands;
    }

    /// <summary>Finds a vJoy route or another usable key already bound to the action in the saved character map.</summary>
    public SwgTarget PickTarget(GameAction action, int vJoyDeviceId = 1)
    {
        if (!savedInputs.TryGetValue((action.Map, action.Id), out SavedInput[]? inputs))
        {
            return new SwgTarget(null, null);
        }

        SavedInput[] unmodified = inputs.Where(input => input.Modifiers == 0).ToArray();
        GremlinOutput? output = unmodified.Select(input => ReadVJoyOutput(input, vJoyDeviceId))
            .FirstOrDefault(item => item is not null && (item.Kind == "axis") == action.IsAxis);
        SwgKey? key = action.IsAxis ? null : unmodified.Where(input => input.Type == "KEYS")
            .Select(input => DecodeKey(input.Index)).OfType<SwgKey>().FirstOrDefault();

        return new SwgTarget(output, key);
    }

    public IReadOnlyList<GremlinOutput> ReservedOutputs(string map, int vJoyDeviceId = 1) =>
        savedInputs.Where(entry => entry.Key.Map == map).SelectMany(entry => entry.Value)
            .Select(input => ReadVJoyOutput(input, vJoyDeviceId)).OfType<GremlinOutput>().Distinct().ToArray();

    /// <summary>Returns actions routed to this output in the selected character's unmodified input maps.</summary>
    public IReadOnlyList<GameAction> GetActionsForTarget(SwgTarget target, int vJoyDeviceId = 1) =>
        Actions.Where(action => savedInputs.TryGetValue((action.Map, action.Id), out SavedInput[]? inputs)
            && inputs.Any(input => input.Modifiers == 0 && (
                (target.Output is { } output && output.DeviceId == vJoyDeviceId && ReadVJoyOutput(input, vJoyDeviceId) == output)
                || (target.Keyboard is { } key && input.Type == "KEYS" && DecodeKey(input.Index) == key))))
            .ToArray();

    private static GremlinOutput? ReadVJoyOutput(SavedInput input, int vJoyDeviceId) =>
        ReadJoystickInput(input.Type, input.Index) is { } slot
            ? new GremlinOutput(vJoyDeviceId, slot.Kind, slot.Index)
            : null;

    internal static (string Kind, int Index)? ReadJoystickInput(string type, int index) => type switch
    {
        "JOYB" when index is >= 0 and < 32 => ("button", index + 1),
        "JOYX" when index is >= 0 and < 6 => ("axis", index + 1),
        "JOYS" when index is 0 or 1 => ("axis", index + 7),
        _ => null
    };

    private static (string? Definition, IReadOnlyList<string> KnownCommands, IReadOnlyList<SavedInput> SavedInputs) ReadMap(string file)
    {
        byte[] data = File.ReadAllBytes(file);
        IffChunk? root = ReadChunks(data).SingleOrDefault();
        IffChunk? version = root?.Children.SingleOrDefault(child => child.Tag is "0006" or "0007");
        if (root?.Tag != "IMAP" || version is null)
        {
            throw new InvalidDataException($"Unsupported SWG keymap format in {Path.GetFileName(file)}.");
        }

        var commands = version.Children.FirstOrDefault(child => child.Tag == "CMDS")?.Children;
        string? definition = commands?.FirstOrDefault(child => child.Tag == "NAME")?.Text();
        string[] knownCommands = (commands?.FirstOrDefault(child => child.Tag == "KNWN")?.Text() ?? "")
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsCommand).ToArray();

        var saved = new List<SavedInput>();
        foreach (IffChunk map in version.Children.Where(child => child.Tag == "MAP "))
        {
            IffChunk? info = map.Children.FirstOrDefault(child => child.Tag == "INFO");
            if (info is null || info.Payload.Length < 4)
            {
                continue;
            }

            int modifiers = BinaryPrimitives.ReadInt32LittleEndian(info.Payload.Span);
            AddSavedInputs(map, modifiers, saved);
            foreach (IffChunk joystick in map.Children.Where(child => child.Tag == "JYDV"))
            {
                IffChunk? device = joystick.Children.FirstOrDefault(child => child.Tag == "INFO");
                if (device is { Payload.Length: >= 4 }
                    && BinaryPrimitives.ReadInt32LittleEndian(device.Payload.Span) == 0)
                {
                    AddSavedInputs(joystick, modifiers, saved);
                }
            }
        }
        return (string.IsNullOrWhiteSpace(definition) ? null : definition, knownCommands, saved);
    }

    private static void AddSavedInputs(IffChunk map, int modifiers, List<SavedInput> saved)
    {
        foreach (IffChunk device in map.Children.Where(child => InputGroups.Contains(child.Tag)))
        {
            foreach (IffChunk entry in device.Children.Where(child => child.Tag == "DATA" && child.Payload.Length > 4))
            {
                string command = entry.Text(offset: 4);
                if (IsCommand(command))
                {
                    saved.Add(new SavedInput(command, device.Tag,
                        BinaryPrimitives.ReadInt32LittleEndian(entry.Payload.Span), modifiers));
                }
            }
        }
    }

    internal static List<IffChunk> ReadChunks(ReadOnlyMemory<byte> data, int depth = 0)
    {
        if (depth > 16)
        {
            throw new InvalidDataException("The SWG keymap contains invalid nested chunks.");
        }

        var chunks = new List<IffChunk>();
        int offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 8)
            {
                throw new InvalidDataException("The SWG keymap contains an incomplete chunk header.");
            }
            string tag = Encoding.Latin1.GetString(data.Span.Slice(offset, 4));
            int length = BinaryPrimitives.ReadInt32BigEndian(data.Span.Slice(offset + 4, 4));
            offset += 8;
            if (length < 0 || length > data.Length - offset || (tag == "FORM" && length < 4))
            {
                throw new InvalidDataException("The SWG keymap contains an invalid chunk length.");
            }

            ReadOnlyMemory<byte> payload = data.Slice(offset, length);
            chunks.Add(tag == "FORM"
                ? IffChunk.Form(Encoding.Latin1.GetString(payload.Span[..4]), ReadChunks(payload[4..], depth + 1))
                : IffChunk.Data(tag, payload));
            offset += length;
        }
        return chunks;
    }

    internal static bool IsSpaceProfile(string path) => path.EndsWith("_space.inp", StringComparison.OrdinalIgnoreCase);

    internal static bool IsCommand(string text) => text.StartsWith("CMD_", StringComparison.Ordinal)
        && text.Length > 4 && text.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static SwgKey? DecodeKey(int index)
    {
        if (index is > 0 and < 128)
        {
            return new SwgKey(index);
        }
        // DirectInput represents common E0-prefixed PC keys as the scan code with bit 7 set.
        return index is 0x9C or 0x9D or 0xB5 or 0xB8 or 0xC7 or 0xC8 or 0xC9 or 0xCB or 0xCD
            or 0xCF or 0xD0 or 0xD1 or 0xD2 or 0xD3
            ? new SwgKey(index & 0x7F, IsExtended: true) : null;
    }

    private static string DisplayName(string id)
    {
        if (id == "CMD_toggleGimbal")
            return "Toggle Gimbal Correction";
        string name = id[4..];
        if (name.StartsWith("ui", StringComparison.Ordinal) && name.Length > 2 && char.IsUpper(name[2]))
        {
            name = name[2..];
        }
        name = NameBoundary().Replace(name.Replace('_', ' '), " ");
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    [GeneratedRegex("(?<=[a-z])(?=[A-Z0-9])|(?<=[A-Z])(?=[A-Z][a-z])")]
    private static partial Regex NameBoundary();

    private static readonly HashSet<string> InputGroups = new(StringComparer.Ordinal)
        { "KEYS", "MOSB", "JOYB", "JOYX", "JOYS", "POV " };

    private static readonly HashSet<string> AxisCommands = new(StringComparer.Ordinal)
    {
        "CMD_pitch", "CMD_roll", "CMD_yaw", "CMD_shipThrottleSetAxis", "CMD_shipThrottleSetSlider",
        "CMD_shipThrottleDeltaAxis", "CMD_shipThrottleDeltaSlider"
    };

    private sealed record SavedInput(string Command, string Type, int Index, int Modifiers);
    internal sealed record IffChunk(string Tag, ReadOnlyMemory<byte> Payload, List<IffChunk> Children, bool IsForm)
    {
        public static IffChunk Form(string tag, List<IffChunk> children) => new(tag, default, children, true);
        public static IffChunk Data(string tag, ReadOnlyMemory<byte> payload) => new(tag, payload, [], false);
        public string Text(int offset = 0) => Encoding.UTF8.GetString(Payload.Span[offset..]).TrimEnd('\0');
    }
}
