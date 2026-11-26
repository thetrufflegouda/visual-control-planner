using System.IO;
using System.Windows;
using System.Windows.Controls;
using VisualControlsBinder.Games;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private static readonly OutputChoice[] OutputChoices =
    [
        new(OutputMode.DirectToGame, "Direct to game"),
        new(OutputMode.JoystickGremlin, "Joystick Gremlin")
    ];
    private bool _settingOutput;
    private bool _settingVirtualRoute;

    private OutputMode OutputFor(GameTarget game) => game.SupportsNativeEditing
        ? _plan.OutputModes.GetValueOrDefault(game.Id, game.DefaultOutput) : OutputMode.JoystickGremlin;

    private void SelectOutput(GameTarget game)
    {
        _settingOutput = true;
        RoutePicker.ItemsSource = game.SupportsNativeEditing ? OutputChoices : [OutputChoices[1]];
        RoutePicker.DisplayMemberPath = nameof(OutputChoice.Label);
        RoutePicker.SelectedItem = OutputChoices.FirstOrDefault(choice => choice.Mode == OutputFor(game)) ?? OutputChoices[0];
        RoutePicker.ToolTip = game.SupportsNativeEditing ? null : game.Kind == GameKind.Discovered
            ? "Actions are read from the selected binding file. Use Joystick Gremlin to route your controls."
            : "Native file editing is unavailable for this game. You can export a Gremlin profile.";
        _settingOutput = false;
    }

    private async void OutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingOutput || SelectedGame is not { } game || RoutePicker.SelectedItem is not OutputChoice choice ||
            OutputFor(game) == choice.Mode)
            return;
        _plan.OutputModes[game.Id] = choice.Mode;
        await LoadGame();
    }

    private string StarProfilePath(GameTarget game) => _plan.SourceProfiles.GetValueOrDefault(game.Id)
        ?? StarCitizenGame.FindProfile(GameFolder(game)) ?? "";

    private string GremlinProfilePath(GameTarget game) => _plan.GremlinProfiles.GetValueOrDefault(game.Id,
        // Plans written before output selection used SourceProfiles for SWG's Gremlin template.
        game.IsSwg ? _plan.SourceProfiles.GetValueOrDefault(game.Id, "") : "");

    private string CurrentProfilePath(GameTarget game)
    {
        if (OutputFor(game) == OutputMode.JoystickGremlin)
            return GremlinProfilePath(game);
        if (game.IsStarCitizen)
            return StarProfilePath(game);
        if (game.IsSwg)
            return _profiles.SwgCatalog?.SourceFiles.FirstOrDefault() ?? _plan.CharacterProfiles.GetValueOrDefault(game.Id, "");
        if (game.Kind == GameKind.Squadrons)
            return SquadronsProfile(game) ?? "";
        if (game.Kind == GameKind.CliffsOfDover)
            return CliffsProfilePath(game);
        if (game.IsMsfs)
            return _plan.SourceProfiles.GetValueOrDefault(MsfsBindingProfileKey(game)) ??
                (_hardware is { } hardware && ConnectedDevice(hardware) is { } controller
                    ? UniqueMsfsProfile(game, controller)?.Path : null) ?? "";
        return FlightProfilePath(game) ?? "";
    }

    private void UpdateVirtualRouting(IReadOnlyList<GremlinRouteView> effective)
    {
        QueuePatchBayRefresh();
        UpdateNativeJoystick();
        ControlPadVJoyRoute.Visibility = Visibility.Collapsed;
        if (SelectedGame is not { } game || OutputFor(game) != OutputMode.JoystickGremlin)
        {
            VirtualRoutingPanel.Visibility = Visibility.Collapsed;
            return;
        }
        VirtualRoutingPanel.Visibility = Visibility.Visible;
        AutoMapInputsButton.IsEnabled = _plan.Devices.Count > 0;
        _settingVirtualRoute = true;
        try
        {
            var address = CurrentAddress;
            var planned = address is null ? null : _plan.GetVirtualRoute(ScopeOf(game), address);
            var routed = effective.Select(row => row.Target.Output).OfType<GremlinOutput>().Distinct().ToArray();
            var route = planned ?? (address is not null && routed is [var output]
                ? new VirtualRoute(ScopeOf(game), address, output.DeviceId, output) : null);
            var signal = address is null ? null : KnownSignal(address, _hardware);
            bool reserved = IsGremlinModifier(address);
            if (address is not null && !reserved)
            {
                ControlPadVJoyRoute.Visibility = Visibility.Visible;
                var savedRoutes = effective.Any(row => row.Staged) ||
                    (_plan.RemovedVirtualRoutes ?? []).Any(removal => removal.Game == ScopeOf(game) && !removal.Applied)
                    ? ControlRoutes(game, address, savedOnly: true) : effective;
                bool differs = !effective.Select(row => row.Target).ToHashSet().SetEquals(savedRoutes.Select(row => row.Target));
                VJoyRouteSummary.Text = DescribeRoutes(effective, differs);
                if (differs)
                    VJoyRouteSummary.Text += $"{Environment.NewLine}Saved in Gremlin · {DescribeRoutes(savedRoutes, false)}";
            }
            string kind = signal?.Kind ?? (_control?.Kind.Equals("axis", StringComparison.OrdinalIgnoreCase) == true ? "axis" : "button");
            var outputs = (_profiles.Gremlin?.Outputs ?? VJoyOutputs.Read() ?? []).Where(output => output.Kind == kind &&
                (output.Kind != "button" || output.InputId <= GremlinButtonLimit(game))).ToArray();
            var devices = new[] { new VirtualDeviceChoice(0, "Automatic") }.Concat(outputs.Select(output => output.DeviceId)
                .Distinct().Order().Select(id => new VirtualDeviceChoice(id, $"vJoy {id}"))).ToList();
            if (route is { } saved && !devices.Any(choice => choice.Id == saved.DeviceId))
                devices.Add(new VirtualDeviceChoice(saved.DeviceId, $"vJoy {saved.DeviceId} (unavailable)"));
            VJoyDevicePicker.ItemsSource = devices;
            VJoyDevicePicker.SelectedItem = devices.First(choice => choice.Id == (route?.DeviceId ?? 0));
            var channels = outputs.Where(output => output.DeviceId == route?.DeviceId).ToList();
            if (route?.Output is { } selected && !channels.Contains(selected))
                channels.Add(selected);
            VJoyInputPicker.ItemsSource = channels;
            VJoyInputPicker.SelectedItem = route?.Output;
            VJoyDevicePicker.IsEnabled = address is not null && !reserved && (outputs.Length > 0 || route is not null);
            VJoyInputPicker.IsEnabled = address is not null && !reserved && route is not null && channels.Count > 0;
            VirtualRoutingHint.Text = address is null ? "Pick a physical control to choose its output."
                : reserved ? "Reserved for the Gremlin Shift modifier."
                : kind == "hat" ? "Gremlin output uses button or axis inputs."
                : outputs.Length == 0 ? "Choose a Gremlin profile with vJoy outputs or configure a vJoy device."
                : route is { Output: null } ? "Choose an output on this vJoy device."
                : routed.Length > 1 ? "This control has multiple outputs. Choose one destination for new assignments."
                : route is not null ? "This output applies to the selected control and layer."
                : "Automatic uses an existing game or Gremlin route when available.";
        }
        finally { _settingVirtualRoute = false; }

        static string DescribeRoutes(IReadOnlyList<GremlinRouteView> rows, bool pending) => rows.Count == 0
            ? pending ? "Disconnect pending" : "No output assigned."
            : string.Join(Environment.NewLine, rows.Select(row =>
                (row.Target.Output?.ToString() ?? $"Keyboard · {row.Target.Keyboard}") +
                (row.Inherited ? " · Inherited from base" : "") + (pending ? " · Planned" : "")).Distinct());
    }

    private void UpdateNativeJoystick()
    {
        bool applies = SelectedGame is { IsSwg: true } game && OutputFor(game) == OutputMode.DirectToGame;
        NativeJoystickPanel.Visibility = applies ? Visibility.Visible : Visibility.Collapsed;
        if (!applies || SelectedGame is not { } selected)
            return;
        var id = _plan.NativeJoysticks.GetValueOrDefault(selected.Id);
        var mapped = id is null ? null : FindPlannedLayout(id);
        NativeJoystickHint.Text = mapped is null ? "Associate the joystick selected in the SWG launcher."
            : $"SWG joystick: {mapped.Name}";
        ChooseNativeJoystickButton.IsEnabled = _hardware is not null && _hardware.Id != id;
    }

    private void ChooseNativeJoystick(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not { IsSwg: true } game || _hardware is null || OutputFor(game) != OutputMode.DirectToGame)
            return;
        _plan.NativeJoysticks[game.Id] = _hardware.Id;
        UpdateControl(revealAssignment: true);
    }

    private void VJoyDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingVirtualRoute || SelectedGame is not { } game || CurrentAddress is not { } address ||
            VJoyDevicePicker.SelectedItem is not VirtualDeviceChoice choice)
            return;
        var existing = _plan.GetVirtualRoute(ScopeOf(game), address);
        if (existing?.DeviceId != choice.Id)
            SetVirtualRouteForPhysicalInput(game, address, choice.Id, null);
        UpdateControl(revealAssignment: true);
    }

    private void VJoyInputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingVirtualRoute || SelectedGame is not { } game || CurrentAddress is not { } address ||
            VJoyInputPicker.SelectedItem is not GremlinOutput output)
            return;
        SetVirtualRouteForPhysicalInput(game, address, output.DeviceId, output);
        UpdateControl(revealAssignment: true);
        Notify($"{_control?.Name} · {output}");
    }

    private void SetVirtualRouteForPhysicalInput(GameTarget game, ControlAddress address, int deviceId, GremlinOutput? output)
    {
        if (IsGremlinModifier(address))
            return;
        string scope = ScopeOf(game);
        var signal = KnownSignal(address);
        foreach (var alias in VJoyRouteAliases(address, signal))
            _plan.SetVirtualRoute(scope, alias, deviceId, output);
        QueueDiagramRefresh();
        QueuePatchBayRefresh();
    }

    private sealed record OutputChoice(OutputMode Mode, string Label);
    private sealed record VirtualDeviceChoice(int Id, string Label);
}
