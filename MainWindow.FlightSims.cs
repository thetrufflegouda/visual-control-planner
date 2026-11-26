using System.IO;
using System.Windows.Controls;
using System.Text;
using VisualControlsBinder.Games;
using VisualControlsBinder.Planner;
using VisualControlsBinder.Inputs;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private bool _settingAircraft;
    private DcsModule? _aircraft;

    private async Task<IReadOnlyList<GameAction>> LoadFlightActions(GameTarget game, string folder, int request)
    {
        if (game.Kind == GameKind.CustomVJoy)
            return _plan.CustomActions.ToArray();
        if (game.Kind == GameKind.Dcs)
        {
            var modules = await Task.Run(() => DcsGame.DiscoverModules(folder));
            if (request != _catalogRequest)
                return [];
            _settingAircraft = true;
            try
            {
                AircraftPicker.ItemsSource = modules;
                _aircraft = modules.FirstOrDefault(module => module.Id == _plan.Aircraft.GetValueOrDefault(game.Id))
                    ?? modules.FirstOrDefault(module => module.Id == "F-16C_50") ?? modules.FirstOrDefault();
                AircraftPicker.SelectedItem = _aircraft;
                if (_aircraft is null)
                    throw new InvalidOperationException("No aircraft input modules were found. Choose the DCS World install folder.");
            }
            finally { _settingAircraft = false; }
            var aircraft = _aircraft;
            return await Task.Run(() => DcsGame.LoadActions(aircraft));
        }

        if (game.Kind == GameKind.Il2)
            return await LoadIl2Actions(game, folder, request);
        var source = game.Kind switch
        {
            GameKind.Squadrons => SquadronsProfile(game),
            GameKind.Msfs2020 or GameKind.Msfs2024 or GameKind.CliffsOfDover => _plan.SourceProfiles.GetValueOrDefault(game.Id),
            GameKind.Discovered => FindAddedGame(game)?.BindingFile,
            _ => FlightProfilePath(game)
        };
        return await Task.Run(() => GameProfiles.ReadActions(game.Kind, folder, source));
    }

    private async void AircraftChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingAircraft || SelectedGame is not { } game)
            return;
        string? id = (game.Kind, AircraftPicker.SelectedItem) switch
        {
            (GameKind.Dcs, DcsModule module) => module.Id,
            (GameKind.Il2, Il2Aircraft plane) => plane.Id,
            _ => null
        };
        if (id is null || id == _plan.Aircraft.GetValueOrDefault(game.Id, ""))
            return;
        if (id.Length == 0)
            _plan.Aircraft.Remove(game.Id);
        else
            _plan.Aircraft[game.Id] = id;
        await LoadGame();
    }

    private string ActiveGameLabel(GameTarget game) => game.Kind switch
    {
        GameKind.Dcs when _aircraft is { } aircraft => $"DCS: {aircraft.Name}",
        GameKind.Il2 when !_il2Aircraft.IsGlobal => $"IL-2: {_il2Aircraft.Name}",
        _ => game.Name
    };

    private string ScopeOf(GameTarget game) => game.Kind switch
    {
        GameKind.Dcs => $"{game.Id}/{_aircraft?.Id ?? _plan.Aircraft.GetValueOrDefault(game.Id, "")}",
        GameKind.Il2 when _plan.Aircraft.GetValueOrDefault(game.Id) is { Length: > 0 } id => $"{game.Id}/{id}",
        _ => game.Id
    };

    private string? FlightProfilePath(GameTarget game, bool refreshWarThunder = false)
    {
        if (game.Kind == GameKind.Dcs)
            return _selectedDcsProfile?.Path;
        var selected = _plan.SourceProfiles.GetValueOrDefault(ScopeOf(game));
        if (!string.IsNullOrWhiteSpace(selected))
            return selected;
        var folder = GameFolder(game);
        return game.Kind switch
        {
            GameKind.WarThunder when !refreshWarThunder && _profiles.WarThunderSource is { } source &&
                string.Equals(source.Folder, folder, StringComparison.OrdinalIgnoreCase) => source.Path,
            GameKind.WarThunder => GameProfiles.FindWarThunderProfile(folder),
            GameKind.Il2 => Il2SourcePath(game, folder),
            GameKind.Elite => EliteDangerousGame.FindProfile(folder),
            _ => null
        };
    }

    private static string FlightProfileFilter(GameKind kind) => kind switch
    {
        GameKind.Dcs => "DCS binding profile (*.diff.lua)|*.diff.lua",
            GameKind.CliffsOfDover => "Cliffs of Dover controls (*.ini)|*.ini",
        GameKind.WarThunder => "War Thunder control profile (*.blk)|*.blk",
        GameKind.Il2 => "IL-2 action file (*.actions)|*.actions",
        GameKind.Elite => "Elite Dangerous control profile (*.binds)|*.binds",
        _ => "All files|*.*"
    };

    private string? ExportFlightProfile(GameTarget game, IReadOnlyList<BindingEdit> edits)
    {
        return game.Kind switch
        {
            GameKind.Dcs => ExportDcs(game, edits),
            GameKind.Squadrons => ExportSquadrons(game, edits),
            GameKind.CliffsOfDover => ExportCliffs(game, edits),
            GameKind.Elite => ExportElite(game, edits),
            GameKind.WarThunder => ExportWarThunder(game, edits),
            GameKind.Il2 => ExportIl2(game, edits),
            _ => throw new InvalidOperationException($"{ActiveGameLabel(game)} supports planning and Joystick Gremlin output. For native bindings, assign the inputs in the game's controls menu.")
        };
    }

    private string? ExportWarThunder(GameTarget game, IReadOnlyList<BindingEdit> edits)
    {
        var path = FlightProfilePath(game, refreshWarThunder: true) ??
            throw new InvalidOperationException("Save controls in War Thunder or choose a controls .blk profile first.");
        var profile = WarThunderProfile.Load(path);
        var preview = new StringBuilder();
        foreach (var edit in edits)
        {
            var device = GetLayout(edit.Address.Device);
            var (signal, reference) = edit.RemoveInput is { } removal && InputSignal.Parse(removal) is { } saved
                ? (saved, false) : RequireInput(device, edit.Address);
            var (vendor, product) = ControllerProductIds(device);
            var mapped = profile.FindDevice(vendor, product) ??
                throw new InvalidOperationException($"{device.Name}: save a War Thunder device map that identifies this controller exactly once.");
            var activation = EditActivation(game, edit);
            if (signal.Kind == "axis" && edit.Action.IsAxis)
                profile.ApplyAxis(edit.Action.Id, mapped, signal.Index, edit.RemoveInput is not null);
            else if (signal.Kind == "button" && !edit.Action.IsAxis)
                profile.ApplyButton(edit.Action.Id, mapped.NativeButton(signal.Index), activation, edit.RemoveInput is not null);
            else
                throw new InvalidOperationException($"{PhysicalLabel(device, edit.Address)}: choose a supported War Thunder button or axis.");
            preview.AppendLine($"{PhysicalLabel(device, edit.Address)} · {signal}{(reference ? " (reference input)" : "")}");
            string timing = signal.Kind == "button" ? $" · {activation.Label()}" : "";
            preview.AppendLine($"  {(edit.RemoveInput is null ? "Assign" : "Remove")} {edit.Action.Name}{timing}");
        }
        bool? update = ChooseBindingFileDisposition();
        if (update is null)
            return null;
        preview.AppendLine().AppendLine($"Source: {path}");
        preview.AppendLine(update.Value && profile.IsMachineProfile
            ? "VCP updates the active controls and its matching account mirror."
            : "Import the saved .blk in War Thunder's Controls menu.");
        if (!ReviewWindow.Confirm(this, "War Thunder binding preview", preview.ToString(),
            update.Value ? "Update selected file" : "Choose new file…"))
            return null;
        if (update.Value)
        {
            RequireLivePlanDevices(edits, game);
            profile.SaveChanges();
            return profile.IsMachineProfile ? "Updated the active War Thunder control settings."
                : $"Updated {Path.GetFileName(path)}. Import it in War Thunder Controls.";
        }
        var destination = PickNewProfilePath("Save a new War Thunder control profile", FlightProfileFilter(GameKind.WarThunder),
            "VisualControlsBinder.blk", NativeProfileDirectory(game, path));
        if (destination is null)
            return null;
        RequireLivePlanDevices(edits, game);
        profile.SaveAs(destination);
        _plan.SourceProfiles[ScopeOf(game)] = destination;
        return $"Created {Path.GetFileName(destination)}. Import it in War Thunder Controls.";
    }

    private string? ExportElite(GameTarget game, IReadOnlyList<BindingEdit> edits)
    {
        var path = FlightProfilePath(game) ?? throw new InvalidOperationException("Select an Elite Dangerous binding profile first.");
        var profile = EliteDangerousGame.LoadProfile(path);
        bool? update = ChooseBindingFileDisposition();
        if (update is null)
            return null;
        var preview = new StringBuilder();
        foreach (var edit in edits)
        {
            var device = GetLayout(edit.Address.Device);
            var (signal, reference) = edit.RemoveInput is { } removal && InputSignal.Parse(removal) is { } saved
                ? (saved, false) : RequireInput(device, edit.Address);
            var id = ProductGuid(device) ?? throw new InvalidOperationException($"{device.Name}: pair this layout with a Windows controller first.");
            string key = EliteDangerousGame.JoystickKey(signal.Kind, signal.Index, signal.Direction);
            var change = profile.Apply(edit.Action, EliteDeviceId(id), key, edit.RemoveInput is not null);
            preview.AppendLine($"{PhysicalLabel(device, edit.Address)} · {signal}{(reference ? " (reference input)" : "")}");
            preview.AppendLine($"  {(edit.RemoveInput is null ? "Assign" : "Remove")} {edit.Action.Name}");
            if (change.Replaced is { } replaced)
                preview.AppendLine($"  Replaces {replaced.Key} on device {replaced.Device}.");
        }
        preview.AppendLine().AppendLine($"Source: {path}");
        if (!ReviewWindow.Confirm(this, "Elite Dangerous binding preview", preview.ToString(),
            update.Value ? "Update selected file" : "Choose new file…"))
            return null;
        if (update.Value)
        {
            RequireLivePlanDevices(edits, game);
            profile.SaveChanges();
            return $"Updated {Path.GetFileName(path)}. Choose this preset in Elite Dangerous.";
        }
        var destination = PickNewProfilePath("Save a new Elite Dangerous controls profile", FlightProfileFilter(GameKind.Elite),
            "VisualControlsBinder.4.2.binds", NativeProfileDirectory(game));
        if (destination is null)
            return null;
        RequireLivePlanDevices(edits, game);
        profile.SaveAs(destination);
        _plan.SourceProfiles[ScopeOf(game)] = destination;
        return $"Created {Path.GetFileName(destination)}. Choose this preset in Elite Dangerous.";
    }

    private static string EliteDeviceId(Guid id)
    {
        uint value = BitConverter.ToUInt32(id.ToByteArray());
        return $"{value & 0xffff:X4}{value >> 16:X4}";
    }
}
