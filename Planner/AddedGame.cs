using System.IO;
using System.Text.Json;

namespace VisualControlsBinder.Planner;

internal sealed record AddedGame(string Id, string Name, string Executable, string InstallFolder, string? BindingFile);

internal static class AddedGameStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly string Pathname = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VisualControlsBinder", "added-games.json");

    public static IReadOnlyList<AddedGame> Load()
    {
        if (!File.Exists(Pathname))
            return [];
        using var file = new FileStream(Pathname, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return JsonSerializer.Deserialize<AddedGame[]>(file, Json) ?? [];
    }

    public static IReadOnlyList<AddedGame> Save(AddedGame game)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Pathname)!);
        using var writeLock = new FileStream(Pathname + ".lock", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var games = Load().Where(existing => existing.Id != game.Id).Append(game).ToArray();
        string pending = Pathname + ".pending";
        using (var file = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, games, Json);
            file.Flush(flushToDisk: true);
        }
        File.Move(pending, Pathname, overwrite: true);
        return games;
    }
}
