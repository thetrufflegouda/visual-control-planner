using System.IO;
using System.Text.Json;

namespace VisualControlsBinder.Games;

internal static class StandaloneFlightSimInstalls
{
    private const string WarThunderRegistry = @"Software\Gaijin\WarThunder";
    private const string Il2UninstallRegistry = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{66F649A9-0FA2-487E-BC0D-894BD7E89D5E}_is1";
    private static readonly string[] EliteProducts = ["elite-dangerous-odyssey-64", "elite-dangerous-64"];
    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string? FindWarThunder() => GameInstallations.RegistryPaths(WarThunderRegistry, "InstallPath")
        .Concat(GameInstallations.RegisteredInstallPaths(["War Thunder", "War Thunder Launcher"], []))
        .Append(Path.Combine(LocalAppData, "Games", "WarThunder"))
        .Select(InstallFolder).FirstOrDefault(folder => folder is not null && IsWarThunder(folder))
        ?? CommonInstall("WarThunder", IsWarThunder);

    public static bool IsWarThunder(string folder) => File.Exists(Path.Combine(folder, "gui.vromfs.bin"))
        && (File.Exists(Path.Combine(folder, "launcher.exe")) || File.Exists(Path.Combine(folder, "win64", "aces.exe")));

    public static string? FindIl2() => GameInstallations.RegistryPaths(Il2UninstallRegistry, "InstallLocation")
        .Concat(GameInstallations.RegisteredInstallPaths(
            ["IL-2 Sturmovik Great Battles", "IL-2 Sturmovik: Great Battles", "IL-2 Sturmovik Battle of Stalingrad",
                "IL-2 Sturmovik: Battle of Stalingrad"], ["Il-2.exe"]))
        .Select(Il2Folder).FirstOrDefault(folder => folder is not null)
        ?? CommonInstall(Path.Combine("1C Game Studios", "IL-2 Sturmovik Great Battles"), IsIl2)
        ?? CommonInstall("IL-2 Sturmovik Great Battles", IsIl2);

    public static bool IsIl2(string folder) => File.Exists(Path.Combine(folder, "bin", "game", "Il-2.exe"))
        && (File.Exists(Path.Combine(folder, "data", "Swf.gtp")) || File.Exists(Path.Combine(folder, "data", "Scripts.gtp")));

    public static string? FindEliteDangerous() => GameInstallations.RegisteredInstallPaths(
            ["Elite Dangerous", "Elite: Dangerous", "Elite Dangerous Launcher", "Elite: Dangerous Launcher"],
            ["EDLaunch.exe", "EliteDangerous64.exe"])
        .Concat(EpicInstallPaths("Elite Dangerous"))
        .Append(Path.Combine(LocalAppData, "Frontier_Developments", "Products"))
        .Select(EliteFolder).FirstOrDefault(folder => folder is not null)
        ?? EliteFolder(GameInstallations.StandaloneGame(Path.Combine("Frontier", "Products"), IsEliteDangerous));

    public static bool IsEliteDangerous(string folder) => EliteFolder(folder) is not null;

    public static string? FindSwg(string gameId)
    {
        (string[] Names, string[] Executables) registration = gameId switch
        {
            "swg-legends" => (["SWG Legends"], ["SWGLegendsLauncher.exe"]),
            "swg-restoration" => (["SWG Restoration", "SWG Restoration Uninstall"], ["SWG Restoration.exe"]),
            _ => ([], [])
        };
        return GameInstallations.RegisteredInstallPaths(registration.Names, registration.Executables)
            .Select(InstallFolder).FirstOrDefault(folder => folder is not null && IsSwg(folder, gameId));
    }

    public static bool IsSwg(string folder, string gameId)
    {
        string? launcher = gameId switch
        {
            "swg-legends" => "SWGLegendsLauncher.exe",
            "swg-restoration" => "SWG Restoration.exe",
            _ => null
        };
        return launcher is not null && File.Exists(Path.Combine(folder, launcher))
            && (File.Exists(Path.Combine(folder, "SwgClient_r.exe")) || File.Exists(Path.Combine(folder, "x64", "SwgClient_r.exe")));
    }

    public static IEnumerable<string> EpicInstallPaths(params string[] displayNames)
    {
        var dataFolders = GameInstallations.RegistryPaths(@"Software\Epic Games\EpicGamesLauncher", "AppDataPath")
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Epic", "EpicGamesLauncher", "Data"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string data in dataFolders)
            foreach (string manifest in EpicManifests(Path.Combine(data, "Manifests")))
                if (ReadEpicLocation(manifest, displayNames) is { } install)
                    yield return install;
    }

    private static string? CommonInstall(string relative, Func<string, bool> matches)
    {
        string folder = GameInstallations.StandaloneGame(relative, matches);
        return matches(folder) ? folder : null;
    }

    private static string? InstallFolder(string path) =>
        Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path) : path;

    private static string? Il2Folder(string path)
    {
        string? folder = InstallFolder(path);
        if (folder is null || IsIl2(folder))
            return folder;
        if (!Path.GetFileName(folder).Equals("game", StringComparison.OrdinalIgnoreCase)
            || Path.GetDirectoryName(folder) is not { } bin
            || !Path.GetFileName(bin).Equals("bin", StringComparison.OrdinalIgnoreCase))
            return null;
        return Path.GetDirectoryName(bin) is { } root && IsIl2(root) ? root : null;
    }

    private static string? EliteFolder(string path)
    {
        string? folder = InstallFolder(path);
        if (folder is null || HasEliteControls(folder))
            return folder;

        // The Frontier installer can put EDLaunch beside Products, while Steam and Epic put it above Products.
        if (Path.GetFileName(folder).Equals("EDLaunch", StringComparison.OrdinalIgnoreCase))
            folder = Path.GetDirectoryName(folder) ?? folder;
        string products = Path.GetFileName(folder).Equals("Products", StringComparison.OrdinalIgnoreCase)
            ? folder : Path.Combine(folder, "Products");
        return EliteProducts.Select(name => Path.Combine(products, name)).FirstOrDefault(HasEliteControls);
    }

    private static bool HasEliteControls(string folder) => File.Exists(Path.Combine(folder, "EliteDangerous64.exe"))
        && Directory.Exists(Path.Combine(folder, "ControlSchemes"));

    private static string[] EpicManifests(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetFiles(folder, "*.item") : []; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string? ReadEpicLocation(string path, string[] displayNames)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > 1024 * 1024)
                return null;
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("DisplayName", out var name) || name.ValueKind != JsonValueKind.String
                || !displayNames.Contains(name.GetString(), StringComparer.OrdinalIgnoreCase)
                || root.TryGetProperty("bIsIncompleteInstall", out var incomplete) && incomplete.ValueKind == JsonValueKind.True
                || !root.TryGetProperty("InstallLocation", out var location) || location.ValueKind != JsonValueKind.String)
                return null;
            return GameInstallations.NormalizePath(location.GetString());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
