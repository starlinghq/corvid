using System.Collections.Immutable;

namespace Corvid.Models;

/// <summary>Why a slot changed. The wire name is the <c>reason</c> of the <c>ModelSwitched</c> event.</summary>
public enum SwitchReason
{
    User,
    Escalation,
    Fallback,
    /// <summary>The automatic return to the default after an escalated or fallen-back turn (plan §6).</summary>
    DeEscalation,
    /// <summary>The privacy egress gate refused a request to a gated model, so the slot moved to a local one (plan §10.4).</summary>
    Privacy,
    /// <summary>
    /// The person chose the model for the rest of the session (A5): it becomes the session's default, so de-escalation
    /// returns to it and it lasts past the turn until they change it (<see cref="SessionModelSlots.SetSessionDefaultAsync"/>).
    /// </summary>
    Session,
}

/// <summary>How long a person's switch lasts (A5): the session's choice, the session's choice pinned, or this turn only.</summary>
public enum SwitchScope
{
    /// <summary>The session's default from now on: escalation and fallback still move off it, de-escalation returns to it.</summary>
    Session,
    /// <summary>As <see cref="Session"/>, and pinned: a turn's end leaves the slot where it is, escalated or not.</summary>
    Pin,
    /// <summary>For the running (or next) turn only: the turn's end returns the slot to the session's default.</summary>
    Turn,
}

public static class SwitchReasons
{
    public static string ToWireName(this SwitchReason reason) => reason switch
    {
        SwitchReason.User => "user",
        SwitchReason.Escalation => "escalation",
        SwitchReason.Fallback => "fallback",
        SwitchReason.DeEscalation => "de_escalation",
        SwitchReason.Privacy => "privacy",
        SwitchReason.Session => "session",
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };
}

/// <summary>What the context budget must do before the next call on the new profile (plan §4.2 step 4, §8).</summary>
public enum BudgetAction
{
    None,
    /// <summary>The new profile accepts less context: compact before the next model call.</summary>
    CompactBeforeNextCall,
    /// <summary>The new profile accepts more: compacted ranges may be expanded back out of the log.</summary>
    ExpandCompactedRanges,
}

/// <param name="SwitchSequence">The log position of the <c>ModelSwitched</c> event.</param>
public sealed record BudgetChange(string SessionId, string Slot, ModelProfile From, ModelProfile To, long SwitchSequence);

/// <summary>
/// Runs after every switch, while the session's switch lock is held, and reports the direction on the
/// <see cref="SwitchResult"/>. It must not call a model: a summarizer call that fell back would wait for that same lock. The
/// turn loop acts on the reported direction (C-016): it expands compacted ranges before the generator's next call on a larger
/// window, and fits every call to the current window, compacting when it does not fit.
/// </summary>
public interface IBudgetPolicy
{
    ValueTask<BudgetAction> OnProfileChangedAsync(BudgetChange change, CancellationToken cancellationToken);
}

/// <summary>Minimal policy: compares accepted context and reports the direction. It does not count tokens.</summary>
public sealed class AcceptedContextBudgetPolicy : IBudgetPolicy
{
    public static AcceptedContextBudgetPolicy Instance { get; } = new();

    public ValueTask<BudgetAction> OnProfileChangedAsync(BudgetChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return ValueTask.FromResult(change.To.AcceptedContext.CompareTo(change.From.AcceptedContext) switch
        {
            < 0 => BudgetAction.CompactBeforeNextCall,
            > 0 => BudgetAction.ExpandCompactedRanges,
            _ => BudgetAction.None,
        });
    }
}

/// <param name="Head">The log position the switch would follow; content up to here is what the new model would see.</param>
public sealed record PrivacyCheck(string SessionId, string Slot, ModelProfile From, ModelProfile To, SwitchReason Reason, long Head);

public sealed record PrivacyVerdict(bool Allowed, string Reason)
{
    public static PrivacyVerdict Allow(string reason = "allowed") => new(true, reason);

