using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Audit;

/// <summary>
/// The Audit area (docs/plan/ui.md §4.12, Audit.png): one session's event log (or the whole log's) with filters by group
/// and by path, tool or type; the selected event's detail, which for a request that left the machine is its entry in the
/// egress ledger, shown as sent after the privacy gate; the session's detection matches with their events; a scan with
/// the detection rules; and trace export.
/// <para>
/// Links from the Sessions page land here through <see cref="ShellViewModel.Follow"/>: "what left the machine" selects
/// the session's latest request in the egress ledger (F8), a detection's "Open in Audit" filters the log to the events
/// that completed the rule (F10), and "Open in Audit" in the Decisions panel opens the session's log.
/// </para>
/// </summary>
public sealed class AuditViewModel : AreaViewModel
{
    /// <summary>The scan preset the page head's button runs: <c>--fail-on high</c>.</summary>
    public const string FailOn = "high";

    private readonly IAuditService _service;
    private readonly TimeProvider _time;
    private readonly ShellViewModel? _shell;
    private readonly List<AuditEvent> _events = [];
    private readonly Dictionary<long, AuditEventRow> _rows = [];
    private IReadOnlyList<EgressRequest> _egress = [];
    private int _sessionIndex = -1;
    private string _pathFilter = "";
    private HashSet<long>? _match;
    private string _matchLabel = "";
    private AuditEventRow? _selected;
    private EgressView? _egressView;
    private EventDetailView? _detail;
    private AuditDetectionView? _detection;
    private string _lastScan = "";
    private string _logCount = "";
    private string _logPath = "";
    private string _error = "";
    private bool _metadataOnly;
    private bool _includeMemory;
    private string _exportResult = "";
    private int _loads;
    private readonly IAuditSessionStatus? _sessionStatus;
    private string _statusDetections = "";
    private string _statusLog = "";
    private IReadOnlyList<StatusField> _myFields = [];
    private bool _showingStatus;

