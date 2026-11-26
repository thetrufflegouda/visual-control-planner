using System.IO;
using System.Text;
using Microsoft.Win32;
using VisualControlsBinder.Games;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private Il2Aircraft _il2Aircraft = Il2Aircraft.Global;

    private async Task<IReadOnlyList<GameAction>> LoadIl2Actions(GameTarget game, string folder, int request)
    {
        var aircraft = await Task.Run(() => Il2AircraftCatalog.Load(folder));
        if (request != _catalogRequest)
            return [];
        _settingAircraft = true;
        try
        {
            var selected = _plan.Aircraft.GetValueOrDefault(game.Id, "");
            _il2Aircraft = aircraft.FirstOrDefault(plane => plane.Id == selected)
                ?? new Il2Aircraft(selected, selected, Available: false);
            AircraftPicker.ItemsSource = _il2Aircraft.Available ? aircraft : [.. aircraft, _il2Aircraft];
            AircraftPicker.SelectedItem = _il2Aircraft;
        }
        finally { _settingAircraft = false; }
        var source = FlightProfilePath(game);
        return await Task.Run(() => Il2Game.LoadActions(folder, source));
    }

    private string? Il2SourcePath(GameTarget game, string folder)
    {
        var selected = _plan.Aircraft.GetValueOrDefault(game.Id, "");
        if (selected.Length == 0)
            return Il2Game.FindProfile(folder);
        if (!_il2Aircraft.Available || _il2Aircraft.Id != selected)
            return null;
        return _il2Aircraft.ProfileId is null ? Il2Game.FindProfile(folder) : _il2Aircraft.ProfilePath;
    }

    private string? Il2AircraftStatus(GameTarget game)
    {
        if (game.Kind != GameKind.Il2 || _il2Aircraft.IsGlobal)
            return null;
        if (_plan.SourceProfiles.ContainsKey(ScopeOf(game)))
            return "Selected file for planning";
        if (!_il2Aircraft.Available)
            return "Aircraft data unavailable";
        if (_il2Aircraft.ProfileId is not { } profile)
            return "Inherits Global bindings";
        return _il2Aircraft.ProfilePath is null ? $"{profile} · key mapping file unavailable" : profile;
    }

    private string? Il2NativeRestriction(GameTarget game)
    {
        if (game.Kind != GameKind.Il2 || _plan.Aircraft.GetValueOrDefault(game.Id) is not { Length: > 0 })
            return null;
        if (_il2Aircraft.Available && _il2Aircraft.ProfilePath is { } assigned &&
            string.Equals(FlightProfilePath(game), assigned, StringComparison.OrdinalIgnoreCase))
            return null;
        return "Create or select a custom key mapping profile for this aircraft in IL-2, then use that assigned profile.";
    }

    private int? FindIl2Slot(Il2JoystickSlots slots, HardwareDefinition device)
    {
        var connected = ConnectedDevice(device);
        return slots.FindSlot(connected?.Id ?? FindCustom(device)?.DeviceId, connected?.Name ?? KnownJoystickName(device));
    }

    private string? ExportIl2(GameTarget game, IReadOnlyList<BindingEdit> edits)
    {
        if (Il2NativeRestriction(game) is { } restriction)
            throw new InvalidOperationException(restriction);
        if (!_il2Aircraft.IsGlobal)
            return ExportIl2Aircraft(game, edits);
        var path = FlightProfilePath(game) ?? throw new InvalidOperationException("Choose an IL-2 .actions file first.");
        var folder = GameFolder(game);
        RequireIl2GlobalDestination(folder, path);
        var profile = Il2Profile.Load(path);
        var devices = Il2JoystickSlots.Load(folder);
        var preview = new StringBuilder();
        foreach (var edit in edits)
        {
            var device = GetLayout(edit.Address.Device);
            var (signal, reference) = edit.RemoveInput is { } removal && InputSignal.Parse(removal) is { } saved
                ? (saved, false) : RequireInput(device, edit.Address);
            var slot = FindIl2Slot(devices, device) ?? throw new InvalidOperationException(
                $"{device.Name} cannot be identified uniquely in IL-2's devices.txt. Open IL-2 with this device connected and check that it appears in Key Mapping.");
            var input = Il2JoystickSlots.Encode(slot, signal) ?? throw new InvalidOperationException(
                $"{PhysicalLabel(device, edit.Address)} cannot be represented as an IL-2 input.");
            bool changed = profile.Apply(edit.Action, input, edit.RemoveInput is not null);
            preview.AppendLine($"{(edit.RemoveInput is null ? "Assign" : "Remove")} · {PhysicalLabel(device, edit.Address)}");
            preview.AppendLine($"  {AssignmentLabel(edit.Action)} · {signal} → {input}{(reference ? " (reference input)" : "")}");
            if (!changed)
                preview.AppendLine("  Already matches this change.");
        }

        var update = ChooseBindingFileDisposition(isPair: true);
        if (update is null)
            return null;
        var destination = profile.SourcePath;
        var map = profile.MapPath;
        if (!update.Value)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save a new IL-2 binding file pair", Filter = FlightProfileFilter(GameKind.Il2),
                DefaultExt = ".actions", FileName = "VisualControlsBinder.actions",
                InitialDirectory = NativeProfileDirectory(game), OverwritePrompt = false
            };
            if (dialog.ShowDialog(this) != true)
                return null;
            destination = dialog.FileName;
            RequireIl2GlobalDestination(folder, destination);
            map = Il2Profile.CompanionPath(destination);
        }

        preview.AppendLine().AppendLine("Binding files to write").AppendLine(destination).AppendLine(map);
        preview.AppendLine(update.Value ? "Close IL-2 before updating a profile it uses."
            : "This saves a separate pair. IL-2 continues to use its active files.");
        if (!ReviewWindow.Confirm(this, "IL-2 binding preview", preview.ToString(), update.Value ? "Update both files" : "Create both files"))
            return null;
        RequireLivePlanDevices(edits, game);
        if (update.Value)
        {
            profile.SaveChanges();
            return $"Updated {Path.GetFileName(destination)} and {Path.GetFileName(map)}.";
        }
        profile.SaveAs(destination);
        _plan.SourceProfiles[ScopeOf(game)] = destination;
        return $"Created {Path.GetFileName(destination)} and {Path.GetFileName(map)}.";
    }

    private string? ExportIl2Aircraft(GameTarget game, IReadOnlyList<BindingEdit> edits)
    {
        var plane = _il2Aircraft;
        var path = plane.ProfilePath ?? throw new InvalidOperationException("Select an assigned IL-2 aircraft profile first.");
        string folder = GameFolder(game);
        var profile = new Il2AircraftProfile(path);
        var slots = Il2JoystickSlots.Load(folder);
        var preview = new StringBuilder().AppendLine(plane.Name).AppendLine(path).AppendLine();
        var shared = AircraftPicker.Items.OfType<Il2Aircraft>().Where(other => other.Id != plane.Id &&
            string.Equals(other.ProfilePath, path, StringComparison.OrdinalIgnoreCase)).Select(other => other.Name).ToArray();
        if (shared.Length > 0)
            preview.AppendLine($"Aircraft sharing this profile · {string.Join(", ", shared)}").AppendLine();
        bool changed = false;
        foreach (var edit in edits)
        {
            var layout = GetLayout(edit.Address.Device);
            var signal = edit.RemoveInput is { } token
                ? InputSignal.Parse(token) ?? throw new InvalidDataException("The IL-2 binding to remove has an invalid input.")
                : RequireInput(layout, edit.Address).Signal;
            var slot = FindIl2Slot(slots, layout)
                ?? throw new InvalidOperationException($"{layout.Name} has no unique device slot in IL-2. Save controls with it connected.");
            string input = Il2JoystickSlots.Encode(slot, signal)
                ?? throw new InvalidOperationException($"{signal} cannot be represented in IL-2.");
            bool updated = profile.Apply(edit.Action, input, edit.RemoveInput is not null);
            changed |= updated;
            preview.AppendLine($"{(edit.RemoveInput is null ? "Assign" : "Remove")} · {edit.Action.Name} · {PhysicalLabel(layout, edit.Address)} · {signal}");
        }
        if (!changed)
            return "The assigned IL-2 aircraft profile already matches the plan.";
        if (!ReviewWindow.Confirm(this, "IL-2 aircraft binding preview", preview.ToString(), "Update assigned profile"))
            return null;
        RequireLivePlanDevices(edits, game);
        if (!string.Equals(Il2AircraftCatalog.AssignedProfilePath(folder, plane.Id), path, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The aircraft’s assigned profile changed. Reload the aircraft before exporting.");
        NativeBindingWrites.Commit([profile.ToWrite()], () => Il2Profile.EnsureClosed(path));
        return $"Updated {plane.Name}’s assigned mapping profile.";
    }

    private static void RequireIl2GlobalDestination(string folder, string path)
    {
        var custom = Path.GetFullPath(Path.Combine(folder, "data", "input", "custom"));
        var relative = Path.GetRelativePath(custom, Path.GetFullPath(path));
        if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException("Use Global bindings or choose a file outside IL-2's custom profile folder.");
    }
}
