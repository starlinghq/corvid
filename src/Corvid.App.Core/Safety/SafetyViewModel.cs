using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Safety;

/// <summary>Which remembered rules the Rules table shows.</summary>
public enum RuleFilter
{
    All,
    Session,
    Always,
}

/// <summary>
/// The Safety area (docs/plan/ui.md §4.4, F9, F10): the approval mode of the current session with what each mode does;
/// the remembered rules with their scope and source, removable (the approval card says "Listed in Safety → Rules,
/// removable there"); the detection pack with its enforce switch, its rules' severity and posture, and every match so
/// far linked into Audit; the hardline list; and each session's taint with what it changes. Every failure shows as a red
/// notice. While the page shows, it holds the status bar (<see cref="StatusBarLease"/>) for the current session's mode
/// and tags, and gives it back when another area shows.
/// </summary>
public sealed class SafetyViewModel : AreaViewModel, IDisposable
{
    /// <summary>The most rule rows drawn at once; the note says how many more there are.</summary>
    public const int MaxRuleRows = 200;

    /// <summary>The most detection matches drawn at once; Audit lists the rest.</summary>
    public const int MaxMatchRows = 100;

    /// <summary>The most sessions the Taint panel draws at once, tainted ones first.</summary>
    public const int MaxTaintRows = 50;

    private readonly ISafetyService _service;
    private readonly ISafetySession _session;
    private readonly ShellViewModel _shell;
    private readonly TimeProvider _time;
    private readonly StatusBarLease _lease;
    private readonly ObservableCollection<RuleRowView> _rules = [];
    private SafetySnapshot? _snapshot;
    private RuleFilter _filter = RuleFilter.All;
    private IReadOnlyList<TextRun> _notice = [];
    private Tone _noticeTone = Tone.Green;
    private bool _loading;
    private bool _reloadAgain;
    private bool _disposed;
    private string _rulesNote = "";
    private string _hiddenRules = "";
    private bool _enforce = true;
    private bool _isAskingRecordOnly;

    public SafetyViewModel(ISafetyService service, ISafetySession session, ShellViewModel shell, StatusBarViewModel statusBar, TimeProvider time)
        : base(Area.Safety, "Approval modes, session and always rules, the detection rules and their matches, and what Corvid never does.")
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(statusBar);
        ArgumentNullException.ThrowIfNull(time);
        _service = service;
        _session = session;
        _shell = shell;
        _time = time;
        _lease = new StatusBarLease(statusBar);

        Modes = [.. Enum.GetValues<ApprovalMode>().Select(mode => new Choice(
            mode.ToString().ToLowerInvariant(),
            Sentence(StatusBarViewModel.Describe(mode)),
            new RelayCommand(() => _session.RequestMode(mode), () => CanChangeMode)))];
        Filters = [.. Enum.GetValues<RuleFilter>().Select(filter => new Choice(FilterLabel(filter), FilterHelp(filter), new RelayCommand(() => Filter = filter)))];
        Filters[0].IsSelected = true;
        ToggleEnforceCommand = new AsyncRelayCommand(ToggleEnforceAsync, () => _snapshot is not null);
        ConfirmRecordOnlyCommand = new AsyncRelayCommand(ct => SetEnforceAsync(false, ct), () => _isAskingRecordOnly);
        CancelRecordOnlyCommand = new RelayCommand(() => IsAskingRecordOnly = false);
        RefreshCommand = new AsyncRelayCommand(ct => ReloadAsync(ct));
        DismissNoticeCommand = new RelayCommand(() => SetNotice([], Tone.Green));
        Rules = new ReadOnlyObservableCollection<RuleRowView>(_rules);

