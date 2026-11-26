using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VisualControlsBinder.Games;

internal sealed partial class Il2AircraftProfile
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _path;
    private readonly byte[] _original;
    private readonly bool _bom;
    private readonly List<string> _lines;
    private readonly string _newline;

    public Il2AircraftProfile(string path)
    {
        _path = path;
        _original = File.ReadAllBytes(path);
        _bom = _original.AsSpan().StartsWith(Encoding.UTF8.GetPreamble());
        string text = Utf8.GetString(_original.AsSpan(_bom ? Encoding.UTF8.GetPreamble().Length : 0));
        _newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        _lines = Regex.Split(text, @"(?<=\n)").ToList();
        if (!_lines.Any(line => line.TrimStart().StartsWith("&actions=action,command,invert|", StringComparison.Ordinal)))
            throw new InvalidDataException("The aircraft profile does not have the IL-2 action table.");
    }

    public bool Apply(GameAction action, string input, bool remove)
    {
        if (action.Map != "Key Mapping" || Il2JoystickSlots.Decode(input) is not { } decoded ||
            !remove && (decoded.Signal.Kind == "axis") != action.IsAxis)
            throw new InvalidOperationException($"{action.Name} needs a compatible IL-2 input.");

        var rows = _lines.Select((line, index) => (Index: index, Fields: ActionLine().Match(line)))
            .Where(row => row.Fields.Success && row.Fields.Groups["id"].Value == action.Id).ToArray();
        if (rows.Length == 0)
            throw new InvalidOperationException($"{action.Name} is unavailable in the assigned aircraft profile.");
        var same = rows.Where(row => row.Fields.Groups["input"].Value.Trim().Equals(input, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (remove)
        {
            foreach (var row in same)
                ReplaceInput(row.Index, row.Fields, "");
            return same.Length > 0;
        }
        if (same.Length > 0)
            return false;
        if (rows.Count(row => row.Fields.Groups["input"].Value.Trim().Length > 0) >= 3)
            throw new InvalidOperationException($"{action.Name} already has three inputs. Clear one before adding another.");
        var empty = rows.FirstOrDefault(row => row.Fields.Groups["input"].Value.Trim().Length == 0);
        if (empty.Fields is not null)
            ReplaceInput(empty.Index, empty.Fields, input);
        else
        {
            int last = rows[^1].Index;
            if (!_lines[last].EndsWith('\n'))
                _lines[last] += _newline;
            _lines.Insert(last + 1, $"{action.Id}, {input}, 0|{_newline}");
        }
        return true;
    }

    public NativeBindingWrite ToWrite()
    {
        byte[] text = Utf8.GetBytes(string.Concat(_lines));
        return new NativeBindingWrite(_path, _original, _bom ? [.. Encoding.UTF8.GetPreamble(), .. text] : text);
    }

    private void ReplaceInput(int line, Match row, string input)
    {
        var field = row.Groups["input"];
        var invert = row.Groups["invert"];
        string text = _lines[line];
        _lines[line] = text[..field.Index] + (" " + input).PadRight(field.Length) +
            text[(field.Index + field.Length)..invert.Index] + "0" + text[(invert.Index + invert.Length)..];
    }

    [GeneratedRegex(@"^\s*(?<id>[^,|\s]+)\s*,(?<input>[^,|\r\n]*),\s*(?<invert>[01])\s*\|")]
    private static partial Regex ActionLine();
}
