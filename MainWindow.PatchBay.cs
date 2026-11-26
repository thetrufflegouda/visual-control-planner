using System.Windows;
using System.Windows.Threading;
using VisualControlsBinder.Games;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private VJoyPatchBayWindow? _patchBay;
    private DispatcherOperation? _patchBayRefresh;

    private void OpenPatchBay(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not { } game || OutputFor(game) != OutputMode.JoystickGremlin)
            return;
        if (_patchBay is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }

        var window = new VJoyPatchBayWindow(ApplyPatchBayRoutes) { Owner = this };
        _patchBay = window;
        window.Closed += (_, _) =>
        {
            _patchBayRefresh?.Abort();
            _patchBayRefresh = null;
            _patchBay = null;
        };
        RefreshPatchBay(window);
        window.Show();
    }

    private void QueuePatchBayRefresh()
    {
        if (_patchBay is not { } window || _patchBayRefresh is not null)
            return;
        _patchBayRefresh = Dispatcher.InvokeAsync(() =>
        {
            _patchBayRefresh = null;
            if (ReferenceEquals(window, _patchBay))
                RefreshPatchBay(window);
        }, DispatcherPriority.Background);
    }

    private void RefreshPatchBay(VJoyPatchBayWindow window)
    {
        if (SelectedGame is not { } game || OutputFor(game) != OutputMode.JoystickGremlin)
        {
            window.UpdatePlan(null, "Select Joystick Gremlin as the output to edit routes.", [], [], []);
            return;
        }

        string scope = ScopeOf(game);
        var hardware = PlannedLayouts();
        var devices = hardware.Select(device => new VJoyPatchDevice(device.Id, device.Name,
            string.Join('|', InputLayers(device)))).ToArray();
        var routes = _plan.VirtualRoutes.Where(route => route.Game == scope)
            .DistinctBy(route => route.Address).ToDictionary(route => route.Address);
        var savedRoutes = PatchBayRoutes(game, hardware);
        var ports = new List<VJoyPatchPort>();
        var shift = ActiveGremlinShift;
        foreach (var device in hardware)
        {
            foreach (string layer in InputLayers(device))
            {
                if (shift is { Behavior: { } behavior, Address.Control.Length: 0 } && shift.Address.Device == device.Id &&
                    (layer == shift.Address.Layer || layer == GremlinShiftLayer) && _savedGremlinModifier is { } source)
                    ports.Add(new VJoyPatchPort(shift.Address with { Layer = layer }, $"Button {source.Button}", "button",
                        InputSignal.Button(source.Button), false, null, Modifier: ShiftModifierLabel(behavior)));
                foreach (var control in device.Controls)
                {
                    var address = new ControlAddress(device.Id, control.Id, layer);
                    var reserved = IsGremlinModifier(address);
                    string? modifier = null;
                    if (reserved)
                    {
                        if (shift is not { Behavior: { } modifierBehavior } || (address != shift.Address &&
                            (layer != GremlinShiftLayer || address.Control != shift.Address.Control)))
                            continue;
                        modifier = ShiftModifierLabel(modifierBehavior);
                    }
                    var learned = _plan.GetSignal(PhysicalAddress(address, device));
                    var signal = learned ?? ReferenceSignal(address, device);
                    string kind = signal?.Kind ?? (control.Kind.Equals("axis", StringComparison.OrdinalIgnoreCase) ? "axis" : "button");
                    if (kind is not ("button" or "axis"))
                        continue;

                    GremlinRouteView[] projected = reserved ? [] : savedRoutes[address].ToArray();
                    bool inherited = projected.Length > 0 && projected.All(row => row.Inherited);
                    VirtualRoute? route = null;
                    string? otherTargets = null;
                    if (projected is [{ Target.Output: { } output }])
                        route = new VirtualRoute(scope, address, output.DeviceId, output);
                    else if (projected.Length > 0)
                        otherTargets = string.Join(", ", projected.Select(row =>
                            row.Target.Output?.ToString() ?? row.Target.Keyboard?.ToString()).Distinct());
                    else if (!reserved && routes.GetValueOrDefault(address) is { Output: null } placeholder)
                        route = placeholder;
                    ports.Add(new VJoyPatchPort(address, control.Name, kind, signal, learned is null && signal is not null,
                        route, inherited, otherTargets, modifier));
                }
            }
        }
        window.UpdatePlan(scope, ActiveGameLabel(game), devices, ports.ToArray(), PatchBayOutputs(game),
            CurrentAddress ?? (_hardware is { } selected ? new ControlAddress(selected.Id, "", LayerPicker.SelectedItem as string ?? "Normal") : null),
            GremlinShiftDescription);
    }

    private static string ShiftModifierLabel(GremlinShiftBehavior behavior) =>
        behavior == GremlinShiftBehavior.Toggle ? "GREMLIN SHIFT TOGGLE" : "GREMLIN SHIFT HOLD";

    private ILookup<ControlAddress?, GremlinRouteView> PatchBayRoutes(GameTarget game, IReadOnlyList<HardwareDefinition> hardware) =>
        ReadGremlinRoutesForView(game, hardware).Where(row => row.Address is not null)
            .DistinctBy(row => (row.Address, row.Target)).ToLookup(row => row.Address);

    private GremlinOutput[] PatchBayOutputs(GameTarget game) => (VJoyOutputs.Read() ?? _profiles.Gremlin?.ReferencedOutputs ?? [])
        .Where(output => output.DeviceId is >= 1 and <= 16 && output.InputId > 0 &&
            (output.Kind == "axis" && output.InputId <= 8 || output.Kind == "button" && output.InputId <= GremlinButtonLimit(game)))
        .ToArray();

    private string? ApplyPatchBayRoutes(string scope, IReadOnlyList<ControlAddress> addresses, GremlinOutput? output)
    {
        if (SelectedGame is not { } game || OutputFor(game) != OutputMode.JoystickGremlin || ScopeOf(game) != scope)
            return "The active game changed. Select a physical input in the current game.";
        var hardware = PlannedLayouts();
        var layouts = hardware.ToDictionary(layout => layout.Id);
        foreach (var address in addresses)
        {
            if (!layouts.TryGetValue(address.Device, out var device) ||
                !device.Controls.Any(control => control.Id == address.Control) || !InputLayers(device).Contains(address.Layer) ||
                IsGremlinModifier(address))
                return "A selected input is no longer in the plan.";
        }
        var projected = PatchBayRoutes(game, hardware);
        if (output is not null)
        {
            if (addresses.Count != 1)
                return "Choose one physical input to connect.";
            var address = addresses[0];
            var signal = KnownSignal(address, layouts[address.Device]);
            if (signal?.Kind != output.Kind || !PatchBayOutputs(game).Contains(output))
                return "Choose an available vJoy output of the same kind.";
            var current = projected[address].Where(row => !row.Inherited).ToArray();
            if (current.Length > 1 || current.Any(row => row.Target.Keyboard is not null))
                return "Edit this input's saved routes in Joystick Gremlin.";
            SetVirtualRouteForPhysicalInput(game, address, output.DeviceId, output);
        }
        else
        {
            foreach (var address in addresses)
            {
                if (projected[address].ToArray() is not [{ Inherited: false, Target.Output: { } target } route])
                    continue;
                var signal = route.Signal;
                bool removedPlanRoute = false;
                foreach (var alias in VJoyRouteAliases(address, signal))
                    if (_plan.GetVirtualRoute(scope, alias)?.Output == target)
                        removedPlanRoute |= _plan.RemoveVirtualRoute(scope, alias, signal);
                if (!removedPlanRoute && (signal is null || !StageSavedRouteRemoval(game, address, signal, target)))
                    return "The saved route could not be identified. Reopen the selected Gremlin profile.";
            }
            QueuePatchBayRefresh();
        }
        UpdateControl();
        return null;
    }

    private IEnumerable<ControlAddress> VJoyRouteAliases(ControlAddress address, InputSignal? signal)
    {
        var device = FindPlannedLayout(address.Device);
        if (device is null || signal is not { Kind: "button" or "axis" })
            return [address];
        return InputLayers(device).Where(layer => (layer == GremlinShiftLayer) == (address.Layer == GremlinShiftLayer))
            .SelectMany(layer => device.Controls.Select(control => new ControlAddress(device.Id, control.Id, layer)))
            .Where(candidate => !IsGremlinModifier(candidate) && KnownSignal(candidate, device) == signal)
            .Append(address).Distinct();
    }
}
