using System.Globalization;
using System.IO;
using VisualControlsBinder.Games;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Inputs;

namespace VisualControlsBinder.Planner;

internal sealed record DiagramBinding(string? DeviceId, ControlAddress? Address, string? Input, GameAction? Action,
    bool Reference = false, GremlinOutput? Route = null, BindingActivation Activation = BindingActivation.Default,
    bool Inherited = false, string? ShiftModifier = null, SwgKey? Keyboard = null, string? SourceMode = null)
{
    public string Label => ShiftModifier ?? (Action is null ? Destination ?? "" : Activation == BindingActivation.Default
        ? Action.Name : $"{Action.Name} · {Activation.Label()}");
    public string? InputDetail => Destination is { } target ? $"{Input ?? "Physical input"} → {target}" : Input;
    public string? InheritanceDetail => !Inherited ? null : Address?.Layer == GremlinShiftSetting.Layer
        ? "Inherited from base. Edit in the base layer." : $"Inherited from {SourceMode ?? "the parent mode"}.";
    private string? Destination => Route?.ToString() ?? Keyboard?.ToString();
}
internal sealed record NativeDiagramBinding(string? DeviceId, string? Token, string Input, GameAction Action,
    BindingActivation Activation = BindingActivation.Default);

internal static class DiagramBindings
{
    private sealed record Placement(ControlAddress Address, InputSignal Signal, bool Reference);

    public static IReadOnlyList<DiagramBinding> ForStarCitizen(string gameId, BindingPlan plan,
        IReadOnlyList<HardwareDefinition> hardware, StarCitizenProfile? source, IReadOnlyList<GameAction> catalog,
        Func<HardwareDefinition, int?> slotFor, Func<HardwareDefinition, bool> useReferenceInputs, bool includeStaged = true,
        (string Path, IReadOnlySet<GameBinding> Bindings)? routedSource = null)
    {
        var edits = includeStaged ? plan.Edits.Where(edit => edit.Game == gameId && !edit.ViaVJoy &&
            edit.Address.Layer != GremlinShiftSetting.Layer).ToArray() : [];
        var placements = IndexControls(hardware, plan, useReferenceInputs);
        var slots = source is null ? new Dictionary<int, HardwareDefinition>() : hardware
            .Select(device => (Device: device, Slot: slotFor(device)))
            .Where(item => item.Slot.HasValue)
            .GroupBy(item => item.Slot!.Value)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Device);
        var slotByDevice = slots.ToDictionary(pair => pair.Value.Id, pair => pair.Key, StringComparer.Ordinal);
        var removed = edits.Where(edit => edit.RemoveInput is not null)
            .Select(edit => (edit.Address.Device, edit.Action.Map, edit.Action.Id,
                Input: edit.RemoveInput!.Trim().ToLowerInvariant(), edit.NativeActivation))
            .ToHashSet();
        var actions = catalog.ToLookup(action => (action.Map, action.Id));
        var rows = new List<DiagramBinding>();
        var represented = source is not null && routedSource is { } routed && routed.Bindings.Count > 0 &&
            source.SourcePath.Equals(Path.GetFullPath(routed.Path), StringComparison.OrdinalIgnoreCase) ? routed.Bindings : null;

        foreach (var binding in source?.Bindings ?? [])
        {
            if (represented?.Contains(binding) == true || !TryJoystick(binding.Input, out int slot, out string token))
                continue;

            var device = slots.GetValueOrDefault(slot);
            if (device is not null && removed.Contains((device.Id, binding.Map, binding.Action,
                binding.Input.Trim().ToLowerInvariant(), binding.NativeActivation)))
                continue;
            var placement = device is null ? null : FindPlacement(placements[device.Id], token);
            var action = actions[(binding.Map, binding.Action)].FirstOrDefault() ??
                new GameAction(binding.Map, binding.Action, "", "", binding.Action, StarCitizenGame.IsJoystickAxis(token));
            rows.Add(new DiagramBinding(device?.Id, placement?.Address, binding.Input, action,
                Reference: placement?.Reference == true, Activation: binding.Activation));
        }