    public AuditViewModel(IAuditService service, TimeProvider time, ShellViewModel? shell = null, IAuditSessionStatus? sessionStatus = null)
        : base(Area.Audit, "The event log per session, scans with the detection rules, trace export and the egress ledger.")
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(time);
        _service = service;
        _time = time;
        _shell = shell;
        _sessionStatus = sessionStatus;
        Filters =
        [
            .. Enum.GetValues<AuditEventGroup>().Select(group => new AuditFilterItem(group,
                group is not (AuditEventGroup.Checkpoints or AuditEventGroup.MemoryAndSkills or AuditEventGroup.Compaction), Refilter)),
        ];
        ResetCommand = new RelayCommand(Reset);
        ClearMatchCommand = new RelayCommand(() => SetMatch(null, ""), () => HasMatch);
        ClearSelectionCommand = new RelayCommand(() => Select(null), () => _selected is not null);
        ScanCommand = new AsyncRelayCommand(ScanAsync);
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        ShowFullRetentionCommand = new RelayCommand(() => MetadataOnly = false);
        ShowMetadataRetentionCommand = new RelayCommand(() => MetadataOnly = true);
        if (_shell is not null)
            _shell.PropertyChanged += OnShellChanged;
        // The session the window shows, or the link that opened the area; the demo answers at once.
        Loaded = StartAsync();
    }

    /// <summary>Completes when the first load has; tests await it.</summary>
    public Task Loaded { get; }

    // ---- The page head ----

    /// <summary>The session picker: each session, then the whole log.</summary>
    public ObservableCollection<AuditSessionOption> SessionOptions { get; } = [];

    /// <summary>The picked entry of <see cref="SessionOptions"/>; setting it loads that log.</summary>
    public int SessionIndex
    {
        get => _sessionIndex;
        set
        {
            if (value < 0 || value >= SessionOptions.Count || !SetProperty(ref _sessionIndex, value))
                return;
            _ = LoadAsync(null);
        }
    }

    /// <summary>The picked session's id; null for the whole log.</summary>
    public string? SessionId => _sessionIndex >= 0 && _sessionIndex < SessionOptions.Count ? SessionOptions[_sessionIndex].SessionId : null;

    /// <summary>"1,214 events · append-only ·".</summary>
    public string LogCount { get => _logCount; private set => SetProperty(ref _logCount, value); }

    /// <summary>"~/.local/share/corvid/events.db".</summary>
    public string LogPath { get => _logPath; private set => SetProperty(ref _logPath, value); }

    /// <summary>A failed load or action, in one line; empty when all is well.</summary>
    public string Error
    {
        get => _error;
        private set
        {
            if (SetProperty(ref _error, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error.Length > 0;

    // ---- Filter ----

    public IReadOnlyList<AuditFilterItem> Filters { get; }

    /// <summary>
    /// "path or tool": words separated by spaces or commas; an event shows when any word is its type, or is part of its
    /// tool or path ("web_fetch", "SeccompTests", "tool_call_issued approval_resolved").
    /// </summary>
    public string PathFilter
    {
        get => _pathFilter;
        set
        {
            if (SetProperty(ref _pathFilter, value ?? ""))
                Refilter();
        }
    }

    public RelayCommand ResetCommand { get; }

    // ---- Events ----

    /// <summary>The events the filters let through, newest first.</summary>
    public ObservableCollection<AuditEventRow> Rows { get; } = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>What the table shows when the filters let nothing through.</summary>
    public string EmptyText => _events.Count == 0 ? "No events in this log yet." : "No event matches the filters. Reset shows the usual set.";

    /// <summary>The log is narrowed to a detection's events (F10).</summary>
    public bool HasMatch => _match is not null;

    /// <summary>"3 events matched by credential-read-then-egress", or the table's usual note.</summary>
    public string EventsNote => HasMatch ? _matchLabel : "newest first · any transcript row jumps here";

    public RelayCommand ClearMatchCommand { get; }

    public AuditEventRow? SelectedRow => _selected;

    public RelayCommand ClearSelectionCommand { get; }

    // ---- The right column ----

    /// <summary>The selected event sent a request out: its entry in the egress ledger.</summary>
    public EgressView? Egress { get => _egressView; private set => SetProperty(ref _egressView, value); }

    public bool HasEgress => _egressView is not null;

    /// <summary>The selected event, when it sent nothing out: its fields and raw JSON.</summary>
    public EventDetailView? Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    public bool HasDetail => _detail is not null;

    /// <summary>Nothing is selected: the column says how to read an event.</summary>
    public bool HasNoSelection => _selected is null;

    public string NoSelectionText => _egress.Count == 0
        ? "Select an event to read it as stored. Nothing in this log left the machine."
        : string.Create(CultureInfo.InvariantCulture,
            $"Select an event to read it as stored. {(_egress.Count == 1 ? "One request" : $"{_egress.Count} requests")} left the machine; selecting {(_egress.Count == 1 ? "it" : "one")} opens the egress ledger.");

    /// <summary>The session's latest detection match; null when no rule matched.</summary>
    public AuditDetectionView? Detection { get => _detection; private set => SetProperty(ref _detection, value); }

    public bool HasDetection => _detection is not null;

    /// <summary>"14:10 · 1 high, 0 medium · would exit 4 with --fail-on high"; empty before any scan.</summary>
    public string LastScan { get => _lastScan; private set => SetProperty(ref _lastScan, value); }

    public bool HasLastScan => _lastScan.Length > 0;

    /// <summary>"Scan · fail on high": the detection rules over the picked log.</summary>
    public AsyncRelayCommand ScanCommand { get; }

    // ---- Export ----

    /// <summary>Metadata only (names, paths, commands and decisions) rather than full content.</summary>
    public bool MetadataOnly
    {
        get => _metadataOnly;
        set
        {
            if (SetProperty(ref _metadataOnly, value))
                OnPropertyChanged(nameof(FullRetention));
        }
    }

    /// <summary>Full content, secrets redacted, strings cut at 4 KiB.</summary>
    public bool FullRetention
    {
        get => !_metadataOnly;
        set => MetadataOnly = !value;
    }

    public RelayCommand ShowFullRetentionCommand { get; }

    public RelayCommand ShowMetadataRetentionCommand { get; }

    /// <summary>Include local-only memory content (off: memory events stay out of the trace).</summary>
    public bool IncludeMemory { get => _includeMemory; set => SetProperty(ref _includeMemory, value); }

    /// <summary>"Write trace.jsonl (owner-only file)".</summary>
    public AsyncRelayCommand ExportCommand { get; }

    /// <summary>"Wrote 23 events (4.1 KB, metadata only) to …"; empty until a trace is written.</summary>
    public string ExportResult
    {
        get => _exportResult;
        private set
        {
            if (SetProperty(ref _exportResult, value))
                OnPropertyChanged(nameof(HasExportResult));
        }
    }

    public bool HasExportResult => _exportResult.Length > 0;

    // ---- Actions ----

    /// <summary>Selects a row (or nothing); the right column follows. A row that is filtered out cannot be selected.</summary>
    public void Select(AuditEventRow? row)
    {
        if (row is not null && !Rows.Contains(row))
            return;
        if (ReferenceEquals(row, _selected))
            return;
        if (_selected is not null)
            _selected.IsSelected = false;
        _selected = row;
        if (row is not null)
            row.IsSelected = true;
        var request = row?.Event.EgressRequest is { } number ? _egress.FirstOrDefault(e => e.Number == number) : null;
        Egress = request is null ? null : new EgressView(request, _time.LocalTimeZone);
        Detail = row is not null && request is null ? new EventDetailView(row.Event, _time.LocalTimeZone, SessionLabel(row.Event.SessionId)) : null;
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(HasEgress));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(HasNoSelection));
        ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Selects the row of an event by sequence, if the filters show it.</summary>
    public bool SelectSequence(long sequence)
    {
        var row = Rows.FirstOrDefault(r => r.Sequence == sequence);
        Select(row);
        return row is not null;
    }

    /// <summary>Selects the next (+1) or previous (−1) visible row; with nothing selected, the first.</summary>
    public void MoveSelection(int delta)
    {
        if (Rows.Count == 0)
            return;
        var index = _selected is null ? (delta > 0 ? 0 : Rows.Count - 1) : Rows.IndexOf(_selected) + delta;
        Select(Rows[Math.Clamp(index, 0, Rows.Count - 1)]);
    }

    /// <summary>
    /// Narrows the log to a detection's events (the matched ones and the match itself), as "Open in Audit" does, and
    /// selects <paramref name="select"/> when given.
    /// </summary>
    public void ShowMatch(DetectionMatch match, long? select = null)
    {
        ArgumentNullException.ThrowIfNull(match);
        SetMatch([.. match.Events, match.Sequence], MatchLabel(match.Events.Length + 1, match.RuleId));
        if (select is { } sequence)
            SelectSequence(sequence);
    }

    /// <summary>Applies a link from another area (<see cref="ShellViewModel.Follow"/>).</summary>
    public Task FollowAsync(AreaLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var index = link.SessionId is null ? -1 : IndexOf(link.SessionId);
        return LoadAsync(link, index);
    }

    private static string MatchLabel(int count, string? rule) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} events matched by {rule ?? "the detection"}");

    private void Reset()
    {
        foreach (var filter in Filters)
            filter.IsChecked = filter.DefaultChecked;
        PathFilter = "";
        SetMatch(null, "");
    }

    private void SetMatch(IEnumerable<long>? events, string label)
    {
        _match = events is null ? null : [.. events];
        _matchLabel = label;
        OnPropertyChanged(nameof(HasMatch));
        OnPropertyChanged(nameof(EventsNote));
        ClearMatchCommand.NotifyCanExecuteChanged();
        Refilter();
    }

    private bool Shows(AuditEvent auditEvent)
    {
        // A detection's link shows exactly its events, whatever the boxes say.
        if (_match is not null)
            return _match.Contains(auditEvent.Sequence);
        if (!Filters.First(filter => filter.Group == auditEvent.Group).IsChecked)
            return false;
        var words = _pathFilter.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length == 0 || words.Any(word =>
            string.Equals(word, auditEvent.Type, StringComparison.OrdinalIgnoreCase)
            || (auditEvent.Tool?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false)
            || (auditEvent.Path?.Contains(word, StringComparison.OrdinalIgnoreCase) ?? false));
    }

    /// <summary>Rebuilds the table from the loaded events; the selection stays when its row still shows.</summary>
    private void Refilter()
    {
        var selected = _selected?.Sequence;
        Rows.Clear();
        foreach (var auditEvent in _events.Where(Shows))
            Rows.Add(RowFor(auditEvent));
        OnPropertyChanged(nameof(HasRows));
        OnPropertyChanged(nameof(EmptyText));
        var keep = selected is { } sequence ? Rows.FirstOrDefault(row => row.Sequence == sequence) : null;
        if (keep is null)
            Select(null);
    }

    private AuditEventRow RowFor(AuditEvent auditEvent)
    {
        if (!_rows.TryGetValue(auditEvent.Sequence, out var row))
            _rows[auditEvent.Sequence] = row = new AuditEventRow(auditEvent, _time.LocalTimeZone, Select);
        return row;
    }

    private string SessionLabel(string sessionId) =>
        SessionOptions.FirstOrDefault(option => option.SessionId == sessionId)?.Label ?? sessionId;

    private int IndexOf(string sessionId)
    {
        for (var i = 0; i < SessionOptions.Count; i++)
        {
            if (string.Equals(SessionOptions[i].SessionId, sessionId, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private async Task StartAsync()
    {
        try
        {
            var log = await _service.GetLogAsync(CancellationToken.None);
            LogCount = $"{AuditText.Count(log.TotalEvents)} events · append-only ·";
            _statusLog = $"log {AuditText.Count(log.TotalEvents)} events";
            LogPath = log.Path;
            foreach (var session in await _service.ListSessionsAsync(CancellationToken.None))
                SessionOptions.Add(new AuditSessionOption(session.Id, $"{session.Id} · {session.Title}"));
            SessionOptions.Add(new AuditSessionOption(null, "whole log"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = $"The event log could not be read: {exception.Message}";
            return;
        }

        // A link that opened the area wins; otherwise the session the window shows, as Audit.png opens on cli-9f2e.
        if (_shell?.Link is { Area: Area.Audit } link && _shell.CurrentArea == Area.Audit)
        {
            await FollowAsync(link);
            return;
        }

        var index = _shell?.CurrentSession?.Id is { } current ? IndexOf(current) : -1;
        await LoadAsync(new AreaLink(Area.Audit, null, AreaLinkTarget.Egress), index < 0 ? 0 : index);
    }

    private Task LoadAsync(AreaLink? link) => LoadAsync(link, _sessionIndex);

    /// <summary>Loads the picked log, then lands on what the link names (the latest egress request, a detection's events).</summary>
    private async Task LoadAsync(AreaLink? link, int index)
    {
        if (index < 0 && _sessionIndex < 0)
            index = 0;
        if (index >= 0 && index != _sessionIndex)
        {
            _sessionIndex = index;
            OnPropertyChanged(nameof(SessionIndex));
            OnPropertyChanged(nameof(SessionId));
        }

        var load = ++_loads;
        var sessionId = SessionId;
        IReadOnlyList<AuditEvent> events;
        IReadOnlyList<EgressRequest> egress;
        IReadOnlyList<DetectionMatch> detections;
        ScanResult? scan;
        try
        {
            events = await _service.ReadEventsAsync(sessionId, CancellationToken.None);
            egress = await _service.ListEgressAsync(sessionId, CancellationToken.None);
            detections = await _service.ListDetectionsAsync(sessionId, CancellationToken.None);
            scan = await _service.GetLastScanAsync(sessionId, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = $"The event log could not be read: {exception.Message}";
            return;
        }

        // A newer pick answered first; this answer is stale.
        if (load != _loads)
            return;
        Error = "";
        _events.Clear();
        _events.AddRange(events);
        _rows.Clear();
        _egress = egress;
        OnPropertyChanged(nameof(NoSelectionText));
        var match = detections.FirstOrDefault();
        Detection = match is null ? null : new AuditDetectionView(match,
            [.. match.Events.Append(match.Sequence).Select(sequence => new AuditEventLink(sequence,
                sequence.ToString(CultureInfo.InvariantCulture), new RelayCommand(() => ShowMatch(match, sequence))))]);
        OnPropertyChanged(nameof(HasDetection));
        _statusDetections = AuditText.DetectionSummary(detections.Select(detection => detection.Severity), sessionId is not null);
        ApplyStatus();
        SetScan(scan);

        _selected = null;
        Egress = null;
        Detail = null;
        switch (link?.Target)
        {
            case AreaLinkTarget.Detection:
                var named = detections.FirstOrDefault(d => link.Name is null || string.Equals(d.RuleId, link.Name, StringComparison.Ordinal));
                if (named is not null && (link.Events.Count == 0 || link.Events.All(named.Events.Contains)))
                    SetMatch([.. named.Events, named.Sequence], MatchLabel(named.Events.Length + 1, named.RuleId));
                else
                    SetMatch(link.Events.Count > 0 ? link.Events : null, MatchLabel(link.Events.Count, link.Name));
                Select(Rows.FirstOrDefault());
                break;
            case AreaLinkTarget.Egress:
                SetMatch(null, "");
                if (egress.FirstOrDefault() is { } latest)
                    SelectSequence(latest.Sequence);
                break;
            default:
                SetMatch(null, "");
                break;
        }

        // Select raised these only if the selection changed; a fresh log always redraws the column.
        OnPropertyChanged(nameof(SelectedRow));
        OnPropertyChanged(nameof(HasEgress));
        OnPropertyChanged(nameof(HasDetail));
        OnPropertyChanged(nameof(HasNoSelection));
        ClearSelectionCommand.NotifyCanExecuteChanged();
    }

    private void SetScan(ScanResult? scan)
    {
        LastScan = scan is null ? "" : AuditText.Scan(scan, _time.LocalTimeZone);
        OnPropertyChanged(nameof(HasLastScan));
    }

    private async Task ScanAsync(CancellationToken cancellationToken)
    {
        try
        {
            SetScan(await _service.ScanAsync(SessionId, FailOn, cancellationToken));
            Error = "";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = $"The scan failed: {exception.Message}";
        }
    }

    private async Task ExportAsync(CancellationToken cancellationToken)
    {
        try
        {
            var options = new TraceExportOptions(MetadataOnly ? TraceRetention.MetadataOnly : TraceRetention.Full, IncludeMemory);
            var result = await _service.ExportAsync(SessionId, options, cancellationToken);
            var retention = result.Retention == TraceRetention.MetadataOnly ? "metadata only" : "full content, secrets redacted";
            ExportResult = result.Written
                ? string.Create(CultureInfo.InvariantCulture,
                    $"Wrote {result.Events} events ({AuditText.Size(result.Bytes)}, {retention}) to {result.Path}, readable by you only.")
                : string.Create(CultureInfo.InvariantCulture,
                    $"Demo: no file was written. The engine would write {result.Events} events ({AuditText.Size(result.Bytes)}, {retention}) to {result.Path}, readable by you only.");
            Error = "";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Error = $"The trace was not written: {exception.Message}";
        }
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Link) && _shell?.Link is { Area: Area.Audit } link && SessionOptions.Count > 0)
            _ = FollowAsync(link);
        else if (e.PropertyName == nameof(ShellViewModel.CurrentArea))
            ApplyStatus();
    }

    /// <summary>
    /// Audit's status bar (Audit.dc.html): the open session's generator, decider, mode and tags, then the log's detections
    /// ("1 high this session") and, at the right, the log's size. Written only while Audit shows; leaving takes back the
    /// detections field so the next area does not inherit it.
    /// </summary>
    private void ApplyStatus()
    {
        if (_shell is null)
            return;
        var bar = _shell.StatusBar;
        if (_shell.CurrentArea != Area.Audit)
        {
            if (!_showingStatus)
                return;
            _showingStatus = false;
            if (_myFields.Count > 0 && ReferenceEquals(bar.AreaFields, _myFields))
                bar.AreaFields = [];
            _myFields = [];
            // The picker may have shown the whole log (no session id); the session the window shows comes back.
            if (bar.SessionId.Length == 0)
                bar.SessionId = _shell.CurrentSession?.Id ?? "";
            return;
        }

        _showingStatus = true;

        if (_sessionStatus is { } session)
        {
            bar.Generator = session.Generator;
            bar.Decider = session.Decider;
            bar.Mode = session.Mode;
            bar.Tags = session.Tags;
        }

        bar.Engine = "";
        bar.Gateway = "";
        bar.Context = "";
        bar.Scheduler = "";
        bar.Branch = "";
        _myFields = _statusDetections.Length > 0 ? [new StatusField("detections", _statusDetections)] : [];
        bar.AreaFields = _myFields;
        bar.SessionId = SessionId ?? "";
        bar.Trailing = _statusLog;
    }
}
