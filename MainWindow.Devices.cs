using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Hardware.Custom;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private DispatcherTimer? _deviceStripTimer;
    private double _deviceStripTarget;
    private readonly DeviceThumbnailCache _libraryThumbnails = new();

    private void RefreshDevices(object sender, RoutedEventArgs e) => ScanDevices();

    private void ScanDevices()
    {
        StopLearning();
        _dcsProfiles.Clear();
        try { RefreshConnectedDevices(); }
        catch (Exception error) { Notify($"Controller detection failed. {error.Message}"); }
        RenderDeviceList();
    }

    private IReadOnlyList<JoystickDevice> RefreshConnectedDevices()
    {
        _connected = [];
        _cliffsDeviceNames.Clear();
        _input ??= new DirectInputReader();
        return _connected = _input.Enumerate();
    }

    private IEnumerable<HardwareDefinition> LibraryLayouts() =>
        _standardDevices.Concat(_customDevices.Select(item => item.ToHardwareDefinition()));

    private IReadOnlyList<HardwareDefinition> PlannedLayouts()
    {
        var layouts = LibraryLayouts().ToDictionary(layout => layout.Id, StringComparer.Ordinal);
        return _plan.Devices.Where(slot => layouts.TryGetValue(slot.LayoutId, out var layout) && !layout.IsAssemblyPart)
            .Select(slot => PlannedLayout(slot, layouts[slot.LayoutId])).ToArray();
    }

    private HardwareDefinition? FindPlannedLayout(string id)
    {
        if (_plan.Devices.FirstOrDefault(slot => slot.Id == id) is not { } selected)
            return null;
        var layout = _standardDevices.FirstOrDefault(item => item.Id == selected.LayoutId)
            ?? _customDevices.FirstOrDefault(item => item.Id == selected.LayoutId)?.ToHardwareDefinition();
        return layout is { IsAssemblyPart: false } ? PlannedLayout(selected, layout) : null;
    }

    private HardwareDefinition GetLayout(string id) => FindPlannedLayout(id)
        ?? throw new InvalidOperationException($"This plan refers to a controller layout that is unavailable: {id}.");

    private HardwareDefinition PlannedLayout(PlannedDevice slot, HardwareDefinition layout)
    {
        if (slot.BaseLayoutId is { } baseId && layout.CompatibleBaseIds.Count > 0)
        {
            var flightstickBase = CompatibleBases(layout).FirstOrDefault(item => item.Id == baseId);
            var panels = flightstickBase is null ? [] : CompatiblePanels(flightstickBase).ToArray();
            layout = HardwareTemplates.Assemble(layout, flightstickBase,
                panels.FirstOrDefault(panel => panel.Id == slot.LeftPanelLayoutId),
                panels.FirstOrDefault(panel => panel.Id == slot.RightPanelLayoutId));
        }
        return layout with { Id = slot.Id, TemplateId = slot.LayoutId, Name = slot.DisplayName(layout.Name) };
    }

    private JoystickDevice? ConnectedDevice(HardwareDefinition device)
    {
        if (device.GripOnly)
            return null;
        var slot = _plan.Devices.FirstOrDefault(item => item.Id == device.Id);
        return slot is { InstanceId: { } instance, ProductId: { } product }
            ? Single(_connected.Where(item => item.Id == instance && item.Product == product)) : null;
    }

    private static bool MatchesLayout(HardwareDefinition layout, JoystickDevice input) =>
        input.Matches(layout.VendorId, layout.ProductId) ||
        layout.AlternateProductIds.Any(productId => input.Matches(layout.VendorId, productId));

    private void RenderDeviceList(string? select = null)
    {
        _deviceStripTimer?.Stop();
        string? previous = _hardware?.Id;
        string? viewId = _view?.Id;
        string? controlId = _control?.Id;
        string layer = Layer;
        var choices = PlannedLayouts().Select(layout =>
            new HardwareChoice(layout, ConnectedDevice(layout), Thumbnail(layout))).ToArray();
        select ??= previous;
        DevicePicker.ItemsSource = choices;
        DevicePicker.SelectedItem = choices.FirstOrDefault(device => device.Definition.Id == select) ?? choices.FirstOrDefault();
        EmptyDeviceRoster.Visibility = choices.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyDeviceRoster.Text = _plan.Devices.Count == 0 ? "Add a controller from the device library to start planning."
            : "The saved controller layouts are unavailable in this device library.";
        if (_hardware is not { } current || current.Id != previous)
            return;

        _view = current.Views.FirstOrDefault(view => view.Id == viewId) ?? _view;
        if (LayerPicker.Items.Contains(layer))
        {
            _updatingControls = true;
            LayerPicker.SelectedItem = layer;
            _updatingControls = false;
        }
        var selected = current.Controls.FirstOrDefault(control => control.Id == controlId);
        var location = selected?.Location ?? selected?.Id;
        if (location is not null && _view?.Hotspots.Any(spot => LocationOf(spot.ControlId) == location) != true)
            _view = current.Views.FirstOrDefault(view => view.Hotspots.Any(spot => LocationOf(spot.ControlId) == location)) ?? _view;
        DrawViewTabs();
        SelectControl(selected, stopLearning: false);
    }

    private void AddLibraryDevice(object sender, RoutedEventArgs e)
    {
        var layouts = LibraryLayouts().Where(layout => !layout.IsAssemblyPart)
            .OrderBy(layout => layout.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        if (layouts.Length == 0)
        {
            Notify("The device library is empty.");
            return;
        }
        var tiles = layouts.Select(layout => new DeviceLibraryTile(layout)).ToArray();
        var library = new DeviceLibraryWindow(tiles, new OwnedDeviceStore(), LibraryPreview) { Owner = this };
        if (library.ShowDialog() != true || library.SelectedLayout is not { } chosen)
            return;
        BaseChoice? assembly = null;
        if (chosen.CompatibleBaseIds.Count > 0)
        {
            assembly = ChooseFlightstickBase(chosen, includeGripOnly: true);
            if (assembly is null)
                return;
        }
        var slot = assembly is null ? _plan.AddDevice(chosen.Id)
            : _plan.AddDeviceAssembly(chosen.Id, assembly.Base?.Id ?? "", assembly.LeftPanel?.Id, assembly.RightPanel?.Id);
        RenderDeviceList(slot.Id);
        if (DevicePicker.SelectedItem is { } added)
            DevicePicker.ScrollIntoView(added);
        Notify($"{PlannedLayout(slot, chosen).Name} added for offline planning.");
    }

    private FrameworkElement LibraryPreview(HardwareDefinition layout)
    {
        if (FindCustom(layout) is { } custom)
            return new Viewbox
            {
                Child = CustomPanelDrawing.CreatePanel(custom, Brush("CanvasBackground"), Brush("ControlOutline"))
            };

        if (layout.Views.FirstOrDefault() is not { Image.Length: > 0 } view)
            return new Border();

        var preview = new Image
        {
            Source = _libraryThumbnails.Get(view.Image), Stretch = Stretch.Uniform
        };
        RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);
        if (layout.IsViewMirrored(view))
        {
            preview.RenderTransformOrigin = new Point(0.5, 0.5);
            preview.RenderTransform = new ScaleTransform(-1, 1);
        }
        return preview;
    }

    private IEnumerable<HardwareDefinition> CompatibleBases(HardwareDefinition grip) => grip.CompatibleBaseIds
        .Select(id => _standardDevices.FirstOrDefault(layout => layout.IsBase && layout.Id == id)).OfType<HardwareDefinition>();

    private IEnumerable<HardwareDefinition> CompatiblePanels(HardwareDefinition flightstickBase) => flightstickBase.CompatiblePanelIds
        .Select(id => _standardDevices.FirstOrDefault(layout => layout.IsPanel && layout.Id == id)).OfType<HardwareDefinition>();

    private BaseChoice? ChooseFlightstickBase(HardwareDefinition grip, bool includeGripOnly)
    {
        var bases = CompatibleBases(grip).Select(layout => new BaseChoice(layout)).ToArray();
        if (bases is [{ Base: { } yokeBase }] && yokeBase.CompatiblePanelIds.Count > 0)
            return ChooseYokeAssembly(bases, yokeBase, includeGripOnly);
        return ReviewWindow.Pick(this, "Choose a compatible base",
            includeGripOnly ? [new BaseChoice(null), .. bases] : bases, width: 520);
    }

    private BaseChoice? ChooseYokeAssembly(BaseChoice[] bases, HardwareDefinition yokeBase, bool includeGripOnly)
    {
        var form = new StackPanel();
        var baseOptions = includeGripOnly ? bases.Append(new BaseChoice(null)).ToArray() : bases;
        var panels = new[] { new PanelChoice(null) }.Concat(CompatiblePanels(yokeBase).Select(panel => new PanelChoice(panel))).ToArray();
        var basePicker = AddPicker("BASE", baseOptions);
        var left = AddPicker("LEFT PANEL", panels);
        var right = AddPicker("RIGHT PANEL", panels);
        basePicker.SelectionChanged += (_, _) =>
            left.IsEnabled = right.IsEnabled = basePicker.SelectedItem is BaseChoice { Base: not null };
        if (!ReviewWindow.Show(this, "Configure yoke", form, "Select", 520, 350)
            || basePicker.SelectedItem is not BaseChoice chosen)
            return null;
        return chosen.Base is null ? chosen : chosen with
        {
            LeftPanel = (left.SelectedItem as PanelChoice)?.Panel,
            RightPanel = (right.SelectedItem as PanelChoice)?.Panel
        };

        ComboBox AddPicker<T>(string label, IReadOnlyList<T> items)
        {
            form.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.Bold });
            var picker = new ComboBox { ItemsSource = items, SelectedIndex = 0, Margin = new Thickness(0, 5, 0, 12) };
            AutomationProperties.SetName(picker, label);
            form.Children.Add(picker);
            return picker;
        }
    }

    private void RemovePlannedDevice(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HardwareChoice choice })
            return;
        var layout = choice.Definition;
        if (_plan.HasMappedDeviceInputs(layout.Id) && !ReviewWindow.Confirm(this, "Remove controller",
                $"Remove {layout.Name} and its learned inputs and assignments from this plan?", "Remove from plan"))
            return;
        int position = _plan.Devices.FindIndex(slot => slot.Id == layout.Id);
        if (!_plan.RemoveDevice(layout.Id))
            return;
        string? select = _hardware?.Id == layout.Id && _plan.Devices.Count > 0
            ? _plan.Devices[Math.Min(position, _plan.Devices.Count - 1)].Id : null;
        RenderDeviceList(select);
        Notify($"{layout.Name} removed from the plan.");
    }

    private ScrollViewer? DeviceStripScroller() =>
        DevicePicker.Template?.FindName("DeviceStripScrollViewer", DevicePicker) as ScrollViewer;

    private void ScrollDeviceStrip(object sender, MouseWheelEventArgs e)
    {
        if (DeviceStripScroller() is not { ScrollableWidth: > 0 } scroller)
            return;
        if (_deviceStripTimer is null)
        {
            _deviceStripTimer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
                { Interval = TimeSpan.FromMilliseconds(16) };
            _deviceStripTimer.Tick += AdvanceDeviceStrip;
        }
        double origin = _deviceStripTimer.IsEnabled ? _deviceStripTarget : scroller.HorizontalOffset;
        _deviceStripTarget = Math.Clamp(origin - e.Delta * 0.7, 0, scroller.ScrollableWidth);
        _deviceStripTimer.Start();
        e.Handled = true;
    }

    private void AdvanceDeviceStrip(object? sender, EventArgs e)
    {
        if (DeviceStripScroller() is not { ScrollableWidth: > 0 } scroller)
        {
            _deviceStripTimer?.Stop();
            return;
        }
        double target = Math.Min(_deviceStripTarget, scroller.ScrollableWidth);
        double distance = target - scroller.HorizontalOffset;
        if (Math.Abs(distance) < 1.5)
        {
            scroller.ScrollToHorizontalOffset(target);
            _deviceStripTimer?.Stop();
            return;
        }
        scroller.ScrollToHorizontalOffset(scroller.HorizontalOffset + distance * 0.32);
    }

    private void DeviceStripUnloaded(object sender, RoutedEventArgs e) => _deviceStripTimer?.Stop();

    private void DeviceStripPointerDown(object sender, MouseButtonEventArgs e) => _deviceStripTimer?.Stop();

    private void ConnectDevice(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: HardwareChoice choice })
            return;
        var layout = choice.Definition;
        int index = _plan.Devices.FindIndex(slot => slot.Id == layout.Id);
        if (index < 0)
            return;
        StopLearning();
        if (choice.Device is not null)
        {
            _plan.Devices[index] = _plan.Devices[index] with { InstanceId = null, ProductId = null };
            RenderDeviceList();
            Notify($"{layout.Name} is offline.");
            return;
        }

        try
        {
            if (layout.GripOnly)
            {
                if (ChooseFlightstickBase(layout, includeGripOnly: false) is not { Base: { } selectedBase } assembly)
                    return;

                var slot = _plan.Devices[index] with
                {
                    BaseLayoutId = selectedBase.Id, LeftPanelLayoutId = assembly.LeftPanel?.Id,
                    RightPanelLayoutId = assembly.RightPanel?.Id, InstanceId = null, ProductId = null
                };
                var grip = _standardDevices.First(item => item.Id == slot.LayoutId);
                layout = PlannedLayout(slot, grip);
                _plan.Devices[index] = slot;
                Notify($"{selectedBase.Name} added to the grip. Choose the assembled controller to connect it.");
            }
            var reserved = _plan.Devices.Where(slot => slot.Id != layout.Id)
                .Select(slot => slot.InstanceId).ToHashSet();
            var devices = RefreshConnectedDevices();
            var available = devices.Where(device => !reserved.Contains(device.Id) &&
                (layout.CompatibleBaseIds.Count == 0 || !device.Name.StartsWith("vJoy", StringComparison.OrdinalIgnoreCase))).ToArray();
            var choices = available.OrderByDescending(device => MatchesLayout(layout, device))
                .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(device => device.Id)
                .Select(device => new ConnectionChoice(device, ConnectionLabel(device, available))).ToArray();
            if (choices.Length == 0)
            {
                Notify(devices.Count == 0 ? "Windows did not report any controllers."
                    : "All detected controllers are already connected to other plan devices.");
                return;
            }
            if (ReviewWindow.Pick(this, "Connect controller", choices, width: 520) is not { Device: { } selected })
                return;
            if (!MatchesLayout(layout, selected) && !ReviewWindow.Confirm(this, "Connect a different device",
                    $"{selected.Name} reports a different controller model than {layout.Name}. Verify its inputs with Input Configurator or set them manually.",
                    "Connect this device"))
                return;
            if (!RefreshConnectedDevices().Any(device => device.Id == selected.Id && device.Product == selected.Product))
            {
                Notify($"{selected.Name} is no longer available. Reconnect it and try again.");
                return;
            }
            _plan.Devices[index] = _plan.Devices[index] with { InstanceId = selected.Id, ProductId = selected.Product };
            Notify($"{layout.Name} connected to {selected.Name}.");
        }
        catch (Exception error) { Notify($"Could not connect {layout.Name}. {error.Message}"); }
        finally { RenderDeviceList(); }
    }

    private static string ConnectionLabel(JoystickDevice device, IReadOnlyList<JoystickDevice> devices)
    {
        var sameName = devices.Where(item => StringComparer.OrdinalIgnoreCase.Equals(item.Name, device.Name))
            .OrderBy(item => item.Id).ToArray();
        return sameName.Length == 1 ? device.Name : $"{device.Name} ({Array.IndexOf(sameName, device) + 1})";
    }

    private CustomController? FindCustom(HardwareDefinition? device) =>
        _customDevices.FirstOrDefault(item => item.Id == device?.ModelId);

    private void EditCustomController(object sender, RoutedEventArgs e)
    {
        var choices = new[] { new CustomChoice(null) }
            .Concat(_customDevices.Select(controller => new CustomChoice(controller))).ToArray();
        var choice = choices.Length == 1 ? choices[0] : ReviewWindow.Pick(this, "Custom controllers", choices);
        if (choice is null)
            return;
        var editor = new CustomControllerEditor(choice.Controller, _connected) { Owner = this };
        if (editor.ShowDialog() == true && editor.Result is { } controller)
        {
            _customDevices = CustomControllerStore.Load();
            RenderDeviceList();
            Notify($"{controller.Name} saved to the device library.");
        }
    }

    private sealed record HardwareChoice(HardwareDefinition Definition, JoystickDevice? Device, ImageSource? Thumbnail)
    {
        public string Name => Definition.Name.Replace(" — ", Environment.NewLine, StringComparison.Ordinal);
        public string Status => Device is null ? "Offline planning" : "Connected";
        public string ConnectionAction => Device is null ? "Connect" : "Disconnect";
        public string ConnectionAutomationName => $"{ConnectionAction} {Definition.Name}";
        public string? ConnectionDetails => Device?.Name;
        public string RemoveAutomationName => $"Remove {Definition.Name} from plan";
        public double ThumbnailScale => Definition.MirrorGrip ? -1 : 1;
    }

    private sealed record BaseChoice(HardwareDefinition? Base, HardwareDefinition? LeftPanel = null, HardwareDefinition? RightPanel = null)
    {
        public override string ToString() => Base?.Name ?? "Grip only · Offline planning";
    }

    private sealed record PanelChoice(HardwareDefinition? Panel)
    {
        public override string ToString() => Panel?.Name ?? "No panel";
    }

    private sealed record ConnectionChoice(JoystickDevice Device, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record CustomChoice(CustomController? Controller)
    {
        public override string ToString() => Controller?.Name ?? "Build a new controller";
    }
}
