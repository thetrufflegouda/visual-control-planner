using VisualControlsBinder.Games;
using VisualControlsBinder.Inputs;

namespace VisualControlsBinder.Planner;

internal sealed record VJoyPhysicalInput(ControlAddress Address, InputSignal Signal, string Label, int DeviceOrder, int LayerOrder);

internal sealed record VJoyInputRoute(VJoyPhysicalInput Input, GremlinOutput Output, IReadOnlyList<ControlAddress> Aliases);

internal sealed record VJoyInputPlan(IReadOnlyList<VJoyInputRoute> Routes, IReadOnlyList<VJoyPhysicalInput> Unmapped, int KeptAxes);

internal static class VJoyInputMap
{
    public static VJoyInputPlan Plan(IEnumerable<VJoyPhysicalInput> source, IEnumerable<GremlinOutput> outputs,
        int startingDevice, IReadOnlyList<GremlinRouteView> existing)
    {
        var capacity = outputs.Where(output => output.DeviceId >= startingDevice)
            .Distinct().OrderBy(output => output.DeviceId).ThenBy(output => output.InputId).ToArray();
        var existingAxes = existing.Where(route => route.Target.Output is { Kind: "axis" }).ToArray();
        var assigned = existingAxes.Select(route => route.Address).ToHashSet();
        var occupied = existingAxes.Select(route => route.Target.Output!).ToHashSet();
        var buttons = new Queue<GremlinOutput>(capacity.Where(output => output.Kind == "button"));
        var axes = capacity.Where(output => output.Kind == "axis" && !occupied.Contains(output)).ToList();
        var ordered = source.OrderBy(input => input.DeviceOrder).ThenBy(input => input.LayerOrder)
            .ThenBy(input => input.Signal.Index).GroupBy(input => (input.Address.Device, input.Signal.Kind, input.Signal.Index));
        var routes = new List<VJoyInputRoute>();
        var unmapped = new List<VJoyPhysicalInput>();
        int keptAxes = 0;
        foreach (var group in ordered)
        {
            var input = group.First();
            var addresses = group.Select(item => item.Address).Distinct().ToArray();
            bool axis = input.Signal.Kind == "axis";
            if (axis && addresses.Any(assigned.Contains))
            {
                keptAxes++;
                continue;
            }
            var output = axis ? OrderOutputs(axes, input.Signal).FirstOrDefault()
                : buttons.TryDequeue(out var button) ? button : null;
            if (output is null)
                unmapped.Add(input);
            else
            {
                routes.Add(new VJoyInputRoute(input, output, addresses));
                if (axis)
                    axes.Remove(output);
            }
        }
        return new VJoyInputPlan(routes, unmapped, keptAxes);
    }

    internal static IOrderedEnumerable<GremlinOutput> OrderOutputs(IEnumerable<GremlinOutput> outputs, InputSignal signal) =>
        outputs.OrderBy(output => output.DeviceId)
            .ThenBy(output => signal.Kind == "axis" && output.InputId != signal.Index)
            .ThenBy(output => output.InputId);
}
