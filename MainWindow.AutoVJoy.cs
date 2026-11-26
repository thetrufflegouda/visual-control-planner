using System.Text;
using System.Windows;
using VisualControlsBinder.Games;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private static string[] HardwareLayers(HardwareDefinition? hardware) => hardware?.ModelId == "virpil-cm3"
        ? ["Mode 1", "Mode 2", "Mode 3", "Mode 4", "Mode 5"] : ["Normal", "Shift"];

    private string[] InputLayers(HardwareDefinition? hardware) => ActiveGremlinShift is { Behavior: not null }
        ? [.. HardwareLayers(hardware), GremlinShiftLayer] : HardwareLayers(hardware);

    private void AutoMapInputs(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not { } game || OutputFor(game) != OutputMode.JoystickGremlin)
            return;
        try
        {
            var layouts = PlannedLayouts();
            var inputs = KnownVJoyInputs(layouts).ToArray();
            if (inputs.Length == 0)
            {
                Notify("Learn a controller button or axis before auto-mapping.");
                return;
            }
            var outputs = VJoyOutputs.Read()?.Where(output => output.Kind == "axis" ||
                output.Kind == "button" && output.InputId <= GremlinButtonLimit(game)).ToArray() ?? [];
            var devices = outputs.GroupBy(output => output.DeviceId).OrderBy(group => group.Key)
                .Select(group => new AutoMapStartingDevice(group.Key, group.Count(output => output.Kind == "button"),
                    group.Count(output => output.Kind == "axis"))).ToArray();
            if (devices.Length == 0)
            {
                Notify("Configure vJoy button or axis outputs before auto-mapping.");
                return;
            }
            int selected = (VJoyDevicePicker.SelectedItem as VirtualDeviceChoice)?.Id ?? 0;
            if (devices.Length == 1)
                selected = devices[0].Id;
            else if (devices.Length > 1 && !devices.Any(device => device.Id == selected))
            {
                if (ReviewWindow.Pick(this, "Start auto-mapping at", devices) is not { } start)
                    return;
                selected = start.Id;
            }

            var existing = ReadGremlinRoutesForView(game, layouts);
            if (game.IsSwg)
                outputs = outputs.Where(output => output.DeviceId == selected).ToArray();
            var (routes, unmapped, keptAxes) = VJoyInputMap.Plan(inputs, outputs, selected, existing);
            string counts = $"{routes.Count} mapped · {unmapped.Count} unmapped" +
                (keptAxes > 0 ? $" · {keptAxes} existing axis routes kept" : "");
            if (routes.Count == 0)
            {
                Notify($"Auto-map · {counts}.");
                return;
            }
            var preview = new StringBuilder(counts).AppendLine();
            foreach (var group in routes.GroupBy(route => route.Output.DeviceId))
            {
                preview.AppendLine($"vJoy {group.Key} · {group.Count(route => route.Output.Kind == "button")} buttons · " +
                    $"{group.Count(route => route.Output.Kind == "axis")} axes");
            }
            preview.AppendLine().AppendLine("Auto-map replaces button routes on mapped controls and fills available axes.")
                .AppendLine();
            foreach (var route in routes)
                preview.AppendLine($"{route.Input.Label} · {route.Input.Signal} → {route.Output}");
            if (unmapped.Count > 0)
            {
                preview.AppendLine().AppendLine("No compatible vJoy output was available for these inputs:");
                foreach (var input in unmapped)
                    preview.AppendLine($"{input.Label} · {input.Signal}");
            }
            if (!ReviewWindow.Confirm(this, "Auto-map inputs", preview.ToString(), "Apply routes"))
                return;

            string scope = ScopeOf(game);
            foreach (var route in routes)
                foreach (var address in route.Aliases)
                    _plan.SetVirtualRoute(scope, address, route.Output.DeviceId, route.Output);
            UpdateControl(revealAssignment: true);
            Notify($"Auto-map · {counts}. Preview / export to save the routes.");
        }
        catch (Exception error) { Notify(error.Message); }
    }

    private IEnumerable<VJoyPhysicalInput> KnownVJoyInputs(IReadOnlyList<HardwareDefinition> layouts)
    {
        for (int device = 0; device < layouts.Count; device++)
        {
            var layout = layouts[device];
            var layers = HardwareLayers(layout);
            for (int layer = 0; layer < layers.Length; layer++)
                foreach (var control in layout.Controls)
                {
                    var address = new ControlAddress(layout.Id, control.Id, layers[layer]);
                    if (!IsGremlinModifier(address) &&
                        KnownSignal(address, layout) is { Kind: "button" or "axis" } signal)
                        yield return new VJoyPhysicalInput(address, signal, PhysicalLabel(layout, address), device, layer);
                }
        }
    }

    private static int GremlinButtonLimit(GameTarget game) => game.IsSwg ? 32 : 128;

    private sealed record AutoMapStartingDevice(int Id, int ButtonCount, int AxisCount)
    {
        public override string ToString() => $"vJoy {Id} · {ButtonCount} buttons · {AxisCount} axes";
    }
}
