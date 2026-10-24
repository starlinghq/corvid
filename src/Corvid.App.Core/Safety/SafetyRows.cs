using System.Globalization;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Safety;

/// <summary>
/// A remembered rule as the Rules table draws it: the decision, the rule's words (cleaned, with its commands in the code
/// font), the scope, where it came from and when, and Remove, which first asks with the rule revealed: what is removed is
/// exactly what is shown, hidden characters included.
/// </summary>
public sealed class RuleRowView : ObservableObject
{
    private readonly Func<RuleRowView, CancellationToken, Task> _remove;
    private readonly Action<RuleRowView> _confirming;
    private bool _isConfirming;

    internal RuleRowView(SafetyRule rule, TimeProvider time, Func<RuleRowView, CancellationToken, Task> remove, Action<RuleRowView> confirming)
    {
        Rule = rule;
        _remove = remove;
        _confirming = confirming;
        // The engine's words are data: built from their pieces, so a tilde or a stray backtick draws as itself.
        Text = SafetyText.Runs(rule.Text);
        Plain = SafetyText.Plain(rule.Text);
        Revealed = SafeText.Reveal(SafetyText.Unmarked(rule.Text));
        HasHidden = SafeText.HasHidden(rule.Text);
        Source = Spans.Parse(SourceMarkup(rule));
        Written = WrittenText(rule.WrittenAt, time);
        Uses = rule.Uses switch
        {
            0 => "not used yet",
            1 => rule.Decision == RuleDecision.Deny ? "refused 1 call" : "1 call",
            var n => string.Create(CultureInfo.InvariantCulture, $"{(rule.Decision == RuleDecision.Deny ? "refused " : "")}{n} calls"),
        };
        StartRemoveCommand = new RelayCommand(() => IsConfirming = true);
        CancelRemoveCommand = new RelayCommand(() => IsConfirming = false);
        RemoveCommand = new AsyncRelayCommand(ct => _remove(this, ct));
    }

    public SafetyRule Rule { get; }

    public string Id => Rule.Id;

    public string Decision => Rule.Decision == RuleDecision.Allow ? "allow" : "deny";

    public Tone DecisionTone => Rule.Decision == RuleDecision.Allow ? Tone.Green : Tone.Red;

    public string Scope => Rule.Scope == RuleScope.Session ? "session" : "always";

    public Tone ScopeTone => Rule.Scope == RuleScope.Session ? Tone.Blue : Tone.Violet;

    public bool IsSessionRule => Rule.Scope == RuleScope.Session;

    /// <summary>The rule's words for display: cleaned, code in the code font.</summary>
    public IReadOnlyList<TextRun> Text { get; }

    /// <summary>The words without marks, cleaned, for a screen reader.</summary>
    public string Plain { get; }

    /// <summary>The words with every hidden character shown, for the removal's confirmation.</summary>
    public string Revealed { get; }

    /// <summary>The rule's words hold characters a reader cannot see; the confirmation says so.</summary>
    public bool HasHidden { get; }

    /// <summary>"a-32 · turn 13 · cli-9f2e", or "corvid.json".</summary>
    public IReadOnlyList<TextRun> Source { get; }

    /// <summary>"14:04" today, "Oct 19" before.</summary>
    public string Written { get; }

    /// <summary>"3 calls", "not used yet".</summary>
    public string Uses { get; }

    public string AutomationName => $"{Decision} {Plain}, {Scope}, {Spans.Plain(SourceMarkup(Rule))}";

    /// <summary>Remove was pressed: the row asks before it removes.</summary>
    public bool IsConfirming
    {
        get => _isConfirming;
        set
        {
            if (!SetProperty(ref _isConfirming, value))
                return;
            OnPropertyChanged(nameof(IsIdle));
            if (value)
                _confirming(this);
        }
    }

    public bool IsIdle => !_isConfirming;

    /// <summary>"Remove the session rule allow rm -r under build/? The next such call asks again."</summary>
    public string ConfirmText =>
        $"Remove the {Scope} rule \"{Decision} {Revealed}\"? "
        + (Rule.Decision == RuleDecision.Allow ? "The next such call asks again." : "The next such call is asked about instead of refused.")
        + (HasHidden ? " It holds hidden characters, shown in brackets." : "");

    public RelayCommand StartRemoveCommand { get; }

