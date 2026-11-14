using Corvid.Abstractions.Events;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Kernel.Events;
using Corvid.Kernel.Flocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Spectre.Console;

namespace Corvid.Cli;

/// <summary>
/// <c>corvid flock</c> (C-042): flocks from a terminal, beside the chats that are their members. It works on the event log
/// alone (no model is loaded), so it runs while those chats run. <c>watch</c> is the terminal's view of a whole flock:
/// every member's messages as they come, and each line typed sent to all of them (or to one, <c>@name</c> first). Delivery
/// is the members' own processes' work: a message to a member nobody has open waits until it is opened.
/// </summary>
internal static class FlockCommand
{
    public const string UsageText = """
        usage: corvid flock [list] [--all]
               corvid flock show <flock> [--all]
               corvid flock watch <flock>
               corvid flock new <name>
               corvid flock say <flock> [@member] <text>
               corvid flock add <flock> <session> [--as <name>]
               corvid flock leave <flock> <session>
               corvid flock resume <flock> | close <flock>
        """;

    /// <summary>How often <c>watch</c> reads the flock.</summary>
    internal static TimeSpan WatchInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    public static async Task<int> RunAsync(IHost host, CliArguments arguments, CliContext context, CancellationToken cancellationToken)
    {
        var console = context.Console;
        var options = host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;
        var store = new FlockStore(new SqliteEventLog(CorvidPaths.EventLog(options)));
        LockState Driver(string sessionId) => ProcessLock.Probe(CorvidRuntime.DriverLockPath(options, sessionId), withoutNote: true).State;
        var all = arguments.Flag("--all");
        try
        {
            switch (arguments.Positional("--as"))
            {
                case [] or ["list"]:
                    return List(console, store, all);
                case ["show", var name]:
                    var shown = Find(store, name);
                    console.MarkupLine($"[bold]Flock {Markup.Escape(TerminalText.Visible(shown.Name))}[/] [grey]({shown.Id})[/]");
                    foreach (var line in FlockCommands.Describe(shown, null, all ? int.MaxValue : 20, Driver))
                        console.MarkupLine(Markup.Escape(line));
                    return ExitCodes.Success;
                case ["watch", var name]:
                    return await WatchAsync(context, store, Find(store, name), Driver, cancellationToken).ConfigureAwait(false);
                case ["new", var name]:
                    var created = store.Create(name);
                    console.MarkupLine($"[green]Started the flock {Markup.Escape(TerminalText.Visible(created.Name))}[/] [grey]({created.Id})[/]. "
                        + $"Sessions join with `corvid chat --flock {Markup.Escape(created.Name)} --as <name>` or /flock join.");
                    return ExitCodes.Success;
                case ["say", var name, ..]:
                    var flock = Find(store, name);
                    var words = string.Join(' ', arguments.Positional("--as").Skip(2));
                    Say(console, store, flock, words);
                    return ExitCodes.Success;
                case ["add", var name, var session]:
                    if (store.Log.GetHead(session) == 0)
                        throw new FlockException($"There is no session {session}. corvid chat --session <id> resumes one; /sessions lists them.");
                    var joined = store.Join(Find(store, name).Id, session, arguments.Option("--as") ?? CorvidRuntime.MemberName(store.Log, session));
                    console.MarkupLine($"[green]Added {Markup.Escape(session)} as {Markup.Escape(TerminalText.Visible(joined.Member(session)!.Name))}.[/]"
                        + (Driver(session) == LockState.Free ? " [grey]Nobody has it open, so its messages wait until it is.[/]" : ""));
                    return ExitCodes.Success;
                case ["leave", var name, var session]:
                    store.Leave(Find(store, name).Id, session);
                    console.MarkupLine($"[green]{Markup.Escape(session)} left the flock.[/]");
                    return ExitCodes.Success;
                case ["resume", var name]:
                    store.Resume(Find(store, name).Id);
                    console.MarkupLine("[green]Resumed: the members' messages are delivered again.[/]");
                    return ExitCodes.Success;
                case ["close", var name]:
                    var closing = Find(store, name);
                    store.Close(closing.Id);
                    console.MarkupLine($"[green]Closed the flock {Markup.Escape(TerminalText.Visible(closing.Name))}.[/] [grey]Its log stays: corvid flock show {closing.Id}[/]");
                    return ExitCodes.Success;
                default:
                    console.MarkupLine($"[red]{Markup.Escape(UsageText)}[/]");
                    return ExitCodes.Usage;
            }
        }
        catch (FlockException refused)
        {
            console.MarkupLine($"[red]{Markup.Escape(TerminalText.Visible(refused.Message))}[/]");
            return ExitCodes.Failure;
        }
    }

