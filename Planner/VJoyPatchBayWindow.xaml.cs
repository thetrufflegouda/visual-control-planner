using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using VisualControlsBinder.Games;
using VisualControlsBinder.Inputs;

namespace VisualControlsBinder.Planner;

internal sealed record VJoyPatchDevice(string Id, string Name, string Layers);
internal sealed record VJoyPatchPort(ControlAddress Address, string Label, string Kind, InputSignal? Signal,
    bool Reference, VirtualRoute? Route, bool Inherited = false, string? OtherTargets = null, string? Modifier = null)
{
    public bool HasSocket => Modifier is null && (OtherTargets is null || Inherited);
    public bool CanRemove => Route?.Output is not null && !Inherited;
}

public partial class VJoyPatchBayWindow : Window
{
    private readonly Func<string, IReadOnlyList<ControlAddress>, GremlinOutput?, string?> _apply;
    private static readonly string[] Kinds = ["Buttons", "Axes"];
    private VJoyPatchDevice[] _devices = [];
    private VJoyPatchPort[] _ports = [];
    private GremlinOutput[] _capacity = [];
    private readonly Dictionary<ControlAddress, (VJoyPatchPort Port, PortVisual Visual)> _inputRows = [];
    private readonly Dictionary<GremlinOutput, PortVisual> _outputRows = [];
    private readonly HashSet<ControlAddress> _selection = [];
    private ControlAddress? _selectionAnchor;
    private VJoyPatchPort[] _visiblePorts = [];
    private string? _scope;
    private bool _refreshing = true;
    private DispatcherOperation? _wireRefresh;

    internal VJoyPatchBayWindow(Func<string, IReadOnlyList<ControlAddress>, GremlinOutput?, string?> apply)
    {
        _apply = apply;
        InitializeComponent();
        KindPicker.ItemsSource = Kinds;
        KindPicker.SelectedIndex = 0;
        _refreshing = false;
    }

    internal void UpdatePlan(string? scope, string caption, VJoyPatchDevice[] devices, VJoyPatchPort[] ports,
        GremlinOutput[] capacity, ControlAddress? selection = null, string? shiftDescription = null)
    {
        bool changedScope = scope != _scope || GameCaption.Text != caption;
        if (!changedScope && _devices.SequenceEqual(devices) && _ports.SequenceEqual(ports) &&
            _capacity.SequenceEqual(capacity) && GremlinShiftCaption.Text == (shiftDescription ?? ""))
            return;

        string? device = changedScope ? selection?.Device : (PhysicalDevicePicker.SelectedItem as VJoyPatchDevice)?.Id;
        string? layer = changedScope ? selection?.Layer : SelectedLayer;
        int? outputDevice = changedScope ? null : (OutputDevicePicker.SelectedItem as OutputDevice)?.Id;
        if (changedScope)
        {
            _selection.Clear();
            _selectionAnchor = selection;
            if (selection is not null)
                _selection.Add(selection);
        }
        _scope = scope;
        GameCaption.Text = caption;
        GremlinShiftCaption.Text = shiftDescription ?? "";
        GremlinShiftCaption.Visibility = shiftDescription is null ? Visibility.Collapsed : Visibility.Visible;
        _refreshing = true;
        try
        {
            _devices = devices;
            _ports = ports;
            _capacity = capacity;
            PhysicalDevicePicker.ItemsSource = _devices;
            PhysicalDevicePicker.SelectedItem = _devices.FirstOrDefault(item => item.Id == device) ?? _devices.FirstOrDefault();
            SetLayers(layer);
            var selected = _ports.FirstOrDefault(port => port.Address == _selectionAnchor);
            if (changedScope)
                KindPicker.SelectedIndex = selected?.Kind == "axis" ? 1 : 0;
            var outputDevices = capacity.Select(output => output.DeviceId).Distinct()
                .Concat(ports.Select(port => port.Route?.DeviceId).OfType<int>())
                .Distinct().Order().Select(id => new OutputDevice(id, $"vJoy {id}")).ToArray();
            OutputDevicePicker.ItemsSource = outputDevices;
            OutputDevicePicker.SelectedItem = outputDevices.FirstOrDefault(item => item.Id == (outputDevice ?? selected?.Route?.DeviceId))
                ?? outputDevices.FirstOrDefault();
            var addresses = ports.Select(port => port.Address).ToHashSet();
            _selection.IntersectWith(addresses);
            if (selected is null)
                _selectionAnchor = null;
        }
        finally { _refreshing = false; }
        RepaintPorts();
        if (changedScope && _selectionAnchor is { } address && _inputRows.TryGetValue(address, out var row))
            row.Visual.Button.Loaded += (_, _) => row.Visual.Button.BringIntoView();
    }

