using System.IO;
using System.Text;
using Microsoft.Win32;
using VisualControlsBinder.Games;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private readonly Dictionary<(string Profile, Guid Device), string> _cliffsSlots = [];
    private readonly Dictionary<Guid, string?> _cliffsDeviceNames = [];

    private string CliffsProfilePath(GameTarget game) =>
        _plan.SourceProfiles.GetValueOrDefault(game.Id) ?? CliffsOfDoverGame.DefaultProfilePath;

    private string? CliffsDeviceName(CliffsBindingProfile profile, JoystickDevice device)
    {
        if (!_cliffsDeviceNames.TryGetValue(device.Id, out var slot))
            _cliffsDeviceNames[device.Id] = slot = profile.FindDevice(device);
        return slot;
    }

    private string? GetCliffsSlot(CliffsBindingProfile profile, JoystickDevice device)
    {
        var slots = profile.DeviceSlots;
        if (_cliffsSlots.TryGetValue((profile.Path, device.Id), out var known) &&
            slots.Contains(known, StringComparer.OrdinalIgnoreCase))
            return known;
        var stable = CliffsDeviceName(profile, device);
        return stable is not null && (slots.Contains(stable, StringComparer.OrdinalIgnoreCase) || !slots.Any(IsLegacyCliffsSlot))
            ? stable : null;
    }

    private static bool IsLegacyCliffsSlot(string slot) =>
        slot.StartsWith("Joystick", StringComparison.OrdinalIgnoreCase) && slot[8..].All(char.IsAsciiDigit);

    private string? ExportCliffs(GameTarget game, IReadOnlyList<BindingEdit> edits)
    {
        string target = CliffsProfilePath(game);
        string source = File.Exists(target) ? target : Path.Combine(GameFolder(game), "confUser.ini");
        if (!File.Exists(source))
            throw new InvalidOperationException("Choose a Cliffs of Dover controls INI or save the controls once in the game.");
        var profile = CliffsBindingProfile.Load(source);
        var preview = new StringBuilder();
        var slots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var devices = new Dictionary<string, (Guid Id, string Slot)>();
        foreach (var group in edits.GroupBy(edit => edit.Address.Device))
        {
            var hardware = GetLayout(group.Key);
            var device = ConnectedDevice(hardware) ?? throw new InvalidOperationException($"Connect {hardware.Name} first.");
            var slot = GetCliffsSlot(profile, device);
            if (slot is null)
            {
                var choices = profile.DeviceSlots.Where(IsLegacyCliffsSlot)
                    .Append(CliffsDeviceName(profile, device)).OfType<string>().ToArray();
                if (choices.Length == 0)
                    throw new InvalidOperationException($"Bind one input from {device.Name} in Cliffs of Dover first to establish its joystick slot.");
                slot = ReviewWindow.Pick(this, $"Choose the Cliffs of Dover joystick slot for {device.Name}", choices);
                if (slot is null)
                    return null;
            }
            if (!slots.Add(slot))
                throw new InvalidOperationException("Two controllers selected the same Cliffs of Dover joystick slot.");
            devices.Add(group.Key, (device.Id, slot));
            preview.AppendLine($"{hardware.Name} · {slot}");
            foreach (var edit in group)
            {
                var signal = edit.RemoveInput is { } token
                    ? InputSignal.Parse(token) ?? throw new InvalidDataException("The Cliffs of Dover binding to remove has an invalid input.")
                    : RequireInput(hardware, edit.Address).Signal;
                var native = CliffsBindingProfile.EncodeInput(slot, signal)
                    ?? throw new InvalidOperationException($"{signal} cannot be represented in Cliffs of Dover.");
                bool changed = profile.Apply(edit.Action, native, edit.RemoveInput is not null);
                preview.AppendLine($"  {(edit.RemoveInput is null ? "Assign" : "Remove")} · {edit.Action.Name} · {signal}{(changed ? "" : " · already matches")}");
            }
            preview.AppendLine();
        }

        bool? update = ChooseBindingFileDisposition();
        if (update is null)
            return null;
        preview.AppendLine(update.Value ? target : "Save a separate controls INI to load in the game.");
        if (!ReviewWindow.Confirm(this, "Cliffs of Dover binding preview", preview.ToString(), update.Value ? "Write controls" : "Choose new file…"))
            return null;
        string? destination = update.Value ? target : PickNewProfilePath("Save Cliffs of Dover controls",
            FlightProfileFilter(game.Kind), "VCP-controls.ini", NativeProfileDirectory(game));
        if (destination is null)
            return null;
        RequireLivePlanDevices(edits, game);
        if (devices.Any(entry => ConnectedDevice(GetLayout(entry.Key))?.Id != entry.Value.Id))
            throw new InvalidOperationException("A controller changed during preview. Preview the Cliffs of Dover bindings again.");
        var write = profile.ToWrite(destination.Equals(profile.Path, StringComparison.OrdinalIgnoreCase) ? null : destination);
        NativeBindingWrites.Commit([write], update.Value ? () => CliffsBindingProfile.EnsureGameClosed(GameFolder(game)) : null);
        foreach (var (_, device) in devices)
            _cliffsSlots[(destination, device.Id)] = device.Slot;
        _plan.SourceProfiles[game.Id] = destination;
        return $"Saved {Path.GetFileName(destination)}.";
    }

    private string CliffsControlsTooltip(GameTarget game)
    {
        var sources = CliffsOfDoverGame.FindSources(GameFolder(game), _plan.SourceProfiles.GetValueOrDefault(game.Id));
        return sources.Count == 0 ? "Choose the game installation or a saved Cliffs of Dover controls INI."
            : "Commands read from:" + Environment.NewLine + string.Join(Environment.NewLine, sources);
    }

    private async Task ChooseCliffsControls(GameTarget game)
    {
        const string installation = "Game installation";
        const string profile = "Saved controls INI";
        var choice = ReviewWindow.Pick(this, "Cliffs of Dover controls", new[] { installation, profile });
        if (choice == installation)
        {
            var folder = new OpenFolderDialog { Title = "Choose the Cliffs of Dover installation", InitialDirectory = GameFolder(game) };
            if (folder.ShowDialog(this) != true)
                return;
            _plan.GameFolders[game.Id] = folder.FolderName;
            _plan.SourceProfiles.Remove(game.Id);
        }
        else if (choice == profile)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open a Cliffs of Dover controls INI",
                Filter = "Cliffs of Dover controls (*.ini)|*.ini",
                InitialDirectory = NativeProfileDirectory(game, _plan.SourceProfiles.GetValueOrDefault(game.Id))
            };
            if (dialog.ShowDialog(this) != true)
                return;
            _plan.SourceProfiles[game.Id] = dialog.FileName;
        }
        else
        {
            return;
        }
        await LoadGame();
    }
}
