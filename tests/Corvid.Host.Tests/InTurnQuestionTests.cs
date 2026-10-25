using Corvid.Abstractions.Events;
using Corvid.Host.Sessions;
using Corvid.Kernel.Sessions;
using Corvid.Kernel.Turns;

namespace Corvid.Host.Tests;

/// <summary>
/// A5 (A4 leftover): the question router. With a surface attached that can answer, <c>ask_user</c> waits inside the turn and
/// the answer is the call's result; with none, or put aside, the turn ends on the question as K-6 does.
/// </summary>
public sealed class InTurnQuestionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> BuiltinOn = new() { ["Corvid:Tools:Builtin"] = "true" };

    private sealed class Asker(Func<QuestionPrompt, string?> answer) : IQuestionAsker
    {
        public List<QuestionPrompt> Asked { get; } = [];

        public bool CanAsk(string sessionId) => true;

        public ValueTask<string?> AskAsync(QuestionPrompt prompt, CancellationToken cancellationToken)
        {
            Asked.Add(prompt);
            return ValueTask.FromResult(answer(prompt));
        }
    }

    /// <summary>Records, when the turn says it expects a question, whether the log holds it yet.</summary>
    private sealed class ExpectingAsker(Corvid.Host.Composition.CorvidRuntime runtime) : IQuestionAsker
    {
        public List<(QuestionPrompt Prompt, bool Logged)> Expected { get; } = [];

        public List<QuestionPrompt> Asked { get; } = [];

        public bool CanAsk(string sessionId) => true;

        public void Expect(QuestionPrompt prompt) =>
            Expected.Add((prompt, runtime.Log.Read(prompt.SessionId, limit: 4096).Any(item => item.Payload is ClarificationAsked asked
                && asked.QuestionId == prompt.QuestionId)));

        public ValueTask<string?> AskAsync(QuestionPrompt prompt, CancellationToken cancellationToken)
        {
            Asked.Add(prompt);
            return ValueTask.FromResult<string?>("release");
        }
    }

    [Fact]
    public async Task The_surface_is_told_to_expect_the_question_before_it_is_logged_and_then_asked_it()
    {
        // A5 integration: the app shows a question from the log, so it must be waiting for the answer before the question
        // is there; else an answer given as soon as it shows is refused as if the turn were not waiting.
        var models = Models();
        await using var host = TestHost.Create(models, BuiltinOn);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession(surface: "test");
        var asker = new ExpectingAsker(runtime);

        using (runtime.AttachQuestioner(session.Id, asker))
            await session.SendAsync("tag it", Ct);

        var (expected, logged) = Assert.Single(asker.Expected);
        Assert.False(logged);
        Assert.Equal(expected, Assert.Single(asker.Asked));
    }

    private static FakeModels Models() => new(new FakeGenerator(
        Reply.Call("ask_user", """{"question":"Which branch?","options":["main","release"]}"""), Reply.Say("Tagging release.")));

    [Fact]
    public async Task An_attached_surface_answers_inside_the_turn_and_the_question_is_closed()
    {
        var models = Models();
        await using var host = TestHost.Create(models, BuiltinOn);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession(surface: "test");
        var asker = new Asker(_ => "release");

        Delivery delivery;
        using (runtime.AttachQuestioner(session.Id, asker))
            delivery = await session.SendAsync("tag it", Ct);

        Assert.Equal(TurnStatus.Answered, delivery.Result!.Status);
        Assert.Equal("Tagging release.", delivery.Result.Text);
        var prompt = Assert.Single(asker.Asked);
        Assert.Equal("Which branch?", prompt.Text);
        Assert.Equal(["main", "release"], prompt.Options);
        var events = session.Snapshot().Transcript.Select(entry => entry.Content).ToList();
        var result = Assert.Single(events.OfType<ToolResult>());
        Assert.Equal(prompt.CallId, result.CallId);
        Assert.Contains("release", result.Result.GetRawText(), StringComparison.Ordinal);
        // The answer is the call's result, not a message: one turn, one user message.
        Assert.Single(events.OfType<UserMessage>());
        Assert.Equal(2, models.Generator.Calls.Count);

        var log = Corvid.Kernel.Events.SessionProjection.ReadRange(runtime.Log, session.Id, 0, runtime.Log.GetHead(session.Id, Ct), Ct)
            .Select(item => item.Payload).ToList();
        var asked = Assert.Single(log.OfType<ClarificationAsked>());
        Assert.Equal(new ClarificationAnswered(asked.QuestionId, "release"), Assert.Single(log.OfType<ClarificationAnswered>()));
        Assert.Null(SessionCatalogue.Describe(runtime.Log, session.Id, Ct)?.OpenQuestion);

        // Replay: the question and its close where they were logged.
        var replay = await SessionReplay.ReadAsync(runtime, session.Id, new ToolPresentation(runtime), cancellationToken: Ct);
        var updates = replay.Updates.ToList();
        var raised = updates.FindIndex(update => update is QuestionRaised);
        var closed = updates.FindIndex(update => update is QuestionClosed { Answer: "release" });
        Assert.True(raised >= 0 && raised < closed);
        Assert.True(closed < updates.FindIndex(update => update is AnswerChunk));
    }

    [Fact]
    public async Task Put_aside_or_with_nobody_attached_the_turn_ends_on_the_question()
    {
        foreach (var attach in new[] { true, false })
        {
            var models = Models();
            await using var host = TestHost.Create(models, BuiltinOn);
            await using var runtime = await host.StartAsync();
            var session = runtime.OpenSession(surface: "test");

            Delivery delivery;
            using (attach ? runtime.AttachQuestioner(session.Id, new Asker(_ => null)) : null)
                delivery = await session.SendAsync("tag it", Ct);

            Assert.Equal(TurnStatus.Clarifying, delivery.Result!.Status);
            Assert.Equal("Which branch?", delivery.Result.Question?.Text);
            Assert.Single(models.Generator.Calls);
            Assert.Equal("Which branch?", SessionCatalogue.Describe(runtime.Log, session.Id, Ct)?.OpenQuestion?.Text);
        }
    }

    [Fact]
    public async Task An_interrupt_racing_the_question_withdraws_it_even_when_the_surface_returns_no_answer()
    {
        // A5 fix round: an interrupt that reaches the surface before the turn's token is cancelled (the app sets its cancelling
        // flag first) must not leave the question open for the next message: the turn ends cancelled and the question goes.
        var models = Models();
        await using var host = TestHost.Create(models, BuiltinOn);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession(surface: "test");
        var asker = new Asker(_ =>
        {
            session.Cancel();
            return null;
        });

        Delivery delivery;
        using (runtime.AttachQuestioner(session.Id, asker))
            delivery = await session.SendAsync("tag it", Ct);

        Assert.Equal(TurnStatus.Cancelled, delivery.Result!.Status);
        Assert.Single(asker.Asked);
        var log = Corvid.Kernel.Events.SessionProjection.ReadRange(runtime.Log, session.Id, 0, runtime.Log.GetHead(session.Id, Ct), Ct)
            .Select(item => item.Payload).ToList();
        var asked = Assert.Single(log.OfType<ClarificationAsked>());
        Assert.Equal(new ClarificationAnswered(asked.QuestionId, ""), Assert.Single(log.OfType<ClarificationAnswered>()));
        Assert.Null(SessionCatalogue.Describe(runtime.Log, session.Id, Ct)?.OpenQuestion);
        Assert.Single(models.Generator.Calls);
    }
}
