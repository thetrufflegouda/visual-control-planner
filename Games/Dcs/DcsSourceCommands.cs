using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace VisualControlsBinder.Games;

internal sealed class DcsSourceCommands(string installDirectory)
{
    // Mirrors Scripts/Input/Utils.lua. Missing fields are serialized as Lua nil.
    private static readonly (string Field, string Prefix)[] AxisFields =
        [("action", "a"), ("cockpit_device_id", "cd")];
    private static readonly (string Field, string Prefix)[] KeyFields =
        [("down", "d"), ("pressed", "p"), ("up", "u"), ("cockpit_device_id", "cd"),
         ("value_down", "vd"), ("value_pressed", "vp"), ("value_up", "vu")];

    private readonly Lazy<IReadOnlyDictionary<string, string>> _constants = new(() => ReadConstants(installDirectory));

    public string? Hash(DcsLua lua, IReadOnlyDictionary<string, DcsLua.Slice> fields, bool axis)
    {
        var hash = new StringBuilder();
        bool hasCommand = false;
        foreach (var (field, prefix) in axis ? AxisFields : KeyFields)
        {
            string? value = fields.TryGetValue(field, out var slice) ? ReadValue(lua, slice) : "nil";
            if (value is null)
                return null;
            hasCommand |= value != "nil" && field is ("action" or "down" or "pressed" or "up");
            hash.Append(prefix).Append(value);
        }
        return hasCommand ? hash.ToString() : null;
    }

    private string? ReadValue(DcsLua lua, DcsLua.Slice slice)
    {
        if (slice.Length == 1 && lua[slice.Start].Is("nil"))
            return "nil";

        var expression = new StringBuilder();
        for (int i = slice.Start; i < slice.End; i++)
        {
            if (lua[i].Literal || (i > slice.Start && lua[i - 1].EndOffset != lua[i].Offset &&
                !(i == slice.Start + 1 && lua[slice.Start].Is("-"))))
                return null;
            expression.Append(lua[i].Text);
        }
        string text = expression.ToString();
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
            return value.ToString("g14", CultureInfo.InvariantCulture);
        return slice.Length == 1 && lua[slice.Start].Text.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_')
            ? _constants.Value.GetValueOrDefault(text) : null;
    }

    private static Dictionary<string, string> ReadConstants(string installDirectory)
    {
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        string bin = Path.Combine(installDirectory, "bin");
        // A manually browsed folder is a data source. Run the helper only from the detected DCS installation.
        if (!string.Equals(Path.TrimEndingDirectorySeparator(installDirectory),
                Path.TrimEndingDirectorySeparator(DcsGame.DefaultInstallDirectory), StringComparison.OrdinalIgnoreCase) ||
            !DcsInstall.IsInstallation(installDirectory) || !File.Exists(Path.Combine(bin, "Input.dll")) ||
            !File.Exists(Path.Combine(bin, "luae.exe")))
            return constants;

        // Read the built-in command table from DCS's own Input.dll without executing module scripts.
        const string script = """
            local loadInput = assert(package.loadlib('./Input.dll', 'luaopen_Input'))
            for name, value in pairs(loadInput().getEnvTable().Actions) do
                if type(name) == 'string' and type(value) == 'number' then print(name, value) end
            end
            """;
        var start = new ProcessStartInfo(Path.Combine(bin, "luae.exe"))
        {
            WorkingDirectory = bin,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment.Remove("LUA_INIT");
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(script);
        try
        {
            using var process = Process.Start(start);
            if (process is null)
                return constants;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(2500))
            {
                process.Kill();
                return constants;
            }
            if (process.ExitCode == 0)
                foreach (string line in output.GetAwaiter().GetResult().Split('\n', StringSplitOptions.TrimEntries))
                {
                    var parts = line.Split('\t');
                    if (parts is [var key, var number] && double.TryParse(number, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
                        constants[key] = number;
                }
        }
        catch (Exception error) when (error is Win32Exception or IOException or InvalidOperationException) { }
        return constants;
    }
}
