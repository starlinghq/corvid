using Corvid.Host.Commands;
using Corvid.Host.Composition;
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
                ? await console.Status().StartAsync("Loading models…", _ => factory.StartAsync(cancellationToken)).ConfigureAwait(false)
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
            renderer.Banner(session.Id, session.Slots.Current(ModelSlots.Generator).Profile.Id, runtime.Tools.List().Count);
            if (session.Snapshot().Head > 0)
                renderer.Notice($"[grey]Resumed: {session.Snapshot().Transcript.Length} entries in this session's log.[/]");

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
            context.Interrupts.Interrupted += OnInterrupt;
            try
            {
                while (true)
                {
                    renderer.Prompt();
                    string? line;
                    try
                    {
                        line = await context.Input.ReadLineAsync(cancellationToken: leave.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (leave.IsCancellationRequested)
                    {
                        break;
                    }
                    if (line is null)
                        break;
                    if (!context.Input.Echoes)
                        renderer.Echo(line);
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    if (SlashCommandRegistry.IsCommand(line))
                    {
                        var output = await commands.ExecuteAsync(line, new SlashCommandContext(runtime, session, commands), leave.Token)
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
            }
            console.MarkupLine($"[grey]Session {Markup.Escape(session.Id)} is in the log; resume it with `corvid chat --session {Markup.Escape(session.Id)}`.[/]");
            await runtime.MaintenanceIdleAsync().ConfigureAwait(false);
        }
        return ExitCodes.Success;
    }
}
