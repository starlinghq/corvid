using System.Collections.Immutable;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Privacy;

/// <summary>
/// What the privacy egress gate did with one request (G-9), handed to an observer the moment the gate has decided and before
/// anything is sent: the target, the request's context, the decision, and, only when something leaves the machine, the
/// messages exactly as they go to the provider (after redaction, with the instructions as a leading system message) and the
/// names of the tools offered with them. A refused request carries no messages: nothing of it left, and it is unredacted.
/// </summary>
/// <param name="Sent">The messages as sent; null when the request was refused.</param>
/// <param name="Placeholders">
/// How many distinct values the request as sent carries as the gate's placeholders (<c>&lt;EMAIL_1&gt;</c>): placeholders the
/// session's map minted, found in the outgoing text. Zero when nothing was redacted.
/// </param>
/// <param name="Tools">The tools offered with the request, by name; their schemas leave the machine too.</param>
public sealed record EgressObservation(ModelProfile Profile, ModelRequestContext Context, EgressDecision Decision,
    IReadOnlyList<ChatMessage>? Sent, int Placeholders, ImmutableArray<string> Tools)
{
    /// <summary>Something left the machine: the gate sent the request as it was or redacted.</summary>
    public bool Left => Sent is not null;
}

/// <summary>Told what the gate did with each request of the bindings it is given to (<see cref="PrivacyEgressChatClient"/>).</summary>
/// <remarks>
/// Called on the request's own flow, before the provider is called; it must not throw, and should be quick. The host's egress
/// ledger records each observation in the session's log and keeps the request as sent.
/// </remarks>
public interface IEgressObserver
{
    void Observe(EgressObservation observation);
}
