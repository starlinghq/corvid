using System.Globalization;
using System.Text;
using System.Windows.Input;
using Corvid.App.Core.Diffs;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Mcp;

/// <summary>A row of the servers table: name, transport, trust, state, tools and held count. Names come from the configuration and are cleaned.</summary>
public sealed class McpServerRowViewModel : ObservableObject
{
    private McpServerRecord _record;
    private bool _isSelected;

    internal McpServerRowViewModel(McpServerRecord record, Action<McpServerRowViewModel> select)
    {
        _record = record;
        SelectCommand = new RelayCommand(() => select(this));
    }

    public McpServerRecord Record => _record;

    public string Id => _record.Name;

    public string Name => SafeText.Clean(_record.Name);

    public string Transport => McpText.Transport(_record.Transport);

    public string TrustText => McpText.Trust(_record.Trust);

    public Tone TrustTone => _record.Trust == McpTrust.Trusted ? Tone.Green : Tone.Grey;

    public string StateText => McpText.State(_record.State);

    public Tone StateTone => McpText.StateTone(_record.State);

    public string ToolsText => _record.Tools.Count switch
    {
        0 => "—",
        1 => "1 tool",
        var n => $"{n} tools",
    };

    public int HeldCount => _record.Tools.Count(tool => tool.State == McpToolState.Held);

    public string HeldText => HeldCount == 0 ? "" : $"{HeldCount} held";

    public bool HasHeld => HeldCount > 0;

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public RelayCommand SelectCommand { get; }

    public string AutomationName => $"{Name}, {Transport}, {TrustText}, {StateText}, {ToolsText}{(HasHeld ? ", " + HeldText : "")}";

