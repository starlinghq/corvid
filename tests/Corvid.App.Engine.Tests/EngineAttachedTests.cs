using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Remote;
using Corvid.App.Core.Sessions;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Tests;
using Corvid.Kernel.Turns;
using Corvid.Tools;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// Attached mode over the real engine (G-1): the engine's session service behind <see cref="AttachedServer"/>, wired as
/// <c>corvid serve --attach</c> wires it (a <see cref="SerialDispatcher"/>, <see cref="EngineSessionService.AnswererPresent"/>
/// and <see cref="EngineSessionService.PutAsideInTurnQuestions"/>), with a client over an in-memory connection. An answer from
/// the client reaches the engine's turn, and a client that drops never leaves a turn waiting forever.
/// </summary>
public sealed class EngineAttachedTests
{
    private const string Owner = "owner-token-for-the-attached-engine-tests";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ExtraTools(params ToolFunction[] tools) : IToolProvider
    {
        public IReadOnlyList<ToolFunction> CreateTools() => tools;
    }

    /// <summary>The engine's session service served as <c>corvid serve --attach</c> serves it.</summary>
    private sealed class Served : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Task> _connections = [];
        private readonly string _directory = Directory.CreateTempSubdirectory("corvid-attach-").FullName;

        public Served(EngineFixture fixture)
        {
            Dispatcher = new SerialDispatcher("test: attached");
            Service = fixture.Sessions(Dispatcher);
            Server = new AttachedServer(Service, new AttachedAuthenticator((token, _) => ValueTask.FromResult<bool?>(token == Owner),
                Path.Combine(_directory, "devices.json")))
            {
                NoClientsLeft = Service.PutAsideInTurnQuestions,
            };
            Service.AnswererPresent = () => Server.Clients > 0;
        }

        public SerialDispatcher Dispatcher { get; }

        public EngineSessionService Service { get; }

        public AttachedServer Server { get; }

        public async Task<(RemoteSessionService Client, Task Connection)> ConnectAsync()
        {
            var (client, end) = InMemoryMessageChannel.CreatePair();
            var connection = Server.ServeAsync(end, "test", _stop.Token);
            lock (_connections)
                _connections.Add(connection);
            return (await RemoteSessionService.ConnectAsync(client, Owner, "test", new InlineDispatcher(), Ct), connection);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            Task[] connections;
            lock (_connections)
                connections = [.. _connections];
            await Task.WhenAll(connections).WaitAsync(TimeSpan.FromSeconds(30), Ct);
            Service.Dispose();
            Dispatcher.Dispose();
            _stop.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static Microsoft.Extensions.AI.ChatResponse Ask(string question) =>
        new(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant,
            [new Microsoft.Extensions.AI.FunctionCallContent("m0", "ask_user", new Dictionary<string, object?> { ["question"] = question })]));

