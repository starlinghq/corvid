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
        Model = placement.Model;
        Original = placement.Device;
        _device = placement.Device;
        Fallback = placement.NoticeOnFallback ? $"fallback {placement.Fallback}, with a notice" : $"fallback {placement.Fallback}";
        Checks = gpu is null
            ? []
            : [.. gpu.Checks.Select(check => new ModelFact(check.Name, Spans.Parse($"{(check.Ok ? "**ok**" : "**failed**")} · {check.Detail}")))];
        Devices = gpu is null ? ["cpu"] : ["cuda", "cpu"];
        Choices = [.. Devices.Select(device => new Choice(device, device == "cuda" ? $"{gpu!.Id}: {gpu.Summary}" : "the CPU",
            new RelayCommand(() => Device = device)) { IsSelected = device == _device })];
    }

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
    private IReadOnlyList<StatusField> _myFields = [];
    private bool _active;
    private IReadOnlyList<TextRun> _notice = [];
    private Tone _noticeTone = Tone.Green;
    private ModelsPicker _picker;
    private string _switchTitle = "";
    private string _switchSlot = "";
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
        OpenEgressCommand = new RelayCommand(() => _shell.Navigate(Area.Audit));
        EditPlacementCommand = new RelayCommand(() => IsEditingPlacement = true);
        SavePlacementCommand = new AsyncRelayCommand(SavePlacementAsync);
        CancelPlacementCommand = new RelayCommand(() => IsEditingPlacement = false);
        SwitchGeneratorCommand = new RelayCommand(() =>
        {
            if (Slots.FirstOrDefault(slot => slot.Name == ModelSlotNames.Generator) is { CanSwitch: true } generator)
                OpenSwitch(generator);
        });

        _service.Changed += (_, _) => Refresh();
        _shell.PropertyChanged += OnShellChanged;
        Refresh();
        UpdateActive();
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

    internal void OpenSwitch(SlotRowViewModel slot)
    {
        var snapshot = _service.Snapshot;
        SwitchChoices.Clear();
        foreach (var model in snapshot.Catalogue.Where(model => model.CanServe.Contains(slot.Name)))
        {
            var reason = model.IsAvailable ? ""
                : model.Kind == ModelKind.Local ? (model.State is ModelState.Downloading or ModelState.Queued ? "downloading" : "not downloaded")
                : model.Secret is { } secret ? $"set {secret} in Vault" : "unavailable";
            var name = model.Name;
            SwitchChoices.Add(new SwitchChoiceViewModel(name, $"{Spans.Plain(model.Where)} · {model.Serves}", name == slot.Model,
                model.IsAvailable, reason, new RelayCommand(() => Run(() => SwitchAsync(slot.Name, name)), () => model.IsAvailable && name != slot.Model)));
        }

        _switchSlot = slot.Name;
        SwitchTitle = $"Switch the {slot.Name}";
        Picker = ModelsPicker.Switch;
    }

    internal void TogglePin(SlotRowViewModel slot) =>
        Run(async () => Report(await _service.SetPinnedAsync(slot.Name, !slot.IsPinned).ConfigureAwait(true)));

    internal void WriteLadder(SlotRowViewModel slot, IReadOnlyList<string> ladder) =>
        Run(async () => Report(await _service.SetLadderAsync(slot.Name, ladder).ConfigureAwait(true)));

    internal void Act(CatalogueRowViewModel row)
    {
        var name = row.Name;
        switch (row.Model.State)
        {
            case ModelState.Downloading or ModelState.Queued:
                Run(async () =>
                {
                    await _service.CancelDownloadAsync(name).ConfigureAwait(true);
                    Say(true, $"`{name}`: download cancelled. The partial file was removed, so the cache holds only finished models.");
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
                _shell.Navigate(Area.Audit);
                break;
            case ModelState.Unavailable:
                _shell.Navigate(Area.Secrets);
                break;
        }
    }

    private async Task SwitchAsync(string slot, string model)
    {
        var answer = await _service.SwitchAsync(slot, model).ConfigureAwait(true);
        if (answer.Written)
            ClosePicker();
        Report(answer);
    }

    private async Task AddServerAsync(CancellationToken cancellationToken)
    {
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
            answers.Add(await _service.SetPlacementAsync(row.Model, row.Device, cancellationToken).ConfigureAwait(true));
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

    private void Report(ConfigWrite answer) => Say(answer.Written, answer.Message);

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
            Say(false, $"That did not work: {exception.Message}");
        }
    }

    private List<CatalogueModel> MissingModels() =>
        [.. _service.Snapshot.Catalogue.Where(model => model.InDefaultSet && model.IsMissing)];

    private void Refresh()
    {
        var snapshot = _service.Snapshot;
        Subtitle = Spans.Parse($"config `{snapshot.ConfigPath}` · {(snapshot.Validated ? "validated" : "not valid")} · {ConfigNote}");

        if (!snapshot.Slots.Select(slot => slot.Name).SequenceEqual(Slots.Select(slot => slot.Name)))
        {
            Slots.Clear();
            foreach (var slot in snapshot.Slots)
                Slots.Add(new SlotRowViewModel(slot.Name, this));
        }

        foreach (var (row, slot) in Slots.Zip(snapshot.Slots))
            row.Update(slot, snapshot);

        if (!snapshot.Catalogue.Select(model => model.Name).SequenceEqual(Catalogue.Select(row => row.Name)))
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

        var names = string.Join(" and ", missing.Select(model => $"`{model.Name}` ({model.Role})"));
        var decider = missing.FirstOrDefault(model => model.CanServe.Contains(ModelSlotNames.Decider));
        var consequence = decider is not null
            ? $" Decisions run on the degraded tier and every chat says so until `{decider.Name}` is downloaded."
            : "";
        MissingText = Spans.Parse($"**{(missing.Count == 1 ? "1 model is" : $"{missing.Count} models are")} missing:** {names}.{consequence}");
        MissingButtonText = $"Download {ModelText.Size(missing.Sum(model => model.SizeBytes))}";
    }

    private void RefreshPanels(ModelsSnapshot snapshot)
    {
        var privacy = snapshot.Privacy;
        PrivacyTag = $"cloud: {privacy.Posture}";
        var gate = (privacy.VaultPresent, privacy.ExtractorBound) switch
        {
            (false, _) => "Vault is absent, so cloud slots are hidden until it answers.",
            (true, false) => "No extractor is bound, so every cloud request is refused.",
            _ => "The extractor is bound, so cloud slots are available.",
        };
        PrivacySummary = Spans.Parse($"Requests to cloud models are redacted before they leave: PII and any credential value. {gate}");
        PrivacyFacts =
        [
            Fact("today", $"{privacy.RequestsToday} cloud requests · {privacy.RedactedToday} values redacted · {privacy.RefusedToday} refused"),
            Fact("without Vault", "cloud slots disappear; no key is ever read from the environment"),
        ];

        var decider = snapshot.Decider;
        var calibrated = decider.Tier == DeciderTier.Calibrated;
        DeciderTag = calibrated ? $"{decider.DisplayName}, calibrated" : $"{decider.DisplayName} missing: degraded";
        DeciderTone = calibrated ? Tone.Green : Tone.Amber;
        DeciderFacts =
        [
            Fact("floors", decider.Floors),
            Fact("second tier", decider.SecondTier),
            Fact("fallback", decider.Fallback),
            Fact("profile", decider.Profile),
        ];

        PlacementFacts =
        [
            .. snapshot.Gpus.Select(gpu => Fact(gpu.Id, gpu.Summary)),
            .. snapshot.Placements.Select(placement => Fact(placement.Model,
                $"device {placement.Device} · fallback {placement.Fallback}{(placement.NoticeOnFallback ? ", with a notice" : "")}")),
            Fact("cpu", snapshot.Cpu),
        ];
    }

    private void FillPlacementRows()
    {
        var snapshot = _service.Snapshot;
        PlacementRows.Clear();
        foreach (var placement in snapshot.Placements)
            PlacementRows.Add(new PlacementRowViewModel(placement, snapshot.Gpus.FirstOrDefault()));
    }

    private static ModelFact Fact(string key, string value) => new(key, Spans.Parse(value));

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
            ApplyStatus();
            return;
        }

        // Leaving: the session's own status comes back from the session hub for the session areas; what this area added
        // goes, and the session id (which the hub does not set) comes back.
        ClosePicker();
        if (ReferenceEquals(_bar.AreaFields, _myFields))
            _bar.AreaFields = [];
        if (_bar.SessionId.Length == 0)
            _bar.SessionId = _shell.CurrentSession?.Id ?? "";
    }

    private void ApplyStatus()
    {
        var snapshot = _service.Snapshot;
        var generator = snapshot.Slot(ModelSlotNames.Generator);
        _bar.Engine = "";
        _bar.Gateway = "";
        _bar.Context = "";
        _bar.Generator = generator is { Model: { } model, Placement: { } at } ? $"{model} · {ModelText.Placement(at)}" : generator?.Model ?? "";
        _bar.Decider = StatusDecider(snapshot);
        _myFields = StatusDownloads(snapshot);
        _bar.AreaFields = _myFields;
        _bar.Tags = [];
        _bar.SessionId = "";
        _bar.Trailing = "idle";
    }

    /// <summary>"Kev-0.8B, calibrated", or "degraded until kev-0.8b is downloaded".</summary>
    public static string StatusDecider(ModelsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Decider.Tier == DeciderTier.Calibrated
            ? $"{snapshot.Decider.DisplayName}, calibrated"
            : $"degraded until {snapshot.Decider.Model} is downloaded";
    }

    /// <summary>"downloads kev-4b 42%", with how many wait behind it; nothing when no download runs.</summary>
    public static IReadOnlyList<StatusField> StatusDownloads(ModelsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var running = snapshot.Catalogue.Where(model => model.State == ModelState.Downloading && model.Download is not null).ToList();
        var queued = snapshot.Catalogue.Count(model => model.State == ModelState.Queued);
        if (running.Count == 0 && queued == 0)
            return [];
        var text = string.Join(" · ", running.Select(model => $"{model.Name} {model.Download!.Percent}%"));
        if (queued > 0)
            text = text.Length > 0 ? $"{text} · {queued} queued" : $"{queued} queued";
        return [new StatusField("downloads", text)];
    }
}
