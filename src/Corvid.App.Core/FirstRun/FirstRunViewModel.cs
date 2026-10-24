using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.FirstRun;

/// <summary>One of the setup's six steps in the left column: its number, its name, and whether it is the current one.</summary>
public sealed class FirstRunStepViewModel : ObservableObject
{
    private bool _isCurrent;

    internal FirstRunStepViewModel(int index, string name, string anchor, Action<int> open)
    {
        Index = index;
        Name = name;
        Anchor = anchor;
        Open = new RelayCommand(() => open(index));
    }

    public int Index { get; }

    public string Number => (Index + 1).ToString(CultureInfo.InvariantCulture);

    public string Name { get; }

    /// <summary>The panel the step scrolls to.</summary>
    public string Anchor { get; }

    public bool IsCurrent { get => _isCurrent; internal set { if (SetProperty(ref _isCurrent, value)) OnPropertyChanged(nameof(AutomationName)); } }

    public RelayCommand Open { get; }

    public string AutomationName => IsCurrent ? $"Step {Number}, {Name}, current" : $"Step {Number}, {Name}";
}

/// <summary>One doctor check in its section's table: the status tag, the name, what was found, and the fix.</summary>
public sealed class CheckRowViewModel
{
    internal CheckRowViewModel(DoctorCheck check, Action<string> copy)
    {
        Status = check.Status;
        StatusText = check.Status switch
        {
            CheckStatus.Ok => "ok",
            CheckStatus.Info => "info",
            CheckStatus.Warning => "warning",
            _ => "failed",
        };
        StatusTone = check.Status switch
        {
            CheckStatus.Ok => Tone.Green,
            CheckStatus.Info => Tone.Grey,
            CheckStatus.Warning => Tone.Amber,
            _ => Tone.Red,
        };
        Name = SafeText.Clean(check.Name);
        Detail = Spans.Parse(check.Detail);
        // The fix names the command Copy puts on the clipboard: revealed, so what is shown is what will be pasted and run.
        // The clipboard gets the command as the check gave it.
        Fix = Spans.Parse(Reveal(check.Fix));
        FixCommand = check.FixCommand ?? "";
        CopyCommand = new RelayCommand(() => copy(FixCommand), () => FixCommand.Length > 0);
        AutomationName = $"{Name}: {StatusText}. {Spans.Plain(check.Detail)}{(check.Fix is { } fix ? $" {Spans.Plain(Reveal(fix))}" : "")}";
    }

    public string Name { get; }

    public CheckStatus Status { get; }

    public string StatusText { get; }

    public Tone StatusTone { get; }

    public IReadOnlyList<TextRun> Detail { get; }

    /// <summary>
    /// A check that is not ok and has a fix draws its detail muted with the fix below in the body colour; an ok check
    /// draws its detail muted on its own.
    /// </summary>
    public IReadOnlyList<TextRun> Fix { get; }

    public bool HasFix => Fix.Count > 0;

    public string FixCommand { get; }

    public bool HasFixCommand => FixCommand.Length > 0;

    public RelayCommand CopyCommand { get; }

    public string AutomationName { get; }

    /// <summary>The section's last row, which has no line below it (the panel's border is there).</summary>
    public bool IsLast { get; internal set; }

    /// <summary>A line of markup whose text a person acts on: every hidden character shown, the marks kept.</summary>
    private static string? Reveal(string? markup) => markup is null ? null : SafeText.Reveal(markup, keepJoiners: true);
}

/// <summary>A command for the clipboard; the view says whether the platform took it.</summary>
public sealed class CopyRequestEventArgs(string text) : EventArgs
{
    public string Text { get; } = text;

    /// <summary>Set by the view when the clipboard took the text.</summary>
    public bool Copied { get; set; }
}

/// <summary>A section of the doctor's checks (one panel): its title, its summary ("2 ok", "1 warning") and its rows.</summary>
public sealed class CheckSectionViewModel
{
    public CheckSectionViewModel(string title, string summary, IReadOnlyList<CheckRowViewModel> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        Title = title;
        Summary = summary;
        Rows = rows;
        if (rows.Count > 0)
            rows[^1].IsLast = true;
    }

