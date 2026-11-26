using System.IO;
using System.Text.RegularExpressions;

namespace VisualControlsBinder.Games;

public static partial class CliffsOfDoverGame
{
    public static string DefaultInstallDirectory { get; } = GameInstallations.SteamGame("IL-2 Sturmovik Cliffs of Dover Blitz");
    private const string ProfileName = "confUser.ini";

    public static string BindingsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "1C SoftClub", "il-2 sturmovik cliffs of dover");

    public static string DefaultProfilePath => Path.Combine(BindingsDirectory, ProfileName);

    internal static string StockProfilePath(string installDirectory) => Path.Combine(installDirectory, ProfileName);

    public static IReadOnlyList<string> FindSources(string installDirectory, string? profile)
    {
        string? stock = Directory.Exists(installDirectory) ? StockProfilePath(installDirectory) : null;
        string? user = !string.IsNullOrWhiteSpace(profile) ? profile : DefaultProfilePath;
        string? game = Directory.Exists(installDirectory) ? CliffsInstalledControls.GameAssembly(installDirectory) : null;
        return new[] { game, stock, user }.OfType<string>().Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<GameAction> LoadActions(string installDirectory, string? profile = null)
    {
        if (!string.IsNullOrWhiteSpace(profile) && !File.Exists(profile))
            throw new FileNotFoundException("The selected Cliffs of Dover controls file is unavailable.", profile);
        var sources = FindSources(installDirectory, profile);
        if (sources.Count == 0)
            throw new FileNotFoundException("Choose the Cliffs of Dover installation or a saved controls INI under Cliffs controls.");

        var actions = new Dictionary<(string Map, string Command), GameAction>();
        void Add(GameAction action) => actions.TryAdd((action.Map.ToLowerInvariant(), action.Id.ToLowerInvariant()), action);
        foreach (var (map, command) in CliffsInstalledControls.Read(installDirectory))
            if (CreateAction(map, command) is { } action)
                Add(action);
        foreach (string path in sources.Where(path => Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase)))
        {
            bool recognized = false;
            foreach (var action in ReadControls(path))
            {
                recognized = true;
                Add(action);
            }
            if (!recognized && string.Equals(path, profile, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The selected INI contains no Cliffs of Dover control commands.");
        }
        if (actions.Count == 0)
            throw new InvalidDataException("No Cliffs of Dover control commands were found.");

        return actions.Values.OrderBy(action => action.Category).ThenBy(action => action.Section)
            .ThenBy(action => action.Name).ToArray();
    }

    private static IEnumerable<GameAction> ReadControls(string path)
    {
        string map = "";
        bool calibration = false;
        foreach (string read in File.ReadLines(path))
        {
            string line = read.Trim();
            if (line.Length == 0 || line[0] is ';' or '#')
                continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                (map, calibration) = GetMap(line[1..^1].Trim());
                continue;
            }

            int equals = line.IndexOf('=');
            if (map.Length == 0 || equals <= 0)
                continue;
            string command = calibration ? line[..equals].Trim() : line[(equals + 1)..].Trim();
            if (calibration && map == "vehicleMove" && command != "power")
                continue; // The shipped file also contains a stray 'ower' calibration key.
            if (CreateAction(map, command) is { } action)
                yield return action;
        }
    }

    internal static GameAction? CreateAction(string map, string command)
    {
        bool axis = IsAxisMap(map);
        if (axis)
            command = command.StartsWith("-1", StringComparison.Ordinal) ? command[2..]
                : command.StartsWith('1') ? command[1..] : command;
        if (!CommandPattern().IsMatch(command))
            return null;
        var (category, section) = Labels(map);
        return new GameAction(map, command, category, section, CommandLabel(command, axis), axis);
    }

    internal static (string Map, bool Calibration) GetMap(string section)
    {
        if (section.StartsWith("HotKey ", StringComparison.OrdinalIgnoreCase))
            return (section[7..].Trim(), false);
        if (section.StartsWith("Config ", StringComparison.OrdinalIgnoreCase) && IsAxisMap(section[7..].Trim()))
            return (section[7..].Trim(), true);
        if (section.Equals("FreeFly", StringComparison.OrdinalIgnoreCase) ||
            section.Equals("FreeFlyXYZ", StringComparison.OrdinalIgnoreCase))
            return (section, false);
        return ("", false);
    }

    private static bool IsAxisMap(string map) => map.ToLowerInvariant() is "commonmove" or "pilotmove" or "turretmove" or "vehiclemove";

    private static (string Category, string Section) Labels(string map) => map.ToLowerInvariant() switch
    {
        "pilot" => ("Aircraft", "Keys"),
        "pilotmove" => ("Aircraft", "Axes"),
        "common" => ("General", "Cockpit and other controls"),
        "commonmove" => ("General", "Axes"),
        "bob" => ("General", "Game"),
        "gui" => ("General", "Interface"),
        "console" => ("General", "Console"),
        "view" => ("View", "Aircraft views"),
        "viewset" => ("Camera", "Camera movement"),
        "viewpan" => ("Camera", "Pan views"),
        "viewsnap" => ("Camera", "Snap views"),
        "freefly" or "freeflyxyz" or "camposmousexyz" => ("Camera", "Free camera"),
        "timespeed" => ("General", "Time compression"),
        "artillery_gunner" => ("Gun Positions", "Artillery"),
        "turret" => ("Gun Positions", "Turret keys"),
        "turretmove" => ("Gun Positions", "Turret axes"),
        "vehicledriver" => ("Vehicles", "Driving keys"),
        "vehiclegunner" => ("Vehicles", "Gunner"),
        "vehiclemove" => ("Vehicles", "Axes"),
        "vehicle" => ("Vehicles", "Vehicle keys"),
        "orders" => ("Orders", "Radio orders"),
        "chat" => ("Chat", "Keys"),
        "builder" or "buildermousexyz" => ("Mission Builder", "Editor"),
        _ => (Humanize(map), "Keys")
    };

    private static string CommandLabel(string command, bool axis)
    {
        string id = command.ToLowerInvariant();
        if (Names.TryGetValue(id, out string? name))
            return name;
        if (axis)
        {
            var match = EngineAxisPattern().Match(id);
            if (match.Success)
            {
                string control = match.Groups[1].Value;
                int engine = match.Groups[2].Value[0] - '0' + 1;
                return $"Engine {engine} {Names.GetValueOrDefault(control, control).ToLowerInvariant()}";
            }
        }
        return Humanize(command);
    }

    private static string Humanize(string value)
    {
        string words = WordBoundary().Replace(value.Replace('_', ' '), " ").Trim();
        return words.Length == 0 ? value : char.ToUpperInvariant(words[0]) + words[1..];
    }

    private static readonly IReadOnlyDictionary<string, string> Names =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["aileron"] = "Ailerons", ["elevator"] = "Elevator", ["rudder"] = "Rudder",
            ["trimaileron"] = "Aileron trim", ["trimelevator"] = "Elevator trim", ["trimrudder"] = "Rudder trim",
            ["throttle"] = "Throttle", ["throttle_wep"] = "War emergency power", ["pitch"] = "Propeller pitch",
            ["mix"] = "Mixture", ["radiator"] = "Radiator", ["oilradiator"] = "Oil radiator",
            ["compressor"] = "Supercharger", ["carbheater"] = "Carburetor heater",
            ["flaps"] = "Flaps", ["underc"] = "Undercarriage", ["airbrake"] = "Air brake",
            ["brakes"] = "Wheel brakes", ["brakeslt"] = "Left wheel brake", ["brakesrt"] = "Right wheel brake",
            ["turretyaw"] = "Turret yaw", ["turretpitch"] = "Turret pitch", ["bombbaydoor"] = "Bomb bay doors",
            ["door_open"] = "Open canopy, hatch or door", ["door_close"] = "Close canopy, hatch or door",
            ["door_toggle"] = "Toggle canopy, hatch or door", ["door_jettison"] = "Jettison canopy, hatch or door",
            ["toggleaim"] = "Toggle gunsight view", ["thtl_plus"] = "Increase throttle", ["thtl_minus"] = "Decrease throttle",
            ["thtl_wep"] = "War emergency power", ["uc"] = "Toggle undercarriage", ["uc_down_emer"] = "Emergency undercarriage extension",
            ["landingflaps_u"] = "Raise landing flaps", ["landingflaps_d"] = "Lower landing flaps",
            ["wheelbrakex"] = "Full wheel brake", ["pitch_plus"] = "Increase propeller pitch",
            ["pitch_minus"] = "Decrease propeller pitch", ["bombbaydoors"] = "Toggle bomb bay doors"
        };

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_+\-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex CommandPattern();

    [GeneratedRegex(@"^(throttle|pitch|mix|radiator|oilradiator|compressor|carbheater)([0-7])$", RegexOptions.CultureInvariant)]
    private static partial Regex EngineAxisPattern();

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])|(?<=[a-zA-Z])(?=[0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex WordBoundary();
}
