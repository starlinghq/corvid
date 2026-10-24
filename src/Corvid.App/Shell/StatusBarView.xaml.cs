using System.ComponentModel;
using Corvid.App.Controls;
using Corvid.App.Core.Shell;
using Microsoft.UI.Xaml.Automation;

namespace Corvid.App.Shell;

/// <summary>
/// The status bar (Main.dc.html <c>footer.statusbar</c>), with the toggles that bring back a hidden rail or dock. Each tag
/// is its own item in the row, after the mode segments, so the row can let "2 approvals" give way and keep "tainted",
/// which never does (ui.md §7).
/// </summary>
public sealed partial class StatusBarView : UserControl
{
    public static readonly DependencyProperty ShellProperty = DependencyProperty.Register(
        nameof(Shell), typeof(ShellViewModel), typeof(StatusBarView), new PropertyMetadata(null, OnShellChanged));

    private readonly List<TagChip> _tags = [];

    public StatusBarView() => InitializeComponent();

    public ShellViewModel Shell
    {
        get => (ShellViewModel)GetValue(ShellProperty);
        set => SetValue(ShellProperty, value);
    }

    private static void OnShellChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (StatusBarView)d;
        if (e.OldValue is ShellViewModel old)
            old.StatusBar.PropertyChanged -= view.OnBarChanged;
        if (e.NewValue is ShellViewModel shell)
            shell.StatusBar.PropertyChanged += view.OnBarChanged;
        view.ShowTags();
    }

    private void OnBarChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(StatusBarViewModel.Tags) or null or "")
            ShowTags();
    }

    /// <summary>Puts the bar's tags in the row after the mode segments, one item each: taint never gives way, the rest go last.</summary>
    private void ShowTags()
    {
        foreach (var chip in _tags)
            Row.Children.Remove(chip);
        _tags.Clear();
        var at = Row.Children.IndexOf(ModeSegments) + 1;
        foreach (var tag in Shell?.StatusBar.Tags ?? [])
        {
            var chip = new TagChip { Text = tag.Text, Tone = tag.Tone, VerticalAlignment = VerticalAlignment.Center };
            FitRowPanel.SetGiveWay(chip, ShellViewModel.IsTaint(tag) ? -1 : 0);
            AutomationProperties.SetName(chip, tag.Text);
            if (tag.Detail.Length > 0)
            {
                AutomationProperties.SetHelpText(chip, tag.Detail);
                ToolTipService.SetToolTip(chip, tag.Detail);
            }

            Row.Children.Insert(at++, chip);
            _tags.Add(chip);
        }
    }
}
