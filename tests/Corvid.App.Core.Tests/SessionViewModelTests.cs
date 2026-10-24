using Corvid.App.Core.Markdown;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class SessionViewModelTests
{
    private const string Main = DemoSessionService.MainSessionId;
    private const string Vault = DemoSessionService.VaultSessionId;

    private static async Task<(SessionViewModel Session, DemoSessionService Service, ManualTimeProvider Time)> Open(string id,
        DemoScene scene = DemoScene.Main)
    {
        var time = ManualTimeProvider.Demo();
        var service = new DemoSessionService(new InlineDispatcher(), time, scene);
        var session = new SessionViewModel(id, service, new InlineDispatcher(), time);
        await session.OpenAsync();
        return (session, service, time);
    }

    [Fact]
    public async Task TheMainSessionDrawsMainPngsBlocksInOrder()
    {
        var (session, _, _) = await Open(Main);

        Assert.Equal(
        [
            TranscriptBlockKind.User, TranscriptBlockKind.TurnHeader, TranscriptBlockKind.Decisions, TranscriptBlockKind.Switch,
            TranscriptBlockKind.Tool, TranscriptBlockKind.Tool, TranscriptBlockKind.Tool, TranscriptBlockKind.Taint,
            TranscriptBlockKind.Tool, TranscriptBlockKind.Answer, TranscriptBlockKind.Thought,
        ], session.Blocks.Select(block => block.Kind));

        var user = (UserMessageBlock)session.Blocks[0];
        Assert.Equal(("you · 14:01", "the sandbox test fails on CI only. find out why and fix it without touching the production launcher."), (user.Who, user.Text));
        var header = (TurnHeaderBlock)session.Blocks[1];
        Assert.Equal(("corvid · turn 12", "qwen3.5-0.8b → claude · 1.2k tokens · 48 s · $0.04"), (header.Who, header.Detail));
        var chips = (DecisionsBlock)session.Blocks[2];
        Assert.Equal(["intent 0.94 → task", "difficulty 0.71 → escalate", "recall 0.88 → yes", "tool 0.91 → read_file", "clarify 0.41 → no, unsure"],
            chips.Chips.Select(chip => $"{chip.Question} {chip.Text}"));
        Assert.Equal([false, false, false, false, true], chips.Chips.Select(chip => chip.IsUnsure));
        Assert.Equal("Ingress checkpoint, Kev-0.8B", chips.AutomationName);
        var escalation = (SwitchBlock)session.Blocks[3];
        Assert.Equal(("escalated to", "claude", "step budget (difficulty 0.71)", "2 values redacted", true),
            (escalation.Lead, escalation.Model, escalation.Reason, escalation.Redaction, escalation.HasEgressLink));

        var cards = session.Blocks.OfType<ToolCardBlock>().ToList();
        Assert.Equal(["read_file|.github/workflows/ci.yml||auto: read-only · 0.3 s", "search_files|\"unshare\" in tests/|3 hits|auto: read-only · 0.8 s",
            "web_fetch|https://docs.example.org/bubblewrap/seccomp|48 KB|approved once · 1.9 s",
            "shell|dotnet test tests/Corvid.Sandbox.Tests --filter Seccomp||running · 12 s"],
            cards.Select(card => $"{card.ToolName}|{card.Target}|{card.Summary}|{card.StatusText}"));
        Assert.Equal([IconKind.File, IconKind.Search, IconKind.Globe, IconKind.Terminal], cards.Select(card => card.Icon));
        var shell = cards[3];
        Assert.True(shell.IsRunning);
        Assert.True(shell.HasTail);
        Assert.True(shell.StopCommand.CanExecute(null));
        Assert.Equal("ran under a session rule: dotnet test in workspace (approval a-31, turn 10) · risk 1 (0.93) · checkpoint c-44 taken before the call",
            SessionText.Plain(shell.Why));
        Assert.Equal(InlineStyle.Code, shell.Why[1].Style);
        Assert.False(cards[0].HasWhy);

        var taint = (TaintBlock)session.Blocks[7];
        Assert.Equal(("outside content in context", "web_fetch docs.example.org · network reads now ask; egress and new recipients offer only \"once\""),
            (taint.Lead, taint.Text));
        var answer = (AnswerBlock)session.Blocks[9];
        Assert.True(answer.IsStreaming);
        Assert.IsType<MarkdownParagraph>(answer.Blocks[0]);
        Assert.Equal(["1.", "2."], Assert.IsType<MarkdownList>(answer.Blocks[2]).Items.Select(item => item.Marker));
        Assert.Equal("thought for 4 s", ((ThoughtBlock)session.Blocks[10]).Label);
    }

    [Fact]
    public async Task TheTurnStripComposerStatusAndPanelsAreMainPngs()
    {
        var (session, _, _) = await Open(Main);

        Assert.Equal(("Fix the flaky sandbox test on CI", "fork of cli-8c11 at turn 9", "goal: keep CI green on main"), (session.Title, session.ForkText, session.GoalText));
        Assert.True(session.ShowTurnStrip);
        Assert.True(session.IsWorking);
        Assert.Equal("step 4 of 6 · shell running · 12 s", session.TurnStripText);
        // The live region's words leave out the ticking time, so a screen reader hears a step change, not every second.
        Assert.Equal("step 4 of 6 · shell running", session.TurnStripStatus);
        Assert.Equal(["queued|also check the retry count"], session.Queue.Select(chip => $"{chip.Label}|{chip.Text}"));
        Assert.Equal("Ctrl ↵ steers at the next checkpoint · Esc interrupts", string.Concat(session.Hint.Select(part => part.Text)));
        Assert.Equal([true, false, true, false, true, false], session.Hint.Select(part => part.IsKey));
        Assert.Equal("Ask Corvid…   / commands   @ files   ! shell   # memory", session.Placeholder);

        Assert.Equal("claude (escalated) · cuda:0 · q0 · 410 ms", session.StatusGenerator);
        Assert.Equal(("Kev-0.8B, calibrated", "38%", ApprovalMode.Smart), (session.StatusDecider, session.StatusContext, session.Mode));
        Assert.Equal(["tainted"], session.StatusTags.Select(tag => tag.Text));
        Assert.Equal(("here · 2 jobs", "main", "turn 48 s"), (session.StatusScheduler, session.StatusBranch, session.StatusTrailing));

        Assert.Equal(("2 files", "since turn 1 · 3 files", 3), (session.TurnFilesSummary, session.SessionFilesSummary, session.ChangesCount));
        Assert.Equal(["src/Corvid.Sandbox/BubblewrapSandbox.cs +12 −3", "tests/Corvid.Sandbox.Tests/SeccompTests.cs +4 −1"],
            session.TurnFiles.Select(file => $"{file.Path} {file.AddedText} {file.RemovedText}"));
        Assert.Equal("not readable by Corvid", session.SessionFiles[^1].Note);
        Assert.Equal(("38% of 200k", 38d), (session.ContextSummary, session.ContextPercent));
        Assert.Equal("system 2.1k · memory 1.4k · skills 0.9k · results 61k · stubs 3k · next compaction at 70%", session.ContextBreakdown);
        Assert.Equal(2, session.OpenTasks);
        Assert.Equal("pre-execute · 14:04:20", session.DecisionGroups[0].Header);
        Assert.Equal("risk → 2, ask", session.DecisionGroups[0].Rows[1].Label);
        Assert.Contains(session.Facts, fact => fact.Label == "fork" && fact.Value == "fork of cli-8c11 at turn 9");
    }

    [Fact]
    public async Task TheApprovalCardIsApprovalPngs()
    {
        var (session, _, _) = await Open(Main, DemoScene.Approval);

        var card = session.CurrentApproval!;
        Assert.Same(card, session.Blocks.OfType<ApprovalCardBlock>().Single());
        var index = session.Blocks.IndexOf(card);
        Assert.Equal("awaiting approval", ((ToolCardBlock)session.Blocks[index - 1]).StatusText);
        Assert.True(((ToolCardBlock)session.Blocks[index - 1]).IsAwaitingApproval);
        Assert.Equal(("Approval 1 of 2 · shell", "denied automatically in 4:12", "4:12 left"), (card.Title, card.DeadlineText, card.ShortDeadline));
        Assert.Equal("rm -rf build/ && dotnet test Corvid.slnx", card.CommandText);
        var rm = card.Commands[0];
        Assert.Equal(("rm -rf build/", "delete, recursive", Tone.Red, "rm", "-r -f"), (rm.Text, rm.EffectsText, rm.EffectsTone, rm.Verb, rm.Flags));
        Assert.Equal("./build/ · workspace · 212 files", rm.Targets.Single().Text);
        Assert.True(rm.Targets.Single().IsDestructive);
        Assert.Equal("checkpoint c-45 is taken first, so build/ is restorable", SessionText.Plain(rm.Notes.Single().Runs));
        var test = card.Commands[1];
        Assert.Equal(("execute", Tone.Grey, "test"), (test.EffectsText, test.EffectsTone, test.Subcommand));
        Assert.Equal(["Corvid.slnx · workspace", "writes under bin/ obj/ TestResults/"], test.Targets.Select(target => target.Text));
        Assert.Equal(["risk 3", "taint", "reversible"], card.Reasons.Select(reason => reason.Label));
        Assert.Equal(["1|Allow once", "2|This session", "3|Always…", "4|Deny", "5|Deny and say why"], card.Options.Select(option => $"{option.Key}|{option.Label}"));
        Assert.Equal("Next in the queue: patch_file src/Corvid.Sandbox/BubblewrapSandbox.cs (diff ready) · 2 of 2", SessionText.Plain(card.NextText));
        Assert.Equal(InlineStyle.Code, card.NextText[1].Style);

        Assert.Equal("step 2 of 6 · waiting on you · 2 approvals", session.TurnStripText);
        Assert.True(session.IsWaiting);
        Assert.Equal("Your draft is kept. ↵ focuses the card, a second ↵ answers it.", string.Concat(session.Hint.Select(part => part.Text)));
        Assert.Equal(["tainted", "2 approvals"], session.StatusTags.Select(tag => tag.Text));
        Assert.Equal(("turn 9 s", "41%"), (session.StatusTrailing, session.StatusContext));
        var header = session.Blocks.OfType<TurnHeaderBlock>().Last();
        Assert.Equal("claude · step 2 of 6 · 9 s", header.Detail);
        var summary = session.Blocks.OfType<DecisionsBlock>().Last();
        Assert.Equal(["tool 0.91 → shell", "risk 3 (0.86) → ask", "done 0.12 → no"], summary.Chips.Select(chip => $"{chip.Question} {chip.Text}"));
        Assert.Equal(["pre-execute · 14:04:12|Kev-0.8B · 240 ms", "step · 14:04:08|Kev-0.8B · 231 ms", "ingress · 14:03:41|Kev-0.8B · 258 ms"],
            session.DecisionGroups.Take(3).Select(group => $"{group.Header}|{group.Detail}"));
        Assert.Equal(("risk → 3, ask", "0.86 · acted", true), (session.DecisionGroups[0].Rows[1].Label, session.DecisionGroups[0].Rows[1].Value, session.DecisionGroups[0].Rows[1].IsAsk));
    }

    [Fact]
    public async Task EnterFocusesAnOpenCardFirstAndTheSecondEnterOnTheCardAnswersIt()
    {
        var (session, service, time) = await Open(Main, DemoScene.Approval);
        var focus = new List<SessionFocusTarget>();
        session.FocusRequested += (_, target) => focus.Add(target);
        session.Draft = "and keep the launcher untouched, the test is the only thing to change";
        var card = session.CurrentApproval!;

        Assert.Equal("typed while the card opened: it never answers the card", session.DraftNote);
        Assert.Equal("about 18 tokens", session.TokenEstimate);
        // Digits never reach a card that does not have focus.
        Assert.False(card.ChooseCommand.CanExecute("1"));

        session.SubmitCommand.Execute(null);

        Assert.True(card.IsFocused);
        Assert.Equal([SessionFocusTarget.Approval], focus);
        Assert.Equal("and keep the launcher untouched, the test is the only thing to change", session.Draft);
        Assert.Equal("a-32", service.StateOf(Main).CurrentApproval?.ApprovalId);
        Assert.Empty(service.StateOf(Main).Queue);
        Assert.True(card.Options[0].IsFocused);

        // Another Enter in the composer still only points at the card.
        session.SubmitCommand.Execute(null);
        Assert.Equal("a-32", service.StateOf(Main).CurrentApproval?.ApprovalId);

        card.MoveFocus(1);
        Assert.Equal("2", card.FocusedOption?.Key);
        card.ConfirmCommand.Execute(null);

        Assert.Equal("a-33", service.StateOf(Main).CurrentApproval?.ApprovalId);
        Assert.Equal(SessionFocusTarget.Composer, focus[^1]);
        time.Advance(5);
        var next = session.CurrentApproval!;
        Assert.Equal("a-33", next.ApprovalId);
        Assert.False(next.IsFocused);
        Assert.True(next.HasProposedDiff);
        Assert.Equal(("+2", "−0"), (next.ProposedDiff!.AddedText, next.ProposedDiff.RemovedText));
        var shell = session.Blocks.OfType<ToolCardBlock>().Single(block => block.CallId == "call-5");
        Assert.Equal("approved for this session · 1.8 s", shell.StatusText);
        Assert.Equal("approved for this session (rule: rm -r under build/ and dotnet test in the workspace) · checkpoint c-45 taken before the call",
            SessionText.Plain(shell.Why));
    }

    [Fact]
    public async Task AlwaysPreviewsTheRuleWhosePathsCanOnlyBeNarrowed()
    {
        var (session, service, _) = await Open(Main, DemoScene.Approval);
        var card = session.CurrentApproval!;

        card.Options.Single(option => option.Key == "3").Choose.Execute(null);

        Assert.True(card.IsRulePreviewOpen);
        Assert.True(card.Options[2].IsExpanded);
        var preview = card.RulePreview!;
        Assert.Equal(("Rule to be written · scope: always", "source: approval a-32, session cli-9f2e"), (preview.Header, preview.Source));
        Assert.Equal(["allow|rm -r|build/|", "allow|dotnet test||in the workspace, no network, no elevation"],
            preview.Rows.Select(row => $"{row.Decision}|{row.Command}|{row.Paths}|{row.Scope}"));

        preview.Rows[0].Paths = "src/";
        Assert.True(preview.Rows[0].HasPathsError);
        Assert.False(card.ConfirmRuleCommand.CanExecute(null));
        preview.Rows[0].Paths = "build/cache";
        Assert.False(preview.Rows[0].HasPathsError);

        card.ConfirmRuleCommand.Execute(null);

        var shell = service.StateOf(Main).Transcript.OfType<ToolEntry>().Single(tool => tool.CallId == "call-5");
        Assert.Equal((VerdictKind.ApprovedAlways, "`rm -r` under `build/cache` and `dotnet test` in the workspace"), (shell.Verdict!.Kind, shell.Verdict.Rule));
    }

    [Fact]
    public async Task DenyAndSayWhySendsTheReasonAsTheNextMessage()
    {
        var (session, service, time) = await Open(Main, DemoScene.Approval);
        var card = session.CurrentApproval!;

        card.Options.Single(option => option.Key == "5").Choose.Execute(null);
        Assert.True(card.IsAskingWhy);
        Assert.False(card.SendDenyReasonCommand.CanExecute(null));
        card.DenyReason = "do not delete build/, it holds the fixtures";
        card.SendDenyReasonCommand.Execute(null);

        var state = service.StateOf(Main);
        Assert.Equal("a-33", state.CurrentApproval?.ApprovalId);
        Assert.Equal(["do not delete build/, it holds the fixtures"], state.Queue.Select(message => message.Text));
        time.Advance(1);
        var shell = session.Blocks.OfType<ToolCardBlock>().Single(block => block.CallId == "call-5");
        Assert.Equal((ToolCardState.Denied, "denied"), (shell.State, shell.StatusText));
        Assert.Equal("denied by you: do not delete build/, it holds the fixtures", SessionText.Plain(shell.Why));
    }

    [Fact]
    public async Task EnterSendsWhenIdleQueuesWhileRunningAndCtrlEnterSteers()
    {
        var (session, service, time) = await Open(Vault);
        Assert.False(session.IsTicking);
        Assert.False(session.ShowTurnStrip);
        Assert.Equal("idle", session.StatusTrailing);

        session.Draft = "check the retry count";
        session.SubmitCommand.Execute(null);

        Assert.Equal("", session.Draft);
        Assert.True(session.IsRunning);
        Assert.True(session.IsTicking);
        time.Advance(0.4);

        session.Draft = "and the hosts";
        session.SubmitCommand.Execute(null);
        session.Draft = "only in ci.yml";
        session.SteerCommand.Execute(null);
        Assert.Equal(["queued|and the hosts", "steer|only in ci.yml"], session.Queue.Select(chip => $"{chip.Label}|{chip.Text}"));

        // Up on an empty composer takes the last one back.
        session.TakeBackLastCommand.Execute(null);
        Assert.Equal("only in ci.yml", session.Draft);
        Assert.Equal(["and the hosts"], session.Queue.Select(chip => chip.Text));
        session.SteerCommand.Execute(null);

        time.Advance(10);
        Assert.Contains(session.Blocks, block => block is UserMessageBlock { IsSteer: true, Text: "only in ci.yml" });
        // The turn ended and the queued message went next as turn 8.
        Assert.Contains(session.Blocks, block => block is TurnHeaderBlock { Number: 8 });
        time.Advance(10);
        Assert.False(session.IsRunning);
        Assert.False(session.IsTicking);
        Assert.Empty(session.Queue);
        Assert.Equal(8, service.StateOf(Vault).Turn);
    }

    [Fact]
    public async Task EscInterruptsKeepingTheWorkAndSlashCommandsRunFromTheComposer()
    {
        var (session, _, time) = await Open(Vault);

        session.Draft = "list the policies";
        session.SubmitCommand.Execute(null);
        time.Advance(0.7);
        Assert.True(session.InterruptCommand.CanExecute(null));
        session.InterruptCommand.Execute(null);

        Assert.False(session.IsRunning);
        Assert.Contains("interrupted", session.Blocks.OfType<TurnHeaderBlock>().Last().Detail, StringComparison.Ordinal);
        Assert.Equal(ToolCardState.Cancelled, session.Blocks.OfType<ToolCardBlock>().Last().State);

        session.Draft = "/goal ship the ntfy policy";
        session.SubmitCommand.Execute(null);
        Assert.Equal("goal: ship the ntfy policy", session.GoalText);
        Assert.Equal("/goal ship the ntfy policy", session.Blocks.OfType<CommandBlock>().Single().Line);
        Assert.False(SessionViewModel.IsCommandLine("/tmp/x is a path"));
        Assert.True(SessionViewModel.IsCommandLine("/compact keep the decisions"));
    }

    [Fact]
    public async Task TheVerboseToggleShowsEveryCheckpointAtItsPlace()
    {
        var (session, _, _) = await Open(Main);
        var tool = session.Blocks.OfType<ToolCardBlock>().First();

        session.ToggleDecisionsCommand.Execute(null);

        var rows = session.Blocks.OfType<DecisionsBlock>().ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.False(row.IsSummary));
        Assert.Same(tool, session.Blocks.OfType<ToolCardBlock>().First());
        session.ToggleDecisionsCommand.Execute(null);
        Assert.True(Assert.Single(session.Blocks.OfType<DecisionsBlock>()).IsSummary);
    }

    [Fact]
    public async Task StreamingUpdatesTheSameBlocksInPlace()
    {
        var (session, service, time) = await Open(Main);
        var answer = session.Blocks.OfType<AnswerBlock>().Single();
        var shell = session.Blocks.OfType<ToolCardBlock>().Last();
        var raised = new List<string?>();
        answer.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        await service.SteerAsync(Main, "keep the launcher untouched", TestContext.Current.CancellationToken);
        time.Advance(3);

        Assert.Same(answer, session.Blocks.OfType<AnswerBlock>().First());
        Assert.Same(shell, session.Blocks.OfType<ToolCardBlock>().Single(block => block.CallId == "call-4"));
        Assert.Contains(nameof(AnswerBlock.Document), raised);
        Assert.Equal(ToolCardState.Succeeded, shell.State);
        Assert.Equal("1 failed", shell.Summary);
        Assert.Contains(session.Blocks, block => block is UserMessageBlock { IsSteer: true });
        Assert.EndsWith("untouched.", answer.Text.TrimEnd(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadOnlySessionSaysWhoHoldsItAndTakesNoInput()
    {
        var (session, _, _) = await Open(DemoSessionService.TerminalSessionId);

        Assert.True(session.IsReadOnly);
        Assert.Equal("Read-only: open in a terminal (corvid chat --session cli-c3d2); take over once it lets go.", session.ReadOnlyNote);
        Assert.False(session.SubmitCommand.CanExecute(null));
        Assert.Contains(session.StatusTags, tag => tag.Text == "read-only");
    }

    [Fact]
    public async Task TheStashedDraftIsMainPngsDraftSaved()
    {
        var (session, _, _) = await Open(Main);

        session.StashDraft(Composition.AppComposition.MainSceneStash);

        Assert.Equal(("", "draft saved", "about 40 tokens"), (session.Draft, session.DraftNote, session.TokenEstimate));
        session.StashDraftCommand.Execute(null);
        Assert.Equal(Composition.AppComposition.MainSceneStash, session.Draft);
        Assert.Equal("", session.DraftNote);
    }
}
