using System.IO;
using System.Xml.Linq;

namespace VisualControlsBinder.Games;

public static class EliteDangerousGame
{
    public static string DefaultInstallDirectory { get; } = GameInstallations.SteamGame("Elite Dangerous",
        StandaloneFlightSimInstalls.IsEliteDangerous, StandaloneFlightSimInstalls.FindEliteDangerous);
    public static string BindingsDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Frontier Developments", "Elite Dangerous", "Options", "Bindings");

    public static string FindProfile(string folder)
    {
        if (Directory.Exists(BindingsDirectory))
        {
            string? profile = ActiveProfiles().FirstOrDefault()
                ?? Directory.EnumerateFiles(BindingsDirectory, "*.binds").MaxBy(File.GetLastWriteTimeUtc);
            if (profile is not null)
                return profile;
        }

        return Path.Combine(SchemeDirectory(folder) ?? Path.Combine(folder, "ControlSchemes"), "GenericJoystick.binds");
    }

    public static IReadOnlyList<GameAction> LoadActions(string folder, string? sourceProfile = null)
    {
        string selected = sourceProfile ?? FindProfile(folder);
        string? schemes = SchemeDirectory(folder);
        if (sourceProfile is not null && !File.Exists(selected))
            throw new FileNotFoundException("Choose an existing Elite Dangerous .binds file.");

        string[] paths = new[] { selected }
            .Concat(sourceProfile is null ? ActiveProfiles() : [])
            .Concat(schemes is null ? [] : Directory.EnumerateFiles(schemes, "*.binds"))
            .Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0)
            throw new FileNotFoundException("Choose an Elite Dangerous installation or a saved .binds profile.");

        return paths.SelectMany(path => LoadProfile(path).Actions).DistinctBy(action => action.Id)
            .OrderBy(action => action.Category).ThenBy(action => action.Name).ToArray();
    }

    public static EliteDangerousProfile LoadProfile(string path) => EliteDangerousProfile.Load(path);

    private static IEnumerable<string> ActiveProfiles()
    {
        string start = Path.Combine(BindingsDirectory, "StartPreset.4.start");
        if (!File.Exists(start))
            yield break;
        var profiles = Directory.EnumerateFiles(BindingsDirectory, "*.binds")
            .GroupBy(PresetFileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.MaxBy(File.GetLastWriteTimeUtc)!, StringComparer.OrdinalIgnoreCase);
        // Odyssey stores the selected preset for each controls screen on its own line.
        foreach (string line in File.ReadLines(start))
            if (profiles.TryGetValue(line.Trim(), out string? active))
                yield return active;
    }

    public static string JoystickKey(string kind, int index, string? direction = null) => (kind, index) switch
    {
        ("button", >= 1 and <= 128) => $"Joy_{index}",
        ("axis", >= 1 and <= 8) => $"Joy_{AxisNames[index - 1]}",
        ("hat", >= 1 and <= 4) when direction?.ToLowerInvariant() is "up" or "right" or "down" or "left" =>
            $"Joy_POV{index}{char.ToUpperInvariant(direction[0])}{direction[1..].ToLowerInvariant()}",
        _ => throw new InvalidOperationException("Choose a joystick button, axis, or cardinal POV direction for Elite Dangerous.")
    };

    internal static string PresetFileName(string path)
    {
        string fileName = Path.GetFileNameWithoutExtension(path);
        int minorSeparator = fileName.LastIndexOf('.');
        int majorSeparator = minorSeparator > 0 ? fileName.LastIndexOf('.', minorSeparator - 1) : -1;
        return majorSeparator >= 0 && int.TryParse(fileName.AsSpan(majorSeparator + 1, minorSeparator - majorSeparator - 1), out _)
            && int.TryParse(fileName.AsSpan(minorSeparator + 1), out _) ? fileName[..majorSeparator] : fileName;
    }

    internal static GameAction Describe(XElement element)
    {
        string id = element.Name.LocalName;
        string section = id.StartsWith("Humanoid", StringComparison.Ordinal) ? "On Foot Controls"
            : id.Contains("Buggy", StringComparison.Ordinal) ? "SRV Controls"
            : id.StartsWith("MultiCrew", StringComparison.Ordinal) ? "Multicrew"
            : id.StartsWith("Exploration", StringComparison.Ordinal) ? "Exploration"
            : "Controls";
        return new GameAction("Controls", id, section, "", FlightActionNames.FromId(id), element.Element("Binding") is not null);
    }

    internal static string? SchemeDirectory(string folder)
    {
        string direct = Path.Combine(folder, "ControlSchemes");
        if (Directory.Exists(direct))
            return direct;
        string products = Path.Combine(folder, "Products");
        if (!Directory.Exists(products))
            return null;
        return Directory.EnumerateDirectories(products).OrderByDescending(path => path.Contains("odyssey", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.Combine(path, "ControlSchemes")).FirstOrDefault(Directory.Exists);
    }

    private static readonly string[] AxisNames = ["XAxis", "YAxis", "ZAxis", "RXAxis", "RYAxis", "RZAxis", "UAxis", "VAxis"];
}
