using System.ComponentModel;
using System.Collections.ObjectModel;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Models;

/// <summary>The picker open over the Models page: none, a slot's Switch…, Download…, or Add a local server….</summary>
public enum ModelsPicker
{
    None,
    Switch,
    Download,
    AddServer,
}

/// <summary>A model a slot can switch to, in the Switch… picker.</summary>
public sealed class SwitchChoiceViewModel(string model, string detail, bool isCurrent, bool isAvailable, string reason, RelayCommand choose)
{
    public string Model { get; } = model;

    /// <summary>Where it runs and what it serves.</summary>
    public string Detail { get; } = detail;

    public bool IsCurrent { get; } = isCurrent;

    public bool IsAvailable { get; } = isAvailable;

    /// <summary>Why it cannot be chosen ("not downloaded", "set gemini.key in Vault"); empty when it can.</summary>
    public string Reason { get; } = reason;

    public bool HasReason => Reason.Length > 0;

    public RelayCommand Choose { get; } = choose;

    public string AutomationName => IsCurrent ? $"{Model}, current" : HasReason ? $"{Model}, {Reason}" : Model;
}

/// <summary>
/// A local model's placement in the Placement panel's editor (F14): cuda or cpu, and beside cuda the doctor's driver and
/// runtime checks for the GPU it would run on.
/// </summary>
public sealed class PlacementRowViewModel : ObservableObject
{
    private string _device;

    internal PlacementRowViewModel(ModelPlacement placement, GpuDevice? gpu)
    {
        Id = placement.Model;
        Model = SafeText.Clean(placement.Model);
        Original = placement.Device;
        _device = placement.Device;
        Fallback = SafeText.Clean(placement.NoticeOnFallback ? $"fallback {placement.Fallback}, with a notice" : $"fallback {placement.Fallback}");
        Checks = gpu is null
            ? []
            : [.. gpu.Checks.Select(check => new ModelFact(SafeText.Clean(check.Name), Spans.Parse($"{(check.Ok ? "**ok**" : "**failed**")} · {Spans.Escape(check.Detail)}")))];
        Devices = gpu is null ? ["cpu"] : ["cuda", "cpu"];
        Choices = [.. Devices.Select(device => new Choice(device, device == "cuda" ? SafeText.Clean($"{gpu!.Id}: {gpu.Summary}") : "the CPU",
            new RelayCommand(() => Device = device)) { IsSelected = device == _device })];
    }

    /// <summary>The model as the configuration names it, for the write.</summary>
    public string Id { get; }

    /// <summary>The model's name as drawn (cleaned).</summary>
    public string Model { get; }

    public string Original { get; }

    public IReadOnlyList<string> Devices { get; }

    public IReadOnlyList<Choice> Choices { get; }

    /// <summary>The device picked in the editor.</summary>
    public string Device
    {
        get => _device;
        set
        {
            if (!Devices.Contains(value) || !SetProperty(ref _device, value))
                return;
            foreach (var choice in Choices)
                choice.IsSelected = choice.Label == value;
            OnPropertyChanged(nameof(ShowChecks));
            OnPropertyChanged(nameof(IsChanged));
        }
    }

    public string Fallback { get; }

    /// <summary>The GPU's driver and runtime checks, shown beside the row while cuda is picked.</summary>
    public IReadOnlyList<ModelFact> Checks { get; }

    public bool ShowChecks => _device == "cuda" && Checks.Count > 0;

    public bool IsChanged => _device != Original;
}

/// <summary>
/// The Models area (docs/plan/ui.md §4.8, flows F14 and F23, Models.dc.html): the seven slots with the escalation ladder,
/// the catalogue and downloads, privacy and egress, the decider and placement. Every change goes through
/// <see cref="IModelService"/>, which validates it and writes <c>corvid.json</c>; the answer shows as a notice. While the
/// area shows, it puts the generator, the decider, the downloads and "idle" in the status bar, as the mockup draws it, and
/// gives the session id back when it goes.
/// </summary>
public sealed class ModelsViewModel : AreaViewModel
{
    public const string ConfigNote = "a slot change is written there and applies to new sessions";

