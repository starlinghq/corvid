using System.Collections.Immutable;

namespace Corvid.Approvals.Shell;

/// <summary>How a list item joins the one before it.</summary>
public enum ShellConnector
{
    /// <summary>The first item.</summary>
    None,
    /// <summary><c>;</c>, a newline, or after a backgrounded item.</summary>
    Sequence,
    /// <summary><c>&amp;&amp;</c>: runs only when the previous item succeeded.</summary>
    And,
    /// <summary><c>||</c>: runs only when the previous item failed.</summary>
    Or,
}

/// <summary>A parsed command list, the only shape Corvid's bash subset has: no control flow, functions or subshells.</summary>
public sealed record ShellScript(ImmutableArray<ShellListItem> Items);

/// <param name="Background">Followed by <c>&amp;</c>: runs asynchronously in a subshell.</param>
public sealed record ShellListItem(ShellConnector Connector, ShellPipeline Pipeline, bool Background);

/// <param name="Negated">Prefixed with <c>!</c>.</param>
public sealed record ShellPipeline(ImmutableArray<ShellCommand> Commands, bool Negated);

/// <summary>A simple command: prefix assignments, words (the first is the program) and redirections in source order.</summary>
public sealed record ShellCommand(ImmutableArray<ShellAssignment> Assignments, ImmutableArray<ShellWord> Words,
    ImmutableArray<ShellRedirect> Redirects);

/// <param name="Append"><c>NAME+=value</c>.</param>
public sealed record ShellAssignment(string Name, ShellWord Value, bool Append);

public enum RedirectKind
{
    /// <summary><c>&lt;</c></summary>
    Input,
    /// <summary><c>&gt;</c> and <c>&gt;|</c></summary>
    Output,
    /// <summary><c>&gt;&gt;</c></summary>
    Append,
    /// <summary><c>&lt;&gt;</c>: opens for reading and writing.</summary>
    ReadWrite,
    /// <summary><c>&amp;&gt;</c>, <c>&amp;&gt;&gt;</c>, or <c>&gt;&amp; file</c>: stdout and stderr to a file.</summary>
    OutputAndError,
    /// <summary><c>2&gt;&amp;1</c>, <c>&lt;&amp;3</c>, <c>&gt;&amp;-</c>: duplicates or closes a descriptor; no file.</summary>
    Duplicate,
    /// <summary><c>&lt;&lt;&lt; word</c>: the word is the input.</summary>
    HereString,
    /// <summary><c>&lt;&lt;DELIM</c>: the body is the input.</summary>
    HereDocument,
}

/// <summary>A redirection. For a here-document the target is the body, filled in once the parser reaches it.</summary>
public sealed class ShellRedirect
{
    private ShellWord? _target;

    internal ShellRedirect(int? descriptor, RedirectKind kind, ShellWord? target, bool input = false)
    {
        Descriptor = descriptor;
        Kind = kind;
        _target = target;
        Input = input || kind is RedirectKind.Input or RedirectKind.ReadWrite or RedirectKind.HereString or RedirectKind.HereDocument;
    }

    /// <summary>Written with <c>&lt;</c> (<c>&lt;</c>, <c>&lt;&gt;</c>, <c>&lt;&amp;</c>, <c>&lt;&lt;</c>, <c>&lt;&lt;&lt;</c>), so it defaults to descriptor 0.</summary>
    public bool Input { get; }

    /// <summary>The descriptor this redirection changes: the explicit one, or 0 for input forms and 1 for output forms.</summary>
    public int Changes => Descriptor ?? (Input ? 0 : 1);

    /// <summary>The explicit descriptor (<c>2&gt;</c>), or null for the default.</summary>
    public int? Descriptor { get; }

    public RedirectKind Kind { get; }

    /// <summary>The file, the here-string, or the here-document body.</summary>
    public ShellWord Target => _target ?? throw new InvalidOperationException("The here-document body was not read.");

    internal void SetBody(ShellWord body) => _target = body;

    internal bool HasTarget => _target is not null;

    /// <summary>Reads or writes a file named by <see cref="Target"/>.</summary>
    public bool NamesFile => Kind is RedirectKind.Input or RedirectKind.Output or RedirectKind.Append or RedirectKind.ReadWrite
        or RedirectKind.OutputAndError;

    /// <summary>Writes the file named by <see cref="Target"/>.</summary>
    public bool Writes => Kind is RedirectKind.Output or RedirectKind.Append or RedirectKind.ReadWrite or RedirectKind.OutputAndError;
}

/// <summary>One word before expansion. <see cref="Source"/> is the text as written, for display only.</summary>
public sealed record ShellWord(ImmutableArray<WordPart> Parts, string Source)
{
    /// <summary>The word's text when it is entirely literal, after quote removal; null when it expands anything.</summary>
    public string? Literal => Parts.All(part => part is LiteralPart)
        ? string.Concat(Parts.Cast<LiteralPart>().Select(part => part.Text))
        : null;

    public override string ToString() => Source;
}

/// <param name="Quoted">Quoted or escaped: never globbed or split.</param>
public abstract record WordPart(bool Quoted);

public sealed record LiteralPart(string Text, bool Quoted) : WordPart(Quoted);

/// <summary>An unquoted <c>~</c> at the start of a word or after an assignment's <c>=</c>: the home directory.</summary>
public sealed record TildePart() : WordPart(false);

/// <summary><c>$NAME</c>, <c>${NAME}</c> or a special parameter (<c>$1</c>, <c>$@</c>, <c>$?</c>).</summary>
public sealed record ParameterPart(string Name, bool Quoted) : WordPart(Quoted);

/// <summary><c>$(...)</c> or a backquoted command. The inner script runs before the outer command.</summary>
public sealed record SubstitutionPart(ShellScript Script, bool Quoted) : WordPart(Quoted);

/// <summary>The parser's verdict: a script in the subset, or the reason the text is outside it.</summary>
public abstract record ShellParseResult;

public sealed record ShellParsed(ShellScript Script) : ShellParseResult;

/// <summary>
/// The text is outside the subset Corvid parses (control flow, functions, subshells, arithmetic, brace or process
/// expansion, parameter operators, unterminated quotes, and so on). The caller never guesses: it asks or denies.
/// </summary>
public sealed record ShellUnsupported(string Reason, int Position) : ShellParseResult;
