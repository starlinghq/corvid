namespace Corvid.App.Core;

/// <summary>
/// The mockups' 16 px stroke icons by what they draw, so a view model can name one (a tool card's kind, a tray item) and
/// the head's icon control draws it. The path data lives with that control. <see cref="PanelRight"/> is the one icon the
/// mockups lack: the toggle that brings the dock back when the window is too narrow for it, drawn in the same style.
/// </summary>
public enum IconKind
{
    None,
    /// <summary>Corvid's mark, the one filled glyph.</summary>
    Logo,
    /// <summary>Attention: the rail's bell and the phone's Attention tab.</summary>
    Bell,
    /// <summary>A speech bubble: Sessions, and the phone's Chat tab.</summary>
    Chat,
    /// <summary>Four tiles: the Board.</summary>
    Board,
    /// <summary>Two sliders: Review, and the phone's Changes tab.</summary>
    Sliders,
    /// <summary>Stacked layers: Memory.</summary>
    Layers,
    /// <summary>A four-pointed star: Skills.</summary>
    Sparkle,
    /// <summary>A clock: Jobs and times.</summary>
    Clock,
    /// <summary>A chip with pins: Models.</summary>
    Chip,
    /// <summary>A shield: Safety and approvals.</summary>
    Shield,
    /// <summary>A plug: MCP.</summary>
    Plug,
    /// <summary>A paper plane: the Gateway, send, and pairing.</summary>
    Send,
    /// <summary>A padlock: Secrets.</summary>
    Lock,
    /// <summary>Lines of decreasing length: Audit.</summary>
    Lines,
    /// <summary>A terminal: the shell tool.</summary>
    Terminal,
    /// <summary>A paperclip: attach a file.</summary>
    Paperclip,
    /// <summary>A question in a circle: a clarifying question.</summary>
    Question,
    /// <summary>A triangle with an exclamation mark: taint and warnings.</summary>
    Warning,
    /// <summary>A box with an arrow out: pop a session into its own window.</summary>
    PopOut,
    /// <summary>A cloud: escalation to a cloud model.</summary>
    Cloud,
    /// <summary>A page with a folded corner: read_file.</summary>
    File,
    /// <summary>A chevron pointing down: expand.</summary>
    ChevronDown,
    /// <summary>A magnifier: search_files and search.</summary>
    Search,
    /// <summary>A globe: web_fetch.</summary>
    Globe,
    /// <summary>Three lines: the phone's menu, and the rail toggle when the rail is hidden.</summary>
    Menu,
    /// <summary>Three dots: the phone's More tab.</summary>
    More,
    /// <summary>A check mark: done.</summary>
    Check,
    /// <summary>A window with a right-hand panel: show the dock (not in the mockups).</summary>
    PanelRight,
}
