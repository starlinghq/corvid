using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Settings;

/// <summary>
/// One typed setting on the page: its label, key and sentence, the editor its kind takes (a field, a segmented choice or
/// a switch), the value as edited against the value saved, the validator's message under it, when it applies, and the
/// value given with <c>--set</c> for this run when there is one.
/// </summary>
public sealed class SettingFieldViewModel : ObservableObject
{
    private readonly Action<SettingFieldViewModel> _changed;
    private string _value;
    private string _saved;
    private string _problem = "";
    private bool _restartPending;

    public SettingFieldViewModel(SettingDefinition definition, string value, string? runOverride, Action<SettingFieldViewModel> changed)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(changed);
        Definition = definition;
        _changed = changed;
        _value = value;
        _saved = value;
        RunOverride = runOverride is null ? "" : SafeText.Clean(runOverride);
        // Segments read in lower case, as the status bar's mode and Safety's segment do; the value written keeps its case.
        Choices = [.. definition.Choices.Select(choice => new Choice(choice.ToLowerInvariant(), $"{definition.Label}: {choice}", new RelayCommand(() => Value = choice)))];
        UpdateChoices();
    }

    public SettingDefinition Definition { get; }

    public string Label => Definition.Label;

    public string Description => Definition.Description;

    public bool HasDescription => Definition.Description.Length > 0;

    /// <summary>The key as <c>corvid.json</c> and the validator name it ("Corvid:Turn:MaxSteps"); empty for the app's own preferences.</summary>
    public string KeyText => Definition.Store == SettingStore.CorvidJson ? Definition.FullKey : "";

    public bool IsField => Definition.Kind is SettingKind.Text or SettingKind.Number or SettingKind.Path or SettingKind.Url;

    public bool IsChoice => Definition.Kind == SettingKind.Choice;

    public bool IsToggle => Definition.Kind == SettingKind.Toggle;

    /// <summary>Paths, addresses and names are set in the code font.</summary>
    public bool IsMono => Definition.Kind is SettingKind.Path or SettingKind.Url or SettingKind.Text && Definition.Store == SettingStore.CorvidJson;

    public string Placeholder => Definition.Default.Length > 0 ? Definition.Default : Definition.Kind switch
    {
        SettingKind.Path => "not set: the default location",
        SettingKind.Url => "not set",
        _ => "not set",
    };

    /// <summary>The value as edited (two-way). For a toggle "true" or "false"; for a choice one of its words.</summary>
    public string Value
    {
        get => _value;
        set
        {
            if (!SetProperty(ref _value, value ?? ""))
                return;
            OnPropertyChanged(nameof(IsOn));
            OnPropertyChanged(nameof(IsChanged));
            UpdateChoices();
            _changed(this);
        }
    }

    /// <summary>A toggle's value as a switch (two-way).</summary>
    public bool IsOn
    {
        get => bool.TryParse(_value, out var on) && on;
        set => Value = value ? "true" : "false";
    }

    public IReadOnlyList<Choice> Choices { get; }

    public bool IsChanged => !string.Equals(_value, _saved, StringComparison.Ordinal);

    /// <summary>The value as last read or saved; Revert puts it back.</summary>
    public string Saved => _saved;

    /// <summary>The validator's message for this setting; empty when it has none.</summary>
    public string Problem
    {
        get => _problem;
        set
        {
            if (SetProperty(ref _problem, value ?? ""))
                OnPropertyChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => _problem.Length > 0;

    /// <summary>When a change takes effect, as a tag: "needs a restart", "next turn"; empty for at once.</summary>
    public string AppliesText => _restartPending ? "restart to apply" : Definition.Applies switch
    {
        SettingApplies.Restart => "needs a restart",
        SettingApplies.NextTurn => "next turn",
        SettingApplies.NewSessions => "new sessions",
        _ => "",
    };

    public Tone AppliesTone => Definition.Applies == SettingApplies.Restart ? Tone.Amber : Tone.Grey;

    public bool HasApplies => AppliesText.Length > 0;

    public string AppliesDetail => _restartPending
        ? "Saved; the running host still uses the old value until it restarts."
        : Definition.Applies switch
        {
            SettingApplies.Restart => "Read when the host starts: a change applies after a restart.",
            SettingApplies.NextTurn => "A turn already running keeps what it started with; the next one reads the change.",
            SettingApplies.NewSessions => "Sessions opened from now on use it; a session already open keeps what it was opened with.",
            _ => "",
        };

    /// <summary>The value given with <c>--set</c> for this run, cleaned; empty when none.</summary>
    public string RunOverride { get; }

    public bool HasRunOverride => RunOverride.Length > 0;

    public string RunOverrideText => HasRunOverride ? $"set for this run: {RunOverride}" : "";

    public string RunOverrideDetail => HasRunOverride
        ? $"--set {Definition.FullKey}={RunOverride} on the command line. This run uses it; the file's value applies from the next start."
        : "";

    /// <summary>The setting's name for a screen reader: its label, its key, and its message when it has one.</summary>
    public string AutomationName => KeyText.Length > 0 ? $"{Label} ({KeyText})" : Label;

    /// <summary>The page saved: what is edited is now what is saved; a restart setting that changed waits on one.</summary>
    internal void MarkSaved(bool restartPending)
    {
        _saved = _value;
        if (restartPending)
            _restartPending = true;
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(AppliesText));
        OnPropertyChanged(nameof(AppliesDetail));
        OnPropertyChanged(nameof(HasApplies));
    }

    /// <summary>Takes a value read from the file (a raw save, a revert) as both the edited and the saved value.</summary>
    internal void Reset(string value)
    {
        _saved = value;
        _value = value;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(IsChanged));
        UpdateChoices();
    }

    private void UpdateChoices()
    {
        foreach (var choice in Choices)
            choice.IsSelected = string.Equals(choice.Label, _value, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A titled group of settings ("Approvals", "Turns").</summary>
public sealed class SettingGroupViewModel(string title, IReadOnlyList<SettingFieldViewModel> fields)
{
    public string Title { get; } = title;

    public IReadOnlyList<SettingFieldViewModel> Fields { get; } = fields;
}
