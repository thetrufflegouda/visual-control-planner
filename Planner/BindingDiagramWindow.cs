using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Hardware.Custom;

namespace VisualControlsBinder.Planner;

internal sealed record DiagramDevice(HardwareDefinition Hardware, CustomController? Custom)
{
    public override string ToString() => Hardware.Name;
}

internal sealed class BindingDiagramWindow : Window
{
    private readonly Func<string, BitmapSource?> _photo;
    private IReadOnlyList<DiagramDevice> _devices = [];
    private IReadOnlyList<DiagramBinding> _bindings = [];
    private HashSet<string> _deviceIds = new(StringComparer.Ordinal);
    private string? _scope;
    private bool _showOtherInputs;
    private bool _updatingPlan;
    private readonly ComboBox _devicePicker = new() { MinWidth = 280 };
    private readonly TextBox _search = new() { MinWidth = 208, ToolTip = "Search action, input, layer, or control" };
    private readonly StackPanel _pages = new();
    private readonly TextBlock _subtitle = Muted("");
    private readonly TextBlock _shiftInfo = Muted("");
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _legend = new() { Orientation = Orientation.Horizontal };
    private readonly ScrollViewer _scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

    public BindingDiagramWindow(Func<string, BitmapSource?> photo)
    {
        _photo = photo;
        Title = "BINDING DIAGRAM";
        Width = 1190;
        Height = 800;
        MinWidth = 830;
        MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ColorResource("WindowBackground");
        Foreground = ColorResource("TextPrimary");
        FontSize = 14;
        FontFamily = new FontFamily("Segoe UI");
        ShowInTaskbar = false;

        var root = new DockPanel();
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var heading = new DockPanel();
        var close = new Button { Content = "Close" };
        close.Click += (_, _) => Close();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && !_devicePicker.IsDropDownOpen)
            {
                e.Handled = true;
                Close();
            }
        };
        DockPanel.SetDock(close, Dock.Right);
        heading.Children.Add(close);
        heading.Children.Add(Heading(Title, 25));
        header.Children.Add(heading);
        header.Children.Add(_subtitle);
        _shiftInfo.Visibility = Visibility.Collapsed;
        _shiftInfo.Margin = new Thickness(0, 4, 0, 0);
        header.Children.Add(_shiftInfo);

        var filter = new DockPanel { Margin = new Thickness(0, 12, 0, 0) };
        var searchColumn = Field("Search bindings", _search);
        DockPanel.SetDock(searchColumn, Dock.Right);
        filter.Children.Add(searchColumn);
        var deviceColumn = Field("Controller", _devicePicker);
        deviceColumn.Margin = new Thickness(0, 0, 12, 0);
        filter.Children.Add(deviceColumn);
        header.Children.Add(filter);
        _summary.Foreground = ColorResource("TextSecondary");
        var info = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(_legend, Dock.Right);
        info.Children.Add(_legend);
        info.Children.Add(_summary);
        header.Children.Add(info);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        _scroll.Content = _pages;
        root.Children.Add(_scroll);
        Content = new Border { Padding = new Thickness(22, 16, 22, 16), Background = Background, Child = root };
        _devicePicker.SelectionChanged += (_, _) => RenderFilter();
        _search.TextChanged += (_, _) => RenderFilter();
    }

    public void UpdatePlan(string scope, string subtitle, IReadOnlyList<DiagramDevice> devices,
        IReadOnlyList<DiagramBinding> bindings, string? preferredDevice, string? shiftDescription = null)
    {
        bool scopeChanged = _scope != scope;
        bool devicesChanged = devices.Count != _devices.Count || !devices.Zip(_devices).All(pair =>
            pair.First.Hardware.Id == pair.Second.Hardware.Id && pair.First.Hardware.Name == pair.Second.Hardware.Name &&
            pair.First.Hardware.ModelId == pair.Second.Hardware.ModelId && pair.First.Custom == pair.Second.Custom);
        _subtitle.Text = subtitle;
        _shiftInfo.Text = shiftDescription ?? "";
        _shiftInfo.Visibility = string.IsNullOrWhiteSpace(shiftDescription) ? Visibility.Collapsed : Visibility.Visible;
        if (_shiftInfo.Visibility == Visibility.Visible)
            _shiftInfo.Foreground = ColorResource("GremlinShiftAccent");
        if (!scopeChanged && !devicesChanged && _bindings.SequenceEqual(bindings))
            return;

        _scope = scope;
        _bindings = bindings;
        if (devicesChanged)
        {
            _devices = devices;
            _deviceIds = devices.Select(device => device.Hardware.Id).ToHashSet(StringComparer.Ordinal);
        }
        var previous = _devicePicker.SelectedItem as DeviceChoice;
        bool showOther = bindings.Any(HasNoLayout);
        _updatingPlan = true;
        try
        {
            if (scopeChanged)
                _search.Clear();
            if (devicesChanged || showOther != _showOtherInputs)
            {
                _showOtherInputs = showOther;
                var options = devices.Select(device => new DeviceChoice(device)).ToList();
                if (showOther)
                    options.Add(new DeviceChoice(null));
                _devicePicker.ItemsSource = options;
                _devicePicker.SelectedItem = (previous is null ? null : options.FirstOrDefault(choice => choice.Id == previous.Id))
                    ?? options.FirstOrDefault(choice => preferredDevice is not null && choice.Id == preferredDevice)
                    ?? options.FirstOrDefault();
            }
        }
        finally { _updatingPlan = false; }
        bool keepView = !scopeChanged && previous is not null && _devicePicker.SelectedItem is DeviceChoice current &&
            previous.Id == current.Id;
        Render(keepView);
    }

    private void RenderFilter()
    {
        if (!_updatingPlan)
            Render(preserveView: false);
    }

    private void Render(bool preserveView)
    {
        var expansions = preserveView ? _pages.Children.OfType<Expander>()
            .ToDictionary(section => (string)section.Tag, section => section.IsExpanded) : [];
        double offset = preserveView ? _scroll.VerticalOffset : 0;
        _pages.Children.Clear();
        _scroll.ScrollToVerticalOffset(offset);
        if (_devicePicker.SelectedItem is not DeviceChoice choice)
        {
            RenderLegend(null);
            _summary.Text = "";
            _pages.Children.Add(Muted("Add a controller in the main window to see its binding diagram."));
            return;
        }
        var device = choice.Device;
        var hardware = device?.Hardware;
        RenderLegend(hardware);
        var controls = hardware?.Controls.ToDictionary(control => control.Id, StringComparer.Ordinal);
        string query = _search.Text.Trim();
        var visible = _bindings.Where(row => (device is null ? HasNoLayout(row) : row.DeviceId == hardware!.Id) &&
            Matches(row, controls, query)).ToArray();
        var placed = visible.Where(row => row.Address is not null &&
            controls?.ContainsKey(row.Address.Control) == true).ToArray();
        var unplaced = visible.Except(placed).ToArray();
        if (device is not null)
        {
            var locations = device.Hardware.Controls.GroupBy(control => control.Location ?? control.Id)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<HardwareControl>)group.ToArray(), StringComparer.Ordinal);
            var byLocation = placed.GroupBy(row =>
            {
                var control = controls![row.Address!.Control];
                return control.Location ?? control.Id;
            }).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);

            foreach (var view in device.Hardware.Views)
            {
                bool vkbOverview = IsVkbOverview(device, view);
                var callouts = new List<Callout>();
                for (int index = 0; index < view.Hotspots.Count; index++)
                {
                    var hotspot = view.Hotspots[index];
                    var anchor = controls![hotspot.ControlId];
                    var location = anchor.Location ?? anchor.Id;
                    var gestures = locations[location];
                    bool assigned = byLocation.Remove(location, out var rows);
                    if (assigned || (query.Length == 0 && vkbOverview && DiagramControlCard.IsDirectionalHat(gestures)))
                        callouts.Add(new Callout(hotspot, gestures, rows ?? [], index + 1));
                }
                if (callouts.Count > 0)
                {
                    var page = DiagramPage(device, view, callouts);
                    _pages.Children.Add(vkbOverview || device.Hardware.Views[0] == view ? Section(view.Name, page)
                        : Expand(view.Id, view.Name, page,
                            expansions.GetValueOrDefault(view.Id, true)));
                }
            }
            unplaced = unplaced.Concat(byLocation.Values.SelectMany(rows => rows)).ToArray();
        }
        _summary.Text = $"{visible.Length - unplaced.Length} placed · {unplaced.Length} unplaced";
        if (unplaced.Length > 0)
            _pages.Children.Add(Expand("", $"Unplaced inputs ({unplaced.Length})", UnplacedInputs(unplaced),
                expansions.GetValueOrDefault("", query.Length > 0 || _pages.Children.Count == 0)));
        if (_pages.Children.Count == 0)
        {
            _pages.Children.Add(Muted(query.Length > 0 ? "No bindings match the search."
                : "This controller has no assignments in the selected profile or plan."));
        }
    }

    private void RenderLegend(HardwareDefinition? hardware)
    {
        _legend.Children.Clear();
        if (hardware is null)
            return;
        var layers = hardware.ModelId == "virpil-cm3"
            ? new[] { "Mode 1", "Mode 2", "Mode 3", "Mode 4", "Mode 5" } : ["Normal", "Shift"];
        if (_bindings.Any(row => row.DeviceId == hardware.Id && row.Address?.Layer == GremlinShiftSetting.Layer))
            layers = [.. layers, GremlinShiftSetting.Layer];
        foreach (var layer in layers)
        {
            var label = Heading(DiagramControlCard.DisplayLayer(layer), 11);
            label.Foreground = DiagramControlCard.LayerBrush(layer);
            label.Margin = new Thickness(12, 0, 0, 0);
            _legend.Children.Add(label);
        }
    }

    private bool HasNoLayout(DiagramBinding row) =>
        row.DeviceId is null || !_deviceIds.Contains(row.DeviceId);

    private static bool Matches(DiagramBinding row, Dictionary<string, HardwareControl>? controls, string query)
    {
        if (query.Length == 0)
            return true;
        var address = row.Address;
        var control = address is null ? null : controls?.GetValueOrDefault(address.Control);
        return new[] { row.Label, row.Action?.Id, row.Action?.Category, row.Action?.Section,
            row.InputDetail, address is null ? null : DiagramControlCard.DisplayLayer(address.Layer), row.InheritanceDetail,
            control?.Name }.Any(text => text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool IsVkbOverview(DiagramDevice device, HardwareView view) =>
        device.Hardware.ModelId is "vkb-left" or "vkb-right" && view.Id == "face";

    private FrameworkElement DiagramPage(DiagramDevice device, HardwareView view, IReadOnlyList<Callout> callouts)
    {
        var layout = new Grid { Margin = new Thickness(0, 8, 0, 6) };
        bool hatsAbove = IsVkbOverview(device, view);
        bool balanceColumns = device.Hardware.ModelId == "virpil-cm3";
        bool useMarkers = !hatsAbove && callouts.Count > 8;
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(hatsAbove ? 1.06 : 1.65, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int photoRow = hatsAbove ? 1 : 0;
        for (int index = 0; index <= photoRow; index++)
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        Panel.SetZIndex(left, 1);
        Panel.SetZIndex(right, 1);
        Grid.SetRow(left, photoRow);
        Grid.SetRow(right, photoRow);
        Grid.SetColumn(right, 2);
        var mirror = device.Hardware.IsViewMirrored(view);
        var source = _photo(view.Image);
        var panel = HardwareCanvas(device.Custom, source, mirror);
        var imageFrame = new Viewbox
        {
            Child = panel, Stretch = Stretch.Uniform, MaxHeight = hatsAbove ? 360 : 470,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(10, hatsAbove ? 24 : 0, 10, 4)
        };
        Grid.SetRow(imageFrame, photoRow);
        Grid.SetColumn(imageFrame, 1);
        layout.Children.Add(left);
        layout.Children.Add(imageFrame);
        layout.Children.Add(right);

        var connectors = new Canvas { IsHitTestVisible = useMarkers, ClipToBounds = true };
        Grid.SetColumnSpan(connectors, 3);
        Grid.SetRowSpan(connectors, photoRow + 1);
        layout.Children.Add(connectors);
        var placements = new List<Placement>();
        double leftHeight = 0;
        double rightHeight = 0;
        double cardWidth = Math.Max(150, ((ActualWidth > 0 ? ActualWidth : Width) - 78) / 3.65);
        foreach (var callout in callouts.OrderBy(item => item.Anchor.Y))
        {
            double x = mirror ? 1 - callout.Anchor.X : callout.Anchor.X;
            string? modifier = callout.Bindings.Select(row => row.ShiftModifier).OfType<string>().FirstOrDefault();
            string? hatLabel = null;
            bool onLeft = x < 0.50;
            if (hatsAbove && DiagramControlCard.IsDirectionalHat(callout.Gestures))
                hatLabel = callout.Gestures[0].Location == "a3" ? "CENTER HAT" : onLeft ? "LEFT HAT" : "RIGHT HAT";
            var card = DiagramControlCard.Create(callout.Gestures, callout.Bindings, hatLabel,
                useMarkers && modifier is null ? callout.Number : null);
            if (hatLabel is not null)
            {
                bool center = hatLabel == "CENTER HAT";
                Grid.SetColumn(card, center ? 1 : onLeft ? 0 : 2);
                card.VerticalAlignment = VerticalAlignment.Top;
                if (center)
                    card.Margin = new Thickness(2, 22, 2, 3);
                layout.Children.Add(card);
                placements.Add(new Placement(card, x, callout.Anchor.Y, Dock.Top, callout.Number, callout.Gestures[0].Name, modifier));
            }
            else
            {
                if (balanceColumns)
                {
                    card.Measure(new Size(cardWidth, double.PositiveInfinity));
                    double height = card.DesiredSize.Height;
                    // Keep a card near its own physical side when both columns have room.
                    if (Math.Abs(leftHeight - rightHeight) > 32)
                        onLeft = leftHeight < rightHeight;
                    if (onLeft)
                        leftHeight += height;
                    else
                        rightHeight += height;
                }
                (onLeft ? left : right).Children.Add(card);
                placements.Add(new Placement(card, x, callout.Anchor.Y, onLeft ? Dock.Left : Dock.Right,
                    callout.Number, callout.Gestures[0].Name, modifier));
            }
        }

        void Connect()
        {
            if (!layout.IsLoaded || layout.ActualWidth <= 0)
                return;
            connectors.Children.Clear();
            foreach (var item in placements)
            {
                var anchor = panel.TranslatePoint(new Point(item.X * panel.Width, item.Y * panel.Height), connectors);
                if (useMarkers)
                {
                    if (item.Modifier is { } modifier)
                        PutModifierMarker(connectors, anchor, modifier);
                    else
                        Put(connectors, CreateMarker(item), anchor.X - 8, anchor.Y - 8);
                    continue;
                }
                Point offset = item.Side == Dock.Top ? new Point(Grid.GetColumn(item.Card) switch
                    {
                        0 => item.Card.ActualWidth - 1,
                        2 => 1,
                        _ => item.Card.ActualWidth / 2
                    }, item.Card.ActualHeight)
                    : new Point(item.Side == Dock.Left ? item.Card.ActualWidth : 0, Math.Min(item.Card.ActualHeight / 2, 24));
                var edge = item.Card.TranslatePoint(offset, connectors);
                var elbow = item.Side == Dock.Top ? new Point(edge.X, anchor.Y)
                    : new Point(edge.X + (item.Side == Dock.Left ? 14 : -14), edge.Y);
                connectors.Children.Add(new Polyline
                {
                    Points = new PointCollection([edge, elbow, anchor]),
                    Stroke = ColorResource(item.Modifier is null ? "TextSecondary" : "GremlinShiftAccent"),
                    StrokeThickness = 1, Opacity = 0.50
                });
                if (item.Modifier is { } label)
                    PutModifierMarker(connectors, anchor, label);
                else
                {
                    var dot = new Ellipse { Width = 5, Height = 5, Fill = ColorResource("Accent") };
                    Put(connectors, dot, anchor.X - 2.5, anchor.Y - 2.5);
                }
            }
        }
        void Schedule(object? sender, RoutedEventArgs e) => Dispatcher.InvokeAsync(Connect, DispatcherPriority.Loaded);
        layout.Loaded += Schedule;
        layout.SizeChanged += Schedule;
        left.SizeChanged += Schedule;
        right.SizeChanged += Schedule;
        return layout;
    }

    private static void PutModifierMarker(Canvas connectors, Point anchor, string label)
    {
        var accent = ColorResource("GremlinShiftAccent");
        const double radius = 5;
        var ring = new Ellipse { Width = 2 * radius, Height = 2 * radius, Stroke = accent, StrokeThickness = 2 };
        var text = Heading(label, 9);
        text.Foreground = accent;
        var badge = new Border
        {
            Child = text, Background = ColorResource("PanelBackground"), BorderBrush = accent,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 2, 5, 2),
            IsHitTestVisible = false
        };
        badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double x = anchor.X + 9 + badge.DesiredSize.Width <= connectors.ActualWidth ? anchor.X + 9
            : Math.Max(0, anchor.X - 9 - badge.DesiredSize.Width);
        double y = Math.Clamp(anchor.Y - badge.DesiredSize.Height / 2, 0, Math.Max(0, connectors.ActualHeight - badge.DesiredSize.Height));
        Put(connectors, ring, anchor.X - radius, anchor.Y - radius);
        Put(connectors, badge, x, y);
    }

    private static Button CreateMarker(Placement item)
    {
        var button = new Button
        {
            Width = 16, Height = 16, MinHeight = 0, MinWidth = 0, Padding = new Thickness(0),
            Content = new TextBlock { Text = item.Number.ToString(), Foreground = ColorResource("WindowBackground") },
            ToolTip = item.Control, FontSize = 9, FontWeight = FontWeights.Bold,
            Foreground = ColorResource("WindowBackground"), Background = ColorResource("Accent"),
            BorderBrush = ColorResource("WindowBackground"), BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        AutomationProperties.SetName(button, $"{item.Number}. {item.Control}");
        button.Click += (_, _) => item.Card.BringIntoView();
        return button;
    }

    private Canvas HardwareCanvas(CustomController? custom, BitmapSource? source, bool mirror)
    {
        if (source is null && custom is not null)
            return CustomPanelDrawing.CreatePanel(custom, ColorResource("PanelBackground"), ColorResource("PanelBorder"));

        var surface = new Canvas { Width = source?.PixelWidth ?? 400, Height = source?.PixelHeight ?? 440 };
        if (source is not null)
        {
            var image = new Image { Source = source, Width = surface.Width, Height = surface.Height };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            if (mirror)
            {
                image.RenderTransformOrigin = new Point(0.5, 0.5);
                image.RenderTransform = new ScaleTransform(-1, 1);
            }
            surface.Children.Add(image);
        }
        return surface;
    }

    private FrameworkElement UnplacedInputs(IReadOnlyList<DiagramBinding> rows)
    {
        var section = new StackPanel { Margin = new Thickness(12, 6, 0, 4) };
        section.Children.Add(Muted("Use Learn this control to place a saved input on the diagram."));
        foreach (var group in rows.GroupBy(row => (row.Action is null ? row.Input : row.InputDetail) ?? "No input assigned"))
        {
            var heading = Heading(group.Key, 13);
            heading.Margin = new Thickness(0, 8, 0, 0);
            section.Children.Add(heading);
            foreach (var row in group)
                section.Children.Add(AssignmentText(row));
        }
        return section;
    }

    private TextBlock AssignmentText(DiagramBinding row)
    {
        var context = string.Join(" / ", new[] { row.Action?.Category, row.Action?.Section }
            .Where(part => !string.IsNullOrWhiteSpace(part)).Distinct());
        return new TextBlock
        {
            Text = row.Label + (row.Inherited ? " · inherited" : "") +
                (context.Length > 0 ? $"\n{context}" : ""),
            Foreground = DiagramControlCard.BindingBrush(row, row.Address?.Layer ?? "Normal"),
            TextWrapping = TextWrapping.Wrap, FontSize = 13, Margin = new Thickness(0, 3, 0, 4)
        };
    }

    private static void Put(Canvas surface, FrameworkElement child, double x, double y)
    {
        Canvas.SetLeft(child, x);
        Canvas.SetTop(child, y);
        surface.Children.Add(child);
    }

    private static StackPanel Section(string name, FrameworkElement body)
    {
        var section = new StackPanel { Margin = new Thickness(0, 5, 14, 18) };
        section.Children.Add(Heading(name, 16));
        section.Children.Add(body);
        return section;
    }

    private static Expander Expand(string id, string name, FrameworkElement body, bool expanded) => new()
    {
        Tag = id,
        Header = Heading(name, 13),
        Content = body,
        IsExpanded = expanded,
        Margin = new Thickness(0, 4, 14, 12),
        Foreground = ColorResource("TextPrimary")
    };

    private static TextBlock Heading(string text, double size) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = size, FontWeight = FontWeights.SemiBold,
        TextWrapping = TextWrapping.Wrap
    };

    private static Brush ColorResource(string name) => (Brush)Application.Current.FindResource(name);
    private static TextBlock Muted(string text) => new()
    {
        Text = text, Foreground = ColorResource("TextSecondary"), TextWrapping = TextWrapping.Wrap, FontSize = 12
    };

    private static StackPanel Field(string name, FrameworkElement field)
    {
        var layout = new StackPanel();
        layout.Children.Add(Heading(name, 11));
        layout.Children.Add(field);
        AutomationProperties.SetName(field, name);
        return layout;
    }

    private sealed record Callout(HardwareHotspot Anchor, IReadOnlyList<HardwareControl> Gestures,
        IReadOnlyList<DiagramBinding> Bindings, int Number);
    private sealed record Placement(FrameworkElement Card, double X, double Y, Dock Side, int Number, string Control, string? Modifier);
    private sealed record DeviceChoice(DiagramDevice? Device)
    {
        public string? Id => Device?.Hardware.Id;
        public override string ToString() => Device?.Hardware.Name ?? "OTHER GAME INPUTS";
    }
}
