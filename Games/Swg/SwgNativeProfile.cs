using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using IffChunk = VisualControlsBinder.Games.SwgCatalog.IffChunk;

namespace VisualControlsBinder.Games;

public sealed record SwgNativeBinding(string Map, string Action, string Kind, int Index);
public sealed record SwgNativeChange(bool Changed, string? ReplacedActionId = null);

public sealed class SwgNativeProfile
{
    private readonly Keymap[] _keymaps;

    private SwgNativeProfile(Keymap[] keymaps) => _keymaps = keymaps;

    public IReadOnlyList<SwgNativeBinding> Bindings => _keymaps.SelectMany(keymap => keymap.ReadBindings()).ToArray();

    public static SwgNativeProfile Load(IEnumerable<string> sourceFiles)
    {
        var keymaps = sourceFiles.Select(Keymap.Read).ToArray();
        if (keymaps.Length == 0 || keymaps.GroupBy(keymap => keymap.Map).Any(group => group.Count() > 1))
            throw new InvalidDataException("Choose up to one Ground keymap and one Space keymap for the same SWG character.");

        return new SwgNativeProfile(keymaps);
    }

    public SwgNativeChange Apply(GameAction action, string kind, int index, bool remove = false)
    {
        if (action.Map != "Space")
            throw new InvalidOperationException($"{action.Name}: direct joystick edits are supported in the saved Space keymap. Use a keyboard route for Ground actions.");
        if (!SwgCatalog.IsCommand(action.Id) || (!remove && action.IsAxis != (kind == "axis")))
            throw new InvalidOperationException($"{action.Name}: choose a compatible SWG joystick input.");

        var slot = (kind, index) switch
        {
            ("button", >= 1 and <= 32) => (Tag: "JOYB", Index: index - 1),
            ("axis", >= 1 and <= 6) => (Tag: "JOYX", Index: index - 1),
            ("axis", 7 or 8) => (Tag: "JOYS", Index: index - 7),
            _ => throw new InvalidOperationException($"{action.Name}: a direct SWG assignment needs button 1–32 or an analog axis. Use a button-reported hat direction for individual POV directions.")
        };
        if (!remove && action.Id == "CMD_shipThrottleSetSlider" && slot.Tag != "JOYS")
            throw new InvalidOperationException("SWG's Throttle Slider action requires Slider 1 or Slider 2.");

        Keymap keymap = _keymaps.SingleOrDefault(item => item.Map == action.Map)
            ?? throw new InvalidOperationException("Load a saved SWG Space keymap first.");
        return keymap.Apply(slot.Tag, slot.Index, action.Id, remove);
    }

    internal IReadOnlyList<NativeBindingWrite> ToWrites(string? copyFolder = null)
    {
        if (copyFolder is null)
            return _keymaps.Where(keymap => keymap.Changed)
                .Select(keymap => new NativeBindingWrite(keymap.Path, keymap.Original, keymap.Bytes())).ToArray();

        string destination = Path.GetFullPath(copyFolder);
        return _keymaps.Select(keymap => new NativeBindingWrite(
            Path.Combine(destination, Path.GetFileName(keymap.Path)), null, keymap.Bytes())).ToArray();
    }

    internal void EnsureGameClosed()
    {
        foreach (var keymap in _keymaps.Where(keymap => keymap.Changed))
            EnsureClientClosed(keymap.Path);
    }

