using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Win32;
using VisualControlsBinder.Games;
using VisualControlsBinder.Planner;

namespace VisualControlsBinder;

public partial class MainWindow
{
    private readonly Dictionary<string, string?> _routedStarDefaults = new(StringComparer.OrdinalIgnoreCase);
    private (string Path, DateTime Stamp, long Length, StarCitizenProfile Profile)? _routedStarRead;
    private (string Path, DateTime Stamp, long Length, WarThunderProfile Profile)? _routedThunderRead;
    private (SwgCatalog Catalog, SwgNativeProfile Profile)? _routedSwgRead;

    private static bool SupportsRoutedGameBindings(GameTarget game) => game.IsStarCitizen || game.IsSwg || game.Kind == GameKind.WarThunder;

    private static bool CanWriteRoutedAction(GameTarget game, GameAction action) =>
        game.IsStarCitizen || game.Kind == GameKind.WarThunder || game.IsSwg && action.Map == "Space";

    private void SelectNewGremlinProfile(GameTarget game, string path)
    {
        var gameProfile = SupportsRoutedGameBindings(game) ? RoutedGameProfilePath(game) : null;
        _plan.GremlinProfiles[game.Id] = path;
        if (gameProfile is not null)
            _plan.SetRoutedProfile(ScopeOf(game), path, gameProfile);
    }

    private string? RoutedGameProfilePath(GameTarget game, bool refreshWarThunder = false)
    {
        var scope = ScopeOf(game);
        var gremlinPath = GremlinProfilePath(game);
        if ((_plan.GetRoutedProfile(scope, gremlinPath) ?? _plan.GetRoutedProfile(scope, "")) is { } selected)
            return RoutedProfileFile(game, selected);
        if (game.IsSwg)
            return _plan.CharacterProfiles.TryGetValue(game.Id, out var character) ? RoutedProfileFile(game, character)
                : _profiles.SwgCatalog?.SourceFiles.FirstOrDefault(SwgCatalog.IsSpaceProfile);
        if (game.Kind == GameKind.WarThunder)
            return FlightProfilePath(game, refreshWarThunder);
        if (!game.IsStarCitizen)
            return null;
        var install = GameFolder(game);
        if (!_routedStarDefaults.TryGetValue(install, out var path))
            _routedStarDefaults[install] = path = StarCitizenGame.FindVJoyOnlyProfile(install);
        return path;
    }

    private static string RoutedProfileFile(GameTarget game, string path)
    {
        if (game.IsSwg && !SwgCatalog.IsSpaceProfile(path))
        {
            var space = Path.ChangeExtension(path, null) + "_space.inp";
            if (File.Exists(space))
                return space;
        }
        return path;
    }

    private static bool SameRoutedProfile(GameTarget game, string? left, string? right) =>
        string.Equals(left is null ? null : RoutedProfileFile(game, left), right, StringComparison.OrdinalIgnoreCase);

    private async void ChooseRoutedGameProfile(object sender, RoutedEventArgs e)
    {
        if (SelectedGame is { } game && OutputFor(game) == OutputMode.JoystickGremlin && SupportsRoutedGameBindings(game))
            await PickRoutedGameProfile(game);
    }

