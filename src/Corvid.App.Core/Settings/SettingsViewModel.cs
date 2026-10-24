using System.ComponentModel;
using Corvid.App.Core.Keys;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Settings;

/// <summary>
/// The Settings area (docs/plan/ui.md F24, §4.15): a typed page over <c>corvid.json</c> with the validator's messages
/// under each setting and what needs a restart, the raw file in an editor checked the same way, the keybindings with
/// conflict detection, and the app's own appearance, notification and layout preferences. Values given with
/// <c>--set</c> show as "set for this run". The theme applies at once (focus colours after a restart, a limit of Uno).
/// <para>
/// Nothing is saved while the validator has a message, and a value holding hidden characters is a message: it is shown
/// with them made visible and must be typed again. In the demo every save changes an in-memory copy, and the page says
/// so. A failed save or load shows in red; nothing is swallowed.
/// </para>
/// </summary>
public sealed class SettingsViewModel : AreaViewModel
{
    private static readonly AppTheme[] ThemeOrder = [AppTheme.Light, AppTheme.Dark, AppTheme.System];

    private readonly ShellViewModel _shell;
    private readonly ISettingsService _service;
    private readonly KeyMap _keys;
    private readonly List<SettingFieldViewModel> _fields = [];
    private IReadOnlyList<SettingGroupViewModel> _groups = [];
    private IReadOnlyList<SettingFieldViewModel> _appearance = [];
    private IReadOnlyList<SettingFieldViewModel> _notifications = [];
    private IReadOnlyList<SettingFieldViewModel> _layout = [];
    private IReadOnlyList<string> _fileProblems = [];
    private IReadOnlyList<string> _jsonProblems = [];
    private string _section = SettingsCatalogue.Configuration;
    private string _path = "";
    private string _json = "";
    private string _savedJson = "";
    private string _notice = "";
    private Tone _noticeTone = Tone.Green;
    private bool _isDemo;
    private bool _loaded;
    private bool _jsonValid = true;
    private Task? _loading;

