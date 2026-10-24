using System.Collections.ObjectModel;
using System.Windows.Input;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Models;

/// <summary>A key and its value in a panel's list (corvid.css <c>.kv</c>): "floors", "catalogue 2026-10-21 · …".</summary>
public sealed record ModelFact(string Key, IReadOnlyList<TextRun> Value)
{
    public string AutomationName => $"{Key}: {string.Concat(Value.Select(run => run.Text))}";
}

/// <summary>
/// One step of a slot's escalation ladder (the <c>⋮⋮ qwen-27b</c> chips): dragged, or moved with Alt+Left and Alt+Right,
/// to change the order a hard step goes up it. The arrow after it shows unless it is the last.
/// </summary>
public sealed class LadderStepViewModel
{
    internal LadderStepViewModel(SlotRowViewModel slot, string model, int index, int count)
    {
        Slot = slot;
        Model = SafeText.Clean(model);
        Index = index;
        IsLast = index == count - 1;
        MoveEarlierCommand = new RelayCommand(() => slot.MoveStep(index, index - 1), () => index > 0);
        MoveLaterCommand = new RelayCommand(() => slot.MoveStep(index, index + 1), () => index < count - 1);
        AutomationName = $"{Model}, step {index + 1} of {count} of the {slot.Name}'s escalation ladder. Alt+Left or Alt+Right moves it.";
    }

    public SlotRowViewModel Slot { get; }

    public string Model { get; }

    public int Index { get; }

    public bool IsLast { get; }

    public bool ShowArrow => !IsLast;

    public RelayCommand MoveEarlierCommand { get; }

    public RelayCommand MoveLaterCommand { get; }

    public string AutomationName { get; }
}

/// <summary>
/// One row of the Slots table: the slot, its model (or "unbound"), the ladder and fallback with a note, placement and
/// median latency with a gauge, and its actions (Switch…, Pin; the decider says it is shared instead).
/// </summary>
public sealed class SlotRowViewModel : ObservableObject
{
    /// <summary>The latency the gauge is full at: the slots' p50s are drawn against 600 ms.</summary>
    public const double GaugeFullMilliseconds = 600;

    private readonly ModelsViewModel _owner;
    private IReadOnlyList<string> _visibleLadder = [];
    private IReadOnlyList<string> _hiddenLadder = [];
    private string _model = "";
    private bool _isBound;
    private string _currentNote = "";
    private string _tagText = "";
    private Tone _tagTone;
    private string _fallback = "";
    private string _note = "";
    private string _plain = "";
    private string _placement = "";
    private double _latency;
    private bool _canSwitch;
    private bool _canPin;
    private bool _pinned;
    private bool _shared;

    internal SlotRowViewModel(string name, ModelsViewModel owner)
    {
        Name = name;
        _owner = owner;
        SwitchCommand = new RelayCommand(() => owner.OpenSwitch(this), () => _canSwitch);
        PinCommand = new RelayCommand(() => owner.TogglePin(this), () => _canPin);
    }

    public string Name { get; }

    /// <summary>The bound model, or "unbound".</summary>
    public string Model { get => _model; private set => SetProperty(ref _model, value); }

    /// <summary>The bound model's name as configured (not cleaned), for comparing with the catalogue's names; null when unbound.</summary>
    public string? BoundModel { get; private set; }

    public bool IsBound { get => _isBound; private set => SetProperty(ref _isBound, value); }

    /// <summary>Under the model: "default", "pinned".</summary>
    public string CurrentNote { get => _currentNote; private set { if (SetProperty(ref _currentNote, value)) OnPropertyChanged(nameof(HasCurrentNote)); } }

    /// <summary>The decider's tier beside its model: "calibrated" (green) or "degraded" (amber).</summary>
    public string TagText { get => _tagText; private set { if (SetProperty(ref _tagText, value)) OnPropertyChanged(nameof(HasTag)); } }

    public Tone TagTone { get => _tagTone; private set => SetProperty(ref _tagTone, value); }

    public ObservableCollection<LadderStepViewModel> Ladder { get; } = [];

    public bool HasLadder => Ladder.Count > 0;

    /// <summary>The fallback model ("e5"), shown as "fallback e5".</summary>
    public string Fallback
    {
        get => _fallback;
        private set
        {
            if (!SetProperty(ref _fallback, value))
                return;
            OnPropertyChanged(nameof(HasFallback));
            OnPropertyChanged(nameof(FallbackText));
        }
    }

    public bool HasFallback => _fallback.Length > 0;

