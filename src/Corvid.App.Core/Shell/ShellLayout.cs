namespace Corvid.App.Core.Shell;

/// <summary>
/// The shell's arrangement at a window width (docs/plan/ui.md §3 and the mockups' breakpoints): the dock hides under
/// 1180 px, the rail under 860 px, and under 720 px the phone layout takes over with its top and bottom bars.
/// </summary>
public enum ShellLayout
{
    /// <summary>Rail, content and dock: 1180 px and wider.</summary>
    Wide,
    /// <summary>Rail and content; the dock opens over the content from the status bar's toggle.</summary>
    Medium,
    /// <summary>Content only, from 720 to 860 px; the rail and the dock open over it.</summary>
    Narrow,
    /// <summary>One column with a top bar and the Chat, Changes, Attention and More bar, under 720 px.</summary>
    Phone,
}

public static class ShellLayouts
{
    public const double DockMinWidth = 1180;
    public const double RailMinWidth = 860;
    public const double DesktopMinWidth = 720;

    public static ShellLayout ForWidth(double width) => width switch
    {
        >= DockMinWidth => ShellLayout.Wide,
        >= RailMinWidth => ShellLayout.Medium,
        >= DesktopMinWidth => ShellLayout.Narrow,
        _ => ShellLayout.Phone,
    };
}
