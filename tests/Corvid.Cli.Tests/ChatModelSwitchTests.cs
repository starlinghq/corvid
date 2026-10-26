using Corvid.Cli.Chat;
using Corvid.Host.Commands;
using Corvid.Host.Sessions;
using Corvid.Host.Tests;

namespace Corvid.Cli.Tests;

/// <summary>A5: <c>/model</c> typed while a turn runs, the input router that keeps approval answers apart, and the switch notices.</summary>
public sealed class ChatModelSwitchTests
{
    private static readonly SlashCommandRegistry Commands = CoreCommands.Register(new SlashCommandRegistry());

    private static bool TakesMidTurn(string line) => ChatRenderer.TakesMidTurn(Commands, line);

    [Fact]
    public async Task ModelTypedDuringATurnSwitchesItAtItsNextStep()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("note_list"), Reply.Say("Done.")));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        models.Generator.Hold = (step, _) =>
        {
            if (step != 0)
                return null;
            started.TrySetResult();
            return release.Task;
        };
        using var run = new CliRun(models);
        var input = new ScriptedChatInput(["list my notes", "/exit"]).TypeDuringTurn("/model 2", started.Task);
        var exit = run.RunAsync(["chat"], input);
        // The switch is in once the reply says so; then the held first step answers and the second runs on the new model.
        // Without the mid-turn reader nothing asks for the line: fail rather than hang.
        await input.DuringTurnDrained.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        release.SetResult();

        Assert.Equal(ExitCodes.Success, await exit);
        var output = run.Output;
        Assert.Contains("generator: fake-gen@cpu → fake-gen-large@cpu for this session. The running turn takes it at its next step.", output, StringComparison.Ordinal);
        Assert.Contains("Hello from the larger model.", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Done.", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALineForAnOpenPromptIsNeverTakenForACommand()
    {
        var router = new LineRouter();
        var watched = router.WatchAsync(TakesMidTurn, TestContext.Current.CancellationToken);
        // An approval prompt opens while the turn's input is watched: the next line is its answer, even a /model line.
        var answer = router.ReadAsync(fresh: true, TestContext.Current.CancellationToken);
        router.Deliver("/model 2");
        Assert.Equal("/model 2", await answer);
        Assert.False(watched.IsCompleted);

        // With no prompt open, a message waits for the turn's end and /model is taken now.
        router.Deliver("tell me more");
        Assert.False(watched.IsCompleted);
        router.Deliver("/model decider fake-kev");
        Assert.Equal("/model decider fake-kev", await watched);
        Assert.Equal("tell me more", await router.ReadAsync(fresh: false, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWatchTheTurnEndsLosesNoLine()
    {
        var router = new LineRouter();
        using var turn = new CancellationTokenSource();
        var watched = router.WatchAsync(TakesMidTurn, turn.Token);
        await turn.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await watched);
        router.Deliver("/model 2");
        // Nobody watches now: the line is the next ordinary read's.
        Assert.Equal("/model 2", await router.ReadAsync(fresh: false, TestContext.Current.CancellationToken));
        router.Complete();
        Assert.Null(await router.ReadAsync(fresh: false, TestContext.Current.CancellationToken));
        Assert.Null(await router.WatchAsync(TakesMidTurn, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("/model 2", true)]
    [InlineData("  /models decider", true)]
    [InlineData("/status", false)]
    [InlineData("model 2", false)]
    [InlineData("/modelx", false)]
    [InlineData("/fork", true)]
    [InlineData("/agents", false)]
    public void OnlyCommandsThatRunBesideATurnAreTakenMidTurn(string line, bool taken) => Assert.Equal(taken, TakesMidTurn(line));

    [Fact]
    public void SlotsThatMoveOnTheirOwnAreSaidAndThePersonsOwnSwitchIsNot()
    {
        Assert.Null(ChatRenderer.ModelNotice(new ModelChanged("generator", "a", "b", "session", 4)));
        Assert.Null(ChatRenderer.ModelNotice(new ModelChanged("generator", "a", "a", "escalation", 4)));
        Assert.Null(ChatRenderer.ModelNotice(new ModelChanged("generator", "a", "b", "escalation", null)));
        Assert.Contains("generator escalated to b from a", ChatRenderer.ModelNotice(new ModelChanged("generator", "a", "b", "escalation", 4)), StringComparison.Ordinal);
        Assert.Contains("kept local", ChatRenderer.ModelNotice(new ModelChanged("generator", "cloud", "a", "privacy", 5)), StringComparison.Ordinal);
        Assert.Contains("back to a", ChatRenderer.ModelNotice(new ModelChanged("generator", "b", "a", "de_escalation", 6)), StringComparison.Ordinal);
        Assert.Contains("egress gate", ChatRenderer.ModelNotice(new ModelChanged("generator", "a", "c", "escalation", 7) { Cloud = true }), StringComparison.Ordinal);
        // Hidden characters in a configured id are shown, not obeyed, and markup in it is escaped.
        var notice = ChatRenderer.ModelNotice(new ModelChanged("generator", "a", "b‮[red]x", "fallback", 8))!;
        Assert.DoesNotContain("‮", notice, StringComparison.Ordinal);
        Assert.Contains("[[red]]", notice, StringComparison.Ordinal);
    }
}
