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

    private static BoardViewModel Start(ISessionSearch? search = null) => Start(search, out _);

    private static BoardViewModel Start(ISessionSearch? search, out DemoSessionService sessions)
    {
        var time = ManualTimeProvider.Demo();
        var dispatcher = new InlineDispatcher();
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        DemoShell.Fill(bar);
        DemoShell.Fill(shell);
        sessions = new DemoSessionService(dispatcher, time, DemoScene.Approval);
        var hub = new SessionHub(sessions, dispatcher, time, shell, bar);
        hub.StartAsync(null, DemoShell.CurrentSessionId, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        shell.Navigate(Area.Board);
        return new BoardViewModel(shell, hub, new DemoBoardService(time), new SessionServiceWatcher(sessions), time, dispatcher, search)
        {
            SearchDelay = TimeSpan.Zero,
        };
    }

    // Every column's cards, left to right (C-043): the filter box narrows all four.
    private static List<string> Shown(BoardViewModel board) => [.. board.Columns.SelectMany(column => column.Cards).Select(card => card.Id)];

    [Fact]
    public void Typing_narrows_the_cards_at_once_by_their_own_text_and_esc_clears_it()
    {
        using var board = Start();
        var tray = board.Attention.Count;
        var all = Shown(board).Count;

        board.FilterText = "vault";
        Assert.Equal([DemoSessionService.VaultSessionId], Shown(board));
        Assert.True(board.IsFiltering);
        Assert.False(board.IsEmpty);
        Assert.Equal("", board.EmptyText);

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
        Assert.Equal(all, Shown(board).Count);
        Assert.False(board.IsEmpty);
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
        Assert.Equal(new[] { DemoSessionService.MainSessionId, DemoSessionService.ReleaseNotesSessionId }.Order(), Shown(board).Order());
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
    public void A_session_only_its_meaning_found_joins_from_two_words()
    {
        var finder = new FakeFinder();
        SessionFind[] finds =
        [
            new(DemoSessionService.MainSessionId, null, null, 0.03, ByMeaning: true),
            new(DemoSessionService.ReleaseNotesSessionId, null, null, 0.02, ByMeaning: true, ByWords: true),
            new(DemoSessionService.VaultSessionId, null, null, 0.01, ByMeaning: false, ByWords: true),
        ];
        finder.Answers["okapi"] = finds;
        finder.Answers["okapi tusk"] = finds;
        using var board = Start(finder);

        // One word is a lookup: the nearest cards would join it whatever it says, so only what words found joins.
        board.FilterText = "okapi";
        Assert.Equal(new[] { DemoSessionService.ReleaseNotesSessionId, DemoSessionService.VaultSessionId }.Order(), Shown(board).Order());
        Assert.Equal("by meaning", board.Cards[DemoSessionService.ReleaseNotesSessionId].FoundBy);

        board.FilterText = "okapi tusk";
        Assert.Equal(new[] { DemoSessionService.MainSessionId, DemoSessionService.ReleaseNotesSessionId, DemoSessionService.VaultSessionId }.Order(),
            Shown(board).Order());
    }

    [Fact]
    public void A_card_the_filter_keeps_stays_in_its_column()
    {
        var finder = new FakeFinder();
        using var board = Start(finder);
        var columns = board.Columns.ToDictionary(column => column.Column, column => column.Cards.Select(card => card.Id).ToList());
        finder.Answers["the CI hang"] = [.. Shown(board).Select(id => new SessionFind(id, null, null, 0.01, ByMeaning: false, ByWords: true))];

        board.FilterText = "the CI hang";

        // The engine found every session: each column holds what it held, in its own order.
        Assert.All(board.Columns, column => Assert.Equal(columns[column.Column], column.Cards.Select(card => card.Id)));
        board.FilterText = "okapi";
        Assert.Empty(Shown(board));
        Assert.Equal("No session matches \"okapi\".", board.EmptyText);
    }

    [Fact]
    public async Task A_found_card_loses_its_tag_once_its_own_text_matches()
    {
        var finder = new FakeFinder();
        finder.Answers["the okapi plan"] = [new SessionFind(DemoSessionService.ReleaseNotesSessionId, null, null, 0.03, ByMeaning: true)];
        using var board = Start(finder, out var sessions);

        board.FilterText = "the okapi plan";
        Assert.Equal("by meaning", board.Cards[DemoSessionService.ReleaseNotesSessionId].FoundBy);

        // A title generated (or a rename) while the filter holds it: the card now matches by its own text.
        await sessions.RenameAsync(DemoSessionService.ReleaseNotesSessionId, "The okapi plan", TestContext.Current.CancellationToken);

        Assert.Equal([DemoSessionService.ReleaseNotesSessionId], Shown(board));
        Assert.Equal("", board.Cards[DemoSessionService.ReleaseNotesSessionId].FoundBy);
        Assert.DoesNotContain(board.Cards[DemoSessionService.ReleaseNotesSessionId].Tags, tag => tag.Text == "by meaning");
    }

    [Fact]
    public void A_found_session_the_board_has_no_card_for_is_left_out()
    {
        var finder = new FakeFinder();
        finder.Answers["the CI hang"] =
        [
            new SessionFind("cli-gone", "A session the Board never listed", null, 0.05, ByMeaning: false, ByWords: true),
            new SessionFind(DemoSessionService.VaultSessionId, null, null, 0.01, ByMeaning: false, ByWords: true),
        ];
        using var board = Start(finder);

        board.FilterText = "the CI hang";

        Assert.Equal([DemoSessionService.VaultSessionId], Shown(board));
        Assert.False(board.Cards.ContainsKey("cli-gone"));
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
