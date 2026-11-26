using System.Diagnostics;
using System.IO;
using System.Text;

namespace VisualControlsBinder.Games;

internal sealed class DcsDiffProfile
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly byte[] Bom = Encoding.UTF8.GetPreamble();
    private readonly byte[]? _original;
    private readonly bool _hasBom;
    private string _text;

    public string Path { get; }
    public bool Exists => _original is not null;

    private DcsDiffProfile(string path, byte[]? original)
    {
        Path = path;
        _original = original;
        _hasBom = original?.AsSpan().StartsWith(Bom) == true;
        _text = original is null ? "local diff = {\n}\nreturn diff\n" : Utf8.GetString(original.AsSpan(_hasBom ? Bom.Length : 0));
        ReadDocument();
    }

    public static DcsDiffProfile Open(string path) => Read(path, allowMissing: true);
    public static DcsDiffProfile Load(string path) => Read(path, allowMissing: false);

    public IReadOnlyList<DcsSavedBinding> Bindings => DcsGame.LoadSavedBindings(ReadDocument());

    private static DcsDiffProfile Read(string path, bool allowMissing)
    {
        var info = new FileInfo(path);
        if (allowMissing && !info.Exists)
            return new DcsDiffProfile(info.FullName, null);
        if (info.Length > 16 * 1024 * 1024)
            throw new InvalidDataException("The DCS device profile exceeds 16 MB.");
        return new DcsDiffProfile(info.FullName, File.ReadAllBytes(info.FullName));
    }

    public bool Apply(string hash, GameAction action, string key, bool remove)
    {
        if (DcsJoystickInputs.Parse(key) is not { } signal || (signal.Kind == "axis") != action.IsAxis)
            throw new InvalidOperationException($"{action.Name} requires a compatible DCS input.");
        var lua = ReadDocument();
        const int root = 3;
        string sectionName = action.IsAxis ? "axisDiffs" : "keyDiffs";
        var sections = ReadFields(lua, root);
        if (!sections.TryGetValue(sectionName, out var section))
        {
            if (remove)
                return false;
            InsertField(lua, root, sectionName, Table([Field(hash, NewAction(action, key))]));
            return true;
        }

        var actions = ReadFields(lua, section.Start);
        if (!actions.TryGetValue(hash, out var actionTable))
        {
            if (remove)
                return false;
            InsertField(lua, section.Start, hash, NewAction(action, key));
            return true;
        }

        var entries = ReadFields(lua, actionTable.Start);
        var added = ReadCombos(lua, entries.GetValueOrDefault("added"));
        var changed = ReadCombos(lua, entries.GetValueOrDefault("changed"));
        var removed = ReadCombos(lua, entries.GetValueOrDefault("removed"));
        if (remove)
        {
            bool disabledDefault = changed.RemoveAll(Matches) > 0;
            if (added.RemoveAll(Matches) == 0 && !disabledDefault)
                return false;
            if (disabledDefault && !removed.Any(Matches))
                removed.Add(new Combo(key, false, KeyTable(key)));
        }
        else
        {
            if (added.Any(Matches) || changed.Any(Matches))
                return false;
            if (removed.RemoveAll(Matches) == 0)
                added.Add(new Combo(key, false, KeyTable(key)));
        }

        var ordered = new List<string>();
        foreach (var (name, slice) in entries)
            if (name is not ("added" or "changed" or "removed"))
                ordered.Add(Field(name, Raw(lua, slice)));
        foreach (var (name, combos) in new[] { ("added", added), ("changed", changed), ("removed", removed) })
            if (combos.Count > 0)
                ordered.Add(Field(name, ComboTable(combos.Select(combo => combo.Raw))));
        Replace(lua, actionTable, Table(ordered));
        return true;

        bool Matches(Combo combo) => !combo.Modified && combo.Key == key;
    }

    public NativeBindingWrite ToWrite(string? copyPath = null)
    {
        ReadDocument();
        byte[] bytes = Utf8.GetBytes(_text);
        return new NativeBindingWrite(copyPath ?? Path, copyPath is null ? _original : null, _hasBom ? [.. Bom, .. bytes] : bytes);
    }

    public static void EnsureDcsClosed()
    {
        foreach (var process in Process.GetProcessesByName("DCS"))
        {
            using (process)
                if (!process.HasExited)
                    throw new InvalidOperationException("Close DCS World before updating the active device binding files.");
        }
    }

    private DcsLua ReadDocument()
    {
        var lua = DcsLua.Parse(_text);
        if (!lua[0].Is("local") || !lua[1].Is("diff") || !lua[2].Is("=") || !lua[3].Is("{") ||
            lua.Match(3) is not (> 3) || !lua[lua.Match(3) + 1].Is("return") ||
            !lua[lua.Match(3) + 2].Is("diff") || lua.Match(3) + 3 != lua.Count)
            throw new InvalidDataException("The selected file is not a standard DCS device diff.");
        return lua;
    }

    private static Dictionary<string, DcsLua.Slice> ReadFields(DcsLua lua, int start)
    {
        if (!lua[start].Is("{") || lua.Match(start) <= start)
            throw new InvalidDataException("A DCS binding table has an unexpected format.");
        var entries = new Dictionary<string, DcsLua.Slice>(StringComparer.Ordinal);
        foreach (var part in lua.Parts(start))
            if (lua.Field(part) is not { } field || !entries.TryAdd(field.Key, field.Value))
                throw new InvalidDataException("A DCS binding table contains an invalid or duplicate property.");
        return entries;
    }

    private List<Combo> ReadCombos(DcsLua lua, DcsLua.Slice slice)
    {
        if (slice.Length == 0)
            return [];
        var result = new List<Combo>();
        if (!lua[slice.Start].Is("{") || lua.Match(slice.Start) != slice.End - 1)
            throw new InvalidDataException("The DCS input list has an unexpected format.");
        foreach (var part in lua.Parts(slice.Start))
        {
            var entry = lua.Field(part)?.Value ?? part;
            var fields = ReadFields(lua, entry.Start);
            string key = lua.Label(fields.GetValueOrDefault("key"))
                ?? throw new InvalidDataException("A DCS input has no key.");
            bool modified = fields.TryGetValue("reformers", out var reformers) &&
                (!lua[reformers.Start].Is("{") || lua.Parts(reformers.Start).Any());
            result.Add(new Combo(key, modified, Raw(lua, entry)));
        }
        return result;
    }

    private void InsertField(DcsLua lua, int table, string name, string value)
    {
        int close = lua.Match(table);
        bool missingSeparator = close > table + 1 && !lua[close - 1].Is(",") && !lua[close - 1].Is(";");
        int offset = lua[close].Offset;
        _text = _text.Insert(offset, (missingSeparator ? ",\n" : "") + Field(name, value) + ",\n");
    }

    private void Replace(DcsLua lua, DcsLua.Slice slice, string value) =>
        _text = _text[..lua[slice.Start].Offset] + value + _text[lua[slice.End - 1].EndOffset..];

    private string Raw(DcsLua lua, DcsLua.Slice slice) => _text[lua[slice.Start].Offset..lua[slice.End - 1].EndOffset];
    private static string Field(string name, string value) => $"[{Quote(name)}] = {value}";
    private static string KeyTable(string key) => Table([Field("key", Quote(key))]);
    private static string Table(IEnumerable<string> fields) => "{\n" + string.Join(",\n", fields) + "\n}";
    private static string ComboTable(IEnumerable<string> combos) =>
        Table(combos.Select((combo, index) => $"[{index + 1}] = {combo}"));
    private static string NewAction(GameAction action, string key) =>
        Table([Field("added", ComboTable([KeyTable(key)])), Field("name", Quote(action.Name))]);

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"")
        .Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

    private sealed record Combo(string Key, bool Modified, string Raw);
}
