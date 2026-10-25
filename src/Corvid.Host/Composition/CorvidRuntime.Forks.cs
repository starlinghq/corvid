using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Host.Commands;
using Corvid.Host.Models;
using Corvid.Kernel.Sessions;
using Corvid.Models;

namespace Corvid.Host.Composition;

/// <summary>How <see cref="CorvidRuntime.ForkAsync"/> forks (G-15).</summary>
public sealed record ForkOptions
{
    /// <summary>
    /// The last position to copy. Null: the source's head, or, while one of its turns runs here, that turn's last completed
    /// step (<see cref="Kernel.Turns.TurnRunner.StepBoundary"/>).
    /// </summary>
    public long? Through { get; init; }

    /// <summary>The surface the fork is opened for; its id starts with it.</summary>
    public string Surface { get; init; } = "cli";

    public SessionSettings? Settings { get; init; }

    /// <summary>
    /// Models the person picked for the fork, one per slot, each made the fork's choice for the session (through the privacy
    /// gate with the fork's taint) over the source's. Only a model per slot: a fork's ladders and fallbacks are the
    /// configured ones, so a mix naming a ladder, fallbacks or <c>escalate: false</c> is refused.
    /// </summary>
    public ModelMix? Mix { get; init; }
}

/// <summary>A fork (G-15): the new session, the position copied through, and its model choices.</summary>
/// <param name="Through">The source's last event the fork holds; the fork's own events (its model choices) follow it.</param>
/// <param name="DuringTurn">A turn of the source was running here: the fork was taken at a step boundary without waiting.</param>
/// <param name="Tainted">The fork starts tainted, as its source was (plan §10.3).</param>
/// <param name="Models">
/// The fork's model choices as their switches answered: the source's session choices, then the picked mix's. A refused one
/// (the privacy gate) is here with its reason, and the slot stays where the fork started.
/// </param>
public sealed record ForkResult(CorvidSession Session, long Through, bool DuringTurn, bool Tainted, ImmutableArray<SwitchResult> Models);