        _service.Changed += OnServiceChanged;
        _session.Changed += OnSessionChanged;
        _shell.PropertyChanged += OnShellChanged;
        RefreshMode();
        Loaded = ReloadAsync(CancellationToken.None);
        UpdateLease();
    }

    /// <summary>The first load; tests await it.</summary>
    public Task Loaded { get; }

    public string Subtitle => "rules are remembered answers · detections watch the log · the hardline list applies in every mode";

    // ---- the approval mode ----

    /// <summary>Manual, Smart, Permissive, each with its sentence; the current session's is selected.</summary>
    public IReadOnlyList<Choice> Modes { get; }

    /// <summary>"`cli-9f2e` · Fix the flaky sandbox test on CI", or why no mode can be set.</summary>
    public IReadOnlyList<TextRun> ModeSession { get; private set; } = [];

    /// <summary>Why the mode cannot change from here, when it cannot; empty otherwise.</summary>
    public string ModeNote { get; private set; } = "";

    public bool HasModeNote => ModeNote.Length > 0;

    public bool CanChangeMode => _session.SessionId is not null && !_session.ReadOnly && _session.Mode is not null;

    // ---- remembered rules ----

    /// <summary>All, Session, Always.</summary>
    public IReadOnlyList<Choice> Filters { get; }

    public RuleFilter Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value))
                return;
            for (var i = 0; i < Filters.Count; i++)
                Filters[i].IsSelected = i == (int)value;
            RebuildRules();
        }
    }

    /// <summary>The rules the filter shows, at most <see cref="MaxRuleRows"/>.</summary>
    public ReadOnlyObservableCollection<RuleRowView> Rules { get; }

    /// <summary>"5 rules · 2 session, 3 always".</summary>
    public string RulesNote
    {
        get => _rulesNote;
        private set => SetProperty(ref _rulesNote, value);
    }

    /// <summary>"and 40 more, not drawn: narrow with the filter", when the cap hides rows.</summary>
    public string HiddenRules
    {
        get => _hiddenRules;
        private set
        {
            if (SetProperty(ref _hiddenRules, value))
                OnPropertyChanged(nameof(HasHiddenRules));
        }
    }

    public bool HasHiddenRules => _hiddenRules.Length > 0;

    public bool HasNoRules => _rules.Count == 0;

    /// <summary>The empty table's line: what writes a rule.</summary>
    public string NoRulesText => _filter switch
    {
        RuleFilter.Session => "No session rules. Answering \"this session\" on an approval card writes one for that session.",
        RuleFilter.Always => "No always rules. Answering \"always…\" on an approval card previews one before it is written.",
        _ => "No remembered rules. Answering \"this session\" or \"always…\" on an approval card writes one.",
    };

    // ---- detections ----

    public IReadOnlyList<DetectionRuleRowView> DetectionRules { get; private set; } = [];

    /// <summary>The matches, newest first, at most <see cref="MaxMatchRows"/>.</summary>
    public IReadOnlyList<DetectionMatchRowView> Matches { get; private set; } = [];

    public bool HasMatches => Matches.Count > 0;

    /// <summary>"and 12 more, not drawn: Audit lists every match", when the cap hides some.</summary>
    public string HiddenMatches { get; private set; } = "";

    public bool HasHiddenMatches => HiddenMatches.Length > 0;

    /// <summary>"built-in pack · 12 rules · 5 enforce".</summary>
    public string PackNote { get; private set; } = "";

    /// <summary>The pack's enforce switch (<c>Detections:Enforce</c>).</summary>
    public bool Enforce
    {
        get => _enforce;
        private set
        {
            if (SetProperty(ref _enforce, value))
            {
                OnPropertyChanged(nameof(Posture));
                OnPropertyChanged(nameof(PostureTone));
                OnPropertyChanged(nameof(EnforceLabel));
                OnPropertyChanged(nameof(EnforceTooltip));
            }
        }
    }

    /// <summary>"enforcing", or "recording only" with the switch off (F10).</summary>
    public string Posture => _enforce ? "enforcing" : "recording only";

    public Tone PostureTone => _enforce ? Tone.Green : Tone.Amber;

    public string EnforceLabel => _enforce ? "Record only…" : "Enforce";

    /// <summary>What the switch's button does from here: turning enforcement off asks first; turning it on does not.</summary>
    public string EnforceTooltip => _enforce
        ? "Detections:Enforce off: every match is recorded and none forces an ask, in every session. Asks first."
        : "Detections:Enforce on: a match of an enforcing rule forces an ask again.";

    /// <summary>
    /// Record only… while enforcing opens a question (it turns every session's forced asks off at once); Enforce while
    /// recording only turns enforcement back on at once.
    /// </summary>
    public AsyncRelayCommand ToggleEnforceCommand { get; }

    /// <summary>The question Record only… opened is showing.</summary>
    public bool IsAskingRecordOnly
    {
        get => _isAskingRecordOnly;
        private set
        {
            if (!SetProperty(ref _isAskingRecordOnly, value))
                return;
            OnPropertyChanged(nameof(RecordOnlyQuestion));
            ConfirmRecordOnlyCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>"Record only? These 5 rules stop forcing an ask, in every session: …", naming the enforcing rules.</summary>
    public string RecordOnlyQuestion
    {
        get
        {
            var enforcing = _snapshot?.Packs.FirstOrDefault()?.Rules.Where(rule => rule.Enforces).Select(rule => SafeText.Clean(rule.Id)).ToList() ?? [];
            var which = enforcing.Count switch
            {
                0 => "No rule enforces now, so nothing changes until one does",
                1 => $"This rule stops forcing an ask, in every session: {enforcing[0]}",
                var n => string.Create(CultureInfo.InvariantCulture, $"These {n} rules stop forcing an ask, in every session: {string.Join(", ", enforcing)}"),
            };
            return $"Record only? {which}. Matches are still logged and listed here.";
        }
    }

    /// <summary>The question's "Record only": writes <c>Detections:Enforce=false</c>.</summary>
    public AsyncRelayCommand ConfirmRecordOnlyCommand { get; }

    /// <summary>The question's "Keep enforcing" (and Esc).</summary>
    public RelayCommand CancelRecordOnlyCommand { get; }

    /// <summary>"1 match · 1 forced an ask", "No matches so far.".</summary>
    public string MatchesNote { get; private set; } = "";

    // ---- hardline and taint ----

    public IReadOnlyList<HardlineRowView> Hardline { get; private set; } = [];

    /// <summary>Each session's taint, tainted first, at most <see cref="MaxTaintRows"/>.</summary>
    public IReadOnlyList<TaintRowView> Taint { get; private set; } = [];

    /// <summary>"and 30 more sessions, not drawn", when the cap hides some.</summary>
    public string HiddenTaint { get; private set; } = "";

    public bool HasHiddenTaint => HiddenTaint.Length > 0;

    /// <summary>The taint explainer's facts: what it changes, how it is shown, when it clears.</summary>
    public IReadOnlyList<ModelFact> TaintFacts { get; } =
    [
        new("enters", Spans.Parse("when a tool brings outside content into the session: `web_fetch`, a file from outside the workspace, an MCP result")),
        new("changes", Spans.Parse("network reads ask; egress and new recipients offer only **once**, never a session or always rule")),
        new("shows", Spans.Parse("an amber row in the transcript, the **tainted** tag in the status bar, and the source on every card")),
        new("clears", Spans.Parse("when the kernel says so: a rewind to before it entered, or a new session")),
    ];

    // ---- the notice ----

    public IReadOnlyList<TextRun> Notice
    {
        get => _notice;
        private set => SetProperty(ref _notice, value);
    }

    public Tone NoticeTone
    {
        get => _noticeTone;
        private set => SetProperty(ref _noticeTone, value);
    }

    public bool HasNotice => _notice.Count > 0;

    public RelayCommand DismissNoticeCommand { get; }

    /// <summary>Reads everything again from the engine.</summary>
    public AsyncRelayCommand RefreshCommand { get; }

    /// <summary>The row asking whether to remove its rule, if one is.</summary>
    public RuleRowView? Confirming => _rules.FirstOrDefault(row => row.IsConfirming);

    /// <summary>Esc: a row asking to remove its rule, or the Record only… question, stops asking. False when none was.</summary>
    public bool CancelConfirm()
    {
        var cancelled = false;
        if (_isAskingRecordOnly)
        {
            IsAskingRecordOnly = false;
            cancelled = true;
        }

        if (Confirming is { } row)
        {
            row.IsConfirming = false;
            cancelled = true;
        }

        return cancelled;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _service.Changed -= OnServiceChanged;
        _session.Changed -= OnSessionChanged;
        _shell.PropertyChanged -= OnShellChanged;
        _lease.Release();
    }

    // ---- loading ----

    /// <summary>Loads the snapshot and rebuilds every list once. A change heard while a load runs loads again after it.</summary>
    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (_loading)
        {
            _reloadAgain = true;
            return;
        }

        _loading = true;
        try
        {
            do
            {
                _reloadAgain = false;
                SafetySnapshot snapshot;
                try
                {
                    snapshot = await _service.LoadAsync(cancellationToken).ConfigureAwait(true);
                }
                catch (Exception exception) when (Failed(exception))
                {
                    Fail("The safety settings could not be read", exception);
                    return;
                }

                if (_disposed)
                    return;
                Apply(snapshot);
            }
            while (_reloadAgain && !_disposed);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Apply(SafetySnapshot snapshot)
    {
        _snapshot = snapshot;
        RebuildRules();

        var pack = snapshot.Packs.FirstOrDefault();
        Enforce = pack?.Enforce ?? false;
        var matchCounts = snapshot.Detections.GroupBy(d => d.RuleId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        DetectionRules = pack is null
            ? []
            : [.. pack.Rules.Select(rule => new DetectionRuleRowView(rule, pack.Enforce, matchCounts.GetValueOrDefault(rule.Id)))];
        PackNote = pack is null
            ? "no pack loaded"
            : string.Create(CultureInfo.InvariantCulture, $"{SafeText.Clean(pack.Name)} · {pack.Rules.Length} rules · {pack.Rules.Count(r => r.Enforces)} enforce");
        Matches = [.. snapshot.Detections.Take(MaxMatchRows).Select(match => new DetectionMatchRowView(match, _time, OpenInAudit))];
        HiddenMatches = snapshot.Detections.Length > MaxMatchRows
            ? string.Create(CultureInfo.InvariantCulture, $"and {snapshot.Detections.Length - MaxMatchRows} older, not drawn: Audit lists every match")
            : "";
        var forced = snapshot.Detections.Count(d => d.Enforced);
        MatchesNote = snapshot.Detections.Length switch
        {
            0 => "No matches so far.",
            1 => forced == 1 ? "1 match · it forced an ask" : "1 match · recorded only",
            var n => string.Create(CultureInfo.InvariantCulture, $"{n} matches · {forced} forced an ask"),
        };
        Hardline = [.. snapshot.Hardline.Select(code => new HardlineRowView(code))];
        Taint = [.. snapshot.Taint.OrderByDescending(taint => taint.Tainted).Take(MaxTaintRows).Select(taint => new TaintRowView(taint, _time))];
        HiddenTaint = snapshot.Taint.Length > MaxTaintRows
            ? string.Create(CultureInfo.InvariantCulture, $"and {snapshot.Taint.Length - MaxTaintRows} more sessions, not drawn")
            : "";
        // The question names what stops forcing an ask; with enforcement already off there is nothing left to ask.
        if (!_enforce)
            IsAskingRecordOnly = false;

        OnPropertyChanged(nameof(DetectionRules));
        OnPropertyChanged(nameof(PackNote));
        OnPropertyChanged(nameof(Matches));
        OnPropertyChanged(nameof(HasMatches));
        OnPropertyChanged(nameof(HiddenMatches));
        OnPropertyChanged(nameof(HasHiddenMatches));
        OnPropertyChanged(nameof(MatchesNote));
        OnPropertyChanged(nameof(Hardline));
        OnPropertyChanged(nameof(Taint));
        OnPropertyChanged(nameof(HiddenTaint));
        OnPropertyChanged(nameof(HasHiddenTaint));
        OnPropertyChanged(nameof(RecordOnlyQuestion));
        ToggleEnforceCommand.NotifyCanExecuteChanged();
        UpdateLease();
    }

    /// <summary>
    /// The Rules table for the filter: cleared and filled once, capped. A reload comes with every change the sessions
    /// report (a rule's uses move while they run), so an open "Remove the rule?" stays open on the rebuilt row.
    /// </summary>
    private void RebuildRules()
    {
        var asking = Confirming?.Rule.Id;
        var all = _snapshot?.Rules ?? [];
        var shown = all.Where(rule => _filter switch
        {
            RuleFilter.Session => rule.Scope == RuleScope.Session,
            RuleFilter.Always => rule.Scope == RuleScope.Always,
            _ => true,
        }).OrderBy(rule => rule.Scope).ThenByDescending(rule => rule.WrittenAt ?? DateTimeOffset.MinValue).ToList();

        _rules.Clear();
        foreach (var rule in shown.Take(MaxRuleRows))
        {
            var row = new RuleRowView(rule, _time, RemoveAsync, OnConfirming);
            _rules.Add(row);
            if (asking is not null && rule.Id == asking)
                row.IsConfirming = true;
        }

        HiddenRules = shown.Count > MaxRuleRows
            ? string.Create(CultureInfo.InvariantCulture, $"and {shown.Count - MaxRuleRows} more, not drawn: narrow the list with the filter")
            : "";

        var sessionCount = all.Count(rule => rule.Scope == RuleScope.Session);
        var alwaysCount = all.Length - sessionCount;
        Filters[0].IsSelected = _filter == RuleFilter.All;
        RulesNote = all.Length == 1
            ? $"1 rule · {(sessionCount == 1 ? "session" : "always")}"
            : string.Create(CultureInfo.InvariantCulture, $"{all.Length} rules · {sessionCount} session, {alwaysCount} always");
        OnPropertyChanged(nameof(HasNoRules));
        OnPropertyChanged(nameof(NoRulesText));
        OnPropertyChanged(nameof(Confirming));
    }

    /// <summary>One row asks at a time: another row's question closes.</summary>
    private void OnConfirming(RuleRowView row)
    {
        IsAskingRecordOnly = false;
        foreach (var other in _rules)
        {
            if (!ReferenceEquals(other, row) && other.IsConfirming)
                other.IsConfirming = false;
        }

        OnPropertyChanged(nameof(Confirming));
    }

    private async Task RemoveAsync(RuleRowView row, CancellationToken cancellationToken)
    {
        var rule = row.Rule;
        try
        {
            await _service.RemoveRuleAsync(rule.Id, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            row.IsConfirming = false;
            Fail("The rule could not be removed", exception);
            return;
        }

        var where = rule.Scope == RuleScope.Session && rule.SessionId is { } session
            ? $"from `{Spans.Escape(SafeText.Clean(session))}`"
            : "from corvid.json";
        SetNotice(Spans.Parse($"Removed the {row.Scope} rule {row.Decision} {Spans.Escape(row.Plain)} {where}. The next such call asks again."), Tone.Green);
        await ReloadAsync(cancellationToken).ConfigureAwait(true);
    }

    private Task ToggleEnforceAsync(CancellationToken cancellationToken)
    {
        if (_snapshot?.Packs.FirstOrDefault() is not { } pack)
            return Task.CompletedTask;
        if (!pack.Enforce)
            return SetEnforceAsync(true, cancellationToken);
        // Off turns every session's forced asks off at once: it asks, naming what stops.
        foreach (var row in _rules)
            row.IsConfirming = false;
        IsAskingRecordOnly = true;
        OnPropertyChanged(nameof(Confirming));
        return Task.CompletedTask;
    }

    private async Task SetEnforceAsync(bool next, CancellationToken cancellationToken)
    {
        IsAskingRecordOnly = false;
        if (_snapshot?.Packs.FirstOrDefault() is not { } pack || pack.Enforce == next)
            return;
        try
        {
            await _service.SetEnforceAsync(pack.Name, next, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail("The detection switch could not be changed", exception);
            return;
        }

        SetNotice(Spans.Parse(next
            ? "Detections enforce again: `Detections:Enforce=true`. A match of an enforcing rule forces an ask."
            : "Detections record only: `Detections:Enforce=false`. Matches are logged and listed here; none forces an ask."), next ? Tone.Green : Tone.Amber);
        await ReloadAsync(cancellationToken).ConfigureAwait(true);
    }

    private void OpenInAudit(SafetyDetection match) =>
        // No rule name: Audit lands on the match whose events these are, whatever its demo names the rule.
        _shell.Follow(new AreaLink(Area.Audit, match.SessionId, AreaLinkTarget.Detection) { Events = [.. match.Events] });

    // ---- the session and the status bar ----

    private void RefreshMode()
    {
        var mode = _session.Mode;
        foreach (var (choice, value) in Modes.Zip(Enum.GetValues<ApprovalMode>()))
            choice.IsSelected = value == mode;
        ModeSession = _session.SessionId is { } id
            ? Spans.Parse($"`{Spans.Escape(SafeText.Clean(id))}` · {Spans.Escape(SafeText.Clean(_session.Title))}")
            : Spans.Parse("no session open");
        ModeNote = _session.SessionId is null
            ? "Open a session to set its mode; a new session starts in the mode corvid.json names."
            : _session.ReadOnly
                ? "Another process drives this session: its mode is changed there."
                : "";
        OnPropertyChanged(nameof(ModeSession));
        OnPropertyChanged(nameof(ModeNote));
        OnPropertyChanged(nameof(HasModeNote));
        OnPropertyChanged(nameof(CanChangeMode));
        foreach (var choice in Modes)
            ((RelayCommand)choice.Choose).NotifyCanExecuteChanged();
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        RefreshMode();
        UpdateLease();
    }

    private void OnServiceChanged(object? sender, EventArgs e) => _ = ReloadAsync(CancellationToken.None);

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentArea))
            UpdateLease();
    }

    /// <summary>
    /// On Safety the status bar shows the current session's mode, so its segments work here as in a session (Shift+Tab
    /// stays focus navigation on this page; the mode panel's segments are the keyboard's way), and its tags (tainted), and
    /// the detections' posture and matches; another area gets it back as it was.
    /// </summary>
    private void UpdateLease()
    {
        if (_shell.CurrentArea != Area.Safety || _disposed)
        {
            _lease.Release();
            return;
        }

        _lease.Take();
        _lease.Engine = "";
        _lease.Mode = _session.Mode;
        _lease.Tags = [.. _session.Tags];
        _lease.SessionId = _session.SessionId ?? "";
        var matches = _snapshot?.Detections.Length ?? 0;
        _lease.AreaFields =
        [
            new StatusField("detections", $"{(_enforce ? "enforcing" : "recording only")} · {matches} {(matches == 1 ? "match" : "matches")}"),
        ];
    }

    private void SetNotice(IReadOnlyList<TextRun> text, Tone tone)
    {
        NoticeTone = tone;
        Notice = text;
        OnPropertyChanged(nameof(HasNotice));
    }

    private void Fail(string what, Exception exception) =>
        SetNotice(Spans.Parse($"{Spans.Escape(what)}: {Spans.Escape(SafeText.Clean(exception.Message))}"), Tone.Red);

    private static bool Failed(Exception exception) => exception is not OperationCanceledException and not OutOfMemoryException;

    /// <summary>"Manual: every side effect asks." without its label, which the row already shows.</summary>
    private static string Sentence(string description)
    {
        var colon = description.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? description : char.ToUpperInvariant(description[colon + 2]) + description[(colon + 3)..];
    }

    private static string FilterLabel(RuleFilter filter) => filter switch
    {
        RuleFilter.Session => "session",
        RuleFilter.Always => "always",
        _ => "all",
    };

    private static string FilterHelp(RuleFilter filter) => filter switch
    {
        RuleFilter.Session => "Rules for one session, written by answering \"this session\"",
        RuleFilter.Always => "Rules in corvid.json, for every session",
        _ => "Every remembered rule",
    };
}