    [Fact]
    public async Task AnApprovalAnsweredFromAnAttachedClientReachesTheEnginesTurn()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("add_note", """{"text":"water plants"}"""), Reply.Say("Added.")));
        await using var fixture = await EngineFixture.StartAsync(models);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        await using var served = new Served(fixture);
        var (client, _) = await served.ConnectAsync();
        await using var _client = client;
        var waiter = new SessionWaiter();
        using var subscription = await client.OpenAsync(id, waiter.Add, Ct);

        await client.SendAsync(id, "add a note: water plants", Ct);
        var request = await waiter.WaitAsync<ApprovalRequested>();
        Assert.Equal("add_note", request.Tool);
        // Nothing ran before the answer: the approval blocks the turn.
        Assert.DoesNotContain("water plants", models.Tools.Notes);

        await client.AnswerApprovalAsync(id, request.ApprovalId, new ApprovalAnswer(ApprovalChoice.Once), Ct);

        var resolved = await waiter.WaitAsync<ApprovalResolved>();
        Assert.Equal((request.ApprovalId, ApprovalOutcome.Approved), (resolved.ApprovalId, resolved.Outcome));
        Assert.Equal(TurnOutcome.Answered, (await waiter.WaitAsync<TurnEnded>()).Outcome);
        Assert.Contains("water plants", models.Tools.Notes);
    }

    [Fact]
    public async Task AQuestionTheTurnWaitsOnIsPutAsideWhenTheLastClientDropsAndTheNextAnswersIt()
    {
        var generator = new FakeGenerator(_ => Ask("Which branch?"), Reply.Say("Merged into main."));
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(generator), null,
            services => services.AddSingleton<IToolProvider>(new ExtraTools(new AskUserTool())));
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        await using var served = new Served(fixture);
        var (first, connection) = await served.ConnectAsync();
        var waiter = new SessionWaiter();
        var subscription = await first.OpenAsync(id, waiter.Add, Ct);

        await first.SendAsync(id, "merge the branch", Ct);
        var question = await waiter.WaitAsync<QuestionAsked>();
        // Asked inside the turn while a client is there: the turn waits for the answer.
        Assert.Equal(question.QuestionId, served.Service.QuestionWaiting(id));

        // The client goes (its network dropped) without answering.
        subscription.Dispose();
        await first.DisposeAsync();
        await connection.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        await served.Service.IdleAsync().WaitAsync(TimeSpan.FromSeconds(30), Ct);

        // The turn did not wait for it: it ended, its question left open for the next message (K-6).
        Assert.Null(served.Service.QuestionWaiting(id));
        var (second, _) = await served.ConnectAsync();
        await using var _second = second;
        var summary = (await second.ListAsync(Ct)).Single(item => item.Id == id);
        Assert.Equal(1, summary.PendingQuestions);
        var later = new SessionWaiter();
        using var reopened = await second.OpenAsync(id, later.Add, Ct);
        var view = SessionFold.Replay(id, reopened.History);
        Assert.Equal(question.QuestionId, view.Question?.QuestionId);
        Assert.Contains(reopened.History, update => update is TurnEnded);

        // The next client answers it: it goes as the next message, and the session runs on.
        await second.AnswerQuestionAsync(id, question.QuestionId, "main", Ct);
        Assert.Equal("main", (await later.WaitAsync<QuestionAnswered>()).Answer);
        await later.WaitAsync<TurnEnded>();
        Assert.Equal(2, generator.Calls.Count);
    }

    [Fact]
    public async Task WithNoClientAttachedAQuestionIsNotWaitedOnInsideTheTurn()
    {
        var generator = new FakeGenerator(_ => Ask("Which branch?"));
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(generator), null,
            services => services.AddSingleton<IToolProvider>(new ExtraTools(new AskUserTool())));
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        await using var served = new Served(fixture);
        var waiter = new SessionWaiter();
        using var watching = await served.Service.OpenAsync(id, waiter.Add, Ct);

        // A turn that runs with no client attached (a message sent before everyone left, a queued one starting).
        Assert.Equal(0, served.Server.Clients);
        await served.Service.SendAsync(id, "merge the branch", Ct);
        await waiter.WaitAsync<QuestionAsked>();
        Assert.Equal(TurnOutcome.Clarifying, (await waiter.WaitAsync<TurnEnded>()).Outcome);

        Assert.Null(served.Service.QuestionWaiting(id));
        var (second, _) = await served.ConnectAsync();
        await using var _second = second;
        Assert.Equal(1, (await second.ListAsync(Ct)).Single(item => item.Id == id).PendingQuestions);
    }

    [Fact]
    public async Task AnApprovalAClientLeftUnansweredIsDeniedAtItsDeadlineNotApproved()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("add_note", """{"text":"water plants"}"""), Reply.Say("Not added.")));
        await using var fixture = await EngineFixture.StartAsync(models);
        // The pipeline's deadline runs on the system clock; a short one keeps the test quick.
        fixture.Runtime.Approvals.SetTimeout(TimeSpan.FromSeconds(1));
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        await using var served = new Served(fixture);
        var engineSide = new SessionWaiter();
        using var watching = await served.Service.OpenAsync(id, engineSide.Add, Ct);
        var (client, connection) = await served.ConnectAsync();
        var waiter = new SessionWaiter();
        var subscription = await client.OpenAsync(id, waiter.Add, Ct);

        await client.SendAsync(id, "add a note: water plants", Ct);
        await waiter.WaitAsync<ApprovalRequested>();
        subscription.Dispose();
        await client.DisposeAsync();
        await connection.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        var resolved = await engineSide.WaitAsync<ApprovalResolved>();
        Assert.NotEqual(ApprovalOutcome.Approved, resolved.Outcome);
        await engineSide.WaitAsync<TurnEnded>();
        Assert.DoesNotContain("water plants", models.Tools.Notes);
    }
}
