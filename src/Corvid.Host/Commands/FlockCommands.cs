using System.Collections.Immutable;
using System.Globalization;
using Corvid.Abstractions.Events;
using Corvid.Host.Composition;
using Corvid.Kernel.Flocks;

namespace Corvid.Host.Commands;

/// <summary>
/// <c>/flock</c> (C-042): a few of the user's sessions working on one task together. A session starts or joins a flock,
/// adds others, and leaves; the user writes to all of them at once (or to one with <c>@name</c>); the members write to each
/// other with <c>flock_send</c>. The same flock is open to <c>corvid flock</c> in another terminal and to the app.
/// </summary>
public static class FlockCommands
{
    public const string Usage = "[list | new <name> [as <me>] | join <flock> [as <me>] | add <session> [as <name>] | leave | say [@member] <text> | resume | close]";

    /// <summary>The latest messages <c>/flock</c> shows.</summary>
    private const int Shown = 8;

    internal static ValueTask<CommandOutput> Run(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        var runtime = context.Runtime;
        var session = context.Session;
        try
        {
            return ValueTask.FromResult(invocation.Arguments switch
            {
                [] => Show(runtime, session.Id),
                ["list"] => List(runtime),
                ["new", _, ..] => New(runtime, session.Id, Named(invocation.Arguments[1..]), As(invocation.Arguments[1..])),
                ["join", _, ..] => Join(runtime, session.Id, Named(invocation.Arguments[1..]), As(invocation.Arguments[1..])),
                ["add", var other, .. var rest] => Add(runtime, session.Id, other, As(rest)),
                ["leave"] => Leave(runtime, session.Id),
                ["say", ..] => Say(runtime, session.Id, invocation.Text.Trim()["say".Length..].Trim()),
                ["resume"] => Resume(runtime, session.Id),
                ["close"] => Close(runtime, session.Id),
                _ => CommandOutput.Error("Usage: /flock " + Usage + "."),
            });
        }
        catch (FlockException refused)
        {
            return ValueTask.FromResult(CommandOutput.Error(TerminalText.Visible(refused.Message)));
        }
    }

    // "<words> as <name>": the words before "as" (a name of several words), and the name after it (null when not given).
    private static string Named(ImmutableArray<string> words) =>
        string.Join(' ', words.TakeWhile(word => !string.Equals(word, "as", StringComparison.OrdinalIgnoreCase)));

    private static string? As(ImmutableArray<string> words) =>
        words.SkipWhile(word => !string.Equals(word, "as", StringComparison.OrdinalIgnoreCase)).Skip(1).ToList() is { Count: > 0 } name
            ? string.Join(' ', name)
            : null;

    private static FlockState Mine(CorvidRuntime runtime, string sessionId) =>
        runtime.Flocks.FlockOf(sessionId) ?? throw new FlockException("This session is not in a flock. /flock new <name> starts one; /flock join <name> joins one.");

    private static CommandOutput Show(CorvidRuntime runtime, string sessionId)
    {
        if (runtime.Flocks.FlockOf(sessionId) is not { } flock)
        {
            var open = runtime.Flocks.List();
            return CommandOutput.Text("This session is not in a flock. /flock new <name> starts one with it; " +
                (open.Count == 0 ? "no flock is open." : "open now: " + string.Join(", ", open.Select(item => TerminalText.Visible(item.Name))) + " (/flock join <name>)."));
        }
        return new CommandOutput { Title = "Flock " + TerminalText.Visible(flock.Name), Lines = [.. Describe(flock, sessionId, Shown, id => runtime.Driver(id).State)] };
    }

    /// <summary>
    /// A flock as <c>/flock</c> and <c>corvid flock show</c> print it: its members (with who drives each, by
    /// <paramref name="driver"/>, and "this session" for <paramref name="self"/>), whether it is paused, and its latest
    /// <paramref name="latest"/> messages.
    /// </summary>
    public static IEnumerable<string> Describe(FlockState flock, string? self, int latest, Func<string, LockState> driver)
    {
        yield return "Members: " + (flock.Members.IsEmpty ? "none" : string.Join(", ", flock.Members.Select(member =>
            TerminalText.Visible(member.Name) + " (" + (member.SessionId == self ? "this session" : member.SessionId + ", " + Driven(driver(member.SessionId))) + ")")));
        if (flock.Closed)
            yield return "It is closed: nothing more is delivered.";
        else if (flock.Paused)
            yield return $"Paused: its members started {flock.PeerTurns} turns on each other without you. /flock resume (or a message to the flock) lets them go on.";
        var messages = flock.Messages.TakeLast(latest).ToList();
        if (messages.Count == 0)
            yield return "No messages yet. /flock say <text> writes to every member; members write with flock_send.";
        if (flock.Messages.Length > messages.Count)
            yield return $"… {flock.Messages.Length - messages.Count} earlier messages (corvid flock show {flock.Name} --all).";
        foreach (var entry in messages)
            yield return Line(flock, entry);
    }