public sealed partial class CorvidRuntime
{
    /// <summary>
    /// Forks a session at a log position (plan §3.2, G-15): a new session holding a copy of the source's events through
    /// <see cref="ForkOptions.Through"/>, with its own actor and driver lock. While a turn of the source runs here the fork
    /// does not wait for it: it copies through the turn's last completed step (every call issued by then has its result, no
    /// approval waits) and the turn goes on untouched; a position past that step is refused. Between turns it is taken
    /// between them, never splitting a turn; for a source another process drives, at the latest point where no call or
    /// approval is open. The fork starts with the source's taint and with the source's session model choices (its pins
    /// too) as they stood at the fork's position, as its own, then <see cref="ForkOptions.Mix"/>'s. While a session's first
    /// message, a <c>!</c> line, runs, there is nothing to fork yet.
    /// </summary>
    /// <exception cref="SessionForkException">
    /// The source has no such position, the position leaves a call or prompt open or is past the running turn's last step,
    /// or the source is a child session (its log is its parent's record: fork the parent).
    /// </exception>
    /// <exception cref="ArgumentException">The mix names a model that is not in the catalogue or cannot serve its slot, or a ladder.</exception>
    /// <exception cref="ModelUnavailableException">A model of the mix is not usable offline.</exception>
    public async Task<ForkResult> ForkAsync(string sourceId, ForkOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        options ??= new ForkOptions();
        // Checked before anything is written, so a bad pick leaves no fork behind.
        CheckForkMix(options.Mix);
        if (IsChildLog(sourceId, cancellationToken))
            throw new SessionForkException($"Session '{TerminalText.Visible(sourceId)}' is a child session, whose log is its parent's record; fork the parent instead.");

        var id = NewSessionId(options.Surface);
        var open = TryGetSession(sourceId, out var source);
        // Taint only rises within a turn; read before and after the copy, a turn that started meanwhile cannot clear it here.
        var tainted = open && source.Tainted;
        long through;
        var duringTurn = false;
        if (open && source.Runner.StepBoundary is { } boundary)
        {
            duringTurn = true;
            // A5 review: a `!` line that is the session's first message holds nothing before it (its boundary is 0), and
            // the line without its call is no resting point.
            if (boundary == 0)
                throw new SessionForkException($"Session '{TerminalText.Visible(sourceId)}' is running its first line, a shell command, " +
                    "and holds nothing before it to fork yet; fork once it finishes.");
            if (options.Through is { } asked && asked > boundary)
                throw new SessionForkException($"A turn of session '{TerminalText.Visible(sourceId)}' is running: it can be forked through " +
                    $"position {boundary}, its last completed step, or earlier, not {asked}.");
            through = SessionFork.Fork(Log, sourceId, id, options.Through ?? boundary, cancellationToken);
        }
        else if (open && source.Actor.TurnRunning)
        {
            // The turn has started and not yet logged its message (or has just ended): the log rests where it is.
            duringTurn = true;
            through = SessionFork.Fork(Log, sourceId, id, options.Through ?? SessionFork.LastRestingPoint(Log, sourceId, cancellationToken: cancellationToken),
                cancellationToken);
        }
        else if (open)
        {
            // Between turns (or under a restore): taken between them, so it never splits one.
            through = await source.Actor.RunExclusiveAsync(token => Task.FromResult(SessionFork.Fork(Log, sourceId, id, options.Through, token)),
                cancellationToken).ConfigureAwait(false);
        }
        else
        {
            through = SessionFork.Fork(Log, sourceId, id, options.Through ?? SessionFork.LastRestingPoint(Log, sourceId, cancellationToken: cancellationToken),
                cancellationToken);
        }
        tainted |= open && source.Tainted;

        var fork = OpenSession(id, options.Surface, options.Settings);
        if (tainted)
            fork.Runner.Taint();
        // The source's choices as they stood at the fork's position, not as they are now (A5 review): the copied log holds the
        // ones logged through it, and opening the fork restored those. A cloud choice is not restored without the privacy
        // gate, so it is chosen again here, through the gate with the fork's taint.
        var models = ImmutableArray.CreateBuilder<SwitchResult>();
        foreach (var (slot, choice) in ModelChoiceStates(id))
        {
            if (slot == ModelSlots.TextEmbedder || !ModelSlots.All.Contains(slot, StringComparer.Ordinal) || options.Mix?.Slots.ContainsKey(slot) == true)
                continue;
            if (fork.Slots.HasSessionChoice(slot))
            {
                var restored = fork.Slots.Current(slot).Profile;
                models.Add(new SwitchResult(SwitchStatus.Unchanged, slot, restored, restored, SwitchReason.Session) { Pinned = fork.Slots.IsPinned(slot) });
                continue;
            }
            if (Catalogue.TryFind(choice.Profile.Split('@')[0], out var model) && model.Serves(slot)
                && model.Profile.Privacy == PrivacyClass.Cloud && Catalogue.Check(model.Name).IsReady)
                models.Add(await fork.SetModelAsync(slot, model.Name, choice.Pinned ? SwitchScope.Pin : SwitchScope.Session, cancellationToken)
                    .ConfigureAwait(false));
        }
        foreach (var (slot, pick) in options.Mix?.Slots ?? ImmutableSortedDictionary<string, ModelMixSlot>.Empty)
            models.Add(await fork.SetModelAsync(slot, pick.Model, SwitchScope.Session, cancellationToken).ConfigureAwait(false));
        return new ForkResult(fork, through, duringTurn, tainted, models.ToImmutable());
    }

    private void CheckForkMix(ModelMix? mix)
    {
        if (mix is null)
            return;
        foreach (var (slot, pick) in mix.Slots)
        {
            ModelSlots.ContractOf(slot);
            if (pick.Ladder is not null || pick.Fallbacks is not null || !pick.Escalate)
                throw new ArgumentException($"A fork takes one model per slot; {slot}'s ladder and fallbacks stay the configured ones.", nameof(mix));
            var descriptor = Catalogue.Find(pick.Model);
            if (!descriptor.Serves(slot))
                throw new ArgumentException($"'{TerminalText.Visible(descriptor.Name)}' cannot serve {slot}; it serves {string.Join(", ", descriptor.Slots)}.",
                    nameof(mix));
            if (Catalogue.Check(descriptor.Name) is { IsReady: false } availability)
                throw new ModelUnavailableException(availability);
        }
    }

    private bool IsChildLog(string sessionId, CancellationToken cancellationToken) =>
        (TryGetSession(sessionId, out var session) && session.Child is not null)
        || Log.Read(sessionId, 0, 1, 1, cancellationToken) is [{ Payload: ChildOf }];
}