    private async Task PickRoutedGameProfile(GameTarget game)
    {
        var current = RoutedGameProfilePath(game);
        var picker = new OpenFileDialog
        {
            Title = $"Choose the {game.Name} game binding profile fed by Gremlin",
            Filter = game.IsStarCitizen ? "Star Citizen profile (*.xml)|*.xml"
                : game.IsSwg ? "SWG character keymap (*.inp)|*.inp" : FlightProfileFilter(game.Kind),
            InitialDirectory = current is not null ? Path.GetDirectoryName(current) ?? "" :
                game.IsStarCitizen ? StarCitizenGame.MappingsDirectory(GameFolder(game)) : game.IsSwg
                    ? SwgProfileDirectory(game) : NativeProfileDirectory(game),
            FileName = current is null ? "" : Path.GetFileName(current)
        };
        if (picker.ShowDialog(this) != true)
            return;
        try
        {
            var selected = picker.FileName;
            if (game.IsStarCitizen)
            {
                var profile = StarCitizenGame.LoadProfile(selected);
                if (!profile.Devices.Any(device => device.ProductGuid == StarCitizenVJoyBindings.ProductId))
                    throw new InvalidDataException("Choose a Star Citizen profile that already contains a vJoy joystick.");
            }
            else if (game.IsSwg)
            {
                var catalog = SwgCatalog.Load(GameFolder(game), selected);
                selected = catalog.SourceFiles.FirstOrDefault(SwgCatalog.IsSpaceProfile)
                    ?? throw new InvalidDataException("Choose a character with a saved SWG Space keymap.");
            }
            else
                _ = WarThunderProfile.Load(selected);
            _plan.SetRoutedProfile(ScopeOf(game), GremlinProfilePath(game), selected);
            await LoadGame();
        }
        catch (Exception error) { Notify(error.Message); }
    }

    private int[] RoutedDeviceIds(GameTarget game) => ReadGremlinRoutesForView(game, PlannedLayouts())
        .Select(row => row.Target.Output?.DeviceId).OfType<int>().Distinct().Order().ToArray();

    private void InvalidateRoutedGameBindings()
    {
        _routedStarRead = null;
        _routedThunderRead = null;
        _routedSwgRead = null;
    }

    private IReadOnlyDictionary<GremlinOutput, RoutedBindingRead> ReadRoutedGameBindings(GameTarget game, string? path,
        IReadOnlyCollection<GremlinOutput> outputs, IReadOnlyCollection<int>? deviceIds = null)
    {
        if (outputs.Count == 0)
            return new Dictionary<GremlinOutput, RoutedBindingRead>();
        try
        {
            if (path is null || !File.Exists(path))
                return RoutedBindingRead.Unavailable(outputs, $"Choose a {game.Name} game binding profile.");
            if (game.IsStarCitizen)
            {
                var file = new FileInfo(path);
                var stamp = file.LastWriteTimeUtc;
                var length = file.Length;
                if (_routedStarRead is not { } cache || !cache.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                    cache.Stamp != stamp || cache.Length != length)
                    _routedStarRead = (path, stamp, length, StarCitizenGame.LoadProfile(path));
                return new StarCitizenVJoyBindings(_routedStarRead.Value.Profile, DeviceIds()).Read(outputs);
            }
            if (game.Kind == GameKind.WarThunder)
            {
                var file = new FileInfo(path);
                var stamp = file.LastWriteTimeUtc;
                var length = file.Length;
                if (_routedThunderRead is not { } cache || !cache.Path.Equals(path, StringComparison.OrdinalIgnoreCase) ||
                    cache.Stamp != stamp || cache.Length != length)
                    _routedThunderRead = (path, stamp, length, WarThunderProfile.Load(path));
                return new WarThunderVJoyBindings(_routedThunderRead.Value.Profile, VJoyOutputs.Read() ?? []).Read(outputs);
            }
            if (_profiles.SwgCatalog is not { } catalog || !catalog.SourceFiles.Any(SwgCatalog.IsSpaceProfile))
                return RoutedBindingRead.Unavailable(outputs, "Choose an SWG character with a saved Space keymap.");
            if (_routedSwgRead is not { } swg || !ReferenceEquals(swg.Catalog, catalog))
                _routedSwgRead = (catalog, SwgNativeProfile.Load(catalog.SourceFiles));
            return new SwgVJoyBindings(_routedSwgRead.Value.Profile, DeviceIds()).Read(outputs);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
                                      InvalidOperationException or ArgumentException or System.Xml.XmlException)
        {
            return RoutedBindingRead.Unavailable(outputs, error.Message);
        }

        IReadOnlyCollection<int> DeviceIds() => deviceIds ?? RoutedDeviceIds(game)
            .Concat(outputs.Select(output => output.DeviceId)).Distinct().ToArray();
    }

