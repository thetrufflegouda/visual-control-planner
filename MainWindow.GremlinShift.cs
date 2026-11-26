using System.Windows;
using System.Windows.Controls;
using VisualControlsBinder.Games;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private const string GremlinShiftLayer = GremlinShiftSetting.Layer;
    private bool _settingGremlinModifier;
    private GremlinShiftSetting? _savedGremlinShift;
    private GremlinProfileShift? _savedGremlinModifier;

    private GremlinShiftSetting? ActiveGremlinShift => SelectedGame is { } game && OutputFor(game) == OutputMode.JoystickGremlin
        ? _plan.GetGremlinShift(ScopeOf(game)) ?? _savedGremlinShift : null;

    private string? GremlinShiftDescription
    {
        get
        {
            if (ActiveGremlinShift is not { Behavior: { } behavior } shift)
                return null;
            string label = behavior == GremlinShiftBehavior.Toggle ? "GREMLIN SHIFT TOGGLE" : "GREMLIN SHIFT HOLD";
            if (FindPlannedLayout(shift.Address.Device) is { } layout &&
                layout.Controls.FirstOrDefault(control => control.Id == shift.Address.Control) is { } control)
                return $"{label} · {layout.Name} · {control.Name}";
            if (_savedGremlinModifier is { } source)
            {
                var device = _profiles.Gremlin?.Devices.FirstOrDefault(item => item.Id == source.Device)?.Name;
                return $"{label} · {device ?? "Controller"} · Button {source.Button}";
            }
            return label;
        }
    }

    private string? PreferredGremlinBase(GameTarget game, GremlinProfile profile)
    {
        var baseModes = profile.Modes.Except(profile.ShiftModes).ToArray();
        var preferred = _plan.GetGremlinShift(ScopeOf(game))?.BaseMode;
        if (preferred is not null && baseModes.Contains(preferred))
            return preferred;
        var active = profile.ActiveShifts.Select(shift => shift.BaseMode).Distinct().ToArray();
        return active is [var only] ? only : baseModes.Contains("Default") ? "Default" : baseModes.FirstOrDefault();
    }

    private void ReadSavedGremlinShift()
    {
        _savedGremlinShift = null;
        _savedGremlinModifier = null;
        if (SelectedGame is not { } game || _profiles.Gremlin is not { } profile || OutputFor(game) != OutputMode.JoystickGremlin ||
            PreferredGremlinBase(game, profile) is not { } mode ||
            profile.ActiveShifts.Where(shift => shift.BaseMode == mode).Take(2).ToArray() is not [var source])
            return;
        _savedGremlinModifier = source;
        var layouts = PlannedLayouts().Where(layout => DiagramGremlinDevice(layout) == source.Device).Take(2).ToArray();
        var address = new ControlAddress("", "", "Normal");
        if (layouts is [var device])
        {
            var layers = HardwareLayers(device);
            address = new ControlAddress(device.Id, "", layers[0]);
            var button = InputSignal.Button(source.Button);
            address = layers.SelectMany(layer => device.Controls.Select(control => new ControlAddress(device.Id, control.Id, layer)))
                .FirstOrDefault(candidate => KnownSignal(candidate, device) == button) ?? address;
        }
        _savedGremlinShift = new GremlinShiftSetting(ScopeOf(game), address, source.Behavior) { BaseMode = mode };
    }

    private static ControlAddress PhysicalAddress(ControlAddress address, HardwareDefinition? device) =>
        address.Layer == GremlinShiftLayer ? address with { Layer = HardwareLayers(device)[0] } : address;

    private InputSignal? KnownSignal(ControlAddress address, HardwareDefinition? device = null)
    {
        if (address.Layer == GremlinShiftLayer && device is null)
            device = FindPlannedLayout(address.Device);
        return _plan.GetSignal(PhysicalAddress(address, device)) ?? ReferenceSignal(address, device);
    }

    private bool IsGremlinModifier(ControlAddress? address)
    {
        if (address is null || ActiveGremlinShift is not { Behavior: not null } shift || address.Device != shift.Address.Device)
            return false;
        if (address.Control == shift.Address.Control && (address == shift.Address || address.Layer == GremlinShiftLayer))
            return true;
        if (shift == _savedGremlinShift && _savedGremlinModifier is { } saved)
            return KnownSignal(address) == InputSignal.Button(saved.Button);
        return KnownSignal(shift.Address) is { Kind: "button" } signal && KnownSignal(address) == signal;
    }

    private void RefreshGremlinLayer()
    {
        var layers = InputLayers(_hardware);
        if (!LayerPicker.Items.OfType<string>().SequenceEqual(layers))
        {
            string current = Layer;
            _updatingControls = true;
            LayerPicker.ItemsSource = layers;
            LayerPicker.SelectedItem = layers.Contains(current) ? current : layers[0];
            _updatingControls = false;
        }
        GremlinShiftLayerButton.Visibility = _hardware is not null && layers.Contains(GremlinShiftLayer)
            ? Visibility.Visible : Visibility.Collapsed;
        GremlinShiftLayerButton.IsEnabled = !IsGuidedLearning;
        MarkCurrent(GremlinShiftLayerButton, Layer == GremlinShiftLayer);
    }

    private void RefreshGremlinModifier(ControlAddress? address, InputSignal? signal)
    {
        bool available = SelectedGame is { } game && OutputFor(game) == OutputMode.JoystickGremlin &&
            address is not null && address.Layer != GremlinShiftLayer &&
            _control?.Kind.Equals("axis", StringComparison.OrdinalIgnoreCase) != true && signal?.Kind is null or "button";
        GremlinShiftOptions.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        if (!available)
            return;
        _settingGremlinModifier = true;
        try
        {
            GremlinShiftPicker.IsEnabled = !IsGuidedLearning;
            GremlinShiftPicker.SelectedIndex = IsGremlinModifier(address) ? ActiveGremlinShift?.Behavior switch
            {
                GremlinShiftBehavior.Momentary => 1,
                GremlinShiftBehavior.Toggle => 2,
                _ => 0
            } : 0;
        }
        finally { _settingGremlinModifier = false; }
    }

    private void GremlinModifierChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingGremlinModifier || CurrentAddress is not { } address || SelectedGame is not { } game ||
            OutputFor(game) != OutputMode.JoystickGremlin || GremlinShiftOptions.Visibility != Visibility.Visible)
            return;
        var behavior = GremlinShiftPicker.SelectedIndex switch
        {
            1 => GremlinShiftBehavior.Momentary,
            2 => GremlinShiftBehavior.Toggle,
            _ => (GremlinShiftBehavior?)null
        };
        var previous = ActiveGremlinShift;
        if (behavior is null && !IsGremlinModifier(address))
            return;
        _plan.SetGremlinShift(ScopeOf(game), behavior is null && previous is not null ? previous.Address : address,
            behavior, previous?.BaseMode);
        UpdateControl(revealAssignment: true);
        Notify(behavior is null ? "Gremlin Shift turned off. Preview / export to update the profile."
            : $"{_control?.Name} is reserved for Gremlin Shift. Preview / export to apply.");
    }

    private void ShowGremlinShiftLayer(object sender, RoutedEventArgs e) =>
        LayerPicker.SelectedItem = Layer == GremlinShiftLayer ? HardwareLayers(_hardware)[0] : GremlinShiftLayer;
}
