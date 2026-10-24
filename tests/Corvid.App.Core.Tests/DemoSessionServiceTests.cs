using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class DemoSessionServiceTests
{
    private const string Main = DemoSessionService.MainSessionId;

    private static (DemoSessionService Service, ManualTimeProvider Time) Demo(DemoScene scene = DemoScene.Main)
    {
        var time = ManualTimeProvider.Demo();
        return (new DemoSessionService(new InlineDispatcher(), time, scene), time);
    }

    private static string Clock(DateTimeOffset at) => at.ToUniversalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task TheListIsTheMockupsSessionsWithTheirLiveState()
    {
        var (service, _) = Demo();

        var sessions = (await service.ListAsync(TestContext.Current.CancellationToken)).ToDictionary(summary => summary.Id);

        Assert.Equal(5, sessions.Count);
        var main = sessions[Main];
        Assert.Equal(("Fix the flaky sandbox test on CI", SessionState.Running, true), (main.Title, main.State, main.Pinned));
        Assert.Equal("14:04:48", Clock(main.StepStarted!.Value));
        Assert.Equal((4, 6, 12, true, true), (main.Step, main.MaxSteps, main.Turns, main.Tainted, main.Cloud));
        Assert.Equal("claude (escalated)", main.Model);
        Assert.Equal(("cli-8c11", 9), (main.ForkOf, main.ForkTurn));
        Assert.Equal(0, main.Attention);

        var notes = sessions[DemoSessionService.ReleaseNotesSessionId];
        Assert.Equal((SessionState.Running, 1, "which version number?"), (notes.State, notes.PendingQuestions, notes.Question));
        Assert.Equal("14:03:00", Clock(notes.StepStarted!.Value));
        Assert.Equal("write_file docs/releases/v0.2.md", notes.Activity);

        var vault = sessions[DemoSessionService.VaultSessionId];
        Assert.Equal((SessionState.Idle, "corvid-vault", "ntfy", "13:05:00"), (vault.State, vault.Workspace, vault.Branch, Clock(vault.LastActivity)));

        var terminal = sessions[DemoSessionService.TerminalSessionId];
        Assert.True(terminal.ReadOnly);
        Assert.Equal(SessionState.Running, terminal.State);

        var job = sessions[DemoSessionService.JobSessionId];
        Assert.Equal((SessionState.Job, "nightly-tidy", "09:00:00"), (job.State, job.Job, Clock(job.RunAt!.Value)));
    }

    [Fact]
    public async Task OpeningTheMainSessionGivesTurn12AsMainPngDrawsIt()
    {
        var (service, _) = Demo();

        using var subscription = await service.OpenAsync(Main, _ => { }, TestContext.Current.CancellationToken);
        var state = SessionFold.Replay(Main, subscription.History);

        Assert.True(state.Running);
        Assert.Equal(12, state.Turn);
        Assert.Equal(["read_file", "search_files", "web_fetch", "shell"], state.Transcript.OfType<ToolEntry>().Select(tool => tool.Tool));
        var shell = state.Transcript.OfType<ToolEntry>().Last();
        Assert.Equal((ToolCallStatus.InProgress, VerdictKind.SessionRule, "c-44"), (shell.Status, shell.Verdict!.Kind, shell.Verdict.CheckpointId));
        Assert.StartsWith("[xUnit.net 00:00:08.11]", shell.Tail, StringComparison.Ordinal);
        var answer = state.Transcript.OfType<AnswerEntry>().Single();
        Assert.True(answer.Streaming);
        Assert.EndsWith("2. Run the control under ", answer.Text, StringComparison.Ordinal);
        Assert.Equal(["also check the retry count"], state.Queue.Select(message => message.Text));
        Assert.Equal(0.38, state.ContextFraction!.Value, 3);
        // Seven seeded entries, then the running shell call's before-checkpoint, whose after is still to come.
        Assert.Equal(8, state.Ledger.Count);
        Assert.Equal(("c-44", (string?)null), (state.Ledger[^1].Id, state.Ledger[^1].AfterId));
        Assert.Equal(ApprovalMode.Smart, state.Mode);
        Assert.Equal("keep CI green on main", state.Goal);
    }

    [Fact]
    public async Task TheSceneHoldsStillUntilTheFirstMessageThenTurn12EndsAndTheQueueAsksForApproval()
    {
        var (service, time) = Demo();
        var heard = new List<SessionUpdate>();
        using var subscription = await service.OpenAsync(Main, heard.Add, TestContext.Current.CancellationToken);

        time.Advance(120);
        Assert.Empty(heard);

        await service.QueueAsync(Main, "then summarise what changed", TestContext.Current.CancellationToken);
        time.Advance(30);

        var state = service.StateOf(Main);
        Assert.Equal(13, state.Turn);
        Assert.True(state.Running);
        Assert.Equal(["a-32", "a-33"], state.Approvals.Select(request => request.ApprovalId));
        Assert.Equal(["then summarise what changed"], state.Queue.Select(message => message.Text));
        var twelve = state.Transcript.OfType<TurnHeaderEntry>().Single(header => header.Number == 12);
        Assert.Equal((TurnOutcome.Answered, 1300), (twelve.Outcome, twelve.Tokens));
        Assert.Contains(state.Transcript, entry => entry is UserEntry { Text: "also check the retry count", Turn: 13 });
        Assert.Contains("leaves the production launcher", state.Transcript.OfType<AnswerEntry>().First().Text, StringComparison.Ordinal);
        // The subscriber heard every update after the history, in order, and the fold of both is the service's state.
        Assert.Equal(service.HistoryOf(Main).Count, subscription.History.Count + heard.Count);
        Assert.Equal(state.Transcript.Count, SessionFold.Replay(Main, [.. subscription.History, .. heard]).Transcript.Count);

        // It waits on the answer however long it takes, short of the deadline.
        time.Advance(60);
        Assert.Equal(2, service.StateOf(Main).Approvals.Count);
    }

    [Fact]
    public async Task TheApprovalSceneIsApprovalPngsMoment()
    {
        var (service, _) = Demo(DemoScene.Approval);

        var state = service.StateOf(Main);
        var summary = (await service.ListAsync(TestContext.Current.CancellationToken)).Single(session => session.Id == Main);

        Assert.Equal((SessionState.Waiting, 1), (summary.State, summary.Attention));
        Assert.Equal(TimeSpan.FromSeconds(252), summary.Deadline - service.Clock);
        Assert.Equal((13, 2, 6), (state.Turn, state.Step, state.MaxSteps));
        var request = state.CurrentApproval!;
        Assert.Equal(("rm -rf build/ && dotnet test Corvid.slnx", 2, 3), (request.Text, request.Commands.Length, request.Reasons.Length));
        Assert.Equal(["risk 3", "taint", "reversible"], request.Reasons.Select(reason => reason.Label));
        Assert.Contains("web_fetch docs.example.org at 14:03", request.Reasons[1].Detail, StringComparison.Ordinal);
        Assert.Equal(["rm -r", "dotnet test"], request.Rules.Select(rule => rule.Command));
        Assert.Equal(["14:03:41", "14:04:08", "14:04:12"], state.Decisions.TakeLast(3).Select(group => Clock(group.At)));
        Assert.Equal(0.41, state.ContextFraction!.Value, 3);
    }

    [Fact]
    public void AnsweringForTheSessionRunsTheShellThenTheDeniedPatchSendsTheReasonNext()
    {
        var (service, time) = Demo(DemoScene.Approval);

        service.AnswerApprovalAsync(Main, "a-32", new ApprovalAnswer(ApprovalChoice.Session), TestContext.Current.CancellationToken);
        time.Advance(5);

        var state = service.StateOf(Main);
        var shell = state.Transcript.OfType<ToolEntry>().Single(tool => tool.CallId == "call-5");
        Assert.Equal((ToolCallStatus.Completed, "12 passed", VerdictKind.ApprovedSession), (shell.Status, shell.Summary, shell.Verdict!.Kind));
        Assert.Equal("`rm -r` under build/ and `dotnet test` in the workspace", shell.Verdict.Rule);
        Assert.Equal("a-33", state.CurrentApproval?.ApprovalId);
        Assert.Equal("a-33", state.Transcript.OfType<ToolEntry>().Single(tool => tool.CallId == "call-6").ApprovalId);

        service.AnswerApprovalAsync(Main, "a-33", new ApprovalAnswer(ApprovalChoice.Deny, "keep the launcher untouched"), TestContext.Current.CancellationToken);
        time.Advance(30);

        state = service.StateOf(Main);
        var patch = state.Transcript.OfType<ToolEntry>().Single(tool => tool.CallId == "call-6");
        Assert.Equal((ToolOutcome.Denied, VerdictKind.Denied, "keep the launcher untouched"), (patch.Outcome, patch.Verdict!.Kind, patch.Verdict.Detail));
        Assert.Contains("the production launcher is untouched", state.Transcript.OfType<AnswerEntry>().Last(answer => answer.Turn == 13).Text, StringComparison.Ordinal);
        // The reason went as the next message: turn 14, a scripted turn of its own.
        Assert.Equal(14, state.Turn);
        Assert.Contains(state.Transcript, entry => entry is UserEntry { Text: "keep the launcher untouched", Turn: 14 });
        Assert.False(state.Running);
    }

    [Fact]
    public void AnUnansweredPromptIsDeniedAtItsDeadline()
    {
        var (service, time) = Demo(DemoScene.Approval);

        time.Advance(251);
        Assert.Equal("a-32", service.StateOf(Main).CurrentApproval?.ApprovalId);
        time.Advance(2);

        var state = service.StateOf(Main);
        var shell = state.Transcript.OfType<ToolEntry>().Single(tool => tool.CallId == "call-5");
        Assert.Equal((ToolOutcome.Denied, VerdictKind.TimedOut), (shell.Outcome, shell.Verdict!.Kind));
        Assert.Equal("a-33", state.CurrentApproval?.ApprovalId);

        time.Advance(60);
        state = service.StateOf(Main);
        Assert.False(state.Running);
        Assert.Equal(VerdictKind.TimedOut, state.Transcript.OfType<ToolEntry>().Single(tool => tool.CallId == "call-6").Verdict!.Kind);
    }

    [Fact]
    public async Task ASteerArrivesAtTheNextCheckpointAndInterruptKeepsTheWorkAndRunsTheQueue()
    {
        var (service, time) = Demo();
        const string Vault = DemoSessionService.VaultSessionId;

        await service.SendAsync(Vault, "check the retry count", TestContext.Current.CancellationToken);
        time.Advance(0.4);
        Assert.True(service.StateOf(Vault).Running);
        await service.SteerAsync(Vault, "only in ci.yml", TestContext.Current.CancellationToken);
        Assert.True(service.StateOf(Vault).Queue.Single().Steer);
        time.Advance(10);

        var state = service.StateOf(Vault);
        Assert.False(state.Running);
        Assert.Empty(state.Queue);
        var steer = state.Transcript.OfType<UserEntry>().Single(user => user.Steered);
        Assert.Equal(7, steer.Turn);
        Assert.Contains("only in ci.yml", state.Transcript.OfType<AnswerEntry>().Last().Text, StringComparison.Ordinal);
        Assert.Equal(["read_file", "patch_file", "search_files"], state.Transcript.OfType<ToolEntry>().Select(tool => tool.Tool));

        await service.SendAsync(Vault, "list the policies", TestContext.Current.CancellationToken);
        time.Advance(0.7);
        await service.QueueAsync(Vault, "and the hosts they allow", TestContext.Current.CancellationToken);
        await service.InterruptAsync(Vault, TestContext.Current.CancellationToken);

        state = service.StateOf(Vault);
        var eight = state.Transcript.OfType<TurnHeaderEntry>().Single(header => header.Number == 8);
        Assert.Equal(TurnOutcome.Cancelled, eight.Outcome);
        Assert.Equal(ToolOutcome.Cancelled, state.Transcript.OfType<ToolEntry>().Last().Outcome);
        time.Advance(1);
        Assert.Equal(9, service.StateOf(Vault).Turn);
        Assert.Contains(service.StateOf(Vault).Transcript, entry => entry is UserEntry { Text: "and the hosts they allow" });
    }

    [Fact]
    public async Task TheScriptIsTheSameEveryTimeUnderAManualClock()
    {
        static async Task<List<string>> Run()
        {
            var (service, time) = Demo();
            await service.QueueAsync(Main, "then summarise");
            time.Advance(30);
            await service.AnswerApprovalAsync(Main, "a-32", ApprovalAnswer.Once);
            time.Advance(5);
            await service.AnswerApprovalAsync(Main, "a-33", ApprovalAnswer.Once);
            time.Advance(60);
            return [.. service.HistoryOf(Main).Select(update => $"{update.GetType().Name}@{update.At:O}")];
        }

        var first = await Run();
        var second = await Run();

        Assert.Equal(first, second);
        Assert.True(first.Count > 100);
    }

    [Fact]
    public async Task StaleAnswersChangeNothingAndAReadOnlySessionRefusesRequests()
    {
        var (service, _) = Demo(DemoScene.Approval);
        var before = service.HistoryOf(Main).Count;

        await service.AnswerApprovalAsync(Main, "a-99", ApprovalAnswer.Once, TestContext.Current.CancellationToken);
        Assert.Equal(before, service.HistoryOf(Main).Count);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SendAsync(DemoSessionService.TerminalSessionId, "hello", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.OpenAsync("cli-0000", _ => { }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnsweringTheQuestionLetsTheReleaseNotesFinish()
    {
        var (service, time) = Demo();
        const string Notes = DemoSessionService.ReleaseNotesSessionId;

        await service.AnswerQuestionAsync(Notes, "q-1", "0.2.0-rc1", TestContext.Current.CancellationToken);
        time.Advance(10);

        var state = service.StateOf(Notes);
        Assert.False(state.Running);
        Assert.Null(state.Question);
        var write = state.Transcript.OfType<ToolEntry>().Single(tool => tool.CallId == "call-a3");
        Assert.Equal(ToolCallStatus.Completed, write.Status);
        Assert.Contains("# Corvid 0.2.0-rc1", Assert.Single(write.Diffs).NewText, StringComparison.Ordinal);
        Assert.Contains("**0.2.0-rc1**", state.Transcript.OfType<AnswerEntry>().Last().Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlashCommandsAnswerAsCards()
    {
        var (service, _) = Demo();
        const string Vault = DemoSessionService.VaultSessionId;

        await service.RunCommandAsync(Vault, "/help", TestContext.Current.CancellationToken);
        await service.RunCommandAsync(Vault, "/goal ship the policy", TestContext.Current.CancellationToken);
        await service.RunCommandAsync(Vault, "/compact the policy decisions", TestContext.Current.CancellationToken);
        await service.RunCommandAsync(Vault, "/frobnicate", TestContext.Current.CancellationToken);

        var state = service.StateOf(Vault);
        var results = state.Transcript.OfType<CommandEntry>().Select(entry => entry.Result).ToList();
        Assert.Equal(["/help", "/goal ship the policy", "/compact the policy decisions", "/frobnicate"], results.Select(result => result.Line));
        Assert.Contains(results[0].Rows, row => row[0] == "/compact [focus]");
        Assert.Equal("ship the policy", state.Goal);
        Assert.Equal("the policy decisions", state.Transcript.OfType<CompactionEntry>().Single().Compaction.Focus);
        Assert.Equal(OutputTone.Error, results[3].Tone);
    }

    [Fact]
    public async Task AForkIsANewSessionWithTheHistoryThroughItsPosition()
    {
        var (service, _) = Demo();
        var changed = new List<SessionSummary>();
        service.SessionChanged += (_, summary) => changed.Add(summary);

        var id = await service.ForkAsync(DemoSessionService.VaultSessionId, null, TestContext.Current.CancellationToken);

        var fork = service.StateOf(id);
        Assert.Equal(("Vault policy for ntfy (fork)", "cli-77b0", 6), (fork.Title, fork.ForkOf, fork.ForkTurn));
        Assert.Contains(fork.Transcript, entry => entry is AnswerEntry);
        Assert.Contains(changed, summary => summary.Id == id);
        Assert.Contains(await service.ListAsync(TestContext.Current.CancellationToken), summary => summary.Id == id);
    }
}
