using System.IO;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace VisualControlsBinder.Games;

public static partial class WarThunderGame
{
    public static string DefaultFolder { get; } = GameInstallations.SteamGame("War Thunder",
        StandaloneFlightSimInstalls.IsWarThunder, StandaloneFlightSimInstalls.FindWarThunder);
    public static string DefaultInstallDirectory => DefaultFolder;

    private const string MenuPath = "scripts/controls/shortcutslist/";
    private const string ControlsCsv = "lang/controls.csv";
    internal const long ProfileSizeLimit = 16 * 1024 * 1024;
    internal static string SavesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "WarThunder", "Saves");

    public static string? FindProfile(string folder)
    {
        var exports = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.blk").Where(WarThunderProfile.IsControlExport).Take(2).ToArray()
            : [];
        return exports.Length == 1 ? exports[0] : null;
    }

    public static string? FindActiveProfile() => FindActivePair(SavesDirectory)?.AccountPath;

    internal static ActiveWarThunderProfile? FindActivePair(string savesDirectory)
    {
        string mirror = Path.Combine(savesDirectory, "last", "production", "machine.blk");
        try
        {
            var info = new FileInfo(mirror);
            if (!info.Exists || info.Length > ProfileSizeLimit)
                return null;
            byte[] snapshot = File.ReadAllBytes(mirror);
            var matches = Directory.EnumerateDirectories(savesDirectory).Where(directory =>
                    Path.GetFileName(directory) is { Length: > 0 and <= 20 } account && account.All(char.IsAsciiDigit))
                .Select(directory => new FileInfo(Path.Combine(directory, "production", "machine.blk")))
                .Where(candidate => candidate.Exists && candidate.Length == snapshot.Length &&
                    File.ReadAllBytes(candidate.FullName).AsSpan().SequenceEqual(snapshot))
                .Take(2).ToArray();
            if (matches.Length != 1 || !WarThunderProfile.Load(matches[0].FullName).IsMachineProfile)
                return null;
            return new ActiveWarThunderProfile(matches[0].FullName, mirror, snapshot);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    internal sealed record ActiveWarThunderProfile(string AccountPath, string MirrorPath, byte[] Snapshot);

    public static IReadOnlyList<GameAction> LoadActions(string folder, string? profile = null)
    {
        var labels = ReadLabels(folder);
        var actions = new Dictionary<(string Map, string Id), GameAction>();
        var guiPath = Path.Combine(folder, "gui.vromfs.bin");
        if (File.Exists(guiPath))
        {
            var sources = WarThunderArchive.ReadTexts(guiPath, name => name.StartsWith(MenuPath, StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith(".nut", StringComparison.OrdinalIgnoreCase));
            var modules = sources.TryGetValue(MenuPath + "updateshortcutsmoduleslist.nut", out string? menu)
                ? ModulePath().Matches(menu).Select(match => MenuPath + match.Groups["name"].Value)
                : sources.Keys.Where(name => name.StartsWith(MenuPath + "shortcutsgroup", StringComparison.OrdinalIgnoreCase) &&
                    !name.Equals(MenuPath + "shortcutsgroupaxis.nut", StringComparison.OrdinalIgnoreCase));
            foreach (var path in modules.Distinct(StringComparer.OrdinalIgnoreCase))
                if (sources.TryGetValue(path, out string? script))
                    AddMenuActions(path, script, labels, actions);
        }

        if (profile is not null)
        {
            var saved = WarThunderProfile.Load(profile);
            foreach (var (map, id) in saved.ActionIds)
                actions.TryAdd((map, id), new GameAction(map, id, "Saved controls", "", ActionName(id, map == "axes", labels), map == "axes"));
        }

        if (actions.Count == 0)
            throw new InvalidDataException("Choose the War Thunder installation or an exported Controls .blk profile.");
        return actions.Values.ToArray();
    }

    private static Dictionary<string, string> ReadLabels(string folder)
    {
        var loose = Path.Combine(folder, "lang", "controls.csv");
        var archive = Path.Combine(folder, "lang.vromfs.bin");
        string? text = File.Exists(loose) ? File.ReadAllText(loose) : File.Exists(archive)
            ? WarThunderArchive.ReadTexts(archive, name => name.Equals(ControlsCsv, StringComparison.OrdinalIgnoreCase)).GetValueOrDefault(ControlsCsv)
            : null;
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        if (text is null)
            return labels;

        using var csv = new TextFieldParser(new StringReader(text));
        csv.SetDelimiters(";");
        var header = csv.ReadFields() ?? [];
        var english = Array.IndexOf(header, "<English>");
        if (english < 1)
            return labels;
        while (!csv.EndOfData)
        {
            var fields = csv.ReadFields();
            if (fields is not null && fields.Length > english && fields[english].Length != 0)
                labels.TryAdd(fields[0], fields[english]);
        }
        return labels;
    }

    private static void AddMenuActions(string path, string script, Dictionary<string, string> labels,
        Dictionary<(string Map, string Id), GameAction> actions)
    {
        string category = path.EndsWith("shortcutsgroupspecial.nut", StringComparison.OrdinalIgnoreCase) ? "Infantry" : "Common";
        string section = "";
        foreach (Match row in MenuEntry().Matches(script))
        {
            string body = row.Groups["body"].Value;
            var idField = MenuId().Match(body);
            if (!idField.Success)
                continue;

            string id = idField.Groups["id"].Value;
            var type = ControlType().Match(body).Groups["type"].Value;
            if (type == "HEADER")
            {
                category = labels.GetValueOrDefault("hotkeys/" + id, category);
                section = "";
                continue;
            }
            if (type == "SECTION")
            {
                section = ActionName(id, false, labels);
                continue;
            }
            if (type is not ("" or "SHORTCUT" or "AXIS"))
                continue;

            bool analog = type == "AXIS";
            string map = analog ? "axes" : "hotkeys";
            actions.TryAdd((map, id), new GameAction(map, id, category, section, ActionName(id, analog, labels), analog));
        }

        if (path.EndsWith("shortcutsgroupvoice.nut", StringComparison.OrdinalIgnoreCase))
            AddRadioShortcuts(script, labels, category, section, actions);
    }

    private static void AddRadioShortcuts(string script, Dictionary<string, string> labels, string category, string section,
        Dictionary<(string Map, string Id), GameAction> actions)
    {
        // The installed voice module constructs these rows in two loops. The localization file lists each concrete ID.
        foreach (var pattern in new[] { "ID_VOICE_MESSAGE_", "ID_FAST_VOICE_MESSAGE_" })
        {
            if (!script.Contains($"$\"{pattern}{{index}}\"", StringComparison.Ordinal))
                continue;
            string prefix = "hotkeys/" + pattern;
            foreach (var (key, name) in labels)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal) || !int.TryParse(key.AsSpan(prefix.Length), out int number)
                    || number <= 0)
                    continue;
                string id = key["hotkeys/".Length..];
                actions.TryAdd(("hotkeys", id), new GameAction("hotkeys", id, category, section, name));
            }
        }
    }

    private static string ActionName(string id, bool analog, Dictionary<string, string> labels) =>
        labels.GetValueOrDefault((analog ? "controls/" : "hotkeys/") + id)
        ?? labels.GetValueOrDefault((analog ? "hotkeys/" : "controls/") + id)
        ?? FlightActionNames.FromId(id.StartsWith("ID_", StringComparison.Ordinal) ? id[3..].ToLowerInvariant() : id);

    [GeneratedRegex("%scripts/controls/shortcutsList/(?<name>shortcuts(?:Group[a-z]+|ListHelpersMode)\\.nut)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ModulePath();

    [GeneratedRegex(@"^  \{[ \t]*\r*\n(?<body>.*?)^  \}", RegexOptions.Multiline | RegexOptions.Singleline)]
    private static partial Regex MenuEntry();

    [GeneratedRegex("^    id\\s*=\\s*\"(?<id>[A-Za-z0-9_]+)\"", RegexOptions.Multiline)]
    private static partial Regex MenuId();

    [GeneratedRegex(@"^    type\s*=\s*CONTROL_TYPE\.(?<type>[A-Z_]+)", RegexOptions.Multiline)]
    private static partial Regex ControlType();
}
