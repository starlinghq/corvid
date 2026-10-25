using System.Globalization;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Kernel.Jobs;
using Corvid.Kernel.Sessions;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Spectre.Console;

namespace Corvid.Cli.Chat;

/// <summary>
/// <c>corvid chat</c>: a scrollback-first conversation (plan §12). Each line is a message or a slash command; a message
/// runs one turn, whose answer streams into the scrollback while a live panel shows its tools and approvals are asked
/// inline. Ctrl-C cancels a running turn, and leaves when none is running.
/// </summary>
internal static class ChatCommand
{
    // How long leaving waits for background work (a title being written) before cancelling it.
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(3);

    public static async Task<int> RunAsync(IHost host, CliArguments arguments, CliContext context, CancellationToken cancellationToken)
    {
        var console = context.Console;
        await CorvidCli.StartAsync(host, cancellationToken).ConfigureAwait(false);
        var factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();

        CorvidRuntime runtime;
        try
        {
            runtime = console.Profile.Capabilities.Interactive
                ? await console.Status().StartAsync("Starting Corvid…", status => factory.StartAsync(new StartupReport(console, status), cancellationToken))
                    .ConfigureAwait(false)
                : await factory.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ModelsUnavailableException exception)
        {
            ModelCommand.ReportMissing(console, exception.Missing);
            return ExitCodes.ModelsMissing;
        }

        await using (runtime.ConfigureAwait(false))
        {
            CorvidSession session;
            // A5: a child session is its parent's record: read-only, never driven from a terminal.
            if (arguments.Option("--session") is { } wanted && ChildParent(runtime, wanted) is { } parentOfChild)
            {
                console.MarkupLine($"[red]{Markup.Escape(TerminalText.Visible(wanted))} is a child session of {Markup.Escape(parentOfChild)}: it is read-only.[/]");
                console.MarkupLine($"[grey]`corvid chat --session {Markup.Escape(parentOfChild)}` then `/agents show {Markup.Escape(TerminalText.Visible(wanted))}` shows it.[/]");
                return ExitCodes.Failure;
            }
            try
            {
                session = runtime.OpenSession(arguments.Option("--session"), "cli");
            }
            catch (SessionHeldException exception)
            {
                // G-12: one driver per session. Another terminal or the app has it; this one may not post into it.
                console.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
                console.MarkupLine("[grey]Resume it here once that process exits; meanwhile the windowed app can follow it read-only.[/]");
                return ExitCodes.Failure;
            }
            var renderer = new ChatRenderer(console, verbose: arguments.Flag("--verbose"));
            using var feed = runtime.Feed.Subscribe(session.Id, renderer);
            using var prompter = runtime.AttachPrompter(session.Id, new CliApprovalPrompter(renderer, context.Input,
                (prompt, token) => DelegationCard.LinesAsync(runtime, prompt, token)));
            // A5: this session's children, the agent's and /delegate's, as they start and end.
            void OnChild(string parentId, SessionStreamUpdate update)
            {
                if (parentId != session.Id)
                    return;
                if (update is ChildStarted started)
                    renderer.Notice($"[grey]↳ {Markup.Escape(AgentCommands.Started(started.Child))}[/]");
                else if (update is ChildEnded ended)
                    renderer.Notice($"[grey]↳ {Markup.Escape(AgentCommands.Ended(ended.Child))}[/]");
            }
            runtime.ChildChanged += OnChild;
            // Scheduled runs happen in their own sessions; until the gateway delivers them (C-020), the chat says that they ran.
            void OnJob(ScheduledJob job, JobRun run) =>
                renderer.Notice($"[grey]⏰ job {Markup.Escape(job.Name)}: {Markup.Escape(run.Status)} (session {Markup.Escape(run.SessionId)})[/]");
            renderer.Banner(session.Id, session.Slots.Current(ModelSlots.Generator).Profile.Id, runtime.Tools.List().Count);
            foreach (var notice in runtime.DeciderNotices)
                renderer.Notice($"[yellow]{Markup.Escape(notice)}[/]");
            if (session.Snapshot().Head > 0)
                await ResumeAsync(runtime, session, renderer, console, cancellationToken).ConfigureAwait(false);
            // G-7: seen now, so a recap on the next return covers only what happens after.
            Shared(() => runtime.UiState.MarkSeen(session.Id, runtime.Log.GetHead(session.Id), DateTimeOffset.UtcNow));
            // After the banner: runs that finished since the runtime started are replayed here (C-039), then later ones live.
            runtime.JobCompleted += OnJob;
            // Background problems (the egress ledger) as notices through the renderer, never written mid-panel by their thread.
            void OnNotice(string line) => renderer.Notice($"[yellow]{Markup.Escape(line)}[/]");
            runtime.Notice += OnNotice;
            // A5: a slot that moves on its own (escalation, a fallback, the privacy gate, the turn's end) says so; the person's
            // own switch is said by /model's reply.
            void OnModel(string id, ModelChanged change)
            {
                if (id == session.Id && ChatRenderer.ModelNotice(change) is { } notice)
                    renderer.Notice(notice);
            }
            runtime.ModelChanged += OnModel;

            using var leave = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task<Delivery>? running = null;
            void OnInterrupt()
            {
                // Ctrl-C during a turn cancels the turn; at the prompt it leaves.
                if (Volatile.Read(ref running) is { IsCompleted: false })
                {
                    renderer.Cancelled();
                    session.Cancel();
                }
                else
                {
                    leave.Cancel();
                }
            }
            // The next line, echoed when the terminal did not show it; null at the end of input or when Ctrl-C leaves.
            async Task<string?> ReadAsync()
            {
                string? line;
                try
                {
                    line = await context.Input.ReadLineAsync(cancellationToken: leave.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (leave.IsCancellationRequested)
                {
                    return null;
                }
                if (line is not null && !context.Input.Echoes)
                    renderer.Echo(line);
                return line;
            }
            // A5: while a turn runs, the lines a turn can take now (/model) are read and run at once; anything else typed then
            // waits for the turn's end as before, and an approval prompt open meanwhile gets every line first.
            async Task WatchTurnAsync(CancellationToken turnEnded)
            {
                try
                {
                    while (await context.Input.ReadDuringTurnAsync(ChatRenderer.TakesMidTurn, turnEnded).ConfigureAwait(false) is { } command)
                    {
                        if (!context.Input.Echoes)
                            renderer.Echo(command);
                        // The switch itself is not cut short by the turn's end, only by leaving.
                        var output = await commands.ExecuteAsync(command, new SlashCommandContext(runtime, session, commands), leave.Token)
                            .ConfigureAwait(false);
                        renderer.Output(output);
                    }
                }
                catch (OperationCanceledException) when (turnEnded.IsCancellationRequested)
                {
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    renderer.Notice($"[red]{Markup.Escape(exception.Message)}[/]");
                }
            }
            context.Interrupts.Interrupted += OnInterrupt;
            try
            {
                // A line typed at a pick prompt that is not a pick is handled as if typed at the ordinary prompt.
                string? next = null;
                // K-6: the question the last turn ended on; the next message answers it, a number picking one of its choices.
                ClarifyingQuestion? question = null;
                while (true)
                {
                    string? line;
                    if (next is not null)
                    {
                        (line, next) = (next, null);
                    }
                    else
                    {
                        renderer.Prompt();
                        line = await ReadAsync().ConfigureAwait(false);
                        if (line is null)
                            break;
                    }
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (SlashCommandRegistry.IsCommand(line))
                    {
                        var output = await commands.ExecuteAsync(line, new SlashCommandContext(runtime, session, commands), leave.Token)
                            .ConfigureAwait(false);
                        renderer.Output(output);
                        if (output.Exit)
                            break;
                        if (output.Choices.IsEmpty)
                            continue;
                        // A numbered list (/model) asks for a number: Enter keeps things as they are.
                        renderer.Choose(output);
                        var answer = await ReadAsync().ConfigureAwait(false);
                        if (answer is null)
                            break;
                        if (output.Choices.FirstOrDefault(choice => choice.Key == answer.Trim()) is not { } chosen)
                        {
                            next = answer;
                            continue;
                        }
                        output = await commands.ExecuteAsync(chosen.Command, new SlashCommandContext(runtime, session, commands), leave.Token)
                            .ConfigureAwait(false);
                        renderer.Output(output);
                        if (output.Exit)
                            break;
                        continue;
                    }

                    // A4: the composer's ! and # lines (docs/plan/tui.md §4.1), read as the app reads them.
                    var mode = ComposerLine.Parse(line);
                    if (mode is ComposerLine.Empty empty)
                    {
                        renderer.Notice($"[yellow]{Markup.Escape(empty.Message)}[/]");
                        continue;
                    }
                    if (mode is ComposerLine.Memory memory)
                    {
                        renderer.Output(await RememberAsync(session, memory.Text, leave.Token).ConfigureAwait(false));
                        continue;
                    }
                    // With a question open the next line is its answer: a ! line would run its command as that answer.
                    if (mode is ComposerLine.Shell && question is not null)
                    {
                        renderer.Notice("[yellow]A question is open: answer it first, then send the ! line. Nothing was sent.[/]");
                        continue;
                    }
                    // A \# or \! line is a message, sent without its backslash.
                    if (mode is null)
                        line = ComposerLine.MessageText(line);
                    line = ChatRenderer.Answer(question, line);
                    question = null;

                    renderer.BeginTurn();
                    // A ! line reports its own turn's result only (security L1): the log's head before the line is posted.
                    var before = mode is ComposerLine.Shell ? session.Snapshot().Head : 0;
                    var turn = mode is ComposerLine.Shell shell
                        ? await session.PostShellAsync(line.Trim(), new ShellLine(shell.Command, shell.LocalOnly), cancellationToken).ConfigureAwait(false)
                        : session.SendAsync(line, cancellationToken);
                    Volatile.Write(ref running, turn);
                    Delivery delivery;
                    // A5: /model typed while the turn runs switches this session; the turn takes it at its next step.
                    using var turnInput = CancellationTokenSource.CreateLinkedTokenSource(leave.Token);
                    var watching = WatchTurnAsync(turnInput.Token);
                    try
                    {
                        delivery = await turn.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    finally
                    {
                        await turnInput.CancelAsync().ConfigureAwait(false);
                        await watching.ConfigureAwait(false);
                    }
                    string? shellStatus = null;
                    // Only a turn that issued the call has a result to report (a refused line, say the shell tool is off, issued none).
                    if (mode is ComposerLine.Shell ran && delivery is { Error: null, Result.ToolCalls: > 0 })
                    {
                        var (output, exit, error) = ComposerLine.LastShellResult(session.Snapshot(), before);
                        shellStatus = renderer.ShellResult(output, exit, ran.LocalOnly, error);
                    }
                    renderer.EndTurn(delivery, shellStatus);
                    question = delivery.Result?.Question;
                }
            }
            finally
            {
                context.Interrupts.Interrupted -= OnInterrupt;
                runtime.JobCompleted -= OnJob;
                runtime.Notice -= OnNotice;
                runtime.ModelChanged -= OnModel;
                runtime.ChildChanged -= OnChild;
            }
            console.MarkupLine($"[grey]Session {Markup.Escape(session.Id)} is in the log; resume it with `corvid chat --session {Markup.Escape(session.Id)}`.[/]");
            // The title the local generator writes after the first exchange lands before the runtime goes, if it comes within
            // a few seconds; otherwise the wait cancels it, and the next exchange queues it again.
            await runtime.MaintenanceIdleAsync(ExitWait).ConfigureAwait(false);
            Shared(() => runtime.UiState.MarkSeen(session.Id, runtime.Log.GetHead(session.Id), DateTimeOffset.UtcNow));
        }
        return ExitCodes.Success;
    }

    /// <summary>
    /// A resumed session (G-6, G-7): its title and size, the draft the app kept for it in <c>ui.db</c> (the chat reads lines,
    /// so the draft is shown rather than put back), and a recap when the person has been away longer than
    /// <c>Corvid:Sessions:RecapAfterMinutes</c>.
    /// </summary>
    private static async Task ResumeAsync(CorvidRuntime runtime, CorvidSession session, ChatRenderer renderer, IAnsiConsole console,
        CancellationToken cancellationToken)
    {
        var description = SessionCatalogue.Describe(runtime.Log, session.Id, cancellationToken);
        var title = description?.Title is { } named ? $" Title: \"{named}\" (/rename changes it)." : "";
        renderer.Notice($"[grey]Resumed: {session.Snapshot().Transcript.Length} entries in this session's log.{Markup.Escape(title)}[/]");
        var draft = Shared(() => runtime.UiState.GetDraft(session.Id));
        if (draft is { Text.Length: > 0 })
            renderer.Notice($"[grey]A draft from the app is waiting for this session: {Markup.Escape(SessionCatalogue.Line(draft.Text) ?? "")}[/]");
        var seen = Shared(() => runtime.UiState.LastSeen(session.Id));
        if (description is null || !runtime.Titles.ShouldRecapOnReturn(description, seen?.At))
            return;
        try
        {
            var recap = console.Profile.Capabilities.Interactive
                ? await console.Status().StartAsync("Recapping what happened while you were away…",
                    _ => runtime.RecapAsync(session.Id, RecapSources.Absence, cancellationToken)).ConfigureAwait(false)
                : await runtime.RecapAsync(session.Id, RecapSources.Absence, cancellationToken).ConfigureAwait(false);
            if (recap is not null)
                renderer.Output(new CommandOutput
                {
                    Title = "While you were away",
                    Lines = [.. SessionCatalogue.Visible(recap.Text).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
                });
        }
        catch (InvalidOperationException exception)
        {
            renderer.Notice($"[grey]No recap: {Markup.Escape(exception.Message)}[/]");
        }
    }

    /// <summary>A <c>#</c> line through memory's gated write path, as command output.</summary>
    private static async Task<CommandOutput> RememberAsync(CorvidSession session, string fact, CancellationToken cancellationToken)
    {
        try
        {
            var (text, problem) = ComposerLine.Describe(await session.RememberAsync(fact, cancellationToken).ConfigureAwait(false), fact);
            return problem ? CommandOutput.Warning(text) : CommandOutput.Success(text);
        }
        catch (InvalidOperationException exception)
        {
            return CommandOutput.Warning(exception.Message);
        }
        catch (ArgumentException exception)
        {
            // The extractor refused the text (too long).
            return CommandOutput.Warning("Not remembered: " + exception.Message);
        }
    }

    // ui.db is a convenience: a file another process holds too long, or a read-only data directory, never stops the chat.
    /// <summary>A5: the parent of a child session (whose id starts <c>child-</c> and whose log starts with <c>ChildOf</c>), else null.</summary>
    internal static string? ChildParent(CorvidRuntime runtime, string sessionId) =>
        sessionId.StartsWith(ChildSessions.Surface + "-", StringComparison.Ordinal)
        && Corvid.Kernel.Events.SessionProjection.Load(runtime.Log, sessionId).Parent is { } parent
            ? parent.ParentId
            : null;

    private static void Shared(Action action) => Shared<object?>(() =>
    {
        action();
        return null;
    });

    private static T? Shared<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception exception) when (Corvid.Host.State.UiStateStore.IsUnavailable(exception))
        {
            return default;
        }
    }

    /// <summary>
    /// The start, step by step: the spinner names the step under way (loading a model takes seconds), and each finished step
    /// leaves a line with its time in the scrollback, so a slow start says where the time went.
    /// </summary>
    internal sealed class StartupReport(IAnsiConsole console, StatusContext status) : IProgress<StartupStep>
    {
        public void Report(StartupStep value)
        {
            if (value.Elapsed is { } elapsed)
                console.MarkupLine(string.Create(CultureInfo.InvariantCulture,
                    $"[green]✓[/] {Markup.Escape(value.Description)} [grey]({elapsed.TotalSeconds:0.0}s)[/]"));
            else
                status.Status = $"{Markup.Escape(value.Description)}…";
        }
    }
}
