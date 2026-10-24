using System.ComponentModel;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Rewind;

/// <summary>The Rewind area (docs/plan/ui.md §4.6, F5): the selected session's checkpoint ledger as a timeline.</summary>
public sealed class RewindViewModel : AreaViewModel
{
    private readonly ShellViewModel _shell;

    public RewindViewModel(ShellViewModel shell)
        : base(Area.Rewind, "The checkpoint ledger as a timeline: preview a restore point, then put back the files, the conversation or both.")
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;
        _shell.PropertyChanged += OnShellChanged;
        BackToSessionCommand = new RelayCommand(() => _shell.Navigate(Area.Sessions));
    }

    /// <summary>The session whose ledger shows; empty when none is open.</summary>
    public string SessionTitle => _shell.CurrentSession?.Title ?? "";

    /// <summary>The mockup's "Back to the session".</summary>
    public RelayCommand BackToSessionCommand { get; }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentSession))
            OnPropertyChanged(nameof(SessionTitle));
    }
}
