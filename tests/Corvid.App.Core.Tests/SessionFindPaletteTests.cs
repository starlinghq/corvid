using Corvid.App.Core.Composition;
using Corvid.App.Core.Keys;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Palette;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

/// <summary>C-044 in the palette: Ctrl+Shift+F lists the sessions a search is about above what was said in them.</summary>
public sealed class SessionFindPaletteTests
{
    /// <summary>A search with a session index: turns from one list, sessions from another, or memory off.</summary>
    private sealed class FindingSearch(SessionMatch[] matches, SessionFind[] finds, string? embedder = "e5") : ISessionSearch
    {
        public string? Unavailable { get; init; }

        public bool FailFinding { get; init; }

        public Task<SessionMatches> SearchAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(Unavailable is { } why ? new SessionMatches([], why) : new SessionMatches(matches));

        public Task<SessionFinds?> FindSessionsAsync(string query, CancellationToken cancellationToken = default)
        {
            if (FailFinding)
                throw new InvalidOperationException("the session index is locked");
            return Task.FromResult<SessionFinds?>(Unavailable is { } why ? new SessionFinds([], null, why) : new SessionFinds(finds, embedder));
        }
    }

    private static AppComposition Compose()
    {
        Assert.True(AppOptions.TryParse(["--demo"], out var options, out var error), error);
        return new AppComposition(options, new InlineDispatcher(), ManualTimeProvider.DemoLocal());
    }

    private static PaletteViewModel Palette(AppComposition composition, ISessionSearch search) =>
        new(new PaletteHost(composition.Shell, new KeyMap(), composition.Time, new InlineDispatcher())
        {
            Search = search,
            SearchDelay = TimeSpan.Zero,
            Sessions = composition.SessionService,
            RevealAt = (session, position) => composition.Sessions.RevealAt(session, position),
        });

    private static SessionMatch Said(string session, string snippet) =>
        new(session, "Draft release notes", 7, 2, "you", DateTimeOffset.Parse("2026-10-24T09:30:00Z", System.Globalization.CultureInfo.InvariantCulture), snippet);

    [Fact]
    public void Sessions_found_are_listed_above_what_was_said_and_one_opens_at_its_head()
    {
        using var composition = Compose();
        var search = new FindingSearch([Said("cli-a410", "the flaky test again")],
        [
            new SessionFind(DemoSessionService.MainSessionId, "Fix the flaky sandbox test on CI", "Retried with a longer timeout.", 0.03, ByMeaning: true),
            new SessionFind(DemoSessionService.VaultSessionId, "Vault policy for ntfy", null, 0.01, ByMeaning: false),
        ]);
        var palette = Palette(composition, search);
        palette.Execute(KeyCommands.Search);

        palette.Query = "the sandbox flake one";

        Assert.Equal(["Fix the flaky sandbox test on CI", "Vault policy for ntfy", "the flaky test again"], palette.Items.Select(item => item.Title));
        Assert.Equal([PaletteViewModel.SessionsGroup, "", PaletteViewModel.SaidGroup], palette.Items.Select(item => item.GroupHeader));
        Assert.Equal("Retried with a longer timeout. · corvid · by meaning", palette.Items[0].Detail);
        Assert.Equal("corvid-vault · by words", palette.Items[1].Detail);

        palette.Accept();

        Assert.False(palette.IsOpen);
        Assert.Equal(DemoSessionService.MainSessionId, composition.Shell.CurrentSession?.Id);
        Assert.Equal(Area.Sessions, composition.Shell.CurrentArea);
        // A session opens where it is now, not at a turn.
        Assert.Null(composition.Sessions.PendingPosition);
    }

    [Fact]
    public void At_most_five_sessions_lead_the_list()
    {
        using var composition = Compose();
        var finds = Enumerable.Range(0, 8).Select(i => new SessionFind($"cli-{i}", $"Session {i}", null, 1.0 / (i + 1), ByMeaning: false)).ToArray();
        var palette = Palette(composition, new FindingSearch([], finds, embedder: null));
        palette.Execute(KeyCommands.Search);

        palette.Query = "session";

        Assert.Equal(PaletteViewModel.MaxSessionRows, palette.Items.Count);
        Assert.All(palette.Items, item => Assert.EndsWith("by words", item.Detail, StringComparison.Ordinal));
        // A session the rail does not list says so when chosen.
        palette.Accept();
        Assert.True(palette.IsOpen);
        Assert.Contains("is not in the rail's list", palette.Note, StringComparison.Ordinal);
        Assert.Equal(Tone.Amber, palette.NoteTone);
    }

    [Fact]
    public void With_memory_off_the_sessions_group_matches_the_rails_titles()
    {
        using var composition = Compose();
        var palette = Palette(composition, new FindingSearch([], []) { Unavailable = "memory is off (Corvid:Memory:Enabled)" });
        palette.Execute(KeyCommands.Search);

        palette.Query = "vault";

        var row = Assert.Single(palette.Items);
        Assert.Equal(("Vault policy for ntfy", PaletteViewModel.SessionsGroup), (row.Title, row.Group));
        Assert.EndsWith("by title", row.Detail, StringComparison.Ordinal);
        Assert.Equal(Tone.Amber, palette.NoteTone);
        Assert.Contains("Corvid:Memory:Enabled", palette.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_failed_session_search_still_lists_what_was_said()
    {
        using var composition = Compose();
        var palette = Palette(composition, new FindingSearch([Said("cli-a410", "the zebra plan")], []) { FailFinding = true });
        palette.Execute(KeyCommands.Search);

        palette.Query = "zebra";

        Assert.Equal("the zebra plan", Assert.Single(palette.Items).Title);
        Assert.Equal("", palette.Note);
    }
}
