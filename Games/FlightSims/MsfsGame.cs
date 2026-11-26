using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace VisualControlsBinder.Games;

public static class MsfsGame
{
    private const int MaxSourceBytes = 16 * 1024 * 1024;
    private const int MaxCandidates = 128;
    private const string Aircraft = "AIRCRAFT";
    private const string Basic = "BASIC_CONTROL";
    private const string Surfaces = "SURFACES";
    private const string Always = "ALWAYS";
    private const string Plane = "PLANE";
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;

    public static string DefaultFolder(bool is2024)
    {
        var package = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages",
            is2024 ? "Microsoft.Limitless_8wekyb3d8bbwe" : "Microsoft.FlightSimulator_8wekyb3d8bbwe");
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            is2024 ? "Microsoft Flight Simulator 2024" : "Microsoft Flight Simulator");
        return Directory.Exists(package) || !Directory.Exists(roaming) ? package : roaming;
    }

    public static IReadOnlyList<string> FindProfiles(string folder, bool is2024) =>
        ReadProfiles(UserProfileCandidates(folder, is2024), MaxCandidates, null).Select(source => source.Path).ToArray();

    public static string? FindProfileFolder(string folder, bool is2024)
    {
        var profile = ReadProfiles(UserProfileCandidates(folder, is2024), MaxCandidates, null).FirstOrDefault().Path;
        if (profile is not null)
            return Path.GetDirectoryName(profile);

        string? selected = string.IsNullOrWhiteSpace(folder) ? null : Path.GetFullPath(folder);
        var local = selected is null ? Array.Empty<string>() : new[] { WgsFolder(selected), Path.Combine(selected, "Input") };
        return local.Concat(SteamRemoteFolders(is2024)).Append(selected).FirstOrDefault(Directory.Exists);
    }

    public static IReadOnlyList<GameAction> LoadActions(string folder, string? profile = null, bool is2024 = false)
    {
        var sources = new MsfsCatalogSources(folder, is2024);
        var labels = sources.ReadEnglishLabels();
        var imported = string.IsNullOrWhiteSpace(profile)
            ? ReadProfiles(UserProfileCandidates(folder, is2024), MaxCandidates, labels)
                .Concat(ReadProfiles(sources.StockProfiles(), 1024, labels)).SelectMany(source => source.Actions)
            : ReadActions(profile, labels);
        var actions = imported.GroupBy(row => (row.Map, row.Id)).Select(group =>
        {
            var first = group.First();
            return first with { IsAxis = group.Any(row => row.IsAxis) };
        }).ToArray();
        if (actions.Length > 0)
            return actions.OrderBy(action => action.Category, IgnoreCase).ThenBy(action => action.Name, IgnoreCase).ToArray();
        if (!string.IsNullOrWhiteSpace(profile))
            throw new InvalidDataException("No Microsoft Flight Simulator actions were found in this control profile or ActionDB.");

        return StarterEvents.Where(row => is2024 || !row.Requires2024).Select(row => new GameAction(
            is2024 ? row.Context : Plane, row.Id, row.Category, "", row.Name, row.IsAxis)).ToArray();
    }

    private static IEnumerable<(string Path, GameAction[] Actions)> ReadProfiles(IEnumerable<string> candidates,
        int limit, IReadOnlyDictionary<string, string>? labels)
    {
        foreach (string path in candidates.Distinct(IgnoreCase).Take(limit))
        {
            GameAction[] rows;
            try { rows = ReadActions(path, labels); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException or InvalidDataException)
            {
                continue;
            }
            if (rows.Length > 0)
                yield return (path, rows);
        }
    }

    private static IEnumerable<string> UserProfileCandidates(string folder, bool is2024)
    {
        if (!string.IsNullOrWhiteSpace(folder))
        {
            string selected = Path.GetFullPath(folder);
            foreach (var path in Files(WgsFolder(selected), 3).Where(IsWgsBlob))
                yield return path;
            foreach (var path in Files(selected, 1).Concat(Files(Path.Combine(selected, "Input"), 2)).Where(IsNamedProfile))
                yield return path;
        }

        // Steam stores controller profiles in the game's own remote directory, keyed by its product AppID.
        foreach (string remote in SteamRemoteFolders(is2024))
            foreach (string path in Files(remote, 2).Where(IsNamedProfile))
                yield return path;
    }

    private static string WgsFolder(string folder)
    {
        string package = IgnoreCase.Equals(Path.GetFileName(folder), "LocalCache")
            ? Path.GetDirectoryName(folder) ?? folder : folder;
        return IgnoreCase.Equals(Path.GetFileName(package), "wgs") ? package : Path.Combine(package, "SystemAppData", "wgs");
    }

    private static IEnumerable<string> SteamRemoteFolders(bool is2024)
    {
        if (!OperatingSystem.IsWindows())
            yield break;
        var users = Path.Combine(GameInstallations.SteamFolder(), "userdata");
        if (!Directory.Exists(users))
            yield break;
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        string appId = is2024 ? "2537590" : "1250410";
        foreach (string user in Directory.EnumerateDirectories(users, "*", options).Take(20))
            if (Path.GetFileName(user).All(char.IsAsciiDigit))
                yield return Path.Combine(user, appId, "remote");
    }

    internal static IEnumerable<string> Files(string path, int levels, string pattern = "*")
    {
        if (!Directory.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            return [];
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = levels,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        return Directory.EnumerateFiles(path, pattern, options).Take(1024);
    }

    private static bool IsWgsBlob(string path)
    {
        string name = Path.GetFileName(path);
        return name.Length == 32 && name.All(char.IsAsciiHexDigit);
    }

    private static bool IsNamedProfile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".xml" or ".actiondb" or ".inputprofile" ||
        Path.GetFileName(path).StartsWith("inputprofile_", StringComparison.OrdinalIgnoreCase);

    private static GameAction[] ReadActions(string path, IReadOnlyDictionary<string, string>? labels)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxSourceBytes)
            throw new InvalidDataException("The Microsoft Flight Simulator control profile exceeds 16 MB.");
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            ConformanceLevel = ConformanceLevel.Fragment, DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null, MaxCharactersInDocument = MaxSourceBytes, IgnoreComments = true, IgnoreWhitespace = true
        });

        // Controller exports/WGS entries use Version, FriendlyName and Device as adjacent top-level elements.
        var rows = new List<GameAction>();
        while (reader.MoveToContent() == XmlNodeType.Element)
        {
            var item = (XElement)XNode.ReadFrom(reader);
            switch (item.Name.LocalName)
            {
                case "Device" when item.Attribute("DeviceName") is not null:
                    rows.AddRange(DeviceActions(item, labels));
                    break;
                case "DefaultInput":
                case "DefaulftInput": // Spelling in the bundled MSFS 2024 PC profile package.
                    rows.AddRange(item.Elements("Device").SelectMany(device => DeviceActions(device, labels)));
                    break;
                case "ActionDefinition":
                    rows.AddRange(item.Elements("Actions").SelectMany(group => group.Elements("Action")).Select(action => ActionDefinition(action, labels))
                        .OfType<GameAction>());
                    break;
            }
        }
        return rows.ToArray();
    }

    private static IEnumerable<GameAction> DeviceActions(XElement device, IReadOnlyDictionary<string, string>? labels)
    {
        foreach (var context in device.Elements("Context"))
        {
            string? map = ((string?)context.Attribute("ContextName"))?.Trim();
            if (string.IsNullOrWhiteSpace(map))
                continue;
            foreach (var action in context.Elements("Action"))
            {
                string? id = ((string?)action.Attribute("ActionName"))?.Trim();
                if (string.IsNullOrWhiteSpace(id))
                    continue;
                int.TryParse((string?)action.Attribute("Flag"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int flags);
                var known = StarterById.GetValueOrDefault(id);
                string categoryFromContext = CategoryName(map);
                string category = known?.Category ?? categoryFromContext;
                yield return new GameAction(map, id, category, category == categoryFromContext ? "" : categoryFromContext,
                    labels?.GetValueOrDefault("INPUT." + id) ?? known?.Name ?? HumanName(id),
                    (flags & 5) != 0 || known is { IsAxis: true } || AxisName(id));
            }
        }
    }

    private static GameAction? ActionDefinition(XElement action, IReadOnlyDictionary<string, string>? labels)
    {
        string? id = ((string?)action.Element("Name"))?.Trim();
        string? map = ((string?)action.Element("Context"))?.Trim();
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(map))
            return null;
        string category = DisplayText(action.Element("TT_Category_Main"), labels) ?? CategoryName(map);
        string section = DisplayText(action.Element("TT_Category_Sub"), labels) ?? "";
        string name = DisplayText(action.Element("TT_Name"), labels) ?? HumanName(id);
        bool axis = IgnoreCase.Equals(((string?)action.Element("Type"))?.Trim(), "AXIS");
        return new GameAction(map, id, category, section, name, axis);
    }

    private static string? DisplayText(XElement? element, IReadOnlyDictionary<string, string>? labels)
    {
        string? text = ((string?)element)?.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return null;
        return text.StartsWith("TT:", StringComparison.OrdinalIgnoreCase) ? labels?.GetValueOrDefault(text[3..]) : text;
    }

    private static bool AxisName(string id) => id.Contains("_AXIS_", StringComparison.OrdinalIgnoreCase)
        || id.StartsWith("AXIS_", StringComparison.OrdinalIgnoreCase);

    private static string CategoryName(string context) => context switch
    {
        Surfaces => "Flight Control Surfaces",
        Basic => "Basic Controls",
        Always => "General Controls",
        Plane or Aircraft => "Aircraft Controls",
        _ => HumanName(context)
    };

    private static string HumanName(string id) =>
        FlightActionNames.FromId(id.StartsWith("KEY_", StringComparison.OrdinalIgnoreCase) ? id[4..] : id);

    // Common assignable events from the 2020 and 2024 SDK key event references.
    private static readonly StarterEvent[] StarterEvents =
    [
        new("KEY_AXIS_AILERONS_SET", Surfaces, "Flight Control Surfaces", "Ailerons Axis", true),
        new("KEY_AXIS_ELEVATOR_SET", Surfaces, "Flight Control Surfaces", "Elevator Axis", true),
        new("KEY_AXIS_RUDDER_SET", Basic, "Flight Control Surfaces", "Rudder Axis", true),
        new("KEY_ELEV_TRIM_UP", Aircraft, "Flight Control Surfaces", "Elevator Trim Up"),
        new("KEY_ELEV_TRIM_DN", Aircraft, "Flight Control Surfaces", "Elevator Trim Down"),
        new("KEY_AILERON_TRIM_LEFT", Aircraft, "Flight Control Surfaces", "Aileron Trim Left"),
        new("KEY_AILERON_TRIM_RIGHT", Aircraft, "Flight Control Surfaces", "Aileron Trim Right"),
        new("KEY_RUDDER_TRIM_LEFT", Aircraft, "Flight Control Surfaces", "Rudder Trim Left"),
        new("KEY_RUDDER_TRIM_RIGHT", Aircraft, "Flight Control Surfaces", "Rudder Trim Right"),
        new("KEY_FLAPS_INCR", Aircraft, "Flight Control Surfaces", "Increase Flaps"),
        new("KEY_FLAPS_DECR", Aircraft, "Flight Control Surfaces", "Decrease Flaps"),
        new("KEY_FLAPS_UP", Aircraft, "Flight Control Surfaces", "Retract Flaps"),
        new("KEY_FLAPS_DOWN", Aircraft, "Flight Control Surfaces", "Extend Flaps"),
        new("KEY_AXIS_SPOILER_SET", Aircraft, "Flight Control Surfaces", "Spoilers Axis", true),
        new("KEY_SPOILERS_TOGGLE", Aircraft, "Flight Control Surfaces", "Toggle Spoilers"),
        new("KEY_SPOILERS_ARM_TOGGLE", Aircraft, "Flight Control Surfaces", "Toggle Arm Spoilers"),
        new("KEY_GEAR_TOGGLE", Aircraft, "Landing Gear", "Toggle Landing Gear"),
        new("KEY_GEAR_UP", Aircraft, "Landing Gear", "Landing Gear Up"),
        new("KEY_GEAR_DOWN", Aircraft, "Landing Gear", "Landing Gear Down"),
        new("KEY_BRAKES", Basic, "Brakes", "Brakes"),
        new("KEY_PARKING_BRAKES", Aircraft, "Brakes", "Toggle Parking Brakes"),
        new("KEY_AXIS_LEFT_BRAKE_SET", Basic, "Brakes", "Left Brake Axis", true),
        new("KEY_AXIS_RIGHT_BRAKE_SET", Basic, "Brakes", "Right Brake Axis", true),
        new("KEY_THROTTLE_AXIS_SET_EX1", Aircraft, "Power Management", "Throttle Axis", true),
        new("KEY_THROTTLE1_AXIS_SET_EX1", Aircraft, "Power Management", "Throttle 1 Axis", true),
        new("KEY_THROTTLE2_AXIS_SET_EX1", Aircraft, "Power Management", "Throttle 2 Axis", true),
        new("KEY_AXIS_MIXTURE_SET", Aircraft, "Power Management", "Mixture Axis", true),
        new("KEY_PROP_PITCH_AXIS_SET_EX1", Aircraft, "Power Management", "Propeller Axis", true),
        new("KEY_THROTTLE_INCR", "THROTTLE", "Power Management", "Increase Throttle"),
        new("KEY_THROTTLE_DECR", "THROTTLE", "Power Management", "Decrease Throttle"),
        new("KEY_AP_MASTER", Aircraft, "Autopilot", "Toggle Autopilot Master"),
        new("KEY_AP_HDG_HOLD", Aircraft, "Autopilot", "Toggle Heading Hold"),
        new("KEY_AP_ALT_HOLD", Aircraft, "Autopilot", "Toggle Altitude Hold"),
        new("KEY_AP_NAV1_HOLD", Aircraft, "Autopilot", "Toggle NAV1 Hold"),
        new("KEY_AP_APR_HOLD", Aircraft, "Autopilot", "Toggle Approach Hold"),
        new("KEY_HEADING_BUG_INC", Aircraft, "Autopilot", "Increase Heading Bug"),
        new("KEY_HEADING_BUG_DEC", Aircraft, "Autopilot", "Decrease Heading Bug"),
        new("KEY_TOGGLE_FLIGHT_DIRECTOR", Aircraft, "Autopilot", "Toggle Flight Director"),
        new("KEY_TOGGLE_BEACON_LIGHTS", Aircraft, "Lights", "Toggle Beacon Lights"),
        new("KEY_LANDING_LIGHTS_TOGGLE", Aircraft, "Lights", "Toggle Landing Lights"),
        new("KEY_TOGGLE_NAV_LIGHTS", Aircraft, "Lights", "Toggle Navigation Lights"),
        new("KEY_TOGGLE_TAXI_LIGHTS", Aircraft, "Lights", "Toggle Taxi Lights"),
        new("KEY_STROBES_TOGGLE", Aircraft, "Lights", "Toggle Strobes"),
        new("KEY_TOGGLE_MASTER_BATTERY", Aircraft, "Electrical", "Toggle Master Battery"),
        new("KEY_TOGGLE_MASTER_ALTERNATOR", Aircraft, "Electrical", "Toggle Master Alternator"),
        new("KEY_PITOT_HEAT_TOGGLE", Aircraft, "Aircraft Systems", "Toggle Pitot Heat"),
        new("KEY_PUSHBACK_SET", "SIM_SHORTCUT", "Ground Operations", "Toggle Pushback"),
        new("KEY_ATC", "INGAME_UI", "Communications", "Toggle ATC Panel"),
        new("KEY_PAUSE_TOGGLE", "MODE_PAUSE", "General Controls", "Toggle Pause"),
        new("KEY_MENU_SR_EFB_TOGGLE", Always, "General Controls", "Toggle EFB", Requires2024: true),
        new("KEY_PC_CROUCH_TOGGLE", "PLAYER_CHARACTER_MOVEMENT", "Walkaround", "Toggle Crouch", Requires2024: true)
    ];

    private static readonly IReadOnlyDictionary<string, StarterEvent> StarterById = StarterEvents.ToDictionary(row => row.Id, IgnoreCase);
    private sealed record StarterEvent(string Id, string Context, string Category, string Name, bool IsAxis = false,
        bool Requires2024 = false);
}
