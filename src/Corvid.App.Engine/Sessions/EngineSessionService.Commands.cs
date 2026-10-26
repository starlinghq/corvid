using System.Collections.Immutable;
using Corvid.App.Core;
using Corvid.App.Core.Sessions;
using Corvid.Host.Commands;
using Corvid.Kernel.Events;
using AppTone = Corvid.App.Core.Sessions.OutputTone;
using HostTone = Corvid.Host.Commands.OutputTone;
using LoggedCompacted = Corvid.Abstractions.Events.Compacted;
using LoggedGoal = Corvid.Abstractions.Events.GoalSet;
using LoggedRewound = Corvid.Abstractions.Events.SessionRewound;

namespace Corvid.App.Engine.Sessions;

public sealed partial class EngineSessionService
{
    /// <summary>
    /// Runs a slash command line through the registry every surface shares (plan §12), in the session it was typed in. Its
    /// output is a <see cref="CommandResult"/> card; what it wrote to the log beside it (a goal, a compaction) reaches the
    /// transcript as the same updates the page's own controls produce.
    /// </summary>
    public async Task RunCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        if (!SlashCommandRegistry.IsCommand(line))
            throw new ArgumentException("A command starts with / and its name, such as /help.", nameof(line));
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var channel = await LoadAsync(sessionId, cancellationToken).ConfigureAwait(false);
        Writable(channel);
        var session = await SessionAsync(runtime, sessionId, cancellationToken).ConfigureAwait(false);
        var registry = Registry;
        var invocation = SlashCommandRegistry.Parse(line);
        if (registry.TryGet(invocation.Name, out var command) && command.Name == "cancel")
            lock (channel.Gate)
                if (channel.Reporter is not null)
                    channel.Cancelling = true;

        // A6 (G-4): /workspace new and remove ask the person before writing a worktree; outside a turn, on this page's card.
        using var cards = invocation.Name == "workspace" ? await AttachCardsAsync(runtime, channel).ConfigureAwait(false) : null;

        // Commands read and write the log (SQLite): off the UI thread.
        var (output, logged) = await Task.Run(async () =>
        {
            var head = runtime.Log.GetHead(sessionId, cancellationToken);
            var result = await registry.ExecuteAsync(line, new SlashCommandContext(runtime, session, registry), cancellationToken).ConfigureAwait(false);
            return (result, runtime.Log.Read(sessionId, head, cancellationToken: cancellationToken));
        }, cancellationToken).ConfigureAwait(false);

        var at = _time.GetUtcNow();
        Publish(channel, translator =>
        {
            var updates = new List<SessionUpdate> { Result(line.Trim(), output, at) };
            foreach (var item in logged)
            {
                switch (item.Payload)
                {
                    case LoggedGoal goal:
                        updates.Add(new GoalChanged(goal.Goal) { At = at });
                        break;
                    case LoggedCompacted compacted:
                        updates.Add(new CompactionUpdate(Math.Max(1, translator.TurnAt(compacted.FromSequence)), Math.Max(1, translator.TurnAt(compacted.ThroughSequence)))
                        {
                            At = at,
                            Goal = compacted.Summary.Goal,
                            Constraints = compacted.Summary.Constraints.IsDefault ? [] : compacted.Summary.Constraints,
                            Decisions = compacted.Summary.Decisions.IsDefault ? [] : compacted.Summary.Decisions,
                            Files = compacted.Summary.Files.IsDefault ? [] : compacted.Summary.Files,
                            OpenQuestions = compacted.Summary.OpenQuestions.IsDefault ? [] : compacted.Summary.OpenQuestions,
                            NextStep = compacted.Summary.NextStep,
                            Focus = invocation.Name == "compact" && invocation.Text.Length > 0 ? invocation.Text : null,
                        });
                        break;
                    case LoggedRewound:
                        // The transcript's rewind marker is Rewind's to draw from the ledger (A3 history); the session shows
                        // the conversation as it now stands when it is opened again.
                        updates.Add(new Notice(NoticeKind.Info, "The session was rewound: open it again to see the conversation as it stands.")
                        {
                            At = at,
                        });
                        break;
                }
            }
            return updates;
        });
        if (_engine.Runtime.IsCompletedSuccessfully)
            Refresh(runtime, sessionId);
    }

    /// <summary>The commands the composer's palette offers: the registry's, without leaving, which is the window's business.</summary>
    private ImmutableArray<SlashCommandInfo> Commands() =>
        [.. Registry.Commands.Where(command => command.Name is not ("exit" or "quit"))
            .Select(command => new SlashCommandInfo(command.Name, command.Summary, command.Usage))];

    /// <summary>A command's output as the transcript's card: its lines, its table, and its numbered choices as lines.</summary>
    internal static CommandResult Result(string line, CommandOutput output, DateTimeOffset at)
    {
        var lines = output.Lines.IsDefault ? [] : output.Lines;
        if (!output.Choices.IsDefaultOrEmpty)
            lines = lines.AddRange(output.Choices.Select(choice => $"{choice.Key}. {choice.Label} ({choice.Command})"));
        return new CommandResult(line)
        {
            At = at,
            Title = output.Title,
            Lines = lines,
            Columns = output.Table?.Columns ?? [],
            Rows = output.Table?.Rows ?? [],
            Tone = output.Tone switch
            {
                HostTone.Success => AppTone.Success,
                HostTone.Warning => AppTone.Warning,
                HostTone.Error => AppTone.Error,
                _ => AppTone.Normal,
            },
        };
    }
}
