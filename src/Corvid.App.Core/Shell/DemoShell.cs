namespace Corvid.App.Core.Shell;

/// <summary>
/// The parts of the Main mockup's shell (Main.dc.html) that no live feed sets yet, which demo mode shows until their areas
/// are built: the corvid workspace on main, the rail's counts for Review, Memory and MCP, the two attention items those
/// areas hold (a staged memory, a held MCP tool), and the status bar of a turn that escalated to claude in a tainted
/// session. The session list, the Sessions badge and the sessions' own attention items come from the session service
/// (Sessions/SessionHub.cs), which also keeps the status bar while a session's area shows.
/// </summary>
public static class DemoShell
{
    /// <summary>The fixed session the demo opens: the mockups' "Fix the flaky sandbox test on CI".</summary>
    public const string CurrentSessionId = "cli-9f2e";

    /// <summary>The attention source for the items the areas other than Sessions will report.</summary>
    public const string AreasAttention = "areas";

    public static void Fill(ShellViewModel shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        shell.Workspace = "corvid · main";
        // Main.png's bell says 3: the question waiting in cli-a410 (from the session service), a staged memory and a held
        // MCP tool (these two).
        shell.SetAttention(AreasAttention, 2);
        shell.SetBadge(Area.Review, 3);
        shell.SetBadge(Area.Memory, 1);
        shell.SetBadge(Area.Mcp, 1);
    }

    public static void Fill(StatusBarViewModel bar)
    {
        ArgumentNullException.ThrowIfNull(bar);
        bar.Generator = "claude (escalated) · cuda:0 · q0 · 410 ms";
        bar.Decider = "Kev-0.8B, calibrated";
        bar.Context = "38%";
        bar.Mode = ApprovalMode.Smart;
        bar.Tags =
        [
            new StatusTag("tainted", Tone.Amber,
                "Outside content in context: web_fetch docs.example.org. Network reads now ask; egress and new recipients offer only \"once\"."),
        ];
        bar.Scheduler = "here · 2 jobs";
        bar.Branch = "main";
        bar.Trailing = "turn 48 s";
    }
}
