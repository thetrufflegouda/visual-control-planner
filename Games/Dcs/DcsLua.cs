using System.IO;
using System.Text;

namespace VisualControlsBinder.Games;

internal sealed class DcsLua
{
    internal readonly record struct Token(string Text, bool Literal = false, int Offset = 0, int EndOffset = 0)
    {
        public bool Is(string text) => !Literal && Text == text;
    }

    internal readonly record struct Slice(int Start, int End)
    {
        public int Length => End - Start;
    }

    private readonly Token[] _tokens;
    private readonly int[] _ends;

    private DcsLua(string contents)
    {
        _tokens = Tokenize(contents).ToArray();
        _ends = new int[_tokens.Length];
        var stack = new Stack<int>();
        for (int i = 0; i < _tokens.Length; i++)
        {
            if (_tokens[i] is { Literal: false, Text: "(" or "{" or "[" })
                stack.Push(i);
            else if (_tokens[i] is { Literal: false, Text: ")" or "}" or "]" } && stack.TryPop(out int opening))
                _ends[opening] = i;
        }
    }

    public static DcsLua Read(string path) => new(File.ReadAllText(path));
    public static DcsLua Parse(string contents) => new(contents);
    public int Count => _tokens.Length;
    public Token this[int index] => index >= 0 && index < Count ? _tokens[index] : default;
    public int Match(int opening) => _ends[opening];

    public IEnumerable<(int Index, Slice[] Args)> Calls(string name)
    {
        for (int i = 0; i < Count - 1; i++)
        {
            if (this[i].Is(name) && this[i + 1].Is("(") && Match(i + 1) > i + 1)
                yield return (i, Parts(i + 1).ToArray());
        }
    }

    public IEnumerable<Slice> Parts(int opening)
    {
        int stop = Match(opening);
        int start = opening + 1;
        for (int i = start; i < stop; i++)
        {
            if (Match(i) > i)
                i = Match(i);
            else if (this[i] is { Literal: false, Text: "," or ";" })
            {
                if (start < i)
                    yield return new Slice(start, i);
                start = i + 1;
            }
        }
        if (start < stop)
            yield return new Slice(start, stop);
    }

    public IEnumerable<(string Key, Slice Value)> Fields(int opening)
    {
        foreach (var part in Parts(opening))
            if (Field(part) is { } field)
                yield return field;
    }

    public (string Key, Slice Value)? Field(Slice part)
    {
        int i = part.Start;
        if (part.Length > 2 && !this[i].Literal && this[i + 1].Is("="))
            return (this[i].Text, new Slice(i + 2, part.End));
        if (part.Length > 4 && this[i].Is("[") && this[i + 1].Text is { Length: > 0 } key &&
            (this[i + 1].Literal || key.All(char.IsAsciiDigit)) &&
            this[i + 2].Is("]") && this[i + 3].Is("="))
            return (key, new Slice(i + 4, part.End));
        return null;
    }

    public string? Label(Slice content) => Label(content, null);

    public string? Label(Slice content, IReadOnlyDictionary<string, string>? locals)
    {
        if (content.Length <= 0)
            return null;
        var label = new StringBuilder();
        int i = content.Start;
        while (i < content.End)
        {
            if (this[i].Literal)
                label.Append(this[i++].Text);
            else if (i + 3 < content.End && this[i].Is("_") && this[i + 1].Is("(") &&
                this[i + 2].Literal && this[i + 3].Is(")"))
            {
                label.Append(this[i + 2].Text);
                i += 4;
            }
            else if (locals?.TryGetValue(this[i].Text, out string? constant) == true)
            {
                label.Append(constant);
                i++;
            }
            else
                return null;
            if (i == content.End || this[i].Is(";"))
                return label.ToString();
            if (!this[i++].Is(".."))
                return null;
        }
        return null;
    }

    public string[] Labels(Slice content) => Labels(content, null);

    public string[] Labels(Slice content, IReadOnlyDictionary<string, string>? locals)
    {
        if (content.Length > 0 && this[content.Start].Is("{"))
            return Parts(content.Start).Select(item => Label(item, locals)).OfType<string>().ToArray();
        return Label(content, locals) is { } label ? [label] : [];
    }

