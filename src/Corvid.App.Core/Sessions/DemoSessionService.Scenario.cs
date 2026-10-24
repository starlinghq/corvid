using System.Collections.Immutable;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Sessions;

public sealed partial class DemoSessionService
{
    /// <summary>The fixed session the demo opens: the mockups' "Fix the flaky sandbox test on CI".</summary>
    public const string MainSessionId = "cli-9f2e";

    public const string ReleaseNotesSessionId = "cli-a410";

    public const string VaultSessionId = "cli-77b0";

    public const string TerminalSessionId = "cli-c3d2";

    public const string JobSessionId = "job:nightly-tidy:2026-10-24";

    private const string Kev = "Kev-0.8B";
    private const string Qwen = "qwen3.5-0.8b";
    private const string Claude = "claude";

    // Turn 12's answer as Main.png shows it streaming, then the rest of it.
    private const string Turn12AnswerShown =
        "The failure is the control step, and the sandbox itself is fine. On the runner `unshare -U` is refused because "
        + "unprivileged user namespaces are off (`kernel.unprivileged_userns_clone = 0`), so the test's precondition fails "
        + "before bubblewrap runs.\n\nTwo ways through, and I would take the first:\n\n"
        + "1. Skip the control step when the sysctl is off and assert the sandbox's own refusal message instead.\n"
        + "2. Run the control under ";

    private const string Turn12AnswerRest =
        "a user namespace only where the sysctl allows it, and mark the test skipped elsewhere.\n\n"
        + "The first keeps the test meaningful on runners that forbid user namespaces, and it leaves the production launcher "
        + "untouched.";

    private const string ShellTail =
        "[xUnit.net 00:00:08.11]   SeccompTests.Unshare_user_namespace_is_refused_inside [FAIL]\n"
        + "  Expected the control step to pass outside the sandbox: unshare -U returned 1\n"
        + "  /proc/sys/kernel/unprivileged_userns_clone = 0 on this host\n"
        + "Passed 11   Failed 1   Skipped 0   (12.4 s so far)";

    private const string ShellTailDone =
        "[xUnit.net 00:00:08.11]   SeccompTests.Unshare_user_namespace_is_refused_inside [FAIL]\n"
        + "  Expected the control step to pass outside the sandbox: unshare -U returned 1\n"
        + "  /proc/sys/kernel/unprivileged_userns_clone = 0 on this host\n"
        + "[xUnit.net 00:00:13.84]   Finished: Corvid.Sandbox.Tests\n"
        + "Passed 11   Failed 1   Skipped 0   Total 12   (14.2 s)";

    private void BuildScenario()
    {
        BuildMainSession();
        BuildReleaseNotesSession();
        BuildVaultSession();
        BuildTerminalSession();
        BuildJobSession();
        foreach (var session in _sessions.Values)
            session.LastSummary = Summary(session);
    }