    private readonly IModelService _service;
    private readonly ShellViewModel _shell;
    private readonly StatusBarViewModel _bar;
    private readonly StatusBarLease _lease;
    private bool _active;
    private IReadOnlyList<TextRun> _notice = [];
    private string? _problem;
    private Tone _noticeTone = Tone.Green;
    private ModelsPicker _picker;
    private string _switchTitle = "";
    private string _switchSlot = "";
    private bool _switchForSession = true;
    private SlotRowViewModel? _switchRow;
    private string _switchHint = "";
    private string _serverName = "";
    private string _serverUrl = "";
    private bool _editingPlacement;
    private IReadOnlyList<TextRun> _missing = [];
    private string _missingButton = "";
    private string _privacyTag = "";
    private IReadOnlyList<TextRun> _privacySummary = [];
    private IReadOnlyList<ModelFact> _privacyFacts = [];
    private string _deciderTag = "";
    private Tone _deciderTone = Tone.Green;
    private IReadOnlyList<ModelFact> _deciderFacts = [];
    private IReadOnlyList<ModelFact> _placementFacts = [];
    private IReadOnlyList<TextRun> _subtitle = [];

    public ModelsViewModel(IModelService service, ShellViewModel shell, StatusBarViewModel statusBar)
        : base(Area.Models, "The seven slots, the catalogue and downloads, placement on CPU or CUDA, and each cloud model's privacy.")
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(statusBar);
        _service = service;
        _shell = shell;
        _bar = statusBar;
        _lease = new StatusBarLease(statusBar);
        Downloads = new DownloadSelectionViewModel(service);
        Downloads.Started += (_, _) =>
        {
            ClosePicker();
            Say(true, "Downloading. Nothing else downloads unless you start it; a cancelled download leaves no partial file.");
        };

        DownloadPickerCommand = new RelayCommand(() => Open(ModelsPicker.Download));
        AddServerPickerCommand = new RelayCommand(() => Open(ModelsPicker.AddServer));
        AddServerCommand = new AsyncRelayCommand(AddServerAsync, () => _serverName.Trim().Length > 0 && _serverUrl.Trim().Length > 0);
        ClosePickerCommand = new RelayCommand(ClosePicker);
        DismissNoticeCommand = new RelayCommand(() => Say(true, ""));
        DownloadMissingCommand = new AsyncRelayCommand(DownloadMissingAsync, () => MissingModels().Count > 0);
        OpenEgressCommand = new RelayCommand(OpenEgress);
        EditPlacementCommand = new RelayCommand(() => IsEditingPlacement = true);
        SavePlacementCommand = new AsyncRelayCommand(SavePlacementAsync);
        CancelPlacementCommand = new RelayCommand(() => IsEditingPlacement = false);
        ForgetChoiceCommand = new RelayCommand(() =>
        {
            if (_switchRow is { } row)
                Run(() => ForgetChoiceAsync(row.Name));
        }, () => CanForgetChoice);
        SwitchGeneratorCommand = new RelayCommand(() =>
        {
            if (Slots.FirstOrDefault(slot => slot.Name == ModelSlotNames.Generator) is { CanSwitch: true } generator)
                OpenSwitch(generator);
        });
        foreach (var command in new[] { AddServerCommand, DownloadMissingCommand, SavePlacementCommand, Downloads.DownloadCommand })
            ReportFailures(command);

