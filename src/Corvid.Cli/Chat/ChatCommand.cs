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
            var session = runtime.OpenSession(arguments.Option("--session"), "cli");
            var renderer = new ChatRenderer(console, verbose: arguments.Flag("--verbose"));
            using var feed = runtime.Feed.Subscribe(session.Id, renderer);
            using var prompter = runtime.AttachPrompter(session.Id, new CliApprovalPrompter(renderer, context.Input));
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
                    line = ChatRenderer.Answer(question, line);
                    question = null;

                    renderer.BeginTurn();
                    var turn = mode is ComposerLine.Shell shell
                        ? await session.PostShellAsync(line.Trim(), new ShellLine(shell.Command, shell.LocalOnly), cancellationToken).ConfigureAwait(false)
                        : session.SendAsync(line, cancellationToken);
                    Volatile.Write(ref running, turn);
                    Delivery delivery;
                    try
                    {
                        delivery = await turn.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                    if (mode is ComposerLine.Shell ran && delivery.Error is null)
                    {
                        var (output, exit) = ComposerLine.LastShellResult(session.Snapshot());
                        renderer.ShellResult(output, exit, ran.LocalOnly);
                    }
                    renderer.EndTurn(delivery);
                    question = delivery.Result?.Question;
                }
            }
            finally
            {
                context.Interrupts.Interrupted -= OnInterrupt;
                runtime.JobCompleted -= OnJob;
            }
            console.MarkupLine($"[grey]Session {Markup.Escape(session.Id)} is in the log; resume it with `corvid chat --session {Markup.Escape(session.Id)}`.[/]");
            // The title the local generator writes after the first exchange lands before the runtime goes.
            await runtime.MaintenanceIdleAsync().ConfigureAwait(false);
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
        catch (Exception exception) when (exception is System.Data.Common.DbException or IOException or UnauthorizedAccessException
            or InvalidDataException)
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