    private GremlinOutput PrepareRoutedAssignment(GameTarget game, ControlAddress address, GameAction action)
    {
        var routes = ControlRoutes(game, address);
        var existing = routes.Select(row => row.Target).Distinct().ToArray();
        string kind = action.IsAxis ? "axis" : "button";
        var (signal, _) = RequireInput(GetLayout(address.Device), address);
        if (signal.Kind != kind)
            throw new InvalidOperationException("Choose a physical button or axis of the same kind as this action.");
        bool needsSlider = game.IsSwg && action.Id == "CMD_shipThrottleSetSlider";
        if (existing is [{ Output: { } saved }] && saved.Kind == kind)
        {
            if (address.Layer == GremlinShiftLayer && routes.All(row => row.Inherited))
                throw new InvalidOperationException("This route inherits the base vJoy output. Edit the game binding in the base layer or choose a separate Shift output.");
            if (needsSlider && saved.InputId is not (7 or 8))
                throw new InvalidOperationException("The SWG Throttle Slider action uses vJoy Slider 1 or Slider 2. Choose one for this route.");
            var profilePath = RoutedGameProfilePath(game);
            if (File.Exists(profilePath) && ReadRoutedGameBindings(game, profilePath, [saved])[saved].Status is { } issue)
                throw new InvalidOperationException(issue);
            return saved;
        }
        if (existing.Length > 0)
            throw new InvalidOperationException("Choose a single vJoy output for this control before assigning a game action.");
        var used = ReadGremlinRoutesForView(game, PlannedLayouts()).Select(row => row.Target.Output).OfType<GremlinOutput>().ToHashSet();
        var devices = used.Select(output => output.DeviceId).Distinct().ToArray();
        if (devices.Length > 1)
            throw new InvalidOperationException($"Choose a vJoy device and {kind} for this control.");
        var selected = _plan.GetVirtualRoute(ScopeOf(game), address)?.DeviceId;
        var available = _profiles.Gremlin?.Outputs ?? VJoyOutputs.Read() ?? [];
        int device = selected ?? devices.FirstOrDefault();
        if (device == 0)
            device = available.Select(output => output.DeviceId).FirstOrDefault();
        var path = RoutedGameProfilePath(game);
        var routedDevices = devices.Append(device).Distinct().ToArray();
        string? unavailable = null;
        var candidates = available.Where(output => output.DeviceId == device && output.Kind == kind && !used.Contains(output) &&
            (kind != "button" || output.InputId <= GremlinButtonLimit(game)) && (!needsSlider || output.InputId is 7 or 8));
        var ordered = VJoyInputMap.OrderOutputs(candidates, signal).ToArray();
        var assignments = File.Exists(path) ? ReadRoutedGameBindings(game, path, ordered, routedDevices) : null;
        foreach (var next in ordered)
        {
            if (assignments is not null)
            {
                var (bindings, status) = assignments[next];
                if (status is not null || bindings.Any(binding => binding.Map != action.Map || binding.Action != action.Id))
                {
                    unavailable ??= status;
                    continue;
                }
            }
            SetVirtualRouteForPhysicalInput(game, address, next.DeviceId, next);
            return next;
        }
        throw new InvalidOperationException(unavailable ?? (needsSlider ? "Choose an available vJoy slider for this control."
            : $"Choose an available vJoy {kind} for this control."));
    }

    private sealed record RoutedGameExport(string ProfilePath, IReadOnlyList<NativeBindingWrite> Writes, Action? EnsureClosed, string Details);

