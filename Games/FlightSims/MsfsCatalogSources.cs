using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VisualControlsBinder.Games;

internal sealed partial class MsfsCatalogSources
{
    private const int MaxLocalizationBytes = 16 * 1024 * 1024;
    private const int MaxSettingsBytes = 1024 * 1024;
    private static readonly StringComparer IgnoreCase = StringComparer.OrdinalIgnoreCase;
    private readonly string[] _contentFolders;
    private readonly string[] _packageFolders;
    private readonly bool _is2024;

    public MsfsCatalogSources(string folder, bool is2024)
    {
        _is2024 = is2024;
        string selected = string.IsNullOrWhiteSpace(folder) ? MsfsGame.DefaultFolder(is2024) : Path.GetFullPath(folder);
        _contentFolders = ContentFolders(selected).Distinct(IgnoreCase).Where(Directory.Exists).ToArray();
        _packageFolders = PackageFolders(selected).Distinct(IgnoreCase).Where(Directory.Exists).ToArray();
    }

    public IEnumerable<string> StockProfiles()
    {
        foreach (string content in _contentFolders)
            foreach (string path in ProfileXml(Path.Combine(content, "Input"), 3))
                yield return path;

        foreach (string packages in _packageFolders)
        {
            string profiles = Path.Combine(packages, "asobo-input-profiles-pc", "InputProfiles");
            foreach (string path in ProfileXml(profiles, 4))
                yield return path;
        }
    }

    public Dictionary<string, string> ReadEnglishLabels()
    {
        var result = new Dictionary<string, string>(IgnoreCase);
        foreach (string packages in _packageFolders)
        {
            foreach (string name in new[] { "fs-base", "fs-base-aircraft-common" })
                ReadStrings(Path.Combine(packages, name, "en-US.locPak"), result);
        }
        return result;
    }

    private IEnumerable<string> ContentFolders(string selected)
    {
        yield return selected;
        yield return Path.Combine(selected, "Content");

        string storeName = _is2024 ? "Microsoft Flight Simulator 2024" : "Microsoft Flight Simulator";
        foreach (var drive in DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed))
            yield return Path.Combine(drive.RootDirectory.FullName, "XboxGames", storeName, "Content");

        string steamName = _is2024 ? "MSFS2024" : "MicrosoftFlightSimulator";
        foreach (string library in GameInstallations.SteamLibraries())
        {
            string installation = Path.Combine(library, "steamapps", "common", steamName);
            yield return installation;
            yield return Path.Combine(installation, "Content");
        }
    }

    private IEnumerable<string> PackageFolders(string selected)
    {
        foreach (string content in _contentFolders)
            yield return Path.Combine(content, "Packages");

        string settings = MsfsGame.DefaultFolder(_is2024);
        foreach (string root in new[] { selected, settings }.Distinct(IgnoreCase))
        {
            foreach (string location in new[] { root, Path.Combine(root, "LocalCache") })
            {
                if (ReadSmallFile(Path.Combine(location, "UserCfg.opt"), MaxSettingsBytes) is { } options)
                {
                    var match = InstalledPackagesPath().Match(options);
                    string configured = match.Groups["path"].Value;
                    if (Path.IsPathFullyQualified(configured))
                        foreach (string relative in OfficialLocations())
                            yield return Path.Combine(configured, relative);
                }
                foreach (string relative in OfficialLocations())
                    yield return Path.Combine(location, "Packages", relative);
            }
        }
    }

    private string[] OfficialLocations() => _is2024
        ? [Path.Combine("Official2024", "OneStore"), Path.Combine("Official2024", "Steam")]
        : [Path.Combine("Official", "OneStore"), Path.Combine("Official", "Steam")];

    private static IEnumerable<string> ProfileXml(string folder, int depth) =>
        MsfsGame.Files(folder, depth, "*.xml");

    private static void ReadStrings(string path, Dictionary<string, string> destination)
    {
        if (ReadSmallFile(path, MaxLocalizationBytes) is not { } text)
            return;
        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("LocalisationPackage", out var package)
                || !package.TryGetProperty("Strings", out var strings) || strings.ValueKind != JsonValueKind.Object)
                return;
            foreach (var entry in strings.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.String && entry.Value.GetString()?.Trim() is { Length: > 0 } label)
                    destination.TryAdd(entry.Name, label);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { }
    }

    private static string? ReadSmallFile(string path, int maximumBytes)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length <= maximumBytes ? File.ReadAllText(path) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    [GeneratedRegex("^InstalledPackagesPath[ \\t]+\"(?<path>[^\"\\r\\n]+)\"", RegexOptions.Multiline)]
    private static partial Regex InstalledPackagesPath();
}
