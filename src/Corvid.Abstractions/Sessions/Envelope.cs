using System.Collections.Immutable;

namespace Corvid.Abstractions.Sessions;

/// <summary>Who wrote an envelope's text, for taint (plan §10.3).</summary>
public enum EnvelopeOrigin
{
    /// <summary>The session's own user. An explicit user message clears the session's taint.</summary>
    User,
    /// <summary>Someone else: another gateway user, a forwarded message, a webhook. Its text taints the session.</summary>
    External,
    /// <summary>
    /// C-042: another of the user's own sessions in the same flock, which held no outside content when it sent the message.
    /// It neither taints the session nor clears its taint; a tainted peer's message is <see cref="External"/>.
    /// </summary>
    Peer,
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
    /// C-042: set for a message from a peer session in a flock (with <see cref="EnvelopeOrigin.Peer"/>, or
    /// <see cref="EnvelopeOrigin.External"/> when the peer was tainted). The turn gets the whole toolset, as the user's does.
    /// </summary>
    public PeerSender? Peer { get; init; }

    /// <summary>Who wrote the text when it is not the session's own user (a guest, a peer): logged on its message (security M2).</summary>
    public string? From => Guest is { } guest ? guest.Sender ?? "a guest" : Peer?.Name;

    /// <summary>
    /// Set for the composer's <c>!</c> line (A4, docs/plan/tui.md §4.1): the turn runs this command through the shell tool,
    /// the approval pipeline, the sandbox and the checkpoints, as a call the user made, and asks the generator nothing. Only
    /// the session's own user may send one. <see cref="Text"/> is the line as typed, which the log keeps as the message.
    /// </summary>
    public ShellLine? Shell { get; init; }

    /// <summary>
    /// K-8 (A6): images the message carries (a paste, a drop, a named file, an ACP image block), unchecked: the turn reads
    /// their format from the bytes, strips their metadata and stores them before the message is logged, or refuses the
    /// message. On an <see cref="EnvelopeOrigin.External"/> envelope they are outside content like its text.
    /// </summary>
    public ImmutableArray<Media.ImageInput> Images { get; init; } = [];
}

/// <summary>C-042: the peer session a message came from: its name in the flock ("api") and the flock's name.</summary>
public sealed record PeerSender(string Name, string Flock)
{
    public string Name { get; } = !string.IsNullOrWhiteSpace(Name) ? Name : throw new ArgumentException("A peer needs a name.", nameof(Name));

    public string Flock { get; } = !string.IsNullOrWhiteSpace(Flock) ? Flock : throw new ArgumentException("A peer needs a flock.", nameof(Flock));
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

    /// <summary>
    /// Who the guest is, as the log and the prompt name them ("telegram guest dana (1001)"); one line, no control
    /// characters. Null when the surface does not say.
    /// </summary>
    public string? Sender { get; init; }
}
