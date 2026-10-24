using System.ComponentModel;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Settings;

/// <summary>
/// The Settings area (docs/plan/ui.md F24). Its first setting is the theme, which applies at once: light, dark, or the
/// system's.
/// </summary>
public sealed class SettingsViewModel : AreaViewModel
{
    private static readonly AppTheme[] ThemeOrder = [AppTheme.Light, AppTheme.Dark, AppTheme.System];

    private readonly ShellViewModel _shell;

    public SettingsViewModel(ShellViewModel shell)
        : base(Area.Settings, "Configuration over corvid.json with the validator's messages, keybindings, themes, accessibility and notifications.")
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        Themes = [.. ThemeOrder.Select(theme => new Choice(theme.ToString().ToLowerInvariant(), Describe(theme),
            new RelayCommand(() => _shell.Theme = theme)))];
        _shell.PropertyChanged += OnShellChanged;
        UpdateThemes();
    }

    /// <summary>Light, dark, system: the segmented control on the page, the one in force selected.</summary>
    public IReadOnlyList<Choice> Themes { get; }

    public AppTheme Theme => _shell.Theme;

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

    private void UpdateThemes()
    {
        foreach (var (choice, theme) in Themes.Zip(ThemeOrder))
            choice.IsSelected = theme == _shell.Theme;
    }
}