    /// <summary>Writes new copies of the loaded character keymaps using their game-recognized filenames.</summary>
    public void SaveAs(string directory)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"Choose an existing folder for the SWG keymaps: {directory}.");
        NativeBindingWrites.Commit(ToWrites(directory));
    }

    /// <summary>Replaces the Space keymap if its contents still match the loaded file.</summary>
    public void SaveChanges()
    {
        Keymap? changed = _keymaps.SingleOrDefault(keymap => keymap.Changed);
        if (changed is null)
            return;
        byte[] bytes = changed.Bytes();
        NativeBindingWrites.Commit([new NativeBindingWrite(changed.Path, changed.Original, bytes)],
            EnsureGameClosed, createBackups: false);
        changed.Accept(bytes);
    }

    private static void EnsureClientClosed(string keymapPath)
    {
        DirectoryInfo? directory = new FileInfo(keymapPath).Directory;
        while (directory is not null && !directory.Name.Equals("profiles", StringComparison.OrdinalIgnoreCase))
            directory = directory.Parent;
        if (directory?.Parent is not { } gameFolder)
            return;

        foreach (string name in new[] { "SwgClient_r", "SwgClient_tc", "SwgClient" })
        {
            Process[] clients = Process.GetProcessesByName(name);
            try
            {
                if (clients.Any(client => UsesKeymapFolder(client, gameFolder.FullName)))
                    throw new InvalidOperationException("Close this SWG client before updating its loaded keymap. You can still export a new copy.");
            }
            finally
            {
                foreach (Process client in clients)
                    client.Dispose();
            }
        }
    }

    private static bool UsesKeymapFolder(Process client, string gameFolder)
    {
        try
        {
            string? executable = client.MainModule?.FileName;
            return executable is null
                || executable.StartsWith(Path.TrimEndingDirectorySeparator(gameFolder) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidOperationException)
        {
            return false; // The process exited while its module was queried.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true; // Its installation could not be determined safely.
        }
    }

    private sealed class Keymap(string path, byte[] original, IffChunk root, IffChunk version)
    {
        public string Path { get; } = path;
        public string Map { get; } = SwgCatalog.IsSpaceProfile(path) ? "Space" : "Ground";
        public byte[] Original { get; private set; } = original;
        public bool Changed { get; private set; }

        public static Keymap Read(string path)
        {
            string fullPath = System.IO.Path.GetFullPath(path);
            byte[] original = File.ReadAllBytes(fullPath);
            var root = SwgCatalog.ReadChunks(original).SingleOrDefault();
            var version = root?.Children.SingleOrDefault(chunk => chunk.IsForm && chunk.Tag is "0006" or "0007");
            if (root is not { Tag: "IMAP", IsForm: true } || version is null)
                throw new InvalidDataException($"Unsupported SWG keymap format in {System.IO.Path.GetFileName(path)}.");
            return new Keymap(fullPath, original, root, version);
        }

        public IEnumerable<SwgNativeBinding> ReadBindings()
        {
            var joystick = JoystickMap(create: false);
            if (joystick is null)
                yield break;
            foreach (var group in joystick.Children.Where(group => group.IsForm && group.Tag is "JOYB" or "JOYX" or "JOYS"))
            {
                foreach (var entry in group.Children.Where(IsInput))
                {
                    if (SwgCatalog.ReadJoystickInput(group.Tag, NativeIndex(entry)) is { } slot)
                        yield return new SwgNativeBinding(Map, entry.Text(4), slot.Kind, slot.Index);
                }
            }
        }

        public SwgNativeChange Apply(string tag, int index, string command, bool remove)
        {
            var joystick = JoystickMap(create: !remove);
            var group = joystick?.Children.SingleOrDefault(chunk => chunk.IsForm && chunk.Tag == tag);
            var old = group?.Children.SingleOrDefault(chunk => IsInput(chunk) && NativeIndex(chunk) == index);
            string? oldCommand = old?.Text(offset: 4);
            if (remove)
            {
                if (old is null || oldCommand != command)
                    return new SwgNativeChange(false);
                group!.Children.Remove(old);
            }
            else
            {
                if (oldCommand == command)
                    return new SwgNativeChange(false);
                if (joystick is null)
                    throw new InvalidDataException("The saved Space keymap does not contain an unmodified input map.");
                if (group is null)
                {
                    group = IffChunk.Form(tag, []);
                    joystick.Children.Add(group);
                }

                byte[] payload = new byte[4 + Encoding.ASCII.GetByteCount(command) + 1];
                BinaryPrimitives.WriteInt32LittleEndian(payload, index);
                Encoding.ASCII.GetBytes(command, payload.AsSpan(4));
                var entry = IffChunk.Data("DATA", payload);
                if (old is null)
                    group.Children.Add(entry);
                else
                    group.Children[group.Children.IndexOf(old)] = entry;
            }

            Changed = true;
            return new SwgNativeChange(true, remove ? null : oldCommand);
        }

        public byte[] Bytes()
        {
            if (!Changed)
                return Original;
            using var output = new MemoryStream();
            WriteChunk(root, output);
            return output.ToArray();
        }

        public void Accept(byte[] bytes)
        {
            Original = bytes;
            Changed = false;
        }

        private IffChunk? JoystickMap(bool create)
        {
            if (Map != "Space")
                return null;
            var map = version.Children.FirstOrDefault(chunk => chunk.IsForm && chunk.Tag == "MAP " && IsZeroInfo(chunk));
            if (map is null || version.Tag == "0006")
                return map;

            var joystick = map.Children.SingleOrDefault(chunk => chunk.IsForm && chunk.Tag == "JYDV" && IsZeroInfo(chunk));
            if (joystick is null && create)
            {
                joystick = IffChunk.Form("JYDV",
                    [IffChunk.Data("INFO", new byte[4]), IffChunk.Form("JOYB", []), IffChunk.Form("JOYX", []),
                     IffChunk.Form("JOYS", []), IffChunk.Form("POV ", [])]);
                map.Children.Add(joystick);
            }
            return joystick;
        }
    }

    private static bool IsInput(IffChunk chunk) => !chunk.IsForm && chunk.Tag == "DATA"
        && chunk.Payload.Length > 4 && SwgCatalog.IsCommand(chunk.Text(4));

    private static int NativeIndex(IffChunk chunk) => BinaryPrimitives.ReadInt32LittleEndian(chunk.Payload.Span);

    private static bool IsZeroInfo(IffChunk chunk) => chunk.Children.Any(info =>
        !info.IsForm && info.Tag == "INFO" && info.Payload.Length >= 4 && NativeIndex(info) == 0);

    private static void WriteChunk(IffChunk chunk, Stream output)
    {
        Span<byte> header = stackalloc byte[8];
        Encoding.Latin1.GetBytes(chunk.IsForm ? "FORM" : chunk.Tag, header[..4]);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], PayloadLength(chunk));
        output.Write(header);
        if (!chunk.IsForm)
        {
            output.Write(chunk.Payload.Span);
            return;
        }
        Encoding.Latin1.GetBytes(chunk.Tag, header[..4]);
        output.Write(header[..4]);
        foreach (var child in chunk.Children)
            WriteChunk(child, output);
    }

    private static int PayloadLength(IffChunk chunk) =>
        chunk.IsForm ? checked(4 + chunk.Children.Sum(child => 8 + PayloadLength(child))) : chunk.Payload.Length;
}
