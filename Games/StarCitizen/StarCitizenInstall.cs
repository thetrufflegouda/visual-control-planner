using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VisualControlsBinder.Games;

internal static partial class StarCitizenInstall
{
    private const int SettingsLimit = 256 * 1024;
    private const int LogLimit = 2 * 1024 * 1024;
    private static readonly string[] GameFolders = ["StarCitizen", "Star Citizen"];
    private static readonly string[] ExecutableNames = ["StarCitizen.exe", "StarCitizen_Launcher.exe", "RSI Launcher.exe"];
    private static readonly string[] SettingsScopes =
        ["settings", "gameSettings", "games", "SC", "installations", "releases", "release", "libraries", "library", "LIVE", "gameInformation"];
    private static readonly string[] SettingsPaths = ["libraryFolder", "installDirectory", "installPath"];

    public static string Find() => FindInLauncherData(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
        ?? GameInstallations.RegisteredInstallPaths(["Star Citizen", "RSI Launcher"], ExecutableNames)
            .Select(ResolveLiveDirectory).FirstOrDefault(folder => folder is not null)
        ?? GameInstallations.StandaloneGame(Path.Combine("Roberts Space Industries", "StarCitizen", "LIVE"), HasGameData);

    public static string? ResolveLiveDirectory(string? path)
    {
        if (GameInstallations.NormalizePath(path) is not { } selected)
            return null;
        string name = Path.GetFileName(selected);
        if (name.Equals("Data.p4k", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            if (Path.GetDirectoryName(selected) is not { } directory)
                return null;
            selected = directory;
        }

        var nearby = new List<string> { selected };
        string selectedName = Path.GetFileName(selected);
        if ((selectedName.StartsWith("Bin64", StringComparison.OrdinalIgnoreCase) ||
            selectedName.Equals("RSI Launcher", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("RSI Launcher.exe", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Uninstall RSI Launcher.exe", StringComparison.OrdinalIgnoreCase)) &&
            Path.GetDirectoryName(selected) is { } parent)
            nearby.Add(parent);
        return nearby.SelectMany(folder => new[] { folder, Path.Combine(folder, "LIVE") }
            .Concat(GameFolders.Select(product => Path.Combine(folder, product, "LIVE"))))
            .FirstOrDefault(HasGameData);
    }

    private static bool HasGameData(string folder) => File.Exists(Path.Combine(folder, "Data.p4k"));

    private static string? FindInLauncherData(string roaming)
    {
        if (string.IsNullOrEmpty(roaming))
            return null;
        string modern = Path.Combine(roaming, "rsilauncher");
        string legacy = Path.Combine(roaming, "RSI Launcher");
        foreach (string folder in new[] { modern, legacy })
        {
            foreach (string name in new[] { "launcher store.json", "config.json", "settings.json" })
            {
                using var settings = Parse(ReadBounded(Path.Combine(folder, name), SettingsLimit, tail: false));
                if (settings is not null &&
                    SettingsCandidates(settings.RootElement).Select(ResolveLiveDirectory).FirstOrDefault(path => path is not null) is { } install)
                    return install;
            }
        }

        // Current launcher releases may keep settings in an encoded store. The public launcher log
        // provides the LIVE path on launch, install, and verification without opening any login data.
        foreach (string path in new[] { Path.Combine(modern, "logs", "log.log"), Path.Combine(modern, "log.log"), Path.Combine(legacy, "log.log") })
        {
            string[] lines = ReadBounded(path, LogLimit, tail: true).Split('\n');
            foreach (string line in lines.Reverse())
            {
                using var log = Parse(line.Trim().TrimEnd(','));
                if (log?.RootElement.ValueKind != JsonValueKind.Object)
                    continue;
                foreach (var entry in log.RootElement.EnumerateObject())
                    if (entry.Name.Trim() is "[main][info]" or "info" &&
                        ResolveLiveDirectory(LogCandidate(entry.Value)) is { } install)
                        return install;
            }
        }
        return null;
    }

    private static IEnumerable<string> SettingsCandidates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray().SelectMany(SettingsCandidates);
        if (value.ValueKind != JsonValueKind.Object ||
            (Text(value, "channelId") is { } channel && !channel.Equals("LIVE", StringComparison.OrdinalIgnoreCase)) ||
            (Text(value, "gameId") is { } game && !game.Equals("SC", StringComparison.OrdinalIgnoreCase)))
            return [];
        return SettingsPaths.Select(key => Text(value, key)).OfType<string>()
            .Concat(SettingsScopes.Where(key => value.TryGetProperty(key, out _))
                .SelectMany(key => SettingsCandidates(value.GetProperty(key))));
    }

    private static string? LogCandidate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string message = value.GetString() ?? "";
            var launch = LiveLaunch().Match(message);
            if (launch.Success)
                return launch.Groups["path"].Value;
            var patch = LiveInstall().Match(message);
            return patch.Success ? patch.Groups["path"].Value : null;
        }
        if (value.ValueKind == JsonValueKind.Object && Text(value, "event") == "INSTALLER@INSTALL" &&
            value.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
            data.TryGetProperty("gameInformation", out var game))
            return SettingsCandidates(game).FirstOrDefault();
        return null;
    }

    private static string? Text(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static string ReadBounded(string path, int limit, bool tail)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = stream.Length;
            if (!tail && length > limit)
                return "";
            bool partial = length > limit;
            stream.Position = partial ? length - limit : 0;
            var bytes = new byte[(int)Math.Min(length, limit)];
            int read = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            string text = System.Text.Encoding.UTF8.GetString(bytes, 0, read).TrimStart('\uFEFF');
            if (!partial)
                return text;
            int firstNewline = text.IndexOf('\n');
            return firstNewline < 0 ? "" : text[(firstNewline + 1)..];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static JsonDocument? Parse(string text)
    {
        text = text.TrimStart();
        if (!text.StartsWith('{'))
            return null;
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex(@"^(?:\[Launcher::launch\] )?Launching Star Citizen LIVE from \((?<path>.+)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex LiveLaunch();

    [GeneratedRegex(@"^(?:\[(?:Pipeline|Installer|PatcherPhase|ComputeSizePhase)\] )?(?:Installing Star Citizen LIVE \S+ at |Delta update (?:completed|applied) \(SC LIVE [^)]+\) in |Computing required space for \(SC LIVE\) in )(?<path>.+?)(?: \((?:type|DP):.*\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex LiveInstall();
}
