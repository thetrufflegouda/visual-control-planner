using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using VisualControlsBinder.Games;
using VisualControlsBinder.Hardware;
using VisualControlsBinder.Inputs;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private BindingPlan _plan = new();
    private IReadOnlyList<GameAction> _actions = [];
    private readonly Dictionary<string, IReadOnlyList<GameAction>> _starCatalogs = new(StringComparer.OrdinalIgnoreCase);
    private GameProfiles _profiles = new();
    private int _catalogRequest;
    private JoystickCapture? _capture;
    private DispatcherTimer? _learnTimer;
    private DateTime _learnUntil;
    private BindingActivation _bindingActivation;
    private ControlAddress? _activationAddress;

    private GameTarget? SelectedGame => GamePicker.SelectedItem as GameTarget;
    private string Layer => LayerPicker.SelectedItem as string ?? "Normal";
    private ControlAddress? CurrentAddress => _hardware is not null && _control is not null
        ? new ControlAddress(_hardware.Id, _control.Id, Layer) : null;

    private async void GameChanged(object sender, SelectionChangedEventArgs e)
    {
        _bindingActivation = BindingActivation.Default;
        _activationAddress = null;
        await LoadGame();
    }

    private string GameFolder(GameTarget game) => _plan.GameFolders.GetValueOrDefault(game.Id, game.Folder);

    private bool IsCurrentGameRequest(BindingPlan plan, GameTarget game, int request) =>
        ReferenceEquals(plan, _plan) && game == SelectedGame && request == _catalogRequest;

    private async Task LoadGame()
    {
        var target = SelectedGame;
        if (target is null)
            return;
        int request = ++_catalogRequest;
        var plan = _plan;
        var folder = GameFolder(target);
        var output = OutputFor(target);
        _readingDiagramSources = true;
        StopLearning();
        _actions = [];
        _profiles = new GameProfiles
        {
            WarThunderSource = target.Kind == GameKind.WarThunder ? (folder, null) : null
        };
        _aircraft = null;
        _dcsProfiles.Clear();
        var character = target.IsSwg && output == OutputMode.JoystickGremlin
            ? RoutedGameProfilePath(target) : plan.CharacterProfiles.GetValueOrDefault(target.Id);
        SelectOutput(target);
        AircraftRow.Visibility = target.Kind is GameKind.Dcs or GameKind.Il2 ? Visibility.Visible : Visibility.Collapsed;
        AircraftHeading.Text = target.Kind == GameKind.Dcs ? "MODULE" : "AIRCRAFT";
        AircraftPicker.ToolTip = target.Kind == GameKind.Il2
            ? "Aircraft come from installed Great Battles data. Your IL-2 account controls access to each aircraft."
            : null;
        UpdateProfileButton(target);
        Notify($"Reading {target.Name} controls…");
        RenderActionList();
        UpdateControl();

        try
        {
            if (target.Kind == GameKind.WarThunder && string.IsNullOrWhiteSpace(plan.SourceProfiles.GetValueOrDefault(target.Id)))
            {
                var source = await Task.Run(() => GameProfiles.FindWarThunderProfile(folder));
                if (!IsCurrentGameRequest(plan, target, request))
                    return;
                _profiles = _profiles with { WarThunderSource = (folder, source) };
            }
            if (target.IsStarCitizen)
            {
                if (!_starCatalogs.TryGetValue(folder, out var actions))
                    actions = await Task.Run(() => StarCitizenGame.LoadActions(folder));
                if (!IsCurrentGameRequest(plan, target, request))
                    return;
                _starCatalogs[folder] = actions;
                _actions = actions;
            }
            else if (target.IsSwg)
            {
                var catalog = await Task.Run(() => SwgCatalog.Load(folder, character));
                if (!IsCurrentGameRequest(plan, target, request))
                    return;
                _profiles = _profiles with { SwgCatalog = catalog };
                _actions = catalog.Actions;
            }
            else
            {
                var flightActions = await LoadFlightActions(target, folder, request);
                if (!IsCurrentGameRequest(plan, target, request))
                    return;
                _actions = flightActions;
            }

            if (target.IsStarCitizen || (output == OutputMode.DirectToGame && target.Kind != GameKind.Dcs))
            {
                var source = target.Kind switch
                {
                    GameKind.StarCitizen => StarProfilePath(target),
                    GameKind.Squadrons => SquadronsProfile(target),
                    GameKind.CliffsOfDover => CliffsProfilePath(target),
                    GameKind.Msfs2020 or GameKind.Msfs2024 => plan.SourceProfiles.GetValueOrDefault(MsfsBindingProfileKey(target)),
                    _ => FlightProfilePath(target)
                };
                var profiles = _profiles;
                var actions = _actions;
                bool globalIl2 = _il2Aircraft.IsGlobal;
                var loaded = await Task.Run(() => profiles.ReadNative(target.Kind, folder, source, actions, globalIl2));
                if (!IsCurrentGameRequest(plan, target, request))
                    return;
                _profiles = loaded;
            }
            if (output == OutputMode.JoystickGremlin)
            {
                var gremlinPath = GremlinProfilePath(target);
                if (File.Exists(gremlinPath))
                    _profiles = _profiles with { Gremlin = GremlinProfile.Load(gremlinPath) };
            }
            var aircraftStatus = Il2AircraftStatus(target);
            Notify(target.Kind == GameKind.CustomVJoy && _actions.Count == 0 ? "Use Edit actions to create or import your list."
                : $"{ActiveGameLabel(target)} · {_actions.Count} game actions" +
                    (aircraftStatus is null ? "" : $" · {aircraftStatus}"));
        }
        catch (Exception error)
        {
            if (!IsCurrentGameRequest(plan, target, request))
                return;
            bool canOfferInstallPicker = _actions.Count == 0 && !_plan.GameFolders.ContainsKey(target.Id) &&
                !_plan.SourceProfiles.ContainsKey(ScopeOf(target)) && !_plan.CharacterProfiles.ContainsKey(target.Id);
            Notify(canOfferInstallPicker && MissingGameInstallation(target, folder)
                ? $"Couldn't locate the {target.Name} installation. Use Game files… to choose its folder."
                : error.Message);
        }
        _readingDiagramSources = false;
        UpdateProfileButton(target);
        RenderActionList();
        UpdateControl(revealAssignment: true);
    }

    private static bool MissingGameInstallation(GameTarget game, string folder) => game.Kind switch
    {
        GameKind.StarCitizen => !File.Exists(Path.Combine(folder, "Data.p4k")),
        GameKind.Dcs => !DcsInstall.IsInstallation(folder),
        GameKind.Il2 => !StandaloneFlightSimInstalls.IsIl2(folder),
        GameKind.WarThunder => !StandaloneFlightSimInstalls.IsWarThunder(folder),
        GameKind.Elite => !StandaloneFlightSimInstalls.IsEliteDangerous(folder),
        GameKind.Swg => !StandaloneFlightSimInstalls.IsSwg(folder, game.Id),
        _ => false
    };

    private static string FriendlyProfile(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.StartsWith("layout_", StringComparison.Ordinal) && name.EndsWith("_exported", StringComparison.Ordinal)
            ? name[7..^9] : name;
    }

    private void UpdateProfileButton(GameTarget game)
    {
        bool gremlin = OutputFor(game) == OutputMode.JoystickGremlin;
        System.Windows.Automation.AutomationProperties.SetName(SourceButton,
            gremlin ? "Choose Joystick Gremlin profile" : "Choose source profile");
        bool useRoutedProfile = gremlin && SupportsRoutedGameBindings(game);
        RoutedGameProfilePanel.Visibility = useRoutedProfile ? Visibility.Visible : Visibility.Collapsed;
        var gameProfile = useRoutedProfile ? RoutedGameProfilePath(game) : null;
        RoutedGameProfileButton.Content = gameProfile is not null && File.Exists(gameProfile)
            ? FriendlyProfile(gameProfile) : "Choose game binding profile…";
        RoutedGameProfileButton.ToolTip = gameProfile;
        SourceButton.IsEnabled = true;
        NewProfileButton.IsEnabled = OutputFor(game) == OutputMode.JoystickGremlin ||
            (game.Kind != GameKind.Squadrons && !game.IsMsfs &&
                !(game.Kind == GameKind.Il2 && !_il2Aircraft.IsGlobal));
        var path = CurrentProfilePath(game);
        var added = FindAddedGame(game);
        GameFilesButton.Content = game.Kind switch
        {
            GameKind.CustomVJoy => "Edit actions…", GameKind.Squadrons => "Squadrons settings…",
            GameKind.CliffsOfDover => "Cliffs controls…", GameKind.Discovered => "Binding file…",
            GameKind.Msfs2020 or GameKind.Msfs2024 => "Control files…", _ => "Game files…"
        };
        GameFilesButton.ToolTip = game.Kind switch
        {
            GameKind.CustomVJoy => "Create or import custom actions in this plan.",
            GameKind.Squadrons => SquadronsProfile(game) ?? GameFolder(game),
            GameKind.CliffsOfDover => CliffsControlsTooltip(game),
            GameKind.Msfs2020 or GameKind.Msfs2024 => _plan.SourceProfiles.GetValueOrDefault(game.Id) ?? GameFolder(game),
            _ => added?.BindingFile ?? GameFolder(game)
        };
        if (OutputFor(game) == OutputMode.DirectToGame && Il2AircraftStatus(game) is { } aircraftStatus)
        {
            SourceButton.Content = _plan.SourceProfiles.ContainsKey(ScopeOf(game)) && File.Exists(path)
                ? Path.GetFileName(path) : aircraftStatus;
            SourceButton.ToolTip = string.Join(Environment.NewLine, new[] { aircraftStatus,
                File.Exists(path) ? path : ProfileDialogDirectory(game), Il2NativeRestriction(game) }
                .Where(text => !string.IsNullOrWhiteSpace(text)));
            return;
        }
        if (game.Kind == GameKind.Il2 && OutputFor(game) == OutputMode.DirectToGame && path.Length > 0)
        {
            var map = Il2Profile.CompanionPath(path);
            SourceButton.Content = File.Exists(path) && File.Exists(map)
                ? $"{Path.GetFileName(path)} + {Path.GetFileName(map)}" : "Choose profile…";
            SourceButton.ToolTip = $"{path}{Environment.NewLine}{map}";
            return;
        }
        SourceButton.Content = File.Exists(path) ? FriendlyProfile(path) : gremlin ? "Choose Gremlin profile…" : "Choose profile…";
        var sources = new[] { File.Exists(path) ? path : ProfileDialogDirectory(game) }
            .Concat(!gremlin && game.IsSwg ? _profiles.SwgCatalog?.SourceFiles ?? [] : []);
        if (game.Kind == GameKind.Discovered && added?.BindingFile is { } catalogPath)
            sources = sources.Append($"Game actions · {catalogPath}");
        SourceButton.ToolTip = string.Join(Environment.NewLine, sources.Where(source => source.Length > 0).Distinct());
    }

    private async void ChooseSource(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not { } game)
            return;
        bool gremlin = OutputFor(game) == OutputMode.JoystickGremlin;
        if (!gremlin && game.Kind == GameKind.Dcs)
        {
            ChooseDcsDeviceProfile(sender, e);
            return;
        }
        if (!gremlin && game.IsSwg)
        {
            await ChooseCharacterMap(game);
            return;
        }
        if (!gremlin && game.Kind == GameKind.Squadrons)
        {
            await ChooseSquadronsProfile(game);
            return;
        }
        if (!gremlin && game.IsMsfs)
        {
            await ChooseMsfsBindingProfile(game);
            return;
        }
        var dialog = new OpenFileDialog
        {
            Title = gremlin ? "Open Joystick Gremlin profile" : $"Open {game.Name} binding profile",
            Filter = gremlin || game.IsStarCitizen ? "Profile XML (*.xml)|*.xml" : FlightProfileFilter(game.Kind),
            InitialDirectory = ProfileDialogDirectory(game)
        };
        if (dialog.ShowDialog(this) == true)
        {
            if (gremlin)
                _plan.GremlinProfiles[game.Id] = dialog.FileName;
            else
                _plan.SourceProfiles[ScopeOf(game)] = dialog.FileName;
            await LoadGame();
        }
    }

    private async void ChooseGameFolder(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not { } game)
            return;
        if (game.Kind == GameKind.CustomVJoy)
        {
            await EditCustomGameActions(game);
            return;
        }
        if (game.Kind == GameKind.Squadrons)
        {
            await ChooseSquadronsProfile(game);
            return;
        }
        if (game.Kind == GameKind.CliffsOfDover)
        {
            await ChooseCliffsControls(game);
            return;
        }
        if (game.IsMsfs)
        {
            await ChooseMsfsSource(game);
            return;
        }
        if (game.Kind == GameKind.Discovered)
        {
            await ChangeDiscoveredFile(game);
            return;
        }
        if (game.IsSwg)
        {
            var choice = ReviewWindow.Pick(this, "SWG game files", new[] { "Install folder", "Character keymap" });
            if (choice is null)
                return;
            if (choice == "Character keymap")
            {
                await ChooseCharacterMap(game);
                return;
            }
        }
        string currentFolder = GameFolder(game);
        var dialog = new OpenFolderDialog
        {
            Title = $"Choose {game.Name} install folder",
            InitialDirectory = Directory.Exists(currentFolder) ? currentFolder : ""
        };
        if (dialog.ShowDialog(this) != true)
            return;
        string folder = dialog.FolderName;
        if (game.IsStarCitizen)
        {
            var install = StarCitizenInstall.ResolveLiveDirectory(folder);
            if (install is null)
            {
                Notify("Choose the Star Citizen folder containing Data.p4k or its library folder.");
                return;
            }
            folder = install;
        }
        _plan.GameFolders[game.Id] = folder;
        _plan.CharacterProfiles.Remove(game.Id);
        _starCatalogs.Remove(folder);
        await LoadGame();
    }

    private async Task ChooseCharacterMap(GameTarget game)
    {
        if (OutputFor(game) == OutputMode.JoystickGremlin)
        {
            await PickRoutedGameProfile(game);
            return;
        }

        var keymap = new OpenFileDialog
        {
            Title = "Choose a saved SWG character keymap",
            Filter = "SWG keymap (*.inp)|*.inp|All files|*.*",
            InitialDirectory = SwgProfileDirectory(game)
        };
        if (keymap.ShowDialog(this) != true)
            return;
        _plan.CharacterProfiles[game.Id] = keymap.FileName;
        await LoadGame();
    }

    private void LayerChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingControls)
            return;
        StopLearning();
        UpdateControl(revealAssignment: true);
    }

    private void UpdateControl(bool revealAssignment = false)
    {
        RefreshDcsProfile();
        ReadSavedGremlinShift();
        RefreshGremlinLayer();
        var address = CurrentAddress;
        ControlTitle.Text = _control?.Name.ToUpperInvariant() ?? "CHOOSE A CONTROL";
        LearnButton.IsEnabled = address is not null && _hardware is not null && ConnectedDevice(_hardware) is not null;
        ManualInputButton.IsEnabled = address is not null;
        var signal = address is null ? null : _plan.GetSignal(PhysicalAddress(address, _hardware));
        var hint = address is null ? null : ReferenceSignal(address, _hardware);
        var routes = address is not null && SelectedGame is { } game && OutputFor(game) == OutputMode.JoystickGremlin
            ? ControlRoutes(game, address) : [];
        InputSummary.Text = address is null ? "Click a control on the hardware."
            : signal is not null ? signal.ToString()
            : hint is not null ? $"Reference: {hint} · Learn to verify"
            : "Input needs learning";
        UpdateVirtualRouting(routes);
        RefreshGremlinModifier(address, signal ?? hint);
        RefreshBindingActivations(address, signal ?? hint);
        RefreshAssignments(address, signal ?? hint, routes);
        if (revealAssignment)
            RevealAssignedAction();
        RenderActionList();
        RefreshGuidedLearnControls();
        RefreshControlPad();
        QueueDiagramRefresh();
    }

    private InputSignal? ReferenceSignal(ControlAddress address, HardwareDefinition? hardware)
    {
        hardware ??= FindPlannedLayout(address.Device);
        if (hardware is null)
            return null;
        address = PhysicalAddress(address, hardware);
        if (address.Layer is not ("Normal" or "Mode 1"))
            return null;
        if (!CanUseReferenceInputs(hardware))
            return null;
        return InputSignal.Parse(hardware.Controls.FirstOrDefault(control => control.Id == address.Control)?.SuggestedInput);
    }

    private bool CanUseReferenceInputs(HardwareDefinition device) => device.MatchesReferenceProduct(ControllerProductIds(device));

    private void RefreshAssignments(ControlAddress? address, InputSignal? signal, IReadOnlyList<GremlinRouteView> routes)
    {
        bool selectedNewControl = _activationAddress != address;
        _activationAddress = address;
        var rows = new List<AssignmentRow>();
        AssignmentEmpty.Text = "No bindings assigned.";
        if (address is not null && !IsGremlinModifier(address) && SelectedGame is { } game)
        {
            bool directOutput = OutputFor(game) == OutputMode.DirectToGame;
            bool routed = !directOutput && SupportsRoutedGameBindings(game);
            var edits = _plan.Edits.Where(edit => edit.Game == ScopeOf(game) && edit.Address == address &&
                EditForOutput(game, edit)).ToArray();
            if (directOutput && game.Kind == GameKind.Dcs && signal is not null && _hardware is { } dcsHardware &&
                _aircraft is { } module && _selectedDcsProfile is { } dcsProfile)
            {
                foreach (var action in DcsSavedRows(module, dcsHardware, dcsProfile).Where(row => row.Token == signal.Token)
                    .Select(row => row.Action).Distinct())
                {
                    if (edits.Any(edit => edit.RemoveInput == signal.Token && SameAction(edit.Action, action)))
                        continue;
                    rows.Add(new AssignmentRow(action, Original: new GameBinding(action.Map, action.Id, signal.Token)));
                }
            }
            else if (directOutput && signal is not null && _hardware is not null)
            {
                foreach (var binding in NativeAssignments(_hardware, signal))
                {
                    if (edits.Any(edit => edit.RemoveInput == binding.Input && edit.Action.Map == binding.Map &&
                        edit.Action.Id == binding.Action && EditActivation(game, edit) == binding.Activation &&
                        edit.NativeActivation == binding.NativeActivation))
                        continue;
                    var action = _actions.FirstOrDefault(item => item.Map == binding.Map && item.Id == binding.Action)
                        ?? new GameAction(binding.Map, binding.Action, "", "", binding.Action, signal.Kind == "axis");
                    rows.Add(new AssignmentRow(action, Original: binding));
                }
            }
            else if (routed)
            {
                var outputs = routes.Select(row => row.Target.Output).OfType<GremlinOutput>().Distinct().ToArray();
                if (outputs.Length == 0)
                    AssignmentEmpty.Text = "Choose a vJoy output or assign an action to route this control.";
                var path = RoutedGameProfilePath(game);
                if (outputs.Length > 0 && !File.Exists(path))
                    AssignmentEmpty.Text = "Choose a game binding profile to see actions for this route.";

                var saved = ReadRoutedGameBindings(game, path, outputs);
                foreach (var output in outputs)
                {
                    var (bindings, status) = saved[output];
                    bool inherited = address.Layer == GremlinShiftLayer &&
                        routes.Where(row => row.Target.Output == output).All(row => row.Inherited);
                    if (status is not null)
                        AssignmentEmpty.Text = status;
                    foreach (var binding in bindings)
                    {
                        if (edits.Any(edit => SameRoutedProfile(game, edit.RoutedProfile, path) &&
                            edit.RoutedOutput == output && edit.RemoveInput == binding.Input &&
                            edit.Action.Map == binding.Map && edit.Action.Id == binding.Action &&
                            EditActivation(game, edit) == binding.Activation && edit.NativeActivation == binding.NativeActivation))
                            continue;
                        var action = _actions.FirstOrDefault(item => item.Map == binding.Map && item.Id == binding.Action)
                            ?? new GameAction(binding.Map, binding.Action, "", "", binding.Action, output.Kind == "axis");
                        rows.Add(new AssignmentRow(action, Original: binding, RoutedOutput: output, Inherited: inherited));
                    }
                }
            }
            if (!directOutput && _profiles.SwgCatalog is { } swg)
            {
                var current = routes.Where(row => !routed || row.Target.Keyboard is not null).Select(row => row.Target)
                    .SelectMany(target => swg.GetActionsForTarget(target, target.Output?.DeviceId ?? 1))
                    .DistinctBy(action => (action.Map, action.Id));
                rows.AddRange(current.Select(action => new AssignmentRow(action)));
            }
            rows.AddRange(edits.Where(edit => edit.RemoveInput is null)
                .Select(edit => new AssignmentRow(edit.Action, Edit: edit, RoutedOutput: edit.RoutedOutput)));
        }
        if (ActivationButtons.Visibility == Visibility.Visible && SelectedGame is { } target)
        {
            var byActivation = rows.ToLookup(row => row.Edit is { } edit ? EditActivation(target, edit)
                : row.Original?.Activation ?? BindingActivation.Default);
            if (selectedNewControl && !byActivation[SelectedActivation(target)].Any() && byActivation.FirstOrDefault() is { } assigned)
            {
                _bindingActivation = assigned.Key;
                RefreshBindingActivations(address, signal);
            }
            foreach (Button button in ActivationButtons.Children)
            {
                var activation = (BindingActivation)button.Tag;
                int count = byActivation[activation].Count();
                button.Content = activation.Label().ToUpperInvariant() + (count > 0 ? " •" : "");
                button.ToolTip = $"{activation.Label()} · {count} binding{(count == 1 ? "" : "s")}";
            }
            AssignmentPicker.ItemsSource = byActivation[SelectedActivation(target)].ToArray();
        }
        else
            AssignmentPicker.ItemsSource = rows;
        RemoveButton.IsEnabled = false;
    }

    private static BindingActivation EditActivation(GameTarget game, BindingEdit edit) =>
        game.Kind == GameKind.WarThunder && !edit.Action.IsAxis && edit.Activation == BindingActivation.Default
            ? BindingActivation.Tap : edit.Activation;

    private bool EditForOutput(GameTarget game, BindingEdit edit)
    {
        if (OutputFor(game) == OutputMode.DirectToGame)
            return !edit.ViaVJoy;
        bool routed = SupportsRoutedGameBindings(game) && CanWriteRoutedAction(game, edit.Action);
        return routed ? edit.ViaVJoy : !edit.ViaVJoy && edit.Activation == BindingActivation.Default;
    }

    private BindingActivation SelectedActivation(GameTarget game) => ActivationButtons.Visibility != Visibility.Visible
        ? BindingActivation.Default : game.Kind == GameKind.WarThunder && _bindingActivation == BindingActivation.Default
            ? BindingActivation.Tap : _bindingActivation;

    private void RefreshBindingActivations(ControlAddress? address, InputSignal? signal)
    {
        if (SelectedGame is not { } game)
            return;
        bool direct = OutputFor(game) == OutputMode.DirectToGame;
        bool visible = address is not null && _control?.Kind.Equals("axis", StringComparison.OrdinalIgnoreCase) != true &&
            (direct || SupportsRoutedGameBindings(game) && RoutedGameProfilePath(game) is not null && signal?.Kind is null or "button") &&
            (game.IsStarCitizen || (game.Kind == GameKind.WarThunder && signal?.Kind is null or "button"));
        ActivationButtons.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible)
            return;
        DefaultActivationButton.Visibility = game.IsStarCitizen ? Visibility.Visible : Visibility.Collapsed;
        foreach (Button button in ActivationButtons.Children)
        {
            bool selected = (BindingActivation)button.Tag == SelectedActivation(game);
            button.SetResourceReference(BackgroundProperty, selected ? "SelectionBackground" : "ControlBackground");
            button.SetResourceReference(BorderBrushProperty, selected ? "Accent" : "PanelBorder");
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "Selected" : "");
        }
    }

    private void SelectBindingActivation(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: BindingActivation activation })
        {
            _bindingActivation = activation;
            UpdateControl(revealAssignment: true);
        }
    }

    private IEnumerable<GameBinding> NativeAssignments(HardwareDefinition device, InputSignal signal)
    {
        if (_profiles.Msfs.Length > 0 && SelectedGame is { IsMsfs: true } msfs && ConnectedDevice(device) is { } msfsDevice &&
            UniqueMsfsProfile(msfs, msfsDevice) is { } msfsProfile)
            return msfsProfile.Bindings.Where(binding => binding.Input == signal.Token);
        if (_profiles.Cliffs is { } cliffs && ConnectedDevice(device) is { } cliffsDevice &&
            GetCliffsSlot(cliffs, cliffsDevice) is { } cliffsSlot &&
            CliffsBindingProfile.EncodeInput(cliffsSlot, signal) is { } cliffsInput)
            return cliffs.Bindings.Where(binding => binding.Input.Equals(cliffsInput, StringComparison.OrdinalIgnoreCase))
                .Select(binding => binding with { Input = signal.Token });
        if (_profiles.Squadrons is { } squadrons && ConnectedDevice(device) is { } controller &&
            squadrons.FindDeviceSlot(controller.Name) is { } squadronSlot)
            return _profiles.SquadronsBindings.Where(binding => binding.DeviceSlot == squadronSlot && binding.Token == signal.Token)
                .Select(binding => new GameBinding(binding.Map, binding.Action, binding.Token));
        if (_profiles.StarCitizen is { } profile && FindSlot(profile, device) is { } slot)
            return profile.Bindings.Where(binding => binding.Input == StarInput(slot, signal));
        if (_profiles.Il2Slots is { } slots && FindIl2Slot(slots, device) is { } il2Slot &&
            Il2JoystickSlots.Encode(il2Slot, signal) is { } il2Input)
            return _profiles.Il2Bindings.Where(binding => binding.Input == il2Input)
                .Select(binding => binding with { Input = signal.Token });
        if (_profiles.SwgNative is { } native && SelectedGame is { } game &&
            _plan.NativeJoysticks.GetValueOrDefault(game.Id) == device.Id)
            return native.Bindings.Where(binding => binding.Kind == signal.Kind && binding.Index == signal.Index)
                .Select(binding => new GameBinding(binding.Map, binding.Action, signal.Token));
        if (_profiles.Elite is { } elite && ProductGuid(device) is { } id)
        {
            string key = EliteDangerousGame.JoystickKey(signal.Kind, signal.Index, signal.Direction);
            string model = EliteDeviceId(id);
            return elite.Bindings.Where(binding => !binding.HasModifiers && binding.Key == key &&
                binding.Device.Equals(model, StringComparison.OrdinalIgnoreCase))
                .Select(binding => new GameBinding("Controls", binding.Action, signal.Token));
        }
        if (_profiles.WarThunder is { } thunder && signal.Kind is "axis" or "button")
        {
            var (vendor, product) = ControllerProductIds(device);
            if (thunder.FindDevice(vendor, product) is not { } mapped)
                return [];
            string? key = signal.Kind == "axis" && mapped.AxesCount == 8 && mapped.AxesOffset >= 0
                ? $"axisId:{mapped.NativeDirectInputAxis(signal.Index)}"
                : signal.Kind == "button" && signal.Index <= mapped.ButtonsCount && mapped.ButtonsOffset >= 0
                    ? $"joyButton:{mapped.NativeButton(signal.Index)}" : null;
            return thunder.Bindings.Where(binding => binding.Input.Equals(key, StringComparison.Ordinal))
                .Select(binding => binding with { Input = signal.Token });
        }
        return [];
    }

    private void ActionSearchChanged(object sender, TextChangedEventArgs e) => RenderActionList();

    private void RenderActionList()
    {
        if (ActionPicker is null || ActionSearch is null)
            return;
        bool reveal = _selectAssignedAction;
        var selected = reveal ? _assignedAction : (ActionPicker.SelectedItem as ActionRow)?.Action;
        _selectAssignedAction = false;
        var search = ActionSearch.Text.Trim();
        IEnumerable<GameAction> matching = _actions;
        if (_control is not null)
        {
            bool axis = _control.Kind.Equals("axis", StringComparison.OrdinalIgnoreCase);
            matching = matching.Where(action => action.IsAxis == axis);
        }
        if (search.Length > 0)
            matching = matching.Where(action => MatchesActionSearch(action, search));
        var rows = matching.Select(action => new ActionRow(action)).ToArray();
        var view = new ListCollectionView(rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ActionRow.Group)));
        ActionPicker.ItemsSource = view;
        var selectedRow = selected is null ? null : rows.FirstOrDefault(row => SameAction(row.Action, selected));
        ActionPicker.SelectedItem = selectedRow;
        if (reveal && selectedRow is not null)
            Dispatcher.InvokeAsync(() => ScrollAssignedActionIntoView(selectedRow), DispatcherPriority.Loaded);
        UpdateAssignButton();
    }

    private void ActionSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAssignButton();
    private void UpdateAssignButton()
    {
        if (AssignButton is not null)
            AssignButton.IsEnabled = CurrentAddress is { } address && !IsGremlinModifier(address) && ActionPicker.SelectedItem is ActionRow;
    }

    private void AssignmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var row = AssignmentPicker.SelectedItem as AssignmentRow;
        RemoveButton.IsEnabled = row?.Edit is not null || row is { Original: not null, Inherited: false };
        RemoveButton.ToolTip = row?.Inherited == true ? "Edit this binding in the base layer, or route this Shift control to its own vJoy input." : null;
        if (row is not null)
        {
            RevealAssignedAction(row.Action);
            RenderActionList();
        }
    }
    private void ActionDoubleClicked(object sender, MouseButtonEventArgs e) => StageAction();
    private void AddAssignment(object sender, RoutedEventArgs e) => StageAction();

    private void StageAction()
    {
        if (SelectedGame is not { } game || CurrentAddress is not { } address || IsGremlinModifier(address) ||
            ActionPicker.SelectedItem is not ActionRow { Action: var action })
            return;
        if ((AssignmentPicker.ItemsSource as IEnumerable<AssignmentRow>)?.Any(row =>
            SameAction(row.Action, action) && (row.Edit is { RemoveInput: null } || row.Original is not null)) == true)
        {
            Notify($"{action.Name} is already assigned here.");
            return;
        }
        if (game.IsSwg && OutputFor(game) == OutputMode.DirectToGame &&
            !_plan.NativeJoysticks.ContainsKey(game.Id))
            _plan.NativeJoysticks[game.Id] = address.Device;
        bool direct = OutputFor(game) == OutputMode.DirectToGame;
        bool routed = !direct && SupportsRoutedGameBindings(game) && CanWriteRoutedAction(game, action);
        var routedProfile = routed ? RoutedGameProfilePath(game) : null;
        var activation = SelectedActivation(game);
        GremlinOutput? routedOutput = null;
        if (routed)
        {
            if (_plan.GetVirtualRoute(ScopeOf(game), address) is { Output: null } choosing &&
                ControlRoutes(game, address).Any(row => row.Target.Output is { } output && output.DeviceId != choosing.DeviceId))
            {
                Notify("Choose an input on the selected vJoy device before assigning this action.");
                return;
            }
            try { routedOutput = PrepareRoutedAssignment(game, address, action); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                Notify(error.Message);
                return;
            }
        }
        int undone = direct || routed ? _plan.Edits.RemoveAll(edit =>
            edit.Game == ScopeOf(game) && edit.Address == address && SameAction(edit.Action, action) &&
            edit.ViaVJoy == routed && edit.RoutedOutput == routedOutput &&
            (!routed || SameRoutedProfile(game, edit.RoutedProfile, routedProfile)) &&
            EditActivation(game, edit) == activation && edit.RemoveInput is not null) : 0;
        if (undone == 0)
            _plan.Edits.Add(new BindingEdit(ScopeOf(game), address, action)
                { Activation = activation, ViaVJoy = routed, RoutedOutput = routedOutput, RoutedProfile = routedProfile });
        UpdateControl();
        Notify("");
    }

    private void RemoveAssignment(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is not { } game || CurrentAddress is not { } address ||
            AssignmentPicker.SelectedItem is not AssignmentRow selected)
            return;
        if (selected.Edit is { } edit)
            _plan.Edits.Remove(edit);
        else if (!selected.Inherited && selected.Original is { } binding &&
            (OutputFor(game) == OutputMode.DirectToGame || selected.RoutedOutput is not null))
            _plan.Edits.Add(new BindingEdit(ScopeOf(game), address, selected.Action, binding.Input)
            {
                Activation = binding.Activation,
                NativeActivation = binding.NativeActivation,
                ViaVJoy = selected.RoutedOutput is not null,
                RoutedOutput = selected.RoutedOutput,
                RoutedProfile = selected.RoutedOutput is not null ? RoutedGameProfilePath(game) : null
            });
        else
            return;
        UpdateControl();
        Notify("");
    }

    private void ChooseInput(object sender, RoutedEventArgs e)
    {
        if (CurrentAddress is not { } address || _control is null)
            return;
        StopLearning();
        var options = _control.Kind.Equals("axis", StringComparison.OrdinalIgnoreCase)
            ? Enumerable.Range(1, 8).Select(InputSignal.Axis).ToArray()
            : Enumerable.Range(1, 128).Select(InputSignal.Button)
                .Concat(Enumerable.Range(1, 4).SelectMany(index =>
                    new[] { "up", "right", "down", "left" }.Select(direction => InputSignal.Hat(index, direction)))).ToArray();
        var signal = ReviewWindow.Pick(this, "Set input manually", options);
        if (signal is not null)
        {
            _plan.Remember(PhysicalAddress(address, _hardware), signal);
            UpdateControl(revealAssignment: true);
            Notify($"{_control.Name} · {signal}");
        }
    }

    private void LearnInput(object sender, RoutedEventArgs e)
    {
        if (_capture is not null)
        {
            StopLearning();
            UpdateControl();
            return;
        }
        if (CurrentAddress is not { } address || _control is null || _hardware is null ||
            ConnectedDevice(_hardware) is not { } device || _input is null)
            return;
        try
        {
            bool axes = _control.Kind.Equals("axis", StringComparison.OrdinalIgnoreCase);
            _capture = _input.Open(device, new WindowInteropHelper(this).Handle, axes);
            _learnUntil = DateTime.UtcNow.AddSeconds(12);
            LearnButton.Content = "Cancel";
            InputSummary.Text = axes ? "Move only the selected axis."
                : $"{address.Layer} · press the selected control.";
            if (_control.Id.Contains("stage-2", StringComparison.Ordinal))
                InputSummary.Text = "Hold the first detent, click Learn, then press through.";
            _learnTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            _learnTimer.Tick += ReadLearnedInput;
            _learnTimer.Start();
        }
        catch (Exception error)
        {
            StopLearning();
            Notify(error.Message);
        }
    }

    private void ReadLearnedInput(object? sender, EventArgs e)
    {
        if (_capture is null)
            return;
        if (DateTime.UtcNow >= _learnUntil)
        {
            StopLearning();
            UpdateControl();
            Notify("No matching controller input received. Select the layer first and retry, or set the input manually.");
            return;
        }
        if (!_capture.TryReadChanges(out var changes) || changes.Count == 0)
            return;
        var address = CurrentAddress;
        StopLearning();
        var signal = changes.Count == 1 ? changes[0] : ReviewWindow.Pick(this, "Multiple inputs detected", changes);
        if (address is not null && signal is not null)
        {
            _plan.Remember(PhysicalAddress(address, _hardware), signal);
            Notify($"Learned {signal} · {address.Layer}");
        }
        UpdateControl(revealAssignment: signal is not null);
    }

    private void StopLearning()
    {
        StopGuidedLearn();
        _learnTimer?.Stop();
        _learnTimer = null;
        _capture?.Dispose();
        _capture = null;
        if (LearnButton is not null)
            LearnButton.Content = "Learn this control";
    }

    private static string StarInput(int slot, InputSignal signal) => $"js{slot}_{signal.Token}";
    private int? FindSlot(StarCitizenProfile profile, HardwareDefinition device)
    {
        return ProductGuid(device) is { } product
            ? Single(profile.Devices.Where(item => item.ProductGuid == product))?.Instance : null;
    }

    private Guid? ProductGuid(HardwareDefinition device) => ConnectedDevice(device)?.Product
        ?? _plan.Devices.FirstOrDefault(slot => slot.Id == device.Id)?.ProductId
        ?? FindCustom(device)?.ProductId
        ?? (device.VendorId > 0 ? Guid.Parse($"{device.ProductId:X4}{device.VendorId:X4}-0000-0000-0000-504944564944") : null);

    private (int Vendor, int Product) ControllerProductIds(HardwareDefinition device)
    {
        if (ProductGuid(device) is not { } id)
            return (device.VendorId, device.ProductId);
        uint value = BitConverter.ToUInt32(id.ToByteArray());
        return ((int)(value & 0xffff), (int)(value >> 16));
    }

    private sealed record ActionRow(GameAction Action)
    {
        public string Name => Action.Name;
        public string Group => string.Join(" / ", new[] { Action.Category, Action.Section }
            .Where(part => !string.IsNullOrWhiteSpace(part)).Distinct()).ToUpperInvariant();
    }

    private static string AssignmentLabel(GameAction action)
    {
        var title = AssignmentTitle(action);
        return title.Length == 0 ? action.Name : $"{title} · {action.Name}";
    }

    private static string AssignmentTitle(GameAction action)
    {
        var group = new ActionRow(action).Group;
        return group.Length > 0 ? group : action.Map.ToUpperInvariant();
    }

    private sealed record AssignmentRow(GameAction Action, BindingEdit? Edit = null, GameBinding? Original = null,
        GremlinOutput? RoutedOutput = null, bool Inherited = false)
    {
        public string Title { get; } = AssignmentTitle(Action);
        public string Entry => Action.Name;
        public string Label => string.Join(" · ", new[] { Title, Entry, RoutedOutput?.ToString() }
            .Where(value => !string.IsNullOrEmpty(value)));
    }
}
