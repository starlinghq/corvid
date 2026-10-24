using System.Collections.ObjectModel;
using System.Globalization;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Secrets;

/// <summary>
/// The Secrets area (docs/plan/ui.md §4.11, F17): Vault's status, each secret's name and policy (hosts, methods, toolsets,
/// role, budget), which features wait on which name with the exact <c>corvid-vault set</c> to run, and binding a feature
/// to a name Vault already has. The app never holds or shows a secret value: no service call takes or returns one. Every
/// failure shows as a red notice.
/// </summary>
public sealed class SecretsViewModel : AreaViewModel, IDisposable
{
    /// <summary>The most secret rows drawn at once.</summary>
    public const int MaxSecretRows = 200;

    /// <summary>The most waiting features, and the most rows of "who uses what", drawn at once.</summary>
    public const int MaxFeatureRows = 100;

    /// <summary>The most names the bind picker lists, those that can serve the feature first.</summary>
    public const int MaxBindChoices = 100;

    private readonly ISecretsService _service;
    private readonly TimeProvider _time;
    private readonly ObservableCollection<SecretRowView> _secrets = [];
    private readonly ObservableCollection<SecretNeedView> _needs = [];
    private readonly ObservableCollection<BindChoiceView> _bindChoices = [];
    private SecretsSnapshot? _snapshot;
    private IReadOnlyList<TextRun> _notice = [];
    private Tone _noticeTone = Tone.Green;
    private string _setName = "";
    private SecretNeedView? _binding;
    private bool _isSetOpen;
    private SetterLaunch? _launch;
    private Task<bool>? _reload;
    private string _hiddenBindChoices = "";
    private bool _reloadAgain;
    private bool _disposed;

