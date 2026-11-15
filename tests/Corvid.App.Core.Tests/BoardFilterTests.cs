using Corvid.App.Core.Board;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Palette;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

/// <summary>C-044: the Board's filter box narrows the cards as you type, and the session search adds what it finds.</summary>
public sealed class BoardFilterTests
{
    /// <summary>A session search that answers from a list per query, or waits on a gate; it records what it was asked.</summary>
    private sealed class FakeFinder : ISessionSearch
    {
        public List<string> Asked { get; } = [];

        public Dictionary<string, SessionFind[]> Answers { get; } = [];

        public Dictionary<string, TaskCompletionSource<SessionFinds?>> Gates { get; } = [];

        public string? Unavailable { get; init; }

        public Task<SessionMatches> SearchAsync(string query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionMatches([]));

        public Task<SessionFinds?> FindSessionsAsync(string query, CancellationToken cancellationToken = default)
        {
            Asked.Add(query);
            if (Gates.TryGetValue(query, out var gate))
                return gate.Task.WaitAsync(cancellationToken);
            if (Unavailable is { } why)
                return Task.FromResult<SessionFinds?>(new SessionFinds([], null, why));
            return Task.FromResult<SessionFinds?>(new SessionFinds(Answers.GetValueOrDefault(query) ?? [], "e5"));
        }
    }

