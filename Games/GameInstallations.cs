using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VisualControlsBinder.Games;

internal static partial class GameInstallations
{
    private static string ProgramFiles => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
    private static string ProgramFilesX86 => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    public static string SteamFolder()
    {
        using var key = OperatingSystem.IsWindows() ? Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam") : null;
        return key?.GetValue("SteamPath") is string configured && Path.IsPathFullyQualified(configured)
            ? configured : Path.Combine(ProgramFilesX86, "Steam");
    }

    public static IEnumerable<string> SteamLibraries()
    {
        string steam = SteamFolder();
        yield return steam;
        var manifest = new FileInfo(Path.Combine(steam, "steamapps", "libraryfolders.vdf"));
        string text;
        try
        {
            text = manifest.Exists && manifest.Length <= 1024 * 1024 ? File.ReadAllText(manifest.FullName) : "";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { yield break; }
        foreach (Match entry in SteamLibraryPath().Matches(text).Take(64))
        {
            var root = entry.Groups["path"].Value.Replace("\\\\", "\\");
            if (Path.IsPathFullyQualified(root) && !root.Equals(steam, StringComparison.OrdinalIgnoreCase))
                yield return root;
        }
    }

    public static string SteamGame(string folder) => SteamGame(folder, Directory.Exists);

    public static string SteamGame(string folder, Func<string, bool> matches, Func<string?>? findStandalone = null) =>
        SteamLibraries().Select(root => Path.Combine(root, "steamapps", "common", folder))
            .FirstOrDefault(matches) ?? findStandalone?.Invoke() ?? Path.Combine(SteamFolder(), "steamapps", "common", folder);

    public static string StandaloneGame(string relativePath, Func<string, bool> matches)
    {
        var roots = new[] { ProgramFiles, ProgramFilesX86 }
            .Concat(DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed)
                .Select(drive => drive.RootDirectory.FullName));
        return roots.Select(root => Path.Combine(root, relativePath)).FirstOrDefault(matches)
            ?? Path.Combine(ProgramFiles, relativePath);
    }

    [GeneratedRegex("^\\s*\"path\"[ \\t]+\"(?<path>[^\"\\r\\n]+)\"", RegexOptions.Multiline)]
    private static partial Regex SteamLibraryPath();
}
