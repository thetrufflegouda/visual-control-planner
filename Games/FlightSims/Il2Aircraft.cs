using System.IO;
using Microsoft.VisualBasic.FileIO;

namespace VisualControlsBinder.Games;

public sealed record Il2Aircraft(string Id, string Name, string? ProfileId = null, string? ProfilePath = null,
    bool Available = true)
{
    public static Il2Aircraft Global { get; } = new("", "Global bindings");
    public bool IsGlobal => Id.Length == 0;
}

public static class Il2AircraftCatalog
{
    private const string PlanesPath = "/swf/il2/worldobjects/planes/";
    private const string Info = "info.txt";
    private const string English = "info.locale=eng.txt";

    public static IReadOnlyList<Il2Aircraft> Load(string folder)
    {
        var data = Path.Combine(folder, "data");
        var catalog = ReadPlanes(data);
        var profiles = ReadProfiles(Path.Combine(data, "input"));
        var result = catalog.Select(plane =>
        {
            if (profiles.TryGetValue(plane.Id, out var profile))
                return new Il2Aircraft(plane.Id, plane.Name, profile.Id, profile.Path);
            return new Il2Aircraft(plane.Id, plane.Name);
        });
        return [Il2Aircraft.Global, .. result.OrderBy(plane => plane.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    internal static string? AssignedProfilePath(string folder, string aircraftId) =>
        ReadProfiles(Il2Game.BindingsDirectory(folder)).GetValueOrDefault(aircraftId)?.Path;

    private static IReadOnlyList<Plane> ReadPlanes(string data)
    {
        string archive = Path.Combine(data, "Swf.gtp");
        string views = Path.Combine(data, "LuaScripts", "snapviews", "defaults");
        if (!File.Exists(archive))
            return [];

        using var file = new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var entries = Il2Archive.ReadEntries(file, path => path.StartsWith(PlanesPath, StringComparison.Ordinal)
            && (path.EndsWith("/" + Info, StringComparison.Ordinal) || path.EndsWith("/" + English, StringComparison.Ordinal)));
        var result = new List<Plane>();
        foreach (var (path, item) in entries)
        {
            if (!path.EndsWith('/' + Info, StringComparison.Ordinal))
                continue;
            string id = path[PlanesPath.Length..^(Info.Length + 1)];
            if (!IsPlaneId(id) || !File.Exists(Path.Combine(views, id + ".svc")))
                continue;
            string? info = Il2Archive.ReadText(file, item);
            string? gameId = Field(info, "id")?.Replace('\\', '/');
            if (Field(info, "isPlayerControlled") != "1" || gameId is null ||
                Field(info, "isVirtual") == "1" || !gameId.Equals($"luascripts/worldobjects/planes/{id}.txt", StringComparison.OrdinalIgnoreCase))
                continue;
            string localePath = $"{PlanesPath}{id}/{English}";
            string? name = entries.TryGetValue(localePath, out var localized) ? Field(Il2Archive.ReadText(file, localized), "name") : null;
            result.Add(new Plane(id, name ?? id));
        }
        return result;
    }

    private static string? Field(string? text, string name)
    {
        if (text is null)
            return null;
        string key = $"&{name}=";
        using var lines = new StringReader(text.TrimStart('\uFEFF'));
        while (lines.ReadLine() is { } line)
            if (line.StartsWith(key, StringComparison.Ordinal) && line[key.Length..].Trim() is { Length: > 0 } value)
                return value;
        return null;
    }

    private static bool IsPlaneId(string id) => id.Length > 0 && id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');

    private static Dictionary<string, AssignedProfile> ReadProfiles(string input)
    {
        string file = Path.Combine(input, "profiling.txt");
        string custom = Path.Combine(input, "custom");
        var result = new Dictionary<string, AssignedProfile>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(file) || !Directory.Exists(custom) || new FileInfo(file).Length > 1024 * 1024)
            return result;

        var paths = Directory.EnumerateFiles(custom, "*.actions").ToLookup(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase);
        using var parser = new TextFieldParser(file) { Delimiters = [","], HasFieldsEnclosedInQuotes = true };
        if (parser.ReadFields() is not ["actionsProfileId", "pitchesProfileId", "planeId"])
            return result;

        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (!parser.EndOfData)
        {
            string[]? fields;
            try { fields = parser.ReadFields(); }
            catch (MalformedLineException) { continue; }
            if (fields is not [{ Length: > 0 } profileId, _, { Length: > 0 } gameId])
                continue;

            gameId = gameId.Replace('\\', '/');
            const string prefix = "luascripts/worldobjects/planes/";
            if (gameId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && gameId.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                gameId = gameId[prefix.Length..^4];
            if (!IsPlaneId(gameId))
                continue;
            var matches = paths[profileId].Take(2).ToArray();
            var profile = new AssignedProfile(profileId, matches.Length == 1 && Il2Game.IsActionFile(matches[0]) ? matches[0] : null);
            if (!result.TryAdd(gameId, profile) && result[gameId] != profile)
                ambiguous.Add(gameId);
        }
        foreach (var plane in ambiguous)
            result[plane] = new AssignedProfile("Conflicting profiles", null);
        return result;
    }

    private sealed record Plane(string Id, string Name);
    private sealed record AssignedProfile(string Id, string? Path);
}