    public SecretsViewModel(ISecretsService service, TimeProvider time)
        : base(Area.Secrets, "What Vault will say: each secret's name, hosts and policy, and the features waiting on one. Never a value.")
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(time);
        _service = service;
        _time = time;
        Secrets = new ReadOnlyObservableCollection<SecretRowView>(_secrets);
        Needs = new ReadOnlyObservableCollection<SecretNeedView>(_needs);
        BindChoices = new ReadOnlyObservableCollection<BindChoiceView>(_bindChoices);
        RefreshCommand = new AsyncRelayCommand(CheckAgainAsync);
        OpenSetCommand = new RelayCommand(() => OpenSet(""));
        StartSetCommand = new AsyncRelayCommand(ct => StartSetAsync(_setName, ct), () => SetNameIsValid);
        ClosePickerCommand = new RelayCommand(ClosePicker);
        DismissNoticeCommand = new RelayCommand(() => SetNotice([], Tone.Green));
        _service.Changed += OnServiceChanged;
        Loaded = ReloadAsync(CancellationToken.None);
    }

    /// <summary>The first load; tests await it.</summary>
    public Task Loaded { get; }

    public IReadOnlyList<TextRun> Subtitle { get; } =
        Spans.Parse("names and policy from Vault · values never reach Corvid · set with `corvid-vault set`");

    // ---- Vault ----

    /// <summary>"running", "sealed", "absent".</summary>
    public string VaultTag { get; private set; } = "";

    public Tone VaultTone { get; private set; } = Tone.Grey;

    public IReadOnlyList<ModelFact> VaultFacts { get; private set; } = [];

    /// <summary>"63 uses today · 2 asked · 0 confirmed out of band · 1 refused".</summary>
    public IReadOnlyList<ModelFact> AuditFacts { get; private set; } = [];

    public bool IsVaultAbsent => _snapshot?.Vault.State == VaultState.Absent;

    // ---- secrets and needs ----

    public ReadOnlyObservableCollection<SecretRowView> Secrets { get; }

    /// <summary>"7 secrets · 2 features waiting".</summary>
    public string SecretsNote { get; private set; } = "";

    public bool HasNoSecrets => _secrets.Count == 0;

    public ReadOnlyObservableCollection<SecretNeedView> Needs { get; }

    public bool HasNeeds => _needs.Count > 0;

    public bool HasNoNeeds => _needs.Count == 0;

    /// <summary>"2 features" / "nothing waits".</summary>
    public string NeedsNote { get; private set; } = "";

    /// <summary>"and 12 more features waiting, not drawn", when the cap hides some.</summary>
    public string HiddenNeeds { get; private set; } = "";

    public bool HasHiddenNeeds => HiddenNeeds.Length > 0;

    /// <summary>Every configured feature with the name it uses and whether it can, at most <see cref="MaxFeatureRows"/>.</summary>
    public IReadOnlyList<ConsumerRowView> Consumers { get; private set; } = [];

    /// <summary>"and 30 more features, not drawn", when the cap hides some.</summary>
    public string HiddenConsumers { get; private set; } = "";

    public bool HasHiddenConsumers => HiddenConsumers.Length > 0;

    /// <summary>Asks Vault again (Check again): a secret set in a terminal shows, and what waited on it is ready.</summary>
    public AsyncRelayCommand RefreshCommand { get; }

    // ---- setting a secret (outside the app) ----

    /// <summary>"Set a secret…": the picker with a name field and the command.</summary>
    public RelayCommand OpenSetCommand { get; }

    public bool IsSetOpen
    {
        get => _isSetOpen;
        private set
        {
            if (SetProperty(ref _isSetOpen, value))
                OnPropertyChanged(nameof(IsPickerOpen));
        }
    }

    /// <summary>The name typed in the set picker.</summary>
    public string SetName
    {
        get => _setName;
        set
        {
            if (!SetProperty(ref _setName, value ?? ""))
                return;
            Launch = null;
            OnPropertyChanged(nameof(SetNameIsValid));
            OnPropertyChanged(nameof(SetNameProblem));
            OnPropertyChanged(nameof(HasSetNameProblem));
            OnPropertyChanged(nameof(SetPreview));
            StartSetCommand.NotifyCanExecuteChanged();
        }
    }

    public bool SetNameIsValid => SecretNames.IsValid(_setName);

    /// <summary>Why the typed name cannot be set; empty while nothing is typed or it is valid.</summary>
    public string SetNameProblem => _setName.Length == 0 || SetNameIsValid
        ? ""
        : SafeText.HasHidden(_setName)
            ? $"\"{SafeText.Reveal(_setName)}\" holds hidden characters. {SecretNames.Rule}"
            : SecretNames.Rule;

    public bool HasSetNameProblem => SetNameProblem.Length > 0;

    /// <summary>The command the picker would start, as it would run.</summary>
    public string SetPreview => SecretNames.SetCommand(_setName.Length == 0 ? "NAME" : _setName);

    /// <summary>Open a terminal (the Vault helper comes with U-6).</summary>
    public AsyncRelayCommand StartSetCommand { get; }

    /// <summary>What the last start did: the command to run and whether anything was opened.</summary>
    public SetterLaunch? Launch
    {
        get => _launch;
        private set
        {
            if (SetProperty(ref _launch, value))
            {
                OnPropertyChanged(nameof(HasLaunch));
                OnPropertyChanged(nameof(LaunchCommand));
                OnPropertyChanged(nameof(LaunchNote));
            }
        }
    }

    public bool HasLaunch => _launch is not null;

    public string LaunchCommand => _launch?.Command ?? "";

    public string LaunchNote => _launch?.Note ?? "";

    // ---- binding a name ----

    /// <summary>The feature the bind picker binds, while it is open.</summary>
    public SecretNeedView? Binding
    {
        get => _binding;
        private set
        {
            if (!SetProperty(ref _binding, value))
                return;
            OnPropertyChanged(nameof(IsBindOpen));
            OnPropertyChanged(nameof(IsPickerOpen));
            OnPropertyChanged(nameof(BindTitle));
        }
    }

    public bool IsBindOpen => _binding is not null;

    /// <summary>"Bind model gemini to a secret".</summary>
    public string BindTitle => _binding is { } need ? $"Bind {need.Feature} to a secret" : "";

    public ReadOnlyObservableCollection<BindChoiceView> BindChoices { get; }

    /// <summary>"and 40 more names, not listed", when the picker's cap hides some.</summary>
    public string HiddenBindChoices
    {
        get => _hiddenBindChoices;
        private set
        {
            if (SetProperty(ref _hiddenBindChoices, value))
                OnPropertyChanged(nameof(HasHiddenBindChoices));
        }
    }

    public bool HasHiddenBindChoices => _hiddenBindChoices.Length > 0;

    public bool IsPickerOpen => IsSetOpen || IsBindOpen;

    /// <summary>Esc or Cancel: closes whichever picker is open.</summary>
    public RelayCommand ClosePickerCommand { get; }

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

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _service.Changed -= OnServiceChanged;
    }

    /// <summary>Opens the set picker with a name filled in (a waiting feature's), or empty.</summary>
    public void OpenSet(string name)
    {
        Binding = null;
        _bindChoices.Clear();
        HiddenBindChoices = "";
        SetName = name;
        Launch = null;
        IsSetOpen = true;
    }

    private void ClosePicker()
    {
        IsSetOpen = false;
        Binding = null;
        _bindChoices.Clear();
        HiddenBindChoices = "";
    }

    // ---- loading ----

    private async Task CheckAgainAsync(CancellationToken cancellationToken)
    {
        var before = _snapshot?.Secrets.Select(s => s.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        // A read already running is waited for (and runs once more), so the comparison is with what Vault says now. A read
        // that failed leaves its red notice: there is nothing new to compare.
        if (!await ReloadAsync(cancellationToken).ConfigureAwait(true) || _snapshot is not { } snapshot)
            return;
        var appeared = snapshot.Secrets.Where(s => !before.Contains(s.Name)).ToList();
        if (appeared.Count == 0)
        {
            SetNotice(Spans.Parse("Vault answers as before: no new secret."), Tone.Grey);
            return;
        }

        // The set the picker started is done: it closes, and the notice says what is ready now, for every new name.
        if (IsSetOpen && appeared.Any(secret => secret.Name == _setName))
            ClosePicker();
        var lines = new List<string>();
        foreach (var secret in appeared.Take(5))
        {
            var served = snapshot.Consumers.Where(c => c.Secret == secret.Name && Need(snapshot, c).State == NeedState.Ready)
                .Select(c => $"{SecretsText.Kind(c.Kind)} {Spans.Escape(SafeText.Clean(c.Name))}").ToList();
            var name = Spans.Escape(SafeText.Clean(secret.Name));
            lines.Add(served.Count == 0
                ? $"`{name}` is in Vault now."
                : $"`{name}` is in Vault now: {string.Join(" and ", served)} can use it.");
        }

        if (appeared.Count > 5)
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"And {appeared.Count - 5} more names."));
        lines.Add(appeared.Count == 1 ? "Corvid never saw its value." : "Corvid never saw their values.");
        SetNotice(Spans.Parse(string.Join(" ", lines)), Tone.Green);
    }

    /// <summary>
    /// Loads the snapshot and rebuilds each list once; true when it applied a new snapshot, false when the read failed
    /// (its red notice shows). A change heard while a load runs loads again after it, a failure included, and a caller
    /// that asks meanwhile waits for that same read.
    /// </summary>
    private Task<bool> ReloadAsync(CancellationToken cancellationToken)
    {
        if (_reload is { IsCompleted: false } running)
        {
            _reloadAgain = true;
            return running;
        }

        _reload = LoadLoopAsync(cancellationToken);
        return _reload;
    }

    private async Task<bool> LoadLoopAsync(CancellationToken cancellationToken)
    {
        var applied = false;
        do
        {
            _reloadAgain = false;
            SecretsSnapshot snapshot;
            try
            {
                snapshot = await _service.LoadAsync(cancellationToken).ConfigureAwait(true);
            }
            catch (Exception exception) when (Failed(exception))
            {
                Fail("Vault could not be read", exception);
                applied = false;
                // A change heard during the failed read still gets its read.
                continue;
            }

            if (_disposed)
                return false;
            Apply(snapshot);
            applied = true;
        }
        while (_reloadAgain && !_disposed);

        return applied;
    }

    private void Apply(SecretsSnapshot snapshot)
    {
        _snapshot = snapshot;
        var vault = snapshot.Vault;
        (VaultTag, VaultTone) = vault.State switch
        {
            VaultState.Running => ("running", Tone.Green),
            VaultState.Sealed => ("sealed", Tone.Amber),
            _ => ("absent", Tone.Red),
        };
        VaultFacts = vault.State == VaultState.Absent
            ? [new ModelFact("state", Spans.Parse("no answer on the socket: cloud slots are hidden and every feature here waits. `corvid-vault serve` starts it."))]
            :
            [
                new ModelFact("version", Spans.Parse(Spans.Escape(SafeText.Clean(vault.Version)))),
                new ModelFact("socket", Spans.Parse($"`{Spans.Escape(SafeText.Clean(vault.Socket))}`")),
                new ModelFact("state", Spans.Parse(vault.State == VaultState.Sealed ? "sealed: nothing is served until it is unsealed" : "unsealed: secrets are served by name")),
            ];
        AuditFacts =
        [
            vault.AuditSummary is { } summary
                ? new ModelFact("audit", Spans.Parse(Spans.Escape(SafeText.Clean(summary))))
                : new ModelFact("today", Spans.Parse(string.Create(CultureInfo.InvariantCulture,
                    $"{vault.UsesToday} uses · {vault.AskedToday} asked · {vault.ConfirmedToday} confirmed out of band · {vault.RefusedToday} refused"))),
            new ModelFact("confirm", Spans.Parse(vault.PendingConfirm is { Length: > 0 } pending
                ? $"Vault is asking you now: {Spans.Escape(SafeText.Clean(pending))}"
                : "none pending · Vault shows its own dialog; Corvid cannot answer it")),
        ];

        _secrets.Clear();
        foreach (var secret in snapshot.Secrets.OrderBy(s => s.Name, StringComparer.Ordinal).Take(MaxSecretRows))
            _secrets.Add(new SecretRowView(secret, [.. snapshot.Consumers.Where(c => c.Secret == secret.Name)], _time));

        _needs.Clear();
        var consumers = new List<ConsumerRowView>();
        var waiting = 0;
        foreach (var consumer in snapshot.Consumers)
        {
            var (state, refusal) = Need(snapshot, consumer);
            if (consumers.Count < MaxFeatureRows)
                consumers.Add(new ConsumerRowView(consumer, state));
            if (state == NeedState.Ready)
                continue;
            waiting++;
            if (_needs.Count < MaxFeatureRows)
                _needs.Add(new SecretNeedView(consumer, state, refusal, StartSetForAsync, OpenBind));
        }

        Consumers = consumers;
        HiddenConsumers = snapshot.Consumers.Length > MaxFeatureRows
            ? string.Create(CultureInfo.InvariantCulture, $"and {snapshot.Consumers.Length - MaxFeatureRows} more features, not drawn")
            : "";
        HiddenNeeds = waiting > MaxFeatureRows
            ? string.Create(CultureInfo.InvariantCulture, $"and {waiting - MaxFeatureRows} more features waiting, not drawn: set or bind these first")
            : "";
        SecretsNote = string.Create(CultureInfo.InvariantCulture,
            $"{snapshot.Secrets.Length} {(snapshot.Secrets.Length == 1 ? "secret" : "secrets")}{(snapshot.Secrets.Length > MaxSecretRows ? $", first {MaxSecretRows} shown" : "")} · names only");
        NeedsNote = waiting switch
        {
            0 => "nothing waits",
            1 => "1 feature",
            var n => string.Create(CultureInfo.InvariantCulture, $"{n} features"),
        };

        // A bind picker open over a feature that no longer waits closes; one still open is rebuilt from the new catalogue.
        if (_binding is { } binding)
        {
            if (_needs.FirstOrDefault(n => n.Consumer.Id == binding.Consumer.Id) is { } fresh)
                OpenBind(fresh);
            else
                ClosePicker();
        }

        OnPropertyChanged(nameof(VaultTag));
        OnPropertyChanged(nameof(VaultTone));
        OnPropertyChanged(nameof(VaultFacts));
        OnPropertyChanged(nameof(AuditFacts));
        OnPropertyChanged(nameof(IsVaultAbsent));
        OnPropertyChanged(nameof(SecretsNote));
        OnPropertyChanged(nameof(HasNoSecrets));
        OnPropertyChanged(nameof(NeedsNote));
        OnPropertyChanged(nameof(HasNeeds));
        OnPropertyChanged(nameof(HasNoNeeds));
        OnPropertyChanged(nameof(Consumers));
        OnPropertyChanged(nameof(HiddenConsumers));
        OnPropertyChanged(nameof(HasHiddenConsumers));
        OnPropertyChanged(nameof(HiddenNeeds));
        OnPropertyChanged(nameof(HasHiddenNeeds));
    }

    private static (NeedState State, string? Refusal) Need(SecretsSnapshot snapshot, SecretConsumer consumer)
    {
        if (snapshot.Vault.State != VaultState.Running)
            return (NeedState.Unusable, snapshot.Vault.State == VaultState.Sealed ? "Vault is sealed" : "Vault does not answer");
        if (snapshot.Secrets.FirstOrDefault(s => s.Name == consumer.Secret) is not { } secret)
            return (NeedState.Missing, null);
        return SecretBinding.Refusal(secret, consumer) is { } refusal ? (NeedState.Unusable, refusal) : (NeedState.Ready, null);
    }

    // ---- actions ----

    private Task StartSetForAsync(SecretNeedView need, CancellationToken cancellationToken)
    {
        OpenSet(need.Consumer.Secret);
        return StartSetAsync(need.Consumer.Secret, cancellationToken);
    }

    private async Task StartSetAsync(string name, CancellationToken cancellationToken)
    {
        if (!SecretNames.IsValid(name))
        {
            SetNotice(Spans.Parse(Spans.Escape(SecretNames.Rule)), Tone.Red);
            return;
        }

        try
        {
            Launch = await _service.StartSetAsync(name, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail("Setting the secret could not start", exception);
        }
    }

    private void OpenBind(SecretNeedView need)
    {
        IsSetOpen = false;
        _bindChoices.Clear();
        HiddenBindChoices = "";
        if (_snapshot is { } snapshot)
        {
            foreach (var secret in snapshot.Secrets.OrderBy(s => SecretBinding.Refusal(s, need.Consumer) is null ? 0 : 1).ThenBy(s => s.Name, StringComparer.Ordinal)
                         .Take(MaxBindChoices))
                _bindChoices.Add(new BindChoiceView(secret, need.Consumer, BindAsync));
            if (snapshot.Secrets.Length > MaxBindChoices)
                HiddenBindChoices = string.Create(CultureInfo.InvariantCulture, $"and {snapshot.Secrets.Length - MaxBindChoices} more names, not listed: those that can serve it come first");
        }

        Binding = need;
        OnPropertyChanged(nameof(BindTitle));
    }

    private async Task BindAsync(BindChoiceView choice, CancellationToken cancellationToken)
    {
        if (_binding is not { } need)
            return;
        // Text that will be stored must not be bindable while it hides something.
        if (!choice.CanChoose || SafeText.HasHidden(choice.Secret.Name))
            return;
        try
        {
            await _service.BindAsync(need.Consumer.Id, choice.Secret.Name, cancellationToken).ConfigureAwait(true);
        }
        catch (Exception exception) when (Failed(exception))
        {
            Fail("The binding was refused", exception);
            return;
        }

        ClosePicker();
        SetNotice(Spans.Parse(
            $"{Spans.Escape(need.Feature)} now uses `{Spans.Escape(choice.Name)}` (written to corvid.json). Corvid never saw its value."), Tone.Green);
        await ReloadAsync(cancellationToken).ConfigureAwait(true);
    }

    private void OnServiceChanged(object? sender, EventArgs e) => _ = ReloadAsync(CancellationToken.None);

    private void SetNotice(IReadOnlyList<TextRun> text, Tone tone)
    {
        NoticeTone = tone;
        Notice = text;
        OnPropertyChanged(nameof(HasNotice));
    }

    private void Fail(string what, Exception exception) =>
        SetNotice(Spans.Parse($"{Spans.Escape(what)}: {Spans.Escape(SafeText.Clean(exception.Message))}"), Tone.Red);

    private static bool Failed(Exception exception) => exception is not OperationCanceledException and not OutOfMemoryException;
}
