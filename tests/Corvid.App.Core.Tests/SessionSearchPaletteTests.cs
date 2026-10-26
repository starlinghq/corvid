using Corvid.App.Core.Composition;
using Corvid.App.Core.Keys;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Palette;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

/// <summary>G-5 in the palette: Ctrl+Shift+F searches what was said, and a match opens its session at the matched turn.</summary>
public sealed class SessionSearchPaletteTests
{
    private const string Override = "‮";

    /// <summary>A search that answers from a list, or waits on a gate per query; it records what it was asked.</summary>
    private sealed class FakeSearch(params SessionMatch[] matches) : ISessionSearch
    {
        public List<string> Asked { get; } = [];

        public Dictionary<string, TaskCompletionSource<SessionMatches>> Gates { get; } = [];

        public string? Unavailable { get; init; }

        public Task<SessionMatches> SearchAsync(string query, CancellationToken cancellationToken = default)
        {
            Asked.Add(query);
            if (Gates.TryGetValue(query, out var gate))
                return gate.Task.WaitAsync(cancellationToken);
            return Task.FromResult(Unavailable is { } why ? new SessionMatches([], why)
                : new SessionMatches([.. matches.Where(match => match.Snippet.Contains(query, StringComparison.OrdinalIgnoreCase))]));
        }
    }

    private static AppComposition Compose()
    {
        Assert.True(AppOptions.TryParse(["--demo"], out var options, out var error), error);
        return new AppComposition(options, new InlineDispatcher(), ManualTimeProvider.DemoLocal());
    }

    private static PaletteViewModel Palette(AppComposition composition, ISessionSearch search, TimeSpan? delay = null) =>
        new(new PaletteHost(composition.Shell, new KeyMap(), composition.Time, new InlineDispatcher())
        {
            Search = search,
            SearchDelay = delay ?? TimeSpan.Zero,
            RevealAt = (session, position) => composition.Sessions.RevealAt(session, position),
        });

    private static SessionMatch Match(string session, string snippet, int turn = 2, long position = 7) =>
        new(session, "Draft release notes", position, turn, "you", DateTimeOffset.Parse("2026-10-24T09:30:00Z", System.Globalization.CultureInfo.InvariantCulture), snippet);

    [Fact]
    public void Typing_searches_what_was_said_and_a_match_opens_its_session_at_the_turn()
    {
        using var composition = Compose();
        var search = new FakeSearch(Match("cli-a410", "the zebra migration plan"), Match("cli-77b0", "zebra crossing notes", turn: 1, position: 1));
        var palette = Palette(composition, search);

        palette.Execute(KeyCommands.Search);
        Assert.Equal("Search sessions", palette.Title);
        Assert.Equal("", palette.Note);
        Assert.Empty(palette.Items);
        Assert.Equal("Type to search what was said in your sessions. Gateway chats (other people's messages) and scheduled job runs are left out.", palette.EmptyText);
        Assert.Empty(search.Asked);

        palette.Query = "zebra";

        Assert.Equal(["zebra"], search.Asked);
        Assert.Equal(["the zebra migration plan", "zebra crossing notes"], palette.Items.Select(item => item.Title));
        Assert.StartsWith("Draft release notes · turn 2 · you · ", palette.Items[0].Detail, StringComparison.Ordinal);

        palette.Accept();

        Assert.False(palette.IsOpen);
        Assert.Equal("cli-a410", composition.Shell.CurrentSession?.Id);
        Assert.Equal(Area.Sessions, composition.Shell.CurrentArea);
        Assert.Equal(("cli-a410", 7L), composition.Sessions.PendingPosition);
    }

    [Fact]
    public void A_newer_query_replaces_a_search_still_running()
    {
        using var composition = Compose();
        var search = new FakeSearch(Match("cli-a410", "the zebra migration plan"));
        var slow = new TaskCompletionSource<SessionMatches>();
        search.Gates["zeb"] = slow;
        var palette = Palette(composition, search);
        palette.Execute(KeyCommands.Search);

        palette.Query = "zeb";
        Assert.True(palette.IsLoading);
        palette.Query = "zebra";
        slow.SetResult(new SessionMatches([Match("cli-77b0", "zeb only")]));

        Assert.Equal(["the zebra migration plan"], palette.Items.Select(item => item.Title));
        Assert.False(palette.IsLoading);
    }

