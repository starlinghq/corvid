using System.Windows.Input;

namespace Corvid.App.Core.Mvvm;

/// <summary>
/// One option of a segmented control (the approval modes, the theme): its label, the sentence its hover text and screen
/// reader description give, the command that asks for it, and whether it is the one in force. Not generic, so a XAML data
/// template can name it.
/// </summary>
public sealed class Choice : ObservableObject
{
    private bool _isSelected;

    public Choice(string label, string description, ICommand choose)
    {
        ArgumentNullException.ThrowIfNull(choose);
        Label = label;
        Description = description;
        Choose = choose;
    }

    public string Label { get; }

    public string Description { get; }

    public ICommand Choose { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