    private void SetLayers(string? preferred)
    {
        var layers = (PhysicalDevicePicker.SelectedItem as VJoyPatchDevice)?.Layers.Split('|')
            .Select(id => new InputLayer(id)).ToArray() ?? [];
        LayerFilter.ItemsSource = layers;
        LayerFilter.SelectedItem = layers.FirstOrDefault(layer => layer.Id == preferred) ?? layers.FirstOrDefault();
    }

    private string? SelectedLayer => (LayerFilter.SelectedItem as InputLayer)?.Id;

    private void PhysicalDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing)
            return;
        _refreshing = true;
        try { SetLayers(SelectedLayer); }
        finally { _refreshing = false; }
        FilterChanged(sender, e);
    }

    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshing)
            return;
        if (sender != OutputDevicePicker)
            InputScroll.ScrollToTop();
        if (sender != PhysicalDevicePicker && sender != LayerFilter)
            OutputScroll.ScrollToTop();
        RepaintPorts();
    }

    private void RepaintPorts()
    {
        EndWireDrag();
        string kind = KindPicker.SelectedIndex == 1 ? "axis" : "button";
        var device = PhysicalDevicePicker.SelectedItem as VJoyPatchDevice;
        string? layer = SelectedLayer;
        var ports = _visiblePorts = _ports
            .Where(port => port.Address.Device == device?.Id && port.Address.Layer == layer && port.Kind == kind)
            .OrderBy(port => port.Signal?.Index ?? int.MaxValue).ToArray();
        int? outputDevice = (OutputDevicePicker.SelectedItem as OutputDevice)?.Id;
        var outputRows = _capacity.Concat(ports.Select(port => port.Route?.Output).OfType<GremlinOutput>())
            .Where(output => output.DeviceId == outputDevice && output.Kind == kind)
            .Distinct().OrderBy(output => output.InputId).ToArray();
        var available = _capacity.ToHashSet();
        var outputsInUse = _ports.Where(port => SameGremlinMode(port.Address.Layer, layer))
            .Select(port => port.Route?.Output).OfType<GremlinOutput>().ToHashSet();

        Inputs.Children.Clear();
        Outputs.Children.Clear();
        _inputRows.Clear();
        _outputRows.Clear();
        foreach (var port in ports)
        {
            string detail = port.Signal is null ? "Input needs learning"
                : port.Reference ? $"{port.Signal} · Reference input" : port.Signal.ToString();
            if (port.Inherited)
                detail += port.Address.Layer == GremlinShiftSetting.Layer ? " · Inherited from base" : " · Inherited from parent mode";
            if (port.OtherTargets is { } targets)
                detail += $" · {targets}";
            if (port.Modifier is { } modifier)
                detail = $"{modifier} · {detail}";
            var row = PortButton(port.Label, detail, true, port.Route?.Output is not null, port.Address.Layer,
                port.HasSocket, port.Inherited);
            row.Button.Click += (_, _) => SelectInput(port, Keyboard.Modifiers);
            Inputs.Children.Add(row.Button);
            _inputRows.Add(port.Address, (port, row));
        }
        foreach (var output in outputRows)
        {
            bool mapped = outputsInUse.Contains(output);
            bool supported = available.Contains(output);
            var row = PortButton(output.Label, !supported ? "Unavailable" : mapped ? "In use" : "", false, mapped, layer);
            row.Button.IsEnabled = supported;
            row.Button.Click += (_, _) => ConnectSelected(output);
            Outputs.Children.Add(row.Button);
            _outputRows.Add(output, row);
        }
        if (ports.Length == 0)
            Inputs.Children.Add(Empty(_scope is null ? "Select a game using Joystick Gremlin."
                : device is null ? "Add a controller to the plan." : $"This device has no {kind} inputs on this layer."));
        if (outputRows.Length == 0)
            Outputs.Children.Add(Empty(_scope is null ? "vJoy outputs appear in Gremlin mode."
                : _capacity.Length == 0 ? "Configure a vJoy device or select a Gremlin profile with vJoy outputs."
                : $"No {kind} outputs for this vJoy device."));
        UpdateSelection();
    }

    private static PortVisual PortButton(string title, string detail, bool input, bool connected, string? layer,
        bool hasSocket = true, bool inherited = false)
    {
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        if (detail.Length > 0)
        {
            var subtext = new TextBlock { Text = detail, FontSize = 11, TextWrapping = TextWrapping.Wrap };
            subtext.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
            text.Children.Add(subtext);
        }
        var dot = new Ellipse { Width = 8, Height = 8, StrokeThickness = 1.5, Opacity = inherited ? 0.5 : 1 };
        dot.SetResourceReference(Shape.StrokeProperty, connected ? LayerColor(layer) : "ControlOutline");
        if (connected)
            dot.SetResourceReference(Shape.FillProperty, LayerColor(layer));
        var socket = new Grid
        {
            Width = 24,
            Height = 24,
            Background = Brushes.Transparent,
            Cursor = Cursors.Cross,
            Visibility = hasSocket ? Visibility.Visible : Visibility.Collapsed,
            ToolTip = inherited ? "Drag to add a Gremlin Shift override" : input ? "Drag to a vJoy output" : "Drag to a physical input"
        };
        socket.Children.Add(dot);
        var row = new DockPanel();
        DockPanel.SetDock(socket, input ? Dock.Right : Dock.Left);
        row.Children.Add(socket);
        row.Children.Add(text);
        var button = new Button { Content = row, Padding = new Thickness(7, 5, 7, 5), Margin = new Thickness(0, 0, 0, 3) };
        row.Loaded += (_, _) => row.Width = Math.Max(0, button.ActualWidth - 16);
        button.SizeChanged += (_, _) => row.Width = Math.Max(0, button.ActualWidth - 16);
        AutomationProperties.SetName(button, detail.Length == 0 ? title : $"{title} · {detail}");
        return new PortVisual(button, socket);
    }

    private static TextBlock Empty(string text)
    {
        var row = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 10, 8, 8) };
        row.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");
        return row;
    }

    private static string LayerColor(string? layer) => layer switch
    {
        GremlinShiftSetting.Layer => "GremlinShiftAccent",
        null or "Normal" or "Mode 1" => "Accent",
        _ => "ShiftAccent"
    };

    private static bool SameGremlinMode(string? first, string? second) =>
        (first == GremlinShiftSetting.Layer) == (second == GremlinShiftSetting.Layer);

    private void SelectInput(VJoyPatchPort port, ModifierKeys modifiers, bool revealOutput = true)
    {
        bool toggle = modifiers.HasFlag(ModifierKeys.Control);
        int first = modifiers.HasFlag(ModifierKeys.Shift)
            ? Array.FindIndex(_visiblePorts, candidate => candidate.Address == _selectionAnchor) : -1;
        if (first >= 0)
        {
            int last = Array.IndexOf(_visiblePorts, port);
            if (!toggle)
                _selection.Clear();
            for (int index = Math.Min(first, last); index <= Math.Max(first, last); index++)
                _selection.Add(_visiblePorts[index].Address);
        }
        else
        {
            if (!toggle)
                _selection.Clear();
            if (!toggle || !_selection.Remove(port.Address))
                _selection.Add(port.Address);
            _selectionAnchor = port.Address;
        }

        if (revealOutput && modifiers == ModifierKeys.None && port.Route is { } route &&
            (OutputDevicePicker.SelectedItem as OutputDevice)?.Id != route.DeviceId)
            OutputDevicePicker.SelectedItem = (OutputDevicePicker.ItemsSource as OutputDevice[])?
                .FirstOrDefault(device => device.Id == route.DeviceId);
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        foreach (var row in _inputRows.Values)
            row.Visual.Button.SetResourceReference(BackgroundProperty, _selection.Contains(row.Port.Address)
                ? "SelectionBackground" : "ControlBackground");
        var port = SelectedPort();
        var selectedRoutes = SelectedRemovableRoutes();
        var selectedOutputs = _ports.Where(input => _selection.Contains(input.Address) &&
                SameGremlinMode(input.Address.Layer, SelectedLayer))
            .Select(input => input.Route?.Output).OfType<GremlinOutput>().ToHashSet();
        foreach (var (output, row) in _outputRows)
            row.Button.SetResourceReference(BackgroundProperty, selectedOutputs.Contains(output)
                ? "SelectionBackground" : "ControlBackground");
        RemoveRouteButton.IsEnabled = selectedRoutes.Length > 0;
        RemoveRouteButton.Content = selectedRoutes.Length > 1 ? $"Remove {selectedRoutes.Length} routes" : "Remove route";
        RouteDetails.Text = _selection.Count > 1 ? $"{_selection.Count} inputs selected · {selectedRoutes.Length} routes"
            : port is null ? "Drag between sockets to connect. Use Ctrl or keyboard Shift to select multiple routes."
            : port.Modifier is { } modifier ? $"{port.Label} · {modifier} · Reserved for changing modes"
            : port.Inherited ? $"{port.Label} → {port.Route?.Output?.ToString() ?? port.OtherTargets} · {InheritedEditHint(port)}"
            : port.OtherTargets is not null ? $"{port.Label} · Edit saved routes in Joystick Gremlin"
            : port.Route?.Output is { } outputPort ? $"{port.Label} → {outputPort} · Delete removes the selected route"
            : port.Signal is null ? $"{port.Label} · Learn the physical input in the main window."
            : $"{port.Label} · Drag to a vJoy {port.Kind}.";
        QueueWires();
    }

    private VJoyPatchPort? SelectedPort() =>
        _selection.Count == 1 && _inputRows.TryGetValue(_selection.First(), out var row) ? row.Port : null;

    private static string InheritedEditHint(VJoyPatchPort port) => port.Address.Layer == GremlinShiftSetting.Layer
        ? "Edit in the base layer or drag here to override" : "Edit the parent mode in Joystick Gremlin or drag here to override";

    private VJoyPatchPort[] SelectedRemovableRoutes() => _ports.Where(port => _selection.Contains(port.Address) &&
        SameGremlinMode(port.Address.Layer, SelectedLayer) && port.CanRemove).ToArray();

    private void RemoveRoute(object sender, RoutedEventArgs e) => RemoveSelectedRoutes();

    private void RemoveSelectedRoutes()
    {
        var routes = SelectedRemovableRoutes().Select(port => port.Address).ToArray();
        if (_scope is not null && routes.Length > 0 && _apply(_scope, routes, null) is { } error)
            RouteDetails.Text = error;
    }

    private void ConnectSelected(GremlinOutput output)
    {
        if (SelectedPort() is { } port)
            Connect(port, output);
    }

    private void Connect(VJoyPatchPort port, GremlinOutput output)
    {
        if (_scope is null)
            return;
        if (!port.HasSocket)
            return;
        if (port.Signal is null)
        {
            RouteDetails.Text = $"{port.Label} · Learn the physical input in the main window.";
            return;
        }
        if (_apply(_scope, [port.Address], output) is { } error)
            RouteDetails.Text = error;
    }

    private void PortViewportChanged(object sender, ScrollChangedEventArgs e) => QueueWires();
    private void WireLayerResized(object sender, SizeChangedEventArgs e) => QueueWires();

    private void QueueWires()
    {
        if (_wireRefresh is not null || WireLayer is null)
            return;
        _wireRefresh = Dispatcher.InvokeAsync(() =>
        {
            _wireRefresh = null;
            DrawWires();
        }, DispatcherPriority.Render);
    }

    private void DrawWires()
    {
        WireLayer.Children.Clear();
        _wireHits.Clear();
        double height = WireLayer.ActualHeight;
        if (!WireLayer.IsLoaded || height <= 12)
            return;

        foreach (var (port, input) in _inputRows.Values)
        {
            if (port.Route?.Output is not { } channel || !_outputRows.TryGetValue(channel, out var output) ||
                !input.Socket.IsArrangeValid || !output.Socket.IsArrangeValid)
                continue;
            Point start = SocketCenter(input);
            Point end = SocketCenter(output);
            if ((start.Y < 0 && end.Y < 0) || (start.Y > height && end.Y > height))
                continue;
            start.Y = Math.Clamp(start.Y, 5, height - 5);
            end.Y = Math.Clamp(end.Y, 5, height - 5);
            var geometry = WireGeometry(start, end);
            bool selected = _selection.Contains(port.Address);
            var path = new Path
            {
                StrokeThickness = selected ? 3 : 1.8,
                Opacity = port.Inherited ? (selected ? 0.65 : 0.34) : selected ? 1 : 0.68,
                Data = geometry
            };
            if (port.Inherited)
                path.StrokeDashArray = new DoubleCollection([3, 3]);
            Panel.SetZIndex(path, selected ? 1 : 0);
            path.SetResourceReference(Shape.StrokeProperty, LayerColor(port.Address.Layer));
            WireLayer.Children.Add(path);
            _wireHits.Add((port, geometry));
        }
        if (_draftWire is not null)
            WireLayer.Children.Add(_draftWire);
    }

    private sealed record OutputDevice(int Id, string Name);
    private sealed record InputLayer(string Id)
    {
        public string Label => Id == "Shift" ? "Hardware Shift" : Id;
    }
    private sealed record PortVisual(Button Button, Grid Socket);
}
