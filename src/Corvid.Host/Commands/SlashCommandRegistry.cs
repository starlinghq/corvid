using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Corvid.Host.Composition;

namespace Corvid.Host.Commands;

public enum OutputTone
{
    Normal,
    Success,
    Warning,
    Error,
}

/// <summary>A table a surface renders its own way: a Spectre table in the CLI, aligned text in a chat message.</summary>
public sealed record CommandTable(ImmutableArray<string> Columns, ImmutableArray<ImmutableArray<string>> Rows)
{
    public static CommandTable Of(IEnumerable<string> columns, IEnumerable<IEnumerable<string>> rows) =>
        new([.. columns], [.. rows.Select(row => row.ToImmutableArray())]);
}

/// <summary>An option a command offers: typing <see cref="Key"/> (its number in the output) runs <see cref="Command"/>, a full command line.</summary>
public sealed record CommandChoice(string Key, string Label, string Command);

/// <summary>
/// What a command returns, independent of the surface (plan §12: one registry shared by the CLI and the gateway). Plain
/// lines, an optional table, a tone, and whether the surface should end the session.
/// </summary>
public sealed record CommandOutput
{
    public string? Title { get; init; }

    public ImmutableArray<string> Lines { get; init; } = [];

    public CommandTable? Table { get; init; }

    public OutputTone Tone { get; init; }

    /// <summary>
    /// Numbered options, in the order the output numbers them (<c>/model</c>'s models). The chat asks for a number and
    /// runs the pick; other surfaces show the output, whose numbers work as the command's arguments.
    /// </summary>
    public ImmutableArray<CommandChoice> Choices { get; init; } = [];

    /// <summary>The question the chat asks before reading a pick: <c>Switch generator to</c>.</summary>
    public string? ChoicePrompt { get; init; }

    /// <summary>The surface should leave the session (<c>/exit</c>).</summary>
    public bool Exit { get; init; }

    public static CommandOutput Text(params string[] lines) => new() { Lines = [.. lines] };

    public static CommandOutput Success(params string[] lines) => new() { Lines = [.. lines], Tone = OutputTone.Success };

    public static CommandOutput Warning(params string[] lines) => new() { Lines = [.. lines], Tone = OutputTone.Warning };

    public static CommandOutput Error(params string[] lines) => new() { Lines = [.. lines], Tone = OutputTone.Error };

    /// <summary>Plain text for surfaces without tables (a gateway message, a log line).</summary>
    public string ToPlainText()
    {
        var text = new StringBuilder();
        if (Title is not null)
            text.AppendLine(Title);
        foreach (var line in Lines)
            text.AppendLine(line);
        if (Table is { } table)
        {
            var widths = table.Columns.Select((column, i) => table.Rows.Select(row => i < row.Length ? row[i].Length : 0)
                .Prepend(column.Length).Max()).ToArray();
            text.AppendLine(string.Join("  ", table.Columns.Select((column, i) => column.PadRight(widths[i]))).TrimEnd());
            foreach (var row in table.Rows)
                text.AppendLine(string.Join("  ", row.Select((cell, i) => cell.PadRight(i < widths.Length ? widths[i] : 0))).TrimEnd());
        }
        return text.ToString().TrimEnd();
    }
}

/// <summary>One parsed command line: <c>/model generator qwen3.5-0.8b</c>.</summary>
/// <param name="Text">Everything after the command name, as typed (for free text such as a goal).</param>
public sealed record SlashInvocation(string Name, ImmutableArray<string> Arguments, string Text);

/// <summary>What a command can reach: the runtime, the session it was typed in, and the registry (for <c>/help</c>).</summary>
public sealed record SlashCommandContext(CorvidRuntime Runtime, CorvidSession Session, SlashCommandRegistry Registry);

public delegate ValueTask<CommandOutput> SlashCommandHandler(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken);

/// <param name="Usage">The arguments, shown by <c>/help</c>: <c>[slot] [model] [--pin]</c>.</param>
public sealed record SlashCommand(string Name, string Summary, string Usage, SlashCommandHandler Run)
{
    public ImmutableArray<string> Aliases { get; init; } = [];
}

/// <summary>
/// The slash commands every surface shares (plan §12, §17 row 1.3). Each is defined once, here, against the runtime;
/// surfaces only parse the line and render the <see cref="CommandOutput"/>.
/// </summary>
public sealed partial class SlashCommandRegistry
{
    private readonly Dictionary<string, SlashCommand> _byName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SlashCommand> _commands = [];

    public IReadOnlyList<SlashCommand> Commands => _commands;

    public SlashCommandRegistry Add(SlashCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var names = command.Aliases.Prepend(command.Name).ToArray();
        foreach (var name in names)
        {
            if (!NamePattern().IsMatch(name))
                throw new ArgumentException($"'{name}' is not a valid command name (lower case letters, digits and dashes).", nameof(command));
            if (_byName.ContainsKey(name))
                throw new ArgumentException($"A command named '{name}' is already registered.", nameof(command));
        }
        foreach (var name in names)
            _byName[name] = command;
        _commands.Add(command);
        return this;
    }

    public bool TryGet(string name, out SlashCommand command) => _byName.TryGetValue(name.TrimStart('/'), out command!);

    /// <summary>
    /// A line is a command when its first word is <c>/</c> and a name (so a pasted path such as <c>/tmp/x</c> is a message).
    /// </summary>
    public static bool IsCommand(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var trimmed = line.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != '/' || !char.IsAsciiLetter(trimmed[1]))
            return false;
        var end = trimmed.IndexOfAny([' ', '\t']);
        return (end < 0 ? trimmed[1..] : trimmed[1..end]).All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    /// <summary>Splits a command line into its name and arguments; double quotes group words.</summary>
    public static SlashInvocation Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('/'))
            throw new ArgumentException("A command starts with '/'.", nameof(line));
        var space = trimmed.IndexOfAny([' ', '\t']);
        var name = (space < 0 ? trimmed[1..] : trimmed[1..space]).ToLowerInvariant();
        var text = space < 0 ? "" : trimmed[(space + 1)..].Trim();
        var arguments = ImmutableArray.CreateBuilder<string>();
        var current = new StringBuilder();
        var quoted = false;
        var any = false;
        foreach (var c in text)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                    arguments.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }
        if (any)
            arguments.Add(current.ToString());
        return new SlashInvocation(name, arguments.ToImmutable(), text);
    }

    public async ValueTask<CommandOutput> ExecuteAsync(string line, SlashCommandContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var invocation = Parse(line);
        if (!TryGet(invocation.Name, out var command))
        {
            // Each command once, by its name when that matched (/mod suggests /model, not also /models), else by the alias that did (/qu: /quit).
            var near = _byName.Where(pair => pair.Key.StartsWith(invocation.Name.Length > 2 ? invocation.Name[..2] : invocation.Name,
                    StringComparison.OrdinalIgnoreCase))
                .GroupBy(pair => pair.Value)
                .Select(group => group.Any(pair => pair.Key == group.Key.Name) ? group.Key.Name : group.First().Key)
                .Order(StringComparer.Ordinal).ToArray();
            return CommandOutput.Error($"Unknown command /{invocation.Name}." +
                (near.Length > 0 ? $" Did you mean {string.Join(", ", near.Select(name => "/" + name))}?" : "") + " /help lists them.");
        }
        try
        {
            return await command.Run(context, invocation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // A command's own refusal (a bad argument, a model that cannot load) is shown, not thrown at the surface.
            return CommandOutput.Error($"/{command.Name}: {exception.Message}");
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex NamePattern();
}
