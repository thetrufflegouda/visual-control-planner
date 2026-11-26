using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using VisualControlsBinder.Games;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private bool _exportInProgress;

    private async void PreviewExport(object sender, RoutedEventArgs e)
    {
        if (_exportInProgress || SelectedGame is not { } game)
            return;
        StopLearning();
        string scope = ScopeOf(game);
        bool gremlin = OutputFor(game) == OutputMode.JoystickGremlin;
        var shift = gremlin ? ActiveGremlinShift : null;
        bool HasLayer(ControlAddress address) => address.Layer != GremlinShiftLayer || shift?.Behavior is not null;
        var edits = _plan.Edits.Where(edit => edit.Game == scope && HasLayer(edit.Address) &&
            EditForOutput(game, edit) && (!gremlin || !IsGremlinModifier(edit.Address)))
            .OrderBy(edit => edit.RemoveInput is null).ToArray();
        var routes = gremlin ? _plan.VirtualRoutes.Where(route => route.Game == scope && route.Output is not null &&
            HasLayer(route.Address) && !IsGremlinModifier(route.Address)).ToArray() : [];
        bool pendingRouteRemovals = gremlin && _plan.RemovedVirtualRoutes?.Any(route => route.Game == scope &&
            HasLayer(route.Address) && !route.Applied) == true;
        if (edits.Length == 0 && routes.Length == 0 && !pendingRouteRemovals &&
            (!gremlin || _plan.GetGremlinShift(scope) is null))
        {
            Notify(gremlin ? "Stage an assignment or choose a vJoy output first." : "Select a physical control and stage a binding change first.");
            return;
        }
        var plan = _plan;
        _exportInProgress = true;
        try
        {
            if (gremlin)
            {
                var native = edits.Where(edit => edit.ViaVJoy).ToArray();
                if (ExportGremlin(game, edits.Where(edit => !edit.ViaVJoy && edit.RemoveInput is null).ToArray(), routes, native) is { } exported)
                {
                    plan.Edits.RemoveAll(edit => native.Contains(edit));
                    await LoadGame();
                    Notify(exported);
                }
            }
            else
            {
                if (Il2NativeRestriction(game) is { } restriction)
                    throw new InvalidOperationException(restriction);
                RequireLivePlanDevices(edits, game);
                var changed = game.Kind switch
                {
                    GameKind.StarCitizen => ExportStarCitizen(game, edits),
                    GameKind.Swg => ExportSwgNative(game, edits),
                    GameKind.Msfs2020 or GameKind.Msfs2024 => await ExportMsfs(game, edits),
                    _ => ExportFlightProfile(game, edits)
                };
                if (changed is not null)
                {
                    plan.Edits.RemoveAll(edit => edit.Game == scope && edits.Any(staged => ReferenceEquals(staged, edit)));
                    if (ReferenceEquals(plan, _plan) && SelectedGame?.Id == game.Id)
                        await LoadGame();
                    Notify(changed);
                }
            }
        }
        catch (Exception error) { Notify(error.Message); }
        finally { _exportInProgress = false; }
    }

    private void RequireLivePlanDevices(IReadOnlyList<BindingEdit> edits, GameTarget game, bool throughVJoy = false)
    {
        RefreshConnectedDevices();
        var devices = edits.Select(edit => edit.Address.Device).Distinct(StringComparer.Ordinal)
            .Select(GetLayout).Select(layout => (Layout: layout, Controller: ConnectedDevice(layout))).ToArray();
        var offline = devices.Where(item => item.Controller is null).Select(item => item.Layout.Name).ToArray();
        if (offline.Length > 0)
        {
            RenderDeviceList();
            throw new InvalidOperationException($"Offline planning · {string.Join(", ", offline)}. Use Connect on the device card before writing game bindings.");
        }
        if (!throughVJoy && game.Kind is GameKind.StarCitizen or GameKind.WarThunder or GameKind.Elite)
        {
            var ambiguous = devices.FirstOrDefault(item =>
                _connected.Count(controller => controller.Product == item.Controller?.Product) > 1);
            if (ambiguous.Layout is { } layout)
                throw new InvalidOperationException($"{game.Name}'s profile identifies {layout.Name} by product ID. Two connected controllers share that ID. Use Joystick Gremlin to route them separately.");
        }
    }

    private string? ExportStarCitizen(GameTarget game, IReadOnlyList<BindingEdit> edits)
    {
        var path = StarProfilePath(game);
        if (!File.Exists(path))
            throw new InvalidOperationException("Choose a Star Citizen control profile or create a new one first.");
        var profile = StarCitizenGame.LoadProfile(path);
        bool? update = ChooseBindingFileDisposition();
        if (update is null)
            return null;
        var preview = new StringBuilder();
        var used = new Dictionary<int, string>();
        foreach (var edit in edits)
        {
            var device = GetLayout(edit.Address.Device);
            var (input, reference) = edit.RemoveInput is { } removal ? (removal, false)
                : GetStarInput(profile, device, edit.Address, used);
            var conflict = edit.RemoveInput is null && profile.Bindings.Any(binding => binding.Input == input &&
                binding.Map == edit.Action.Map && binding.Action != edit.Action.Id && binding.Activation == edit.Activation);
            bool changed = edit.RemoveInput is null ? profile.ApplyActivation(edit.Action.Map, edit.Action.Id, input, edit.Activation)
                : profile.RemoveActivationForDevice(edit.Action.Map, edit.Action.Id, input, ProductGuid(device), edit.NativeActivation);
            preview.AppendLine($"{(edit.RemoveInput is null ? "Assign" : "Remove")} · {PhysicalLabel(device, edit.Address)}");
            string timing = edit.Activation == BindingActivation.Default ? "" : $" · {edit.Activation.Label()}";
            preview.AppendLine($"  {edit.Action.Name} → {input}{timing}{(reference ? " (reference input)" : "")}");
            if (!changed)
                preview.AppendLine("  Already matches this change.");
            if (conflict)
                preview.AppendLine("  This input also controls another action in the same action map.");
        }
        preview.AppendLine().AppendLine($"Source: {profile.SourcePath}");
        preview.AppendLine("Load the saved profile through Star Citizen Control Profiles and confirm its joystick slots.");
        if (!ReviewWindow.Confirm(this, "Star Citizen binding preview", preview.ToString(),
            update.Value ? "Update selected file" : "Choose new file…"))
            return null;
        if (update.Value)
        {
            RequireLivePlanDevices(edits, game);
            profile.SaveChanges();
            return $"Updated {Path.GetFileName(profile.SourcePath)}. Load it through Star Citizen Control Profiles.";
        }
        var destination = PickNewProfilePath("Save a new Star Citizen control profile", "Star Citizen profile (*.xml)|*.xml",
            "layout_VisualControlsBinder_exported.xml", NativeProfileDirectory(game));
        if (destination is null)
            return null;
        RequireLivePlanDevices(edits, game);
        profile.SaveAs(destination);
        _plan.SourceProfiles[game.Id] = destination;
        return $"Created {Path.GetFileName(destination)}. Load it through Star Citizen Control Profiles.";
    }

    private bool? ChooseBindingFileDisposition(bool isPair = false)
    {
        var edit = isPair ? "Update the selected binding files" : "Update the selected binding file";
        var copy = isPair ? "Create a new pair based on this profile" : "Create a new file based on this profile";
        var choice = ReviewWindow.Pick(this, "Save binding changes", new[] { copy, edit });
        return choice is null ? null : choice == edit;
    }

    private string? ExportSwgNative(GameTarget game, IReadOnlyList<BindingEdit> edits)
    {
        var catalog = _profiles.SwgCatalog ?? throw new InvalidOperationException("Choose a saved SWG character keymap first.");
        if (edits.Select(edit => edit.Address.Device).Distinct().Take(2).Count() > 1)
            throw new InvalidOperationException("SWG's native keymap targets one joystick. Select Joystick Gremlin to combine multiple controllers.");
        if (_plan.NativeJoysticks.GetValueOrDefault(game.Id) != edits[0].Address.Device)
            throw new InvalidOperationException("Choose Use this device for the joystick selected in the SWG launcher before applying the bindings.");
        var profile = SwgNativeProfile.Load(catalog.SourceFiles);
        bool? update = ChooseBindingFileDisposition();
        if (update is null)
            return null;
        var preview = new StringBuilder();
        var targets = new HashSet<(string Map, string Kind, int Index)>();
        foreach (var edit in edits)
        {
            var device = GetLayout(edit.Address.Device);
            var (signal, reference) = edit.RemoveInput is { } input && InputSignal.Parse(input) is { } saved
                ? (saved, false) : RequireInput(device, edit.Address);
            if (edit.RemoveInput is null && !targets.Add((edit.Action.Map, signal.Kind, signal.Index)))
                throw new InvalidOperationException($"{signal} has competing assignments in the same SWG context. Keep one of them in the plan.");
            var change = profile.Apply(edit.Action, signal.Kind, signal.Index, edit.RemoveInput is not null);
            preview.AppendLine($"{PhysicalLabel(device, edit.Address)} · {signal}{(reference ? " (reference input)" : "")}");
            preview.AppendLine($"  {(edit.RemoveInput is null ? "Assign" : "Remove")} {edit.Action.Name}");
            if (change.ReplacedActionId is { } replaced)
                preview.AppendLine($"  Replaces {catalog.Actions.FirstOrDefault(action => action.Map == edit.Action.Map && action.Id == replaced)?.Name ?? replaced}.");
        }
        preview.AppendLine().AppendLine("Select this controller as the joystick in the SWG launcher.");
        preview.AppendLine(string.Join(Environment.NewLine, catalog.SourceFiles));
        if (!ReviewWindow.Confirm(this, "SWG binding preview", preview.ToString(),
            update.Value ? "Update selected file" : "Choose destination folder…"))
            return null;
        if (update.Value)
        {
            RequireLivePlanDevices(edits, game);
            profile.SaveChanges();
            return $"Updated the selected {game.Name} character keymap.";
        }
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder for the new SWG binding files", InitialDirectory = SwgProfileDirectory(game)
        };
        if (dialog.ShowDialog(this) != true)
            return null;
        RequireLivePlanDevices(edits, game);
        profile.SaveAs(dialog.FolderName);
        _plan.CharacterProfiles[game.Id] = Path.Combine(dialog.FolderName, Path.GetFileName(catalog.SourceFiles[0]));
        return $"Created {game.Name} binding files in {dialog.FolderName}.";
    }

    private (string Input, bool Reference) GetStarInput(StarCitizenProfile profile, HardwareDefinition device,
        ControlAddress address, Dictionary<int, string> used)
    {
        var (signal, reference) = RequireInput(device, address);
        var product = ProductGuid(device) ??
            throw new InvalidOperationException($"{device.Name}: pair this layout with a Windows controller first.");
        int? slot = FindSlot(profile, device);
        if (slot is null)
        {
            var productMatches = profile.Devices.Where(item => item.ProductGuid == product).ToArray();
            if (productMatches.Length > 1)
                throw new InvalidOperationException($"{device.Name}: multiple source profile slots match this device.");
            int free = Enumerable.Range(1, 16).FirstOrDefault(index => profile.Devices.All(item => item.Instance != index));
            if (free == 0)
                throw new InvalidOperationException("There are no free joystick slots in this profile.");
            var connected = ConnectedDevice(device);
            string productName = $"{connected?.Name ?? device.Name}  {{{product.ToString().ToUpperInvariant()}}}";
            profile.RegisterDevice(free, productName);
            slot = free;
        }
        if (used.TryGetValue(slot.Value, out var previous) && previous != device.Id)
            throw new InvalidOperationException($"Two layouts target joystick slot {slot}. Pair the distinct devices before exporting.");
        used[slot.Value] = device.Id;
        return (StarInput(slot.Value, signal), reference);
    }

    private string? ExportGremlin(GameTarget game, IReadOnlyList<BindingEdit> edits, IReadOnlyList<VirtualRoute> routes,
        IReadOnlyList<BindingEdit> routed)
    {
        string scope = ScopeOf(game);
        var shift = _plan.GetGremlinShift(scope);
        bool shiftEnabled = ActiveGremlinShift?.Behavior is not null;
        var removed = _plan.RemovedVirtualRoutes?.Where(route => route.Game == scope &&
            (route.Address.Layer != GremlinShiftLayer || shiftEnabled)).ToArray() ?? [];
        if (edits.Count == 0 && routes.Count == 0 && routed.Count == 0 && shift is null && !removed.Any(route => !route.Applied))
            throw new InvalidOperationException("Stage an assignment for Gremlin. Direct game removals will remain in the plan.");
        var catalog = _profiles.SwgCatalog;
        var path = GremlinProfilePath(game);
        if (!File.Exists(path))
            throw new InvalidOperationException("Choose a Joystick Gremlin profile or create a new one first.");
        var profile = GremlinProfile.Load(path);
        RoutedGameExport? gameExport = null;
        if (routed.Count > 0)
        {
            if (_profiles.Gremlin is null || !string.Equals(profile.SourcePath, _profiles.Gremlin.SourcePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Reload the selected Gremlin profile before saving its game bindings.");
            _profiles.Gremlin.EnsureSourceUnchanged();
            RequireLivePlanDevices(routed, game, throughVJoy: true);
            gameExport = PrepareRoutedGameExport(game, routed);
        }
        int maxButton = GremlinButtonLimit(game);
        var baseModes = profile.Modes.Except(profile.ShiftModes).ToArray();
        var preferred = gameExport is not null ? PreferredGremlinBase(game, profile) : shift?.BaseMode ?? _savedGremlinModifier?.BaseMode;
        var baseMode = preferred is not null && baseModes.Contains(preferred) ? preferred
            : baseModes.Length == 1 ? baseModes[0] : ReviewWindow.Pick(this, "Gremlin base mode", baseModes);
        if (baseMode is null)
            return null;
        var assignments = new List<GremlinAssignment>();
        var preview = new StringBuilder();
        string? shiftMode = shiftEnabled ? profile.ActiveShifts.FirstOrDefault(item => item.BaseMode == baseMode)?.ChildMode : null;
        bool shiftChanged = false;
        if (shift is { Behavior: { } behavior })
        {
            var device = GetLayout(shift.Address.Device);
            var (signal, _) = RequireInput(device, shift.Address);
            if (signal.Kind != "button")
                throw new InvalidOperationException("Learn a physical button for the Gremlin Shift modifier.");
            var source = GremlinDeviceId(profile, device);
            if (!profile.ActiveShifts.Any(existing => existing.BaseMode == baseMode && existing.Device == source &&
                existing.Button == signal.Index && existing.Behavior == behavior))
            {
                shiftMode = profile.ConfigureShift(source, signal.Index, baseMode, behavior);
                shiftChanged = true;
                preview.AppendLine($"{PhysicalLabel(device, shift.Address)} · {signal} → {(behavior == GremlinShiftBehavior.Momentary ? "Hold to shift" : "Toggle shift")}");
                preview.AppendLine($"  {baseMode} ↔ {shiftMode} · This button is reserved for switching modes.").AppendLine();
            }
        }
        else if (shift is not null && profile.ClearShift(baseMode))
        {
            shiftChanged = true;
            preview.AppendLine($"Disable VCP's Gremlin Shift modifier in {baseMode}.").AppendLine();
        }

        string ModeFor(ControlAddress address) => address.Layer == GremlinShiftLayer
            ? shiftMode ?? throw new InvalidOperationException("Set a Gremlin Shift modifier first.") : baseMode;
        var existingRoutes = profile.Devices.SelectMany(device => profile.GetBoundInputs(device.Id)).ToArray();
        var explicitRoutes = ReadGremlinRoutes(profile, routes, ModeFor, maxButton);
        var disabledSources = new HashSet<(Guid DeviceId, string Kind, int InputId, string Mode)>();
        var removals = new HashSet<GremlinRouteRemoval>();
        var completed = new List<(VirtualRouteRemoval Change, Guid Device, string Token)>();
        var otherModes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var change in removed)
        {
            string mode = change.NativeMode ?? ModeFor(change.Address);
            var signal = InputSignal.Parse(change.InputToken);
            var source = change.InstanceId;
            if (signal is null || source is null)
            {
                var layout = GetLayout(change.Address.Device);
                signal ??= RequireInput(layout, change.Address).Signal;
                source ??= GremlinDeviceId(profile, layout, registerDevice: false);
            }
            if (signal.Kind != change.Output.Kind)
                throw new InvalidOperationException($"{change.Address.Control}: learn the original physical {change.Output.Kind} before removing {change.Output}.");

            var key = (source.Value, signal.Kind, signal.Index, mode);
            disabledSources.Add(key);
            if (change.Applied)
                continue;
            if (explicitRoutes.ContainsKey(key))
            {
                // An explicit replacement controls this same physical input and preserves its transforms.
                completed.Add((change, source.Value, signal.Token));
                continue;
            }

            var removal = new GremlinRouteRemoval(source.Value, signal.Kind, signal.Index, mode, change.Output);
            if (profile.ContainsRoute(removal))
            {
                if (removals.Add(removal))
                {
                    var label = PhysicalLabel(GetLayout(change.Address.Device), change.Address);
                    preview.AppendLine($"Disconnect · {label} · {signal} → {change.Output} · {mode}");
                }
                completed.Add((change, source.Value, signal.Token));
                continue;
            }

            var matchingModes = profile.Modes.Where(candidate => change.NativeMode is null && candidate != mode &&
                (change.Address.Layer == GremlinShiftLayer ? profile.ShiftModes.Contains(candidate) : !profile.ShiftModes.Contains(candidate)) &&
                profile.ContainsRoute(removal with { Mode = candidate })).ToArray();
            if (matchingModes.Length == 0)
                completed.Add((change, source.Value, signal.Token));
            else
                otherModes.UnionWith(matchingModes);
        }
        if (otherModes.Count > 0)
            preview.AppendLine($"Some removals are still pending in {string.Join(", ", otherModes.Order(StringComparer.Ordinal))}.");

        var routedOutputs = explicitRoutes.Values.Select(assignment => assignment.Output).OfType<GremlinOutput>().ToArray();
        foreach (var edit in edits)
        {
            string mode = ModeFor(edit.Address);
            var device = GetLayout(edit.Address.Device);
            var (signal, reference) = RequireInput(device, edit.Address);
            if (signal.Kind == "hat")
                throw new InvalidOperationException($"{PhysicalLabel(device, edit.Address)} reports a POV hat. For Gremlin export, select a hat direction that reports a button.");
            var source = GremlinDeviceId(profile, device);
            var key = (source, signal.Kind, signal.Index, mode);
            var selected = _plan.GetVirtualRoute(scope, edit.Address);
            if (selected is { Output: null })
                throw new InvalidOperationException($"{PhysicalLabel(device, edit.Address)}: choose a vJoy button or axis.");
            if (disabledSources.Contains(key) && !explicitRoutes.ContainsKey(key))
            {
                preview.AppendLine($"{edit.Action.Name} stays in the plan. Choose a vJoy output to reconnect this control.");
                continue;
            }
            var target = catalog?.PickTarget(edit.Action) ?? new SwgTarget(null, null);
            var reserved = (catalog?.ReservedOutputs(edit.Action.Map) ?? []).Concat(routedOutputs)
                .Concat(assignments.Select(item => item.Output).OfType<GremlinOutput>());
            var (output, keyboard) = profile.ChooseTarget(key, explicitRoutes.GetValueOrDefault(key)?.Output, target, reserved, maxButton);
            if (output is null && keyboard is null)
                throw new InvalidOperationException($"{edit.Action.Name}: no free {signal.Kind} route. Choose an available vJoy output in the Gremlin template.");
            if (assignments.Any(item => item.DeviceId == source && item.Kind == signal.Kind && item.InputId == signal.Index && item.Mode == mode))
                throw new InvalidOperationException($"{signal} has multiple assignments on the same device in Gremlin mode {mode}.");
            if (output is not null && assignments.Any(item => item.Output == output && item.Action is { } action &&
                action.Map == edit.Action.Map && action.Id != edit.Action.Id))
                throw new InvalidOperationException($"{output} would drive two different actions in the same game context.");

            assignments.Add(new GremlinAssignment(source, signal.Kind, signal.Index, edit.Action, output, keyboard, mode));
            preview.AppendLine($"{PhysicalLabel(device, edit.Address)} · {signal}{(reference ? " (reference input)" : "")} · {mode}");
            preview.AppendLine($"  {edit.Action.Name} → {output?.ToString() ?? $"Saved keyboard input (scan code {keyboard!.ScanCode})"}");
            if (output is not null && output != target.Output)
                preview.AppendLine($"  Bind {output} to this action in {ActiveGameLabel(game)}.");
            if (output is not null && existingRoutes.Any(route => route.Mode == mode &&
                (route.DeviceId != source || route.Kind != signal.Kind || route.InputId != signal.Index) &&
                route.Targets.Any(mapped => mapped.Output == output) &&
                !removals.Contains(new GremlinRouteRemoval(route.DeviceId, route.Kind, route.InputId, mode, output))))
                preview.AppendLine("  Another physical input in this Gremlin mode also drives this vJoy output.");
        }
        var used = assignments.Select(assignment => (assignment.DeviceId, assignment.Kind, assignment.InputId, assignment.Mode)).ToHashSet();
        var unmapped = explicitRoutes.Where(route => !used.Contains(route.Key)).Select(route => route.Value).ToArray();
        if (unmapped.Length > 0)
        {
            var changes = gameExport is null ? unmapped : unmapped.Where(route =>
                profile.GetOutput(route.DeviceId, route.Kind, route.InputId, route.Mode) != route.Output).ToArray();
            if (changes.Length > 0)
                preview.AppendLine().AppendLine($"{changes.Length} explicit vJoy input routes");
            foreach (var route in changes)
                preview.AppendLine($"{route.SourceLabel} · {route.Mode} → {route.Output}");
            assignments.AddRange(unmapped);
        }
        if (gameExport is not null)
        {
            var (gremlinWrite, routeNotes) = profile.PrepareUpdate(assignments, maxButton, removals);
            NativeBindingWrite[] writes = gremlinWrite is null ? gameExport.Writes.ToArray() : [gremlinWrite, .. gameExport.Writes];
            var details = new StringBuilder().AppendLine("GREMLIN ROUTES").AppendLine(path);
            if (gremlinWrite is null)
                details.AppendLine("The selected routes already match this profile.");
            else
                details.Append(preview).AppendLine("Reload this profile in Joystick Gremlin after saving.");
            details.AppendLine().Append(gameExport.Details);
            if (gremlinWrite is not null && routeNotes.Count > 0)
                details.AppendLine().AppendLine(string.Join(Environment.NewLine, routeNotes));
            string result;
            if (writes.Length == 0)
                result = "The Gremlin routes and game profile already match the plan.";
            else
            {
                details.AppendLine().AppendLine($"Update {writes.Length} file(s). Existing files get backups.");
                if (!ReviewWindow.Confirm(this, "Gremlin and game binding preview", details.ToString(), "Save changes"))
                    return null;
                RequireLivePlanDevices(routed, game, throughVJoy: true);
                NativeBindingWrites.Commit(writes, () =>
                {
                    profile.EnsureSourceUnchanged();
                    gameExport.EnsureClosed?.Invoke();
                });
                InvalidateRoutedGameBindings();
                result = $"Saved {writes.Length} changed profile file(s) for {game.Name}.";
            }
            _plan.SetRoutedProfile(scope, path, gameExport.ProfilePath);
            CompleteRouteChanges();
            return result;
        }
        if (assignments.Count == 0 && removals.Count == 0 && !shiftChanged)
        {
            foreach (var (change, device, token) in completed)
                _plan.MarkVirtualRouteRemovalApplied(change, device, token);
            Notify(otherModes.Count > 0 ? preview.ToString().Trim()
                : completed.Count > 0 ? "The selected Gremlin profile already has these routes disconnected."
                : "Choose a vJoy output for a planned control before exporting.");
            return null;
        }
        preview.AppendLine().AppendLine(game.Kind == GameKind.CustomVJoy && edits.Count == 0
            ? "Load the new XML in Joystick Gremlin to activate these vJoy routes."
            : $"Load the new profile in Joystick Gremlin and select your vJoy device in {game.Name}.");
        if (!ReviewWindow.Confirm(this, "Gremlin export preview", preview.ToString(), "Choose export file…"))
            return null;
        var dialog = new SaveFileDialog
        {
            Title = "Save a new Joystick Gremlin profile", Filter = "Gremlin profile (*.xml)|*.xml", DefaultExt = ".xml",
            FileName = $"{scope.Replace('/', '-')}-visual-controls.xml", InitialDirectory = GremlinProfileDirectory(game),
            OverwritePrompt = false
        };
        if (dialog.ShowDialog(this) != true)
            return null;
        var notes = profile.Export(dialog.FileName, assignments, maxButton, removals);
        SelectNewGremlinProfile(game, dialog.FileName);
        CompleteRouteChanges();
        if (notes.Count > 0)
            ReviewWindow.Confirm(this, "Gremlin setup notes", string.Join(Environment.NewLine + Environment.NewLine, notes), "Done");
        return $"Exported {Path.GetFileName(dialog.FileName)} for Joystick Gremlin.";

        void CompleteRouteChanges()
        {
            if (shift is not null)
                _plan.SetGremlinShift(scope, shift.Address, shift.Behavior, baseMode);
            foreach (var (change, device, token) in completed)
                _plan.MarkVirtualRouteRemovalApplied(change, device, token);
        }
    }

    private Dictionary<(Guid DeviceId, string Kind, int InputId, string Mode), GremlinAssignment> ReadGremlinRoutes(
        GremlinProfile profile, IReadOnlyList<VirtualRoute> routes, Func<ControlAddress, string> modeFor, int maxButton)
    {
        var assignments = new Dictionary<(Guid DeviceId, string Kind, int InputId, string Mode), GremlinAssignment>();
        foreach (var route in routes)
        {
            var device = GetLayout(route.Address.Device);
            var (signal, reference) = RequireInput(device, route.Address);
            var output = route.Output!;
            var label = $"{PhysicalLabel(device, route.Address)} · {signal}{(reference ? " (reference input)" : "")}";
            if (signal.Kind is not ("axis" or "button") || output.Kind != signal.Kind ||
                !profile.Outputs.Contains(output) || (output.Kind == "button" && output.InputId > maxButton))
                throw new InvalidOperationException($"{label}: {output} is unavailable for this physical input and game.");
            var source = GremlinDeviceId(profile, device);
            string mode = modeFor(route.Address);
            var key = (source, signal.Kind, signal.Index, mode);
            var assignment = new GremlinAssignment(source, signal.Kind, signal.Index, null, output, Mode: mode, SourceLabel: label);
            if (!assignments.TryAdd(key, assignment) && assignments[key].Output != output)
                throw new InvalidOperationException($"{label}: this physical input has conflicting vJoy routes. Use the same output for every alias.");
        }
        return assignments;
    }

    private (InputSignal Signal, bool Reference) RequireInput(HardwareDefinition device, ControlAddress address)
    {
        var learned = _plan.GetSignal(PhysicalAddress(address, device));
        if (learned is not null)
            return (learned, false);
        if (ReferenceSignal(address, device) is { } signal)
            return (signal, true);
        throw new InvalidOperationException($"{PhysicalLabel(device, address)}: learn the input or set it manually before exporting.");
    }

    private Guid GremlinDeviceId(GremlinProfile profile, HardwareDefinition device, bool registerDevice = true)
    {
        if (ConnectedDevice(device) is { } connected)
        {
            if (registerDevice)
                profile.RegisterDevice(connected.Id, connected.Name);
            return connected.Id;
        }
        var slot = _plan.Devices.FirstOrDefault(item => item.Id == device.Id);
        if (slot?.InstanceId is { } instance)
        {
            if (!registerDevice || profile.Devices.Any(source => source.Id == instance))
                return instance;
            throw new InvalidOperationException($"{device.Name}: connect the paired controller so it can be added to the Gremlin profile.");
        }
        if (_plan.Devices.Count(item => item.LayoutId == device.ModelId) > 1)
            throw new InvalidOperationException($"{device.Name}: connect this controller slot so Gremlin can distinguish it from the other copy.");
        if (FindCustom(device) is { DeviceId: { } id } custom)
        {
            if (registerDevice)
                profile.RegisterDevice(id, custom.Name);
            return id;
        }
        var name = KnownJoystickName(device);
        return (name is null ? null : profile.FindDevice(name))?.Id ??
            throw new InvalidOperationException($"{device.Name}: connect it or choose a Gremlin template that already lists it.");
    }

    private static string PhysicalLabel(HardwareDefinition device, ControlAddress address) =>
        $"{device.Name} / {device.Controls.FirstOrDefault(control => control.Id == address.Control)?.Name ?? address.Control} / {address.Layer}";
}
