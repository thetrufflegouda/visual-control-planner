using System.Globalization;
using System.IO;

namespace VisualControlsBinder.Games;

internal static class SquadronsGame
{
    public const string ProfileName = "ProfileOptions_profile_synced";
    private const string Flight = "IncomStarshipInputConcepts";
    private const string Pilot = "IncomSoldierInputConcepts";
    private const string Menus = "IncomDefaultInputConcepts";
    private static readonly Dictionary<string, (string First, string Second)> Directions = new(StringComparer.Ordinal)
    {
        ["Pitch"] = ("Pitch Up", "Pitch Down"), ["Yaw"] = ("Yaw Right", "Yaw Left"),
        ["Roll"] = ("Roll Right", "Roll Left"), ["Throttle"] = ("Throttle Increase", "Throttle Decrease"),
        ["TargetingMenuX"] = ("Targeting Wheel X Right", "Targeting Wheel X Left"),
        ["TargetingMenuY"] = ("Targeting Wheel Y Up", "Targeting Wheel Y Down"),
        ["CommMenuX"] = ("Comms Wheel Navigate Right", "Comms Wheel Navigate Left"),
        ["CommMenuY"] = ("Comms Wheel Navigate Up", "Comms Wheel Navigate Down"),
        ["PowerTransferMenuX"] = ("Shield/Power Menu Right", "Shield/Power Menu Left"),
        ["CameraPitch"] = ("Free Look Camera Pitch Up", "Free Look Camera Pitch Down"),
        ["CameraYaw"] = ("Free Look Camera Yaw Right", "Free Look Camera Yaw Left")
    };
    private static readonly Dictionary<string, (string First, string Second)> SpectatorDirections = new(StringComparer.Ordinal)
    {
        ["SpectatorCameraPitch"] = ("Spectator Pitch Camera Up", "Spectator Pitch Camera Down"),
        ["SpectatorCameraYaw"] = ("Spectator Yaw Camera Right", "Spectator Yaw Camera Left"),
        ["SpectatorCameraRoll"] = ("Spectator Roll Camera Right", "Spectator Roll Camera Left"),
        ["SpectatorCameraMovementZ"] = ("Spectator Move Camera Forward", "Spectator Move Camera Backward"),
        ["SpectatorCameraFOVChange"] = ("Spectator Zoom In Camera", "Spectator Zoom Out Camera")
    };
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["Afterburner"] = "Boost", ["Fire"] = "Primary Weapon", ["FireCountermeasure"] = "Deploy Countermeasures",
        ["FireAuxiliaryWeaponOneMain"] = "Fire Left Auxiliary", ["FireAuxiliaryWeaponTwoMain"] = "Fire Right Auxiliary",
        ["FireAuxiliaryWeaponOneDoubleTap"] = "Dumb-Fire Left Auxiliary", ["FireAuxiliaryWeaponTwoDoubleTap"] = "Dumb-Fire Right Auxiliary",
        ["ShieldFront"] = "Focus Shields (Front)", ["ShieldBack"] = "Focus Shields (Rear)", ["ShieldBalance"] = "Focus Shields (Balanced)",
        ["VRRecenterCamera"] = "VR Recenter", ["TargetSelect"] = "Select Target Ahead", ["TargetCycleNext"] = "Cycle Targets",
        ["TargetHighestThreat"] = "Target My Attacker", ["CommMenu"] = "Comms Wheel", ["ToggleChat"] = "Toggle Chat Visibility",
        ["CommMenuSelect"] = "Comms Wheel (Toggle Mode) Select", ["TargetPing"] = "Ping Target",
        ["TargetingMenu"] = "Targeting Wheel", ["TargetingMenuSelectTargetingMethod"] = "Targeting Wheel (Toggle Mode) Select",
        ["EmergencyPowerTransferEngine"] = "Convert Power (Engines)", ["EmergencyPowerTransferWeapon"] = "Convert Power (Weapons)",
        ["EmergencyPowerTransferBalance"] = "Convert Power (Balanced)", ["ResetSystemsPower"] = "Balance Power",
        ["PowerTransferMenuSelect"] = "Shield/Power Menu Select", ["ConversationSkip"] = "Skip Conversation"
    };

    private static string SettingsRoot => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    private static string[] SettingsFolders() =>
    [
        Path.Combine(SettingsRoot, "STAR WARS Squadrons Steam", "settings"),
        Path.Combine(SettingsRoot, "STAR WARS Squadrons", "settings")
    ];

    public static string DefaultSettingsFolder
    {
        get
        {
            var folders = SettingsFolders();
            return folders.FirstOrDefault(folder => File.Exists(Path.Combine(folder, ProfileName)))
                ?? folders.FirstOrDefault(Directory.Exists) ?? folders[0];
        }
    }

    public static string? FindProfile() => SettingsFolders().Select(folder => Path.Combine(folder, ProfileName)).FirstOrDefault(File.Exists);

    public static IReadOnlyList<GameAction> LoadActions(string? path)
    {
        if (path is null)
            return Expand(StarterActions());
        if (new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new InvalidDataException("Choose the Squadrons ProfileOptions_profile_synced settings file.");

        var concepts = new HashSet<(string Context, string Concept)>();
        foreach (string line in File.ReadLines(path))
        {
            int end = line.IndexOf(' ');
            if (end < 0 || !line.StartsWith("GstKeyBinding.", StringComparison.Ordinal))
                continue;
            string[] key = line[..end].Split('.');
            if (key.Length != 5 || key[1] is not (Flight or Pilot or Menus) || key[4] != "type" ||
                !key[2].StartsWith("Concept", StringComparison.Ordinal) || key[2].Length <= 7 ||
                !int.TryParse(key[3], NumberStyles.None, CultureInfo.InvariantCulture, out _))
                continue;
            concepts.Add((key[1], key[2][7..]));
        }
        if (concepts.Count == 0)
            throw new InvalidDataException("No Squadrons key binding entries were found. Choose ProfileOptions_profile_synced from the game's settings folder.");
        return Expand(concepts);
    }

    private static IReadOnlyList<GameAction> Expand(IEnumerable<(string Context, string Concept)> concepts) => concepts
        .SelectMany(row => Actions(row.Context, row.Concept)).OrderBy(action => action.Category)
        .ThenBy(action => action.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();

    private static IEnumerable<GameAction> Actions(string context, string concept)
    {
        string category = context switch { Flight => "Flight", Pilot => "Hangar & Briefing Room", _ => "Menus" };
        string id = "Concept" + concept;
        var directionSet = context == Menus ? SpectatorDirections : Directions;
        bool directional = directionSet.TryGetValue(concept, out var pair);
        yield return new GameAction(context, id, category, "", Labels.GetValueOrDefault(concept) ?? FlightActionNames.FromId(concept),
            directional || concept == "PowerTransferMenuY");
        if (directional && context is Flight or Menus)
        {
            yield return new GameAction(context, id + "/positive", category, "", pair.First);
            yield return new GameAction(context, id + "/negative", category, "", pair.Second);
        }
    }

    // A small planning catalog is available before the game has created its settings file.
    private static IEnumerable<(string Context, string Concept)> StarterActions() =>
        new[] { "Pitch", "Yaw", "Roll", "Throttle", "Afterburner", "Drift", "Fire", "FireCountermeasure",
            "FireAuxiliaryWeaponOneMain", "FireAuxiliaryWeaponTwoMain", "ShieldFront", "ShieldBack", "ShieldBalance",
            "IncreaseEnginePower", "IncreaseWeaponPower", "IncreaseShieldPower", "TargetSelect", "TargetCycleNext",
            "TargetHighestThreat", "TargetingMenu", "CommMenu", "TargetPing", "FreeLook" }.Select(concept => (Flight, concept))
        .Concat(new[] { "TeamChat", "ToggleChat", "VRRecenterCamera", "Menu" }.Select(concept => (Menus, concept)));
}