    public IReadOnlyDictionary<string, string> LocalLabels()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i + 3 < Count; i++)
        {
            if (!this[i].Is("local") || this[i + 1].Literal || !this[i + 2].Is("="))
                continue;
            int start = i + 3;
            int length = this[start].Literal ? 1 : this[start].Is("_") ? 4 : 0;
            int end = start + length;
            if (length > 0 && end <= Count && !this[end].Is("..") && Label(new Slice(start, end)) is { } label)
                labels[this[i + 1].Text] = label;
        }
        return labels;
    }

    public IEnumerable<int> EnabledTables()
    {
        var falseLocals = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < Count; i++)
        {
            if (this[i].Is("=") && !this[i - 1].Literal && this[i - 1].Text is { } identifier)
            {
                if (this[i - 2].Is("local") && this[i + 1].Is("false"))
                    falseLocals.Add(identifier);
                else
                    falseLocals.Remove(identifier);
            }

            // The installed UI layer has a literal disabled block with test-only VR commands.
            // Leave complex conditions and branches to the game's runtime.
            if (this[i].Is("if") && !this[i + 1].Literal &&
                (this[i + 1].Is("false") || falseLocals.Contains(this[i + 1].Text)) && this[i + 2].Is("then"))
            {
                int end = i + 3;
                while (end < Count && this[end] is not
                    { Literal: false, Text: "end" or "else" or "elseif" or "do" or "then" or "function" or "repeat" })
                    end = Math.Max(end, Match(end)) + 1;
                if (this[end].Is("end"))
                {
                    i = end;
                    continue;
                }
            }
            if (this[i].Is("{"))
                yield return i;
        }
    }

    public string? PathExpression(Slice expression, IReadOnlyDictionary<string, string> identifiers)
    {
        var parts = new StringBuilder();
        for (int i = expression.Start; i < expression.End; i += 2)
        {
            var token = this[i];
            if (token.Literal)
                parts.Append(token.Text);
            else if (identifiers.TryGetValue(token.Text, out var value))
                parts.Append(value);
            else
                return null;
            if (i + 1 < expression.End && !this[i + 1].Is(".."))
                return null;
        }
        return parts.Length > 0 ? parts.ToString() : null;
    }

    private static IEnumerable<Token> Tokenize(string contents)
    {
        for (int i = 0; i < contents.Length;)
        {
            int begin = i;
            char current = contents[i];
            if (char.IsWhiteSpace(current) || current == '\uFEFF')
            {
                i++;
                continue;
            }
            if (current == '-' && i + 1 < contents.Length && contents[i + 1] == '-')
            {
                i += 2;
                if (LongBracket(contents, i, out int start, out string end))
                    i = FinishLong(contents, start, end, out _);
                else
                    while (i < contents.Length && contents[i] != '\n')
                        i++;
                continue;
            }
            if (current is '\'' or '"')
            {
                i++;
                var value = new StringBuilder();
                while (i < contents.Length && contents[i] != current)
                {
                    char ch = contents[i++];
                    if (ch == '\\' && i < contents.Length)
                        ch = contents[i++] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', var escaped => escaped };
                    value.Append(ch);
                }
                if (i < contents.Length)
                    i++;
                yield return new Token(value.ToString(), true, begin, i);
                continue;
            }
            if (current == '[' && LongBracket(contents, i, out int literalStart, out string literalEnd))
            {
                i = FinishLong(contents, literalStart, literalEnd, out string value);
                yield return new Token(value, true, begin, i);
                continue;
            }

            i++;
            if (char.IsAsciiLetterOrDigit(current) || current == '_')
            {
                while (i < contents.Length && (char.IsAsciiLetterOrDigit(contents[i]) || contents[i] == '_'))
                    i++;
            }
            else if (current == '.' && i < contents.Length && contents[i] == '.')
                i++;
            yield return new Token(contents[begin..i], Offset: begin, EndOffset: i);
        }
    }

    private static bool LongBracket(string contents, int index, out int start, out string end)
    {
        start = index;
        end = "";
        if (index >= contents.Length || contents[index] != '[')
            return false;
        int equals = index + 1;
        while (equals < contents.Length && contents[equals] == '=')
            equals++;
        if (equals >= contents.Length || contents[equals] != '[')
            return false;
        start = equals + 1;
        end = "]" + new string('=', equals - index - 1) + "]";
        return true;
    }

    private static int FinishLong(string contents, int start, string end, out string value)
    {
        int finish = contents.IndexOf(end, start, StringComparison.Ordinal);
        value = finish < 0 ? contents[start..] : contents[start..finish];
        return finish < 0 ? contents.Length : finish + end.Length;
    }
}
