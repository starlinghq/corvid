using Corvid.Abstractions.Events;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Engine.PromptHistory;

namespace Corvid.App.Engine.Tests;

/// <summary>G-5 on the engine: the palette's search across sessions through the runtime's episodic index.</summary>
public sealed class EngineSessionSearchTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void Say(IEventLog log, string session, params SessionEvent[] events) =>
        log.Append(session, log.GetHead(session, Token), [.. events.Select(EventData.Create)], Token);

    [Fact]
    public async Task A_search_finds_what_was_said_with_the_turn_to_open_at()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var log = fixture.Runtime.Log;
        Say(log, "cli-a", new UserMessage("plan the release"), new AssistantMessage("Done."));
        Say(log, "cli-a", new UserMessage("now the walrus migration"), new AssistantMessage("The walrus plan is drafted."));
        Say(log, "acp-b", new UserMessage("walrus facts"));
        // The memory import's staging session holds someone's imported memories, not something said in a session.
        Say(log, Corvid.App.Engine.Memory.EngineMemoryService.ImportSession, new UserMessage("walrus memory"));
        var search = new EngineSessionSearch(fixture.Engine);

        var found = await search.SearchAsync("walrus", Token);

        Assert.Null(found.Unavailable);
        Assert.Equal(3, found.Matches.Count);
        Assert.DoesNotContain(found.Matches, match => match.SessionId == Corvid.App.Engine.Memory.EngineMemoryService.ImportSession);
        var prompt = log.Read("cli-a", cancellationToken: Token).Single(item => item.Payload is UserMessage { Text: "now the walrus migration" });
        Assert.All(found.Matches.Where(match => match.SessionId == "cli-a"), match =>
            Assert.Equal((2, prompt.Sequence), (match.Turn, match.TurnPosition)));
        Assert.Contains(found.Matches, match => match.SessionId == "cli-a" && match.Who == "Corvid" && match.Snippet == "The walrus plan is drafted.");
    }

    [Fact]
    public async Task Sessions_are_found_by_what_they_are_about()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var log = fixture.Runtime.Log;
        Say(log, "cli-a", new UserMessage("the integration tests hang under bubblewrap"), new AssistantMessage("Retried with a longer timeout."),
            new SessionTitled("Sandbox flake in CI", "generated"));
        Say(log, "cli-b", new UserMessage("write the release notes"), new AssistantMessage("Drafted."));
        Say(log, Corvid.App.Engine.Memory.EngineMemoryService.ImportSession, new UserMessage("sandbox flake memory"));
        var search = new EngineSessionSearch(fixture.Engine);

        var found = await search.FindSessionsAsync("sandbox flake", Token);

        Assert.NotNull(found);
        Assert.Null(found.Unavailable);
        // The import's staging session is not one of the person's sessions.
        Assert.DoesNotContain(found.Finds, item => item.SessionId == Corvid.App.Engine.Memory.EngineMemoryService.ImportSession);
        var find = found.Finds[0];
        Assert.Equal(("cli-a", "Sandbox flake in CI", "Retried with a longer timeout."), (find.SessionId, find.Title, find.Line));
    }

    [Fact]
    public async Task The_windows_palette_searches_through_the_engine()
    {
        await using var fixture = await EngineFixture.StartAsync();
        using var composition = new AppComposition(new AppOptions { Demo = false }, new InlineDispatcher(), fixture.Time,
            new EngineAppServices(fixture.Engine, fixture.Host.Root, _ => null));

        Assert.IsType<EngineSessionSearch>(composition.SessionSearch);
        composition.Palette.Execute(Corvid.App.Core.Keys.KeyCommands.Search);
        Assert.Equal("", composition.Palette.Note);
        Assert.Equal("Search what was said in your sessions…", composition.Palette.Placeholder);
    }
}
