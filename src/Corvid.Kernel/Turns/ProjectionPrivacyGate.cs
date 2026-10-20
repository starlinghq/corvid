using Corvid.Abstractions.Events;
using Corvid.Fabric;
using Corvid.Kernel.Events;
using Corvid.Models;
using Corvid.Models.Privacy;

namespace Corvid.Kernel.Turns;

/// <summary>
/// C-006's privacy hook (plan §4.2 step 6): before a slot moves to a gated model, the session's projection at the switch's
/// head, rendered as it would be sent, goes through the egress gate (plan §10.4). A move the gate would refuse on every
/// request (the profile refuses PII and the conversation holds some, or the PII scan cannot run) is refused here, so an
/// escalation passes over that target and the work stays local; anything else is allowed and each request is then gated
/// again by the binding itself.
/// </summary>
/// <param name="isTainted">Whether a session holds outside content (plan §10.3); taint makes the gate stricter.</param>
public sealed class ProjectionPrivacyGate(IEventLog log, PrivacyEgress egress, Func<string, bool>? isTainted = null) : IPrivacyGate
{
    public async ValueTask<PrivacyVerdict> CheckAsync(PrivacyCheck check, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (egress.PolicyFor(check.To) is null)
            return PrivacyVerdict.Allow("the target is not gated");
        var snapshot = SessionProjection.Load(log, check.SessionId, check.Head, cancellationToken: cancellationToken);
        // No budget: the whole visible transcript is what a large cloud context would receive.
        var rendered = PromptRenderer.Render(snapshot, "", CharsPerTokenCounter.Instance, int.MaxValue);
        var decision = await egress.AssessAsync(check.To, PrivacyEgress.OutgoingTexts(rendered.Messages),
            isTainted?.Invoke(check.SessionId) ?? false, cancellationToken).ConfigureAwait(false);
        return decision.Refused ? PrivacyVerdict.Refuse(decision.Reason) : PrivacyVerdict.Allow(decision.Reason);
    }
}