    /// <summary>"fallback" muted, then the model in the code font.</summary>
    public IReadOnlyList<TextRun> FallbackText => _fallback.Length > 0 ? [new TextRun("fallback ", Muted: true), new TextRun(_fallback, Code: true)] : [];

    public bool HasNote => _note.Length > 0;

    public bool HasPlain => _plain.Length > 0;

    public bool HasCurrentNote => _currentNote.Length > 0;

    public bool HasTag => _tagText.Length > 0;

    /// <summary>The small note under the ladder or fallback ("a hard step escalates up this ladder").</summary>
    public string Note { get => _note; private set { if (SetProperty(ref _note, value)) OnPropertyChanged(nameof(HasNote)); } }

    /// <summary>The cell's text when there is no ladder and no fallback ("none", "needed for images in prompts (K-8)").</summary>
    public string Plain { get => _plain; private set { if (SetProperty(ref _plain, value)) OnPropertyChanged(nameof(HasPlain)); } }

    /// <summary>"cuda:0 · q0 · 180 ms", or "—" for an unbound slot.</summary>
    public string Placement { get => _placement; private set => SetProperty(ref _placement, value); }

    public bool HasPlacement => IsBound;

    /// <summary>The median latency against <see cref="GaugeFullMilliseconds"/>, for the gauge under it.</summary>
    public double Latency { get => _latency; private set => SetProperty(ref _latency, value); }

    public bool CanSwitch { get => _canSwitch; private set { if (SetProperty(ref _canSwitch, value)) { SwitchCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(ShowActions)); } } }

    public bool CanPin { get => _canPin; private set { if (SetProperty(ref _canPin, value)) PinCommand.NotifyCanExecuteChanged(); } }

    public bool IsPinned { get => _pinned; private set { if (SetProperty(ref _pinned, value)) OnPropertyChanged(nameof(PinLabel)); } }

    public string PinLabel => _pinned ? "Unpin" : "Pin";

    /// <summary>The decider: shared by every session, so switching it is a command and a restart, not a button.</summary>
    public bool IsShared { get => _shared; private set { if (SetProperty(ref _shared, value)) OnPropertyChanged(nameof(ShowActions)); } }

    /// <summary>Switch… (and Pin) show for a bound slot that is not shared.</summary>
    public bool ShowActions => _canSwitch;

    public IReadOnlyList<TextRun> SharedText { get; } =
        Spans.Parse("shared by every session: `corvid model switch decider …`, then restart");

    public RelayCommand SwitchCommand { get; }

    public RelayCommand PinCommand { get; }

    public string AutomationName => IsBound ? $"{Name}: {Model}" : $"{Name}: unbound";

    /// <summary>The ladder as configured, including steps hidden while Vault is absent.</summary>
    public IReadOnlyList<string> FullLadder => [.. _visibleLadder, .. _hiddenLadder];

    /// <summary>Moves a ladder step; the owner validates and writes the new order.</summary>
    public void MoveStep(int from, int to)
    {
        if (from < 0 || from >= _visibleLadder.Count || to < 0 || to >= _visibleLadder.Count || from == to)
            return;
        var order = _visibleLadder.ToList();
        var step = order[from];
        order.RemoveAt(from);
        order.Insert(to, step);
        _owner.WriteLadder(this, [.. order, .. _hiddenLadder]);
    }

    internal void Update(ModelSlot slot, ModelsSnapshot snapshot)
    {
        IsBound = slot.IsBound;
        // Names from the configuration are data: drawn cleaned; what is written back uses the configured names.
        Model = slot.Model is { } bound ? SafeText.Clean(bound) : "unbound";
        BoundModel = slot.Model;
        IsPinned = slot.Pinned;
        IsShared = slot.Shared;
        CurrentNote = (slot.IsDefault, slot.Pinned) switch
        {
            (true, true) => "default · pinned",
            (true, false) => "default",
            (false, true) => "pinned",
            _ => "",
        };
        if (slot.Name == ModelSlotNames.Decider && slot.IsBound)
        {
            var calibrated = snapshot.Decider.Tier == DeciderTier.Calibrated;
            TagText = calibrated ? "calibrated" : "degraded";
            TagTone = calibrated ? Tone.Green : Tone.Amber;
        }
        else
        {
            TagText = "";
        }

        // Without Vault, cloud models are hidden from the ladder, with the reason (docs/plan/ui.md F23).
        var hidden = slot.Escalation.Where(step => IsHidden(snapshot, step)).ToList();
        _hiddenLadder = hidden;
        SetLadder(slot.Escalation.Where(step => !IsHidden(snapshot, step)).ToList());
        Fallback = SafeText.Clean(slot.Fallback);

        var note = SafeText.Clean(slot.Note);
        // A ladder step still on its way says so ("second tier for gates below their floor · downloading").
        var waiting = _visibleLadder.Select(snapshot.Find).FirstOrDefault(model => model is { Kind: ModelKind.Local } && model.State != ModelState.Cached);
        if (waiting is not null)
            note = $"{note} · {(waiting.State is ModelState.Downloading or ModelState.Queued ? "downloading" : "not downloaded")}";
        if (hidden.Count > 0)
            note = $"{string.Join(", ", hidden.Select(SafeText.Clean))} hidden: no Vault, so no key";
        if (HasLadder || Fallback.Length > 0)
        {
            Note = note;
            Plain = "";
        }
        else
        {
            Note = "";
            Plain = hidden.Count > 0 ? $"none · {note}" : slot.Note is null ? "none" : SafeText.Clean(slot.Note);
        }

        Placement = slot.Placement is { } at ? SafeText.Clean(ModelText.Placement(at)) : "—";
        Latency = slot.Placement is { } p ? Math.Clamp(p.P50Milliseconds / GaugeFullMilliseconds, 0, 1) : 0;
        OnPropertyChanged(nameof(HasPlacement));
        CanSwitch = slot.IsBound && !slot.Shared;
        CanPin = slot.Pinnable;
        OnPropertyChanged(nameof(AutomationName));
    }

    private static bool IsHidden(ModelsSnapshot snapshot, string step) =>
        !snapshot.Privacy.VaultPresent && snapshot.Find(step) is { Kind: ModelKind.Cloud };

    private void SetLadder(IReadOnlyList<string> ladder)
    {
        if (ladder.SequenceEqual(_visibleLadder) && Ladder.Count == ladder.Count)
            return;
        _visibleLadder = ladder;
        Ladder.Clear();
        for (var i = 0; i < ladder.Count; i++)
            Ladder.Add(new LadderStepViewModel(this, ladder[i], i, ladder.Count));
        OnPropertyChanged(nameof(HasLadder));
    }
}

