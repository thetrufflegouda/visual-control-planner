using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using VisualControlsBinder.Hardware;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private static readonly IReadOnlyDictionary<string, (int Row, int Column, string Glyph)> PadPositions =
        new Dictionary<string, (int, int, string)>(StringComparer.Ordinal)
        {
            ["up"] = (0, 1, "▲"),
            ["left"] = (1, 0, "◀"),
            ["press"] = (1, 1, "●"),
            ["right"] = (1, 2, "▶"),
            ["down"] = (2, 1, "▼")
        };

    private readonly Dictionary<string, Button> _padGestureButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _padLayerButtons = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Button> _padAxisButtons = new(StringComparer.Ordinal);
    private HardwareDefinition? _padHardware;
    private string? _padLocation;
    private bool _updatingHardwareShiftToggle;

    private void RefreshControlPad()
    {
        if (ControlPadPanel is null)
            return;
        ControlPadPanel.Visibility = _hardware is null ? Visibility.Collapsed : Visibility.Visible;
        bool selected = _control is not null;
        var selectionVisibility = selected ? Visibility.Visible : Visibility.Collapsed;
        bool showDefaultTiming = ActivationButtons.Visibility == Visibility.Visible && DefaultActivationButton.Visibility == Visibility.Visible;
        ControlPadPanel.Width = selected ? (showDefaultTiming ? 310 : 232) : 180;
        ControlPadSelectionHeader.Visibility = selectionVisibility;
        ControlPadAssignments.Visibility = selected && !IsGremlinModifier(CurrentAddress) ? Visibility.Visible : Visibility.Collapsed;
        ControlPadInputButtons.Visibility = selectionVisibility;
        ClearPadSelectionButton.IsEnabled = !IsGuidedLearning;
        var location = _control?.Location ?? _control?.Id;
        bool hardwareChanged = !ReferenceEquals(_hardware, _padHardware);
        if (hardwareChanged)
        {
            _padHardware = _hardware;
            UpdatePadLayers();
            UpdatePadAxes();
        }
        if (hardwareChanged || !string.Equals(location, _padLocation, StringComparison.Ordinal))
        {
            _padLocation = location;
            UpdatePadGestures();
        }
        ControlPadAxes.Visibility = !selected && _padAxisButtons.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;
        string clearLabel = _padAxisButtons.Count > 0 ? "Back to stick axes" : "Clear control selection";
        ClearPadSelectionButton.ToolTip = clearLabel;
        AutomationProperties.SetName(ClearPadSelectionButton, clearLabel);
        _updatingHardwareShiftToggle = true;
        ShiftLayerToggle.IsChecked = Layer == "Shift";
        _updatingHardwareShiftToggle = false;
        foreach (var (id, button) in _padLayerButtons)
            MarkCurrent(button, id == Layer);
        foreach (var (id, button) in _padGestureButtons)
            MarkCurrent(button, id == _control?.Id);
        foreach (var (id, button) in _padAxisButtons)
            MarkCurrent(button, id == _control?.Id);
    }

    private void UpdatePadGestures()
    {
        _padGestureButtons.Clear();
        ControlPadGestures.Children.Clear();
        HardwareControl[] controls = GesturePicker.Items.OfType<HardwareControl>()
            .Where(control => !_padAxisButtons.ContainsKey(control.Id)).ToArray();
        ControlPadGestures.Visibility = controls.Length > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (controls.Length < 2)
            return;

        var directions = controls.Select(control => GesturePart(control.Id)).ToHashSet(StringComparer.Ordinal);
        if (directions.IsSupersetOf(["up", "left", "right", "down"]) &&
            controls.All(control => PadPositions.ContainsKey(GesturePart(control.Id))))
        {
            var pad = new Grid { HorizontalAlignment = HorizontalAlignment.Center };
            KeyboardNavigation.SetDirectionalNavigation(pad, KeyboardNavigationMode.Contained);
            for (int index = 0; index < 3; index++)
            {
                pad.RowDefinitions.Add(new RowDefinition { Height = new GridLength(44) });
                pad.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            }
            foreach (var control in controls)
            {
                var (row, column, glyph) = PadPositions[GesturePart(control.Id)];
                var button = CreateGestureButton(control, glyph);
                button.FontFamily = new FontFamily("Segoe UI Symbol");
                button.FontSize = 18;
                button.Margin = new Thickness(2);
                Grid.SetRow(button, row);
                Grid.SetColumn(button, column);
                pad.Children.Add(button);
            }
            ControlPadGestures.Children.Add(pad);
            return;
        }

        foreach (var control in controls)
        {
            var button = CreateGestureButton(control, GestureLabel(control));
            button.Margin = new Thickness(0, 0, 0, 4);
            ControlPadGestures.Children.Add(button);
        }
    }

    private void UpdatePadAxes()
    {
        _padAxisButtons.Clear();
        ControlPadAxisButtons.Children.Clear();
        if (_hardware?.ModelId is "vkb-left" or "vkb-right")
        {
            foreach (var (id, label) in new[] { ("stick-x", "X"), ("stick-y", "Y"), ("twist", "TWIST") })
            {
                if (_hardware.Controls.FirstOrDefault(control => control.Id == id) is not { } axis)
                    continue;
                var button = CreatePadButton(label, axis.Name);
                button.Margin = new Thickness(1);
                button.MinHeight = 26;
                button.Click += (_, _) => SelectPadAxis(axis);
                _padAxisButtons.Add(id, button);
                ControlPadAxisButtons.Children.Add(button);
            }
        }
    }

    private void ClearPadSelection(object sender, RoutedEventArgs e)
    {
        if (!IsGuidedLearning)
            SelectControl(null);
    }

    private void SelectPadAxis(HardwareControl axis)
    {
        string location = axis.Location ?? axis.Id;
        if (_view?.Hotspots.Any(spot => LocationOf(spot.ControlId) == location) != true &&
            _hardware?.Views.FirstOrDefault(view => view.Hotspots.Any(spot => LocationOf(spot.ControlId) == location)) is { } view)
        {
            _view = view;
            DrawViewTabs();
        }
        SelectControl(axis);
    }

    private void UpdatePadLayers()
    {
        _padLayerButtons.Clear();
        ControlPadModeButtons.Children.Clear();
        bool hasModes = _hardware?.ModelId == "virpil-cm3";
        ShiftLayerToggle.Visibility = hasModes ? Visibility.Collapsed : Visibility.Visible;
        ControlPadModes.Visibility = hasModes ? Visibility.Visible : Visibility.Collapsed;
        if (!hasModes)
            return;

        foreach (string layer in HardwareLayers(_hardware))
        {
            var button = CreatePadButton(layer["Mode ".Length..], layer);
            button.Padding = new Thickness(0);
            button.Margin = new Thickness(1);
            button.MinHeight = 25;
            button.Click += (_, _) => LayerPicker.SelectedItem = layer;
            _padLayerButtons.Add(layer, button);
            ControlPadModeButtons.Children.Add(button);
        }
    }

    private void ShiftLayerChanged(object sender, RoutedEventArgs e)
    {
        if (_updatingHardwareShiftToggle)
            return;
        string layer = ShiftLayerToggle.IsChecked == true ? "Shift" : "Normal";
        if (LayerPicker is not null && Layer != layer && LayerPicker.Items.Contains(layer))
            LayerPicker.SelectedItem = layer;
    }

    private Button CreateGestureButton(HardwareControl control, string content)
    {
        var button = CreatePadButton(content, control.Name);
        button.Click += (_, _) => GesturePicker.SelectedItem = control;
        _padGestureButtons.Add(control.Id, button);
        return button;
    }

    private static Button CreatePadButton(string text, string name)
    {
        var button = new Button
        {
            Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
            Padding = new Thickness(5, 3, 5, 3),
            ToolTip = name,
            FontSize = 12
        };
        AutomationProperties.SetName(button, name);
        return button;
    }

    private static void MarkCurrent(Button button, bool current)
    {
        if (current)
            button.SetResourceReference(StyleProperty, "PrimaryButton");
        else
            button.ClearValue(StyleProperty);
        AutomationProperties.SetItemStatus(button, current ? "Selected" : "");
    }

    private static string GesturePart(string id) => id[(id.LastIndexOf('-') + 1)..];

    private static string GestureLabel(HardwareControl control) => GesturePart(control.Id) switch
    {
        "first" => "First detent",
        "second" => "Second detent",
        "cw" => "Clockwise",
        "ccw" => "Counterclockwise",
        "press" => "Press",
        "center" => "Center",
        "up" => "Up",
        "down" => "Down",
        "left" => "Left",
        "right" => "Right",
        "x" or "horizontal" when control.Kind == "Axis" => "Horizontal axis",
        "y" or "vertical" when control.Kind == "Axis" => "Vertical axis",
        _ => control.Name
    };
}
