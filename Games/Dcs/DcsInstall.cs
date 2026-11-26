using System.IO;

namespace VisualControlsBinder.Games;

internal static class DcsInstall
{
    private static readonly string[] Editions = ["DCS World", "DCS World OpenBeta"];

    public static string? FindStandalone()
    {
        var vendorPaths = Editions
            .SelectMany(edition => GameInstallations.RegistryPaths($@"Software\Eagle Dynamics\{edition}", "Path"));
        var paths = vendorPaths.Concat(GameInstallations.RegisteredInstallPaths(Editions, ["DCS.exe", "DCS_updater.exe"]));
        return paths.Select(InstallRoot).FirstOrDefault(folder => folder is not null);
    }

    public static bool IsInstallation(string folder) =>
        Directory.Exists(Path.Combine(folder, "Config", "Input")) &&
        (File.Exists(Path.Combine(folder, "bin", "DCS.exe")) || File.Exists(Path.Combine(folder, "bin-mt", "DCS.exe")));

    private static string? InstallRoot(string path)
    {
        if (IsInstallation(path))
            return path;

        string? folder = path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(path) : path;
        if (folder is null)
            return null;
        folder = Path.TrimEndingDirectorySeparator(folder);
        if (!Path.GetFileName(folder).Equals("bin", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetFileName(folder).Equals("bin-mt", StringComparison.OrdinalIgnoreCase))
            return null;

        return Path.GetDirectoryName(folder) is { } root && IsInstallation(root) ? root : null;
    }
}