/// <summary>
/// One row of the Catalogue and downloads table: the model and what it serves, where it runs, size and fit, its state
/// (tags, or a download's gauge and progress line), and the one action that state offers.
/// </summary>
public sealed class CatalogueRowViewModel : ObservableObject
{
    private readonly ModelsViewModel _owner;
    private CatalogueModel _model;
    private IReadOnlyList<TextRun> _where = [];
    private IReadOnlyList<StatusTag> _tags = [];
    private string _progress = "";
    private double _fraction;
    private bool _downloading;
    private string _action = "";
    private bool _danger;

    internal CatalogueRowViewModel(CatalogueModel model, ModelsViewModel owner)
    {
        _model = model;
        _owner = owner;
        // Revealed: its one button downloads (or cancels) this model, so a hidden character in the name must show.
        Name = SafeText.Reveal(model.Name);
        ActionCommand = new RelayCommand(() => owner.Act(this), () => _action.Length > 0);
        Update(model);
    }

    public string Name { get; }

    public CatalogueModel Model => _model;

    public string Serves => SafeText.Clean(_model.Serves);

    public IReadOnlyList<TextRun> Where { get => _where; private set { if (!_where.SequenceEqual(value)) SetProperty(ref _where, value); } }

    public string Fit => SafeText.Clean(_model.Fit);

    public IReadOnlyList<StatusTag> Tags { get => _tags; private set { if (!_tags.SequenceEqual(value)) SetProperty(ref _tags, value); } }

    public bool IsDownloading { get => _downloading; private set => SetProperty(ref _downloading, value); }

    public double Fraction { get => _fraction; private set => SetProperty(ref _fraction, value); }