    public SettingsViewModel(ShellViewModel shell, ISettingsService service, KeyMap keys)
        : base(Area.Settings, "Configuration over corvid.json with the validator's messages, keybindings, themes, accessibility and notifications.")
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(keys);
        _shell = shell;
        _service = service;
        _keys = keys;
        Themes = [.. ThemeOrder.Select(theme => new Choice(theme.ToString().ToLowerInvariant(), Describe(theme),
            new RelayCommand(() => _shell.Theme = theme)))];
        Sections = [.. SettingsCatalogue.Sections.Select(section => new Choice(section, $"Settings: {section}", new RelayCommand(() => SelectSection(section))))];
        KeyGroups = [.. KeyMap.Contexts
            .Select(context => new KeyGroupViewModel(context.Heading, [.. keys.Bindings.Where(binding => binding.Context == context.Context)
                .Select(binding => new KeyBindingRowViewModel(keys, binding.Id))]))
            .Where(group => group.Rows.Count > 0)];
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => CanSave);
        RevertCommand = new RelayCommand(Revert, () => ChangeCount > 0);
        ApplyJsonCommand = new AsyncRelayCommand(ApplyJsonAsync, () => CanApplyJson);
        RevertJsonCommand = new RelayCommand(() => JsonText = _savedJson, () => IsJsonChanged);
        ResetKeysCommand = new RelayCommand(_keys.ResetAll, () => _keys.IsChanged);
        DismissNoticeCommand = new RelayCommand(() => Notice = "");
        _shell.PropertyChanged += OnShellChanged;
        _keys.Changed += OnKeysChanged;
        UpdateThemes();
        UpdateSections();
    }

    // ---- Tabs ----

    /// <summary>The page's tabs: Configuration, corvid.json, Keybindings, Appearance, Notifications, Layout.</summary>
    public IReadOnlyList<Choice> Sections { get; }

    public string Section => _section;

    public bool IsConfiguration => _section == SettingsCatalogue.Configuration;

    public bool IsRaw => _section == "corvid.json";

    public bool IsKeys => _section == "Keybindings";

    public bool IsAppearance => _section == SettingsCatalogue.Appearance;

    public bool IsNotifications => _section == SettingsCatalogue.Notifications;

    public bool IsLayout => _section == SettingsCatalogue.Layout;

    /// <summary>The save bar shows on the tabs whose fields save together.</summary>
    public bool ShowSaveBar => IsConfiguration || IsAppearance || IsNotifications || IsLayout;

    public void SelectSection(string section)
    {
        if (!SettingsCatalogue.Sections.Contains(section) || section == _section)
            return;
        _section = section;
        UpdateSections();
        OnPropertyChanged(nameof(Section));
        OnPropertyChanged(nameof(IsConfiguration));
        OnPropertyChanged(nameof(IsRaw));
        OnPropertyChanged(nameof(IsKeys));
        OnPropertyChanged(nameof(IsAppearance));
        OnPropertyChanged(nameof(IsNotifications));
        OnPropertyChanged(nameof(IsLayout));
        OnPropertyChanged(nameof(ShowSaveBar));
    }

    // ---- The file ----

    public bool IsLoaded => _loaded;

    /// <summary>Where the configuration lives ("~/.config/corvid/corvid.json").</summary>
    public string Path => _path;

    public bool IsDemo => _isDemo;

    public string DemoText => "Demo: changes go to an in-memory copy of corvid.json and are kept for this run. Nothing is written to disk.";

    /// <summary>The head's line: the file, and how many values the command line set for this run.</summary>
    public string Subtitle
    {
        get
        {
            var overrides = _fields.Count(item => item.HasRunOverride);
            return overrides == 0 ? _path : $"{_path} · {overrides} set for this run with --set";
        }
    }

    /// <summary>The <c>corvid.json</c> groups: Workspace and data, Approvals, Turns, Privacy, Devices and tools, Serve and telemetry.</summary>
    public IReadOnlyList<SettingGroupViewModel> Groups { get => _groups; private set => SetProperty(ref _groups, value); }

    public IReadOnlyList<SettingFieldViewModel> AppearanceFields { get => _appearance; private set => SetProperty(ref _appearance, value); }

    public IReadOnlyList<SettingFieldViewModel> NotificationFields { get => _notifications; private set => SetProperty(ref _notifications, value); }

    public IReadOnlyList<SettingFieldViewModel> LayoutFields { get => _layout; private set => SetProperty(ref _layout, value); }

    /// <summary>The validator's messages that name no setting on the page (the whole file).</summary>
    public IReadOnlyList<string> FileProblems { get => _fileProblems; private set { if (SetProperty(ref _fileProblems, value)) OnPropertyChanged(nameof(HasFileProblems)); } }

    public bool HasFileProblems => _fileProblems.Count > 0;

    public int ChangeCount => _fields.Count(item => item.IsChanged);

    public int ProblemCount => _fields.Count(item => item.HasProblem) + _fileProblems.Count;

    /// <summary>"2 changes · 1 needs a restart", "1 problem to fix", or "No changes".</summary>
    public string ChangesText
    {
        get
        {
            var changed = _fields.Where(item => item.IsChanged).ToList();
            if (ProblemCount > 0)
                return ProblemCount == 1 ? "1 problem to fix before saving" : $"{ProblemCount} problems to fix before saving";
            if (changed.Count == 0)
                return "No changes";
            var restart = changed.Count(item => item.Definition.Applies == SettingApplies.Restart);
            var text = changed.Count == 1 ? "1 change" : $"{changed.Count} changes";
            return restart == 0 ? text : $"{text} · {restart} {(restart == 1 ? "needs" : "need")} a restart";
        }
    }

    public Tone ChangesTone => ProblemCount > 0 ? Tone.Red : Tone.Grey;

    public bool CanSave => _loaded && ChangeCount > 0 && ProblemCount == 0 && !SaveCommand.IsRunning;

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand RevertCommand { get; }

    // ---- The raw editor ----

    /// <summary>The raw <c>corvid.json</c> (two-way); every change is parsed and validated.</summary>
    public string JsonText
    {
        get => _json;
        set
        {
            if (!SetProperty(ref _json, value ?? ""))
                return;
            CheckJson();
        }
    }

    public IReadOnlyList<string> JsonProblems { get => _jsonProblems; private set { if (SetProperty(ref _jsonProblems, value)) OnPropertyChanged(nameof(HasJsonProblems)); } }

    public bool HasJsonProblems => _jsonProblems.Count > 0;

    public bool IsJsonChanged => !string.Equals(DemoSettingsService.Lines(_json), DemoSettingsService.Lines(_savedJson), StringComparison.Ordinal);

    public string JsonStatus => !_jsonValid
        ? _jsonProblems.Count == 1 ? "1 problem" : $"{_jsonProblems.Count} problems"
        : IsJsonChanged ? "Valid · not saved" : "Valid · saved";

    public Tone JsonTone => _jsonValid ? IsJsonChanged ? Tone.Blue : Tone.Green : Tone.Red;

    public bool CanApplyJson => _loaded && _jsonValid && IsJsonChanged && !ApplyJsonCommand.IsRunning;

    public AsyncRelayCommand ApplyJsonCommand { get; }

    public RelayCommand RevertJsonCommand { get; }

    // ---- Keybindings ----

    public IReadOnlyList<KeyGroupViewModel> KeyGroups { get; }

    /// <summary>The remappings as <c>keybindings.json</c> would hold them.</summary>
    public string KeysJson => _keys.ToJson();

    public string KeysNote => _isDemo
        ? "The window's own keys change at once, for this run: the demo writes no keybindings.json. A page's keys are listed as the page binds them."
        : "The window's own keys change at once and are written to keybindings.json beside corvid.json. A page's keys are listed as the page binds them.";

    public RelayCommand ResetKeysCommand { get; }

    // ---- Appearance ----

    /// <summary>Light, dark, system: the segmented control on the page, the one in force selected.</summary>
    public IReadOnlyList<Choice> Themes { get; }

    public AppTheme Theme => _shell.Theme;

    // ---- Notices ----

    /// <summary>What the last save did, or why it failed.</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value ?? ""))
                OnPropertyChanged(nameof(HasNotice));
        }
    }

    public Tone NoticeTone { get => _noticeTone; private set => SetProperty(ref _noticeTone, value); }

    public bool HasNotice => _notice.Length > 0;

    public RelayCommand DismissNoticeCommand { get; }

    /// <summary>Loads once; a page shown again keeps what was edited.</summary>
    public Task EnsureLoadedAsync(CancellationToken cancellationToken = default) => _loading ??= LoadAsync(cancellationToken);

    /// <summary>Reads the configuration and rebuilds the page's fields from it.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshot = await _service.LoadAsync(cancellationToken);
            Build(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _loading = null;
        }
        catch (Exception exception)
        {
            _loading = null;
            Show($"Could not read the configuration: {SafeText.Clean(exception.Message)}", Tone.Red);
        }
    }

    private void Build(SettingsSnapshot snapshot)
    {
        _fields.Clear();
        _isDemo = snapshot.IsDemo;
        _path = SafeText.Clean(snapshot.Path);
        foreach (var definition in _service.Definitions)
        {
            var value = snapshot.Values.TryGetValue(definition.Key, out var found) ? found : definition.Default;
            snapshot.RunOverrides.TryGetValue(definition.Key, out var runOverride);
            _fields.Add(new SettingFieldViewModel(definition, value, runOverride, OnFieldChanged));
        }

        SettingGroupViewModel[] groups = [.. _fields.Where(item => item.Definition.Store == SettingStore.CorvidJson)
            .GroupBy(item => item.Definition.Section)
            .Select(group => new SettingGroupViewModel(group.Key, [.. group]))];
        Groups = groups;
        AppearanceFields = [.. _fields.Where(item => item.Definition.Section == SettingsCatalogue.Appearance)];
        NotificationFields = [.. _fields.Where(item => item.Definition.Section == SettingsCatalogue.Notifications)];
        LayoutFields = [.. _fields.Where(item => item.Definition.Section == SettingsCatalogue.Layout)];
        _savedJson = snapshot.Json;
        _json = snapshot.Json;
        _loaded = true;
        OnPropertyChanged(nameof(JsonText));
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(Path));
        OnPropertyChanged(nameof(IsDemo));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(KeysNote));
        CheckJson();
        Revalidate();
    }

    private void OnFieldChanged(SettingFieldViewModel changed) => Revalidate();

    /// <summary>Runs the validator over every value once and puts each message under its setting.</summary>
    private void Revalidate()
    {
        var values = _fields.ToDictionary(item => item.Definition.Key, item => item.Value, StringComparer.Ordinal);
        var problems = _service.Validate(values);
        var byKey = problems.Where(problem => problem.Key is not null)
            .GroupBy(problem => problem.Key!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => string.Join(" ", group.Select(problem => problem.Message).Distinct()), StringComparer.Ordinal);
        foreach (var item in _fields)
            item.Problem = byKey.GetValueOrDefault(item.Definition.Key, "");
        var shown = _fields.Select(item => item.Definition.Key).ToHashSet(StringComparer.Ordinal);
        FileProblems = [.. problems.Where(problem => problem.Key is null || !shown.Contains(problem.Key)).Select(problem => problem.Message)];
        RaiseChanges();
    }

    private void RaiseChanges()
    {
        OnPropertyChanged(nameof(ChangeCount));
        OnPropertyChanged(nameof(ProblemCount));
        OnPropertyChanged(nameof(ChangesText));
        OnPropertyChanged(nameof(ChangesTone));
        OnPropertyChanged(nameof(CanSave));
        SaveCommand.NotifyCanExecuteChanged();
        RevertCommand.NotifyCanExecuteChanged();
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        var changed = _fields.Where(item => item.IsChanged).ToList();
        try
        {
            var write = await _service.SaveAsync(changed.ToDictionary(item => item.Definition.Key, item => item.Value, StringComparer.Ordinal), cancellationToken);
            if (!write.Written)
            {
                Show($"{write.Message} {string.Join(" ", write.Problems.Select(problem => problem.Message))}".Trim(), Tone.Red);
                return;
            }

            foreach (var item in changed)
                item.MarkSaved(write.RestartKeys.Contains(item.Definition.Key));
            var snapshot = await _service.LoadAsync(cancellationToken);
            _savedJson = snapshot.Json;
            _json = snapshot.Json;
            OnPropertyChanged(nameof(JsonText));
            CheckJson();
            Show(write.Message, Tone.Green);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Show($"Not saved: {SafeText.Clean(exception.Message)}", Tone.Red);
        }
        finally
        {
            RaiseChanges();
        }
    }

    private void Revert()
    {
        foreach (var item in _fields.Where(item => item.IsChanged))
            item.Reset(item.Saved);
        Revalidate();
        Show("Changes put back.", Tone.Grey);
    }

    private void CheckJson()
    {
        var parse = _service.Parse(_json);
        _jsonValid = parse.IsValid;
        JsonProblems = [.. parse.Problems.Select(problem => problem.Message)];
        OnPropertyChanged(nameof(IsJsonChanged));
        OnPropertyChanged(nameof(JsonStatus));
        OnPropertyChanged(nameof(JsonTone));
        OnPropertyChanged(nameof(CanApplyJson));
        ApplyJsonCommand.NotifyCanExecuteChanged();
        RevertJsonCommand.NotifyCanExecuteChanged();
    }

    private async Task ApplyJsonAsync(CancellationToken cancellationToken)
    {
        try
        {
            var write = await _service.SaveJsonAsync(_json, cancellationToken);
            if (!write.Written)
            {
                Show(write.Message, Tone.Red);
                return;
            }

            var snapshot = await _service.LoadAsync(cancellationToken);
            var edits = _fields.Where(item => item.IsChanged).ToDictionary(item => item.Definition.Key, item => item.Value, StringComparer.Ordinal);
            _savedJson = snapshot.Json;
            _json = snapshot.Json;
            OnPropertyChanged(nameof(JsonText));
            foreach (var item in _fields)
            {
                if (snapshot.Values.TryGetValue(item.Definition.Key, out var value) && item.Definition.Store == SettingStore.CorvidJson)
                {
                    item.Reset(value);
                    item.MarkSaved(write.RestartKeys.Contains(item.Definition.Key));
                }
            }

            CheckJson();
            Revalidate();
            Show(edits.Count > 0 ? $"{write.Message} The typed page now shows the file's values." : write.Message, Tone.Green);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Show($"Not saved: {SafeText.Clean(exception.Message)}", Tone.Red);
        }
        finally
        {
            OnPropertyChanged(nameof(CanApplyJson));
            ApplyJsonCommand.NotifyCanExecuteChanged();
        }
    }

    private void Show(string notice, Tone tone)
    {
        NoticeTone = tone;
        Notice = notice;
    }

    private static string Describe(AppTheme theme) => theme switch
    {
        AppTheme.Light => "Light: dark text on white.",
        AppTheme.Dark => "Dark: light text on near-black.",
        _ => "System: follows the platform's light or dark setting.",
    };

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.Theme))
            return;
        OnPropertyChanged(nameof(Theme));
        UpdateThemes();
    }

    private void OnKeysChanged(object? sender, EventArgs e)
    {
        foreach (var row in KeyGroups.SelectMany(group => group.Rows))
            row.Refresh();
        OnPropertyChanged(nameof(KeysJson));
        ResetKeysCommand.NotifyCanExecuteChanged();
    }

    private void UpdateThemes()
    {
        foreach (var (choice, theme) in Themes.Zip(ThemeOrder))
            choice.IsSelected = theme == _shell.Theme;
    }

    private void UpdateSections()
    {
        foreach (var choice in Sections)
            choice.IsSelected = choice.Label == _section;
    }
}
