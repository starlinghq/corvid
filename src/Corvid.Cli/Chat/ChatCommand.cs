using System.Globalization;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Kernel.Jobs;
using Corvid.Kernel.Sessions;
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
            CorvidSession session;
            try
            {
                session = runtime.OpenSession(arguments.Option("--session"), "cli");
            }
            catch (SessionHeldException exception)
            {
                // G-12: one driver per session. Another terminal or the app has it; this one may not post into it.
                console.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
                console.MarkupLine("[grey]The app shows it read-only and follows it as it grows; resume it here once that process exits.[/]");
                return ExitCodes.Failure;
            }
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
                renderer.Notice($"[grey]Resumed: {session.Snapshot().Transcript.Length} entries in this session's log.[/]");
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

                    renderer.BeginTurn();
                    var turn = session.SendAsync(line, cancellationToken);
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
                    renderer.EndTurn(delivery);
                }
            }
            finally
            {
                context.Interrupts.Interrupted -= OnInterrupt;
                runtime.JobCompleted -= OnJob;
            }
            console.MarkupLine($"[grey]Session {Markup.Escape(session.Id)} is in the log; resume it with `corvid chat --session {Markup.Escape(session.Id)}`.[/]");
            await runtime.MaintenanceIdleAsync().ConfigureAwait(false);
        }
        return ExitCodes.Success;
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
