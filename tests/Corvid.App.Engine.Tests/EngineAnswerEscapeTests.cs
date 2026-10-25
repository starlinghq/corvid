using Corvid.App.Core.Sessions;
using Corvid.Host.Tests;
using LoggedUserMessage = Corvid.Abstractions.Events.UserMessage;

namespace Corvid.App.Engine.Tests;

/// <summary>A4 verification: an answer typed as <c>\#…</c> is sent without its backslash, as any other message is.</summary>
public sealed class EngineAnswerEscapeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_escaped_answer_is_sent_without_its_backslash()
    {
        var generator = new FakeGenerator(
            Reply.Call("ask_user", """{"question":"Which issue?"}"""),
            Reply.Say("Looking at #123."));
        await using var fixture = await EngineFixture.StartAsync(new FakeModels(generator),
            new Dictionary<string, string?> { ["Corvid:Tools:Builtin"] = "true" });
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var subscription = await service.OpenAsync(id, waiter.Add, Ct);
        await service.SendAsync(id, "fix the failing issue", Ct);
        var question = await waiter.WaitAsync<QuestionAsked>();
        await service.IdleAsync();

        await service.AnswerQuestionAsync(id, question.QuestionId, @"\# the failing one is #123", Ct);
        await service.IdleAsync();

        var log = fixture.Runtime.Log.Read(id, cancellationToken: Ct).Select(item => item.Payload).OfType<LoggedUserMessage>();
        Assert.Equal(["fix the failing issue", "# the failing one is #123"], log.Select(message => message.Text));
    }
}