    public string Title { get; }

    public string Summary { get; }

    public IReadOnlyList<CheckRowViewModel> Rows { get; }

    /// <summary>
    /// "2 ok"; "1 warning" when anything needs attention; "1 info" when something is only informational; prefixed with
    /// the build for the GPU section ("CUDA build · 1 info").
    /// </summary>
    public static string Summarize(IReadOnlyCollection<DoctorCheck> checks, string? prefix = null)
    {
        ArgumentNullException.ThrowIfNull(checks);
        static string Count(int n, string word) => n == 1 ? $"1 {word}" : $"{n} {word}s";
        var failed = checks.Count(check => check.Status == CheckStatus.Failed);
        var warnings = checks.Count(check => check.Status == CheckStatus.Warning);
        var infos = checks.Count(check => check.Status == CheckStatus.Info);
        var parts = new List<string>();
        if (failed > 0)
            parts.Add(Count(failed, "failure").Replace("failures", "failed", StringComparison.Ordinal).Replace("failure", "failed", StringComparison.Ordinal));
        if (warnings > 0)
            parts.Add(Count(warnings, "warning"));
        if (parts.Count == 0 && infos > 0)
            parts.Add($"{infos} info");
        if (parts.Count == 0)
            parts.Add($"{checks.Count} ok");
        if (prefix is { Length: > 0 })
            parts.Insert(0, prefix);
        return string.Join(" · ", parts);
    }
}

/// <summary>An approval mode as the first run offers it: the name in bold and one sentence (docs/plan/ui.md §4.4).</summary>
public sealed class ModeOptionViewModel : ObservableObject
{
    private bool _isSelected;

    internal ModeOptionViewModel(ApprovalMode mode, string name, string sentence, Action<ApprovalMode> choose)
    {
        Mode = mode;
        Name = name;
        Sentence = sentence;
        Choose = new RelayCommand(() => choose(mode));
    }

    public ApprovalMode Mode { get; }

    public string Name { get; }

    /// <summary>" · every side effect asks".</summary>
    public string Sentence { get; }

    public IReadOnlyList<TextRun> Text => [new TextRun(Name, Bold: true), new TextRun(Sentence, Muted: true)];

    public bool IsSelected { get => _isSelected; internal set => SetProperty(ref _isSelected, value); }

    public RelayCommand Choose { get; }

    public string AutomationName => $"{Name}{Sentence}";
}

/// <summary>
/// The first run (docs/plan/ui.md F1, FirstRun.dc.html): the doctor's checks before any model loads, each with its fix;
/// the models to download with their sizes totalled (nothing downloads until the person says so); the workspace; the
/// approval mode; the cloud privacy posture; and the Hermes import. Every step can be skipped; "Skip setup" and the last
/// step end on Sessions, and the app then runs with what it has, its Models badge saying how many models are missing.
/// Opened later from the rail's Doctor, the same page shows the checks without the setup's steps.
/// </summary>
public sealed class FirstRunViewModel : AreaViewModel
{
    private static readonly (string Name, string Anchor, string Title, string Subtitle, string Next)[] StepTable =
    [
        ("Check this machine", "check", "Check this machine", "the same checks as `corvid doctor`, each with its fix", "Continue to models"),
        ("Models to download", "models", "Models to download", "nothing downloads until you say so", "Continue to the workspace"),
        ("Workspace", "workspace", "Choose a workspace", "where tools may read and write", "Continue to the approval mode"),
        ("Approval mode", "approvals", "Pick an approval mode", "the hardline list applies in every mode", "Continue to cloud privacy"),
        ("Cloud privacy", "privacy", "Cloud privacy", "what may leave the machine, and how", "Continue to the import"),
        ("Import from Hermes", "import", "Import from Hermes", "every entry is previewed before anything is staged", "Finish setup"),
    ];