    [Fact]
    public async Task The_search_waits_for_typing_to_pause()
    {
        using var composition = Compose();
        var time = (ManualTimeProvider)composition.Time;
        var search = new FakeSearch(Match("cli-a410", "the zebra migration plan"));
        var palette = Palette(composition, search, TimeSpan.FromMilliseconds(250));
        palette.Execute(KeyCommands.Search);

        palette.Query = "z";
        palette.Query = "zebra";
        Assert.Empty(search.Asked);

        time.Advance(TimeSpan.FromMilliseconds(300));
        await WaitUntil(() => palette.Items.Count == 1);

        Assert.Equal(["zebra"], search.Asked);
    }

    [Fact]
    public void A_snippet_with_hidden_characters_is_shown_cleaned_and_a_session_off_the_rail_says_so()
    {
        using var composition = Compose();
        var search = new FakeSearch(Match("cli-gone", $"the {Override}zebra report"));
        var palette = Palette(composition, search);
        palette.Execute(KeyCommands.Search);

        palette.Query = "zebra";
        var row = Assert.Single(palette.Items);
        Assert.DoesNotContain(Override, row.Title, StringComparison.Ordinal);
        palette.Accept();

        Assert.True(palette.IsOpen);
        Assert.Contains("is not in the rail's list", palette.Note, StringComparison.Ordinal);
        Assert.Null(composition.Sessions.PendingPosition);
    }

    [Fact]
    public void Without_an_index_the_search_says_why()
    {
        using var composition = Compose();
        var palette = Palette(composition, new FakeSearch { Unavailable = "memory is off (Corvid:Memory:Enabled)" });
        palette.Execute(KeyCommands.Search);

        palette.Query = "anything";

        Assert.Empty(palette.Items);
        Assert.Equal(Tone.Amber, palette.NoteTone);
        Assert.Contains("Corvid:Memory:Enabled", palette.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reveal_at_a_position_lands_on_the_turns_prompt_and_waits_for_its_own_session()
    {
        using var composition = Compose();
        var (session, service, time) = await ModelSwitchViewTests.Open();
        var at = time.GetUtcNow();
        service.Push(new UserMessage("first question") { Position = 100, At = at });
        service.Push(new TurnStarted(30, "qwen3.5-0.8b") { At = at });
        service.Push(new AgentMessageChunk("an answer") { At = at });
        service.Push(new UserMessage("second question") { Position = 110, At = at });
        var sessions = composition.Sessions;

        // A position in another session is left for that one.
        sessions.RevealAt("cli-other", 105);
        Assert.Equal(-1, sessions.TakeRevealIndex(session));
        Assert.NotNull(sessions.PendingPosition);

        // A match inside the first of them (the answer, at 105) opens at its prompt, once.
        sessions.RevealAt(session.SessionId, 105);
        var index = sessions.TakeRevealIndex(session);
        var block = Assert.IsType<UserMessageBlock>(session.Blocks[index]);
        Assert.Equal("first question", block.Text);
        Assert.Equal(-1, sessions.TakeRevealIndex(session));

        sessions.RevealAt(session.SessionId, 110);
        Assert.Equal("second question", Assert.IsType<UserMessageBlock>(session.Blocks[sessions.TakeRevealIndex(session)]).Text);
        session.Dispose();
    }

    [Fact]
    public void The_demo_has_no_index_so_its_search_says_it_searches_titles()
    {
        using var composition = Compose();
        Assert.Null(composition.SessionSearch);

        composition.Palette.Execute(KeyCommands.Search);

        Assert.Contains("no episodic index here", composition.Palette.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("G-5", composition.Palette.Note, StringComparison.Ordinal);
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
            await Task.Yield();
        Assert.True(done());
    }
}
