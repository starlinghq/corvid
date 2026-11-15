using System.Collections.Immutable;

namespace Corvid.App.Core.Board;

/// <summary>
/// What an attention item is about (docs/plan/ui.md §3, §4.5): the tray lists everything waiting on the user across
/// sessions. Approvals and questions come from the sessions themselves; the rest come from the areas that hold them.
/// </summary>
public enum AttentionKind
{
    /// <summary>A session's approval queue: one item however many prompts wait in it.</summary>
    Approval,
    /// <summary>A session's clarifying question (K-6).</summary>
    Question,
    /// <summary>An MCP tool held because its definition changed since its pin (F15).</summary>
    McpTool,
    /// <summary>A pairing code from the gateway (F16).</summary>
    Pairing,
    /// <summary>Memory or skills staged for review.</summary>
    Memory,
    /// <summary>A scheduled job's run finished.</summary>
    Job,
    /// <summary>A Vault confirm in progress.</summary>
    VaultConfirm,
}

/// <summary>How an attention item's button is drawn: the mockup's <c>.btn.sm</c>, <c>.btn.sm.ghost</c> or <c>.ghost.danger</c>.</summary>
public enum AttentionActionStyle
{
    Default,
    Ghost,
    Danger,
}

/// <summary>What pressing an attention item's button does.</summary>
public enum AttentionActionKind
{
    /// <summary>
    /// Answers the item through its service; the item goes away. Only for an answer that grants nothing (ignore a code):
    /// pinning a tool or pairing someone is done on the area's card, where what is granted is shown in full.
    /// </summary>
    Resolve,
    /// <summary>Opens the area that holds the item's full card (Memory's review, MCP's held definition, Gateway's request).</summary>
    Navigate,
    /// <summary>Opens a session: an approval's full card, a question, a job's run.</summary>
    OpenSession,
}

/// <summary>One button on an attention item.</summary>
/// <param name="Id">What the service is told on <see cref="AttentionActionKind.Resolve"/> ("guest", "accept").</param>
public sealed record AttentionAction(string Id, string Label, AttentionActionKind Kind)
{
    public AttentionActionStyle Style { get; init; }

    /// <summary>Drawn as a link (the accent text of the mockup's <c>a.btn</c>): it goes to the item's full card elsewhere.</summary>
    public bool IsLink { get; init; }

    /// <summary>For <see cref="AttentionActionKind.Navigate"/>: the area to open.</summary>
    public Area? Area { get; init; }

    /// <summary>
    /// For <see cref="AttentionActionKind.Navigate"/>: what in the area to land on (a held tool's name, a pairing request),
    /// followed as the link's <see cref="Shell.AreaLink.Name"/>; null opens the area where it was left.
    /// </summary>
    public string? Target { get; init; }

    /// <summary>For <see cref="AttentionActionKind.OpenSession"/>: the session to open.</summary>
    public string? SessionId { get; init; }

    /// <summary>The hover text and screen reader description: what the button will do.</summary>
    public string Help { get; init; } = "";
}

/// <summary>
/// An item an area reports to the attention tray (Board.dc.html <c>.tray-item</c>): what it is in a line, the detail
/// under it, and its buttons. Nothing is answered from a bare button: an approval opens its full card.
/// </summary>
public sealed record AttentionItem(string Id, AttentionKind Kind, string What, string Detail)
{
    public ImmutableArray<AttentionAction> Actions { get; init; } = [];

    /// <summary>When it arrived; the tray lists the areas' items in this order after the sessions' own.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>
/// The engine's state as the Board's status bar shows it (Board.dc.html <c>.statusbar</c>): where the engine runs, the
/// decider, the scheduler and the gateway's adapters. The Board adds the workspace count and how many sessions run and wait.
/// </summary>
public sealed record BoardStatus(string Engine, string Decider, string Scheduler, string Gateway);
