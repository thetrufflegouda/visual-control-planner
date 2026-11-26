using System.IO;

namespace VisualControlsBinder.Games;

public static class Il2Game
{
    private const string ActionMap = "Key Mapping";
    private static readonly IReadOnlyDictionary<string, string> MenuCategories = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["service"] = "Service",
        ["cameracontrols"] = "Camera controls",
        ["pilotheadcontrol"] = "Pilot head control",
        ["planecontrols"] = "Plane controls",
        ["planeenginecontrols"] = "Engine controls",
        ["weaponscontrols"] = "Weapons controls",
        ["planeflightleadercommands"] = "Flight leader commands",
        ["tankcontrols"] = "Tank controls"
    };

    public static string DefaultInstallDirectory { get; } = GameInstallations.SteamGame("IL-2 Sturmovik Battle of Stalingrad",
        StandaloneFlightSimInstalls.IsIl2, StandaloneFlightSimInstalls.FindIl2);
    public static string BindingsDirectory(string installDirectory) => Path.Combine(installDirectory, "data", "input");

    public static string FindProfile(string folder)
    {
        string directory = BindingsDirectory(folder);
        string global = Path.Combine(directory, "global.actions");
        return File.Exists(global) ? global : Path.Combine(directory, "current.actions");
    }

    public static IReadOnlyList<GameAction> LoadActions(string folder, string? sourceProfile = null)
    {
        string data = Path.Combine(folder, "data");
        string input = Path.Combine(data, "input");
        var saved = new[] { sourceProfile, Path.Combine(input, "global.actions"), Path.Combine(input, "current.actions") }
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).SelectMany(ReadRows).ToArray();
        var defaults = Il2Archive.ReadText(Path.Combine(data, "Scripts.gtp"), "/input/default.actions");
        var definitions = defaults is null ? saved : saved.Concat(ReadRows(new StringReader(defaults))).ToArray();
        var rows = definitions.ToLookup(row => row.Id, StringComparer.Ordinal);

        var menu = Il2Archive.ReadText(Path.Combine(data, "Swf.gtp"), "/swf/il2/input/actions.txt");
        var actions = menu is null ? [] : ReadMenu(menu, rows).ToArray();
        if (actions.Length > 0)
            return actions;
        if (rows.Count == 0)
            throw new FileNotFoundException("Choose the IL-2 Great Battles installation or one of its saved .actions files.");

        return rows.Select(group =>
        {
            string id = group.Key;
            bool isAxis = group.Any(row => row.Input.Contains("_axis_", StringComparison.OrdinalIgnoreCase)
                || row.Input.Contains('/'));
            return new GameAction(ActionMap, id, Section(id), "", ActionName(id, group), isAxis);
        }).OrderBy(action => action.Category).ThenBy(action => action.Name).ToArray();
    }

    public static IReadOnlyList<GameBinding> LoadBindings(string path) =>
        ReadRows(path).Where(row => row.Input.Length > 0)
            .Select(row => new GameBinding(ActionMap, row.Id, row.Input)).ToArray();

    internal static bool IsActionFile(string path) => ReadRows(path).Any();

    private static IEnumerable<ActionRow> ReadRows(string path)
    {
        using var reader = File.OpenText(path);
        foreach (var row in ReadRows(reader))
            yield return row;
    }

    private static IEnumerable<ActionRow> ReadRows(TextReader reader)
    {
        bool hasHeader = false;
        while (reader.ReadLine() is { } line)
        {
            if (line.TrimStart().StartsWith("&actions=action,command,invert|", StringComparison.Ordinal))
            {
                hasHeader = true;
                continue;
            }
            int end = line.IndexOf('|');
            if (!hasHeader || end < 0)
                continue;
            string[] fields = line[..end].Split(',', StringSplitOptions.TrimEntries);
            if (fields.Length != 3 || fields[0].Length == 0 || fields[2] is not ("0" or "1"))
                continue;
            string comment = line[(end + 1)..].Trim();
            yield return new ActionRow(fields[0], fields[1],
                comment.StartsWith("//", StringComparison.Ordinal) ? comment[2..].Trim() : "");
        }
    }

    private static IEnumerable<GameAction> ReadMenu(string menu, ILookup<string, ActionRow> rows)
    {
        using var reader = new StringReader(menu);
        string? category = null;
        int axisColumn = -1;
        int disabledColumn = -1;
        var seen = new HashSet<(string Category, string Id)>();
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.Trim();
            if (line.StartsWith('&'))
            {
                int separator = line.IndexOf('=');
                category = separator > 1 ? MenuCategories.GetValueOrDefault(line[1..separator]) : null;
                var schema = category is null ? [] : line[(separator + 1)..].TrimEnd('|').Split(',');
                axisColumn = Array.IndexOf(schema, "axisType");
                disabledColumn = Array.IndexOf(schema, "disabled");
                continue;
            }
            if (category is null || !line.EndsWith('|') || line.StartsWith("//", StringComparison.Ordinal))
                continue;
            string[] cells = line[..^1].Split(',', StringSplitOptions.TrimEntries);
            string id = cells[0];
            if (id.Length == 0 || disabledColumn >= 0 && disabledColumn < cells.Length && cells[disabledColumn] == "1"
                || !seen.Add((category, id)))
                continue;
            bool axis = axisColumn >= 0 && axisColumn < cells.Length
                && int.TryParse(cells[axisColumn], out int type) && type > 0;
            yield return new GameAction(ActionMap, id, category, "", ActionName(id, rows[id]), axis);
        }
    }

    private static string ActionName(string id, IEnumerable<ActionRow> rows) =>
        rows.Select(row => row.Comment).FirstOrDefault(comment => comment.Length > 0 &&
            !(comment.StartsWith("inp", StringComparison.OrdinalIgnoreCase) && !comment.Contains(' ')))
        ?? FlightActionNames.FromId(id.StartsWith("rpc_", StringComparison.Ordinal) ? id[4..] : id);

    // A standalone .actions file has no menu categories. Retain a readable grouping when no game archive is available.
    private static string Section(string id) => id switch
    {
        _ when id.StartsWith("cam_", StringComparison.Ordinal) => "Camera",
        _ when id.StartsWith("bc_head_", StringComparison.Ordinal) => "Pilot Head",
        _ when id.StartsWith("engine_", StringComparison.Ordinal) || id.StartsWith("rpc_engine_", StringComparison.Ordinal)
            || id.StartsWith("select_engine_", StringComparison.Ordinal) => "Engine Controls",
        _ when id.StartsWith("rpc_", StringComparison.Ordinal) => "Plane Controls",
        _ when id.StartsWith("wpn_", StringComparison.Ordinal) => "Weapon Controls",
        _ when id.StartsWith("vc_", StringComparison.Ordinal) => "Vehicle Controls",
        _ => "Key Mapping"
    };

    private sealed record ActionRow(string Id, string Input, string Comment);
}
