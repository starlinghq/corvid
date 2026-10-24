using System.Globalization;
using System.Windows.Input;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Shell;

/// <summary>A tab of the phone layout's bottom bar (docs/plan/ui.md §3: Chat, Changes, Attention, More).</summary>
public sealed class PhoneTab : ObservableObject
{
    private int _count;
    private bool _isSelected;

    internal PhoneTab(string label, IconKind icon, ICommand open)
    {
        Label = label;
        Icon = icon;
        Open = open;
    }

    public string Label { get; }

    public IconKind Icon { get; }

    public ICommand Open { get; }

    public int Count
    {
        get => _count;
        internal set
        {
            if (SetProperty(ref _count, Math.Max(0, value)))
            {
                OnPropertyChanged(nameof(CountText));
                OnPropertyChanged(nameof(AutomationName));
            }
        }
    }

    /// <summary>The count as drawn; empty when there is none.</summary>
    public string CountText => _count > 0 ? _count.ToString(CultureInfo.InvariantCulture) : "";

    public bool IsSelected
    {
        get => _isSelected;
        internal set
        {
            if (SetProperty(ref _isSelected, value))
                OnPropertyChanged(nameof(AutomationName));
        }
    }

    /// <summary>"Changes, 3, current": the label, the count, and whether it is the tab shown (not said by colour alone).</summary>
    public string AutomationName => (_count > 0 ? $"{Label}, {CountText}" : Label) + (_isSelected ? ", current" : "");
}
