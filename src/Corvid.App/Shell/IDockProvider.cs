namespace Corvid.App.Shell;

/// <summary>
/// A page that puts panels in the shell's dock (docs/plan/ui.md §3): a session's Changes, Files and Context, the Board's
/// attention tray. The shell shows the content as the 380 px column at wide widths and over the content, from the status
/// bar's toggle, below them; a page without panels leaves the column away.
/// </summary>
public interface IDockProvider
{
    /// <summary>The dock's content, made by the page and kept for its lifetime; null when the page has none.</summary>
    UIElement? Dock { get; }
}
