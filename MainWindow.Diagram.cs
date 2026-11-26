using System.IO;
using System.Windows;
using System.Windows.Threading;
using VisualControlsBinder.Games;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private BindingDiagramWindow? _bindingDiagram;
    private DispatcherOperation? _diagramRefresh;
    private bool _readingDiagramSources;

    private void OpenDiagram(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not { } game)
            return;
        if (_bindingDiagram is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
                open.WindowState = WindowState.Normal;
            open.Activate();
            return;
        }

        try
        {
            var window = new BindingDiagramWindow(LoadPhoto) { Owner = this };
            RefreshDiagram(window, game);
            window.Closed += DiagramClosed;
            window.Show();
            _bindingDiagram = window;
        }
        catch (Exception error)
        {
            Notify(error.Message);
        }
    }

    private void DiagramClosed(object? sender, EventArgs e)
    {
        if (!ReferenceEquals(sender, _bindingDiagram))
            return;
        _bindingDiagram = null;
        _diagramRefresh?.Abort();
        _diagramRefresh = null;
    }

    private void QueueDiagramRefresh()
    {
        if (_readingDiagramSources || _bindingDiagram is not { } window || _diagramRefresh is not null)
            return;
        _diagramRefresh = Dispatcher.InvokeAsync(() =>
        {
            _diagramRefresh = null;
            if (_readingDiagramSources || !ReferenceEquals(window, _bindingDiagram) || SelectedGame is not { } game)
                return;
            try { RefreshDiagram(window, game); }
            catch (Exception error) { Notify(error.Message); }
        }, DispatcherPriority.Background);
    }

    private void RefreshDiagram(BindingDiagramWindow window, GameTarget game)
    {
        string scope = ScopeOf(game);
        var hardware = PlannedLayouts();
        bool gremlin = OutputFor(game) == OutputMode.JoystickGremlin;
        bool IncludeGremlinAddress(ControlAddress address) => !IsGremlinModifier(address) &&
            (address.Layer != GremlinShiftLayer || ActiveGremlinShift is { Behavior: not null });
        var bindings = gremlin ? [] : NativeBindings();
        string? shiftDescription = null;
        if (gremlin)
        {
            bool supportsRouted = SupportsRoutedGameBindings(game);
            var routedProfile = supportsRouted ? RoutedGameProfilePath(game) : null;
            var routes = ReadGremlinRoutesForView(game, hardware);
            var outputs = supportsRouted ? routes.Select(route => route.Target.Output).OfType<GremlinOutput>().Distinct().ToArray() : [];
            var saved = ReadRoutedGameBindings(game, routedProfile, outputs,
                outputs.Select(output => output.DeviceId).Distinct().ToArray());
            var (routedRows, represented) = DiagramBindings.ForGremlin(scope, _plan, hardware, routes, saved, _actions,
                _profiles.SwgCatalog, CanUseReferenceInputs, IncludeGremlinAddress, edit => EditForOutput(game, edit) &&
                    (edit.RoutedProfile is null || SameRoutedProfile(game, edit.RoutedProfile, routedProfile)));
            bindings = NativeBindings(routedProfile is null ? null : (routedProfile, represented))
                .Where(row => row.Address is null || IncludeGremlinAddress(row.Address)).Concat(routedRows).ToArray();
            shiftDescription = GremlinShiftDescription;
            if (ActiveGremlinShift is { Behavior: { } behavior, Address: { } address } &&
                hardware.FirstOrDefault(device => device.Id == address.Device) is { } source &&
                source.Controls.Any(control => control.Id == address.Control))
            {
                string label = behavior == GremlinShiftBehavior.Momentary ? "GREMLIN SHIFT HOLD" : "GREMLIN SHIFT TOGGLE";
                bindings = [.. bindings, new DiagramBinding(source.Id, address, KnownSignal(address, source)?.ToString(),
                    Action: null, ShiftModifier: label)];
            }
        }
        var devices = hardware.Select(device => new DiagramDevice(device, FindCustom(device))).ToArray();
        string? path = game.Kind == GameKind.Dcs ? null : CurrentProfilePath(game);
        string subtitle = path is not null && File.Exists(path)
            ? $"{ActiveGameLabel(game)} · {FriendlyProfile(path)}" : ActiveGameLabel(game);
        window.UpdatePlan(scope, subtitle, devices, bindings, _hardware?.Id, shiftDescription);

        IReadOnlyList<DiagramBinding> NativeBindings((string Path, IReadOnlySet<GameBinding> Bindings)? routedSource = null) =>
            game.IsStarCitizen
                ? DiagramBindings.ForStarCitizen(scope, _plan, hardware, _profiles.StarCitizen, _actions,
                    device => _profiles.StarCitizen is { } profile ? FindSlot(profile, device) : null,
                    CanUseReferenceInputs, includeStaged: !gremlin, routedSource: routedSource)
                : game.Kind == GameKind.Dcs || !gremlin
                    ? DiagramBindings.ForNative(scope, _plan, hardware, NativeDiagramRows(game, hardware),
                        CanUseReferenceInputs, includeStaged: !gremlin)
                    : [];
    }

    private IEnumerable<NativeDiagramBinding> NativeDiagramRows(GameTarget game, IReadOnlyList<HardwareDefinition> hardware)
    {
        if (game.Kind == GameKind.Dcs && _aircraft is { } module)
        {
            foreach (var device in hardware)
                if (DcsProfileFor(module, device) is { } profile)
                    foreach (var binding in DcsSavedRows(module, device, profile))
                        yield return binding;
            yield break;
        }

        var actions = _actions.ToLookup(action => (action.Map, action.Id));
        GameAction Resolve(string map, string id, bool axis) => actions[(map, id)].FirstOrDefault()
            ?? new GameAction(map, id, "", "", id, axis);

        if (_profiles.Squadrons is { } squadrons)
        {
            var layouts = hardware.ToLookup(layout => ConnectedDevice(layout) is { } controller
                ? squadrons.FindDeviceSlot(controller.Name) : null);
            foreach (var binding in _profiles.SquadronsBindings)
            {
                var signal = InputSignal.Parse(binding.Token);
                yield return new NativeDiagramBinding(Single(layouts[binding.DeviceSlot])?.Id, binding.Token,
                    signal?.ToString() ?? binding.Token, Resolve(binding.Map, binding.Action, signal?.Kind == "axis"));
            }
        }
        if (_profiles.Cliffs is { } cliffs)
        {
            var layouts = hardware.ToLookup(layout => ConnectedDevice(layout) is { } controller
                ? GetCliffsSlot(cliffs, controller)?.ToUpperInvariant() : null);
            foreach (var binding in cliffs.Bindings)
            {
                if (CliffsBindingProfile.DecodeInput(binding.Input) is not { } input)
                    continue;
                yield return new NativeDiagramBinding(Single(layouts[input.Slot.ToUpperInvariant()])?.Id, input.Signal.Token,
                    binding.Input, Resolve(binding.Map, binding.Action, input.Signal.Kind == "axis"));
            }
        }
        if (_profiles.Msfs.Length > 0 && game.IsMsfs)
        {
            foreach (var layout in hardware)
            {
                if (ConnectedDevice(layout) is not { } controller || UniqueMsfsProfile(game, controller) is not { } profile)
                    continue;
                foreach (var binding in profile.Bindings)
                {
                    var signal = InputSignal.Parse(binding.Input);
                    yield return new NativeDiagramBinding(layout.Id, binding.Input, signal?.ToString() ?? binding.Input,
                        Resolve(binding.Map, binding.Action, signal?.Kind == "axis"));
                }
            }
        }

        if (_profiles.Il2Slots is { } slots)
        {
            var layouts = hardware.ToLookup(device => FindIl2Slot(slots, device));
            foreach (var binding in _profiles.Il2Bindings)
            {
                if (Il2JoystickSlots.Decode(binding.Input) is not { } source)
                    continue;
                var device = Single(layouts[source.Slot]);
                yield return new NativeDiagramBinding(device?.Id, source.Signal.Token, binding.Input,
                    Resolve(binding.Map, binding.Action, source.Signal.Kind == "axis"));
            }
        }
        if (_profiles.SwgNative is { } swg)
        {
            var device = _plan.NativeJoysticks.GetValueOrDefault(game.Id);
            foreach (var binding in swg.Bindings)
            {
                var signal = binding.Kind == "axis" ? InputSignal.Axis(binding.Index) : InputSignal.Button(binding.Index);
                yield return new NativeDiagramBinding(device, signal.Token, signal.ToString(),
                    Resolve(binding.Map, binding.Action, binding.Kind == "axis"));
            }
        }
        if (_profiles.Elite is { } elite)
        {
            var joystickKeys = Enumerable.Range(1, 128).Select(InputSignal.Button)
                .Concat(Enumerable.Range(1, 8).Select(InputSignal.Axis))
                .Concat(Enumerable.Range(1, 4).SelectMany(index =>
                    new[] { "up", "right", "down", "left" }.Select(direction => InputSignal.Hat(index, direction))))
                .ToDictionary(signal => EliteDangerousGame.JoystickKey(signal.Kind, signal.Index, signal.Direction));
            foreach (var binding in elite.Bindings.Where(binding => binding.Key.StartsWith("Joy_", StringComparison.Ordinal)))
            {
                var id = Single(hardware.Where(device => ProductGuid(device) is { } product &&
                    EliteDeviceId(product).Equals(binding.Device, StringComparison.OrdinalIgnoreCase)))?.Id;
                var signal = joystickKeys.GetValueOrDefault(binding.Key);
                yield return new NativeDiagramBinding(id, binding.HasModifiers ? null : signal?.Token, binding.Key,
                    Resolve("Controls", binding.Action, signal?.Kind == "axis"));
            }
        }
        if (_profiles.WarThunder is { } thunder)
        {
            foreach (var binding in thunder.Bindings)
            {
                var offset = binding.Input.LastIndexOf(':');
                bool axis = binding.Map == "axes";
                int index = offset >= 0 && int.TryParse(binding.Input.AsSpan(offset + 1), out int parsed) ? parsed : -1;
                var source = index < 0 ? null : Single(thunder.Devices.Where(device => axis
                    ? device.DirectInputAxis(index) is not null
                    : device.ButtonsOffset >= 0 && index >= device.ButtonsOffset && index - device.ButtonsOffset < device.ButtonsCount));
                var device = source is null ? null : Single(hardware.Where(item =>
                    ControllerProductIds(item) == (source.VendorId, source.ProductId)));
                var signal = source is null ? null : axis && source.DirectInputAxis(index) is { } axisIndex
                    ? InputSignal.Axis(axisIndex) : InputSignal.Button(index - source.ButtonsOffset + 1);
                yield return new NativeDiagramBinding(device?.Id, signal?.Token, binding.Input,
                    Resolve(binding.Map, binding.Action, binding.Map == "axes"), binding.Activation);
            }
        }
    }

    private Guid? DiagramGremlinDevice(HardwareDefinition hardware)
    {
        if (ConnectedDevice(hardware) is { } connected)
            return connected.Id;
        var profile = _profiles.Gremlin;
        if (_plan.Devices.FirstOrDefault(slot => slot.Id == hardware.Id)?.InstanceId is { } instance &&
            profile?.Devices.Any(device => device.Id == instance) == true)
            return instance;
        if (FindCustom(hardware) is { DeviceId: { } customId })
            return customId;
        var name = KnownJoystickName(hardware);
        return name is null ? null : profile?.FindDevice(name)?.Id;
    }

    private static string? KnownJoystickName(HardwareDefinition hardware) => hardware.ModelId switch
        {
            "vkb-left" => "VKBsim Space Gunfighter L",
            "vkb-right" => "VKBsim Space Gunfighter",
            "virpil-cm3" => "L-VPC Throttle MT-50CM3",
            _ => null
        };
}