    /// <summary>"42% · 7.1 of 17 GB · 48 MB/s · 3 m left".</summary>
    public string Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    /// <summary>The state's action: Cancel, Download, Check, Egress, Set the secret…; empty for none.</summary>
    public string ActionLabel { get => _action; private set { if (SetProperty(ref _action, value)) { ActionCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(HasAction)); } } }

    public bool HasAction => _action.Length > 0;

    /// <summary>The action stops something (Cancel): drawn in red.</summary>
    public bool ActionIsDanger { get => _danger; private set => SetProperty(ref _danger, value); }

    public RelayCommand ActionCommand { get; }

    public string AutomationName => $"{Name}, {Serves}: {string.Join(", ", Tags.Select(tag => tag.Text))}{(IsDownloading ? Progress : "")}";

    internal void Update(CatalogueModel model)
    {
        _model = model;
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(Serves));
        OnPropertyChanged(nameof(Fit));
        Where = Spans.Parse(model.Where);
        IsDownloading = model.State == ModelState.Downloading && model.Download is not null;
        Fraction = model.Download?.Fraction ?? 0;
        Progress = model.Download is { } download && IsDownloading ? ModelText.Progress(download) : "";
        Tags = model.State switch
        {
            ModelState.Cached => [new StatusTag("cached", Tone.Green)],
            ModelState.Missing => [new StatusTag("missing", Tone.Grey, "Not downloaded; nothing downloads until you say so.")],
            ModelState.Queued => [new StatusTag("queued", Tone.Grey, "Waits for the download before it.")],
            ModelState.Reachable => [new StatusTag("reachable", Tone.Green)],
            ModelState.Unreachable => [new StatusTag("unreachable", Tone.Amber)],
            ModelState.KeyBound => [new StatusTag(model.Egress is { } egress ? SafeText.Clean(egress) : "redact", Tone.Blue, "Egress posture: values are redacted before a request leaves."),
                new StatusTag("key bound", Tone.Green, $"The key is {SafeText.Clean(model.Secret)} in Vault; the app never sees it.")],
            ModelState.Unavailable => [new StatusTag("unavailable", Tone.Amber, model.Secret is { } secret ? $"Set {SafeText.Clean(secret)} in Vault." : "")],
            _ => [],
        };
        (ActionLabel, ActionIsDanger) = model.State switch
        {
            ModelState.Downloading or ModelState.Queued => ("Cancel", true),
            ModelState.Missing => ("Download", false),
            ModelState.Reachable or ModelState.Unreachable => ("Check", false),
            ModelState.KeyBound => ("Egress", false),
            ModelState.Unavailable => ("Set the secret…", false),
            _ => ("", false),
        };
        OnPropertyChanged(nameof(AutomationName));
    }
}

/// <summary>
/// One model offered for download (the first run's Models panel and the Models page's Download… picker): its name, role
/// and size, its state, and the check box. Cached models show checked and fixed; a download in flight shows its progress.
/// </summary>
public sealed class DownloadChoiceViewModel : ObservableObject
{
    private readonly Action _changed;
    private CatalogueModel _model;
    private bool _isSelected;
    private IReadOnlyList<TextRun> _text = [];
    private string _stateTag = "";
    private double _fraction;
    private string _progress = "";

    internal DownloadChoiceViewModel(CatalogueModel model, Action changed)
    {
        _model = model;
        _changed = changed;
        // The default set is preselected; the optional second tier is not.
        _isSelected = model.InDefaultSet || model.State != ModelState.Missing;
        Update(model);
    }

    public string Name => _model.Name;

    public CatalogueModel Model => _model;

    /// <summary>"`kev-0.8b` · decider · 2.1 GB ~missing~ · fits cuda:0" as runs.</summary>
    public IReadOnlyList<TextRun> Text { get => _text; private set { if (!_text.SequenceEqual(value)) SetProperty(ref _text, value); } }

    /// <summary>"cached" as a green tag after the text; empty otherwise.</summary>
    public string StateTag { get => _stateTag; private set => SetProperty(ref _stateTag, value); }

    public bool HasStateTag => _stateTag.Length > 0;

    /// <summary>Checked: cached models always, the rest as the person picks.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!IsEnabled || !SetProperty(ref _isSelected, value))
                return;
            _changed();
        }
    }

    /// <summary>Only a missing model can be picked or left out.</summary>
    public bool IsEnabled => _model.State == ModelState.Missing;

    public bool IsDownloading => _model.State is ModelState.Downloading or ModelState.Queued;

    public double Fraction { get => _fraction; private set => SetProperty(ref _fraction, value); }

    /// <summary>"downloading 42% · 0.9 of 2.1 GB · 48 MB/s · 26 s left", or "queued".</summary>
    public string Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public long SizeBytes => _model.SizeBytes;

    public string AutomationName => string.Concat(Text.Select(run => run.Text)) + (HasStateTag ? $" {StateTag}" : "");

    internal void Update(CatalogueModel model)
    {
        var wasEnabled = IsEnabled;
        _model = model;
        if (model.State != ModelState.Missing)
            _isSelected = true;
        else if (!wasEnabled)
            _isSelected = model.InDefaultSet;
        // The name is revealed (this box picks it for download) and escaped, like the role and note, so its marks are text.
        var name = $"`{Spans.Escape(SafeText.Reveal(model.Name))}` · {Spans.Escape(model.Role)} · {ModelText.Size(model.SizeBytes)}";
        var note = model.SetupNote.Length > 0 ? $" · {Spans.Escape(model.SetupNote)}" : "";
        Text = Spans.Parse(model.State switch
        {
            ModelState.Cached => name,
            ModelState.Missing when !model.InDefaultSet => $"{name}{note} ~optional~",
            ModelState.Missing => $"{name} ~missing~{note}",
            _ => $"{name}{note}",
        });
        StateTag = model.State == ModelState.Cached ? "cached" : "";
        Fraction = model.Download?.Fraction ?? 0;
        Progress = model.State switch
        {
            ModelState.Queued => "queued",
            ModelState.Downloading when model.Download is { } download => $"downloading {ModelText.Progress(download)}",
            _ => "",
        };
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(IsEnabled));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(HasStateTag));
        OnPropertyChanged(nameof(AutomationName));
    }
}

