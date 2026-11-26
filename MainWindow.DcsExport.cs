using Microsoft.Win32;
using System.IO;
using System.Text;
using VisualControlsBinder.Games;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private string? ExportDcs(GameTarget game, IReadOnlyList<BindingEdit> edits)
    {
        if (_aircraft is not { } module)
            throw new InvalidOperationException("Choose a DCS aircraft or input module first.");
        var commands = DcsNativeCommands.Read(module);
        var profiles = new List<(string Slot, Guid Device, DcsDiffProfile Profile)>();
        var preview = new StringBuilder();
        foreach (var deviceEdits in edits.GroupBy(edit => edit.Address.Device))
        {
            var hardware = GetLayout(deviceEdits.Key);
            var device = ConnectedDevice(hardware) ?? throw new InvalidOperationException($"Connect {hardware.Name} before exporting.");
            var profile = OpenDcsProfileForWrite(module, hardware, device);
            var savedBindings = profile.Bindings;
            preview.AppendLine($"{module.Name} · {hardware.Name}")
                .AppendLine($"{(profile.Exists ? "Update" : "Create")} · {profile.Path}");
            bool changed = false;
            foreach (var edit in deviceEdits)
            {
                var signal = edit.RemoveInput is { } token
                    ? InputSignal.Parse(token) ?? throw new InvalidDataException("The DCS binding to remove has an invalid input.")
                    : RequireInput(hardware, edit.Address).Signal;
                string key = DcsJoystickInputs.Encode(signal)
                    ?? throw new InvalidOperationException($"{signal} cannot be represented as a DCS joystick input.");
                var oldHashes = edit.RemoveInput is not null ? savedBindings
                    .Where(binding => binding.ActionName == edit.Action.Name && binding.IsAxis == edit.Action.IsAxis &&
                        binding.Key == key && binding.Reformers.Count == 0 && binding.Change is "added" or "changed")
                    .Select(binding => binding.ActionId).Distinct().Take(2).ToArray() : [];
                string hash = oldHashes is [var saved] ? saved : commands.ResolveForProfile(edit.Action, savedBindings);
                bool applied = profile.Apply(hash, edit.Action, key, edit.RemoveInput is not null);
                changed |= applied;
                preview.AppendLine($"  {(edit.RemoveInput is null ? "Assign" : "Remove")} · {edit.Action.Name} · {signal}{(applied ? "" : " · already matches")}");
            }
            if (changed)
                profiles.Add((deviceEdits.Key, device.Id, profile));
            preview.AppendLine();
        }
        if (profiles.Count == 0)
            return "The DCS device profiles already match the plan.";

        preview.AppendLine("Existing profiles get a backup beside each file.");
        if (!ReviewWindow.Confirm(this, "DCS binding preview", preview.ToString(), "Write controller profiles"))
            return null;
        RequireLivePlanDevices(edits, game);
        foreach (var item in profiles)
        {
            var hardware = GetLayout(item.Slot);
            if (ConnectedDevice(hardware) is not { } device || device.Id != item.Device ||
                !item.Profile.Path.Equals(DcsProfilePathForWrite(module, hardware, device), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A DCS controller or binding file changed during preview. Open the preview again.");
        }
        var writes = profiles.Select(item => item.Profile.ToWrite()).ToArray();
        NativeBindingWrites.Commit(writes, writes.Any(write => IsDcsInputPath(module, write.Path))
            ? DcsDiffProfile.EnsureDcsClosed : null);
        return $"Wrote {writes.Length} DCS controller profile(s) for {module.Name}.";
    }

    private string? CreateDcsProfile()
    {
        if (_aircraft is not { } module || _hardware is not { } hardware)
            throw new InvalidOperationException("Choose a DCS aircraft and controller first.");
        var source = DcsProfileFor(module, hardware);
        if (source?.Path is { } path)
        {
            var copy = new SaveFileDialog
            {
                Title = $"Save a new DCS profile for {hardware.Name}",
                Filter = FlightProfileFilter(GameKind.Dcs), DefaultExt = ".diff.lua", OverwritePrompt = false,
                InitialDirectory = ExistingProfileDirectory([IsDcsInputPath(module, path) ? null : Path.GetDirectoryName(path)]),
                FileName = Path.GetFileName(path)[..^".diff.lua".Length] + " - copy.diff.lua"
            };
            if (copy.ShowDialog(this) != true)
                return null;
            string destination = copy.FileName;
            if (IsDcsInputPath(module, destination) || !destination.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Save the new .diff.lua profile outside the active DCS input directories.");
            NativeBindingWrites.Commit([DcsDiffProfile.Load(path).ToWrite(destination)]);
            _plan.SetDcsProfile(module.Id, hardware.Id, destination);
            return destination;
        }

        if (source?.Error is { } error)
            throw new InvalidOperationException(error);
        RefreshConnectedDevices();
        var device = ConnectedDevice(hardware) ?? throw new InvalidOperationException("Connect the controller before creating its active profile.");
        var profile = OpenDcsProfileForWrite(module, hardware, device);
        if (profile.Exists)
            throw new InvalidOperationException("The controller already has an active DCS profile. Reload to create a copy.");
        NativeBindingWrites.Commit([profile.ToWrite()], DcsDiffProfile.EnsureDcsClosed);
        return profile.Path;
    }

    private string DcsProfilePathForWrite(DcsModule module, HardwareDefinition hardware, JoystickDevice controller)
    {
        var selection = _plan.GetDcsProfile(module.Id, hardware.Id);
        if (selection is null)
            return DcsDeviceProfilePath(module, controller);
        if (!File.Exists(selection))
            throw new FileNotFoundException($"The selected DCS profile for {hardware.Name} is missing. Choose another file or use the active DCS profile.", selection);
        return selection;
    }

    private DcsDiffProfile OpenDcsProfileForWrite(DcsModule module, HardwareDefinition hardware, JoystickDevice controller)
    {
        string path = DcsProfilePathForWrite(module, hardware, controller);
        return _plan.GetDcsProfile(module.Id, hardware.Id) is null ? DcsDiffProfile.Open(path) : DcsDiffProfile.Load(path);
    }

    private static bool IsDcsInputPath(DcsModule module, string path)
    {
        string savedGames = Path.GetDirectoryName(module.SavedGamesDirectory) ?? module.SavedGamesDirectory;
        var relative = Path.GetRelativePath(savedGames, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return relative is [var variant, var config, var input, ..] &&
            (variant.Equals("DCS", StringComparison.OrdinalIgnoreCase) || variant.StartsWith("DCS.", StringComparison.OrdinalIgnoreCase)) &&
            config.Equals("Config", StringComparison.OrdinalIgnoreCase) && input.Equals("Input", StringComparison.OrdinalIgnoreCase);
    }

    private static string DcsDeviceProfilePath(DcsModule module, JoystickDevice device)
    {
        if (DcsGame.FindSavedJoystickProfile(module, device.Id) is { } existing)
            return existing;
        string directory = DcsGame.SavedJoystickDirectory(module);
        string? parent = Path.GetDirectoryName(Path.GetDirectoryName(directory));
        string suffix = device.Id.ToString("B").ToUpperInvariant() + ".diff.lua";
        var names = parent is not null && Directory.Exists(parent)
            ? Directory.EnumerateDirectories(parent).Select(path => Path.Combine(path, "joystick")).Where(Directory.Exists)
                .SelectMany(path => Directory.EnumerateFiles(path, "*.diff.lua")).Select(Path.GetFileName)
                .OfType<string>().Where(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray()
            : [];
        if (names.Length > 1)
            throw new InvalidOperationException($"DCS uses more than one filename for {device.Name}. Save a change to that controller in the selected aircraft first.");
        if (names is [var file])
            return Path.Combine(directory, file);
        string name = device.NativeName ?? device.Name;
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException($"Save a change to {device.Name} in DCS first so VCP can identify the filename.");
        return Path.Combine(directory, $"{name} {suffix}");
    }
}
