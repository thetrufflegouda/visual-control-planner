using System.IO;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace VisualControlsBinder.Games;

internal static partial class GameInstallations
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppPathsKey = @"Software\Microsoft\Windows\CurrentVersion\App Paths";

    public static IEnumerable<string> RegisteredInstallPaths(string[] displayNames, string[] executableNames)
    {
        var names = displayNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return executableNames.SelectMany(name => RegistryPaths($@"{AppPathsKey}\{name}", ""))
            .Concat(UninstallPaths(names))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public static IEnumerable<string> RegistryPaths(string subkey, params string[] valueNames)
    {
        foreach (var (hive, view) in RegistryLocations())
        {
            using var key = OpenKey(hive, view, subkey);
            if (key is null)
                continue;
            foreach (string name in valueNames)
                if (NormalizePath(ReadString(key, name)) is { } path)
                    yield return path;
        }
    }

    public static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(path))
            return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private static IEnumerable<string> UninstallPaths(HashSet<string> displayNames)
    {
        foreach (var (hive, view) in RegistryLocations())
        {
            using var root = OpenKey(hive, view, UninstallKey);
            if (root is null)
                continue;
            foreach (string name in RegistryRead(root.GetSubKeyNames) ?? [])
            {
                using var entry = RegistryRead(() => root.OpenSubKey(name));
                if (entry is null || ReadString(entry, "DisplayName") is not { } displayName ||
                    !MatchesDisplayName(displayName.Trim(), displayNames))
                    continue;

                if (NormalizePath(ReadString(entry, "InstallLocation")) is { } install)
                    yield return install;
                foreach (string value in new[] { "DisplayIcon", "UninstallString" })
                    if (ExecutablePath(ReadString(entry, value)) is { } executable)
                        yield return executable;
            }
        }
    }

    private static bool MatchesDisplayName(string displayName, HashSet<string> names) =>
        names.Contains(displayName) || names.Any(name => displayName.Length > name.Length + 1 &&
            displayName.StartsWith(name, StringComparison.OrdinalIgnoreCase) && displayName[name.Length] == ' ' &&
            Version.TryParse(displayName.AsSpan(name.Length + 1), out _));

    private static string? ExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return null;
        var match = ExecutableCommand().Match(command.Trim());
        return match.Success ? NormalizePath(match.Groups["path"].Value) : null;
    }

    private static IEnumerable<(RegistryHive Hive, RegistryView View)> RegistryLocations()
    {
        if (!OperatingSystem.IsWindows())
            yield break;
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            if (Environment.Is64BitOperatingSystem)
                yield return (hive, RegistryView.Registry64);
            yield return (hive, RegistryView.Registry32);
        }
    }

    private static RegistryKey? OpenKey(RegistryHive hive, RegistryView view, string path) => RegistryRead(() =>
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        return root.OpenSubKey(path);
    });

    private static string? ReadString(RegistryKey key, string name) =>
        RegistryRead(() => key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string);

    private static T? RegistryRead<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException) { return null; }
    }

    [GeneratedRegex(@"^(?:""(?<path>[^""]+\.exe)""|(?<path>[^""]+?\.exe)(?=[\s,]|$))", RegexOptions.IgnoreCase)]
    private static partial Regex ExecutableCommand();
}
