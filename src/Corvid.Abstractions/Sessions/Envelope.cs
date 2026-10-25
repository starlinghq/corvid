using System.Collections.Immutable;

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

    /// <summary>
    /// Set for a message from someone who is not the user and may use only some tools (a gateway guest); null for the
    /// session's own user, whose turns get the whole toolset.
    /// </summary>
    public GuestScope? Guest { get; init; }

    /// <summary>
    /// Set for the composer's <c>!</c> line (A4, docs/plan/tui.md §4.1): the turn runs this command through the shell tool,
    /// the approval pipeline, the sandbox and the checkpoints, as a call the user made, and asks the generator nothing. Only
    /// the session's own user may send one. <see cref="Text"/> is the line as typed, which the log keeps as the message.
    /// </summary>
    public ShellLine? Shell { get; init; }
}

/// <summary>A shell command the user typed after <c>!</c> (or <c>!!</c>, <see cref="LocalOnly"/>).</summary>
/// <param name="LocalOnly">
/// <c>!!</c>: the output stays with the user. The message, the call and its result are logged marked local-only, and the
/// prompt, compaction, the decider's recent events and the episodic index leave them out.
/// </param>
public sealed record ShellLine(string Command, bool LocalOnly = false)
{
    public string Command { get; } = !string.IsNullOrWhiteSpace(Command) ? Command : throw new ArgumentException("A shell line needs a command.", nameof(Command));
}

/// <summary>
/// What a turn driven by someone other than the user may reach (a gateway guest, C-020). The turn is offered only the tools
/// named here, by exact name, so a tool added later (an MCP server's <c>server__tool</c>, a skill's tools) is never reached
/// unless named; and it gets none of the user's own context: no skills, no memory recall and no core blocks.
/// </summary>
public sealed class GuestScope(IEnumerable<string> tools)
{
    /// <summary>No tools at all: the default for guests.</summary>
    public static GuestScope None { get; } = new([]);

    /// <summary>Tool names compared exactly (ordinal), whatever comparer the caller's set used.</summary>
    public ImmutableHashSet<string> Tools { get; } = ImmutableHashSet.CreateRange(StringComparer.Ordinal,
        tools ?? throw new ArgumentNullException(nameof(tools)));

    public bool Allows(string tool) => Tools.Contains(tool);
}
