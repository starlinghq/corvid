using System.Collections.Immutable;
using System.Globalization;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Audit;

/// <summary>An entry of the session picker: a session ("cli-9f2e · Fix the flaky sandbox test on CI") or the whole log.</summary>
public sealed record AuditSessionOption(string? SessionId, string Label);

/// <summary>A box of the Filter panel: one event group, shown or hidden.</summary>
public sealed class AuditFilterItem : ObservableObject
{
    private readonly Action _changed;
    private bool _isChecked;

    internal AuditFilterItem(AuditEventGroup group, bool isChecked, Action changed)
    {
        Group = group;
        Label = AuditText.GroupLabel(group);
        DefaultChecked = isChecked;
        _isChecked = isChecked;
        _changed = changed;
    }

    public AuditEventGroup Group { get; }

    public string Label { get; }

    /// <summary>Whether reset checks it: the conversation, tools, approvals, decisions, switches and detections.</summary>
    public bool DefaultChecked { get; }

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (SetProperty(ref _isChecked, value))
                _changed();
        }
    }
}

/// <summary>A row of the Events table.</summary>
public sealed class AuditEventRow : ObservableObject
{
    private bool _isSelected;

    internal AuditEventRow(AuditEvent auditEvent, TimeZoneInfo zone, Action<AuditEventRow> select)
    {
        Event = auditEvent;
        SequenceText = auditEvent.Sequence.ToString(CultureInfo.InvariantCulture);
        TimeText = AuditText.Time(auditEvent.At, zone);
        Type = SafeText.Clean(auditEvent.Type);
        SelectCommand = new RelayCommand(() => select(this));
        AutomationName = $"Event {SequenceText}, {TimeText}, {Type}: {AuditText.Plain(auditEvent.Summary)}";
    }

    public AuditEvent Event { get; }

    public long Sequence => Event.Sequence;

    public string SequenceText { get; }

    public string TimeText { get; }

    /// <summary>The event's type as stored, cleaned for drawing.</summary>
    public string Type { get; }

    public IReadOnlyList<AuditSpan> Summary => Event.Summary;

    /// <summary>A detection match: the whole row in red.</summary>
    public bool IsAlert => Event.IsAlert;

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public RelayCommand SelectCommand { get; }

    public string AutomationName { get; }
}

/// <summary>A line of text as the gate sent it, in pieces so each redacted value can be marked.</summary>
public sealed record AuditLine(ImmutableArray<RedactedSegment> Segments);

/// <summary>A labelled value of a detail panel (<c>.kv</c>): "when 14:03:52 · turn 12, step 3".</summary>
public sealed record AuditField(string Label, IReadOnlyList<AuditSpan> Value);

/// <summary>The egress ledger's panel for one request (Audit.png's right column, docs/plan/ui.md F8).</summary>
public sealed class EgressView : ObservableObject
{
    private bool _isFull;

    internal EgressView(EgressRequest request, TimeZoneInfo zone)
    {
        Request = request;
        Title = string.Create(CultureInfo.InvariantCulture, $"Egress ledger · request {request.Number} → {SafeText.Clean(request.Slot)}");
        When = string.Create(CultureInfo.InvariantCulture, $"{AuditText.Time(request.At, zone)} · turn {request.Turn}, step {request.Step}");
        Model = SafeText.Clean(request.Model);
        // Built span by span, never parsed: the secret's name and the host are data.
        // A gated local server takes no Vault secret: the request goes to it directly.
        Route = request.Secret.Length > 0
            ? [new AuditSpan("via Vault "), new AuditSpan(SafeText.Clean(request.Secret), AuditSpanStyle.Mono), new AuditSpan(", " + SafeText.Clean(request.Host))]
            : [new AuditSpan("direct to "), new AuditSpan(SafeText.Clean(request.Host.Length > 0 ? request.Host : "the model's endpoint"))];
        // The gate knows the request's size, not the provider's token count or cache: those show only when known.
        Sent = AuditText.Size(request.Bytes)
            + (request.Tokens > 0 ? $" · {AuditText.Count(request.Tokens)} tokens" : "")
            + (request.CachedPrefix > 0 ? $" · cached prefix {AuditText.Count(request.CachedPrefix)}" : "");
        RedactedTag = request.Redacted == 1 ? "1 value" : string.Create(CultureInfo.InvariantCulture, $"{request.Redacted} values");
        var markers = AuditText.CountRedactions(request.Full);
        RedactionMismatch = markers == request.Redacted ? ""
            : string.Create(CultureInfo.InvariantCulture,
                $"The request as sent holds {markers} redaction {(markers == 1 ? "marker" : "markers")} but the gate recorded {request.Redacted}: a marker may be text a tool or the model wrote, or a redaction may be unmarked. Read it with care.");
        ToggleFullCommand = new RelayCommand(() => IsFull = !IsFull);
    }

    public EgressRequest Request { get; }

    /// <summary>"Egress ledger · request 7 → claude".</summary>
    public string Title { get; }

    public string When { get; }

    public string Model { get; }

    /// <summary>"via Vault <c>anthropic.key</c>, api.anthropic.com".</summary>
    public IReadOnlyList<AuditSpan> Route { get; }

    /// <summary>"38.2 KB · 9,410 tokens · cached prefix 6,120".</summary>
    public string Sent { get; }

