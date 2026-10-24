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
        SelectCommand = new RelayCommand(() => select(this));
        AutomationName = $"Event {SequenceText}, {TimeText}, {auditEvent.Type}: {AuditText.Plain(auditEvent.Summary)}";
    }

    public AuditEvent Event { get; }

    public long Sequence => Event.Sequence;

    public string SequenceText { get; }

    public string TimeText { get; }

    public string Type => Event.Type;

    public ImmutableArray<AuditSpan> Summary => Event.Summary;

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
public sealed record AuditField(string Label, ImmutableArray<AuditSpan> Value);

/// <summary>The egress ledger's panel for one request (Audit.png's right column, docs/plan/ui.md F8).</summary>
public sealed class EgressView : ObservableObject
{
    private bool _isFull;

    internal EgressView(EgressRequest request, TimeZoneInfo zone)
    {
        Request = request;
        Title = string.Create(CultureInfo.InvariantCulture, $"Egress ledger · request {request.Number} → {request.Slot}");
        When = string.Create(CultureInfo.InvariantCulture, $"{AuditText.Time(request.At, zone)} · turn {request.Turn}, step {request.Step}");
        Route = AuditText.Parse($"via Vault `{request.Secret}`, {request.Host}");
        Sent = $"{AuditText.Size(request.Bytes)} · {AuditText.Count(request.Tokens)} tokens · cached prefix {AuditText.Count(request.CachedPrefix)}";
        RedactedTag = request.Redacted == 1 ? "1 value" : string.Create(CultureInfo.InvariantCulture, $"{request.Redacted} values");
        ToggleFullCommand = new RelayCommand(() => IsFull = !IsFull);
    }

    public EgressRequest Request { get; }

    /// <summary>"Egress ledger · request 7 → claude".</summary>
    public string Title { get; }

    public string When { get; }

    public string Model => Request.Model;

    /// <summary>"via Vault <c>anthropic.key</c>, api.anthropic.com".</summary>
    public ImmutableArray<AuditSpan> Route { get; }

    /// <summary>"38.2 KB · 9,410 tokens · cached prefix 6,120".</summary>
    public string Sent { get; }

    /// <summary>"2 values", the amber tag.</summary>
    public string RedactedTag { get; }

    public bool HasRedactions => Request.Redacted > 0;

    public ImmutableArray<AuditSpan> RedactedSummary => Request.RedactedSummary;

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

    /// <summary>The request as sent, line by line, with each redaction marked.</summary>
    public IReadOnlyList<AuditLine> Lines => [.. Text.Split('\n').Select(line => new AuditLine(AuditText.Redactions(line)))];

    /// <summary>What Copy puts on the clipboard: the text shown, markers and all.</summary>
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
        Title = string.Create(CultureInfo.InvariantCulture, $"Event {auditEvent.Sequence} · {auditEvent.Type}");
        Fields =
        [
            new AuditField("when", [new AuditSpan(AuditText.Time(auditEvent.At, zone))]),
            new AuditField("session", [new AuditSpan(sessionLabel)]),
            new AuditField("filter", [new AuditSpan(AuditText.GroupLabel(auditEvent.Group))]),
        ];
        Lines = [.. auditEvent.Json.Split('\n').Select(line => new AuditLine([new RedactedSegment(line, false)]))];
    }

    public AuditEvent Event { get; }

    /// <summary>"Event 1189 · tool_call_issued".</summary>
    public string Title { get; }

    public bool IsAlert => Event.IsAlert;

    public IReadOnlyList<AuditField> Fields { get; }

    public ImmutableArray<AuditSpan> Summary => Event.Summary;

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
        Title = $"Detection · {match.RuleId}";
        Tag = $"{match.Severity} · {match.Posture}";
        RuleDetail = string.Create(CultureInfo.InvariantCulture, $"v{match.Version} · {match.Pack}");
        Events = events;
    }

    public DetectionMatch Match { get; }

    /// <summary>"Detection · credential-read-then-egress".</summary>
    public string Title { get; }

    /// <summary>"high · enforce".</summary>
    public string Tag { get; }

    public ImmutableArray<AuditSpan> Explanation => Match.Explanation;

    public string Rule => Match.RuleId;

    /// <summary>"v3 · built-in pack".</summary>
    public string RuleDetail { get; }

    /// <summary>The matched events and the match itself, oldest first; each filters the log to them and selects itself.</summary>
    public IReadOnlyList<AuditEventLink> Events { get; }
}
