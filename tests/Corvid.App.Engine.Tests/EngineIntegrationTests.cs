using Corvid.App.Core.Sessions;
using Corvid.Host.Tests;
using Corvid.Kernel.Turns;
using Corvid.Tools;
using Microsoft.Extensions.DependencyInjection;
using LoggedCall = Corvid.Abstractions.Events.ToolCallIssued;
using LoggedUserMessage = Corvid.Abstractions.Events.UserMessage;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// Where A4's groups meet on the engine: a clarifying question's answer and the composer's <c>!</c> and <c>#</c> lines
/// (turn and records), steering and the composer modes, and a list's open question read through the host's catalogue.
/// </summary>
public sealed class EngineIntegrationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ExtraTools(params ToolFunction[] tools) : IToolProvider
    {
        public IReadOnlyList<ToolFunction> CreateTools() => tools;
    }

    private static Task<EngineFixture> StartAsync(FakeGenerator generator, params ToolFunction[] tools) =>
        EngineFixture.StartAsync(new FakeModels(generator), new Dictionary<string, string?> { ["Corvid:Tools:Builtin"] = "true" },
            services => services.AddSingleton<IToolProvider>(new ExtraTools(tools)));

    [Fact]
    public async Task An_answer_that_reads_as_a_shell_line_is_refused_and_runs_nothing_and_a_memory_line_is_staged_with_the_question_left_open()
    {
        var generator = new FakeGenerator(
            Reply.Call("ask_user", """{"question":"What should I run before tagging?"}"""),
            Reply.Say("I will run the cleanup first."));
        // ask_user comes with the built-in tools.
        await using var fixture = await StartAsync(generator);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        await service.SendAsync(id, "tag the release", Ct);
        var question = await waiter.WaitAsync<QuestionAsked>();
        await service.IdleAsync();

        // M1: a ! line typed in the composer is not sent as the answer (its command would read as the person's words, or
        // run): refused, and the view keeps the text.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => service.AnswerQuestionAsync(id, question.QuestionId, "!rm -rf build", Ct));
        Assert.Contains("answer it first", refused.Message, StringComparison.Ordinal);
        // A # line is its mode: the fact is staged at once and the question still waits.
        await service.AnswerQuestionAsync(id, question.QuestionId, "# the release branch is main", Ct);
        var notice = await waiter.WaitAsync<Notice>(item => item.Kind == NoticeKind.MemoryStaged);
        Assert.StartsWith("Staged for review: \"the release branch is main\"", notice.Text, StringComparison.Ordinal);
        await service.IdleAsync();
        Assert.Single(generator.Calls);
        Assert.Equal(question.QuestionId, SessionFold.Replay(id, [.. subscription.History, .. waiter.Updates]).Question?.QuestionId);

        await service.AnswerQuestionAsync(id, question.QuestionId, "the cleanup script", Ct);
        await service.IdleAsync();
        Assert.Equal(2, generator.Calls.Count);
        var log = fixture.Runtime.Log.Read(id, cancellationToken: Ct).Select(item => item.Payload).ToList();
        Assert.Equal(["tag the release", "the cleanup script"], log.OfType<LoggedUserMessage>().Select(message => message.Text));
        Assert.DoesNotContain(log, payload => payload is LoggedCall { CallId: TurnRunner.UserShellCall });
        Assert.DoesNotContain(waiter.Updates, update => update is ApprovalRequested);
    }

    [Fact]
    public async Task A_memory_line_sent_with_ctrl_enter_is_staged_as_enter_stages_it()
    {
        var generator = new FakeGenerator();
        await using var fixture = await StartAsync(generator);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);

        Assert.True(((ISessionService)service).CanSteer);
        await service.SteerAsync(id, "# the release branch is main", Ct);

        var notice = await waiter.WaitAsync<Notice>(item => item.Kind == NoticeKind.MemoryStaged);
        Assert.StartsWith("Staged for review: \"the release branch is main\"", notice.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(fixture.Runtime.Log.Read(id, cancellationToken: Ct), item => item.Payload is LoggedUserMessage);
        Assert.Empty(generator.Calls);
    }
}