    private static FlockState Find(FlockStore store, string name) =>
        store.Find(name) ?? throw new FlockException($"There is no flock {name}. corvid flock list shows the open ones.");

    private static int List(IAnsiConsole console, FlockStore store, bool all)
    {
        var flocks = store.List(includeClosed: all);
        if (flocks.Count == 0)
        {
            console.MarkupLine("[grey]No flock is open. corvid flock new <name>, or corvid chat --flock <name>, starts one.[/]");
            return ExitCodes.Success;
        }
        foreach (var flock in flocks)
            console.MarkupLine($"[bold]{Markup.Escape(TerminalText.Visible(flock.Name))}[/] [grey]{flock.Id}[/]  "
                + Markup.Escape(string.Join(", ", flock.Members.Select(member => TerminalText.Visible(member.Name))))
                + $"  [grey]{flock.Messages.Length} messages{(flock.Paused ? ", paused" : "")}{(flock.Closed ? ", closed" : "")}[/]");
        return ExitCodes.Success;
    }

    private static void Say(IAnsiConsole console, FlockStore store, FlockState flock, string text)
    {
        var (to, body) = FlockCommands.Addressed(flock, text);
        if (body.Length == 0)
            throw new FlockException("Nothing to send: corvid flock say <flock> [@member] <text>.");
        store.Say(flock.Id, FlockSessions.User, body, to, tainted: false);
        console.MarkupLine($"[grey]Sent to {(to.Count == 0 ? "everyone" : Markup.Escape(TerminalText.Visible(flock.Member(to.First())!.Name)))}.[/]");
    }

    // Prints the flock, then what it logs as it comes (messages, members joining and leaving, a pause), and sends each line
    // typed to its members, until the end of input or Ctrl-C.
    private static async Task<int> WatchAsync(CliContext context, FlockStore store, FlockState flock, Func<string, LockState> driver,
        CancellationToken cancellationToken)
    {
        var console = context.Console;
        console.MarkupLine($"[bold]Flock {Markup.Escape(TerminalText.Visible(flock.Name))}[/] [grey]({flock.Id}) — type to write to every member, @name to one; Ctrl-C leaves.[/]");
        foreach (var line in FlockCommands.Describe(flock, null, 20, driver))
            console.MarkupLine(Markup.Escape(line));
        var shown = flock.Messages.IsEmpty ? 0 : flock.Messages[^1].Sequence;
        var members = flock.Members.Select(member => member.SessionId).ToHashSet(StringComparer.Ordinal);
        var paused = flock.Paused;
        Task<string?>? reading = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                reading ??= context.Input.ReadLineAsync(cancellationToken: cancellationToken).AsTask();
                await Task.WhenAny(reading, Task.Delay(WatchInterval, cancellationToken)).ConfigureAwait(false);
                if (reading.IsCompleted)
                {
                    var typed = await reading.ConfigureAwait(false);
                    reading = null;
                    if (typed is null)
                        break;
                    if (!string.IsNullOrWhiteSpace(typed))
                    {
                        try
                        {
                            Say(console, store, store.Get(flock.Id) ?? flock, typed);
                        }
                        catch (FlockException refused)
                        {
                            console.MarkupLine($"[yellow]{Markup.Escape(TerminalText.Visible(refused.Message))}[/]");
                        }
                    }
                }
                if (store.Get(flock.Id) is not { } now || now.Head == flock.Head)
                    continue;
                flock = now;
                foreach (var member in flock.Members.Where(member => members.Add(member.SessionId)))
                    console.MarkupLine($"[grey]{Markup.Escape(TerminalText.Visible(member.Name))} ({member.SessionId}) joined.[/]");
                foreach (var gone in members.Where(id => flock.Member(id) is null).ToList())
                {
                    members.Remove(gone);
                    console.MarkupLine($"[grey]{Markup.Escape(gone)} left.[/]");
                }
                foreach (var entry in flock.Messages.Where(entry => entry.Sequence > shown))
                {
                    console.MarkupLine(entry.Message.From == FlockSessions.User ? $"[cyan]{Markup.Escape(FlockCommands.Line(flock, entry))}[/]" : Markup.Escape(FlockCommands.Line(flock, entry)));
                    shown = entry.Sequence;
                }
                if (flock.Paused != paused)
                {
                    paused = flock.Paused;
                    console.MarkupLine(paused
                        ? $"[yellow]Paused: the members started {flock.PeerTurns} turns on each other without you. corvid flock resume {Markup.Escape(flock.Name)}, or write to them, lets them go on.[/]"
                        : "[grey]Delivering again.[/]");
                }
                if (flock.Closed)
                {
                    console.MarkupLine("[grey]The flock was closed.[/]");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        return ExitCodes.Success;
    }
}
