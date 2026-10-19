namespace Corvid.Abstractions.Sessions;

/// <summary>Who wrote an envelope's text, for taint (plan §10.3).</summary>
public enum EnvelopeOrigin
{
    /// <summary>The session's own user. An explicit user message clears the session's taint.</summary>
    User,
    /// <summary>Someone else: another gateway user, a forwarded message, a webhook. Its text taints the session.</summary>
    External,
}

/// <summary>
/// What every surface (CLI, ACP, HTTP, gateway adapters) posts to the kernel (plan §3.1): one message for one session.
/// Surfaces post envelopes into a bounded channel, so a surface that outpaces the agent waits.
/// </summary>
/// <param name="Surface">The surface's name as the decider reads it, for example <c>cli</c> or <c>telegram</c>.</param>
/// <param name="MessageId">The surface's own id for the message, when it has one, for replies and the delivery ledger.</param>
public sealed record Envelope(string SessionId, string Surface, string Text, EnvelopeOrigin Origin = EnvelopeOrigin.User,
    string? MessageId = null)
{
    public string SessionId { get; } = !string.IsNullOrWhiteSpace(SessionId) ? SessionId : throw new ArgumentException("An envelope needs a session.", nameof(SessionId));

    public string Surface { get; } = !string.IsNullOrWhiteSpace(Surface) ? Surface : throw new ArgumentException("An envelope needs a surface.", nameof(Surface));

    public string Text { get; } = Text ?? throw new ArgumentNullException(nameof(Text));
}
