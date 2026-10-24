using Corvid.App.Core.Keys;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Settings;

/// <summary>
/// One key on Settings' Keybindings tab: what it does, the key in force, and for the window's own keys a field to change
/// it. A gesture that does not parse, or that another of the window's keys already uses, is refused with the reason and
/// nothing changes; one a page also uses is taken with a note that the page's key wins there.
/// </summary>
public sealed class KeyBindingRowViewModel : ObservableObject
{
    private readonly KeyMap _keys;
    private string _editText;
    private string _problem = "";

    public KeyBindingRowViewModel(KeyMap keys, string id)
    {
        ArgumentNullException.ThrowIfNull(keys);
        _keys = keys;
        Id = id;
        _editText = Binding.KeyText;
        ApplyCommand = new RelayCommand(Apply);
        ResetCommand = new RelayCommand(Reset, () => IsChanged);
    }

    public string Id { get; }

    public KeyBinding Binding => _keys[Id];

    public string Action => Binding.Action;

    public string KeyText => Binding.KeyText;

    public IReadOnlyList<HintPart> KeyParts => Keys.KeyParts.For(Binding.KeyText);

    public bool IsRemappable => Binding.IsRemappable;

    public bool IsFixed => !Binding.IsRemappable;

    /// <summary>What the key needs that is not built yet; it still opens its surface.</summary>
    public string ComesWith => Binding.ComesWith is { } with ? $"comes with {with}" : "";

    public bool HasComesWith => ComesWith.Length > 0;

    /// <summary>The gesture as typed in the field (two-way); Enter or leaving the field applies it.</summary>
    public string EditText
    {
        get => _editText;
        set => SetProperty(ref _editText, value ?? "");
    }

    /// <summary>Why the last gesture typed was refused.</summary>
    public string Problem
    {
        get => _problem;
        private set
        {
            if (SetProperty(ref _problem, value))
                OnPropertyChanged(nameof(HasProblem));
        }
    }

    public bool HasProblem => _problem.Length > 0;

    /// <summary>Where a page's own key wins over this one ("On Audit, Ctrl+F puts the caret in the path or tool filter").</summary>
    public string ShadowText
    {
        get
        {
            var shadows = _keys.ShadowsOf(Binding);
            return shadows.Count == 0
                ? ""
                : string.Join(" ", shadows.Select(shadow => $"In {KeyMap.Heading(shadow.Context)}, {Binding.KeyText} {Lower(shadow.Action)} instead."));
        }
    }

    public bool HasShadow => ShadowText.Length > 0;

    public bool IsChanged => Binding.IsRemappable && Binding.Gesture != Default;

    public RelayCommand ApplyCommand { get; }

    public RelayCommand ResetCommand { get; }

    public string AutomationName => $"{Action}: {KeyText}";

    private KeyGesture? Default => _keys.DefaultOf(Id).Gesture;

    /// <summary>Takes the typed gesture, or shows why not and puts the field back.</summary>
    public void Apply()
    {
        if (!IsRemappable)
            return;
        if (KeyGesture.TryParse(_editText, out var typed, out _) && typed == Binding.Gesture)
        {
            Problem = "";
            EditText = Binding.KeyText;
            return;
        }

        var result = _keys.Rebind(Id, _editText);
        Problem = result.Accepted ? "" : result.Problem;
        if (!result.Accepted)
            return;
        Refresh();
    }

    public void Reset()
    {
        _keys.Reset(Id);
        Problem = "";
        Refresh();
    }

    /// <summary>The map changed (this row or another): the key, the field and the notes follow it.</summary>
    internal void Refresh()
    {
        if (!HasProblem)
            EditText = Binding.KeyText;
        OnPropertyChanged(nameof(Binding));
        OnPropertyChanged(nameof(KeyText));
        OnPropertyChanged(nameof(KeyParts));
        OnPropertyChanged(nameof(ShadowText));
        OnPropertyChanged(nameof(HasShadow));
        OnPropertyChanged(nameof(IsChanged));
        OnPropertyChanged(nameof(AutomationName));
        ResetCommand.NotifyCanExecuteChanged();
    }

    private static string Lower(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}

/// <summary>The keys of one context ("Anywhere", "Composer"), as the Keybindings tab lists them.</summary>
public sealed class KeyGroupViewModel(string heading, IReadOnlyList<KeyBindingRowViewModel> rows)
{
    public string Heading { get; } = heading;

    public IReadOnlyList<KeyBindingRowViewModel> Rows { get; } = rows;
}