        _service.Changed += (_, _) => Refresh();
        _shell.PropertyChanged += OnShellChanged;
        Refresh();
        UpdateActive();
        // The store reads the configuration and the cache on its first load (the demo's has its snapshot already); one that
        // cannot (the engine's, before A3 setup) says why here rather than leaving an empty page unexplained.
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            await _service.LoadAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Say(false, $"The models could not be read: {Spans.Escape(exception.Message)}");
        }
    }

    public IReadOnlyList<TextRun> Subtitle { get => _subtitle; private set => SetRuns(ref _subtitle, value); }

    public ObservableCollection<SlotRowViewModel> Slots { get; } = [];

    public ObservableCollection<CatalogueRowViewModel> Catalogue { get; } = [];

    /// <summary>The Download… picker's list, and its total.</summary>
    public DownloadSelectionViewModel Downloads { get; }

    // The notice after a change: what was written, or why not.

    public IReadOnlyList<TextRun> Notice { get => _notice; private set { if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); } }

    public bool HasNotice => _notice.Count > 0;

    /// <summary>Green for written, red for refused.</summary>
    public Tone NoticeTone { get => _noticeTone; private set => SetProperty(ref _noticeTone, value); }

    // F23: the banner while default models are missing.

    public bool HasMissing => _missing.Count > 0;

    public IReadOnlyList<TextRun> MissingText { get => _missing; private set { if (SetRuns(ref _missing, value)) OnPropertyChanged(nameof(HasMissing)); } }

    /// <summary>"Download 3.3 GB".</summary>
    public string MissingButtonText { get => _missingButton; private set => SetProperty(ref _missingButton, value); }

    // The right column's panels.

    public string PrivacyTag { get => _privacyTag; private set => SetProperty(ref _privacyTag, value); }

    public IReadOnlyList<TextRun> PrivacySummary { get => _privacySummary; private set => SetRuns(ref _privacySummary, value); }

    public IReadOnlyList<ModelFact> PrivacyFacts { get => _privacyFacts; private set => SetFacts(ref _privacyFacts, value); }

    public string DeciderTag { get => _deciderTag; private set => SetProperty(ref _deciderTag, value); }

    public Tone DeciderTone { get => _deciderTone; private set => SetProperty(ref _deciderTone, value); }

    public IReadOnlyList<ModelFact> DeciderFacts { get => _deciderFacts; private set => SetFacts(ref _deciderFacts, value); }

    public IReadOnlyList<ModelFact> PlacementFacts { get => _placementFacts; private set => SetFacts(ref _placementFacts, value); }

    /// <summary>The Placement panel's editor (Edit placement…): one row per local model.</summary>
    public ObservableCollection<PlacementRowViewModel> PlacementRows { get; } = [];

    public bool IsEditingPlacement
    {
        get => _editingPlacement;
        set
        {
            if (value)
                FillPlacementRows();
            if (SetProperty(ref _editingPlacement, value))
                OnPropertyChanged(nameof(IsShowingPlacement));
        }
    }

    public bool IsShowingPlacement => !_editingPlacement;

    // The pickers.

    public ModelsPicker Picker
    {
        get => _picker;
        private set
        {
            if (!SetProperty(ref _picker, value))
                return;
            OnPropertyChanged(nameof(IsPickerOpen));
            OnPropertyChanged(nameof(IsSwitchOpen));
            OnPropertyChanged(nameof(IsDownloadOpen));
            OnPropertyChanged(nameof(IsAddServerOpen));
        }
    }

    public bool IsPickerOpen => _picker != ModelsPicker.None;

    public bool IsSwitchOpen => _picker == ModelsPicker.Switch;

    public bool IsDownloadOpen => _picker == ModelsPicker.Download;

    public bool IsAddServerOpen => _picker == ModelsPicker.AddServer;

    /// <summary>"Switch the generator".</summary>
    public string SwitchTitle { get => _switchTitle; private set => SetProperty(ref _switchTitle, value); }

    public ObservableCollection<SwitchChoiceViewModel> SwitchChoices { get; } = [];

    /// <summary>
    /// A5: the picker's two choices, "This session" and "Default for new sessions": separate, as in the CLI's
    /// <c>/model</c> and <c>/model --default</c>.
    /// </summary>
    public ObservableCollection<Choice> SwitchScopes { get; } = [];

    /// <summary>A5: true when a pick switches the open session only; false when it writes the default for new sessions.</summary>
    public bool SwitchForSession => _switchForSession;

    /// <summary>What a pick does in the chosen scope, under the picker's title.</summary>
    public string SwitchHint { get => _switchHint; private set => SetProperty(ref _switchHint, value); }

    /// <summary>A5: "Follow the default": forgets the open session's choice for the slot (shown while it has one).</summary>
    public RelayCommand ForgetChoiceCommand { get; }

    public bool CanForgetChoice => _switchForSession && _switchRow?.HasSessionChoice == true;

    public string ServerName { get => _serverName; set { if (SetProperty(ref _serverName, value ?? "")) AddServerCommand.NotifyCanExecuteChanged(); } }

    public string ServerUrl { get => _serverUrl; set { if (SetProperty(ref _serverUrl, value ?? "")) AddServerCommand.NotifyCanExecuteChanged(); } }

    public RelayCommand DownloadPickerCommand { get; }

    public RelayCommand AddServerPickerCommand { get; }

    public AsyncRelayCommand AddServerCommand { get; }

    /// <summary>Esc: closes the open picker, or the placement editor.</summary>
    public RelayCommand ClosePickerCommand { get; }

    public RelayCommand DismissNoticeCommand { get; }

    public AsyncRelayCommand DownloadMissingCommand { get; }

    /// <summary>"Open the egress ledger": Audit, filtered to what left the machine.</summary>
    public RelayCommand OpenEgressCommand { get; }

    public RelayCommand EditPlacementCommand { get; }

    public AsyncRelayCommand SavePlacementCommand { get; }

    public RelayCommand CancelPlacementCommand { get; }

    /// <summary>Alt+P (docs/plan/ui.md §6): the generator's Switch… picker.</summary>
    public RelayCommand SwitchGeneratorCommand { get; }

    internal void OpenSwitch(SlotRowViewModel slot, bool? forSession = null)
    {
        _switchRow = slot;
        _switchForSession = forSession ?? true;
        var snapshot = _service.Snapshot;
        SwitchChoices.Clear();
        // A5: in "This session" the current one is what the open session uses; in "Default for new sessions" the configured one.
        var current = _switchForSession ? slot.SessionModel ?? slot.BoundModel : slot.BoundModel;
        var scope = _switchForSession;
        foreach (var model in snapshot.Catalogue.Where(model => model.CanServe.Contains(slot.Name)))
        {
            // The current model says so; why another could not be picked is for the others.
            var reason = model.IsAvailable || model.Name == current ? ""
                : model.Kind == ModelKind.Local ? (model.State is ModelState.Downloading or ModelState.Queued ? "downloading" : "not downloaded")
                : model.Secret is { } secret ? $"set {SafeText.Clean(secret)} in Vault" : "unavailable";
            var name = model.Name;
            // Revealed: choosing it switches the slot in one click, so a hidden character in the name must show.
            SwitchChoices.Add(new SwitchChoiceViewModel(SafeText.Reveal(name), $"{Spans.Plain(model.Where)} · {SafeText.Clean(model.Serves)}", name == current,
                model.IsAvailable, reason, new RelayCommand(() => Run(() => SwitchAsync(slot.Name, name, scope)), () => model.IsAvailable && name != current)));
        }

        SwitchScopes.Clear();
        SwitchScopes.Add(new Choice("This session", "Switches the open session only, until you change it", new RelayCommand(() => OpenSwitch(slot, true)))
        {
            IsSelected = _switchForSession,
        });
        SwitchScopes.Add(new Choice("Default for new sessions", "Writes corvid.json, as corvid model switch does; open sessions keep their own choice",
            new RelayCommand(() => OpenSwitch(slot, false)))
        {
            IsSelected = !_switchForSession,
        });
        SwitchHint = _switchForSession
            ? "This session only, until you change it: escalation still moves off it and the turn's end comes back to it. A running turn takes it at its next step, after its issued tool calls."
            : "Written to corvid.json: new sessions start on it. Open sessions keep their own choice; one that made none follows it when its turn ends.";
        ForgetChoiceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanForgetChoice));
        OnPropertyChanged(nameof(SwitchForSession));

        _switchSlot = slot.Name;
        SwitchTitle = $"Switch the {slot.Name}";
        Picker = ModelsPicker.Switch;
    }

    private async Task ForgetChoiceAsync(string slot)
    {
        var answer = await _service.ClearSessionChoiceAsync(slot).ConfigureAwait(true);
        if (answer.Written)
            ClosePicker();
        Report(answer);
    }

    internal void TogglePin(SlotRowViewModel slot) =>
        Run(async () => Report(await _service.SetPinnedAsync(slot.Name, !slot.IsPinned).ConfigureAwait(true)));

    internal void WriteLadder(SlotRowViewModel slot, IReadOnlyList<string> ladder) =>
        Run(async () => Report(await _service.SetLadderAsync(slot.Name, ladder).ConfigureAwait(true)));

    internal void Act(CatalogueRowViewModel row)
    {
        var name = row.Model.Name;
        switch (row.Model.State)
        {
            case ModelState.Downloading or ModelState.Queued:
                Run(async () =>
                {
                    await _service.CancelDownloadAsync(name).ConfigureAwait(true);
                    Say(true, $"`{Spans.Escape(name)}`: download cancelled. The partial file was removed, so the cache holds only finished models.");
                });
                break;
            case ModelState.Missing:
                Run(() => _service.DownloadAsync([name]));
                break;
            case ModelState.Reachable or ModelState.Unreachable:
                Run(async () =>
                {
                    var check = await _service.CheckServerAsync(name).ConfigureAwait(true);
                    Say(check.Reachable, check.Message);
                });
                break;
            case ModelState.KeyBound:
                OpenEgress();
                break;
            case ModelState.Unavailable:
                _shell.Navigate(Area.Secrets);
                break;
        }
    }

    private async Task SwitchAsync(string slot, string model, bool forSession)
    {
        // A local model loads before the switch is made (Kev-4B takes seconds on the CPU): until then the open session is on
        // the old one, so the picker says so rather than looking as if the click did nothing.
        var hint = SwitchHint;
        var pending = forSession
            ? $"Switching this session's {slot} to {SafeText.Clean(model)}… A model that is not loaded yet loads first; until then the session stays on {SafeText.Clean(_service.Snapshot.Slot(slot)?.SessionModel ?? _service.Snapshot.Slot(slot)?.Model ?? "its current model")}."
            : $"Writing {SafeText.Clean(model)} as the {slot} for new sessions…";
        SwitchHint = pending;
        ConfigWrite answer;
        try
        {
            answer = forSession
                ? await _service.SwitchSessionAsync(slot, model).ConfigureAwait(true)
                : await _service.SwitchAsync(slot, model).ConfigureAwait(true);
        }
        finally
        {
            if (SwitchHint == pending)
                SwitchHint = hint;
        }
        if (answer.Written)
            ClosePicker();
        Report(answer);
    }

    private async Task AddServerAsync(CancellationToken cancellationToken)
    {
        // A name or address with hidden characters is not sent to be stored; the service refuses it too.
        if (SafeText.HasHidden(_serverName) || SafeText.HasHidden(_serverUrl))
        {
            Say(false, $"The server's name or address holds hidden characters (`{Spans.Escape(SafeText.Reveal(_serverName))}`, " +
                $"`{Spans.Escape(SafeText.Reveal(_serverUrl))}`). Nothing was written: type them again.");
            return;
        }

        var answer = await _service.AddServerAsync(_serverName, _serverUrl, cancellationToken).ConfigureAwait(true);
        if (answer.Written)
        {
            ClosePicker();
            ServerName = "";
            ServerUrl = "";
        }

        Report(answer);
    }

    private async Task DownloadMissingAsync(CancellationToken cancellationToken)
    {
        await _service.DownloadAsync(MissingModels().Select(model => model.Name), cancellationToken).ConfigureAwait(true);
        Say(true, "Downloading the missing models. The decider leaves its degraded tier when `kev-0.8b` is done.");
    }

    private async Task SavePlacementAsync(CancellationToken cancellationToken)
    {
        var answers = new List<ConfigWrite>();
        foreach (var row in PlacementRows.Where(row => row.IsChanged).ToList())
            answers.Add(await _service.SetPlacementAsync(row.Id, row.Device, cancellationToken).ConfigureAwait(true));
        IsEditingPlacement = false;
        if (answers.Count == 0)
            Say(true, "Placement unchanged.");
        else if (answers.FirstOrDefault(answer => !answer.Written) is { } refused)
            Report(refused);
        else
            Say(true, string.Join(" ", answers.Select(answer => answer.Message)));
    }

    private void Open(ModelsPicker picker)
    {
        if (picker == ModelsPicker.Download)
            Downloads.Refresh();
        Picker = picker;
    }

    private void ClosePicker()
    {
        if (_picker != ModelsPicker.None)
            Picker = ModelsPicker.None;
        else if (_editingPlacement)
            IsEditingPlacement = false;
    }

    /// <summary>Audit's egress ledger, landing on the latest request that left the machine (the window's session's).</summary>
    private void OpenEgress() => _shell.Follow(new AreaLink(Area.Audit, _shell.CurrentSession?.Id, AreaLinkTarget.Egress));

    private void Report(ConfigWrite answer) => Say(answer.Written, answer.Message);

    /// <summary>A button's run that failed shows as a red notice: the command keeps the error, and nothing else would show it.</summary>
    private void ReportFailures(AsyncRelayCommand command) => command.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(AsyncRelayCommand.Error) && command.Error is { } error)
            Say(false, $"That did not work: {Spans.Escape(error.Message)}");
    };

    private void Say(bool ok, string message)
    {
        NoticeTone = ok ? Tone.Green : Tone.Red;
        Notice = Spans.Parse(message);
    }

    /// <summary>Runs a request, turning a failure into a red notice rather than an unhandled exception.</summary>
    private async void Run(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Say(false, $"That did not work: {Spans.Escape(exception.Message)}");
        }
    }

    private List<CatalogueModel> MissingModels() =>
        [.. _service.Snapshot.Catalogue.Where(model => model.InDefaultSet && model.IsMissing)];

    private void Refresh()
    {
        var snapshot = _service.Snapshot;
        // A store that could not read the configuration again after its own change (a download finishing) says so once.
        if (snapshot.Problem is { } problem && !string.Equals(problem, _problem, StringComparison.Ordinal))
            Say(false, Spans.Escape(problem));
        _problem = snapshot.Problem;
        Subtitle = snapshot.IsLoaded
            ? Spans.Parse($"config `{Spans.Escape(snapshot.ConfigPath)}` · {(snapshot.Validated ? "validated" : "not valid")} · {ConfigNote}")
            : Spans.Parse("reading the configuration and the model cache…");

        if (!snapshot.Slots.Select(slot => slot.Name).SequenceEqual(Slots.Select(slot => slot.Name)))
        {
            Slots.Clear();
            foreach (var slot in snapshot.Slots)
                Slots.Add(new SlotRowViewModel(slot.Name, this));
        }

        foreach (var (row, slot) in Slots.Zip(snapshot.Slots))
            row.Update(slot, snapshot);

        if (!snapshot.Catalogue.Select(model => model.Name).SequenceEqual(Catalogue.Select(row => row.Model.Name)))
        {
            Catalogue.Clear();
            foreach (var model in snapshot.Catalogue)
                Catalogue.Add(new CatalogueRowViewModel(model, this));
        }
        else
        {
            foreach (var (row, model) in Catalogue.Zip(snapshot.Catalogue))
                row.Update(model);
        }

        Downloads.Refresh();
        RefreshMissing();
        RefreshPanels(snapshot);
        if (_active)
            ApplyStatus();
    }

    private void RefreshMissing()
    {
        var missing = MissingModels();
        _shell.SetBadge(Area.Models, missing.Count);
        DownloadMissingCommand.NotifyCanExecuteChanged();
        if (missing.Count == 0)
        {
            MissingText = [];
            MissingButtonText = "";
            return;
        }

        var names = string.Join(" and ", missing.Select(model => $"`{Spans.Escape(SafeText.Reveal(model.Name))}` ({Spans.Escape(model.Role)})"));
        var decider = missing.FirstOrDefault(model => model.CanServe.Contains(ModelSlotNames.Decider));
        var consequence = decider is not null
            ? $" Decisions run on the degraded tier and every chat says so until `{Spans.Escape(decider.Name)}` is downloaded."
            : "";
        MissingText = Spans.Parse($"**{(missing.Count == 1 ? "1 model is" : $"{missing.Count} models are")} missing:** {names}.{consequence}");
        MissingButtonText = $"Download {ModelText.Size(missing.Sum(model => model.SizeBytes))}";
    }

    private void RefreshPanels(ModelsSnapshot snapshot)
    {
        var privacy = snapshot.Privacy;
        PrivacyTag = $"cloud: {privacy.Posture}";
        var gate = !snapshot.IsLoaded ? "Checking whether Vault answers." : (privacy.VaultPresent, privacy.ExtractorBound) switch
        {
            (false, _) => "Vault is absent, so cloud slots are hidden until it answers.",
            (true, false) => "No extractor is bound, so every cloud request is refused.",
            _ => "The extractor is bound, so cloud slots are available.",
        };
        // What the posture does to a request, as the configuration says it (Corvid:Privacy:Cloud).
        var posture = privacy.Posture switch
        {
            "refuse" => "Requests to cloud models are refused: nothing that holds PII or a credential leaves for a cloud model.",
            "send" => "Requests to cloud models are sent as they are: nothing is redacted.",
            _ => "Requests to cloud models are redacted before they leave: PII and any credential value.",
        };
        PrivacySummary = Spans.Parse($"{posture} {gate}");
        PrivacyFacts =
        [
            Fact("today", privacy.TodayUnknown is { } unknown
                ? Spans.Escape(unknown)
                : $"{privacy.RequestsToday} cloud requests · {privacy.RedactedToday} values redacted · {privacy.RefusedToday} refused"),
            Fact("without Vault", "cloud slots disappear; no key is ever read from the environment"),
        ];

        var decider = snapshot.Decider;
        var calibrated = decider.Tier == DeciderTier.Calibrated;
        DeciderTag = !snapshot.IsLoaded ? "checking" : calibrated ? $"{decider.DisplayName}, calibrated"
            // No decider configured at all (the slot is unbound): nothing is missing, the tier is simply degraded.
            : decider.Model.Length == 0 ? "no decider: degraded" : $"{decider.DisplayName} missing: degraded";
        DeciderTone = !snapshot.IsLoaded ? Tone.Grey : calibrated ? Tone.Green : Tone.Amber;
        DeciderFacts =
        [
            Fact("floors", decider.Floors),
            Fact("second tier", decider.SecondTier),
            Fact("fallback", decider.Fallback),
            Fact("profile", decider.Profile),
        ];

        PlacementFacts =
        [
            .. snapshot.Gpus.Select(gpu => Fact(gpu.Id, Spans.Escape(gpu.Summary))),
            .. snapshot.Placements.Select(placement => Fact(placement.Model,
                $"device {Spans.Escape(placement.Device)} · fallback {Spans.Escape(placement.Fallback)}{(placement.NoticeOnFallback ? ", with a notice" : "")}")),
            Fact("cpu", Spans.Escape(snapshot.Cpu)),
        ];
    }

    private void FillPlacementRows()
    {
        var snapshot = _service.Snapshot;
        PlacementRows.Clear();
        foreach (var placement in snapshot.Placements)
            PlacementRows.Add(new PlacementRowViewModel(placement, snapshot.Gpus.FirstOrDefault()));
    }

    private static ModelFact Fact(string key, string value) => new(SafeText.Clean(key), Spans.Parse(value));

    /// <summary>
    /// Replaces a panel's facts only when their text changed: the snapshot changes on every download tick, and a list
    /// rebuilt each time would make the view regenerate its rows four times a second.
    /// </summary>
    private bool SetRuns(ref IReadOnlyList<TextRun> field, IReadOnlyList<TextRun> value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field.SequenceEqual(value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void SetFacts(ref IReadOnlyList<ModelFact> field, IReadOnlyList<ModelFact> value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (field.Select(fact => fact.AutomationName).SequenceEqual(value.Select(fact => fact.AutomationName)))
            return;
        field = value;
        OnPropertyChanged(name);
    }

    // The status bar while Models shows (Models.dc.html): generator, decider, downloads, the mode, scheduler, branch, "idle".

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentArea))
            UpdateActive();
    }

    private void UpdateActive()
    {
        var active = _shell.CurrentArea == Area.Models;
        if (active == _active)
            return;
        _active = active;
        if (active)
        {
            _lease.Take();
            ApplyStatus();
            return;
        }

        // Leaving: what this area wrote and nobody has written since goes back to what it replaced (the session hub
        // writes the session areas' own items), and the session id, which the hub does not set, comes back.
        ClosePicker();
        _lease.Release();
        if (_bar.SessionId.Length == 0)
            _bar.SessionId = _shell.CurrentSession?.Id ?? "";
    }

    private void ApplyStatus()
    {
        var snapshot = _service.Snapshot;
        var generator = snapshot.Slot(ModelSlotNames.Generator);
        _lease.Engine = "";
        _lease.Gateway = "";
        _lease.Context = "";
        _lease.Generator = SafeText.Clean(generator is { Model: { } model, Placement: { } at } ? $"{model} · {ModelText.Placement(at)}" : generator?.Model ?? "");
        _lease.Decider = StatusDecider(snapshot);
        _lease.AreaFields = StatusDownloads(snapshot);
        _lease.Tags = [];
        _lease.SessionId = "";
        _lease.Trailing = "idle";
    }

    /// <summary>
    /// "Kev-0.8B, calibrated", "degraded until kev-0.8b is downloaded", or "degraded tier (no decider configured)" when the
    /// decider slot is unbound.
    /// </summary>
    public static string StatusDecider(ModelsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.IsLoaded)
            return "";
        return SafeText.Clean(snapshot.Decider.Tier == DeciderTier.Calibrated
            ? $"{snapshot.Decider.DisplayName}, calibrated"
            : snapshot.Decider.Model.Length == 0 ? "degraded tier (no decider configured)"
            : $"degraded until {snapshot.Decider.Model} is downloaded");
    }

    /// <summary>"downloads kev-4b 42%", with how many wait behind it; nothing when no download runs.</summary>
    public static IReadOnlyList<StatusField> StatusDownloads(ModelsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var running = snapshot.Catalogue.Where(model => model.State == ModelState.Downloading && model.Download is not null).ToList();
        var queued = snapshot.Catalogue.Count(model => model.State == ModelState.Queued);
        if (running.Count == 0 && queued == 0)
            return [];
        var text = string.Join(" · ", running.Select(model => $"{SafeText.Clean(model.Name)} {model.Download!.Percent}%"));
        if (queued > 0)
            text = text.Length > 0 ? $"{text} · {queued} queued" : $"{queued} queued";
        return [new StatusField("downloads", text)];
    }
}