        AddStaged(rows, edits, placements, (device, signal) => slotByDevice.TryGetValue(device, out int slot)
            ? $"js{slot}_{signal.Token}" : signal.Token);
        return rows.Distinct().ToArray();
    }

    public static (IReadOnlyList<DiagramBinding> Rows, IReadOnlySet<GameBinding> SavedBindings) ForGremlin(string gameId, BindingPlan plan,
        IReadOnlyList<HardwareDefinition> hardware, IReadOnlyList<GremlinRouteView> routes,
        IReadOnlyDictionary<GremlinOutput, RoutedBindingRead> savedBindings, IReadOnlyList<GameAction> catalog,
        SwgCatalog? swgCatalog, Func<HardwareDefinition, bool> useReferenceInputs,
        Func<ControlAddress, bool> include, Func<BindingEdit, bool>? includeEdit = null)
    {
        var edits = plan.Edits.Where(edit => edit.Game == gameId &&
            (includeEdit?.Invoke(edit) ?? edit.Activation == BindingActivation.Default) && include(edit.Address)).ToArray();
        var placements = IndexControls(hardware, plan, useReferenceInputs);
        var staged = edits.Where(edit => edit.RemoveInput is null)
            .Select(edit => (Edit: edit, Row: StagedBinding(edit, placements, (_, signal) => signal.Token))).ToArray();
        var stagedByAddress = staged.ToLookup(item => item.Edit.Address);
        var rows = staged.Select(item => item.Row).ToList();
        var removed = edits.Where(edit => edit.ViaVJoy && edit.RemoveInput is not null)
            .ToLookup(edit => edit.RoutedOutput);
        var actions = catalog.ToLookup(action => (action.Map, action.Id));
        var represented = new HashSet<GameBinding>();
        foreach (var route in routes)
        {
            if (route.Address is { } address && !include(address))
                continue;

            string? input = route.Signal?.Token;
            if (route.Address is null && route.DeviceName is { } name)
                input = input is null ? name : $"{name} · {input}";
            var binding = new DiagramBinding(route.DeviceId, route.Address, input, Action: null,
                Reference: route.Reference, Route: route.Target.Output, Inherited: route.Inherited,
                Keyboard: route.Target.Keyboard, SourceMode: route.NativeMode);
            var output = route.Target.Output;
            bool hasAction = false;
            if (!route.Inherited && route.Address is not null && stagedByAddress.Contains(route.Address) &&
                (route.Staged || output is not null &&
                    (plan.GetVirtualRoute(gameId, route.Address)?.Output == output ||
                     edits.Any(edit => edit.Address == route.Address && edit.RoutedOutput == output))))
            {
                foreach (var (edit, row) in stagedByAddress[route.Address])
                {
                    if (edit.ViaVJoy && edit.RoutedOutput != output)
                        continue;
                    rows.Remove(row);
                    rows.Add(binding with { Action = edit.Action, Activation = EditActivation(gameId, edit) });
                    hasAction = true;
                }
            }

            if (output is not null && savedBindings.GetValueOrDefault(output) is { Status: null } saved)
            {
                foreach (var prior in saved.Bindings)
                {
                    if (removed[output].Any(edit => edit.Action.Map == prior.Map && edit.Action.Id == prior.Action &&
                        edit.RemoveInput == prior.Input && EditActivation(gameId, edit) == prior.Activation &&
                        edit.NativeActivation == prior.NativeActivation))
                        continue;
                    var action = actions[(prior.Map, prior.Action)].FirstOrDefault() ??
                        new GameAction(prior.Map, prior.Action, "", "", prior.Action, output.Kind == "axis");
                    rows.Add(binding with { Action = action, Activation = prior.Activation });
                    represented.Add(prior);
                    hasAction = true;
                }
            }
            else if (swgCatalog is not null)
            {
                foreach (var action in swgCatalog.GetActionsForTarget(route.Target, output?.DeviceId ?? 1))
                {
                    if (removed[output].Any(edit => edit.Action.Map == action.Map && edit.Action.Id == action.Id))
                        continue;
                    rows.Add(binding with { Action = action });
                    hasAction = true;
                }
            }

            if (!hasAction)
                rows.Add(binding);
        }
        return (rows.Distinct().ToArray(), represented);
    }

    public static IReadOnlyList<DiagramBinding> ForNative(string gameId, BindingPlan plan,
        IReadOnlyList<HardwareDefinition> hardware, IEnumerable<NativeDiagramBinding> current,
        Func<HardwareDefinition, bool> useReferenceInputs, bool includeStaged = true)
    {
        var edits = includeStaged ? plan.Edits.Where(edit => edit.Game == gameId && !edit.ViaVJoy &&
            edit.Address.Layer != GremlinShiftSetting.Layer).ToArray() : [];
        var placed = IndexControls(hardware, plan, useReferenceInputs);
        var removed = edits.Where(edit => edit.RemoveInput is not null)
            .Select(edit => (edit.Address.Device, edit.Action.Map, edit.Action.Id, edit.RemoveInput, Activation: EditActivation(gameId, edit)))
            .ToHashSet();
        var rows = new List<DiagramBinding>();
        foreach (var input in current)
        {
            if (input.DeviceId is { } device && removed.Contains((device, input.Action.Map, input.Action.Id, input.Token, input.Activation)))
                continue;
            var location = input.DeviceId is { } id && input.Token is { } token && placed.TryGetValue(id, out var controls)
                ? FindPlacement(controls, token) : null;
            rows.Add(new DiagramBinding(input.DeviceId, location?.Address, input.Input, input.Action,
                Reference: location?.Reference == true, Activation: input.Activation));
        }
        AddStaged(rows, edits, placed, (_, signal) => signal.Token);
        return rows.Distinct().ToArray();
    }

    private static Dictionary<string, IReadOnlyList<Placement>> IndexControls(
        IReadOnlyList<HardwareDefinition> hardware, BindingPlan plan, Func<HardwareDefinition, bool> useReferenceInputs)
    {
        var learnedByDevice = plan.Learned.ToLookup(item => item.Address.Device, StringComparer.Ordinal);
        return hardware.ToDictionary(device => device.Id, device =>
        {
            string referenceLayer = device.ModelId == "virpil-cm3" ? "Mode 1" : "Normal";
            var controls = device.Controls.ToDictionary(control => control.Id, StringComparer.Ordinal);
            var known = new Dictionary<ControlAddress, Placement>();
            foreach (var item in learnedByDevice[device.Id])
            {
                if (controls.ContainsKey(item.Address.Control) && InputSignal.Parse(item.Token) is { } signal)
                    known.TryAdd(item.Address, new Placement(item.Address, signal, false));
            }
            if (useReferenceInputs(device))
            {
                foreach (var control in device.Controls)
                {
                    var address = new ControlAddress(device.Id, control.Id, referenceLayer);
                    if (InputSignal.Parse(control.SuggestedInput) is { } hint)
                        known.TryAdd(address, new Placement(address, hint, true));
                }
            }
            return (IReadOnlyList<Placement>)known.Values.ToArray();
        }, StringComparer.Ordinal);
    }

    private static Placement? FindPlacement(IReadOnlyList<Placement> placements, string input)
    {
        var matches = placements.Where(item => string.Equals(item.Signal.Token, input, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var learned = matches.Where(item => !item.Reference).ToArray();
        if (learned.Length > 1 && learned.All(item => item.Address.Control == learned[0].Address.Control))
        {
            var address = learned[0].Address with { Layer = string.Join(" / ", learned.Select(item => item.Address.Layer).Distinct()) };
            return learned[0] with { Address = address };
        }
        return learned.Length switch
        {
            1 => learned[0],
            0 when matches.Length == 1 => matches[0],
            _ => null
        };
    }

    private static void AddStaged(List<DiagramBinding> rows, IEnumerable<BindingEdit> edits,
        Dictionary<string, IReadOnlyList<Placement>> placements, Func<string, InputSignal, string> formatInput)
    {
        rows.AddRange(edits.Where(edit => edit.RemoveInput is null).Select(edit => StagedBinding(edit, placements, formatInput)));
    }

    private static DiagramBinding StagedBinding(BindingEdit edit, Dictionary<string, IReadOnlyList<Placement>> placements,
        Func<string, InputSignal, string> formatInput)
    {
        var address = edit.Address;
        var placement = placements.GetValueOrDefault(address.Device)?.FirstOrDefault(item => item.Address == address ||
            address.Layer == GremlinShiftSetting.Layer && item.Address.Control == address.Control &&
            item.Address.Layer is "Normal" or "Mode 1");
        string? input = placement is null ? null : formatInput(address.Device, placement.Signal);
        return new DiagramBinding(address.Device, address, input, edit.Action,
            Reference: placement?.Reference == true, Activation: edit.Activation);
    }

    private static BindingActivation EditActivation(string gameId, BindingEdit edit) =>
        gameId == "war-thunder" && !edit.Action.IsAxis && edit.Activation == BindingActivation.Default
            ? BindingActivation.Tap : edit.Activation;

    private static bool TryJoystick(string input, out int slot, out string token)
    {
        slot = 0;
        token = "";
        int separator = input.IndexOf('_');
        if (separator <= 2 || !input.StartsWith("js", StringComparison.OrdinalIgnoreCase) ||
            !int.TryParse(input.AsSpan(2, separator - 2), NumberStyles.None, CultureInfo.InvariantCulture, out slot) || slot < 1)
            return false;
        token = input[(separator + 1)..].Trim();
        return token.Length > 0;
    }

}