    private RoutedGameExport PrepareRoutedGameExport(GameTarget game, IReadOnlyList<BindingEdit> edits)
    {
        var path = RoutedGameProfilePath(game, refreshWarThunder: true);
        if (path is null || !File.Exists(path))
            throw new InvalidOperationException($"Choose the {game.Name} game binding profile next to the Gremlin profile.");
        var ids = RoutedDeviceIds(game).Concat(edits.Select(edit => edit.RoutedOutput!.DeviceId)).Distinct().ToArray();
        var star = game.IsStarCitizen ? new StarCitizenVJoyBindings(StarCitizenGame.LoadProfile(path), ids) : null;
        var thunder = game.Kind == GameKind.WarThunder
            ? new WarThunderVJoyBindings(WarThunderProfile.Load(path), VJoyOutputs.Read() ?? []) : null;
        var swgCatalog = game.IsSwg ? SwgCatalog.Load(GameFolder(game), path) : null;
        var swg = swgCatalog is null ? null : new SwgVJoyBindings(SwgNativeProfile.Load(swgCatalog.SourceFiles), ids);
        var swgTargets = new Dictionary<GremlinOutput, (string Map, string Id)>();
        var details = new StringBuilder().AppendLine($"GAME BINDINGS · {game.Name}").AppendLine(path);
        foreach (var edit in edits)
        {
            var output = edit.RoutedOutput ?? throw new InvalidOperationException("Choose a vJoy destination for each game binding.");
            bool remove = edit.RemoveInput is not null;
            if (edit.RoutedProfile is { } destination && !SameRoutedProfile(game, destination, path))
                throw new InvalidOperationException($"The change to {edit.Action.Name} targets {Path.GetFileName(destination)}. Select that game binding profile to export it.");
            if (!remove)
            {
                var targets = ControlRoutes(game, edit.Address).Select(row => row.Target).Distinct().ToArray();
                if (targets is not [{ Output: { } effective }] || effective != output)
                    throw new InvalidOperationException($"The route for {edit.Action.Name} changed. Unassign the action, then assign it to the new output.");
            }
            string native = star?.Input(output) ?? thunder?.Input(output) ?? swg!.Input(output);
            if (swg is not null && !remove)
            {
                var action = (edit.Action.Map, edit.Action.Id);
                if (!swgTargets.TryAdd(output, action) && swgTargets[output] != action)
                    throw new InvalidOperationException($"{output} has competing SWG Space assignments. Choose one action for that vJoy input.");
            }
            var swgChange = swg?.Apply(edit.Action, output, edit.RemoveInput);
            var activation = EditActivation(game, edit);
            bool changed = star?.Apply(edit.Action, output, activation, edit.RemoveInput, edit.NativeActivation) ??
                thunder?.Apply(edit.Action, output, activation, edit.RemoveInput) ?? swgChange!.Changed;
            var hardware = GetLayout(edit.Address.Device);
            details.AppendLine($"  {(remove ? "Unassign" : "Assign")} · {PhysicalLabel(hardware, edit.Address)} → {output} → {edit.Action.Name}");
            details.AppendLine($"    {native}{(activation == BindingActivation.Default ? "" : $" · {activation.Label()}")}{(changed ? "" : " · already matches")}");
            if (swgChange?.ReplacedActionId is { } replaced)
                details.AppendLine($"    Replaces {swgCatalog!.Actions.FirstOrDefault(action => action.Map == edit.Action.Map && action.Id == replaced)?.Name ?? replaced}.");
        }
        IReadOnlyList<NativeBindingWrite> writes = star is not null
            ? star.ToWrite() is { } write ? [write] : []
            : thunder?.ToWrites() ?? swg!.ToWrites();
        Action? ensureClosed = null;
        if (writes.Count > 0)
            ensureClosed = thunder is not null ? thunder.EnsureGameClosed : swg is not null ? swg.EnsureGameClosed : null;
        details.AppendLine(game.Kind switch
        {
            GameKind.StarCitizen => "Load the saved profile in Star Citizen and select its vJoy joystick slot.",
            GameKind.WarThunder => "Import the updated preset in War Thunder if you selected an exported controls file.",
            _ => "Select the routed vJoy joystick in the SWG launcher before entering space."
        });
        return new RoutedGameExport(path, writes, ensureClosed, details.ToString());
    }
}
