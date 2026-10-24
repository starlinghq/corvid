namespace Corvid.App.Core.Mcp;

/// <summary>
/// A server's trust (C-018): an untrusted server's tools ask before every call and their descriptions and results taint
/// the session; a trusted server's hints are honoured.
/// </summary>
public enum McpTrust
{
    Untrusted,
    Trusted,
}

public enum McpTransport
{
    Stdio,
    Http,
}

public enum McpServerState
{
    Running,
    Starting,
    Failed,
    /// <summary>Added but not started yet: it starts with the next session.</summary>
    NotStarted,
}

/// <summary>Why a tool's definition is held back (<c>McpHoldReasons</c>): new since the pins, changed, flagged, or judged an injection.</summary>
public enum McpHoldReason
{
    New,
    Changed,
    /// <summary>Its text matches a poisoning pattern: only allow offers it, as an untrusted tool that always asks.</summary>
    Flagged,
    /// <summary>The injection question judged its description to instruct the agent: only allow offers it.</summary>
    Injection,
}

public enum McpToolState
{
    /// <summary>Offered: its definition matches its pin.</summary>
    Pinned,
    /// <summary>Not offered until reviewed.</summary>
    Held,
    /// <summary>A flagged or withheld definition offered by allow: it asks before every call.</summary>
    Allowed,
    /// <summary>Its server's pins were forgotten: the next start pins whatever it lists.</summary>
    Unpinned,
}

/// <summary>
/// A tool definition's parts, as the pin compares them (<c>McpToolDefinition</c>): the text fields, and the input schema
/// and annotations as JSON. Everything here comes from the server.
/// </summary>
public sealed record McpDefinition(string Description, string Title, string InputSchema, string Annotations = "")
{
    /// <summary>Every field as one text, for the hidden-character check: what is pinned is all of it.</summary>
    public string All => string.Join('\n', Description, Title, InputSchema, Annotations);
}

/// <summary>One tool a server lists, with its pinned hash and whether it is offered.</summary>
public sealed record McpToolRecord(string Server, string Name, McpToolState State)
{
    /// <summary>The pinned definition's hash, shortened (C-033); empty when unpinned.</summary>
    public string Hash { get; init; } = "";

    public McpHoldReason? HeldReason { get; init; }

    /// <summary>Held and looked at: the person chose to keep it blocked. It stays held and is not offered.</summary>
    public bool KeptBlocked { get; init; }
}

/// <summary>
/// A held definition and its pin (<c>McpHeldTool</c>): what changed, the matched text for a flagged one, the hashes, and when
/// it was pinned and held. Accept pins exactly <see cref="Hash"/>, so a review accepts what it showed.
/// </summary>
public sealed record McpHeldTool(string Server, string Tool, McpHoldReason Reason, McpDefinition Definition, string Hash, DateTimeOffset HeldAt)
{
    /// <summary>The pinned definition, for a changed tool; null for a new one, and for a withheld one (its pin is this definition).</summary>
    public McpDefinition? Pinned { get; init; }

    public string PinnedHash { get; init; } = "";

    public DateTimeOffset? PinnedAt { get; init; }

    /// <summary>For a flagged definition: the text that matched a poisoning pattern.</summary>
    public string Flag { get; init; } = "";

    public bool KeptBlocked { get; init; }

    /// <summary>Only allow takes it (as an untrusted tool that asks before every call); accept refuses.</summary>
    public bool NeedsAllow => Reason is McpHoldReason.Flagged or McpHoldReason.Injection;
}

/// <summary>A configured server (<c>Corvid:Mcp:Servers</c>) with its state and the tools it added.</summary>
public sealed record McpServerRecord(string Name, McpTransport Transport, McpTrust Trust, McpServerState State)
{
    /// <summary>The command line it runs (stdio) or its address (HTTP).</summary>
    public string Target { get; init; } = "";

    /// <summary>Why it failed, or what to know ("exited with code 1 at start").</summary>
    public string Detail { get; init; } = "";

    public DateTimeOffset? Since { get; init; }

    public IReadOnlyList<McpToolRecord> Tools { get; init; } = [];

    /// <summary>The server has pins; forget drops them.</summary>
    public bool HasPins { get; init; }
}

/// <summary><c>corvid mcp serve</c>: Corvid as an MCP server for another client, and what it exposes.</summary>
public sealed record McpServeStatus(bool Running, IReadOnlyList<string> Exposes, string Note);

public sealed record McpSnapshot(IReadOnlyList<McpServerRecord> Servers, IReadOnlyList<McpHeldTool> Held, McpServeStatus Serve, string PinsPath);

/// <summary>What the add-server form sends: checked, then written to the configuration.</summary>
public sealed record McpServerDraft(string Name, McpTransport Transport, string Target, McpTrust Trust);
