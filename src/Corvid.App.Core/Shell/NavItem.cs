using System.Globalization;
using System.Windows.Input;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Shell;

/// <summary>One destination in the rail: its icon and label, the count of what waits there, and whether it is current.</summary>
public sealed class NavItem : ObservableObject
{
    private int _badge;
    private bool _isSelected;

    internal NavItem(Area area, ICommand open)
    {
        Area = area;
        Open = open;
    }

    public Area Area { get; }

    public string Label => Area.Title();

    public IconKind Icon => Area.Icon();

    /// <summary>Opens the area.</summary>
    public ICommand Open { get; }

    /// <summary>What waits in the area (running sessions, files to review, staged writes, held tools); 0 hides the badge.</summary>
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

    /// <summary>The badge as drawn; empty when nothing waits, which the view takes as "no badge".</summary>
    public string BadgeText => _badge > 0 ? _badge.ToString(CultureInfo.InvariantCulture) : "";

    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetProperty(ref _isSelected, value);
    }

    /// <summary>What a screen reader says for the row: the label, and the count when there is one.</summary>
    public string AutomationName => _badge > 0 ? $"{Label}, {BadgeText}" : Label;
}
