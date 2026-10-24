using Corvid.App.Core.Sessions;
using Corvid.Host.Tests;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// The Sessions page's view model over the engine (docs/plan/ui.md F3 and F6): what the page binds to, driven as the page
/// drives it, with the runtime behind it rather than the demo.
/// </summary>
public sealed class EngineSessionViewTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheCardOpensOnTheComposersSendAndTheDigitAnswersIt()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("add_note", """{"text":"water plants"}"""), Reply.Say("Added.")));
        await using var fixture = await EngineFixture.StartAsync(models);
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        var waiter = new SessionWaiter();
        using var view = new SessionViewModel(id, service, new Corvid.App.Core.Mvvm.InlineDispatcher(), fixture.Time);
        await view.OpenAsync(Ct);
        // Subscribed after the view, so the view has every update the waiter has (subscribers hear them in order).
        using var watch = await service.OpenAsync(id, waiter.Add, Ct);
        Assert.Equal(Corvid.App.Core.Shell.ApprovalMode.Smart, view.State.Mode);
        Assert.Contains(view.State.Commands, command => command.Name == "compact");

        view.Draft = "add a note: water plants";
        view.SubmitCommand.Execute(null);
        await waiter.WaitAsync<ApprovalRequested>();

        // The card is the session's current approval; the first Enter only hands it the keys, the digit answers.
        var card = Assert.IsType<ApprovalCardBlock>(view.CurrentApproval);
        Assert.True(view.IsWaiting);
        view.Draft = "typed while it was open";
        view.SubmitCommand.Execute(null);
        Assert.True(card.IsFocused);
        Assert.Equal("typed while it was open", view.Draft);
        card.ChooseCommand.Execute("2");
        await waiter.WaitAsync<TurnEnded>();
        await service.IdleAsync();

        Assert.Null(view.CurrentApproval);
        Assert.False(view.IsRunning);
        Assert.Equal("", view.Error);
        Assert.Contains("water plants", models.Tools.Notes);
        Assert.Single(fixture.Runtime.Approvals.SessionRules(id));
        Assert.Contains(view.Blocks, block => block is ToolCardBlock);
        Assert.Contains(view.Blocks, block => block is AnswerBlock);
    }

    [Fact]
    public async Task AnUnbuiltRequestShowsItsReasonOnThePage()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var id = fixture.Runtime.OpenSession(surface: "app").Id;
        using var service = fixture.Sessions();
        using var view = new SessionViewModel(id, service, new Corvid.App.Core.Mvvm.InlineDispatcher(), fixture.Time);
        await view.OpenAsync(Ct);

        view.RenameCommand.Execute("a better title");
        await service.IdleAsync();

        Assert.StartsWith("Renaming comes with G-6:", view.Error, StringComparison.Ordinal);
    }
}