    public static PrivacyVerdict Refuse(string reason) => new(false, reason);
}

/// <summary>
/// Consulted before any switch whose target is <see cref="PrivacyClass.Cloud"/>. A refusal leaves the slot where it
/// is and appends nothing. Per-request redaction (plan §10.4) is a separate gate on each cloud binding
/// (<see cref="Privacy.PrivacyEgressChatClient"/>).
/// </summary>
public interface IPrivacyGate
{
    ValueTask<PrivacyVerdict> CheckAsync(PrivacyCheck check, CancellationToken cancellationToken);
}

public static class PrivacyGates
{
    /// <summary>The default: no content leaves the machine until a real gate is configured.</summary>
    public static IPrivacyGate DenyCloud { get; } = new Fixed(PrivacyVerdict.Refuse("no privacy gate is configured for cloud slots"));

    /// <summary>For tests and explicit opt-in only.</summary>
    public static IPrivacyGate AllowAll { get; } = new Fixed(PrivacyVerdict.Allow());

    private sealed class Fixed(PrivacyVerdict verdict) : IPrivacyGate
    {
        public ValueTask<PrivacyVerdict> CheckAsync(PrivacyCheck check, CancellationToken cancellationToken) => ValueTask.FromResult(verdict);
    }
}

public sealed record ModelSwitchOptions
{
    public IBudgetPolicy Budget { get; init; } = AcceptedContextBudgetPolicy.Instance;

    public IPrivacyGate Privacy { get; init; } = PrivacyGates.DenyCloud;

    /// <summary>
    /// Told after each change of a slot's binding or of its session default (A5), with the result: every
    /// <see cref="SwitchStatus.Switched"/> one, and an <see cref="SwitchStatus.Unchanged"/> one whose
    /// <see cref="SwitchResult.SessionDefaultChanged"/> is set. Called inside the session's switch lock, on the switching
    /// thread: it must be quick, must not throw (a throw is swallowed) and must not switch again.
    /// </summary>
    public Action<SwitchResult>? Changed { get; init; }
}

public enum SwitchStatus
{
    Switched,
    /// <summary>The slot already used the target binding. Nothing was appended.</summary>
    Unchanged,
    /// <summary>The privacy gate refused the target. Nothing was appended.</summary>
    Refused,
    /// <summary>Escalation, fallback or de-escalation had nowhere to go.</summary>
    NoTarget,
}

/// <param name="Sequence">The <c>ModelSwitched</c> event's position, when one was appended.</param>
/// <param name="PendingApprovals">Approvals still pending when the switch was recorded; they carry over unchanged.</param>
/// <param name="UnfinishedToolCalls">
/// Issued calls with no result when the switch was recorded: calls parked for an approval, or, on a fallback, calls
/// still running. They carry over unchanged.
/// </param>
public sealed record SwitchResult(
    SwitchStatus Status,
    string Slot,
    ModelProfile From,
    ModelProfile? To,
    SwitchReason Reason,
    long? Sequence = null,
    BudgetAction Budget = BudgetAction.None,
    ImmutableArray<string> PendingApprovals = default,
    string? Detail = null,
    ImmutableArray<string> UnfinishedToolCalls = default)
{
    public ImmutableArray<string> PendingApprovals { get; init; } = PendingApprovals.IsDefault ? [] : PendingApprovals;

    public ImmutableArray<string> UnfinishedToolCalls { get; init; } = UnfinishedToolCalls.IsDefault ? [] : UnfinishedToolCalls;

    /// <summary>The switch made <see cref="To"/> the session's default for the slot (A5, <see cref="SwitchScope.Session"/> or <see cref="SwitchScope.Pin"/>).</summary>
    public bool SessionDefaultChanged { get; init; }

    /// <summary>Whether the slot is pinned after the switch.</summary>
    public bool Pinned { get; init; }
}