    internal void Update(McpServerRecord record)
    {
        _record = record;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>A tool a server added: its name, pinned hash and state (pinned, held, allowed and always asking, unpinned).</summary>
public sealed class McpToolRowViewModel
{
    internal McpToolRowViewModel(McpToolRecord tool, Action<McpToolRecord>? review)
    {
        Tool = tool;
        Name = SafeText.Clean(tool.Name);
        Hash = tool.Hash.Length == 0 ? "—" : SafeText.Clean(tool.Hash);
        (StateText, StateTone) = tool.State switch
        {
            McpToolState.Held when tool.KeptBlocked => ("kept blocked", Tone.Red),
            McpToolState.Held => ($"held · {McpText.Reason(tool.HeldReason ?? McpHoldReason.Changed)}", Tone.Red),
            McpToolState.Allowed => ("allowed · always asks", Tone.Amber),
            McpToolState.Unpinned => ("unpinned · pinned at next start", Tone.Grey),
            _ => ("pinned", Tone.Green),
        };
        ReviewCommand = new RelayCommand(() => review?.Invoke(tool), () => tool.State == McpToolState.Held && review is not null);
    }

    public McpToolRecord Tool { get; }

    public string Name { get; }

    public string Hash { get; }

    public string StateText { get; }

    public Tone StateTone { get; }

    public bool IsHeld => Tool.State == McpToolState.Held;

    public RelayCommand ReviewCommand { get; }

    public string AutomationName => $"{Name}, {StateText}, hash {Hash}";
}

/// <summary>
/// A held definition under review (F15): why it is held, its definition against its pin side by side with the changed
/// words marked and every hidden character revealed (⟨U+202E⟩), the hashes, and the answers. Accept pins exactly the hash
/// shown; allow is for a flagged or withheld one; keep blocked leaves it held. A definition holding a hidden character can
/// be neither accepted nor allowed: what would be pinned is not what reads.
/// </summary>
public sealed class McpHeldViewModel : ObservableObject
{
    /// <summary>Lines of context around a change: a definition is short, so nearly all of it shows.</summary>
    private const int Context = 40;

    private bool _showDiff;

    internal McpHeldViewModel(McpHeldTool held, DateTimeOffset now, TimeZoneInfo zone, ICommand accept, ICommand allow, ICommand keep)
    {
        Held = held;
        Tool = SafeText.Clean(held.Tool);
        Server = SafeText.Clean(held.Server);
        var heldAt = Jobs.JobsText.When(held.HeldAt, now, zone);
        var pinned = held.PinnedAt is { } at ? at.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "";
        Title = held.Reason switch
        {
            McpHoldReason.New => $"{Tool} · new since the server's tools were pinned",
            McpHoldReason.Changed => $"{Tool} · changed since its pin of {pinned}",
            McpHoldReason.Flagged => $"{Tool} · flagged",
            _ => $"{Tool} · withheld after an injection verdict",
        };
        Note = held.KeptBlocked ? $"held {heldAt} · kept blocked" : $"held {heldAt} · not offered until you accept";
        Why = Spans.Parse(held.Reason switch
        {
            McpHoldReason.Flagged =>
                $"Its text matches a poisoning pattern at **{Spans.Escape(SafeText.Reveal(held.Flag))}**. Allow offers it as an untrusted tool that asks before every call.",
            McpHoldReason.Injection =>
                "The injection question judged its description to instruct the agent. Allow offers it as an untrusted tool: it asks before every call and taints the session.",
            McpHoldReason.New => "New since the server's tools were pinned. Accept pins the definition below.",
            _ => $"Its definition differs from the pin of {pinned}. Accept pins the new one below; until then the tool is not offered.",
        });
        HasHidden = SafeText.HasHidden(held.Definition.All);
        HiddenWarning = HasHidden
            ? "This definition holds hidden characters, shown as ⟨U+…⟩ below. It cannot be pinned: what the model would read is not what you see."
            : "";
        var diff = FileDiff.Compute(held.Tool, held.Pinned is { } before ? Document(before) : null, Document(held.Definition));
        Rows = diff.SideBySide(Context);
        UnifiedRows = diff.Unified(Context);
        Counts = diff.CountsText;
        HashText = held.PinnedHash.Length > 0 ? $"{SafeText.Clean(held.PinnedHash)} → {SafeText.Clean(held.Hash)}" : SafeText.Clean(held.Hash);
        CanAccept = !held.NeedsAllow && !HasHidden;
        CanAllow = held.NeedsAllow && !HasHidden;
        AcceptCommand = accept;
        AllowCommand = allow;
        KeepBlockedCommand = keep;
        _showDiff = !held.KeptBlocked;
        ToggleDiffCommand = new RelayCommand(() => ShowDiff = !ShowDiff);
    }

    public McpHeldTool Held { get; }

    public string Tool { get; }

    public string Server { get; }

    public string Title { get; }

    public string Note { get; }

    public IReadOnlyList<TextRun> Why { get; }

    public bool IsFlagged => Held.NeedsAllow;

    public bool HasHidden { get; }

    public string HiddenWarning { get; }

    public IReadOnlyList<SideBySideRow> Rows { get; }

    public IReadOnlyList<DiffRow> UnifiedRows { get; }

    /// <summary>"+3 −1": lines of the definition added and removed.</summary>
    public string Counts { get; }

    /// <summary>"3e9a41c0 → 7b20d5f9": the pinned hash and the one accept pins.</summary>
    public string HashText { get; }

    public bool CanAccept { get; }

    /// <summary>Allow shows only for a flagged or withheld definition, the one kind it is for.</summary>
    public bool CanAllow { get; }

    public bool ShowAllow => Held.NeedsAllow;

    public bool ShowAccept => !Held.NeedsAllow;

    public bool KeptBlocked => Held.KeptBlocked;

    public ICommand AcceptCommand { get; }

    public ICommand AllowCommand { get; }

    public ICommand KeepBlockedCommand { get; }

    /// <summary>A kept-blocked card folds its diff away until asked.</summary>
    public bool ShowDiff { get => _showDiff; set { if (SetProperty(ref _showDiff, value)) OnPropertyChanged(nameof(DiffToggleText)); } }

    public string DiffToggleText => _showDiff ? "Hide the diff" : "Show the diff";

    public RelayCommand ToggleDiffCommand { get; }

    public string AcceptName => $"Accept {Tool}: pin hash {SafeText.Clean(Held.Hash)}";

    public string AllowName => $"Allow {Tool}: offer it as a tool that asks before every call";

    public string KeepName => $"Keep {Tool} blocked";

    /// <summary>
    /// A definition as the diff shows it: each field under its name, the description wrapped at sentence and word
    /// boundaries so it reads in a column, the schema and annotations as their JSON lines. Every hidden character is
    /// revealed, so a change that adds one shows as a changed word.
    /// </summary>
    public static string Document(McpDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var text = new StringBuilder();
        Field(text, "description", Wrap(SafeText.Reveal(definition.Description), 64));
        Field(text, "title", [SafeText.Reveal(definition.Title)]);
        Field(text, "inputSchema", SafeText.Reveal(definition.InputSchema).Split('\n'));
        if (definition.Annotations.Length > 0)
            Field(text, "annotations", SafeText.Reveal(definition.Annotations).Split('\n'));
        return text.ToString();
    }

    private static void Field(StringBuilder text, string name, IEnumerable<string> lines)
    {
        text.Append(name).Append('\n');
        foreach (var line in lines)
            text.Append("  ").Append(line.TrimEnd('\r')).Append('\n');
    }

    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
                line.Append(' ');
            line.Append(word);
            // A sentence ends its line, so an added sentence is an added line rather than a reflowed paragraph.
            if (word.EndsWith('.') || word.EndsWith('?') || word.EndsWith('!'))
            {
                yield return line.ToString();
                line.Clear();
            }
        }

        if (line.Length > 0)
            yield return line.ToString();
    }
}

/// <summary>The MCP page's wording.</summary>
public static class McpText
{
    public static string Transport(McpTransport transport) => transport == McpTransport.Stdio ? "stdio" : "http";

    public static string Trust(McpTrust trust) => trust == McpTrust.Trusted ? "trusted" : "untrusted";

    public static string State(McpServerState state) => state switch
    {
        McpServerState.Running => "running",
        McpServerState.Starting => "starting",
        McpServerState.Failed => "failed",
        _ => "not started",
    };

    public static Tone StateTone(McpServerState state) => state switch
    {
        McpServerState.Running => Tone.Green,
        McpServerState.Failed => Tone.Red,
        _ => Tone.Grey,
    };

    public static string Reason(McpHoldReason reason) => reason switch
    {
        McpHoldReason.New => "new",
        McpHoldReason.Changed => "changed",
        McpHoldReason.Flagged => "flagged",
        _ => "injection",
    };
}
