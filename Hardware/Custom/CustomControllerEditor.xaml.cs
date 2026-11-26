using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using VisualControlsBinder.Inputs;

namespace VisualControlsBinder.Hardware.Custom;

internal partial class CustomControllerEditor : Window
{
    private readonly string _id;
    private readonly List<CustomPart> _parts;
    private readonly IReadOnlyList<DeviceChoice> _devices;
    private readonly Dictionary<string, Border> _partSurfaces = new(StringComparer.Ordinal);
    private double _width;
    private double _height;
    private string? _selectedPart;
    private (CustomPart Part, double PanelWidth, double PanelHeight)? _copiedPart;
    private Point? _paletteStart;
    private Vector? _dragOffset;

    public CustomControllerEditor(CustomController? initial, IReadOnlyList<JoystickDevice> devices)
    {
        _id = initial?.Id ?? $"custom-{Guid.NewGuid():N}";
        _width = initial?.Width ?? 620;
        _height = initial?.Height ?? 420;
        _parts = initial?.Parts.ToList() ?? [];
        _devices = DeviceChoices(devices, initial);

        InitializeComponent();
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, CopyPart,
            (_, e) => e.CanExecute = _selectedPart is not null && !IsEditingField(e.OriginalSource as DependencyObject)));
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, PastePart,
            (_, e) => e.CanExecute = _copiedPart is not null && !IsEditingField(e.OriginalSource as DependencyObject)));
        ControllerName.Text = initial?.Name ?? "New controller";
        DevicePicker.ItemsSource = _devices;
        DevicePicker.SelectedItem = _devices.FirstOrDefault(choice => choice.Id == initial?.DeviceId) ?? _devices[0];
        Palette.ItemsSource = Enum.GetValues<CustomPartKind>()
            .Select(kind => new PaletteItem(kind, CustomPart.Label(kind))).ToArray();
        ShapePicker.ItemsSource = new[]
        {
            new ShapeChoice(CustomPanelShape.Rectangle, "Rectangle"),
            new ShapeChoice(CustomPanelShape.RoundedRectangle, "Rounded rectangle")
        };
        ShapePicker.DisplayMemberPath = nameof(ShapeChoice.Label);
        ShapePicker.SelectedIndex = initial?.Shape == CustomPanelShape.RoundedRectangle ? 1 : 0;
        CreateResizeHandles();
        DrawPanel();
    }

    public CustomController? Result { get; private set; }

    private CustomPanelShape Shape => (ShapePicker.SelectedItem as ShapeChoice)?.Shape ?? CustomPanelShape.Rectangle;

    private static IReadOnlyList<DeviceChoice> DeviceChoices(IReadOnlyList<JoystickDevice> devices, CustomController? initial)
    {
        var choices = new List<DeviceChoice> { new(null, null, "Choose later") };
        foreach (var group in devices.GroupBy(device => device.Name, StringComparer.Ordinal))
        {
            var sameName = group.ToArray();
            for (var i = 0; i < sameName.Length; i++)
            {
                var device = sameName[i];
                var label = sameName.Length == 1 ? device.Name : $"{device.Name} ({i + 1})";
                choices.Add(new DeviceChoice(device.Id, device.Product, label));
            }
        }

        if (initial?.DeviceId is Guid id && choices.All(choice => choice.Id != id))
            choices.Add(new DeviceChoice(id, initial.ProductId, "Previously selected device (disconnected)"));

        return choices;
    }

    private void ChangeShape(object sender, SelectionChangedEventArgs e) => UpdatePanelOutline();

    private void UpdatePanelOutline()
    {
        PanelOutline.CornerRadius = Shape == CustomPanelShape.RoundedRectangle ? new CornerRadius(24) : new CornerRadius(0);
    }

    private void DrawPanel()
    {
        ResizeSurface();
        PanelSurface.Children.Clear();
        _partSurfaces.Clear();

        foreach (var part in _parts)
        {
            var surface = new Border
            {
                Tag = part.Id,
                ToolTip = part.Name,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(6),
                Child = CustomPanelDrawing.CreatePart(part.Kind, part.QuarterTurns),
                Cursor = Cursors.SizeAll,
                Focusable = true
            };
            AutomationProperties.SetName(surface, part.Name);
            surface.MouseLeftButtonDown += StartMove;
            surface.MouseMove += MovePart;
            surface.MouseLeftButtonUp += EndMove;
            surface.KeyDown += MoveWithArrow;
            surface.GotKeyboardFocus += FocusPart;
            Position(surface, part);
            _partSurfaces.Add(part.Id, surface);
            PanelSurface.Children.Add(surface);
        }

        PositionBody();
        ShowSelectedPart();
    }

    private void Position(FrameworkElement surface, CustomPart part)
    {
        surface.Width = part.Width * _width;
        surface.Height = part.Height * _height;
        Canvas.SetLeft(surface, (part.X - part.Width / 2) * _width);
        Canvas.SetTop(surface, (part.Y - part.Height / 2) * _height);
    }

    private void SelectPart(string? id)
    {
        if (id == _selectedPart) return;
        CommitRename();
        _selectedPart = id;
        ShowSelectedPart();
    }

    private void ShowSelectedPart()
    {
        var part = _parts.FirstOrDefault(part => part.Id == _selectedPart);
        PartTools.Visibility = part is null ? Visibility.Collapsed : Visibility.Visible;
        SelectedPartName.Text = part?.Name ?? "";
        SelectedPartName.IsEnabled = part is not null;
        RemoveButton.IsEnabled = part is not null;
        RotatePartButton.Visibility = part?.CanRotate == true ? Visibility.Visible : Visibility.Collapsed;
        var accent = (Brush)FindResource("Accent");
        foreach (var (id, surface) in _partSurfaces)
            surface.BorderBrush = id == _selectedPart ? accent : Brushes.Transparent;
        PositionHandles();
    }

    private void CommitRename()
    {
        var index = _parts.FindIndex(part => part.Id == _selectedPart);
        if (index < 0) return;
        var part = _parts[index];
        var name = SelectedPartName.Text.Trim();
        if (name.Length == 0)
        {
            SelectedPartName.Text = part.Name;
            return;
        }
        if (name == part.Name) return;

        _parts[index] = part with { Name = name };
        if (_partSurfaces.TryGetValue(part.Id, out var surface))
        {
            surface.ToolTip = name;
            AutomationProperties.SetName(surface, name);
        }
    }

    private void CommitPartName(object sender, KeyboardFocusChangedEventArgs e) => CommitRename();

    private void FocusPart(object sender, KeyboardFocusChangedEventArgs e) => SelectPart((string)((Border)sender).Tag);

    private void ClearPartSelection(object sender, MouseButtonEventArgs e) => SelectPart(null);

    private void RemovePart(object sender, RoutedEventArgs e) => RemoveSelectedPart();

    private void EditorKeyDown(object sender, KeyEventArgs e)
    {
        if (_selectedPart is null || e.Key is not (Key.Delete or Key.Back) ||
            IsEditingField(e.OriginalSource as DependencyObject))
            return;
        RemoveSelectedPart();
        e.Handled = true;
    }

    private static bool IsEditingField(DependencyObject? element)
    {
        for (var current = element; current is not null; current =
            current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem)
                return true;
        }
        return false;
    }

    private void CopyPart(object sender, ExecutedRoutedEventArgs e)
    {
        CommitRename();
        if (_parts.FirstOrDefault(part => part.Id == _selectedPart) is not { } part)
            return;
        _copiedPart = (part, _width, _height);
        Status.Text = $"Copied {part.Name}.";
    }

    private void PastePart(object sender, ExecutedRoutedEventArgs e)
    {
        if (_copiedPart is not { } copied)
            return;
        CommitRename();
        var template = CustomPanelGeometry.Reframe(copied.Part, copied.PanelWidth, copied.PanelHeight, _width, _height);
        if (CustomPanelGeometry.OffsetCopy(template, _width, _height, _parts) is not { } positioned)
        {
            Status.Text = "Make more room on the panel before pasting this control.";
            return;
        }
        var part = positioned with
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = NextPartName(NumberedName(copied.Part.Name).Prefix)
        };
        AppendPart(part);
        _partSurfaces[part.Id].Focus();
        Status.Text = $"Pasted {part.Name}.";
    }

    private void RemoveSelectedPart()
    {
        _parts.RemoveAll(part => part.Id == _selectedPart);
        _selectedPart = null;
        DrawPanel();
        PanelWorkspace.Focus();
    }

    private void RotateSelectedPart(object sender, RoutedEventArgs e)
    {
        CommitRename();
        int index = _parts.FindIndex(part => part.Id == _selectedPart);
        if (index < 0 || !_parts[index].CanRotate)
            return;
        if (CustomPanelGeometry.RotateControl(_parts[index], _width, _height) is not { } rotated)
        {
            Status.Text = "Make more room on the panel before rotating this control.";
            return;
        }
        _parts[index] = rotated;
        var surface = _partSurfaces[rotated.Id];
        surface.Child = CustomPanelDrawing.CreatePart(rotated.Kind, rotated.QuarterTurns);
        Position(surface, rotated);
        PositionHandles();
        Status.Text = "";
    }

    private void AddFromPalette(object sender, RoutedEventArgs e)
    {
        var kind = (CustomPartKind)((Button)sender).Tag;
        var offset = .1 * (_parts.Count % 5);
        AddPart(kind, .3 + offset, .3 + offset);
    }

    private void StartPaletteDrag(object sender, MouseButtonEventArgs e) => _paletteStart = e.GetPosition(this);

    private void DragFromPalette(object sender, MouseEventArgs e)
    {
        if (_paletteStart is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - start;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _paletteStart = null;
        var paletteButton = (Button)sender;
        var data = new DataObject(typeof(CustomPartKind), (CustomPartKind)paletteButton.Tag);
        DragDrop.DoDragDrop(paletteButton, data, DragDropEffects.Copy);
    }

    private void PreviewPartDrop(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(CustomPartKind)) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropPart(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(CustomPartKind)) is not CustomPartKind kind) return;
        var point = e.GetPosition(PanelSurface);
        AddPart(kind, point.X / _width, point.Y / _height);
        e.Handled = true;
    }

    private void AddPart(CustomPartKind kind, double x, double y)
    {
        CommitRename();
        var (width, height) = CustomPart.DefaultSize(kind);
        var part = new CustomPart(Guid.NewGuid().ToString("N"), NextPartName(CustomPart.Label(kind)), kind,
            x, y, Math.Min(.8, width / _width), Math.Min(.8, height / _height));
        AppendPart(CustomPanelGeometry.Snap(part, x, y, _width, _height));
    }

    private string NextPartName(string prefix)
    {
        var used = _parts.Select(part => NumberedName(part.Name))
            .Where(name => name.Prefix.Equals(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(name => name.Number).ToHashSet();
        int number = 1;
        while (used.Contains(number))
            number++;
        return $"{prefix} {number}";
    }

    private static (string Prefix, int Number) NumberedName(string name)
    {
        int space = name.LastIndexOf(' ');
        return space > 0 && int.TryParse(name.AsSpan(space + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            && number > 0 ? (name[..space].TrimEnd(), number) : (name, 0);
    }

    private void AppendPart(CustomPart part)
    {
        _parts.Add(part);
        _selectedPart = part.Id;
        DrawPanel();
    }

    private void StartMove(object sender, MouseButtonEventArgs e)
    {
        var surface = (Border)sender;
        var id = (string)surface.Tag;
        var part = _parts.First(part => part.Id == id);
        SelectPart(id);
        surface.Focus();
        _dragOffset = e.GetPosition(PanelSurface) - new Point(part.X * _width, part.Y * _height);
        surface.CaptureMouse();
        e.Handled = true;
    }

    private void MovePart(object sender, MouseEventArgs e)
    {
        var surface = (Border)sender;
        if (_dragOffset is not Vector offset || !surface.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
        var point = e.GetPosition(PanelSurface) - offset;
        UpdatePosition(surface, point.X / _width, point.Y / _height);
    }

    private void EndMove(object sender, MouseButtonEventArgs e)
    {
        _dragOffset = null;
        ((Border)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private void MoveWithArrow(object sender, KeyEventArgs e)
    {
        var offset = e.Key switch
        {
            Key.Left => new Vector(-CustomPanelGeometry.GridStep, 0), Key.Right => new Vector(CustomPanelGeometry.GridStep, 0),
            Key.Up => new Vector(0, -CustomPanelGeometry.GridStep), Key.Down => new Vector(0, CustomPanelGeometry.GridStep),
            _ => new Vector()
        };
        if (offset.LengthSquared == 0) return;
        var surface = (Border)sender;
        var part = _parts.First(part => part.Id == (string)surface.Tag);
        UpdatePosition(surface, part.X + offset.X / _width, part.Y + offset.Y / _height);
        e.Handled = true;
    }

    private void UpdatePosition(Border surface, double x, double y)
    {
        var index = _parts.FindIndex(part => part.Id == (string)surface.Tag);
        var moved = CustomPanelGeometry.Snap(_parts[index], x, y, _width, _height);
        _parts[index] = moved;
        Position(surface, moved);
        PositionHandles();
    }

    private void SaveController(object sender, RoutedEventArgs e)
    {
        var name = ControllerName.Text.Trim();
        if (name.Length == 0)
        {
            Status.Text = "Enter a controller name.";
            ControllerName.Focus();
            return;
        }
        CommitRename();

        var device = (DeviceChoice)DevicePicker.SelectedItem;
        var controller = new CustomController(_id, name, Shape, _width, _height, device.Id, device.ProductId, _parts.ToArray());
        try
        {
            CustomControllerStore.Save(controller);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Status.Text = $"Could not save the controller. {ex.Message}";
            return;
        }

        Result = controller;
        DialogResult = true;
    }

    private sealed record DeviceChoice(Guid? Id, Guid? ProductId, string Label);
    private sealed record PaletteItem(CustomPartKind Kind, string Label);
    private sealed record ShapeChoice(CustomPanelShape Shape, string Label);
}