    private readonly IFirstRunService _service;
    private readonly IModelService _models;
    private readonly ShellViewModel _shell;
    private readonly StatusBarViewModel _bar;
    private readonly StatusBarLease _lease;
    private bool _isSetup;
    private bool _stepsShown = true;
    private bool _active;
    private bool _finished;
    private int _step;
    // The service's first suggestion (the configured workspace) once the page loads, unless the person typed one first.
    private string _workspace = "";
    private bool _workspaceTyped;
    private IReadOnlyList<TextRun> _workspaceNote = [];
    private bool _choosingWorkspace;
    private ApprovalMode _mode = ApprovalMode.Smart;
    private string _posture = "redact";
    private IReadOnlyList<TextRun> _hermes = [];
    private bool _hasHermes;
    private IReadOnlyList<TextRun> _notice = [];
    private Tone _noticeTone = Tone.Green;
    private IReadOnlyList<TextRun> _deciderWarning = [];
    private bool _vaultPresent = true;

    public FirstRunViewModel(IFirstRunService service, IModelService models, ShellViewModel shell, StatusBarViewModel statusBar, bool isSetup)
        : base(Area.FirstRun, "The doctor's checks with their fixes, then the models to download, the workspace, the approval mode and cloud privacy.")
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(statusBar);
        _service = service;
        _models = models;
        _shell = shell;
        _bar = statusBar;
        _lease = new StatusBarLease(statusBar);
        _isSetup = isSetup;
        Steps = [.. StepTable.Select((step, index) => new FirstRunStepViewModel(index, step.Name, step.Anchor, GoTo))];
        Steps[0].IsCurrent = true;
        Downloads = new DownloadSelectionViewModel(models);
        Downloads.Started += (_, _) => Say("Downloading. The rest of the setup does not wait for it.");
        Modes =
        [
            new(ApprovalMode.Manual, "Manual", " · every side effect asks", SetMode),
            new(ApprovalMode.Smart, "Smart", " · runs unasked only when Kev rates it low risk and it stays in the workspace, with no network, elevation or destructive tool", SetMode),
            new(ApprovalMode.Permissive, "Permissive", " · follows the fabric: calibrated levels 0 to 1 run, 2 to 4 ask", SetMode),
        ];
        Postures =
        [
            new("redact", "Redact: PII and credential values are replaced before a request leaves", new RelayCommand(() => Posture = "redact")),
            new("local only", "Local only: cloud models are never asked", new RelayCommand(() => Posture = "local only")),
        ];

        RunAgainCommand = new AsyncRelayCommand(RunChecksAsync);
        NextCommand = new AsyncRelayCommand(NextAsync);
        SkipSetupCommand = new AsyncRelayCommand(FinishAsync);
        SkipModelsCommand = new RelayCommand(() => GoTo(2));
        ChooseWorkspaceCommand = new RelayCommand(() => IsChoosingWorkspace = !IsChoosingWorkspace);
        ClosePickerCommand = new RelayCommand(() => IsChoosingWorkspace = false);
        OpenImportCommand = new RelayCommand(() => _shell.Navigate(Area.Memory));
        DismissNoticeCommand = new RelayCommand(() => Say(""));
        foreach (var command in new[] { RunAgainCommand, NextCommand, SkipSetupCommand, Downloads.DownloadCommand })
            ReportFailures(command);