    public RelayCommand CancelRemoveCommand { get; }

    public AsyncRelayCommand RemoveCommand { get; }

    private static string SourceMarkup(SafetyRule rule)
    {
        if (rule.ApprovalId is null && rule.SessionId is null)
            return Spans.Escape(SafeText.Clean(rule.Origin ?? "corvid.json"));
        var parts = new List<string>();
        if (rule.ApprovalId is { } approval)
            parts.Add(Spans.Escape(SafeText.Clean(approval)));
        if (rule.Turn is { } turn)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"turn {turn}"));
        if (rule.SessionId is { } session)
            parts.Add($"`{Spans.Escape(SafeText.Clean(session))}`");
        if (rule.Scope == RuleScope.Always)
            parts.Add("corvid.json");
        return string.Join(" · ", parts);
    }

    internal static string WrittenText(DateTimeOffset? at, TimeProvider time)
    {
        if (at is not { } when)
            return "";
        var zone = time.LocalTimeZone;
        var local = TimeZoneInfo.ConvertTime(when, zone);
        var today = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone).Date;
        return local.Date == today
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MMM d", CultureInfo.InvariantCulture);
    }
}

/// <summary>A rule of a detection pack: id and version, what it finds, severity, its posture under the pack's switch, and its matches.</summary>
public sealed class DetectionRuleRowView(DetectionRule rule, bool packEnforces, int matches)
{
    public DetectionRule Rule { get; } = rule;

    public string Id => SafeText.Clean(Rule.Id);

    public string Version => string.Create(CultureInfo.InvariantCulture, $"v{Rule.Version}");

    public string Title => SafeText.Clean(Rule.Title);

    public string Category => SafeText.Clean(Rule.Category);

    public string Severity => SafetyText.Severity(Rule.Severity);

    public Tone SeverityTone => SafetyText.SeverityTone(Rule.Severity);

    /// <summary>"enforce", "detect", or "enforce · recording only" while the pack's switch is off.</summary>
    public string Posture => Rule.Enforces ? packEnforces ? "enforce" : "enforce · recording only" : "detect";

    public bool ForcesAsk => Rule.Enforces && packEnforces;

    public string Matches => matches switch
    {
        0 => "no matches",
        1 => "1 match",
        _ => string.Create(CultureInfo.InvariantCulture, $"{matches} matches"),
    };

    public string AutomationName => $"{Id} {Version}, {Severity}, {Posture}, {Title}, {(matches == 0 ? "no matches" : Matches)}";
}

/// <summary>A match so far: when, where, which rule, the events, what it did, and the link to Audit filtered to them.</summary>
public sealed class DetectionMatchRowView
{
    internal DetectionMatchRowView(SafetyDetection match, TimeProvider time, Action<SafetyDetection> openInAudit)
    {
        Match = match;
        var local = TimeZoneInfo.ConvertTime(match.At, time.LocalTimeZone);
        var today = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).Date;
        At = local.Date == today
            ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : local.ToString("MMM d HH:mm", CultureInfo.InvariantCulture);
        Summary = SafetyText.Runs(match.Summary);
        Events = "events " + string.Join(", ", match.Events.Select(e => e.ToString(CultureInfo.InvariantCulture)));
        OpenInAuditCommand = new RelayCommand(() => openInAudit(match));
    }

    public SafetyDetection Match { get; }

    public string At { get; }

    public string RuleId => SafeText.Clean(Match.RuleId);

    public string SessionId => SafeText.Clean(Match.SessionId);

    public string Severity => SafetyText.Severity(Match.Severity);

    public Tone SeverityTone => SafetyText.SeverityTone(Match.Severity);

    public string Outcome => SafeText.Clean(Match.Outcome ?? (Match.Enforced ? "forced an ask" : "recorded only"));

    public Tone OutcomeTone => Match.Enforced ? Tone.Red : Tone.Grey;

    public string Events { get; }

    /// <summary>What happened, cleaned; commands in the code font.</summary>
    public IReadOnlyList<TextRun> Summary { get; }

    public RelayCommand OpenInAuditCommand { get; }

    public string AutomationName => $"{At}, {RuleId}, {Severity}, {SessionId}, {Events}, {Outcome}";
}