    /// <summary>"2 values", the amber tag.</summary>
    public string RedactedTag { get; }

    public bool HasRedactions => Request.Redacted > 0;

    /// <summary>
    /// The markers in the request as sent do not add up to the gate's count: a warning, since a marker is read back out
    /// of text a tool or the model could have written. Empty when they agree.
    /// </summary>
    public string RedactionMismatch { get; }

    public bool HasRedactionMismatch => RedactionMismatch.Length > 0;

    public IReadOnlyList<AuditSpan> RedactedSummary => Request.RedactedSummary;

    /// <summary>Showing the whole request ("Open as sent") rather than its excerpt.</summary>
    public bool IsFull
    {
        get => _isFull;
        private set
        {
            if (!SetProperty(ref _isFull, value))
                return;
            OnPropertyChanged(nameof(Lines));
            OnPropertyChanged(nameof(Text));
            OnPropertyChanged(nameof(ShownLabel));
            OnPropertyChanged(nameof(ToggleLabel));
        }
    }

    /// <summary>
    /// The request as sent, line by line, with each redaction marked. Nothing is removed, since all of it left the
    /// machine: a hidden character is shown as its code (⟨U+202E⟩), the joiners Persian and emoji need excepted.
    /// </summary>
    public IReadOnlyList<AuditLine> Lines => [.. Text.Split('\n').Select(line => new AuditLine(
        [.. AuditText.Redactions(line.TrimEnd('\r')).Select(segment => segment.IsRedaction ? segment
            : segment with { Text = SafeText.Reveal(segment.Text, keepJoiners: true) })]))];

    /// <summary>What Copy puts on the clipboard: the text as sent, markers and all, nothing revealed or removed.</summary>
    public string Text => IsFull ? Request.Full : Request.Excerpt;

    /// <summary>"excerpt", or "full request · 38.2 KB" once opened.</summary>
    public string ShownLabel => IsFull ? $"full request · {AuditText.Size(Request.Bytes)}" : "excerpt";

    /// <summary>"Open as sent", or "Show the excerpt" to go back.</summary>
    public string ToggleLabel => IsFull ? "Show the excerpt" : "Open as sent";

    public RelayCommand ToggleFullCommand { get; }

    public string Note => "rendered by the same gate, on demand (G-9)";
}

/// <summary>A selected event that sent nothing out: its fields and the raw event (docs/plan/ui.md F21).</summary>
public sealed class EventDetailView
{
    internal EventDetailView(AuditEvent auditEvent, TimeZoneInfo zone, string sessionLabel)
    {
        Event = auditEvent;
        Title = string.Create(CultureInfo.InvariantCulture, $"Event {auditEvent.Sequence} · {SafeText.Clean(auditEvent.Type)}");
        Fields =
        [
            new AuditField("when", [new AuditSpan(AuditText.Time(auditEvent.At, zone))]),
            new AuditField("session", [new AuditSpan(SafeText.Clean(sessionLabel))]),
            new AuditField("filter", [new AuditSpan(AuditText.GroupLabel(auditEvent.Group))]),
        ];
        // The event as stored, nothing removed: a hidden character in a field shows as its code.
        Lines = [.. auditEvent.Json.Split('\n').Select(line =>
            new AuditLine([new RedactedSegment(SafeText.Reveal(line.TrimEnd('\r'), keepJoiners: true), false)]))];
    }

    public AuditEvent Event { get; }

    /// <summary>"Event 1189 · tool_call_issued".</summary>
    public string Title { get; }

    public bool IsAlert => Event.IsAlert;

    public IReadOnlyList<AuditField> Fields { get; }

    public IReadOnlyList<AuditSpan> Summary => Event.Summary;

    /// <summary>The raw event as stored, as indented JSON.</summary>
    public string Json => Event.Json;

    public IReadOnlyList<AuditLine> Lines { get; }
}

/// <summary>A link to one of a detection's events ("1182").</summary>
public sealed record AuditEventLink(long Sequence, string Text, RelayCommand Open);

/// <summary>The detection panel: the match, the rule, its events and the last scan (docs/plan/ui.md F10).</summary>
public sealed class AuditDetectionView
{
    internal AuditDetectionView(DetectionMatch match, IReadOnlyList<AuditEventLink> events)
    {
        Match = match;
        Rule = SafeText.Clean(match.RuleId);
        Title = $"Detection · {Rule}";
        Tag = $"{SafeText.Clean(match.Severity)} · {SafeText.Clean(match.Posture)}";
        RuleDetail = string.Create(CultureInfo.InvariantCulture, $"v{match.Version} · {SafeText.Clean(match.Pack)}");
        Events = events;
    }

    public DetectionMatch Match { get; }

    /// <summary>"Detection · credential-read-then-egress".</summary>
    public string Title { get; }

    /// <summary>"high · enforce".</summary>
    public string Tag { get; }

    public IReadOnlyList<AuditSpan> Explanation => Match.Explanation;

    public string Rule { get; }

    /// <summary>"v3 · built-in pack".</summary>
    public string RuleDetail { get; }

    /// <summary>The matched events and the match itself, oldest first; each filters the log to them and selects itself.</summary>
    public IReadOnlyList<AuditEventLink> Events { get; }
}
