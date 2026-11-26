using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using VisualControlsBinder.Games;
using VisualControlsBinder.Inputs;

namespace VisualControlsBinder.Planner;

internal sealed record ControlAddress(string Device, string Control, string Layer);
internal sealed record LearnedControl(ControlAddress Address, string Token);
internal sealed record BindingEdit(string Game, ControlAddress Address, GameAction Action, string? RemoveInput = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public BindingActivation Activation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeActivation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ViaVJoy { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GremlinOutput? RoutedOutput { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoutedProfile { get; init; }
}
internal sealed record PlannedDevice(string LayoutId, Guid? InstanceId = null, Guid? ProductId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SlotId = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseLayoutId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LeftPanelLayoutId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RightPanelLayoutId { get; init; }

    [JsonIgnore]
    public string Id => SlotId ?? LayoutId;

    public string DisplayName(string layoutName) =>
        SlotId is null ? layoutName : $"{layoutName} · {SlotId[(LayoutId.Length + 1)..]}";
}
internal enum OutputMode { DirectToGame, JoystickGremlin }
internal enum GameKind { StarCitizen, Swg, Dcs, WarThunder, Il2, Elite, Discovered, Squadrons, CustomVJoy, Msfs2020, Msfs2024, CliffsOfDover }
internal sealed record VirtualRoute(string Game, ControlAddress Address, int DeviceId, GremlinOutput? Output);
internal sealed record VirtualRouteRemoval(string Game, ControlAddress Address, GremlinOutput Output,
    string? InputToken = null, Guid? InstanceId = null, bool Applied = false)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NativeMode { get; init; }
}
internal sealed record DcsProfileSelection(string ModuleId, string DeviceId, string Path);
internal sealed record RoutedProfileSelection(string Game, string GremlinPath, string GamePath);
internal sealed record GremlinShiftSetting(string Game, ControlAddress Address, GremlinShiftBehavior? Behavior)
{
    public const string Layer = "Gremlin Shift";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseMode { get; init; }
}

internal sealed class BindingPlan
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public int Version { get; set; } = 3;
    public List<PlannedDevice> Devices { get; set; } = [];
    public List<LearnedControl> Learned { get; set; } = [];
    public List<BindingEdit> Edits { get; set; } = [];
    public List<GameAction> CustomActions { get; set; } = [];
    public Dictionary<string, string> GameFolders { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> SourceProfiles { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> CharacterProfiles { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, OutputMode> OutputModes { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> GremlinProfiles { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Aircraft { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> NativeJoysticks { get; set; } = new(StringComparer.Ordinal);
    public List<VirtualRoute> VirtualRoutes { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<VirtualRouteRemoval>? RemovedVirtualRoutes { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DcsProfileSelection>? DcsProfiles { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<GremlinShiftSetting>? GremlinShifts { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RoutedProfileSelection>? RoutedProfiles { get; set; }

    public string? GetRoutedProfile(string game, string gremlinPath) => RoutedProfiles?
        .FirstOrDefault(item => item.Game == game && item.GremlinPath.Equals(gremlinPath, StringComparison.OrdinalIgnoreCase))?.GamePath;

    public void SetRoutedProfile(string game, string gremlinPath, string gamePath)
    {
        RoutedProfiles ??= [];
        RoutedProfiles.RemoveAll(item => item.Game == game && item.GremlinPath.Equals(gremlinPath, StringComparison.OrdinalIgnoreCase));
        RoutedProfiles.Add(new RoutedProfileSelection(game, gremlinPath, Path.GetFullPath(gamePath)));
    }

    public GremlinShiftSetting? GetGremlinShift(string game) => GremlinShifts?.FirstOrDefault(item => item.Game == game);

    public void SetGremlinShift(string game, ControlAddress address, GremlinShiftBehavior? behavior, string? baseMode = null)
    {
        baseMode ??= GetGremlinShift(game)?.BaseMode;
        GremlinShifts ??= [];
        GremlinShifts.RemoveAll(item => item.Game == game);
        GremlinShifts.Add(new GremlinShiftSetting(game, address, behavior) { BaseMode = baseMode });
    }

    public string? GetDcsProfile(string moduleId, string deviceId) =>
        DcsProfiles?.FirstOrDefault(profile => profile.ModuleId == moduleId && profile.DeviceId == deviceId)?.Path;

    public void SetDcsProfile(string moduleId, string deviceId, string? path)
    {
        if (path is not null)
        {
            path = Path.GetFullPath(path);
            if (!path.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Choose a DCS controller profile with a .diff.lua extension.", nameof(path));
        }
        DcsProfiles?.RemoveAll(profile => profile.ModuleId == moduleId && profile.DeviceId == deviceId);
        if (path is not null)
            (DcsProfiles ??= []).Add(new DcsProfileSelection(moduleId, deviceId, path));
        else if (DcsProfiles?.Count == 0)
            DcsProfiles = null;
    }

    public PlannedDevice AddDevice(string layoutId) => AddDeviceAssembly(layoutId, null, null, null);

    public PlannedDevice AddDeviceAssembly(string layoutId, string? baseLayoutId, string? leftPanel, string? rightPanel)
    {
        var ids = Devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal);
        string? copyId = null;
        for (int copy = 2; ids.Contains(copyId ?? layoutId); copy++)
            copyId = FormattableString.Invariant($"{layoutId}#{copy}");
        var slot = new PlannedDevice(layoutId, SlotId: copyId)
        {
            BaseLayoutId = baseLayoutId, LeftPanelLayoutId = leftPanel, RightPanelLayoutId = rightPanel
        };
        Devices.Add(slot);
        return slot;
    }

    public bool HasMappedDeviceInputs(string slotId) => Learned.Any(item => item.Address.Device == slotId)
        || Edits.Any(item => item.Address.Device == slotId) || VirtualRoutes.Any(item => item.Address.Device == slotId)
        || RemovedVirtualRoutes?.Any(item => item.Address.Device == slotId && !item.Applied) == true;

    public bool RemoveDevice(string slotId)
    {
        int index = Devices.FindIndex(slot => slot.Id == slotId);
        if (index < 0)
            return false;
        Devices.RemoveAt(index);
        Learned.RemoveAll(item => item.Address.Device == slotId);
        Edits.RemoveAll(item => item.Address.Device == slotId);
        if (GremlinShifts is not null)
            for (int i = 0; i < GremlinShifts.Count; i++)
                if (GremlinShifts[i].Address.Device == slotId)
                    GremlinShifts[i] = GremlinShifts[i] with { Behavior = null };
        VirtualRoutes.RemoveAll(item => item.Address.Device == slotId);
        DcsProfiles?.RemoveAll(profile => profile.DeviceId == slotId);
        if (DcsProfiles?.Count == 0)
            DcsProfiles = null;
        RemovedVirtualRoutes?.RemoveAll(item => item.Address.Device == slotId);
        if (RemovedVirtualRoutes?.Count == 0)
            RemovedVirtualRoutes = null;
        foreach (string game in NativeJoysticks.Where(link => link.Value == slotId).Select(link => link.Key).ToArray())
            NativeJoysticks.Remove(game);
        return true;
    }

    public VirtualRoute? GetVirtualRoute(string game, ControlAddress address) =>
        VirtualRoutes.FirstOrDefault(route => route.Game == game && route.Address == address);

    public void SetVirtualRoute(string game, ControlAddress address, int deviceId, GremlinOutput? output)
    {
        if (deviceId <= 0)
        {
            RemoveVirtualRoute(game, address);
            return;
        }

        if (output is null)
            RemoveVirtualRoute(game, address);
        else
            VirtualRoutes.RemoveAll(route => route.Game == game && route.Address == address);
        VirtualRoutes.Add(new VirtualRoute(game, address, deviceId, output));
        if (output is not null && RemovedVirtualRoutes is { } removals)
        {
            string? input = GetSignal(address)?.Token;
            Guid? device = Devices.FirstOrDefault(item => item.Id == address.Device)?.InstanceId;
            removals.RemoveAll(item => item.Game == game && item.Address == address
                && (device is null || item.InstanceId is null || item.InstanceId == device)
                && (input is null || item.InputToken is null || item.InputToken == input));
            if (removals.Count == 0)
                RemovedVirtualRoutes = null;
        }
    }

    public bool RemoveVirtualRoute(string game, ControlAddress address, InputSignal? signal = null)
    {
        var route = GetVirtualRoute(game, address);
        if (route is null)
            return false;

        VirtualRoutes.RemoveAll(item => item.Game == game && item.Address == address);
        if (route.Output is { } output)
        {
            signal ??= GetSignal(address);
            string? input = signal?.Kind == output.Kind ? signal.Token : null;
            Guid? device = Devices.FirstOrDefault(item => item.Id == address.Device)?.InstanceId;
            AddVirtualRouteRemoval(new VirtualRouteRemoval(game, address, output, input, device));
        }
        return true;
    }

    public void AddVirtualRouteRemoval(VirtualRouteRemoval removal)
    {
        var removals = RemovedVirtualRoutes ??= [];
        int index = removals.FindIndex(item => (item.Game, item.Address, item.Output, item.InputToken, item.InstanceId, item.NativeMode)
            == (removal.Game, removal.Address, removal.Output, removal.InputToken, removal.InstanceId, removal.NativeMode));
        if (index < 0)
            removals.Add(removal);
        else
            removals[index] = removal;
    }

    public void MarkVirtualRouteRemovalApplied(VirtualRouteRemoval change, Guid device, string input)
    {
        if (RemovedVirtualRoutes is not { } removals)
            return;
        int index = removals.IndexOf(change);
        if (index >= 0)
            removals[index] = change with { Applied = true, InstanceId = device, InputToken = input };
    }

    public InputSignal? GetSignal(ControlAddress address) =>
        InputSignal.Parse(Learned.FirstOrDefault(item => item.Address == address)?.Token);

    public void Remember(ControlAddress address, InputSignal signal)
    {
        Learned.RemoveAll(item => item.Address == address);
        Learned.Add(new LearnedControl(address, signal.Token));
    }

    public BindingPlan CreateShareableCopy() => new()
    {
        Devices = Devices.Select(device => device with { InstanceId = null, ProductId = null }).ToList(),
        Learned = [.. Learned],
        Edits = Edits.Where(edit => edit.RemoveInput is null)
            .Select(edit => edit with { RoutedProfile = null, NativeActivation = null }).ToList(),
        CustomActions = [.. CustomActions],
        OutputModes = new(OutputModes, StringComparer.Ordinal),
        Aircraft = new(Aircraft, StringComparer.Ordinal),
        NativeJoysticks = new(NativeJoysticks, StringComparer.Ordinal),
        VirtualRoutes = [.. VirtualRoutes],
        GremlinShifts = GremlinShifts?.Where(shift => shift.Behavior is not null)
            .Select(shift => shift with { BaseMode = null }).ToList()
    };

    public bool HasNetworkOrDevicePaths() => GameFolders.Values
        .Concat(SourceProfiles.Values).Concat(CharacterProfiles.Values).Concat(GremlinProfiles.Values)
        .Concat(DcsProfiles?.Select(profile => profile.Path) ?? [])
        .Concat(RoutedProfiles?.SelectMany(profile => new[] { profile.GremlinPath, profile.GamePath }) ?? [])
        .Concat(Edits.Select(edit => edit.RoutedProfile))
        .Any(IsNetworkOrDevicePath);

    private static bool IsNetworkOrDevicePath(string? path)
    {
        var value = path.AsSpan().TrimStart();
        return value.Length >= 2 && (value[0] is '\\' or '/') && (value[1] is '\\' or '/');
    }

    public static BindingPlan Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var document = JsonDocument.Parse(stream);
        var plan = document.RootElement.Deserialize<BindingPlan>(Json)
            ?? throw new InvalidDataException("The plan is empty.");
        if (!document.RootElement.EnumerateObject().Any(property => property.Name.Equals("version", StringComparison.OrdinalIgnoreCase)))
            plan.Version = 1;
        return Normalize(plan);
    }

    internal static BindingPlan Normalize(BindingPlan plan)
    {
        if (plan.Version is not (1 or 2 or 3) || plan.Edits is null || plan.Learned is null || plan.CustomActions is null || plan.GameFolders is null ||
            plan.SourceProfiles is null || plan.CharacterProfiles is null || plan.OutputModes is null ||
            plan.GremlinProfiles is null || plan.Aircraft is null || plan.NativeJoysticks is null || plan.VirtualRoutes is null)
            throw new InvalidDataException("This plan uses an unsupported format.");
        if (plan.Learned.Any(item => item?.Address is null) || plan.Edits.Any(item => item?.Address is null || item.Action is null)
            || plan.VirtualRoutes.Any(item => item?.Address is null))
            throw new InvalidDataException("This plan contains an incomplete controller or action reference.");
        if (plan.RoutedProfiles?.Any(item => item?.GremlinPath is null || string.IsNullOrWhiteSpace(item.Game) ||
            string.IsNullOrWhiteSpace(item.GamePath)) == true || plan.Edits.Any(edit => edit.ViaVJoy &&
            (edit.RoutedOutput is not { DeviceId: >= 1 and <= 16, InputId: >= 1, Kind: "button" or "axis" } ||
            edit.RemoveInput is not null && string.IsNullOrWhiteSpace(edit.RoutedProfile))))
            throw new InvalidDataException("This plan contains an incomplete vJoy game binding.");
        if (plan.RemovedVirtualRoutes?.Any(item => item?.Address is null || string.IsNullOrWhiteSpace(item.Game)
            || item.Output is null || item.Output.DeviceId <= 0 || item.Output.InputId <= 0
            || item.Output.Kind is not ("button" or "axis") || item.InstanceId == Guid.Empty
            || item.InputToken is not null && InputSignal.Parse(item.InputToken)?.Kind != item.Output.Kind) == true)
            throw new InvalidDataException("This plan contains an invalid vJoy route removal.");
        if (plan.RemovedVirtualRoutes?.Count == 0)
            plan.RemovedVirtualRoutes = null;
        if (plan.Version == 1)
        {
            // Older plans had no device list. Recover the referenced layouts without pairing controllers.
            plan.Devices = plan.Learned.Select(item => item.Address.Device)
                .Concat(plan.Edits.Select(item => item.Address.Device))
                .Concat(plan.VirtualRoutes.Select(item => item.Address.Device))
                .Concat(plan.RemovedVirtualRoutes?.Select(item => item.Address.Device) ?? [])
                .Concat(plan.NativeJoysticks.Values)
                .Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal)
                .Select(id => new PlannedDevice(id)).ToList();
        }
        if (plan.Devices is not null)
        {
            // Earlier ALPHA Prime layouts treated ordinary stick axes as a selectable base.
            for (int index = 0; index < plan.Devices.Count; index++)
            {
                if (plan.Devices[index] is { LayoutId: "virpil-constellation-alpha-prime-r" or "virpil-constellation-alpha-prime-l",
                        BaseLayoutId: "" or "virpil-warbrd-d-base" or "virpil-mongoost-cm3-base" } device)
                    plan.Devices[index] = device with { BaseLayoutId = null };
            }
        }
        if (plan.Devices is null || plan.Devices.Any(device => device is null || !ValidDevice(device))
            || plan.Devices.Select(device => device.Id).Distinct(StringComparer.Ordinal).Count() != plan.Devices.Count)
            throw new InvalidDataException("This plan contains an invalid device list.");
        if (plan.Devices.Where(device => device.InstanceId.HasValue).GroupBy(device => device.InstanceId).Any(group => group.Count() > 1))
            throw new InvalidDataException("This plan assigns one physical controller to multiple device slots.");
        if (plan.DcsProfiles is { } dcsProfiles &&
            (dcsProfiles.Any(profile => profile is null || string.IsNullOrWhiteSpace(profile.ModuleId) ||
                string.IsNullOrWhiteSpace(profile.DeviceId) || profile.Path is not { } path ||
                !Path.IsPathFullyQualified(path) || !path.EndsWith(".diff.lua", StringComparison.OrdinalIgnoreCase)) ||
                dcsProfiles.GroupBy(profile => (profile.ModuleId, profile.DeviceId)).Any(group => group.Count() > 1)))
            throw new InvalidDataException("This plan contains an invalid DCS profile selection.");
        plan.Version = 3;
        return plan;
    }

    private static bool ValidDevice(PlannedDevice device) => !string.IsNullOrWhiteSpace(device.LayoutId)
        && device.InstanceId.HasValue == device.ProductId.HasValue
        && device.InstanceId != Guid.Empty && device.ProductId != Guid.Empty
        && (device.BaseLayoutId != "" || device.InstanceId is null)
        && ((device.LeftPanelLayoutId is null && device.RightPanelLayoutId is null)
            || !string.IsNullOrEmpty(device.BaseLayoutId))
        && (device.SlotId is null || (device.SlotId.StartsWith(device.LayoutId + "#", StringComparison.Ordinal)
            && int.TryParse(device.SlotId.AsSpan(device.LayoutId.Length + 1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int copy) && copy >= 2));

    public string Fingerprint() => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this, Json)));

    public void Save(string path) => PlanSessionStore.WriteAtomically(path, JsonSerializer.SerializeToUtf8Bytes(this, Json));
}

internal sealed record GameTarget(string Id, string Name, string Folder, GameKind Kind)
{
    public bool IsStarCitizen => Kind == GameKind.StarCitizen;
    public bool IsSwg => Kind == GameKind.Swg;
    public bool IsMsfs => Kind is GameKind.Msfs2020 or GameKind.Msfs2024;
    public bool SupportsNativeEditing => Kind is GameKind.StarCitizen or GameKind.Swg or GameKind.WarThunder or GameKind.Il2 or GameKind.Elite
        or GameKind.Dcs or GameKind.Squadrons or GameKind.CliffsOfDover or GameKind.Msfs2020 or GameKind.Msfs2024;
    public OutputMode DefaultOutput => Kind is GameKind.StarCitizen or GameKind.Il2
        ? OutputMode.DirectToGame : OutputMode.JoystickGremlin;

    public static IReadOnlyList<GameTarget> All { get; } =
    [
        new("star-citizen", "Star Citizen", StarCitizenGame.DefaultInstallDirectory, GameKind.StarCitizen),
        new("swg-legends", "SWG Legends", StandaloneFlightSimInstalls.FindSwg("swg-legends") ??
            GameInstallations.StandaloneGame("SWG Legends", folder => StandaloneFlightSimInstalls.IsSwg(folder, "swg-legends")), GameKind.Swg),
        new("swg-restoration", "SWG Restoration", StandaloneFlightSimInstalls.FindSwg("swg-restoration") ??
            GameInstallations.StandaloneGame("SWG Restoration", folder => StandaloneFlightSimInstalls.IsSwg(folder, "swg-restoration")), GameKind.Swg),
        new("dcs", "DCS World", DcsGame.DefaultInstallDirectory, GameKind.Dcs),
        new("war-thunder", "War Thunder", WarThunderGame.DefaultInstallDirectory, GameKind.WarThunder),
        new("il2", "IL-2 Sturmovik: Battle of Stalingrad / Great Battles", Il2Game.DefaultInstallDirectory, GameKind.Il2),
        new("il2-cliffs-of-dover", "IL-2 Sturmovik: Cliffs of Dover Blitz", CliffsOfDoverGame.DefaultInstallDirectory, GameKind.CliffsOfDover),
        new("elite-dangerous", "Elite Dangerous", EliteDangerousGame.DefaultInstallDirectory, GameKind.Elite),
        new("star-wars-squadrons", "Star Wars: Squadrons", SquadronsGame.DefaultSettingsFolder, GameKind.Squadrons),
        new("msfs-2020", "Microsoft Flight Simulator 2020", MsfsGame.DefaultFolder(false), GameKind.Msfs2020),
        new("msfs-2024", "Microsoft Flight Simulator 2024", MsfsGame.DefaultFolder(true), GameKind.Msfs2024),
        new("custom-vjoy", "Custom to vJoy", "", GameKind.CustomVJoy)
    ];
}