    /// <summary>A wall-clock time on the scenario's day, in the provider's zone: the mockups' times.</summary>
    private DateTimeOffset At(int hour, int minute, int second = 0, int millisecond = 0)
    {
        var zone = _time.LocalTimeZone;
        var day = TimeZoneInfo.ConvertTime(_start, zone);
        var wall = new DateTime(day.Year, day.Month, day.Day, hour, minute, second, millisecond, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    private DemoSession Add(SessionSummary summary)
    {
        var session = new DemoSession(summary.Id) { Base = summary };
        _sessions[summary.Id] = session;
        return session;
    }

    private static void Seed(DemoSession session, DateTimeOffset at, params SessionUpdate[] updates)
    {
        foreach (var update in updates)
            Seed(session, update.At == default ? update with { At = at } : update);
    }

    // ---- cli-9f2e: Main.png and Approval.png ----

    private void BuildMainSession()
    {
        var session = Add(new SessionSummary(MainSessionId, "Fix the flaky sandbox test on CI")
        {
            Workspace = "corvid",
            Branch = "main",
            Pinned = true,
        });
        Seed(session, At(13, 10),
            new TitleChanged("Fix the flaky sandbox test on CI") { Generated = true },
            new SessionForked("cli-8c11", 9),
            new GoalChanged("keep CI green on main"),
            new ApprovalModeChanged(ApprovalMode.Smart),
            new EngineStatus
            {
                Generator = new GeneratorStatus(Qwen, "cuda:0") { Latency = TimeSpan.FromMilliseconds(180) },
                Decider = "Kev-0.8B, calibrated",
                Scheduler = "here · 2 jobs",
                Workspace = "corvid",
                Branch = "main",
                Project = "github.com/starlinghq/corvid",
            },
            new AvailableCommandsUpdate(Commands));
        SeedLedger(session);
        Seed(session, At(14, 0), new PlanUpdate(
        [
            new PlanEntry("Find why the sandbox test fails on CI only", PlanEntryStatus.Completed),
            new PlanEntry("Make the control step allow hosts without user namespaces", PlanEntryStatus.InProgress),
            new PlanEntry("Assert the sandbox's own refusal message", PlanEntryStatus.Pending),
        ]));

        if (Scene == DemoScene.Approval)
        {
            SeedTurn12(session, At(14, 2, 50), complete: true);
            SeedTurn13Waiting(session);
        }
        else
        {
            SeedTurn12(session, At(14, 4, 12), complete: false);
            Seed(session, At(14, 4, 58), new QueueChanged([new QueuedMessage("q-1", "also check the retry count")]));
            session.Queue = [new QueuedMessage("q-1", "also check the retry count")];
            session.QueueCounter = 1;
            session.ApprovalTurnNext = true;
            session.Resume = () => ResumeTurn12(session);
        }
    }

    private static ImmutableArray<SlashCommandInfo> Commands { get; } =
    [
        new("help", "List the commands."),
        new("status", "Slots with their device, queue depth and median latency; the session's state."),
        new("model", "Pick a model: lists a slot's models numbered, or switches one for this session.", "[slot] [<model> | <number>] [--pin] | unpin <slot>"),
        new("goal", "Show, set or clear the standing goal.", "[text | clear]"),
        new("approvals", "Pending approvals and the remembered approval rules."),
        new("todos", "The session's todo list."),
        new("compact", "Compact the conversation before the latest turn now, keeping what you name in full.", "[focus]"),
        new("undo", "Revert the last turn's file changes and take it out of the conversation."),
        new("rollback", "List checkpoints, or go back to before the Nth latest one (files and conversation).", "[N]"),
        new("cancel", "Cancel the running turn."),
    ];

    /// <summary>The write ledger Rewind.png lists: the calls of turns 8 to 12 and the undo of turn 8.</summary>
    private void SeedLedger(DemoSession session)
    {
        void Call(string callId, string tool, string target, int turn, DateTimeOffset at, string before, string after, params FileChange[] changes)
        {
            Seed(session, new CheckpointRecorded(before, CheckpointKind.Before) { CallId = callId, Tool = tool, Target = target, Turn = turn, At = at });
            Seed(session, new CheckpointRecorded(after, CheckpointKind.After)
            {
                CallId = callId,
                Before = before,
                Tool = tool,
                Target = target,
                Turn = turn,
                Changes = [.. changes],
                At = at.AddSeconds(1),
            });
        }

        Call("call-a1", "write_file", "docs/notes.md", 8, At(13, 22), "c-31", "c-32", new FileChange("docs/notes.md", 40, 0));
        Call("call-a2", "shell", "dotnet build", 8, At(13, 30), "c-33", "c-34");
        Seed(session, new CheckpointRecorded("c-35", CheckpointKind.Restore) { Target = "undo of turn 8", Turn = 8, At = At(13, 41) });
        Call("call-b1", "patch_file", "README.md", 9, At(13, 52), "c-36", "c-37", new FileChange("README.md", 2, 1));
        Call("call-c1", "write_file", ".github/workflows/ci.yml", 10, At(13, 59), "c-38", "c-39", new FileChange(".github/workflows/ci.yml", 3, 0));
        Call("call-d1", "patch_file", DemoFiles.BubblewrapPath, 11, At(14, 2), "c-40", "c-41", new FileChange(DemoFiles.BubblewrapPath, 19, 5));
        Call("call-d2", "patch_file", DemoFiles.SeccompTestsPath, 11, At(14, 3), "c-42", "c-43", new FileChange(DemoFiles.SeccompTestsPath, 9, 4));
    }

    /// <summary>
    /// Turn 12 as Main.png draws it: the ingress chips, the escalation to claude, three finished calls, the taint row, the
    /// shell call running for 12 s with its tail, the answer streaming and the thought row; complete, it has finished.
    /// </summary>
    private void SeedTurn12(DemoSession session, DateTimeOffset start, bool complete)
    {
        DateTimeOffset T(double seconds) => start.AddSeconds(seconds);
        Seed(session, new UserMessage("the sandbox test fails on CI only. find out why and fix it without touching the production launcher.")
        {
            At = At(14, 1),
        });
        Seed(session, T(0),
            new TurnStarted(12, Qwen) { MaxSteps = 6 },
            new CheckpointDecided("ingress", Kev,
            [
                new DecisionAnswer("intent", 0.94, "task", true),
                new DecisionAnswer("difficulty", 0.71, "escalate", true),
                new DecisionAnswer("recall", 0.88, "yes", true),
                new DecisionAnswer("tool", 0.91, "read_file", true),
                new DecisionAnswer("clarify", 0.41, "no", false),
            ]) { Latency = TimeSpan.FromMilliseconds(258) });
        Seed(session, T(1),
            new ModelSwitched("generator", Qwen, Claude, ModelSwitchKind.Escalated, "step budget (difficulty 0.71)") { Redacted = 2, Cloud = true },
            new EngineStatus { Generator = new GeneratorStatus(Claude, "cuda:0") { Escalated = true, Cloud = true, Latency = TimeSpan.FromMilliseconds(410) } },
            new TurnProgress(1) { MaxSteps = 6 });
        Seed(session, T(2), new ToolCall("call-1", "read_file", ToolKind.Read, ToolCallStatus.InProgress)
        {
            Target = ".github/workflows/ci.yml",
            Locations = [".github/workflows/ci.yml"],
            Verdict = new CallVerdict(VerdictKind.ReadOnly),
        });
        Seed(session, T(2.3), new ToolCallUpdate("call-1")
        {
            Status = ToolCallStatus.Completed,
            Outcome = ToolOutcome.Succeeded,
            Elapsed = TimeSpan.FromSeconds(0.3),
            Output = "name: ci\non: [push, pull_request]\njobs:\n  test:\n    runs-on: [self-hosted, arch]\n    steps:\n      - uses: actions/checkout@v4\n      - run: dotnet test Corvid.slnx -c Release",
        });
        Seed(session, T(4),
            new TurnProgress(2) { MaxSteps = 6 },
            new ToolCall("call-2", "search_files", ToolKind.Search, ToolCallStatus.InProgress)
            {
                Target = "\"unshare\" in tests/",
                Verdict = new CallVerdict(VerdictKind.ReadOnly),
            });
        Seed(session, T(4.8), new ToolCallUpdate("call-2")
        {
            Status = ToolCallStatus.Completed,
            Outcome = ToolOutcome.Succeeded,
            Summary = "3 hits",
            Elapsed = TimeSpan.FromSeconds(0.8),
            Output = $"{DemoFiles.SeccompTestsPath}:45:        var control = await Run(\"unshare\", \"-U\", \"true\");\n"
                + $"{DemoFiles.SeccompTestsPath}:48:        var inside = await Sandbox.RunAsync(\"unshare -U true\");\n"
                + "tests/Corvid.Sandbox.Tests/NamespaceTests.cs:22:        // unshare -n is refused inside as well",
        });
        Seed(session, T(8),
            new TurnProgress(3) { MaxSteps = 6 },
            new ToolCall("call-3", "web_fetch", ToolKind.Fetch, ToolCallStatus.Pending) { Target = "https://docs.example.org/bubblewrap/seccomp" },
            new CheckpointDecided("pre_execute", Kev,
            [
                new DecisionAnswer("tool", 0.86, "web_fetch", true),
                new DecisionAnswer("risk", 0.81, "ask", true) { Option = "2" },
            ]) { Latency = TimeSpan.FromMilliseconds(244) },
            new ApprovalRequested("a-30", "web_fetch", "web_fetch https://docs.example.org/bubblewrap/seccomp", T(8).AddMinutes(5))
            {
                CallId = "call-3",
                Commands = [new ParsedCommand("web_fetch https://docs.example.org/bubblewrap/seccomp", "web_fetch") { Effects = ["read", "network"] }],
                Reasons = [new ApprovalReason("risk", "risk 2", "Kev-0.8B scored the fetch at risk 2 (0.81): it reads from the network.")],
                Offered = [ApprovalScope.Once, ApprovalScope.Session, ApprovalScope.Always],
            });
        Seed(session, T(12),
            new ApprovalResolved("a-30", ApprovalOutcome.Approved),
            new ToolCallUpdate("call-3") { Status = ToolCallStatus.InProgress });
        Seed(session, T(13.9), new ToolCallUpdate("call-3")
        {
            Status = ToolCallStatus.Completed,
            Outcome = ToolOutcome.Succeeded,
            Summary = "48 KB",
            Elapsed = TimeSpan.FromSeconds(1.9),
            Output = "Seccomp and namespaces in bubblewrap\n\nbwrap --unshare-user needs unprivileged user namespaces, which some distributions turn off with kernel.unprivileged_userns_clone=0; bwrap then needs to be installed setuid.",
        });
        Seed(session, T(14), new TaintChanged(true) { Source = "web_fetch docs.example.org" });
        Seed(session, T(28), new AgentThoughtChunk("The control step runs `unshare -U true` on the host. On the runner it returned 1, so the host refuses user namespaces before the sandbox is involved. "));
        Seed(session, T(30), new AgentThoughtChunk("The sysctl the fetched page names would explain it; the test should tell the two refusals apart."));
        Seed(session, T(32), new TurnProgress(4) { MaxSteps = 6 });
        Seed(session, T(36),
            new ToolCall("call-4", "shell", ToolKind.Execute, ToolCallStatus.InProgress)
            {
                Target = "dotnet test tests/Corvid.Sandbox.Tests --filter Seccomp",
                Verdict = new CallVerdict(VerdictKind.SessionRule)
                {
                    Rule = "`dotnet test` in workspace",
                    ApprovalId = "a-31",
                    RuleTurn = 10,
                    RiskLevel = 1,
                    RiskConfidence = 0.93,
                    CheckpointId = "c-44",
                },
            },
            new CheckpointRecorded("c-44", CheckpointKind.Before)
            {
                CallId = "call-4",
                Tool = "shell",
                Target = "dotnet test tests/Corvid.Sandbox.Tests --filter Seccomp",
                Turn = 12,
            });
        Seed(session, T(40), new AgentMessageChunk(Turn12AnswerShown[..187]));
        Seed(session, T(44), new AgentMessageChunk(Turn12AnswerShown[187..]));
        Seed(session, T(47), new ToolCallUpdate("call-4") { Tail = ShellTail });
        Seed(session, T(47.5),
            new UsageUpdate
            {
                TurnTokens = 1200,
                TurnCost = 0.04m,
                SessionCost = 0.31m,
                ContextTokens = 76_000,
                ContextWindow = 200_000,
                CompactAt = 0.70,
                Breakdown =
                [
                    new ContextPart("system", 2100), new ContextPart("memory", 1400), new ContextPart("skills", 900),
                    new ContextPart("results", 61_000), new ContextPart("stubs", 3000),
                ],
            },
            new ChangesUpdate
            {
                Turn = [new FileChange(DemoFiles.BubblewrapPath, 12, 3), new FileChange(DemoFiles.SeccompTestsPath, 4, 1)],
                Session =
                [
                    new FileChange(DemoFiles.BubblewrapPath, 19, 5), new FileChange(DemoFiles.SeccompTestsPath, 9, 4),
                    new FileChange(".github/workflows/ci.yml", 3, 0), new FileChange("vault.json", 0, 0) { Readable = false },
                ],
                SinceTurn = 1,
            });
        if (!complete)
            return;

        Seed(session, T(50),
            new ToolCallUpdate("call-4")
            {
                Status = ToolCallStatus.Completed,
                Outcome = ToolOutcome.Succeeded,
                Summary = "1 failed",
                Tail = ShellTailDone,
                Output = ShellTailDone,
                Elapsed = TimeSpan.FromSeconds(14.2),
            },
            new CheckpointRecorded("c-44b", CheckpointKind.After) { CallId = "call-4", Before = "c-44", Turn = 12 },
            new AgentMessageChunk(Turn12AnswerRest),
            new UsageUpdate { TurnTokens = 1300, TurnCost = 0.05m, SessionCost = 0.32m },
            new TurnEnded(12, TurnOutcome.Answered) { Elapsed = TimeSpan.FromSeconds(51), Tokens = 1300, Cost = 0.05m });
    }

    /// <summary>
    /// Turn 13 as Approval.png draws it: the pre-execute chips, the shell call awaiting approval a-32 with a-33 behind it,
    /// 9 s into the turn with 4:12 left on the deadline. Its decisions keep the mockup's times.
    /// </summary>
    private void SeedTurn13Waiting(DemoSession session)
    {
        Seed(session, new UserMessage("also check the retry count") { At = At(14, 3, 41) });
        Seed(session, _start.AddSeconds(-9), new TurnStarted(13, Claude) { MaxSteps = 6 });
        Seed(session, Turn13Ingress(At(14, 3, 41)));
        Seed(session, _start.AddSeconds(-8), new TurnProgress(1) { MaxSteps = 6 });
        Seed(session, _start.AddSeconds(-6), new TurnProgress(2) { MaxSteps = 6 });
        Seed(session, Turn13Step(At(14, 4, 8)));
        Seed(session, ShellCall(_start.AddSeconds(-2)));
        Seed(session, Turn13PreExecute(At(14, 4, 12)));
        var deadline = At(14, 9, 12);
        Seed(session, ShellApproval(At(14, 4, 12), deadline, TaintClock(session)));
        Seed(session, PatchApproval(At(14, 4, 12), deadline.AddSeconds(47)));
        Seed(session, _start.AddSeconds(-1), new UsageUpdate { ContextTokens = 82_000, ContextWindow = 200_000 });

        var script = new Script(this, session);
        session.Script = script;
        script.Wait();
        WaitOnShellApproval(session, script);
    }

    private string TaintClock(DemoSession session) =>
        session.State.TaintedAt is { } tainted ? SessionText.Clock(tainted, _time.LocalTimeZone) : SessionText.Clock(_clock, _time.LocalTimeZone);

    private static CheckpointDecided Turn13Ingress(DateTimeOffset at) => new("ingress", Kev,
    [
        new DecisionAnswer("intent", 0.94, "task", true),
        new DecisionAnswer("difficulty", 0.71, "escalate", true),
        new DecisionAnswer("recall", 0.88, "yes", true),
        new DecisionAnswer("clarify", 0.41, "no", false),
    ]) { Latency = TimeSpan.FromMilliseconds(258), At = at };

    private static CheckpointDecided Turn13Step(DateTimeOffset at) => new("step", Kev,
    [
        new DecisionAnswer("tool", 0.88, "shell", true),
        new DecisionAnswer("stuck", 0.07, "no", true),
        new DecisionAnswer("escalate", 0.21, "no", true),
    ]) { Latency = TimeSpan.FromMilliseconds(231), At = at };

    private static CheckpointDecided Turn13PreExecute(DateTimeOffset at) => new("pre_execute", Kev,
    [
        new DecisionAnswer("tool", 0.91, "shell", true),
        new DecisionAnswer("risk", 0.86, "ask", true) { Option = "3" },
        new DecisionAnswer("done", 0.12, "no", true),
    ]) { Latency = TimeSpan.FromMilliseconds(240), At = at };

    private static ToolCall ShellCall(DateTimeOffset at) => new("call-5", "shell", ToolKind.Execute, ToolCallStatus.Pending)
    {
        Target = "rm -rf build/ && dotnet test Corvid.slnx",
        At = at,
    };

    /// <summary>Approval a-32 exactly as Approval.png draws it: two parsed commands, three reasons, the rules always writes.</summary>
    private static ApprovalRequested ShellApproval(DateTimeOffset at, DateTimeOffset deadline, string taintedAt) =>
        new("a-32", "shell", "rm -rf build/ && dotnet test Corvid.slnx", deadline)
        {
            At = at,
            CallId = "call-5",
            Commands =
            [
                new ParsedCommand("rm -rf build/", "rm")
                {
                    Flags = ["-r", "-f"],
                    Effects = ["delete", "recursive"],
                    Targets = [new CommandTarget("./build/", "delete") { Location = "inside", Detail = "212 files" }],
                    Notes = [new CommandNote("undo", "checkpoint c-45 is taken first, so `build/` is restorable")],
                },
                new ParsedCommand("dotnet test Corvid.slnx", "dotnet")
                {
                    Subcommand = "test",
                    Effects = ["execute"],
                    Targets =
                    [
                        new CommandTarget("Corvid.slnx", "read") { Location = "inside" },
                        new CommandTarget("bin/ obj/ TestResults/", "write") { Implied = true },
                    ],
                    Notes = [new CommandNote("network", "none: the sandbox gets no network for this call")],
                },
            ],
            Reasons =
            [
                new ApprovalReason("risk", "risk 3", "Kev-0.8B scored the call at risk 3 (0.86), above the smart-mode floor, because of the recursive delete.")
                {
                    Short = "recursive delete; Kev scored 3 (0.86), above the smart floor",
                },
                new ApprovalReason("tainted", "taint", $"The session holds outside content (web_fetch docs.example.org at {taintedAt}), so only \"once\" is offered for anything that reaches the network. This call reaches none.")
                {
                    Short = "outside content in the session; this call reaches no network",
                },
                new ApprovalReason("reversible", "reversible", "A checkpoint is taken before the call; `dotnet test` changes no tracked files.")
                {
                    Short = "checkpoint c-45 is taken first",
                },
            ],
            Offered = [ApprovalScope.Once, ApprovalScope.Session, ApprovalScope.Always],
            Reversible = true,
            CheckpointId = "c-45",
            TaintSource = "web_fetch docs.example.org",
            Rules =
            [
                new RulePreview("allow", "rm -r") { Paths = ["build/"] },
                new RulePreview("allow", "dotnet test") { Scope = "in the workspace, no network, no elevation" },
            ],
        };

    /// <summary>Approval a-33, behind a-32: a patch to the production launcher, with its diff ready.</summary>
    private static ApprovalRequested PatchApproval(DateTimeOffset at, DateTimeOffset deadline) =>
        new("a-33", "patch_file", $"patch_file {DemoFiles.BubblewrapPath}", deadline)
        {
            At = at,
            CallId = "call-6",
            Commands =
            [
                new ParsedCommand($"patch_file {DemoFiles.BubblewrapPath}", "patch_file")
                {
                    Effects = ["write"],
                    Targets = [new CommandTarget(DemoFiles.BubblewrapPath, "write") { Location = "inside", Detail = "+2 lines" }],
                    Notes = [new CommandNote("undo", "checkpoint c-47 is taken first, so the file is restorable")],
                },
            ],
            Reasons =
            [
                new ApprovalReason("risk", "risk 2", "Kev-0.8B scored the edit at risk 2 (0.78): it changes the production launcher, which the task asked to leave alone.")
                {
                    Short = "edits the production launcher; Kev scored 2 (0.78)",
                },
                new ApprovalReason("reversible", "reversible", "A checkpoint is taken before the edit.") { Short = "checkpoint c-47 is taken first" },
            ],
            Offered = [ApprovalScope.Once, ApprovalScope.Session, ApprovalScope.Always],
            Reversible = true,
            CheckpointId = "c-47",
            TaintSource = "web_fetch docs.example.org",
            ProposedDiff = new FileEdit(DemoFiles.BubblewrapPath, DemoFiles.BubblewrapBefore, DemoFiles.BubblewrapAfter),
            Rules = [new RulePreview("allow", "patch_file") { Paths = [DemoFiles.BubblewrapPath] }],
        };

    // ---- cli-a410: running, with a question queued for the user ----

    private void BuildReleaseNotesSession()
    {
        var session = Add(new SessionSummary(ReleaseNotesSessionId, "Draft release notes for v0.2") { Workspace = "corvid", Branch = "main" });
        Seed(session, At(14, 1, 30),
            new TitleChanged("Draft release notes for v0.2") { Generated = true },
            new ApprovalModeChanged(ApprovalMode.Smart),
            new EngineStatus
            {
                Generator = new GeneratorStatus(Qwen, "cuda:0") { Queue = 1, Latency = TimeSpan.FromMilliseconds(180) },
                Decider = "Kev-0.8B, calibrated",
                Scheduler = "here · 2 jobs",
                Workspace = "corvid",
                Branch = "main",
            },
            new AvailableCommandsUpdate(Commands),
            new UserMessage("draft release notes for v0.2 from CHANGELOG.md and the merged pull requests"));
        Seed(session, At(14, 1, 31),
            new TurnStarted(1, Qwen) { MaxSteps = 5 },
            new CheckpointDecided("ingress", Kev,
            [
                new DecisionAnswer("intent", 0.96, "task", true),
                new DecisionAnswer("difficulty", 0.22, "local", true),
                new DecisionAnswer("recall", 0.74, "yes", true),
                new DecisionAnswer("clarify", 0.38, "no", false),
            ]) { Latency = TimeSpan.FromMilliseconds(251) },
            new TurnProgress(1) { MaxSteps = 5 },
            new ToolCall("call-a1", "read_file", ToolKind.Read, ToolCallStatus.InProgress) { Target = "CHANGELOG.md", Verdict = new CallVerdict(VerdictKind.ReadOnly) });
        Seed(session, new ToolCallUpdate("call-a1") { Status = ToolCallStatus.Completed, Outcome = ToolOutcome.Succeeded, Elapsed = TimeSpan.FromSeconds(0.2), At = At(14, 1, 32) });
        Seed(session, At(14, 2),
            new TurnProgress(2) { MaxSteps = 5 },
            new ToolCall("call-a2", "search_files", ToolKind.Search, ToolCallStatus.InProgress) { Target = "\"Merged PR\" in docs/", Verdict = new CallVerdict(VerdictKind.ReadOnly) });
        Seed(session, new ToolCallUpdate("call-a2") { Status = ToolCallStatus.Completed, Outcome = ToolOutcome.Succeeded, Summary = "18 hits", Elapsed = TimeSpan.FromSeconds(0.7), At = At(14, 2, 1) });
        Seed(session, At(14, 3),
            new TurnProgress(3) { MaxSteps = 5 },
            new ToolCall("call-a3", "write_file", ToolKind.Edit, ToolCallStatus.InProgress)
            {
                Target = DemoFiles.ReleaseNotesPath,
                Verdict = new CallVerdict(VerdictKind.Mode) { Mode = ApprovalMode.Smart, RiskLevel = 0, RiskConfidence = 0.97, Detail = "in workspace, no network" },
            });
        Seed(session, At(14, 3, 10),
            new QuestionAsked("q-1", "0.2.0, or 0.2.0-rc1 for the first tag?") { Title = "which version number?", Options = ["0.2.0", "0.2.0-rc1"] },
            new UsageUpdate { ContextTokens = 24_000, ContextWindow = 200_000, CompactAt = 0.70 });
        session.QuestionHandler = version => ResumeReleaseNotes(session, version);
    }

    // ---- cli-77b0: idle, in another workspace ----

    private void BuildVaultSession()
    {
        var session = Add(new SessionSummary(VaultSessionId, "Vault policy for ntfy") { Workspace = "corvid-vault", Branch = "ntfy" });
        Seed(session, At(12, 10),
            new TitleChanged("Vault policy for ntfy") { Generated = true },
            new ApprovalModeChanged(ApprovalMode.Smart),
            new EngineStatus
            {
                Generator = new GeneratorStatus(Qwen, "cuda:0") { Latency = TimeSpan.FromMilliseconds(180) },
                Decider = "Kev-0.8B, calibrated",
                Scheduler = "here · 2 jobs",
                Workspace = "corvid-vault",
                Branch = "ntfy",
            },
            new AvailableCommandsUpdate(Commands));
        Seed(session, new UserMessage("make the ntfy secret send-only and bind it to ntfy.sh") { At = At(13, 4, 40) });
        Seed(session, At(13, 4, 42),
            new TurnStarted(6, Qwen) { MaxSteps = 4 },
            new CheckpointDecided("ingress", Kev,
            [
                new DecisionAnswer("intent", 0.93, "task", true),
                new DecisionAnswer("difficulty", 0.35, "local", true),
                new DecisionAnswer("recall", 0.84, "yes", true),
                new DecisionAnswer("clarify", 0.18, "no", true),
            ]) { Latency = TimeSpan.FromMilliseconds(249) },
            new ToolCall("call-v1", "read_file", ToolKind.Read, ToolCallStatus.InProgress) { Target = "policies/ntfy.toml", Verdict = new CallVerdict(VerdictKind.ReadOnly) });
        Seed(session, new ToolCallUpdate("call-v1") { Status = ToolCallStatus.Completed, Outcome = ToolOutcome.Succeeded, Elapsed = TimeSpan.FromSeconds(0.2), At = At(13, 4, 43) });
        const string PolicyBefore = "[secret.\"ntfy.key\"]\nhosts = [\"*\"]\nmethods = [\"GET\", \"POST\"]\npolicy = \"ask\"\n";
        const string PolicyAfter = "[secret.\"ntfy.key\"]\nhosts = [\"ntfy.sh\"]\nmethods = [\"POST\"]\npolicy = \"auto\"\n";
        Seed(session, At(13, 4, 50),
            new ToolCall("call-v2", "patch_file", ToolKind.Edit, ToolCallStatus.InProgress)
            {
                Target = "policies/ntfy.toml",
                Verdict = new CallVerdict(VerdictKind.Mode) { Mode = ApprovalMode.Smart, RiskLevel = 1, RiskConfidence = 0.91, Detail = "in workspace, no network", CheckpointId = "c-12" },
            });
        Seed(session, new ToolCallUpdate("call-v2")
        {
            Status = ToolCallStatus.Completed,
            Outcome = ToolOutcome.Succeeded,
            Summary = "+3 −3",
            Elapsed = TimeSpan.FromSeconds(0.1),
            Diffs = [new FileEdit("policies/ntfy.toml", PolicyBefore, PolicyAfter)],
            At = At(13, 4, 51),
        });
        Seed(session, At(13, 4, 58),
            new AgentMessageChunk("The policy now lets `ntfy.key` reach `ntfy.sh` only, with `POST` and nothing else:\n\n```toml\n"
                + PolicyAfter + "```\n\nVault confirms any other use out of band, so a tainted turn cannot widen it."),
            new Notice(NoticeKind.MemoryStaged, "memory staged: 1 fact") { Detail = "ntfy.key is send-only and bound to ntfy.sh" },
            new UsageUpdate { ContextTokens = 18_000, ContextWindow = 200_000, CompactAt = 0.70 });
        Seed(session, new TurnEnded(6, TurnOutcome.Answered) { Elapsed = TimeSpan.FromSeconds(18), Tokens = 640, At = At(13, 5) });
    }

    // ---- cli-c3d2: open in a terminal, a read-only live view ----

    private void BuildTerminalSession()
    {
        var session = Add(new SessionSummary(TerminalSessionId, "Spike S4 write-up") { Workspace = "corvid", Branch = "main" });
        Seed(session, At(14, 4),
            new TitleChanged("Spike S4 write-up") { Generated = true },
            new DriverChanged(true) { Holder = "open in a terminal (corvid chat --session cli-c3d2); take over once it lets go" },
            new EngineStatus
            {
                Generator = new GeneratorStatus(Qwen, "cuda:0") { Latency = TimeSpan.FromMilliseconds(180) },
                Decider = "Kev-0.8B, calibrated",
                Workspace = "corvid",
                Branch = "main",
            },
            new UserMessage("write up spike S4: Kev's tool and done accuracy at the 600-token state against a transcript-sized one"));
        Seed(session, At(14, 4, 20),
            new TurnStarted(1, Qwen) { MaxSteps = 4 },
            new TurnProgress(1) { MaxSteps = 4 },
            new ToolCall("call-s1", "read_file", ToolKind.Read, ToolCallStatus.InProgress) { Target = "docs/spikes/s4/results.json", Verdict = new CallVerdict(VerdictKind.ReadOnly) });
    }

    // ---- job:nightly-tidy: this morning's run ----

    private void BuildJobSession()
    {
        var session = Add(new SessionSummary(JobSessionId, "nightly-tidy run")
        {
            Workspace = "corvid",
            Branch = "main",
            Surface = "job",
            Job = "nightly-tidy",
            Schedule = "every day at 9am",
            RunAt = At(9, 0),
        });
        Seed(session, At(9, 0),
            new TitleChanged("nightly-tidy run"),
            new DriverChanged(true) { Holder = "a job's run, kept as it ran" },
            new EngineStatus { Generator = new GeneratorStatus(Qwen, "cpu"), Decider = "Kev-0.8B, calibrated", Workspace = "corvid", Branch = "main" },
            new UserMessage("Tidy the workspace: list merged branches, TODOs older than a month and files over 1 MB; stage what is worth remembering."));
        Seed(session, At(9, 0, 1),
            new TurnStarted(1, Qwen) { MaxSteps = 4 },
            new ToolCall("call-j1", "shell", ToolKind.Execute, ToolCallStatus.InProgress) { Target = "git branch --merged main", Verdict = new CallVerdict(VerdictKind.ReadOnly) });
        Seed(session, new ToolCallUpdate("call-j1") { Status = ToolCallStatus.Completed, Outcome = ToolOutcome.Succeeded, Summary = "3 branches", Elapsed = TimeSpan.FromSeconds(0.4), At = At(9, 0, 2) });
        Seed(session, At(9, 0, 9),
            new ToolCall("call-j2", "search_files", ToolKind.Search, ToolCallStatus.InProgress) { Target = "\"TODO\" in src/", Verdict = new CallVerdict(VerdictKind.ReadOnly) });
        Seed(session, new ToolCallUpdate("call-j2") { Status = ToolCallStatus.Completed, Outcome = ToolOutcome.Succeeded, Summary = "12 hits", Elapsed = TimeSpan.FromSeconds(0.9), At = At(9, 0, 10) });
        Seed(session, At(9, 0, 18),
            new ToolCall("call-j3", "shell", ToolKind.Execute, ToolCallStatus.InProgress) { Target = "find . -size +1M -not -path './.git/*'", Verdict = new CallVerdict(VerdictKind.ReadOnly) });
        Seed(session, new ToolCallUpdate("call-j3") { Status = ToolCallStatus.Completed, Outcome = ToolOutcome.Succeeded, Summary = "2 files", Elapsed = TimeSpan.FromSeconds(1.1), At = At(9, 0, 19) });
        Seed(session, At(9, 0, 38),
            new AgentMessageChunk("Three branches are merged into `main` and can go: `spike/s1-latency`, `c-030-checkpoints` and `fix/bwrap-proxy`. "
                + "Twelve TODOs are older than a month, nine of them in `src/Corvid.Tools.Builtin`. Two files are over 1 MB, both test fixtures.\n\n"
                + "I staged two facts for review: the merged branches, and that the fixtures are large on purpose."),
            new Notice(NoticeKind.MemoryStaged, "memory staged: 2 facts") { Detail = "staged because memory_op scored 0.62, under its floor" });
        Seed(session, At(9, 0, 41),
            new TurnEnded(1, TurnOutcome.Answered) { Elapsed = TimeSpan.FromSeconds(40), Tokens = 820 },
            new Notice(NoticeKind.JobFinished, "job nightly-tidy ran: 3 tool calls, the result delivered to the Telegram home chat"));
    }
}
