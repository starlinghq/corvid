using Corvid.App.Core.Shell;

namespace Corvid.App.Controls;

/// <summary>
/// A session row in the rail (Main.dc.html <c>.sessions</c>): the status dot (grey idle, accent running, amber waiting,
/// violet job), the title cut with an ellipsis, and the time since its last activity. The selected session has the
/// third background. The dot's meaning is also in the row's accessible name and tooltip, never in colour alone.
/// </summary>
public sealed partial class RailSessionItem : Button
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(RailSessionItem), new PropertyMetadata(""));

    public static readonly DependencyProperty WhenProperty = DependencyProperty.Register(
        nameof(When), typeof(string), typeof(RailSessionItem), new PropertyMetadata(""));

    public static readonly DependencyProperty StateProperty = DependencyProperty.Register(
        nameof(State), typeof(SessionState), typeof(RailSessionItem), new PropertyMetadata(SessionState.Idle, (d, _) => ((RailSessionItem)d).UpdateStates(true)));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(RailSessionItem), new PropertyMetadata(false, (d, _) => ((RailSessionItem)d).UpdateStates(true)));

    public RailSessionItem() => DefaultStyleKey = typeof(RailSessionItem);

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string When
    {
        get => (string)GetValue(WhenProperty);
        set => SetValue(WhenProperty, value);
    }

    public SessionState State
    {
        get => (SessionState)GetValue(StateProperty);
        set => SetValue(StateProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateStates(false);
    }

    private void UpdateStates(bool useTransitions)
    {
        VisualStateManager.GoToState(this, IsSelected ? "Selected" : "Unselected", useTransitions);
        VisualStateManager.GoToState(this, State.ToString(), useTransitions);
    }
}
