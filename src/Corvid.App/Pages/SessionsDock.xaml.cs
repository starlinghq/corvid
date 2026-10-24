using System.Collections;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Pages;

/// <summary>The session's dock panels (Main.dc.html <c>aside.dock</c>): the tabs, and the selected one's body.</summary>
public sealed partial class SessionsDock : UserControl
{
    public static readonly DependencyProperty ViewModelProperty = DependencyProperty.Register(
        nameof(ViewModel), typeof(SessionsViewModel), typeof(SessionsDock), new PropertyMetadata(null));

    /// <summary>The dock's path column fits about this many monospace characters at 380 px.</summary>
    private const int PathChars = 38;

    public SessionsDock() => InitializeComponent();

    public SessionsViewModel ViewModel
    {
        get => (SessionsViewModel)GetValue(ViewModelProperty);
        set => SetValue(ViewModelProperty, value);
    }

    public static Visibility Is(SessionPanel? panel, string label) =>
        panel is not null && string.Equals(panel.Label, label, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A panel's one line for when it has nothing to show.</summary>
    public static string Label(IReadOnlyList<SessionPanel>? panels, int index) => panels is not null && index < panels.Count ? panels[index].EmptyText : "";

    public static Visibility None(IEnumerable? items) => items is null || !items.GetEnumerator().MoveNext() ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility NoText(string? text) => string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Not(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// Long paths keep their end (CSS <c>direction: rtl</c> with an ellipsis): "…c/Corvid.Sandbox/BubblewrapSandbox.cs".
    /// </summary>
    public static string TrimPath(string path) => path.Length <= PathChars ? path : "…" + path[^(PathChars - 1)..];

    // The gap after the arrow is the row's spacing: trailing spaces are trimmed from measured text.
    public static string Arrow(string question) => $"{question} →";

    public static Visibility AskVisible(bool ask, bool code) => ask && !code ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility PlainVisible(bool ask, bool code) => !ask && !code ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A task's mark: done, in progress, or to do (the words say it too).</summary>
    public static string TaskMark(bool done, bool active) => done ? "✓" : active ? "●" : "○";

    public static string TaskName(string text, string status) => $"{text}, {status}";
}
