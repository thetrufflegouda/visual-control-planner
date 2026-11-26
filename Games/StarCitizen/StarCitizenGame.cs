using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VisualControlsBinder.Games;

public static partial class StarCitizenGame
{
    private const string CatalogMember = "Data/Libs/Config/defaultProfile.xml";
    private const string EnglishMember = "Data/Localization/english/global.ini";
    private static readonly HashSet<string> Axes = new(StringComparer.OrdinalIgnoreCase)
    {
        "x", "y", "z", "rotx", "roty", "rotz", "slider1", "slider2"
    };

    public static string DefaultInstallDirectory { get; } = StarCitizenInstall.Find();

    public static string MappingsDirectory(string installDirectory) =>
        Path.Combine(installDirectory, "USER", "client", "0", "Controls", "Mappings");

    public static string? FindProfile(string installDirectory)
    {
        string mappings = MappingsDirectory(installDirectory);
        if (!Directory.Exists(mappings))
            return null;
        var candidates = Directory.EnumerateFiles(mappings, "layout_*_exported.xml").Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    internal static string? FindVJoyOnlyProfile(string installDirectory)
    {
        string mappings = MappingsDirectory(installDirectory);
        if (!Directory.Exists(mappings))
            return null;
        return Directory.EnumerateFiles(mappings, "layout_*_exported.xml").Where(IsVJoyOnly).Take(2).ToArray()
            is [var only] ? only : null;

        static bool IsVJoyOnly(string path)
        {
            try { return LoadProfile(path).IsVJoyOnly; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return false;
            }
        }
    }

    public static IReadOnlyList<GameAction> LoadActions(string installDirectory)
    {
        using var archive = new P4kArchive(Path.Combine(installDirectory, "Data.p4k"), CatalogMember, EnglishMember);
        var profile = CryXml.Read(archive.Read(CatalogMember));
        if (profile.Name.LocalName != "profile")
            throw new InvalidDataException("Invalid Star Citizen control catalog.");

        var localized = ReadLocalization(archive.Read(EnglishMember));
        var actions = new List<GameAction>();
        foreach (var map in profile.Elements("actionmap"))
        {
            if (map.Attribute("name")?.Value is not { Length: > 0 } mapId)
                continue;

            string section = ResolveLabel(map.Attribute("UILabel")?.Value, mapId, localized);
            string category = ResolveLabel(map.Attribute("UICategory")?.Value, "", localized);
            foreach (var action in map.Elements("action"))
            {
                string? label = action.Attribute("UILabel")?.Value;
                if (string.IsNullOrWhiteSpace(label) || action.Attribute("name")?.Value is not { Length: > 0 } id)
                    continue;
                var joystick = JoystickInputs(action);
                if (joystick.Length == 0)
                    continue;

                string subgroup = ResolveLabel(action.Attribute("UICategory")?.Value, "", localized);
                string actionSection = string.IsNullOrEmpty(subgroup) ? section : $"{section} / {subgroup}";
                actions.Add(new GameAction(mapId, id, category, actionSection,
                    ResolveLabel(label, id, localized), IsAxis(action, id, joystick)));
            }
        }
        if (actions.Count == 0)
            throw new InvalidDataException("No joystick controls were found in the Star Citizen catalog.");
        return actions.AsReadOnly();
    }

    public static StarCitizenProfile LoadProfile(string path) => new(path);

    public static bool IsJoystickAxis(string input) => Axes.Contains(input);

    internal static bool IsJoystickInput(string input) => JoystickInputPattern().IsMatch(input);

    private static string[] JoystickInputs(XElement action)
    {
        var inputs = action.Elements("joystick").Select(input => input.Attribute("input")?.Value ?? "");
        if (action.Attribute("joystick") is { } value)
            inputs = inputs.Prepend(value.Value);
        return inputs.ToArray();
    }

    private static bool IsAxis(XElement action, string id, string[] joystick)
    {
        if (joystick.Any(input => IsJoystickAxis(input.Trim())))
            return true;
        if (joystick.Any(input => !string.IsNullOrWhiteSpace(input)))
            return false;

        string gamepad = action.Attribute("gamepad")?.Value ?? "";
        if (GamepadAxisPattern().IsMatch(gamepad))
            return true;
        if (id == "v_mining_throttle" || id.EndsWith("_abs", StringComparison.OrdinalIgnoreCase) ||
            id.EndsWith("_rel", StringComparison.OrdinalIgnoreCase))
            return true;
        if (action.Attribute("optionGroup") is null)
            return false;

        // Some bindable analog inputs intentionally have blank defaults on all devices.
        // The digital alternatives carry activation rules even while their input is blank.
        return action.Attribute("onPress") is null && action.Attribute("onRelease") is null &&
            action.Attribute("activationMode") is null && action.Attribute("ActivationMode") is null;
    }

    private static Dictionary<string, string> ReadLocalization(byte[] bytes)
    {
        var localized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        while (reader.ReadLine() is { } line)
        {
            int split = line.IndexOf('=');
            if (split <= 0)
                continue;
            string key = line[..split].Trim();
            if (key.StartsWith("ui_", StringComparison.OrdinalIgnoreCase))
                localized[key] = line[(split + 1)..].Trim();
        }
        return localized;
    }

    private static string ResolveLabel(string? label, string fallback, Dictionary<string, string> localized)
    {
        if (string.IsNullOrWhiteSpace(label))
            return Humanize(fallback);
        if (!label.StartsWith('@'))
            return label;
        if (localized.TryGetValue(label[1..], out var value) && !string.IsNullOrWhiteSpace(value))
            return value;
        return Humanize(fallback);
    }

    private static string Humanize(string id)
    {
        if (id.StartsWith("v_", StringComparison.OrdinalIgnoreCase))
            id = id[2..];
        if (id.Length == 0)
            return "";
        string words = CamelCaseBoundary().Replace(id, " ").Replace('_', ' ');
        return char.ToUpperInvariant(words[0]) + words[1..];
    }

    [GeneratedRegex("(?i)^js[1-9][0-9]*_(?:button[1-9][0-9]*|hat[1-9][0-9]*_(?:up|right|down|left)|[xyz]|rot[xyz]|slider[12])$")]
    private static partial Regex JoystickInputPattern();

    [GeneratedRegex("(?i)(?:^|\\+)(?:thumb[lr][xy]|trigger[lr])$")]
    private static partial Regex GamepadAxisPattern();

    [GeneratedRegex("(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelCaseBoundary();
}
