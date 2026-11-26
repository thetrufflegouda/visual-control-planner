using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Hardware.Custom;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow : Window
{
    private readonly Dictionary<string, BitmapSource> _photos = new(StringComparer.Ordinal);
    private IReadOnlyList<HardwareDefinition> _standardDevices = [];
    private IReadOnlyList<CustomController> _customDevices = [];
    private IReadOnlyList<JoystickDevice> _connected = [];
    private DirectInputReader? _input;
    private HardwareDefinition? _hardware;
    private HardwareView? _view;
    private HardwareControl? _control;
    private bool _updatingControls;

    public MainWindow() : this(new PlanSessionStore()) { }

    internal MainWindow(PlanSessionStore sessions)
    {
        _planSessions = sessions;
        InitializeComponent();
        PreviewKeyDown += CancelPhotoTurnWithEscape;
        Loaded += Start;
        Closing += SavePlanSession;
        Closed += (_, _) =>
        {
            StopLearning();
            _input?.Dispose();
        };
    }

    private void Start(object sender, RoutedEventArgs e)
    {
        Loaded -= Start;
        try
        {
            _standardDevices = HardwareTemplates.Load();
            _customDevices = CustomControllerStore.Load();
        }
        catch (Exception error)
        {
            Notify(error.Message);
        }
        try { _addedGames = AddedGameStore.Load(); }
        catch (Exception error) { Notify($"Could not read added games. {error.Message}"); }
        GamePicker.DisplayMemberPath = nameof(GameTarget.Name);
        PopulateGamePicker(RestorePlanSession());
        ScanDevices();
        RestoreSelectedDeviceAndControl();
    }

    private static T? Single<T>(IEnumerable<T> candidates) where T : class
    {
        var matches = candidates.Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private void DeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_photoTurn is not null)
            EndPhotoTurn();
        StopLearning();
        _hardware = (DevicePicker.SelectedItem as HardwareChoice)?.Definition;
        _view = _hardware?.Views.FirstOrDefault();
        _control = null;
        _updatingControls = true;
        ControlPicker.ItemsSource = _hardware?.Controls
            .GroupBy(control => control.Location ?? control.Id).Select(group => group.First()).ToArray();
        LayerPicker.ItemsSource = InputLayers(_hardware);
        LayerPicker.SelectedIndex = 0;
        _updatingControls = false;
        DrawViewTabs();
        SelectControl(null);
    }

    private void DrawViewTabs()
    {
        ViewTabs.Children.Clear();
        var canTurn = _hardware?.Views.Count > 1;
        PhotoCanvas.Cursor = canTurn ? Cursors.ScrollWE : null;
        if (_hardware is null)
            return;
        foreach (var view in _hardware.Views)
        {
            var tab = new Button { Content = view.Name, Margin = new Thickness(0, 0, 6, 0) };
            if (view == _view)
                tab.SetResourceReference(StyleProperty, "PrimaryButton");
            tab.Click += (_, _) => ShowPhotoView(view);
            ViewTabs.Children.Add(tab);
        }
        if (canTurn)
            ViewTabs.Children.Add(new TextBlock
            {
                Text = "Drag sideways to turn",
                FontSize = 11,
                Margin = new Thickness(4, 4, 4, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brush("TextSecondary")
            });
    }

    private void ShowPhotoView(HardwareView view)
    {
        if (_view == view)
            return;
        _view = view;
        DrawViewTabs();
        DrawPhoto();
    }

    private void ControlChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingControls && ControlPicker.SelectedItem is HardwareControl control)
        {
            var location = control.Location ?? control.Id;
            if (_view?.Hotspots.Any(spot => LocationOf(spot.ControlId) == location) != true && _hardware is not null)
            {
                _view = _hardware.Views.FirstOrDefault(view => view.Hotspots.Any(spot => LocationOf(spot.ControlId) == location)) ?? _view;
                DrawViewTabs();
            }
            SelectControl(control);
        }
    }

    private void SelectControl(HardwareControl? chosen, bool stopLearning = true)
    {
        if (stopLearning)
            StopLearning();
        _control = chosen;
        var group = chosen is null || _hardware is null ? [] : _hardware.Controls
            .Where(item => (item.Location ?? item.Id) == (chosen.Location ?? chosen.Id)).ToArray();
        _updatingControls = true;
        ControlPicker.SelectedItem = (ControlPicker.ItemsSource as IEnumerable<HardwareControl>)?
            .FirstOrDefault(item => (item.Location ?? item.Id) == (chosen?.Location ?? chosen?.Id));
        GestureRow.Visibility = group.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        GesturePicker.ItemsSource = group;
        GesturePicker.SelectedItem = chosen;
        _updatingControls = false;
        UpdateControl(revealAssignment: true);
        DrawPhoto();
    }

    private void GestureChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls)
            return;
        StopLearning();
        _control = GesturePicker.SelectedItem as HardwareControl;
        UpdateControl(revealAssignment: true);
        DrawPhoto();
    }

    private void PhotoSizeChanged(object sender, SizeChangedEventArgs e) => DrawPhoto();
    private string? LocationOf(string id)
    {
        var control = _hardware?.Controls.FirstOrDefault(item => item.Id == id);
        return control?.Location ?? control?.Id;
    }

    private void DrawPhoto()
    {
        PhotoCanvas.Children.Clear();
        if (_hardware is null || _view is null || PhotoCanvas.ActualWidth < 1 || PhotoCanvas.ActualHeight < 1)
            return;
        bool mirrored = _hardware.IsViewMirrored(_view);
        var custom = FindCustom(_hardware);
        var photo = custom is null ? LoadPhoto(_view.Image) : null;
        var sourceHeight = photo?.PixelHeight ?? 1;
        var cropTop = photo is not null && _hardware.ModelId is "vkb-left" or "vkb-right"
            ? (int)(sourceHeight * 0.06) : 0;
        if (photo is not null && cropTop > 0)
            photo = new CroppedBitmap(photo, new Int32Rect(0, cropTop, photo.PixelWidth, sourceHeight - cropTop * 2));
        double ratio = photo is not null ? (double)photo.PixelWidth / photo.PixelHeight
            : custom is not null ? custom.Width / custom.Height : 1;
        const double margin = 12;
        double width = Math.Max(1, Math.Min(PhotoCanvas.ActualWidth - 2 * margin, (PhotoCanvas.ActualHeight - 2 * margin) * ratio));
        double height = width / ratio;
        double left = (PhotoCanvas.ActualWidth - width) / 2;
        double top = (PhotoCanvas.ActualHeight - height) / 2;

        if (photo is not null)
        {
            var image = new Image { Source = photo, Width = width, Height = height, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            if (mirrored)
            {
                image.RenderTransformOrigin = new Point(0.5, 0.5);
                image.RenderTransform = new ScaleTransform(-1, 1);
            }
            Place(image, left, top);
        }
        else if (custom is not null)
        {
            var body = new Border
            {
                Width = width, Height = height, BorderBrush = Brush("PanelBorder"), BorderThickness = new Thickness(2),
                Background = Brush("PanelBackground"),
                CornerRadius = new CornerRadius(custom.Shape == CustomPanelShape.RoundedRectangle ? 20 : 2)
            };
            Place(body, left, top);
        }

        foreach (var spot in _view.Hotspots.OrderByDescending(item => item.Width * item.Height))
        {
            var target = _hardware.Controls.FirstOrDefault(item => item.Id == spot.ControlId);
            if (target is null)
                continue;
            bool selected = _control is not null && (target.Location ?? target.Id) == (_control.Location ?? _control.Id);
            var part = custom?.Parts.FirstOrDefault(item => item.Id == (target.Location ?? target.Id));
            double scaleY = height * sourceHeight / (sourceHeight - cropTop * 2);
            var control = CreateHotspot(target, selected, part, spot.Width * width, spot.Height * scaleY);
            double x = mirrored ? 1 - spot.X : spot.X;
            double y = spot.Y - (double)cropTop / sourceHeight;
            Place(control, left + x * width - control.Width / 2, top + y * scaleY - control.Height / 2);
        }
    }

    private Button CreateHotspot(HardwareControl target, bool selected, CustomPart? part, double width, double height)
    {
        var isPanelControl = target.Location?.StartsWith("cm3-b", StringComparison.Ordinal) == true ||
            target.Id.StartsWith("base-b", StringComparison.Ordinal);
        var border = new FrameworkElementFactory(typeof(Border), "Highlight");
        var accent = (Color)FindResource("AccentColor");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(isPanelControl ? 3 : Math.Min(width, height) / 2));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        if (part is not null)
        {
            border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        }
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        foreach (var property in new[] { IsMouseOverProperty, IsKeyboardFocusedProperty })
        {
            var trigger = new Trigger { Property = property, Value = true };
            trigger.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("Accent"), "Highlight"));
            if (!selected)
                trigger.Setters.Add(new Setter(Border.BackgroundProperty,
                    new SolidColorBrush(Color.FromArgb(65, accent.R, accent.G, accent.B)), "Highlight"));
            template.Triggers.Add(trigger);
        }
        if (selected)
        {
            border.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(100, accent.R, accent.G, accent.B)));
            border.SetValue(Border.BorderBrushProperty, Brush("Accent"));
            border.SetValue(EffectProperty, new DropShadowEffect
            {
                Color = accent, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.85
            });
        }
        var button = new Button
        {
            Width = width, Height = height, MinHeight = 0, Padding = new Thickness(0),
            ToolTip = target.Name, Template = template, Cursor = Cursors.Hand,
            Content = part is null ? null : CustomPanelDrawing.CreatePart(part.Kind, part.QuarterTurns)
        };
        AutomationProperties.SetName(button, target.Name);
        button.Click += (_, _) => SelectControl(target);
        return button;
    }

    private void Place(FrameworkElement element, double left, double top)
    {
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        PhotoCanvas.Children.Add(element);
    }

    private BitmapSource? LoadPhoto(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        if (!_photos.TryGetValue(path, out var photo))
        {
            var source = new BitmapImage(new Uri(HardwareTemplates.AssetPath(path), UriKind.Absolute));
            photo = PhotoBackdrop.Remove(source, path);
            _photos[path] = photo;
        }
        return photo;
    }

    private ImageSource? Thumbnail(HardwareDefinition device) =>
        LoadPhoto(device.Views.FirstOrDefault()?.Image ?? "");

    private Brush Brush(string resource) => (Brush)FindResource(resource);
    private void Notify(string message) => StatusText.Text = message;

}
