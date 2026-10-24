using System.Windows.Input;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Board;

/// <summary>A button on an attention item: its label, how it is drawn, what it does and the sentence that says so.</summary>
public sealed class AttentionButton(string label, AttentionActionStyle style, string help, ICommand command, bool isLink = false)
{
    public string Label { get; } = label;

    /// <summary>Accent text: it opens the item's full card rather than acting (the mockup's <c>a.btn</c>).</summary>
    public bool IsLink { get; } = isLink;

    public AttentionActionStyle Style { get; } = style;

    public bool IsGhost => Style != AttentionActionStyle.Default;

    public bool IsDanger => Style == AttentionActionStyle.Danger;

    public string Help { get; } = help;

    public ICommand Command { get; } = command;
}

/// <summary>
/// One row of the attention tray (Board.dc.html <c>.tray-item</c>): an icon in the item's colour role, what it is in a
/// line, the detail under it, and its buttons. Items from sessions (an approval queue, a question) and from the other
/// areas (a held MCP tool, a pairing code, staged memory, a finished job) look alike.
/// </summary>
public sealed class AttentionItemViewModel : ObservableObject
{
    private string _what;
    private string _detail;
    private IReadOnlyList<AttentionButton> _buttons;

    public AttentionItemViewModel(string id, AttentionKind kind, string what, string detail, IReadOnlyList<AttentionButton> buttons)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(buttons);
        Id = id;
        Kind = kind;
        _what = what ?? "";
        _detail = detail ?? "";
        _buttons = buttons;
    }

    public string Id { get; }

    public AttentionKind Kind { get; }

    /// <summary>The session it belongs to, for an approval or a question; null for the areas' items.</summary>
    public string? SessionId { get; init; }

    public IconKind Icon => Kind switch
    {
        AttentionKind.Approval => IconKind.Shield,
        AttentionKind.Question => IconKind.Question,
        AttentionKind.McpTool => IconKind.Plug,
        AttentionKind.Pairing => IconKind.Send,
        AttentionKind.Memory => IconKind.Layers,
        AttentionKind.Job => IconKind.Clock,
        AttentionKind.VaultConfirm => IconKind.Lock,
        _ => IconKind.Bell,
    };

    /// <summary>The icon's colour role: amber an approval, the accent a question, red a held tool, violet pairing.</summary>
    public Tone IconTone => Kind switch
    {
        AttentionKind.Approval or AttentionKind.VaultConfirm => Tone.Amber,
        AttentionKind.Question => Tone.Blue,
        AttentionKind.McpTool => Tone.Red,
        AttentionKind.Pairing => Tone.Violet,
        _ => Tone.Grey,
    };

    /// <summary>Drawn in the text colour rather than a role's: staged memory and finished jobs.</summary>
    public bool IconPlain => IconTone == Tone.Grey;

    public string What
    {
        get => _what;
        set
        {
            if (SetProperty(ref _what, value ?? ""))
                OnPropertyChanged(nameof(AutomationName));
        }
    }

    public string Detail
    {
        get => _detail;
        set
        {
            if (SetProperty(ref _detail, value ?? ""))
                OnPropertyChanged(nameof(AutomationName));
        }
    }

    public IReadOnlyList<AttentionButton> Buttons { get => _buttons; set => SetProperty(ref _buttons, value ?? []); }

    public string AutomationName => $"{What}. {Detail}";
}
