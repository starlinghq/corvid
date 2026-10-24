namespace Corvid.App.Core.Shell;

/// <summary>
/// The Main mockup's shell (Main.dc.html), which demo mode shows until live data replaces it: the corvid workspace on
/// main, three items in the attention tray, the rail's counts, one pinned and three recent sessions, and the status bar
/// of a turn that escalated to claude in a tainted session. Times are relative to the demo clock, so they read as the
/// mockup's ("12 s", "2 m", "1 h", "09:00").
/// </summary>
public static class DemoShell
{
    /// <summary>The fixed session the demo opens: the mockups' "Fix the flaky sandbox test on CI".</summary>
    public const string CurrentSessionId = "cli-9f2e";

    public static void Fill(ShellViewModel shell, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(time);
        var now = time.GetUtcNow();
        shell.Workspace = "corvid · main";
        shell.AttentionCount = 3;
        shell.SetBadge(Area.Sessions, 2);
        shell.SetBadge(Area.Review, 3);
        shell.SetBadge(Area.Memory, 1);
        shell.SetBadge(Area.Mcp, 1);

        var today = time.GetLocalNow();
        var nightly = new DateTimeOffset(today.Date.AddHours(9), today.Offset);
        shell.SetSessions(
        [
            new SessionGroup("Pinned",
            [
                shell.CreateSession(CurrentSessionId, "Fix the flaky sandbox test on CI", SessionState.Running,
                    RelativeTime.Since(now, now.AddSeconds(-12))),
            ]),
            new SessionGroup("Recent",
            [
                shell.CreateSession("cli-a410", "Draft release notes for v0.2", SessionState.Running,
                    RelativeTime.Since(now, now.AddMinutes(-2))),
                shell.CreateSession("cli-77b0", "Vault policy for ntfy", SessionState.Idle,
                    RelativeTime.Since(now, now.AddHours(-1))),
                shell.CreateSession("job:nightly-tidy:2026-10-24", "nightly-tidy run", SessionState.Job,
                    RelativeTime.ClockTime(nightly, time.LocalTimeZone)),
            ]),
        ]);
        shell.SelectSession(shell.FindSession(CurrentSessionId));
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
