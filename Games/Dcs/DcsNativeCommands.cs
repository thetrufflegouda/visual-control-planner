using System.IO;
using System.Net;
using System.Text.RegularExpressions;

namespace VisualControlsBinder.Games;

internal sealed partial class DcsNativeCommands
{
    private readonly DcsModule _module;
    private readonly ILookup<(bool IsAxis, string Name), string> _report;
    private readonly ILookup<(bool IsAxis, string Name), string> _diffs;
    private readonly HashSet<(bool IsAxis, string Id)> _ids;
    private readonly Lazy<ILookup<(bool IsAxis, string Name), string>> _source;

    private DcsNativeCommands(DcsModule module, Command[] report, Command[] diffs)
    {
        _module = module;
        _report = MakeLookup(report);
        _diffs = MakeLookup(diffs);
        _ids = report.Concat(diffs).Select(command => (command.IsAxis, command.Id)).ToHashSet();
        _source = new(() => ReadSource(module));
    }

    public static DcsNativeCommands Read(DcsModule module)
    {
        // DCS's Controls > Make HTML output includes the native hash even for unassigned commands.
        string reportFolder = Path.Combine(module.SavedGamesDirectory, "InputLayoutsTxt", module.Id);
        var report = Files(reportFolder, "*.html").SelectMany(ReadReport).Distinct().ToArray();
        string shippedJoystick = Path.Combine(module.InputDirectory, "joystick");
        var diffs = DcsGame.SavedJoystickProfiles(module).Concat(Files(shippedJoystick, "*.diff.lua"))
            .SelectMany(ReadDiff).Distinct().ToArray();
        return new DcsNativeCommands(module, report, diffs);
    }

    public string Resolve(GameAction action)
    {
        if (!string.Equals(action.Map, _module.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"'{action.Name}' belongs to a different DCS aircraft.");

        if (_ids.Contains((action.IsAxis, action.Id)))
            return action.Id;

        var key = (action.IsAxis, action.Name.Trim());
        var matches = _report[key].Take(2).ToArray();
        if (matches.Length == 0)
            matches = _diffs[key].Take(2).ToArray();
        if (matches.Length == 1)
            return matches[0];
        if (_source.Value[key].FirstOrDefault() is { } installed)
            return installed;

        string problem = matches.Length == 0
            ? $"VCP could not verify the DCS command ID for '{action.Name}'."
            : $"More than one DCS command ID matches '{action.Name}'.";
        throw new InvalidDataException($"{problem} In DCS Controls, select {_module.Name} and use Make HTML to export the aircraft layout.");
    }

    private static ILookup<(bool IsAxis, string Name), string> ReadSource(DcsModule module)
    {
        try
        {
            return MakeLookup(DcsGame.ReadSourceCommandIds(module).Where(entry => IsNativeHash(entry.Id, entry.Action.IsAxis))
                .Select(entry => new Command(entry.Action.IsAxis, entry.Action.Name.Trim(), entry.Id)));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return MakeLookup([]);
        }
    }

    public string ResolveForProfile(GameAction action, IReadOnlyList<DcsSavedBinding> profile)
    {
        if (!string.Equals(action.Map, _module.Id, StringComparison.OrdinalIgnoreCase))
            return Resolve(action); // Report the same aircraft mismatch as a regular DCS export.
        var compatible = profile.Where(binding => action.IsAxis == binding.IsAxis && IsNativeHash(binding.ActionId, binding.IsAxis));
        if (compatible.Any(binding => binding.ActionId == action.Id))
            return action.Id;
        string name = action.Name.Trim();
        var matches = compatible.Where(binding => binding.ActionName.Trim() == name)
            .Select(binding => binding.ActionId).Distinct().Take(2).ToArray();
        return matches is [var id] ? id : Resolve(action);
    }

    private static ILookup<(bool IsAxis, string Name), string> MakeLookup(IEnumerable<Command> source) =>
        source.Select(command => (command.IsAxis, command.Name, command.Id)).Distinct()
            .ToLookup(command => (command.IsAxis, command.Name), command => command.Id);

    private static IEnumerable<string> Files(string folder, string pattern) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, pattern) : [];

    private static Command[] ReadDiff(string path)
    {
        try
        {
            return DcsGame.LoadSavedBindings(path).Where(binding => IsNativeHash(binding.ActionId, binding.IsAxis))
                .Select(binding => new Command(binding.IsAxis, binding.ActionName.Trim(), binding.ActionId)).ToArray();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
    }

    private static Command[] ReadReport(string path)
    {
        string html;
        try { html = File.ReadAllText(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return []; }
        var commands = new List<Command>();
        foreach (Match row in ReportRow().Matches(html))
        {
            string id = WebUtility.HtmlDecode(row.Groups["hash"].Value).Trim();
            bool axis = id.StartsWith('a');
            if (!IsNativeHash(id, axis))
                continue;
            string name = WebUtility.HtmlDecode(row.Groups["name"].Value).Trim();
            if (name.Length > 0)
                commands.Add(new Command(axis, name, id));
        }
        return commands.ToArray();
    }

    private static bool IsNativeHash(string id, bool axis) => (axis ? AxisHash() : KeyHash()).IsMatch(id);

    private sealed record Command(bool IsAxis, string Name, string Id);

    // The field prefixes and order are defined by DCS's Scripts/Input/Utils.lua.
    private const string Numeric = @"(?:nil|[-+]?\d+(?:\.\d+)?(?:[eE][-+]?\d+)?)";

    [GeneratedRegex(@"\Aa" + Numeric + "cd" + Numeric + @"\z", RegexOptions.CultureInvariant)]
    private static partial Regex AxisHash();

    [GeneratedRegex(@"\Ad" + Numeric + "p" + Numeric + "u" + Numeric + "cd" + Numeric + "vd" + Numeric
        + "vp" + Numeric + "vu" + Numeric + @"\z", RegexOptions.CultureInvariant)]
    private static partial Regex KeyHash();

    [GeneratedRegex(@"<tr>\s*<td>[^\r\n]*?</td>\s*<td>(?<name>[^\r\n]*?)</td>\s*<td>[^\r\n]*?</td>\s*<td>(?<hash>[^\r\n]*?)</td>\s*</tr>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReportRow();
}