/// <summary>
/// A pick of models to download with their total (the first run's Models panel, F1; the Models page's Download…):
/// "selected 3.3 GB · about 2 minutes at 48 MB/s", and the command that starts them.
/// </summary>
public sealed class DownloadSelectionViewModel : ObservableObject
{
    private readonly IModelService _service;
    private string _selected = "";
    private bool _hasPending;

    public DownloadSelectionViewModel(IModelService service)
    {
        ArgumentNullException.ThrowIfNull(service);
        _service = service;
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, () => SelectedBytes > 0);
        Refresh();
    }

    /// <summary>Raised after the selected models were queued.</summary>
    public event EventHandler? Started;

    /// <summary>Local models, in the catalogue's order.</summary>
    public ObservableCollection<DownloadChoiceViewModel> Items { get; } = [];

    /// <summary>The bytes of the picked models that still need downloading.</summary>
    public long SelectedBytes => Items.Where(item => item.IsSelected && item.IsEnabled).Sum(item => item.SizeBytes);

    /// <summary>"**3.3 GB** · about 2 minutes at 48 MB/s".</summary>
    public string SelectedText { get => _selected; private set => SetProperty(ref _selected, value); }

    public IReadOnlyList<TextRun> SelectedRuns { get; private set; } = [];

    /// <summary>Some model is missing, so there is something to pick.</summary>
    public bool HasPending { get => _hasPending; private set => SetProperty(ref _hasPending, value); }

    public AsyncRelayCommand DownloadCommand { get; }

    /// <summary>
    /// Takes the catalogue's latest state: new rows, states, progress. The list is ordered once, as FirstRun.png lists it
    /// (what is cached, then the missing default models smallest first, then the optional ones), and keeps that order
    /// while downloads finish, so a row never jumps under the pointer.
    /// </summary>
    public void Refresh()
    {
        var local = _service.Snapshot.Catalogue.Where(model => model.Kind == ModelKind.Local).ToList();
        if (!local.Select(model => model.Name).Order(StringComparer.Ordinal).SequenceEqual(Items.Select(item => item.Name).Order(StringComparer.Ordinal)))
        {
            Items.Clear();
            var ordered = local
                .Select((model, index) => (Model: model, Index: index))
                .OrderBy(entry => entry.Model.State == ModelState.Cached ? 0 : entry.Model.InDefaultSet ? 1 : 2)
                .ThenBy(entry => entry.Model.State == ModelState.Cached ? 0 : entry.Model.SizeBytes)
                .ThenBy(entry => entry.Index);
            foreach (var (model, _) in ordered)
                Items.Add(new DownloadChoiceViewModel(model, Recount));
        }
        else
        {
            foreach (var item in Items)
                item.Update(local.First(model => model.Name == item.Name));
        }

        HasPending = Items.Any(item => item.IsEnabled);
        Recount();
    }

    private void Recount()
    {
        var bytes = SelectedBytes;
        var text = bytes > 0 ? $"**{ModelText.Size(bytes)}** · {ModelText.Estimate(bytes, DemoModelService.Rate)}" : "nothing to download";
        if (text == SelectedText && SelectedRuns.Count > 0)
            return;
        SelectedText = text;
        SelectedRuns = Spans.Parse(text);
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectedRuns));
        DownloadCommand.NotifyCanExecuteChanged();
    }

    private async Task DownloadAsync(CancellationToken cancellationToken)
    {
        var picked = Items.Where(item => item.IsSelected && item.IsEnabled).Select(item => item.Name).ToList();
        if (picked.Count == 0)
            return;
        await _service.DownloadAsync(picked, cancellationToken).ConfigureAwait(true);
        Refresh();
        Started?.Invoke(this, EventArgs.Empty);
    }
}
