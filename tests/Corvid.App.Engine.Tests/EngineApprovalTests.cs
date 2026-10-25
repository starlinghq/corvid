using System.Net;
using System.Text;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Tests;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Composition;
using Corvid.Host.Tests;
using Corvid.Models;
using Microsoft.Extensions.DependencyInjection;
using SessionState = Corvid.App.Core.Shell.SessionState;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// A host on fake models with settings or services of its own (built-in file tools, a fake web), wrapped as the window's
/// engine, in a temporary directory.
/// </summary>
internal sealed class CustomEngine : IAsyncDisposable
{
    private CustomEngine(TestHost host, CorvidRuntime runtime)
    {
        Host = host;
        Runtime = runtime;
        Engine = AppEngine.ForRuntime(host.Host, runtime);
    }

    public TestHost Host { get; }

    public CorvidRuntime Runtime { get; }

    public AppEngine Engine { get; }

    public ManualTimeProvider Time { get; } = ManualTimeProvider.Demo();

    public static async Task<CustomEngine> StartAsync(FakeModels models, IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null)
    {
        var host = EngineHosts.Create(models, settings, services);
        return new CustomEngine(host, await host.StartAsync());
    }

    public EngineSessionService Sessions(IDispatcher? dispatcher = null) => new(Engine, dispatcher ?? new InlineDispatcher(), Time);

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        await Host.DisposeAsync();
    }
}