        _models.Changed += (_, _) => RefreshModels();
        _service.EngineStatusChanged += (_, _) =>
        {
            if (_active)
                ApplyStatus();
            else
                ApplyRunningStatus();
        };
        _shell.PropertyChanged += OnShellChanged;
        _bar.ModeRequested += OnModeRequested;
        SetMode(_mode);
        Posture = _posture;
        RefreshModels();
        _ = LoadAsync();
        UpdateActive();
    }

    /// <summary>Raised to bring a step's panel into view (its anchor: check, models, workspace, approvals, privacy, import).</summary>
    public event EventHandler<string>? ScrollRequested;

    /// <summary>Raised with a command for the clipboard ("Copy the command"); the view copies it and says whether it could.</summary>
    public event EventHandler<CopyRequestEventArgs>? CopyRequested;

    /// <summary>The window opened on a fresh machine: the setup's steps show in the left column, and the status bar is the setup's.</summary>
    public bool IsSetup
    {
        get => _isSetup;
        private set
        {
            if (!SetProperty(ref _isSetup, value))
                return;
            OnPropertyChanged(nameof(StatusTrailing));
            OnPropertyChanged(nameof(ShowHeadSkip));
            UpdateRail();
        }
    }

    /// <summary>
    /// Whether the page shows the steps column (the setup at 860 px and wider); the page reports it. Without the column,
    /// the page draws the shell's rail toggle again and "Skip setup" moves into the head.
    /// </summary>
    public bool IsStepsColumnShown
    {
        get => _stepsShown;
        set
        {
            if (!SetProperty(ref _stepsShown, value))
                return;
            OnPropertyChanged(nameof(ShowHeadSkip));
            UpdateRail();
        }
    }

    /// <summary>"Skip setup" in the head: during the setup, while the steps column (where it usually is) is hidden.</summary>
    public bool ShowHeadSkip => _isSetup && !_stepsShown;

    public IReadOnlyList<FirstRunStepViewModel> Steps { get; }

    public int CurrentStep => _step;

    /// <summary>The page's title: the current step's ("Check this machine").</summary>
    public string Heading => _isSetup ? StepTable[_step].Title : StepTable[0].Title;

    public IReadOnlyList<TextRun> Subtitle => Spans.Parse(_isSetup ? StepTable[_step].Subtitle : StepTable[0].Subtitle);

    /// <summary>The primary button: on to the next step ("Continue to models"), or "Finish setup" on the last.</summary>
    public string NextLabel => _isSetup ? StepTable[_step].Next : "Continue to models";

    /// <summary>"step 1 of 6" at the status bar's right.</summary>
    public string StatusTrailing => _isSetup ? $"step {_step + 1} of {Steps.Count}" : "";

    public ObservableCollection<CheckSectionViewModel> Sections { get; } = [];

    /// <summary>The Models panel: the catalogue's local models, the default set preselected, and the total.</summary>
    public DownloadSelectionViewModel Downloads { get; }

    /// <summary>The amber warning while the decider's model is not on disk.</summary>
    public IReadOnlyList<TextRun> DeciderWarning { get => _deciderWarning; private set { if (!_deciderWarning.SequenceEqual(value) && SetProperty(ref _deciderWarning, value)) OnPropertyChanged(nameof(HasDeciderWarning)); } }

    public bool HasDeciderWarning => _deciderWarning.Count > 0;

    public string Workspace
    {
        get => _workspace;
        set
        {
            _workspaceTyped = true;
            if (SetProperty(ref _workspace, value ?? ""))
                _ = DescribeWorkspaceAsync();
        }
    }

    public IReadOnlyList<TextRun> WorkspaceNote { get => _workspaceNote; private set => SetProperty(ref _workspaceNote, value); }

    /// <summary>Choose…'s list of folders is open.</summary>
    public bool IsChoosingWorkspace { get => _choosingWorkspace; set => SetProperty(ref _choosingWorkspace, value); }

    public ObservableCollection<Choice> WorkspaceChoices { get; } = [];

    public IReadOnlyList<ModeOptionViewModel> Modes { get; }

    public ApprovalMode Mode => _mode;

    /// <summary>The cloud privacy posture: "redact" (the default) or "local only".</summary>
    public string Posture
    {
        get => _posture;
        set
        {
            SetProperty(ref _posture, value ?? "redact");
            foreach (var posture in Postures)
                posture.IsSelected = posture.Label == _posture;
        }
    }

    public IReadOnlyList<Choice> Postures { get; }

    public IReadOnlyList<TextRun> PrivacyText { get; } = Spans.Parse(
        "Requests to cloud models are redacted (PII and credential values) and every one is viewable afterwards in Audit. Cloud keys live in Vault, never in the app.");

    public IReadOnlyList<TextRun> HermesText { get => _hermes; private set => SetProperty(ref _hermes, value); }

    public bool HasHermes { get => _hasHermes; private set => SetProperty(ref _hasHermes, value); }

    public IReadOnlyList<TextRun> Notice { get => _notice; private set { if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); } }

    public bool HasNotice => _notice.Count > 0;

    /// <summary>Green for what was done, red for what failed.</summary>
    public Tone NoticeTone { get => _noticeTone; private set => SetProperty(ref _noticeTone, value); }

    public bool VaultPresent { get => _vaultPresent; private set => SetProperty(ref _vaultPresent, value); }

    public AsyncRelayCommand RunAgainCommand { get; }

    /// <summary>The primary button: the next step, or on the last, finish.</summary>
    public AsyncRelayCommand NextCommand { get; }

    /// <summary>"Skip setup": save what was chosen so far and go to Sessions.</summary>
    public AsyncRelayCommand SkipSetupCommand { get; }

    /// <summary>The Models panel's "Skip for now".</summary>
    public RelayCommand SkipModelsCommand { get; }

    public RelayCommand ChooseWorkspaceCommand { get; }

    public RelayCommand ClosePickerCommand { get; }

    /// <summary>The Hermes import's preview, which lives in Memory (F11).</summary>
    public RelayCommand OpenImportCommand { get; }

    public RelayCommand DismissNoticeCommand { get; }

    /// <summary>Makes a step current and brings its panel into view.</summary>
    public void GoTo(int step)
    {
        if (step < 0 || step >= Steps.Count)
            return;
        if (_isSetup && _step != step)
        {
            _step = step;
            foreach (var item in Steps)
                item.IsCurrent = item.Index == step;
            OnPropertyChanged(nameof(CurrentStep));
            OnPropertyChanged(nameof(Heading));
            OnPropertyChanged(nameof(Subtitle));
            OnPropertyChanged(nameof(NextLabel));
            OnPropertyChanged(nameof(StatusTrailing));
            if (_active)
                ApplyStatus();
        }

        ScrollRequested?.Invoke(this, Steps[step].Anchor);
    }

    private async Task NextAsync(CancellationToken cancellationToken)
    {
        if (!_isSetup)
        {
            _shell.Navigate(Area.Models);
            return;
        }

        if (_step < Steps.Count - 1)
            GoTo(_step + 1);
        else
            await FinishAsync(cancellationToken).ConfigureAwait(true);
    }

    private async Task FinishAsync(CancellationToken cancellationToken)
    {
        await _service.SaveAsync(new SetupChoices(_workspace, _mode, _posture), cancellationToken).ConfigureAwait(true);
        // The app now runs with what it has; the Models badge says how many default models are still missing (F23).
        _shell.SetBadge(Area.Models, _models.Snapshot.Catalogue.Count(model => model.InDefaultSet && model.IsMissing));
        // F1 ends on an empty session, not on whichever session the rail lists first, and the status bar keeps what the
        // setup found: the degraded decider, what is still missing, Vault's absence and the downloads it started.
        _finished = true;
        _shell.SelectSession(null);
        IsSetup = false;
        _shell.Navigate(Area.Sessions);
        ApplyRunningStatus();
    }

    /// <summary>
    /// The model store reads the configuration and the cache on its first load (the demo's has its snapshot already); the
    /// first run can be the first page to show it, so it asks too. A failure is said here, beside the checks.
    /// </summary>
    private async Task LoadModelsAsync()
    {
        try
        {
            await _models.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Fail($"The models could not be read: {Spans.Escape(exception.Message)}");
        }
    }

    private async Task LoadAsync()
    {
        var models = LoadModelsAsync();
        await LoadChecksAsync().ConfigureAwait(true);
        await models.ConfigureAwait(true);
    }

    private async Task LoadChecksAsync()
    {
        try
        {
            // The suggestions first: their first is the workspace the configuration names, which the page starts on.
            var suggestions = await _service.SuggestWorkspacesAsync().ConfigureAwait(true);
            if (!_workspaceTyped && suggestions.Count > 0)
            {
                _workspace = suggestions[0];
                OnPropertyChanged(nameof(Workspace));
            }

            await RunChecksAsync(CancellationToken.None).ConfigureAwait(true);
            await DescribeWorkspaceAsync().ConfigureAwait(true);
            foreach (var path in suggestions)
            {
                var folder = path;
                WorkspaceChoices.Add(new Choice(SafeText.Reveal(folder), SafeText.Reveal(folder), new RelayCommand(() =>
                {
                    Workspace = folder;
                    IsChoosingWorkspace = false;
                })) { IsSelected = folder == _workspace });
            }

            var hermes = await _service.FindHermesAsync().ConfigureAwait(true);
            HasHermes = hermes is not null;
            HermesText = Spans.Parse(hermes is null
                ? "No Hermes installation was found. Nothing to import."
                : $"Found `{Spans.Escape(hermes.Path)}`: {hermes.Memories} memories and {hermes.Skills} skills. Memory previews every entry; nothing is staged until you approve it.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Fail($"The checks could not run: {Spans.Escape(exception.Message)}");
        }
    }

    private async Task RunChecksAsync(CancellationToken cancellationToken)
    {
        var report = await _service.RunChecksAsync(cancellationToken).ConfigureAwait(true);
        Sections.Clear();
        foreach (var title in CheckSections.All)
        {
            var checks = report.Checks.Where(check => check.Section == title).ToList();
            if (checks.Count == 0)
                continue;
            Sections.Add(new CheckSectionViewModel(title, CheckSectionViewModel.Summarize(checks, title == CheckSections.Gpu ? report.Build : null),
                [.. checks.Select(check => new CheckRowViewModel(check, Copy))]));
        }

        if (RunAgainCommand.IsRunning)
            Say("Checked again.");
    }

    private async Task DescribeWorkspaceAsync()
    {
        var info = await _service.DescribeWorkspaceAsync(_workspace).ConfigureAwait(true);
        if (info.Path == _workspace)
            WorkspaceNote = Spans.Parse(info.Description);
        foreach (var choice in WorkspaceChoices)
            choice.IsSelected = choice.Label == SafeText.Reveal(_workspace);
    }

    private void Copy(string command)
    {
        var request = new CopyRequestEventArgs(command);
        CopyRequested?.Invoke(this, request);
        // What is said shows every hidden character of what was copied.
        var shown = Spans.Escape(SafeText.Reveal(command));
        if (request.Copied)
            Say($"Copied `{shown}`. Run it in a terminal, then Run again.");
        else
            Fail($"The clipboard did not take `{shown}`: select it in the fix and copy it by hand.");
    }

    private void SetMode(ApprovalMode mode)
    {
        _mode = mode;
        foreach (var option in Modes)
            option.IsSelected = option.Mode == mode;
        OnPropertyChanged(nameof(Mode));
    }

    private void Say(string message)
    {
        NoticeTone = Tone.Green;
        Notice = Spans.Parse(message);
    }

    private void Fail(string message)
    {
        NoticeTone = Tone.Red;
        Notice = Spans.Parse(message);
    }

    /// <summary>A button's run that failed shows as a red notice: the command keeps the error, and nothing else would show it.</summary>
    private void ReportFailures(AsyncRelayCommand command) => command.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(AsyncRelayCommand.Error) && command.Error is { } error)
            Fail($"That did not work: {Spans.Escape(error.Message)}");
    };

    /// <summary>After the setup, with no session open, a mode picked in the status bar is the mode from then on.</summary>
    private void OnModeRequested(object? sender, ApprovalMode mode)
    {
        if (_finished && _shell.CurrentSession is null)
            SetMode(mode);
    }

    private void RefreshModels()
    {
        var snapshot = _models.Snapshot;
        Downloads.Refresh();
        // A snapshot not loaded yet says nothing about Vault or the decider.
        VaultPresent = !snapshot.IsLoaded || snapshot.Privacy.VaultPresent;
        var decider = snapshot.IsLoaded ? snapshot.Find(snapshot.Decider.Model) : null;
        DeciderWarning = decider is { State: not ModelState.Cached }
            ? Spans.Parse($"Without `{Spans.Escape(decider.Name)}`, decisions run on the degraded tier and every chat says so. Memory writes then wait for your review.")
            : [];
        if (_active)
            ApplyStatus();
        else
            ApplyRunningStatus();
    }

    // The shell around the setup: its own left column of steps, and the setup's status bar (FirstRun.dc.html).

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentArea))
        {
            UpdateActive();
            ApplyRunningStatus();
        }
    }

    private void UpdateActive()
    {
        var active = _shell.CurrentArea == Area.FirstRun && _isSetup;
        UpdateRail();
        if (active == _active)
            return;
        _active = active;
        if (active)
        {
            _lease.Take();
            ApplyStatus();
        }
        else
        {
            IsChoosingWorkspace = false;
            _lease.Release();
        }
    }

    /// <summary>The page draws its own left column (the steps) in place of the rail, while that column shows.</summary>
    private void UpdateRail()
    {
        _shell.PageDrawsRail = _isSetup && _stepsShown && _shell.CurrentArea == Area.FirstRun;
        _shell.SetupShowing = _isSetup && _shell.CurrentArea == Area.FirstRun;
    }

    private void ApplyStatus()
    {
        var snapshot = _models.Snapshot;
        var decider = !snapshot.IsLoaded ? ""
            : snapshot.Decider.Tier == DeciderTier.Calibrated
            ? $"{SafeText.Clean(snapshot.Decider.DisplayName)}, calibrated"
            : $"degraded until {SafeText.Clean(snapshot.Decider.Model)} is downloaded";
        var downloads = ModelsViewModel.StatusDownloads(snapshot);
        _lease.Engine = _service.EngineStatus ?? "not started · models load after this page";
        _lease.Generator = "";
        _lease.Decider = decider;
        _lease.Context = "";
        _lease.AreaFields = downloads;
        _lease.Mode = null;
        _lease.Tags = !snapshot.IsLoaded || snapshot.Privacy.VaultPresent
            ? []
            : [new StatusTag("Vault absent", Tone.Amber, "No answer at $XDG_RUNTIME_DIR/corvid-vault.sock: cloud models and gateway bots are unavailable.")];
        _lease.Scheduler = "";
        _lease.Gateway = "";
        _lease.Branch = "";
        _lease.SessionId = "";
        _lease.Trailing = StatusTrailing;
    }

    /// <summary>
    /// After the setup, while a session area shows no session yet (F1's empty session), the status bar is the app running
    /// degraded: the decider's tier, an amber tag naming the default models still missing, Vault's absence, and the
    /// downloads in flight, which keep moving as they progress. Once a session is open its own status takes over.
    /// </summary>
    private void ApplyRunningStatus()
    {
        if (!_finished || _isSetup || _shell.CurrentSession is not null || !SessionHub.ShowsSessionStatus(_shell.CurrentArea))
            return;
        var snapshot = _models.Snapshot;
        var missing = snapshot.Catalogue
            .Where(model => snapshot.IsLoaded && model.InDefaultSet && model.Kind == ModelKind.Local && model.State != ModelState.Cached)
            .Select(model => model.Name)
            .ToList();
        var tags = new List<StatusTag>();
        if (missing.Count > 0)
        {
            tags.Add(new StatusTag($"degraded · {string.Join(", ", missing.Select(name => SafeText.Clean(name)))} missing", Tone.Amber,
                "Corvid runs with what it has: decisions use the degraded tier and memory writes wait for review until these models are downloaded."));
        }

        if (snapshot.IsLoaded && !snapshot.Privacy.VaultPresent)
            tags.Add(new StatusTag("Vault absent", Tone.Amber, "No answer at $XDG_RUNTIME_DIR/corvid-vault.sock: cloud models and gateway bots are unavailable."));
        _bar.Engine = _service.EngineStatus ?? "in-process";
        _bar.Generator = "";
        _bar.Decider = ModelsViewModel.StatusDecider(snapshot);
        _bar.Context = "";
        _bar.AreaFields = ModelsViewModel.StatusDownloads(snapshot);
        _bar.Mode = _mode;
        _bar.Tags = tags;
        _bar.Scheduler = "";
        _bar.Gateway = "";
        _bar.Branch = "";
        _bar.SessionId = "";
        _bar.Trailing = "idle";
    }
}
