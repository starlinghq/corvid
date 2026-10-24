using System.Globalization;
using System.Windows.Input;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// A tab of the session's dock (docs/plan/ui.md §3: Changes, Files, Tasks, Decisions, Context, Output, Session): its
/// label, the count its badge shows, and the one line said while the panel has nothing to draw yet.
/// </summary>
public sealed class SessionPanel : ObservableObject
{
    private int _badge;
    private bool _isSelected;

    internal SessionPanel(string label, string emptyText, ICommand open)
    {
        Label = label;
        EmptyText = emptyText;
        Open = open;
    }

    public string Label { get; }

    /// <summary>What the panel shows when it has nothing, in one line that says what will be there.</summary>
    public string EmptyText { get; }

    public ICommand Open { get; }

    public int Badge
    {
        get => _badge;
        internal set
        {
            if (SetProperty(ref _badge, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(BadgeText));
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    public string BadgeText => _badge > 0 ? _badge.ToString(CultureInfo.InvariantCulture) : "";

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    public string AutomationName => _badge > 0 ? $"{Label}, {BadgeText}" : Label;
}
