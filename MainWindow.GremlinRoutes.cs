using VisualControlsBinder.Games;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private IReadOnlyList<GremlinRouteView> ReadGremlinRoutesForView(GameTarget game, IReadOnlyList<HardwareDefinition> layouts) =>
        ProjectGremlinRoutes(game, layouts, only: null);

    private IReadOnlyList<GremlinRouteView> ControlRoutes(GameTarget game, ControlAddress address, bool savedOnly = false) =>
        ProjectGremlinRoutes(game, PlannedLayouts(), address, savedOnly);

    private IReadOnlyList<GremlinRouteView> ProjectGremlinRoutes(GameTarget game,
        IReadOnlyList<HardwareDefinition> layouts, ControlAddress? only, bool savedOnly = false)
    {
        string scope = ScopeOf(game);
        var planRoutes = _plan.VirtualRoutes.Where(route => !savedOnly && route.Game == scope && route.Output is not null)
            .DistinctBy(route => route.Address).ToDictionary(route => route.Address, route => route.Output!);
        var removals = (_plan.RemovedVirtualRoutes ?? []).Where(item => !savedOnly && item.Game == scope && !item.Applied)
            .ToLookup(item => item.Address.Device);
        var profile = _profiles.Gremlin;
        string? baseMode = profile is null ? null : PreferredGremlinBase(game, profile);
        bool shiftEnabled = ActiveGremlinShift?.Behavior is not null;
        string? childMode = shiftEnabled && baseMode is not null ? profile?.ShiftModeForBase(baseMode) : null;
        var baseChain = profile is not null && baseMode is not null ? profile.ModeChain(baseMode).ToArray() : [];
        var saved = (profile?.Devices ?? []).SelectMany(device => profile!.ReadInputOverrides(device.Id))
            .ToLookup(input => (input.DeviceId, input.Kind, input.InputId, input.Mode));
        var savedSources = layouts.Select(device => (Device: device, Source: DiagramGremlinDevice(device)))
            .Where(item => item.Source.HasValue).GroupBy(item => item.Source!.Value)
            .Where(group => group.Count() == 1).ToDictionary(group => group.Single().Device.Id, group => group.Key);
        var layoutIds = savedSources.ToDictionary(pair => pair.Value, pair => pair.Key);
        var placed = new HashSet<(Guid DeviceId, string Kind, int InputId, string Mode)>();
        var rows = new List<GremlinRouteView>();
        foreach (var layout in layouts)
        {
            if (only is not null && only.Device != layout.Id)
                continue;
            Guid source = savedSources.GetValueOrDefault(layout.Id);
            foreach (string layer in InputLayers(layout))
            foreach (var control in layout.Controls)
            {
                var address = new ControlAddress(layout.Id, control.Id, layer);
                if (only is not null && address != only)
                    continue;
                var physical = PhysicalAddress(address, layout);
                var learned = _plan.GetSignal(physical);
                var signal = learned ?? ReferenceSignal(physical, layout);
                bool reserve = IsGremlinModifier(address);
                bool shifted = layer == GremlinShiftLayer;
                if (signal is not null && source != Guid.Empty)
                {
                    foreach (string mode in baseChain)
                        placed.Add((source, signal.Kind, signal.Index, mode));
                    if (shifted && childMode is not null)
                        placed.Add((source, signal.Kind, signal.Index, childMode));
                }
                if (reserve)
                    continue;

                if (planRoutes.TryGetValue(address, out var staged))
                    Add(new SwgTarget(staged, null), inherited: false, staged: true, shifted ? childMode : baseMode);
                else if (shifted && signal is not null && childMode is not null &&
                    saved.Contains((source, signal.Kind, signal.Index, childMode)))
                    ReadSaved(address, [childMode], isShiftInheritance: false);
                else if (shifted && planRoutes.TryGetValue(physical, out var baseOutput))
                    Add(new SwgTarget(baseOutput, null), inherited: true, staged: true, baseMode);
                else if (signal is not null)
                    ReadSaved(physical, baseChain, shifted);

                void ReadSaved(ControlAddress origin, IEnumerable<string> modes, bool isShiftInheritance)
                {
                    var key = modes.Select(mode => (source, signal!.Kind, signal.Index, mode)).FirstOrDefault(saved.Contains);
                    if (key == default)
                        return;
                    foreach (var target in saved[key].SelectMany(input => input.Targets).Distinct())
                        if (!removals[layout.Id].Any(removal => Matches(removal, origin, target, signal!, source, key.mode)))
                            Add(target, isShiftInheritance || key.mode != baseMode && key.mode != childMode, staged: false, key.mode);
                }

                void Add(SwgTarget target, bool inherited, bool staged, string? mode) => rows.Add(new GremlinRouteView(
                    layout.Id, address, signal, target, inherited, staged, learned is null && signal is not null, layout.Name, mode));
            }
        }

        if (only is not null)
            return rows;

        foreach (var device in profile?.Devices ?? [])
        foreach (var input in profile!.GetBoundInputs(device.Id))
        {
            var key = (device.Id, input.Kind, input.InputId, input.Mode);
            if (input.Mode != baseMode && input.Mode != childMode || placed.Contains(key) ||
                _savedGremlinModifier is { } modifier && modifier.Device == device.Id && input.Kind == "button" && input.InputId == modifier.Button)
                continue;
            var signal = input.Kind switch
            {
                "button" when input.InputId > 0 => InputSignal.Button(input.InputId),
                "axis" when input.InputId is >= 1 and <= 8 => InputSignal.Axis(input.InputId),
                _ => null
            };
            string label = input.Mode == childMode ? $"{device.Name} · Gremlin Shift" : device.Name;
            string? layoutId = layoutIds.GetValueOrDefault(device.Id);
            foreach (var target in input.Targets)
            {
                rows.Add(new GremlinRouteView(layoutId, null, signal, target, false, false, false, label, input.Mode));
                if (input.Mode == baseMode && shiftEnabled &&
                    (childMode is null || !saved.Contains((device.Id, input.Kind, input.InputId, childMode))))
                    rows.Add(new GremlinRouteView(layoutId, null, signal, target, true, false, false, $"{device.Name} · Gremlin Shift", input.Mode));
            }
        }
        return rows;

        static bool Matches(VirtualRouteRemoval removal, ControlAddress origin, SwgTarget target, InputSignal signal, Guid source, string mode) =>
            removal.Output == target.Output && (removal.InputToken is null || removal.InputToken == signal.Token) &&
            (removal.InstanceId is null || removal.InstanceId == source) &&
            (removal.NativeMode is { } exactMode ? exactMode == mode : removal.Address == origin ||
                removal.InputToken is not null && (removal.Address.Layer == GremlinShiftLayer) == (origin.Layer == GremlinShiftLayer));
    }

    private bool StageSavedRouteRemoval(GameTarget game, ControlAddress address, InputSignal signal, GremlinOutput output)
    {
        var layouts = PlannedLayouts();
        var device = layouts.FirstOrDefault(layout => layout.Id == address.Device);
        if (device is null || _profiles.Gremlin is not { } profile || DiagramGremlinDevice(device) is not { } source)
            return false;
        var baseMode = PreferredGremlinBase(game, profile);
        var mode = address.Layer == GremlinShiftLayer && baseMode is not null ? profile.ShiftModeForBase(baseMode) : baseMode;
        if (mode is null || !profile.ContainsRoute(new GremlinRouteRemoval(source, signal.Kind, signal.Index, mode, output)))
            return false;
        _plan.AddVirtualRouteRemoval(new VirtualRouteRemoval(ScopeOf(game), address, output, signal.Token, source) { NativeMode = mode });
        return true;
    }
}