/// <summary>A session's taint as the Taint panel lists it.</summary>
public sealed class TaintRowView
{
    internal TaintRowView(SessionTaint taint, TimeProvider time)
    {
        Taint = taint;
        var zone = time.LocalTimeZone;
        string Clock(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        var source = taint.Source is { Length: > 0 } s ? $"`{Spans.Escape(SafeText.Clean(s))}`" : "outside content";
        Detail = Spans.Parse(taint switch
        {
            { Tainted: true, Since: { } since } => $"since {Clock(since)} · {source}",
            { Tainted: true } => source,
            { ClearedAt: { } cleared, Since: { } since } => $"tainted {Clock(since)} by {source} · cleared {Clock(cleared)}",
            _ => "",
        });
    }

    public SessionTaint Taint { get; }

    public string SessionId => SafeText.Clean(Taint.SessionId);

    public string Title => SafeText.Clean(Taint.Title);

    public string State => Taint.Tainted ? "tainted" : Taint.ClearedAt is not null ? "cleared" : "clean";

    public Tone StateTone => Taint.Tainted ? Tone.Amber : Tone.Grey;

    public bool IsTainted => Taint.Tainted;

    public IReadOnlyList<TextRun> Detail { get; }

    public bool HasDetail => Detail.Count > 0;

    public string AutomationName => $"{SessionId}, {Title}, {State}, {string.Concat(Detail.Select(r => r.Text))}";
}

/// <summary>One code of the hardline list.</summary>
public sealed class HardlineRowView(HardlineCode code)
{
    public string Code { get; } = SafeText.Clean(code.Code);

    public string Sentence { get; } = SafeText.Clean(code.Sentence);

    public string AutomationName => $"{Code}: {Sentence}";
}

/// <summary>Words the Safety page shares between its rows.</summary>
public static class SafetyText
{
    /// <summary>
    /// A rule's or a match's words, which come from the engine with commands and paths in backticks, as runs: each pair of
    /// backticks a code run, everything else plain and taken literally (a tilde, two stars, an unpaired backtick), each
    /// piece cleaned and escaped. A backslash before a backtick or a backslash makes it literal, so a path holding a
    /// backtick can sit in a code span.
    /// </summary>
    public static IReadOnlyList<TextRun> Runs(string? text) =>
        Spans.Parse(string.Concat(Pieces(text).Select(piece => piece.Code
            ? $"`{Spans.Escape(SafeText.Clean(piece.Text))}`"
            : Spans.Escape(SafeText.Clean(piece.Text)))));

    /// <summary>The words without their backticks, cleaned: for a screen reader and the notices.</summary>
    public static string Plain(string? text) => SafeText.Clean(Unmarked(text));

    /// <summary>The words without their backticks and nothing else changed: what a confirmation reveals.</summary>
    public static string Unmarked(string? text) => string.Concat(Pieces(text).Select(piece => piece.Text));

    /// <summary>The words split at their backtick pairs; an unpaired backtick stays in the text.</summary>
    internal static IReadOnlyList<(string Text, bool Code)> Pieces(string? text)
    {
        var pieces = new List<(string Text, bool Code)>();
        if (string.IsNullOrEmpty(text))
            return pieces;
        var current = new System.Text.StringBuilder();
        var code = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (IsEscape(text, i))
            {
                current.Append(text[++i]);
            }
            else if (c == '`' && (code || ClosingTick(text, i + 1)))
            {
                if (current.Length > 0)
                    pieces.Add((current.ToString(), code));
                current.Clear();
                code = !code;
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
            pieces.Add((current.ToString(), code));
        return pieces;
    }

    private static bool IsEscape(string text, int at) =>
        text[at] == '\\' && at + 1 < text.Length && text[at + 1] is '`' or '\\';

    private static bool ClosingTick(string text, int from)
    {
        for (var i = from; i < text.Length; i++)
        {
            if (IsEscape(text, i))
                i++;
            else if (text[i] == '`')
                return true;
        }

        return false;
    }

    public static string Severity(DetectionSeverity severity) => severity.ToString().ToLowerInvariant();

    /// <summary>Red for high and critical, amber for medium, grey for low; the word is always shown beside the colour.</summary>
    public static Tone SeverityTone(DetectionSeverity severity) => severity switch
    {
        DetectionSeverity.Critical or DetectionSeverity.High => Tone.Red,
        DetectionSeverity.Medium => Tone.Amber,
        _ => Tone.Grey,
    };
}
