using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Corvid.Approvals.Shell;

/// <summary>
/// A hand-written parser for the bash subset Corvid permits (decision D-5): command lists joined by <c>;</c>, newlines,
/// <c>&amp;&amp;</c>, <c>||</c> and <c>&amp;</c>; pipelines; simple commands with prefix assignments; single, double and
/// ANSI-C quotes; backslash escapes; <c>$NAME</c> and <c>${NAME}</c>; <c>$(...)</c> and backquotes; a leading <c>~</c>;
/// redirections, here-strings and here-documents. Everything else is refused with a reason rather than guessed at: control
/// flow, functions, subshells and groups, arithmetic, brace and process expansion, parameter operators (<c>${X:-y}</c>),
/// <c>~user</c>, arrays and unterminated quotes.
/// </summary>
public static class ShellParser
{
    /// <summary>Longer input is refused, so a hostile command cannot make parsing expensive.</summary>
    public const int MaxLength = 64 * 1024;

    /// <summary>Nesting of substitutions; deeper input is refused rather than recursing without bound.</summary>
    public const int MaxDepth = 16;

    public static ShellParseResult Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxLength)
            return new ShellUnsupported($"longer than {MaxLength} characters", MaxLength);
        var nul = text.IndexOf('\0', StringComparison.Ordinal);
        if (nul >= 0)
            return new ShellUnsupported("NUL character", nul);
        try
        {
            return new ShellParsed(new Parser(text, 0).ParseTop());
        }
        catch (UnsupportedSyntax unsupported)
        {
            return new ShellUnsupported(unsupported.Reason, unsupported.Position);
        }
    }

    private sealed class UnsupportedSyntax(string reason, int position) : Exception(reason)
    {
        public string Reason { get; } = reason;

        public int Position { get; } = position;
    }

    private sealed record PendingHereDocument(ShellRedirect Redirect, string Delimiter, bool Quoted, bool StripTabs);

    private sealed class Parser(string text, int depth)
    {
        private static readonly FrozenSet<string> Reserved = FrozenSet.Create(StringComparer.Ordinal,
            "if", "then", "else", "elif", "fi", "do", "done", "case", "esac", "while", "until", "for", "in", "function",
            "select", "coproc", "{", "}", "[[", "]]", "!");

        private readonly List<PendingHereDocument> _pending = [];
        private int _pos;
        private int _depth = depth;

        public ShellScript ParseTop()
        {
            var script = ParseList(closer: null);
            if (_pos < text.Length)
                throw Unsupported($"unexpected '{text[_pos]}'");
            if (_pending.Count > 0)
                throw Unsupported($"here-document '{_pending[0].Delimiter}' has no body");
            return script;
        }

        private UnsupportedSyntax Unsupported(string reason) => new(reason, _pos);

        private bool AtEnd => _pos >= text.Length;

        private char Peek(int offset = 0) => _pos + offset < text.Length ? text[_pos + offset] : '\0';

        private bool Match(string token)
        {
            if (string.CompareOrdinal(text, _pos, token, 0, token.Length) != 0)
                return false;
            _pos += token.Length;
            return true;
        }

        private static bool IsBlank(char c) => c is ' ' or '\t';

        private static bool IsMeta(char c) => c is '|' or '&' or ';' or '(' or ')' or '<' or '>';

        private static bool IsNameStart(char c) => char.IsAsciiLetter(c) || c == '_';

        private static bool IsNameChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

        internal static bool IsName(string name) => name.Length > 0 && IsNameStart(name[0]) && name.All(IsNameChar);

        private ShellScript ParseList(char? closer)
        {
            var items = ImmutableArray.CreateBuilder<ShellListItem>();
            var connector = ShellConnector.None;
            SkipBlanksAndNewlines();
            while (true)
            {
                SkipBlanks();
                if (AtEnd || closer is { } c && Peek() == c)
                {
                    if (connector is ShellConnector.And or ShellConnector.Or)
                        throw Unsupported("missing command after && or ||");
                    break;
                }

                var pipeline = ParsePipeline();
                SkipBlanks();
                var background = false;
                if (Peek() == '&' && Peek(1) != '&' && Peek(1) != '>')
                {
                    _pos++;
                    background = true;
                }

                items.Add(new ShellListItem(connector, pipeline, background));
                SkipBlanks();
                if (Match("&&"))
                    connector = ShellConnector.And;
                else if (Match("||"))
                    connector = ShellConnector.Or;
                else if (Peek() == ';')
                {
                    if (Peek(1) == ';')
                        throw Unsupported("';;' outside case");
                    _pos++;
                    connector = ShellConnector.Sequence;
                }
                else if (Peek() == '\n' || background)
                    connector = ShellConnector.Sequence;
                else
                    break;
                SkipBlanksAndNewlines();
            }

            return new ShellScript(items.ToImmutable());
        }

        private ShellPipeline ParsePipeline()
        {
            var negated = false;
            if (Peek() == '!' && (IsBlank(Peek(1)) || Peek(1) == '\0'))
            {
                _pos++;
                negated = true;
                SkipBlanks();
            }

            var commands = ImmutableArray.CreateBuilder<ShellCommand>();
            commands.Add(ParseCommand());
            while (true)
            {
                SkipBlanks();
                if (Peek() != '|' || Peek(1) == '|')
                    break;
                _pos++;
                if (Peek() == '&')
                    _pos++;
                SkipBlanksAndNewlines();
                commands.Add(ParseCommand());
            }

            return new ShellPipeline(commands.ToImmutable(), negated);
        }

        private ShellCommand ParseCommand()
        {
            var assignments = ImmutableArray.CreateBuilder<ShellAssignment>();
            var words = ImmutableArray.CreateBuilder<ShellWord>();
            var redirects = ImmutableArray.CreateBuilder<ShellRedirect>();
            while (true)
            {
                SkipBlanks();
                if (AtEnd)
                    break;
                var c = Peek();
                if (c is '\n' or ';' or '|' or ')' || c == '&' && Peek(1) != '>')
                    break;
                if (c == '(')
                    throw Unsupported(words.Count == 0 ? "subshell or arithmetic command" : "function definition");
                if (TryParseRedirect(out var redirect))
                {
                    redirects.Add(redirect);
                    continue;
                }

                var word = ParseWord();
                if (words.Count == 0)
                {
                    if (TryAssignment(word, out var assignment))
                    {
                        assignments.Add(assignment);
                        continue;
                    }

                    if (word.Parts is [LiteralPart { Quoted: false } keyword] && Reserved.Contains(keyword.Text))
                        throw Unsupported($"'{keyword.Text}': control flow, groups and functions are outside the subset");
                }

                words.Add(word);
            }

            if (assignments.Count == 0 && words.Count == 0 && redirects.Count == 0)
                throw Unsupported(AtEnd ? "missing command" : $"unexpected '{Peek()}'");
            return new ShellCommand(assignments.ToImmutable(), words.ToImmutable(), redirects.ToImmutable());
        }

        private bool TryParseRedirect(out ShellRedirect redirect)
        {
            redirect = null!;
            var start = _pos;
            var digits = _pos;
            while (digits < text.Length && char.IsAsciiDigit(text[digits]))
                digits++;
            int? descriptor = null;
            if (digits > _pos)
            {
                if (digits >= text.Length || text[digits] is not ('<' or '>'))
                    return false;
                if (!int.TryParse(text.AsSpan(_pos, digits - _pos), NumberStyles.None, CultureInfo.InvariantCulture, out var fd))
                    throw Unsupported("descriptor out of range");
                descriptor = fd;
                _pos = digits;
            }

            RedirectKind kind;
            var duplicate = false;
            var input = Peek() == '<';
            if (Peek() == '&' && Peek(1) == '>')
            {
                if (descriptor is not null)
                    throw Unsupported("descriptor before &>");
                _pos += 2;
                if (Peek() == '>')
                    _pos++;
                kind = RedirectKind.OutputAndError;
            }
            else if (Peek() == '<')
            {
                if (Peek(1) == '(')
                    throw Unsupported("process substitution");
                if (Match("<<<"))
                    kind = RedirectKind.HereString;
                else if (Match("<<"))
                    return ParseHereDocument(descriptor, out redirect);
                else if (Match("<>"))
                    kind = RedirectKind.ReadWrite;
                else if (Match("<&"))
                {
                    kind = RedirectKind.Duplicate;
                    duplicate = true;
                }
                else
                {
                    _pos++;
                    kind = RedirectKind.Input;
                }
            }
            else if (Peek() == '>')
            {
                if (Peek(1) == '(')
                    throw Unsupported("process substitution");
                if (Match(">>"))
                    kind = RedirectKind.Append;
                else if (Match(">|"))
                    kind = RedirectKind.Output;
                else if (Match(">&"))
                {
                    kind = RedirectKind.Duplicate;
                    duplicate = true;
                }
                else
                {
                    _pos++;
                    kind = RedirectKind.Output;
                }
            }
            else
            {
                _pos = start;
                return false;
            }

            var target = ParseRedirectTarget();
            if (duplicate && !IsDescriptorTarget(target))
            {
                // ">& file" sends stdout and stderr to the file; "<& word" with a non-descriptor is an error in bash.
                if (input)
                    throw Unsupported("'<&' needs a descriptor");
                if (descriptor is not null)
                    throw Unsupported("'n>&' needs a descriptor");
                kind = RedirectKind.OutputAndError;
            }

            redirect = new ShellRedirect(descriptor, kind, target, input);
            return true;
        }

        private static bool IsDescriptorTarget(ShellWord word) =>
            word.Literal is { Length: > 0 } literal && (literal == "-" || literal.TrimEnd('-').All(char.IsAsciiDigit));

        private ShellWord ParseRedirectTarget()
        {
            SkipBlanks();
            if (AtEnd || Peek() == '\n' || IsMeta(Peek()))
                throw Unsupported("redirection without a target");
            return ParseWord();
        }

        private bool ParseHereDocument(int? descriptor, out ShellRedirect redirect)
        {
            var stripTabs = false;
            if (Peek() == '-')
            {
                _pos++;
                stripTabs = true;
            }

            var delimiterWord = ParseRedirectTarget();
            if (delimiterWord.Parts.Any(part => part is not LiteralPart))
                throw Unsupported("here-document delimiter with an expansion");
            var delimiter = delimiterWord.Literal!;
            var quoted = delimiterWord.Parts.Any(part => part.Quoted);
            redirect = new ShellRedirect(descriptor, RedirectKind.HereDocument, null);
            _pending.Add(new PendingHereDocument(redirect, delimiter, quoted, stripTabs));
            return true;
        }

        // Bodies start after the newline that ends the line holding their operators, in operator order.
        private void ConsumeNewline()
        {
            _pos++;
            if (_pending.Count == 0)
                return;
            foreach (var pending in _pending)
            {
                var body = new StringBuilder();
                while (true)
                {
                    if (AtEnd)
                        throw Unsupported($"here-document '{pending.Delimiter}' is not terminated");
                    var newline = text.IndexOf('\n', _pos);
                    var line = text[_pos..(newline < 0 ? text.Length : newline)];
                    _pos = newline < 0 ? text.Length : newline + 1;
                    if (pending.StripTabs)
                        line = line.TrimStart('\t');
                    if (line == pending.Delimiter)
                        break;
                    body.Append(line).Append('\n');
                }

                var content = body.ToString();
                pending.Redirect.SetBody(pending.Quoted
                    ? new ShellWord([new LiteralPart(content, true)], content)
                    : new Parser(content, Deeper()).ParseHereDocumentBody());
            }

            _pending.Clear();
        }

        private ShellWord ParseHereDocumentBody()
        {
            var parts = ParseQuoted(hereDocument: true);
            return new ShellWord(parts, text);
        }

        private int Deeper() =>
            _depth + 1 > MaxDepth ? throw Unsupported($"nested deeper than {MaxDepth}") : _depth + 1;

        private void SkipBlanks()
        {
            while (!AtEnd)
            {
                var c = Peek();
                if (IsBlank(c))
                    _pos++;
                else if (c == '\\' && Peek(1) == '\n')
                    _pos += 2;
                else if (c == '#')
                {
                    while (!AtEnd && Peek() != '\n')
                        _pos++;
                }
                else
                    break;
            }
        }

        private void SkipBlanksAndNewlines()
        {
            while (true)
            {
                SkipBlanks();
                if (Peek() != '\n')
                    return;
                ConsumeNewline();
            }
        }

        private ShellWord ParseWord()
        {
            var start = _pos;
            var parts = new List<WordPart>();
            var literal = new StringBuilder();
            var literalQuoted = false;
            var braceDepth = 0;
            var braceList = false;

            void Flush()
            {
                if (literal.Length == 0)
                    return;
                parts.Add(new LiteralPart(literal.ToString(), literalQuoted));
                literal.Clear();
            }

            void Add(char c, bool quoted)
            {
                if (literal.Length > 0 && literalQuoted != quoted)
                    Flush();
                literalQuoted = quoted;
                literal.Append(c);
            }

            while (!AtEnd)
            {
                var c = Peek();
                if (IsBlank(c) || c == '\n' || IsMeta(c))
                    break;
                switch (c)
                {
                    case '\\':
                        if (Peek(1) == '\n')
                            _pos += 2;
                        else if (_pos + 1 >= text.Length)
                        {
                            Add('\\', true);
                            _pos++;
                        }
                        else
                        {
                            Add(Peek(1), true);
                            _pos += 2;
                        }

                        break;
                    case '\'':
                    {
                        var close = text.IndexOf('\'', _pos + 1);
                        if (close < 0)
                            throw Unsupported("unterminated single quote");
                        Flush();
                        parts.Add(new LiteralPart(text[(_pos + 1)..close], true));
                        _pos = close + 1;
                        break;
                    }
                    case '"':
                        Flush();
                        _pos++;
                        parts.AddRange(ParseQuoted(hereDocument: false));
                        break;
                    case '$':
                        Flush();
                        parts.Add(ParseDollar(quoted: false));
                        break;
                    case '`':
                        Flush();
                        parts.Add(ParseBackquote(quoted: false));
                        break;
                    case '~' when TildeAllowed(parts, literal, literalQuoted):
                        Flush();
                        _pos++;
                        if (!AtEnd && Peek() != '/' && !IsBlank(Peek()) && Peek() != '\n' && !IsMeta(Peek()))
                            throw Unsupported("tilde prefix other than ~ or ~/");
                        parts.Add(new TildePart());
                        break;
                    default:
                        // Brace expansion turns one word into several; refuse it rather than model it.
                        if (c == '{')
                            braceDepth++;
                        else if (c == '}' && braceDepth > 0)
                        {
                            if (braceList)
                                throw Unsupported("brace expansion");
                            braceDepth--;
                        }
                        else if (braceDepth > 0 && (c == ',' || c == '.' && Peek(1) == '.'))
                            braceList = true;
                        Add(c, false);
                        _pos++;
                        break;
                }
            }

            Flush();
            if (parts.Count == 0)
                throw Unsupported(AtEnd ? "expected a word" : $"unexpected '{Peek()}'");
            return new ShellWord([.. parts], text[start.._pos]);
        }

        // "~" expands at the start of a word and after the first "=" of an assignment-shaped word (bash expands
        // "of=~/x" for dd, too).
        private static bool TildeAllowed(List<WordPart> parts, StringBuilder literal, bool literalQuoted)
        {
            if (parts.Count == 0 && literal.Length == 0)
                return true;
            if (parts.Count != 0 || literalQuoted || literal.Length < 2 || literal[^1] != '=')
                return false;
            var name = literal.ToString(0, literal.Length - 1);
            return IsName(name);
        }

        /// <summary>The inside of double quotes (after the opening quote) or an unquoted here-document body.</summary>
        private ImmutableArray<WordPart> ParseQuoted(bool hereDocument)
        {
            var parts = ImmutableArray.CreateBuilder<WordPart>();
            var literal = new StringBuilder();

            void Flush()
            {
                if (literal.Length > 0)
                    parts.Add(new LiteralPart(literal.ToString(), true));
                literal.Clear();
            }

            while (true)
            {
                if (AtEnd)
                {
                    if (hereDocument)
                        break;
                    throw Unsupported("unterminated double quote");
                }

                var c = Peek();
                if (c == '"' && !hereDocument)
                {
                    _pos++;
                    break;
                }

                if (c == '\\' && (Peek(1) is '$' or '`' or '\\' or '\n' || Peek(1) == '"' && !hereDocument))
                {
                    if (Peek(1) != '\n')
                        literal.Append(Peek(1));
                    _pos += 2;
                }
                else if (c == '$')
                {
                    Flush();
                    parts.Add(ParseDollar(quoted: true));
                }
                else if (c == '`')
                {
                    Flush();
                    parts.Add(ParseBackquote(quoted: true));
                }
                else
                {
                    literal.Append(c);
                    _pos++;
                }
            }

            Flush();
            if (parts.Count == 0)
                parts.Add(new LiteralPart("", true));
            return parts.ToImmutable();
        }

        private WordPart ParseDollar(bool quoted)
        {
            _pos++;
            if (AtEnd)
                return new LiteralPart("$", quoted);
            var c = Peek();
            switch (c)
            {
                case '(':
                {
                    if (Peek(1) == '(')
                        throw Unsupported("arithmetic expansion");
                    _pos++;
                    var saved = _depth;
                    _depth = Deeper();
                    var script = ParseList(closer: ')');
                    _depth = saved;
                    if (Peek() != ')')
                        throw Unsupported("unterminated command substitution");
                    _pos++;
                    return new SubstitutionPart(script, quoted);
                }
                case '{':
                {
                    var close = text.IndexOf('}', _pos);
                    if (close < 0)
                        throw Unsupported("unterminated ${");
                    var name = text[(_pos + 1)..close];
                    if (!IsParameterName(name))
                        throw Unsupported($"parameter expansion '${{{name}}}': operators are outside the subset");
                    _pos = close + 1;
                    return new ParameterPart(name, quoted);
                }
                case '\'' when !quoted:
                    return ParseAnsiC();
                case '"' when !quoted:
                    throw Unsupported("locale string $\"...\"");
                case '[':
                    throw Unsupported("arithmetic expansion");
            }

            if (IsNameStart(c))
            {
                var start = _pos;
                while (!AtEnd && IsNameChar(Peek()))
                    _pos++;
                return new ParameterPart(text[start.._pos], quoted);
            }

            if (char.IsAsciiDigit(c) || c is '@' or '*' or '#' or '?' or '$' or '!' or '-')
            {
                _pos++;
                return new ParameterPart(c.ToString(), quoted);
            }

            return new LiteralPart("$", quoted);
        }

        private static bool IsParameterName(string name) =>
            IsName(name) || name.Length > 0 && name.All(char.IsAsciiDigit) || name is "@" or "*" or "#" or "?" or "$" or "!" or "-";

        private WordPart ParseBackquote(bool quoted)
        {
            var start = _pos;
            _pos++;
            var inner = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                    throw Unsupported("unterminated backquote");
                var c = Peek();
                if (c == '`')
                {
                    _pos++;
                    break;
                }

                if (c == '\\' && (Peek(1) is '`' or '\\' or '$' || quoted && Peek(1) == '"'))
                {
                    inner.Append(Peek(1));
                    _pos += 2;
                    continue;
                }

                inner.Append(c);
                _pos++;
            }

            try
            {
                return new SubstitutionPart(new Parser(inner.ToString(), Deeper()).ParseTop(), quoted);
            }
            catch (UnsupportedSyntax nested)
            {
                throw new UnsupportedSyntax(nested.Reason, start);
            }
        }

        // $'...': decoded here so "$'\x72m' -rf /" is seen as rm. Escapes bash would treat specially and that are not
        // modelled are refused.
        private LiteralPart ParseAnsiC()
        {
            _pos++;
            var value = new StringBuilder();
            while (true)
            {
                if (AtEnd)
                    throw Unsupported("unterminated $'...'");
                var c = Peek();
                if (c == '\'')
                {
                    _pos++;
                    break;
                }

                if (c != '\\')
                {
                    value.Append(c);
                    _pos++;
                    continue;
                }

                if (_pos + 1 >= text.Length)
                    throw Unsupported("unterminated $'...'");
                var escape = Peek(1);
                _pos += 2;
                switch (escape)
                {
                    case 'a': value.Append('\a'); break;
                    case 'b': value.Append('\b'); break;
                    case 'e' or 'E': value.Append('\u001b'); break;
                    case 'f': value.Append('\f'); break;
                    case 'n': value.Append('\n'); break;
                    case 'r': value.Append('\r'); break;
                    case 't': value.Append('\t'); break;
                    case 'v': value.Append('\v'); break;
                    case '\\' or '\'' or '"' or '?': value.Append(escape); break;
                    case 'x': value.Append(CodePoint(ReadDigits(16, 2))); break;
                    case 'u': value.Append(CodePoint(ReadDigits(16, 4))); break;
                    case 'U': value.Append(CodePoint(ReadDigits(16, 8))); break;
                    case >= '0' and <= '7':
                        _pos--;
                        value.Append(CodePoint(ReadDigits(8, 3)));
                        break;
                    case 'c':
                        throw Unsupported("control-character escape in $'...'");
                    default:
                        value.Append('\\').Append(escape);
                        break;
                }
            }

            return new LiteralPart(value.ToString(), true);
        }

        private int ReadDigits(int radix, int max)
        {
            var value = 0;
            var count = 0;
            while (count < max && !AtEnd && HexValue(Peek()) is { } digit && digit < radix)
            {
                value = value * radix + digit;
                _pos++;
                count++;
            }

            return count == 0 ? throw Unsupported("escape without digits in $'...'") : value;
        }

        private static int? HexValue(char c) => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'a' and <= 'f' => c - 'a' + 10,
            >= 'A' and <= 'F' => c - 'A' + 10,
            _ => null,
        };

        private string CodePoint(int value) =>
            value is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
                ? char.ConvertFromUtf32(value)
                : throw Unsupported("escape outside the Unicode range, or NUL, in $'...'");

        private static bool TryAssignment(ShellWord word, out ShellAssignment assignment)
        {
            assignment = null!;
            if (word.Parts is not [LiteralPart { Quoted: false } first, ..])
                return false;
            var equals = first.Text.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
                return false;
            var append = first.Text[equals - 1] == '+';
            var name = first.Text[..(append ? equals - 1 : equals)];
            if (!IsName(name))
                return false;
            var rest = first.Text[(equals + 1)..];
            var parts = ImmutableArray.CreateBuilder<WordPart>();
            if (rest.Length > 0)
                parts.Add(new LiteralPart(rest, false));
            parts.AddRange(word.Parts.Skip(1));
            if (parts.Count == 0)
                parts.Add(new LiteralPart("", true));
            var source = word.Source[(word.Source.IndexOf('=', StringComparison.Ordinal) + 1)..];
            assignment = new ShellAssignment(name, new ShellWord(parts.ToImmutable(), source), append);
            return true;
        }
    }
}