    private static BoardViewModel Start(ISessionSearch? search = null)
    {
        var time = ManualTimeProvider.Demo();
        var dispatcher = new InlineDispatcher();
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        DemoShell.Fill(bar);
        DemoShell.Fill(shell);
        var sessions = new DemoSessionService(dispatcher, time, DemoScene.Approval);
        var hub = new SessionHub(sessions, dispatcher, time, shell, bar);
        hub.StartAsync(null, DemoShell.CurrentSessionId, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        shell.Navigate(Area.Board);
        return new BoardViewModel(shell, hub, new DemoBoardService(time), new SessionServiceWatcher(sessions), time, dispatcher, search)
        {
            SearchDelay = TimeSpan.Zero,
        };
    }

    private static List<string> Shown(BoardViewModel board) => [.. board.Tiles.OfType<SessionCardViewModel>().Select(card => card.Id)];

    [Fact]
    public void Typing_narrows_the_cards_at_once_by_their_own_text_and_esc_clears_it()
    {
        using var board = Start();
        var tray = board.Attention.Count;

        board.FilterText = "vault";
        Assert.Equal([DemoSessionService.VaultSessionId], Shown(board));
        // While filtering, the dashed card that starts a session steps aside.
        Assert.DoesNotContain(board.Tiles, tile => tile is NewSessionTile);
        Assert.True(board.IsFiltering);

        // A branch, a workspace and an id match too, fuzzily.
        board.FilterText = "ntfy";
        Assert.Equal([DemoSessionService.VaultSessionId], Shown(board));
        board.FilterText = "c3d2";
        Assert.Equal([DemoSessionService.TerminalSessionId], Shown(board));
        board.FilterText = "flaky sandbox";
        Assert.Equal([DemoSessionService.MainSessionId], Shown(board));

        board.FilterText = "okapi";
        Assert.Empty(Shown(board));
        Assert.True(board.IsEmpty);
        Assert.Equal("No session matches \"okapi\".", board.EmptyText);
        // The tray is the list of what waits on you: never filtered.
        Assert.Equal(tray, board.Attention.Count);

        board.ClearFilterCommand.Execute(null);
        Assert.Equal("", board.FilterText);
        Assert.Equal(5, Shown(board).Count);
        Assert.IsType<NewSessionTile>(board.Tiles[^1]);
        Assert.False(board.ClearFilterCommand.CanExecute(null));
    }

    [Fact]
    public void The_session_search_adds_what_it_finds_with_a_tag_saying_how()
    {
        var finder = new FakeFinder();
        finder.Answers["the CI hang"] =
        [
            new SessionFind(DemoSessionService.MainSessionId, "Fix the flaky sandbox test on CI", null, 0.03, ByMeaning: true),
            new SessionFind(DemoSessionService.ReleaseNotesSessionId, "Draft release notes for v0.2", null, 0.01, ByMeaning: false),
        ];
        using var board = Start(finder);

        board.FilterText = "the CI hang";

        Assert.Equal(["the CI hang"], finder.Asked);
        Assert.Equal([DemoSessionService.MainSessionId, DemoSessionService.ReleaseNotesSessionId], Shown(board));
        Assert.Equal("by meaning", board.Cards[DemoSessionService.MainSessionId].FoundBy);
        Assert.Contains(board.Cards[DemoSessionService.MainSessionId].Tags, tag => tag.Text == "by meaning");
        Assert.Equal("by words", board.Cards[DemoSessionService.ReleaseNotesSessionId].FoundBy);
        Assert.Equal("by meaning", board.FilterNote);

        // Clearing the filter drops the tags with the found cards.
        board.FilterText = "";
        Assert.Equal("", board.Cards[DemoSessionService.MainSessionId].FoundBy);
        Assert.DoesNotContain(board.Cards[DemoSessionService.MainSessionId].Tags, tag => tag.Text == "by meaning");
        Assert.Equal("", board.FilterNote);
    }

    [Fact]
    public void A_card_its_own_text_matches_carries_no_tag_and_a_short_filter_never_asks_the_engine()
    {
        var finder = new FakeFinder();
        finder.Answers["vault"] = [new SessionFind(DemoSessionService.VaultSessionId, "Vault policy for ntfy", null, 0.03, ByMeaning: true)];
        using var board = Start(finder);

        board.FilterText = "va";
        Assert.Empty(finder.Asked);
        board.FilterText = "vault";

        Assert.Equal(["vault"], finder.Asked);
        Assert.Equal([DemoSessionService.VaultSessionId], Shown(board));
        Assert.Equal("", board.Cards[DemoSessionService.VaultSessionId].FoundBy);
    }

    [Fact]
    public void A_newer_keystroke_cancels_a_search_still_running()
    {
        var finder = new FakeFinder();
        var slow = new TaskCompletionSource<SessionFinds?>();
        finder.Gates["the CI"] = slow;
        finder.Answers["the CI hang"] = [];
        using var board = Start(finder);

        board.FilterText = "the CI";
        board.FilterText = "the CI hang";
        slow.SetResult(new SessionFinds([new SessionFind(DemoSessionService.MainSessionId, null, null, 1, ByMeaning: true)], "e5"));

        Assert.Equal(["the CI", "the CI hang"], finder.Asked);
        Assert.Empty(Shown(board));
    }

    [Fact]
    public void With_memory_off_the_filter_says_it_matches_titles_only()
    {
        using var board = Start(new FakeFinder { Unavailable = "memory is off (Corvid:Memory:Enabled)" });

        board.FilterText = "vault";

        Assert.Equal([DemoSessionService.VaultSessionId], Shown(board));
        Assert.StartsWith("matching titles only: memory is off", board.FilterNote, StringComparison.Ordinal);
    }

    [Fact]
    public void The_local_match_reads_a_cards_title_preview_id_workspace_branch_and_flock()
    {
        var summary = new SessionSummary("cli-1234", "Port the board") { Preview = "Moved the tray", Workspace = "corvid", Branch = "corvid/sandbox-flake", Flock = "docs" };

        Assert.True(BoardQuery.Matches(summary, ""));
        Assert.True(BoardQuery.Matches(summary, "board"));
        Assert.True(BoardQuery.Matches(summary, "tray"));
        Assert.True(BoardQuery.Matches(summary, "1234"));
        Assert.True(BoardQuery.Matches(summary, "sandbox-flake"));
        Assert.True(BoardQuery.Matches(summary, "docs"));
        Assert.False(BoardQuery.Matches(summary, "okapi"));
        Assert.False(BoardQuery.AsksEngine("ab "));
        Assert.True(BoardQuery.AsksEngine("abc"));
    }
}