    /// <summary>One message on one line: "14:02 api → web: …", outside content marked.</summary>
    public static string Line(FlockState flock, FlockEntry entry)
    {
        var message = entry.Message;
        var to = message.To.IsDefaultOrEmpty ? "all" : string.Join(", ", message.To.Select(id => flock.Member(id)?.Name ?? id));
        var text = TerminalText.Visible(message.Text).Replace('\n', ' ');
        var waiting = flock.Members.Count(member => member.SessionId != message.From && entry.Sequence > member.JoinedAt
            && (message.To.IsDefaultOrEmpty || message.To.Contains(member.SessionId)) && !flock.WasDelivered(entry.Sequence, member.SessionId));
        return entry.At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture) + " " + TerminalText.Visible(flock.SenderName(message)) + " → " + TerminalText.Visible(to)
            + (message.Tainted ? " (outside content)" : "") + ": " + text + (waiting > 0 ? $"  [waiting for {waiting}]" : "");
    }

    // Who drives a member now: "here", "another process", or "not open" (its messages wait until it is opened).
    private static string Driven(LockState state) => state switch
    {
        LockState.HeldHere => "open here",
        LockState.HeldElsewhere => "open elsewhere",
        _ => "not open: its messages wait",
    };

    private static CommandOutput List(CorvidRuntime runtime)
    {
        var open = runtime.Flocks.List();
        if (open.Count == 0)
            return CommandOutput.Text("No flock is open. /flock new <name> starts one with this session.");
        return new CommandOutput
        {
            Title = "Flocks",
            Lines = [.. open.Select(flock => $"{TerminalText.Visible(flock.Name)} ({flock.Id}): " + string.Join(", ", flock.Members.Select(member => TerminalText.Visible(member.Name)))
                + $", {flock.Messages.Length} messages" + (flock.Paused ? ", paused" : ""))],
        };
    }

    private static CommandOutput New(CorvidRuntime runtime, string sessionId, string name, string? me)
    {
        if (runtime.Flocks.FlockOf(sessionId) is { } current)
            throw new FlockException($"This session is in the flock {current.Name}; /flock leave first.");
        var flock = runtime.Flocks.Create(name);
        flock = runtime.JoinFlock(flock.Id, sessionId, me);
        return CommandOutput.Success($"Started the flock {TerminalText.Visible(flock.Name)} with this session as {TerminalText.Visible(flock.Member(sessionId)!.Name)}. "
            + $"Add sessions with /flock add <session>, or `corvid chat --flock {flock.Name}` in another terminal.");
    }

    private static CommandOutput Join(CorvidRuntime runtime, string sessionId, string wanted, string? me)
    {
        var flock = runtime.Flocks.Find(wanted) ?? throw new FlockException($"There is no open flock {wanted}. /flock list shows them.");
        flock = runtime.JoinFlock(flock.Id, sessionId, me);
        return CommandOutput.Success($"This session is {TerminalText.Visible(flock.Member(sessionId)!.Name)} in the flock {TerminalText.Visible(flock.Name)}. " + Others(flock, sessionId));
    }

    private static CommandOutput Add(CorvidRuntime runtime, string sessionId, string other, string? name)
    {
        var flock = Mine(runtime, sessionId);
        if (runtime.Log.GetHead(other) == 0)
            throw new FlockException($"There is no session {other}. /sessions lists them.");
        flock = runtime.JoinFlock(flock.Id, other, name);
        return CommandOutput.Success($"Added {other} to the flock {TerminalText.Visible(flock.Name)} as {TerminalText.Visible(flock.Member(other)!.Name)}. "
            + (runtime.Driver(other).State == LockState.Free ? "Nobody has it open, so its messages wait until it is." : ""));
    }

    private static CommandOutput Leave(CorvidRuntime runtime, string sessionId)
    {
        var flock = Mine(runtime, sessionId);
        runtime.Flocks.Leave(flock.Id, sessionId);
        return CommandOutput.Success($"This session left the flock {TerminalText.Visible(flock.Name)}.");
    }

    private static CommandOutput Say(CorvidRuntime runtime, string sessionId, string text)
    {
        var flock = Mine(runtime, sessionId);
        var (to, body) = Addressed(flock, text);
        if (body.Length == 0)
            return CommandOutput.Error("Usage: /flock say [@member] <text>.");
        runtime.Flocks.Say(flock.Id, FlockSessions.User, body, to, tainted: false);
        return CommandOutput.Success(to.Count == 0 ? $"Sent to everyone in the flock {TerminalText.Visible(flock.Name)}." : $"Sent to {TerminalText.Visible(flock.Member(to.First())!.Name)}.");
    }

    /// <summary>
    /// The user's message with a leading <c>@name</c> taken as its one recipient: the member's session id and the text after
    /// it; no recipients (every member) without one.
    /// </summary>
    /// <exception cref="FlockException">The name is no member's.</exception>
    public static (IReadOnlyCollection<string> To, string Text) Addressed(FlockState flock, string text)
    {
        text = text.Trim();
        if (!text.StartsWith('@'))
            return ([], text);
        var end = text.IndexOfAny([' ', '\t', '\n']);
        var name = end < 0 ? text[1..] : text[1..end];
        var member = flock.Find(name) ?? throw new FlockException($"{name} is not a member of the flock {flock.Name}. " + Others(flock, null));
        return ([member.SessionId], end < 0 ? "" : text[end..].Trim());
    }

    private static CommandOutput Resume(CorvidRuntime runtime, string sessionId)
    {
        var flock = Mine(runtime, sessionId);
        runtime.Flocks.Resume(flock.Id);
        return CommandOutput.Success($"The flock {TerminalText.Visible(flock.Name)} goes on: its members' messages are delivered again.");
    }

    private static CommandOutput Close(CorvidRuntime runtime, string sessionId)
    {
        var flock = Mine(runtime, sessionId);
        runtime.Flocks.Close(flock.Id);
        return CommandOutput.Success($"Closed the flock {TerminalText.Visible(flock.Name)}. Its sessions go on alone; its log stays (corvid flock show {flock.Id}).");
    }

    private static string Others(FlockState flock, string? self)
    {
        var others = flock.Members.Where(member => member.SessionId != self).Select(member => TerminalText.Visible(member.Name)).ToList();
        return others.Count == 0 ? "Nobody else is in it yet." : "Members: " + string.Join(", ", others) + ".";
    }
}