/// <summary>
/// Approvals on the window's card (docs/plan/ui.md §4.4, F3): a turn's prompt arrives as a card after its call, with the
/// pipeline's analysis, reasons, scopes and the rule an answer would write; the card's answer goes back to the pipeline;
/// the rail and the Board see the session wait; and a prompt nobody answers is never approved on the person's behalf.
/// </summary>
public sealed class EngineApprovalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static FakeModels AddNote(params Func<IReadOnlyList<Microsoft.Extensions.AI.ChatMessage>, Microsoft.Extensions.AI.ChatResponse>[] more) =>
        new(new FakeGenerator([Reply.Call("add_note", """{"text":"water plants"}"""), Reply.Say("Added."), .. more]));

    [Fact]
    public async Task APromptArrivesAsACardAfterItsCallAndASessionAnswerWritesASessionRule()
    {
        var models = AddNote();
        await using var fixture = await EngineFixture.StartAsync(models);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var summaries = new List<SessionSummary>();
        service.SessionChanged += (_, summary) =>
        {
            lock (summaries)
                summaries.Add(summary);
        };
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        await service.SendAsync(id, "add a note: water plants", Ct);
        var request = await waiter.WaitAsync<ApprovalRequested>();

        // The call is announced pending before the card names it.
        var updates = waiter.Updates.ToList();
        var call = Assert.Single(updates.OfType<ToolCall>(), announced => announced.CallId == request.CallId);
        Assert.Equal(ToolCallStatus.Pending, call.Status);
        Assert.True(updates.IndexOf(call) < updates.IndexOf(request));
        Assert.Equal("add_note", request.Tool);
        Assert.Equal([ApprovalScope.Once, ApprovalScope.Session, ApprovalScope.Always], request.Offered);
        Assert.Contains(request.Reasons, reason => reason.Code == "risk" && reason.Label == "risk");
        var parsed = Assert.Single(request.Commands);
        Assert.Equal("add_note", parsed.Verb);
        Assert.Contains("remote", parsed.Effects);
        var rule = Assert.Single(request.Rules);
        Assert.Equal(("allow", "add_note"), (rule.Decision, rule.Command));

        await service.AnswerApprovalAsync(id, request.ApprovalId, new ApprovalAnswer(ApprovalChoice.Session), Ct);
        await service.IdleAsync();

        var resolved = Assert.Single(waiter.Updates.OfType<ApprovalResolved>());
        Assert.Equal((request.ApprovalId, ApprovalOutcome.Approved, ApprovalScope.Session), (resolved.ApprovalId, resolved.Outcome, resolved.Scope));
        Assert.Equal("`add_note`", resolved.Rule);
        Assert.Single(fixture.Runtime.Approvals.SessionRules(id));
        Assert.Contains("water plants", models.Tools.Notes);
        Assert.Equal(TurnOutcome.Answered, waiter.Updates.OfType<TurnEnded>().Single().Outcome);
        // The card folds into the tool card's verdict; nothing is left open.
        var view = SessionFold.Replay(id, [.. subscription.History, .. waiter.Updates]);
        Assert.Empty(view.Approvals);

        // The rail and the Board saw the session wait on the person, with the card's deadline, then go idle.
        lock (summaries)
        {
            var waiting = Assert.Single(summaries, summary => summary.State == SessionState.Waiting);
            Assert.Equal((1, request.Text, request.Deadline, "waiting on you"), (waiting.PendingApprovals, waiting.Approval, waiting.Deadline, waiting.Activity));
            Assert.Equal(1, waiting.Attention);
            Assert.Equal(SessionState.Idle, summaries[^1].State);
            Assert.Equal(0, summaries[^1].PendingApprovals);
        }

        // An answer to a prompt that has closed changes nothing.
        await service.AnswerApprovalAsync(id, request.ApprovalId, ApprovalAnswer.Deny, Ct);
        Assert.Single(waiter.Updates.OfType<ApprovalResolved>());
    }

    [Fact]
    public async Task ADenialsReasonIsQueuedAndSentAsTheNextMessage()
    {
        var models = AddNote(Reply.Say("Understood, no note."));
        await using var fixture = await EngineFixture.StartAsync(models);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        await service.SendAsync(id, "add a note: water plants", Ct);
        var request = await waiter.WaitAsync<ApprovalRequested>();
        await service.AnswerApprovalAsync(id, request.ApprovalId, new ApprovalAnswer(ApprovalChoice.Deny, " not now "), Ct);
        await waiter.WaitAsync<TurnEnded>(ended => ended.Turn == 2);
        await service.IdleAsync();

        var resolved = Assert.Single(waiter.Updates.OfType<ApprovalResolved>());
        Assert.Equal((ApprovalOutcome.Denied, "not now"), (resolved.Outcome, resolved.Reason));
        Assert.DoesNotContain("water plants", models.Tools.Notes);
        Assert.Contains(waiter.Updates.OfType<QueueChanged>(), queue => queue.Items is [{ Text: "not now" }]);
        Assert.Equal(["add a note: water plants", "not now"], waiter.Updates.OfType<UserMessage>().Select(message => message.Text));
        Assert.Empty(waiter.Updates.OfType<QueueChanged>().Last().Items);
    }

    [Fact]
    public async Task AReopenedSessionShowsEachAnswerAsTheLiveCardClosedIt()
    {
        // Turn 1's call is denied with a reason, which turn 2 sends; turn 2 calls again, and that call is allowed once.
        var models = AddNote(Reply.Say("Understood, no note."), Reply.Call("add_note", """{"text":"water plants"}"""), Reply.Say("Added."));
        await using var fixture = await EngineFixture.StartAsync(models);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        await service.SendAsync(id, "add a note: water plants", Ct);
        var first = await waiter.WaitAsync<ApprovalRequested>();
        await service.AnswerApprovalAsync(id, first.ApprovalId, new ApprovalAnswer(ApprovalChoice.Deny, "do not write notes"), Ct);
        var second = await waiter.WaitAsync<ApprovalRequested>(request => request.ApprovalId != first.ApprovalId);
        await service.AnswerApprovalAsync(id, second.ApprovalId, ApprovalAnswer.Once, Ct);
        await waiter.WaitAsync<TurnEnded>(ended => ended.Turn == 2);
        await service.IdleAsync();

        static List<string> Calls(SessionView view) =>
        [
            .. view.Transcript.OfType<ToolEntry>().Select(tool => $"{tool.Tool} {tool.Status} {tool.Outcome} {tool.Verdict?.Kind} {tool.Verdict?.Detail} {tool.Verdict?.ApprovalId}"),
        ];
        var shown = Calls(SessionFold.Replay(id, [.. subscription.History, .. waiter.Updates]));
        Assert.Equal(
        [
            $"add_note Failed Denied Denied do not write notes {first.ApprovalId}",
            $"add_note Completed Succeeded ApprovedOnce  {second.ApprovalId}",
        ], shown);

        // A fresh service has no channel for the session: its history is the log's, and it folds to the same cards.
        using var fresh = fixture.Sessions();
        using var reopened = await fresh.OpenAsync(id, _ => { }, Ct);
        Assert.Equal(shown, Calls(SessionFold.Replay(id, reopened.History)));
    }

    [Fact]
    public async Task InterruptingAWaitingTurnCancelsItsPromptAndTheQueueGoesNext()
    {
        var models = AddNote(Reply.Say("The queued one."));
        await using var fixture = await EngineFixture.StartAsync(models);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        await service.SendAsync(id, "add a note: water plants", Ct);
        var request = await waiter.WaitAsync<ApprovalRequested>();
        // Enter while the turn runs queues; Send does the same.
        var queued = await service.QueueAsync(id, "then list my notes", Ct);
        Assert.Equal("then list my notes", queued.Text);
        await service.InterruptAsync(id, Ct);

        var resolved = await waiter.WaitAsync<ApprovalResolved>();
        Assert.Equal((request.ApprovalId, ApprovalOutcome.Cancelled), (resolved.ApprovalId, resolved.Outcome));
        await waiter.WaitAsync<TurnEnded>(ended => ended.Turn == 2);
        await service.IdleAsync();

        var first = waiter.Updates.OfType<TurnEnded>().First();
        Assert.Equal((1, TurnOutcome.Cancelled), (first.Turn, first.Outcome));
        Assert.DoesNotContain("water plants", models.Tools.Notes);
        Assert.Equal(["add a note: water plants", "then list my notes"], waiter.Updates.OfType<UserMessage>().Select(message => message.Text));
        // An interrupt with nothing running does nothing.
        await service.InterruptAsync(id, Ct);
    }

    [Fact]
    public async Task TheQueueIsReorderedRemovedAndTakenBackWhileTheTurnWaits()
    {
        var models = AddNote(Reply.Say("Third."));
        await using var fixture = await EngineFixture.StartAsync(models);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        await service.SendAsync(id, "add a note: water plants", Ct);
        var request = await waiter.WaitAsync<ApprovalRequested>();
        var a = await service.QueueAsync(id, "a", Ct);
        var b = await service.QueueAsync(id, "b", Ct);
        var c = await service.QueueAsync(id, "c", Ct);
        await service.MoveQueuedAsync(id, c.Id, 0, Ct);
        await service.RemoveQueuedAsync(id, b.Id, Ct);
        Assert.Equal("a", await service.TakeBackQueuedAsync(id, a.Id, Ct));
        Assert.Null(await service.TakeBackQueuedAsync(id, a.Id, Ct));
        // Compaction runs between turns, never under a running one.
        var compacting = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompactAsync(id, null, Ct));
        Assert.Equal("Compaction runs between turns; this one is still running.", compacting.Message);

        Assert.Equal(["c"], waiter.Updates.OfType<QueueChanged>().Last().Items.Select(item => item.Text));
        var full = waiter.Updates.OfType<QueueChanged>().Where(queue => queue.Items.Length == 3).Select(queue => string.Join(" ", queue.Items.Select(item => item.Text)));
        // In sending order as queued, then with c moved to the front.
        Assert.Equal(["a b c", "c a b"], full);

        await service.AnswerApprovalAsync(id, request.ApprovalId, ApprovalAnswer.Once, Ct);
        await waiter.WaitAsync<TurnEnded>(ended => ended.Turn == 2);
        await service.IdleAsync();
        Assert.Equal(["add a note: water plants", "c"], waiter.Updates.OfType<UserMessage>().Select(message => message.Text));
        Assert.Contains("water plants", models.Tools.Notes);
        Assert.Equal(ApprovalScope.Once, waiter.Updates.OfType<ApprovalResolved>().Single().Scope);
        Assert.Empty(fixture.Runtime.Approvals.SessionRules(id));
    }

    private static readonly Dictionary<string, string?> FileTools = new()
    {
        ["Corvid:Tools:Builtin"] = "true",
        ["Corvid:Models:Slots:embedder.text:Model"] = "file-embed",
    };

    private static FakeModels FileWrite(string path)
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("write_file", $$"""{"path":"{{path}}","content":"hello\n"}"""), Reply.Say("Wrote it.")));
        // An embedder that ranks write_file first for "write ..." requests, so the shortlist offers it.
        models.Provider.Add("file-embed", [ModelSlots.TextEmbedder], () => new KeywordEmbedder("write", "save"), context: 512, tools: ToolCallSupport.None);
        return models;
    }

    [Fact]
    public async Task AnAlwaysAnswerWritesThePreviewedRuleOnlyWhenItsPathsAreNarrower()
    {
        await using var engine = await CustomEngine.StartAsync(FileWrite("notes.txt"), FileTools);
        var id = engine.Runtime.OpenSession(surface: "app").Id;
        using var service = engine.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        await service.SendAsync(id, "write hello to the file notes.txt", Ct);
        var request = await waiter.WaitAsync<ApprovalRequested>();

        var file = Path.Combine(engine.Host.Root, "work", "notes.txt");
        // The card shows what would be written, from the in-memory action, and the rule with the path it covers.
        Assert.Equal(new FileEdit("notes.txt", null, "hello\n"), request.ProposedDiff);
        var target = Assert.Single(Assert.Single(request.Commands).Targets);
        Assert.Equal((file, "path", "inside"), (target.Value, target.Kind, target.Location));
        var rule = Assert.Single(request.Rules);
        Assert.Equal([file], rule.Paths);

        // A narrowing that widens is refused and the card stays open.
        var wider = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnswerApprovalAsync(id, request.ApprovalId,
            new ApprovalAnswer(ApprovalChoice.Always) { RulePaths = [[Path.Combine(engine.Host.Root, "work")]] }, Ct));
        Assert.Contains("a rule can only be narrowed", wider.Message, StringComparison.Ordinal);
        Assert.Empty(waiter.Updates.OfType<ApprovalResolved>());

        await service.AnswerApprovalAsync(id, request.ApprovalId, new ApprovalAnswer(ApprovalChoice.Always) { RulePaths = [[file]] }, Ct);
        await service.IdleAsync();

        var resolved = Assert.Single(waiter.Updates.OfType<ApprovalResolved>());
        Assert.Equal((ApprovalOutcome.Approved, ApprovalScope.Always, $"`write_file` under {file}"), (resolved.Outcome, resolved.Scope, resolved.Rule));
        Assert.Equal([file], Assert.Single(engine.Runtime.Approvals.AlwaysRules).Paths);
        Assert.True(File.Exists(Path.Combine(engine.Host.Root, "approvals.json")));
        Assert.Equal("hello\n", await File.ReadAllTextAsync(file, Ct));
    }

    [Fact]
    public async Task ARuleWithAHiddenCharacterIsNeverWritten()
    {
        await using var engine = await CustomEngine.StartAsync(FileWrite("notes​.txt"), FileTools);
        var id = engine.Runtime.OpenSession(surface: "app").Id;
        using var service = engine.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        await service.SendAsync(id, "write hello to the file notes.txt", Ct);
        var request = await waiter.WaitAsync<ApprovalRequested>();
        // The card gets the text as it is, for the page to reveal; the engine never cleans it away.
        Assert.Contains("​", request.Text, StringComparison.Ordinal);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AnswerApprovalAsync(id, request.ApprovalId, new ApprovalAnswer(ApprovalChoice.Always), Ct));
        Assert.Contains("hidden characters", refused.Message, StringComparison.Ordinal);
        // A session rule is a standing grant too.
        var forSession = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AnswerApprovalAsync(id, request.ApprovalId, new ApprovalAnswer(ApprovalChoice.Session), Ct));
        Assert.Contains("hidden characters", forSession.Message, StringComparison.Ordinal);
        await service.AnswerApprovalAsync(id, request.ApprovalId, ApprovalAnswer.Deny, Ct);
        await service.IdleAsync();

        Assert.Equal(ApprovalOutcome.Denied, Assert.Single(waiter.Updates.OfType<ApprovalResolved>()).Outcome);
        Assert.Empty(engine.Runtime.Approvals.AlwaysRules);
        Assert.Empty(engine.Runtime.Approvals.SessionRules(id));
    }

    [Fact]
    public async Task ASessionsApprovalModeIsItsOwnAndReportedOnItsStream()
    {
        var models = AddNote();
        await using var fixture = await EngineFixture.StartAsync(models);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        var other = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
        Assert.Contains(new ApprovalModeChanged(Corvid.App.Core.Shell.ApprovalMode.Smart), subscription.History.Select(update => update with { At = default }));

        await service.SetApprovalModeAsync(id, Corvid.App.Core.Shell.ApprovalMode.Manual, Ct);

        Assert.Equal(Corvid.App.Core.Shell.ApprovalMode.Manual, waiter.Updates.OfType<ApprovalModeChanged>().Single().Mode);
        Assert.True(fixture.Runtime.TryGetSession(id, out var session));
        Assert.Equal(Corvid.Approvals.ApprovalMode.Manual, session.ApprovalMode);
        Assert.True(fixture.Runtime.TryGetSession(other, out var untouched));
        Assert.Equal(Corvid.Approvals.ApprovalMode.Smart, untouched.ApprovalMode);

        await service.SendAsync(id, "add a note: water plants", Ct);
        var request = await waiter.WaitAsync<ApprovalRequested>();
        Assert.Contains(request.Reasons, reason => reason.Code == "mode");
        await service.AnswerApprovalAsync(id, request.ApprovalId, ApprovalAnswer.Deny, Ct);
        await service.IdleAsync();
    }

    [Fact]
    public async Task OutsideContentTaintsTheSessionAndTheUsersNextMessageClearsIt()
    {
        var generator = new FakeGenerator(Reply.Call("web_fetch", """{"url":"http://seed.example/page"}"""), Reply.Say("The page says hello."),
            Reply.Say("Hello to you."));
        await using var engine = await CustomEngine.StartAsync(new FakeModels(generator), new Dictionary<string, string?> { ["Corvid:Tools:Builtin"] = "true" },
            services => services.AddSingleton(new WebFetchTransport(new PageHandler())));
        var id = engine.Runtime.OpenSession(surface: "app").Id;
        using var service = engine.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        await service.SendAsync(id, "fetch the page and report what it says", Ct);
        await waiter.WaitAsync<TurnEnded>();
        await service.IdleAsync();

        var tainted = Assert.Single(waiter.Updates.OfType<TaintChanged>());
        Assert.True(tainted.Tainted);
        Assert.Equal("web_fetch http://seed.example/page", tainted.Source);
        Assert.True(SessionFold.Replay(id, [.. subscription.History, .. waiter.Updates]).Tainted);
        var summary = Assert.Single(await service.ListAsync(Ct), item => item.Id == id);
        Assert.True(summary.Tainted);

        await service.SendAsync(id, "hello", Ct);
        await waiter.WaitAsync<TurnEnded>(ended => ended.Turn == 2);
        await service.IdleAsync();
        Assert.False(waiter.Updates.OfType<TaintChanged>().Last().Tainted);
        Assert.False(SessionFold.Replay(id, [.. subscription.History, .. waiter.Updates]).Tainted);
    }

    private sealed class PageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("<html><body><p>hello</p></body></html>", Encoding.UTF8, "text/html"),
            });
    }
}
