using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
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
        Name = check.Name;
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
        Detail = Spans.Parse(check.Detail);
        Fix = Spans.Parse(check.Fix);
        FixCommand = check.FixCommand ?? "";
        CopyCommand = new RelayCommand(() => copy(FixCommand), () => FixCommand.Length > 0);
        AutomationName = $"{Name}: {StatusText}. {Spans.Plain(check.Detail)}{(check.Fix is { } fix ? $" {Spans.Plain(fix)}" : "")}";
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
    private readonly Dictionary<string, object?> _savedStatus = [];
    private readonly Dictionary<string, object?> _myStatus = [];
    private bool _isSetup;
    private bool _active;
    private int _step;
    private string _workspace = DemoFirstRunService.Workspace;
    private IReadOnlyList<TextRun> _workspaceNote = [];
    private bool _choosingWorkspace;
    private ApprovalMode _mode = ApprovalMode.Smart;
    private string _posture = "redact";
    private IReadOnlyList<TextRun> _hermes = [];
    private bool _hasHermes;
    private IReadOnlyList<TextRun> _notice = [];
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

        _models.Changed += (_, _) => RefreshModels();
        _shell.PropertyChanged += OnShellChanged;
        SetMode(_mode);
        Posture = _posture;
        RefreshModels();
        _ = LoadAsync();
        UpdateActive();
    }

    /// <summary>Raised to bring a step's panel into view (its anchor: check, models, workspace, approvals, privacy, import).</summary>
    public event EventHandler<string>? ScrollRequested;

    /// <summary>Raised with a command for the clipboard ("Copy the command"); the view copies it.</summary>
    public event EventHandler<string>? CopyRequested;

    /// <summary>The window opened on a fresh machine: the setup's steps show in the left column, and the status bar is the setup's.</summary>
    public bool IsSetup
    {
        get => _isSetup;
        private set
        {
            if (!SetProperty(ref _isSetup, value))
                return;
            OnPropertyChanged(nameof(StatusTrailing));
            UpdateRail();
        }
    }

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
    public IReadOnlyList<TextRun> DeciderWarning { get => _deciderWarning; private set { if (SetProperty(ref _deciderWarning, value)) OnPropertyChanged(nameof(HasDeciderWarning)); } }

    public bool HasDeciderWarning => _deciderWarning.Count > 0;

    public string Workspace
    {
        get => _workspace;
        set
        {
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
        IsSetup = false;
        _shell.Navigate(Area.Sessions);
    }

    private async Task LoadAsync()
    {
        try
        {
            await RunChecksAsync(CancellationToken.None).ConfigureAwait(true);
            await DescribeWorkspaceAsync().ConfigureAwait(true);
            foreach (var path in await _service.SuggestWorkspacesAsync().ConfigureAwait(true))
            {
                var folder = path;
                WorkspaceChoices.Add(new Choice(folder, folder, new RelayCommand(() =>
                {
                    Workspace = folder;
                    IsChoosingWorkspace = false;
                })) { IsSelected = folder == _workspace });
            }

            var hermes = await _service.FindHermesAsync().ConfigureAwait(true);
            HasHermes = hermes is not null;
            HermesText = Spans.Parse(hermes is null
                ? "No Hermes installation was found. Nothing to import."
                : $"Found `{hermes.Path}`: {hermes.Memories} memories and {hermes.Skills} skills. Memory previews every entry; nothing is staged until you approve it.");
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Say($"The checks could not run: {exception.Message}");
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
            choice.IsSelected = choice.Label == _workspace;
    }

    private void Copy(string command)
    {
        CopyRequested?.Invoke(this, command);
        Say($"Copied `{command}`. Run it in a terminal, then Run again.");
    }

    private void SetMode(ApprovalMode mode)
    {
        _mode = mode;
        foreach (var option in Modes)
            option.IsSelected = option.Mode == mode;
        OnPropertyChanged(nameof(Mode));
    }

    private void Say(string message) => Notice = Spans.Parse(message);

    private void RefreshModels()
    {
        var snapshot = _models.Snapshot;
        Downloads.Refresh();
        VaultPresent = snapshot.Privacy.VaultPresent;
        var decider = snapshot.Find(snapshot.Decider.Model);
        DeciderWarning = decider is { State: not ModelState.Cached }
            ? Spans.Parse($"Without `{decider.Name}`, decisions run on the degraded tier and every chat says so. Memory writes then wait for your review.")
            : [];
        if (_active)
            ApplyStatus();
    }

    // The shell around the setup: its own left column of steps, and the setup's status bar (FirstRun.dc.html).

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentArea))
            UpdateActive();
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
            _savedStatus.Clear();
            foreach (var (name, value) in ReadStatus())
                _savedStatus[name] = value;
            ApplyStatus();
        }
        else
        {
            IsChoosingWorkspace = false;
            RestoreStatus();
        }
    }

    private void UpdateRail() => _shell.PageDrawsRail = _isSetup && _shell.CurrentArea == Area.FirstRun;

    private void ApplyStatus()
    {
        var snapshot = _models.Snapshot;
        var decider = snapshot.Decider.Tier == DeciderTier.Calibrated
            ? $"{snapshot.Decider.DisplayName}, calibrated"
            : $"degraded until {snapshot.Decider.Model} is downloaded";
        var downloads = ModelsViewModel.StatusDownloads(snapshot);
        Write(nameof(StatusBarViewModel.Engine), "not started · models load after this page");
        Write(nameof(StatusBarViewModel.Generator), "");
        Write(nameof(StatusBarViewModel.Decider), decider);
        Write(nameof(StatusBarViewModel.Context), "");
        Write(nameof(StatusBarViewModel.AreaFields), downloads);
        Write(nameof(StatusBarViewModel.Mode), null);
        Write(nameof(StatusBarViewModel.Tags), snapshot.Privacy.VaultPresent
            ? (IReadOnlyList<StatusTag>)[]
            : [new StatusTag("Vault absent", Tone.Amber, "No answer at $XDG_RUNTIME_DIR/corvid-vault.sock: cloud models and gateway bots are unavailable.")]);
        Write(nameof(StatusBarViewModel.Scheduler), "");
        Write(nameof(StatusBarViewModel.Gateway), "");
        Write(nameof(StatusBarViewModel.Branch), "");
        Write(nameof(StatusBarViewModel.SessionId), "");
        Write(nameof(StatusBarViewModel.Trailing), StatusTrailing);
    }

    /// <summary>Puts back what the setup's status bar replaced, where nothing else has written since.</summary>
    private void RestoreStatus()
    {
        foreach (var (name, value) in ReadStatus())
        {
            if (_myStatus.TryGetValue(name, out var mine) && Equals(value, mine) && _savedStatus.TryGetValue(name, out var saved))
                Set(name, saved);
        }

        _myStatus.Clear();
    }

    private void Write(string name, object? value)
    {
        _myStatus[name] = value;
        Set(name, value);
    }

    private IEnumerable<(string Name, object? Value)> ReadStatus()
    {
        yield return (nameof(StatusBarViewModel.Engine), _bar.Engine);
        yield return (nameof(StatusBarViewModel.Generator), _bar.Generator);
        yield return (nameof(StatusBarViewModel.Decider), _bar.Decider);
        yield return (nameof(StatusBarViewModel.Context), _bar.Context);
        yield return (nameof(StatusBarViewModel.AreaFields), _bar.AreaFields);
        yield return (nameof(StatusBarViewModel.Mode), _bar.Mode);
        yield return (nameof(StatusBarViewModel.Tags), _bar.Tags);
        yield return (nameof(StatusBarViewModel.Scheduler), _bar.Scheduler);
        yield return (nameof(StatusBarViewModel.Gateway), _bar.Gateway);
        yield return (nameof(StatusBarViewModel.Branch), _bar.Branch);
        yield return (nameof(StatusBarViewModel.SessionId), _bar.SessionId);
        yield return (nameof(StatusBarViewModel.Trailing), _bar.Trailing);
    }

    private void Set(string name, object? value)
    {
        switch (name)
        {
            case nameof(StatusBarViewModel.Engine): _bar.Engine = (string)value!; break;
            case nameof(StatusBarViewModel.Generator): _bar.Generator = (string)value!; break;
            case nameof(StatusBarViewModel.Decider): _bar.Decider = (string)value!; break;
            case nameof(StatusBarViewModel.Context): _bar.Context = (string)value!; break;
            case nameof(StatusBarViewModel.AreaFields): _bar.AreaFields = (IReadOnlyList<StatusField>)value!; break;
            case nameof(StatusBarViewModel.Mode): _bar.Mode = (ApprovalMode?)value; break;
            case nameof(StatusBarViewModel.Tags): _bar.Tags = (IReadOnlyList<StatusTag>)value!; break;
            case nameof(StatusBarViewModel.Scheduler): _bar.Scheduler = (string)value!; break;
            case nameof(StatusBarViewModel.Gateway): _bar.Gateway = (string)value!; break;
            case nameof(StatusBarViewModel.Branch): _bar.Branch = (string)value!; break;
            case nameof(StatusBarViewModel.SessionId): _bar.SessionId = (string)value!; break;
            case nameof(StatusBarViewModel.Trailing): _bar.Trailing = (string)value!; break;
        }
    }
}
