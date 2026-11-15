using System.Collections.Concurrent;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Host.State;
using Corvid.Kernel.Events;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>A clock that stands still until a test moves it.</summary>
internal sealed class StoppedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A chat client that answers from a function, optionally waiting on a gate first; it records each prompt.</summary>
internal sealed class ScriptedChat(Func<IReadOnlyList<ChatMessage>, string> reply) : IChatClient
{
    public ConcurrentQueue<IReadOnlyList<ChatMessage>> Prompts { get; } = new();

    public Task? Gate { get; set; }

    public TaskCompletionSource Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // As Starling's local generator refuses it: sampling with no seed (it keeps every sampled response reproducible).
        if (options is { Seed: null } && (options.Temperature > 0 || options.TopP is not null || options.TopK is not null))
            throw new InvalidOperationException("Sampling (ChatOptions.Temperature > 0, TopP or TopK) requires ChatOptions.Seed.");
        var list = messages.ToList();
        Prompts.Enqueue(list);
        Asked.TrySetResult();
        if (Gate is { } gate)
            await gate.WaitAsync(cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, reply(list)));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>G-7: ui.db, shared by the CLI and the app.</summary>
public sealed class UiStateStoreTests : IDisposable
{
    private static readonly DateTimeOffset At = new(2026, 10, 24, 14, 5, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-ui-" + Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(_root, "ui.db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Drafts_pins_review_points_and_preferences_round_trip_and_the_file_is_made_on_first_use()
    {
        var store = new UiStateStore(File);
        Assert.False(System.IO.File.Exists(File));

        store.SetDraft("cli-1", "half a thought", "put aside", At);
        store.SetPinned("cli-1", true, At);
        store.SetPinned("cli-2", true, At);
        store.SetPinned("cli-2", false, At);
        store.SetReviewPoint(new UiReviewPoint("cli-1", 12, 2, At, 40));
        store.SetPreferences(new Dictionary<string, string> { ["Density"] = "Compact", ["NotifyTurnEnd"] = "false" }, At);

        // Another instance, as another process would open it.
        var other = new UiStateStore(File);
        Assert.Equal(new UiDraft("cli-1", "half a thought", "put aside", At), other.GetDraft("cli-1"));
        Assert.Equal(["cli-1"], other.Pinned());
        Assert.Equal(new UiReviewPoint("cli-1", 12, 2, At, 40), other.GetReviewPoint("cli-1"));
        Assert.Equal("Compact", other.Preferences()["Density"]);
        Assert.Null(other.GetDraft("cli-2"));

        other.SetDraft("cli-1", "", null, At);
        Assert.Null(store.GetDraft("cli-1"));
    }

    [Fact]
    public void Seen_keeps_the_furthest_point_and_the_latest_time_either_surface_reached()
    {
        var app = new UiStateStore(File);
        var cli = new UiStateStore(File);

        app.MarkSeen("cli-1", 30, At.AddMinutes(5));
        cli.MarkSeen("cli-1", 20, At);

        Assert.Equal(new UiSeen("cli-1", 30, At.AddMinutes(5)), cli.LastSeen("cli-1"));
    }

    /// <summary>Two stores on one file in this process, as two processes would hold it (each its own connections).</summary>
    [Fact]
    public async Task Two_stores_on_one_file_writing_at_once_lose_nothing()
    {
        // Enough interleaved writes to meet the other store's lock many times, few enough for a busy shared runner.
        const int Rounds = 30;
        var first = new UiStateStore(File);
        var second = new UiStateStore(File);
        first.Pinned();
        using var start = new Barrier(2);
        var token = TestContext.Current.CancellationToken;

        void Write(UiStateStore store, string prefix)
        {
            start.SignalAndWait(token);
            for (var i = 0; i < Rounds; i++)
            {
                store.SetDraft($"{prefix}-{i}", $"draft {i}", null, At);
                store.SetPinned($"{prefix}-{i}", true, At);
                store.SetPreferences(new Dictionary<string, string> { [$"{prefix}:Last"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }, At);
            }
        }

        await Task.WhenAll(Task.Run(() => Write(first, "app"), token), Task.Run(() => Write(second, "cli"), token));

        var pinned = new UiStateStore(File).Pinned();
        Assert.Equal(2 * Rounds, pinned.Count);
        Assert.Equal($"draft {Rounds - 1}", first.GetDraft($"cli-{Rounds - 1}")!.Text);
        Assert.Equal($"{Rounds - 1}", second.Preferences()["app:Last"]);
    }

    [Fact]
    public void A_file_from_a_newer_corvid_is_refused()
    {
        Directory.CreateDirectory(_root);
        using (var connection = new SqliteConnection($"Data Source={File};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=3;";
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<InvalidDataException>(() => new UiStateStore(File).Pinned());
        Assert.Contains("newer Corvid", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_file_is_version_2_with_the_boards_marks()
    {
        var store = new UiStateStore(File);

        Assert.Empty(store.BoardMarks());
        Assert.Equal(2, UserVersion());
    }

    [Fact]
    public void A_version_1_file_gains_the_boards_marks_and_keeps_what_it_held()
    {
        // C-043: ui.db as Corvid wrote it before the Board's marks.
        Directory.CreateDirectory(_root);
        using (var connection = new SqliteConnection($"Data Source={File};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE drafts (session_id TEXT PRIMARY KEY, text TEXT NOT NULL, stash TEXT, updated_at TEXT NOT NULL);
                CREATE TABLE pins (session_id TEXT PRIMARY KEY, pinned_at TEXT NOT NULL);
                CREATE TABLE review_points (session_id TEXT PRIMARY KEY, turn INTEGER NOT NULL, comments INTEGER NOT NULL,
                    at TEXT NOT NULL, head INTEGER NOT NULL);
                CREATE TABLE seen (session_id TEXT PRIMARY KEY, sequence INTEGER NOT NULL, at TEXT NOT NULL);
                CREATE TABLE preferences (key TEXT PRIMARY KEY, value TEXT NOT NULL, updated_at TEXT NOT NULL);
                INSERT INTO pins (session_id, pinned_at) VALUES ('cli-1', '2026-10-24T14:05:00.0000000Z');
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }

        var store = new UiStateStore(File);
        store.SetBoardMark("cli-1", UiBoardMarks.Done, 7, At);

        Assert.Equal(["cli-1"], store.Pinned());
        Assert.Equal(new UiBoardMark("cli-1", UiBoardMarks.Done, 7, At), store.BoardMarks()["cli-1"]);
        Assert.Equal(2, UserVersion());
    }

    [Fact]
    public void A_board_mark_holds_until_newer_news_and_the_last_write_wins()
    {
        var store = new UiStateStore(File);
        store.SetBoardMark("cli-1", UiBoardMarks.Parked, 4, At);
        store.SetBoardMark("cli-2", UiBoardMarks.Done, 9, At);
        store.SetBoardMark("cli-1", UiBoardMarks.Done, 6, At.AddMinutes(1));

        var marks = new UiStateStore(File).BoardMarks();
        Assert.Equal(new UiBoardMark("cli-1", UiBoardMarks.Done, 6, At.AddMinutes(1)), marks["cli-1"]);
        Assert.True(marks["cli-1"].HoldsAt(6));
        Assert.False(marks["cli-1"].HoldsAt(7));

        store.ClearBoardMark("cli-1");
        Assert.Equal(["cli-2"], store.BoardMarks().Keys);
        Assert.Throws<ArgumentException>(() => store.SetBoardMark("cli-1", "archived", 1, At));
    }

    private long UserVersion()
    {
        using var connection = new SqliteConnection($"Data Source={File};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return (long)command.ExecuteScalar()!;
    }

    [Fact]
    public void A_draft_longer_than_the_limit_is_cut()
    {
        var store = new UiStateStore(File);
        store.SetDraft("cli-1", new string('x', UiStateStore.MaxDraftLength + 10), null, At);
        Assert.Equal(UiStateStore.MaxDraftLength, store.GetDraft("cli-1")!.Text.Length);
    }

    [Fact]
    public void A_draft_is_never_cut_between_the_halves_of_a_surrogate_pair()
    {
        var store = new UiStateStore(File);
        // The emoji's high surrogate is the limit's last character.
        store.SetDraft("cli-1", new string('x', UiStateStore.MaxDraftLength - 1) + "\U0001F600tail", null, At);
        var kept = store.GetDraft("cli-1")!.Text;
        Assert.Equal(UiStateStore.MaxDraftLength - 1, kept.Length);
        Assert.False(char.IsHighSurrogate(kept[^1]));
    }

    [Fact]
    public void A_draft_older_than_the_one_kept_is_ignored_whichever_order_the_saves_arrive_in()
    {
        var app = new UiStateStore(File);
        var cli = new UiStateStore(File);

        app.SetDraft("cli-1", "the newer draft", null, At.AddSeconds(2));
        cli.SetDraft("cli-1", "the older draft", null, At.AddSeconds(1));
        Assert.Equal("the newer draft", app.GetDraft("cli-1")!.Text);
        // An older clear does not forget a newer draft either; a newer one does.
        cli.SetDraft("cli-1", "", null, At.AddSeconds(1));
        Assert.Equal("the newer draft", app.GetDraft("cli-1")!.Text);
        cli.SetDraft("cli-1", "", null, At.AddSeconds(3));
        Assert.Null(app.GetDraft("cli-1"));
    }

    [Fact]
    public void A_ui_db_deleted_while_the_store_is_in_use_is_made_again()
    {
        var store = new UiStateStore(File);
        store.SetPinned("cli-1", true, At);
        // Deleted under the store's pooled connections, as a person clearing their state would.
        foreach (var file in Directory.GetFiles(_root, "ui.db*"))
            System.IO.File.Delete(file);

        store.SetPinned("cli-2", true, At);

        Assert.True(System.IO.File.Exists(File));
        Assert.Equal(["cli-2"], new UiStateStore(File).Pinned());
    }

    [Fact]
    public void Ui_db_is_owner_only_and_so_is_the_data_directory_it_makes()
    {
        var data = Path.Combine(_root, "data");
        var store = new UiStateStore(Path.Combine(data, "ui.db"));

        store.SetPinned("cli-1", true, At);

        if (OperatingSystem.IsWindows())
            return;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(store.Path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, System.IO.File.GetUnixFileMode(data));
    }
}

/// <summary>G-6: titles and recaps written by the local generator, and renames.</summary>
public sealed class SessionTitlerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-titler-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private SqliteEventLog Log(params SessionEvent[] events)
    {
        var log = new SqliteEventLog(Path.Combine(_root, "events.db"));
        if (events.Length > 0)
            log.Append("s", 0, [.. events.Select(EventData.Create)], TestContext.Current.CancellationToken);
        return log;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_first_exchange_gets_a_cleaned_generated_title_from_its_message_and_answer()
    {
        var log = Log(new UserMessage("!!cat secret.txt", LocalOnly: true), new UserMessage("the sandbox test flakes on CI, fix it"),
            new AssistantMessage("Fixed: it raced the timer."));
        var chat = new ScriptedChat(_ => "Title: \"Fix the flaky sandbox test.\"\nmore");
        var titler = new SessionTitler(log, chat);

        var titled = await titler.TitleAsync("s", Ct);

        Assert.Equal(new SessionTitled("Fix the flaky sandbox test", TitleSources.Generated), titled);
        var prompt = Assert.Single(chat.Prompts);
        Assert.Contains("the sandbox test flakes", prompt[^1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", prompt[^1].Text, StringComparison.Ordinal);
        Assert.Equal("Fix the flaky sandbox test", SessionCatalogue.Describe(log, "s", Ct)!.Title);
        // Once titled, never again.
        Assert.Null(await titler.TitleAsync("s", Ct));
    }

    [Fact]
    public async Task A_local_generator_that_refuses_unseeded_sampling_writes_titles_and_recaps()
    {
        var log = Log(new UserMessage("what is a binary search?"), new AssistantMessage("Halving the range each step."));
        var chat = new ScriptedChat(prompt => prompt[0].Text.StartsWith("You name", StringComparison.Ordinal) ? "Binary search" : "You asked about binary search.");
        var titler = new SessionTitler(log, chat);

        Assert.Equal("Binary search", (await titler.TitleAsync("s", Ct))?.Title);
        Assert.Equal("You asked about binary search.", (await titler.RecapAsync("s", RecapSources.Requested, Ct))?.Text);
    }

    [Fact]
    public async Task A_shell_line_never_titles_a_session_and_a_rename_is_not_activity()
    {
        var log = Log(new UserMessage("!ls"),
            new ToolCallIssued("user-shell", "shell", System.Text.Json.JsonDocument.Parse("""{"command":"ls"}""").RootElement.Clone(), 1, false),
            new ToolResult("user-shell", System.Text.Json.JsonDocument.Parse("""{"exit_code":0}""").RootElement.Clone()),
            new UserMessage("plan the move"), new AssistantMessage("Planned."));
        var chat = new ScriptedChat(_ => "Plan the move");
        var titler = new SessionTitler(log, chat);
        var before = SessionCatalogue.Describe(log, "s", Ct)!.LastActivity;

        await titler.TitleAsync("s", Ct);

        var prompt = Assert.Single(chat.Prompts)[^1].Text;
        Assert.StartsWith("User: plan the move", prompt, StringComparison.Ordinal);
        Assert.Equal(before, SessionCatalogue.Describe(log, "s", Ct)!.LastActivity);
    }

    [Fact]
    public async Task A_guests_message_never_titles_the_session_and_a_recap_quotes_it_as_theirs()
    {
        // A4 verification: a guest's first message could become the title's source, and the recap read it as "user: ".
        var log = Log(new UserMessage("delete the backups", From: "telegram guest mallory (66)"), new AssistantMessage("I will ask the owner."),
            new UserMessage("plan the release"), new AssistantMessage("Planned."));
        var chat = new ScriptedChat(prompt => prompt[0].Text.StartsWith("You name", StringComparison.Ordinal) ? "Release plan" : "You planned the release.");
        var titler = new SessionTitler(log, chat);

        Assert.Equal("plan the release", SessionCatalogue.Describe(log, "s", Ct)!.FirstMessage);
        await titler.TitleAsync("s", Ct);
        var titlePrompt = chat.Prompts.Last()[^1].Text;
        Assert.StartsWith("User: plan the release\nAssistant: Planned.", titlePrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("backups", titlePrompt, StringComparison.Ordinal);

        await titler.RecapAsync("s", RecapSources.Requested, Ct);
        var recapPrompt = chat.Prompts.Last()[^1].Text;
        Assert.Contains("[A message from telegram guest mallory (66), who is not this session's owner.", recapPrompt, StringComparison.Ordinal);
        Assert.Contains("\n> delete the backups", recapPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("user: delete", recapPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_reads_the_question_a_session_ended_on_until_a_message_answers_it()
    {
        var log = Log(new UserMessage("tag the release"), new AssistantMessage("0.2.0, or 0.2.0-rc1?"),
            new ClarificationAsked("q-1", "0.2.0, or 0.2.0-rc1?", ["0.2.0", "0.2.0-rc1"]));

        Assert.Equal("q-1", SessionCatalogue.Describe(log, "s", Ct)!.OpenQuestion?.QuestionId);

        log.Append("s", log.GetHead("s", Ct), [EventData.Create(new UserMessage("0.2.0"))], Ct);
        Assert.Null(SessionCatalogue.Describe(log, "s", Ct)!.OpenQuestion);
    }

    [Fact]
    public void A_list_reads_how_the_latest_answer_ends_and_a_status_line_is_not_news()
    {
        var log = Log(new UserMessage("open the PR"), new AssistantMessage("PR #12 is up. Want me to merge it?"));
        var asked = SessionCatalogue.Describe(log, "s", Ct)!;
        Assert.Equal(TurnEnding.AsksYou, asked.Ending);
        Assert.Equal(2, asked.LastAnswerSequence);
        Assert.False(asked.InTurn);

        // C-043: the status line is about the session: no news, no activity, but the description carries it.
        log.Append("s", log.GetHead("s", Ct), [EventData.Create(new SessionStatusNoted("PR #12 open; waiting for your merge call", 2, StatusSources.Generated))], Ct);
        var noted = SessionCatalogue.Describe(log, "s", Ct)!;
        Assert.Equal(asked.LastNews, noted.LastNews);
        Assert.Equal(asked.LastActivity, noted.LastActivity);
        Assert.Equal("PR #12 open; waiting for your merge call", noted.Status?.Text);
        Assert.Equal(TurnEnding.AsksYou, noted.Ending);

        log.Append("s", log.GetHead("s", Ct), [EventData.Create(new UserMessage("yes, merge"))], Ct);
        Assert.True(SessionCatalogue.Describe(log, "s", Ct)!.InTurn);
        log.Append("s", log.GetHead("s", Ct), [EventData.Create(new AssistantMessage("PR #12 is merged."))], Ct);
        var merged = SessionCatalogue.Describe(log, "s", Ct)!;
        Assert.Equal(TurnEnding.Finished, merged.Ending);
        Assert.False(merged.InTurn);
    }

    [Fact]
    public void A_turn_reads_as_under_way_from_the_log_until_an_answer_or_a_question()
    {
        var log = Log(new UserMessage("run the tests"), new ToolCallIssued("c1", "shell", Json("{}")));
        Assert.True(SessionCatalogue.Describe(log, "s", Ct)!.InTurn);

        // A question the turn ended on waits on the person: the turn is not under way, whatever came after the answer.
        log.Append("s", log.GetHead("s", Ct), [EventData.Create(new ClarificationAsked("q-1", "Which project?", []))], Ct);
        Assert.False(SessionCatalogue.Describe(log, "s", Ct)!.InTurn);

        // The person's own ! line has no answer: it never reads as a turn under way.
        var shell = new SqliteEventLog(Path.Combine(_root, "shell.db"));
        shell.Append("t", 0, [EventData.Create(new UserMessage("hi")), EventData.Create(new AssistantMessage("Hello.")),
            EventData.Create(new UserMessage("!!ls", LocalOnly: true)), EventData.Create(new ToolCallIssued("u1", "shell", Json("{}"), LocalOnly: true)),
            EventData.Create(new ToolResult("u1", Json("{}"), LocalOnly: true))], Ct);
        Assert.False(SessionCatalogue.Describe(shell, "t", Ct)!.InTurn);
    }

    [Fact]
    public void A_plain_shell_line_ends_with_no_answer_and_never_reads_as_a_turn_under_way()
    {
        // A ! line (not !!) is logged without LocalOnly: its output goes to the model with the next message.
        var log = new SqliteEventLog(Path.Combine(_root, "plain.db"));
        log.Append("t", 0, [EventData.Create(new UserMessage("hi")), EventData.Create(new AssistantMessage("Hello.")),
            EventData.Create(new UserMessage("!ls")), EventData.Create(new ToolCallIssued("user-shell", "shell", Json("""{"command":"ls"}"""))),
            EventData.Create(new ToolResult("user-shell", Json("""{"exit_code":0}""")))], Ct);
        Assert.False(SessionCatalogue.Describe(log, "t", Ct)!.InTurn);

        // Read between the message and its call (the line's command still runs), the message is a turn's start until the
        // call after it shows it was a ! line.
        log.Append("t", log.GetHead("t", Ct), [EventData.Create(new UserMessage("!git status"))], Ct);
        Assert.True(SessionCatalogue.Describe(log, "t", Ct)!.InTurn);
        log.Append("t", log.GetHead("t", Ct), [EventData.Create(new ToolCallIssued("user-shell-2", "shell", Json("""{"command":"git status"}""")))], Ct);
        Assert.False(SessionCatalogue.Describe(log, "t", Ct)!.InTurn);
        log.Append("t", log.GetHead("t", Ct), [EventData.Create(new ToolResult("user-shell-2", Json("""{"exit_code":0}""")))], Ct);
        Assert.False(SessionCatalogue.Describe(log, "t", Ct)!.InTurn);

        // A message to the model after it is a turn again.
        log.Append("t", log.GetHead("t", Ct), [EventData.Create(new UserMessage("why is the tree dirty?"))], Ct);
        Assert.True(SessionCatalogue.Describe(log, "t", Ct)!.InTurn);
    }

    private static System.Text.Json.JsonElement Json(string text) => System.Text.Json.JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public async Task A_status_line_is_written_through_the_latest_answer_from_the_task_and_the_latest_exchange()
    {
        var log = Log(new UserMessage("!!cat secret.txt", LocalOnly: true), new UserMessage("fix the flaky sandbox test"),
            new AssistantMessage("Skipped the control step."), new UserMessage("open a PR"), new AssistantMessage("PR #12 is up. Want me to merge it?"));
        var chat = new ScriptedChat(_ => "Status: \"PR #12 open, waiting for your merge call.\"");
        var titler = new SessionTitler(log, chat);

        var status = await titler.StatusAsync("s", Ct);

        Assert.Equal(new SessionStatusNoted("PR #12 open, waiting for your merge call", 5, StatusSources.Generated), status);
        var prompt = Assert.Single(chat.Prompts);
        Assert.Equal(SessionTitler.StatusPrompt, prompt[0].Text);
        Assert.Contains("Task: fix the flaky sandbox test", prompt[^1].Text, StringComparison.Ordinal);
        Assert.Contains("Latest message: open a PR", prompt[^1].Text, StringComparison.Ordinal);
        Assert.Contains("Latest answer: PR #12 is up", prompt[^1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", prompt[^1].Text, StringComparison.Ordinal);
        // Current: no second call until another answer.
        Assert.Null(await titler.StatusAsync("s", Ct));
        Assert.Single(chat.Prompts);

        log.Append("s", log.GetHead("s", Ct), [EventData.Create(new UserMessage("merge it")), EventData.Create(new AssistantMessage("Merged."))], Ct);
        Assert.NotNull(await titler.StatusAsync("s", Ct));
        Assert.Contains("Previous status: PR #12 open, waiting for your merge call", chat.Prompts.Last()[^1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_status_line_without_a_local_generator_or_with_it_turned_off_or_when_the_generator_fails()
    {
        var log = Log(new UserMessage("hi"), new AssistantMessage("Hello."));

        Assert.Null(await new SessionTitler(log, null).StatusAsync("s", Ct));
        Assert.Null(await new SessionTitler(log, new ScriptedChat(_ => "x"), new SessionTitlerOptions { StatusLines = false }).StatusAsync("s", Ct));
        Assert.Null(await new SessionTitler(log, new ScriptedChat(_ => throw new InvalidOperationException("down"))).StatusAsync("s", Ct));
        Assert.Null(SessionCatalogue.Describe(log, "s", Ct)!.Status);
    }

    [Fact]
    public void A_guests_reply_in_a_gateway_chat_leaves_the_owners_question_open()
    {
        // Security M2: only the owner's message answers the question asked of them.
        var log = Log(new UserMessage("tag the release"), new AssistantMessage("0.2.0, or 0.2.0-rc1?"),
            new ClarificationAsked("q-1", "0.2.0, or 0.2.0-rc1?", ["0.2.0", "0.2.0-rc1"]));

        log.Append("s", log.GetHead("s", Ct), [EventData.Create(new UserMessage("0.2.0", From: "telegram guest mallory (66)"))], Ct);
        Assert.Equal("q-1", SessionCatalogue.Describe(log, "s", Ct)!.OpenQuestion?.QuestionId);

        log.Append("s", log.GetHead("s", Ct), [EventData.Create(new UserMessage("0.2.0-rc1"))], Ct);
        Assert.Null(SessionCatalogue.Describe(log, "s", Ct)!.OpenQuestion);
    }

    [Fact]
    public async Task A_rename_while_the_generator_writes_wins()
    {
        var log = Log(new UserMessage("hello"), new AssistantMessage("Hi."));
        var gate = new TaskCompletionSource();
        var chat = new ScriptedChat(_ => "Greetings") { Gate = gate.Task };
        var titler = new SessionTitler(log, chat);

        var generating = titler.TitleAsync("s", Ct);
        await chat.Asked.Task.WaitAsync(Ct);
        titler.Rename("s", "  My  session\nname ");
        gate.SetResult();

        Assert.Null(await generating);
        var description = SessionCatalogue.Describe(log, "s", Ct)!;
        Assert.Equal(("My  session name", TitleSources.User), (description.Title, description.Titled!.Source));
    }

    [Fact]
    public void A_cloud_generator_writes_no_titles_and_says_why()
    {
        var profile = new ModelProfile("claude", 200_000, 200_000, ToolCallSupport.Native, StructuredOutputSupport.None,
            PromptCachingStyle.None, ModelCost.Free, PrivacyClass.Cloud, "remote");

        var (client, why) = SessionTitler.Local(new ModelBinding(profile, new ScriptedChat(_ => "x")));

        Assert.Null(client);
        Assert.Contains("cloud model", why, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => new SessionTitler(Log(), client, unavailable: why).RecapAsync("s", RecapSources.Requested, Ct)
            .GetAwaiter().GetResult());
    }

    [Fact]
    public async Task A_recap_covers_what_happened_since_the_last_one_and_leaves_local_only_lines_out()
    {
        var log = Log(new UserMessage("deploy the docs"), new AssistantMessage("Deployed."));
        var chat = new ScriptedChat(prompt => prompt[^1].Text.Contains("deploy", StringComparison.Ordinal) ? "You deployed the docs." : "You checked the logs.");
        var titler = new SessionTitler(log, chat);

        var first = await titler.RecapAsync("s", RecapSources.Requested, Ct);
        Assert.Equal(("You deployed the docs.", 0L, 2L), (first!.Text, first.FromSequence, first.ThroughSequence));
        Assert.Null(await titler.RecapAsync("s", RecapSources.Requested, Ct));

        log.Append("s", log.GetHead("s", Ct), [EventData.Create(new UserMessage("!!cat token.txt", LocalOnly: true)),
            EventData.Create(new UserMessage("check the logs")), EventData.Create(new AssistantMessage("Clean."))], Ct);
        var second = await titler.RecapAsync("s", RecapSources.Absence, Ct);

        Assert.Equal(("You checked the logs.", 2L, RecapSources.Absence), (second!.Text, second.FromSequence, second.Source));
        var prompt = chat.Prompts.Last()[^1].Text;
        Assert.DoesNotContain("token.txt", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("deploy", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void A_recap_on_return_needs_an_absence_and_something_new()
    {
        var log = Log(new UserMessage("deploy"), new AssistantMessage("Done."));
        var clock = new StoppedClock(DateTimeOffset.UtcNow.AddHours(3));
        var titler = new SessionTitler(log, new ScriptedChat(_ => "x"), new SessionTitlerOptions { RecapAfter = TimeSpan.FromHours(1) }, time: clock);
        var session = SessionCatalogue.Describe(log, "s", Ct)!;

        Assert.True(titler.ShouldRecapOnReturn(session, lastSeen: null));
        Assert.False(titler.ShouldRecapOnReturn(session, lastSeen: clock.Now.AddMinutes(-10)));
        Assert.True(titler.ShouldRecapOnReturn(session, lastSeen: clock.Now.AddHours(-2)));
        Assert.False(titler.ShouldRecapOnReturn(session with { Recap = new SessionRecapped("r", 0, session.Head, RecapSources.Absence) }, null));
        Assert.False(new SessionTitler(log, new ScriptedChat(_ => "x"), new SessionTitlerOptions { RecapAfter = TimeSpan.Zero }, time: clock)
            .ShouldRecapOnReturn(session, null));
    }

    /// <summary>A4 review L7: the recap itself, a title, an egress record or an assessment is nothing new to recap.</summary>
    [Fact]
    public async Task A_recap_on_return_is_not_due_again_for_records_written_after_it()
    {
        var log = Log(new UserMessage("deploy"), new AssistantMessage("Done."));
        var clock = new StoppedClock(DateTimeOffset.UtcNow.AddHours(3));
        var titler = new SessionTitler(log, new ScriptedChat(_ => "You deployed."), new SessionTitlerOptions { RecapAfter = TimeSpan.FromHours(1) }, time: clock);
        Assert.NotNull(await titler.RecapAsync("s", RecapSources.Absence, Ct));
        titler.Rename("s", "Deploy");
        log.Append("s", log.GetHead("s", Ct),
        [
            EventData.Create(new EgressRecorded("r-1", "claude", EgressOutcomes.Sent, System.Collections.Immutable.ImmutableDictionary<string, int>.Empty, 0, 10, "no PII")),
            EventData.Create(new ApprovalAssessed("c-1", ApprovalResolutions.Automatic, "smart", [])),
        ], Ct);

        var session = SessionCatalogue.Describe(log, "s", Ct)!;
        Assert.Equal(2, session.LastNews);
        Assert.False(titler.ShouldRecapOnReturn(session, lastSeen: null));

        log.Append("s", log.GetHead("s", Ct), [EventData.Create(new UserMessage("and the docs"))], Ct);
        Assert.True(titler.ShouldRecapOnReturn(SessionCatalogue.Describe(log, "s", Ct)!, lastSeen: null));
    }

    /// <summary>A4 review L9: a list reads each session once, then only what was appended since.</summary>
    [Fact]
    public void A_description_is_kept_by_head_and_continued_over_new_events()
    {
        var log = new CountingLog(Log(new UserMessage("plan the release"), new AssistantMessage("Planned.")));
        var first = SessionCatalogue.Describe(log, "s", Ct)!;
        var reads = log.Reads;

        Assert.Same(first, SessionCatalogue.Describe(log, "s", Ct));
        Assert.Equal(reads, log.Reads);

        log.Append("s", log.GetHead("s", Ct), [EventData.Create(new UserMessage("tag it")), EventData.Create(new AssistantMessage("Tagged."))], Ct);
        var next = SessionCatalogue.Describe(log, "s", Ct)!;

        Assert.Equal((2, "Tagged.", "plan the release", 4L), (next.Turns, next.LastAnswer, next.FirstMessage, next.Head));
        // Only the two new events were read.
        Assert.Equal([2L], log.ReadsAfter.Skip(reads));
    }

    /// <summary>An event log that counts its reads and remembers where each started.</summary>
    private sealed class CountingLog(IEventLog inner) : IEventLog
    {
        public List<long> ReadsAfter { get; } = [];

        public int Reads => ReadsAfter.Count;

        public IReadOnlyList<StoredEvent> Read(string sessionId, long afterSequence = 0, long throughSequence = long.MaxValue, int limit = 256,
            CancellationToken cancellationToken = default)
        {
            ReadsAfter.Add(afterSequence);
            return inner.Read(sessionId, afterSequence, throughSequence, limit, cancellationToken);
        }

        public IReadOnlyList<StoredEvent> Append(string sessionId, long expectedHead, IReadOnlyList<EventData> events,
            CancellationToken cancellationToken = default) => inner.Append(sessionId, expectedHead, events, cancellationToken);

        public long GetHead(string sessionId, CancellationToken cancellationToken = default) => inner.GetHead(sessionId, cancellationToken);
    }

    [Theory]
    [InlineData("\"Fix the build.\"", "Fix the build")]
    [InlineData("**Release notes for v0.2**", "Release notes for v0.2")]
    [InlineData("# Title: plan the move", "plan the move")]
    [InlineData("Plan‮the move", "Planthe move")]
    [InlineData("  ", null)]
    public void Generated_titles_are_cleaned(string reply, string? title) => Assert.Equal(title, SessionTitler.Clean(reply));
}

/// <summary>The composer's mode lines, read the same way by the CLI and the app.</summary>
public sealed class ComposerLineTests
{
    [Theory]
    [InlineData("!git status", "git status", false)]
    [InlineData("  !! cat notes.md ", "cat notes.md", true)]
    public void Shell_lines(string text, string command, bool local) =>
        Assert.Equal(new ComposerLine.Shell(command, local), ComposerLine.Parse(text));

    [Fact]
    public void Memory_lines_empty_marks_and_ordinary_messages()
    {
        Assert.Equal(new ComposerLine.Memory("the release branch is main"), ComposerLine.Parse("# the release branch is main"));
        Assert.IsType<ComposerLine.Empty>(ComposerLine.Parse("!"));
        Assert.IsType<ComposerLine.Empty>(ComposerLine.Parse("#  "));
        Assert.Null(ComposerLine.Parse("# Notes\r- one"));
        Assert.Null(ComposerLine.Parse("![screenshot](a.png)"));
        Assert.Null(ComposerLine.Parse("why does ! mean not?"));
    }

    [Theory]
    [InlineData("#123 is failing again")]
    [InlineData("#include <stdio.h> not found")]
    [InlineData("#[derive(Debug)] fails")]
    [InlineData("!important: keep the API stable")]
    [InlineData("!!!")]
    [InlineData("!= is the wrong operator")]
    [InlineData("!wow, that worked")]
    [InlineData("!really? again")]
    [InlineData("!!important!")]
    [InlineData(@"\# the release branch is main")]
    [InlineData(@"\!git status")]
    public void A_line_that_starts_with_a_mark_but_is_no_mode_is_a_message(string text) => Assert.Null(ComposerLine.Parse(text));

    [Theory]
    [InlineData("! ls -la", "ls -la", false)]
    [InlineData("!./build.sh --fast", "./build.sh --fast", false)]
    [InlineData("!~/bin/tidy", "~/bin/tidy", false)]
    [InlineData("!!git log", "git log", true)]
    // A4 verification: a command word may hold the shell's own characters and start with a variable, a quote or a subshell.
    [InlineData("!env|grep PATH", "env|grep PATH", false)]
    [InlineData("!!env|grep PATH", "env|grep PATH", true)]
    [InlineData("!$EDITOR notes.md", "$EDITOR notes.md", false)]
    [InlineData("!!'my script' --now", "'my script' --now", true)]
    [InlineData("!\"$HOME/bin/tidy\"", "\"$HOME/bin/tidy\"", false)]
    [InlineData("!ls>files.txt", "ls>files.txt", false)]
    [InlineData("!make&&make install", "make&&make install", false)]
    [InlineData("!(cd src; ls)", "(cd src; ls)", false)]
    public void A_mark_before_a_space_or_a_command_word_is_a_shell_line(string text, string command, bool local) =>
        Assert.Equal(new ComposerLine.Shell(command, local), ComposerLine.Parse(text));

    [Theory]
    [InlineData(@"\# the release branch is main", "# the release branch is main")]
    [InlineData(@"  \!git status ", "!git status")]
    [InlineData(@"\!!", "!!")]
    [InlineData("#123 is failing again", "#123 is failing again")]
    [InlineData("plain \\# text", "plain \\# text")]
    [InlineData("\\# Notes\n- one", "\\# Notes\n- one")]
    public void An_escaped_line_is_sent_without_its_backslash(string text, string sent) => Assert.Equal(sent, ComposerLine.MessageText(text));
}

/// <summary>Titles, pins, recaps and the composer lines on a running host.</summary>
public sealed class SessionRecordsHostTests
{
    private static ValueTask<CommandOutput> Run(CorvidRuntime runtime, CorvidSession session, SlashCommandRegistry commands, string line) =>
        commands.ExecuteAsync(line, new SlashCommandContext(runtime, session, commands), TestContext.Current.CancellationToken);

    [Fact]
    public async Task The_first_answered_turn_is_titled_by_the_local_generator_in_the_background()
    {
        var generator = new FakeGenerator(Reply.Say("The boat is blue."), Reply.Say("Boat colour question"));
        await using var host = TestHost.Create(new FakeModels(generator), new Dictionary<string, string?> { ["Corvid:Sessions:Titles"] = "true" });
        await using var runtime = await host.StartAsync();
        var titled = new ConcurrentQueue<(string, SessionTitled)>();
        runtime.TitleChanged += (id, title) => titled.Enqueue((id, title));
        var session = runtime.OpenSession();

        await session.SendAsync("what colour is the boat?", TestContext.Current.CancellationToken);
        await runtime.MaintenanceIdleAsync();

        Assert.Equal((session.Id, new SessionTitled("Boat colour question", TitleSources.Generated)), Assert.Single(titled));
        Assert.Equal("Boat colour question", SessionCatalogue.Describe(runtime.Log, session.Id, TestContext.Current.CancellationToken)!.Title);
    }

    [Fact]
    public async Task Each_answered_turn_gets_a_status_line_from_the_local_generator_in_the_background()
    {
        var generator = new FakeGenerator(Reply.Say("PR #12 is up. Want me to merge it?"), Reply.Say("PR #12 open; waiting for your merge call"));
        await using var host = TestHost.Create(new FakeModels(generator), new Dictionary<string, string?> { ["Corvid:Sessions:StatusLines"] = "true" });
        await using var runtime = await host.StartAsync();
        var noted = new ConcurrentQueue<(string, SessionStatusNoted)>();
        runtime.StatusNoted += (id, status) => noted.Enqueue((id, status));
        var session = runtime.OpenSession();

        await session.SendAsync("open a PR for the fix", TestContext.Current.CancellationToken);
        await runtime.MaintenanceIdleAsync();

        var (id, status) = Assert.Single(noted);
        Assert.Equal(session.Id, id);
        Assert.Equal("PR #12 open; waiting for your merge call", status.Text);
        var described = SessionCatalogue.Describe(runtime.Log, session.Id, TestContext.Current.CancellationToken)!;
        Assert.Equal(described.LastAnswerSequence, described.Status?.ThroughSequence);
        Assert.Equal(TurnEnding.AsksYou, described.Ending);
    }

    [Fact]
    public async Task A_session_nobody_answers_here_is_not_titled()
    {
        // An HTTP client's session (corvid serve): every exchange would otherwise cost a generator call nobody sees.
        var generator = new FakeGenerator(Reply.Say("Hi."), Reply.Say("A title"));
        await using var host = TestHost.Create(new FakeModels(generator), new Dictionary<string, string?> { ["Corvid:Sessions:Titles"] = "true" });
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession(surface: "http", settings: new SessionSettings(Interactive: false));

        await session.SendAsync("hello", TestContext.Current.CancellationToken);
        await runtime.MaintenanceIdleAsync();

        Assert.Single(generator.Calls);
        Assert.Null(session.Snapshot().Title);
    }

    [Fact]
    public async Task Rename_sessions_and_pin_are_shared_through_the_log_and_ui_db()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        var session = runtime.OpenSession();
        await session.SendAsync("plan the release", TestContext.Current.CancellationToken);
        var other = runtime.OpenSession();
        await other.SendAsync("an older chat", TestContext.Current.CancellationToken);

        Assert.Contains("its first message", (await Run(runtime, session, commands, "/rename")).Lines[0], StringComparison.Ordinal);
        Assert.Equal(OutputTone.Success, (await Run(runtime, session, commands, "/rename Release v0.2")).Tone);
        Assert.Equal(OutputTone.Success, (await Run(runtime, other, commands, "/pin")).Tone);
        var list = await Run(runtime, session, commands, "/sessions");

        // Pinned first; the renamed one with its new title.
        Assert.Equal([other.Id, session.Id], list.Table!.Rows.Select(row => row[1]));
        Assert.Equal(("pinned", "an older chat"), (list.Table.Rows[0][0], list.Table.Rows[0][2]));
        Assert.Equal(("this", "Release v0.2"), (list.Table.Rows[1][0], list.Table.Rows[1][2]));
        Assert.True(host.Services.GetRequiredService<UiStateStore>().IsPinned(other.Id));
        await Run(runtime, other, commands, "/pin off");
        Assert.False(runtime.UiState.IsPinned(other.Id));
    }

    [Fact]
    public async Task Recap_says_why_when_nothing_can_write_one_and_recaps_when_something_can()
    {
        var generator = new FakeGenerator(Reply.Say("Planned."), Reply.Say("You planned the release."));
        await using var host = TestHost.Create(new FakeModels(generator));
        await using var runtime = await host.StartAsync();
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        var session = runtime.OpenSession();
        await session.SendAsync("plan the release", TestContext.Current.CancellationToken);

        var recap = await Run(runtime, session, commands, "/recap");

        Assert.Equal(("Recap", "You planned the release."), (recap.Title, Assert.Single(recap.Lines)));
        Assert.Equal("You planned the release.", session.Snapshot().Recap!.Text);
        Assert.Equal("Recap (nothing new since)", (await Run(runtime, session, commands, "/recap")).Title);
    }

    /// <summary>A4 review L8: a ui.db the commands cannot use (here one from a newer Corvid) costs the pins, not the command.</summary>
    [Fact]
    public async Task Sessions_and_pin_carry_on_when_ui_db_is_from_a_newer_corvid()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        var session = runtime.OpenSession();
        await session.SendAsync("plan the release", TestContext.Current.CancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(runtime.UiState.Path)!);
        using (var connection = new SqliteConnection($"Data Source={runtime.UiState.Path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=9;";
            command.ExecuteNonQuery();
        }

        var list = await Run(runtime, session, commands, "/sessions");
        var pin = await Run(runtime, session, commands, "/pin");

        Assert.Equal([session.Id], list.Table!.Rows.Select(row => row[1]));
        Assert.Contains(list.Lines, line => line.StartsWith("Pins are not shown:", StringComparison.Ordinal));
        Assert.Equal(OutputTone.Warning, pin.Tone);
        Assert.Contains("ui.db cannot be written", pin.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>A4 review L10: the ledger's problems reach the runtime's notices, which a surface shows its own way.</summary>
    [Fact]
    public async Task The_egress_ledgers_problems_are_runtime_notices()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        var heard = new List<string>();
        runtime.Notice += heard.Add;

        runtime.EgressLedger.Failed("the egress store could not be written: disk full");

        Assert.Equal(["egress ledger: the egress store could not be written: disk full"], heard);
        Assert.Equal(heard, runtime.Notices);
    }

    /// <summary>A4 review M4: leaving waits a bounded time for a title, then cancels it; nothing is written.</summary>
    [Fact]
    public async Task A_bounded_wait_cancels_a_title_still_being_written()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var generator = new FakeGenerator(Reply.Say("The boat is blue."), Reply.Say("Never written"))
        {
            // The title's call (the second step) waits until it is cancelled.
            Hold = (step, token) =>
            {
                if (step != 1)
                    return null;
                asked.TrySetResult();
                return Task.Delay(Timeout.Infinite, token);
            },
        };
        await using var host = TestHost.Create(new FakeModels(generator), new Dictionary<string, string?> { ["Corvid:Sessions:Titles"] = "true" });
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        await session.SendAsync("what colour is the boat?", TestContext.Current.CancellationToken);
        await asked.Task.WaitAsync(TestContext.Current.CancellationToken);
        var idle = await runtime.MaintenanceIdleAsync(TimeSpan.Zero);

        Assert.False(idle);
        // Cancelled, not waited for: the chain finishes without the gate ever opening.
        await runtime.MaintenanceIdleAsync().WaitAsync(TestContext.Current.CancellationToken);
        Assert.Null(SessionCatalogue.Describe(runtime.Log, session.Id, TestContext.Current.CancellationToken)!.Titled);
    }

    [Fact]
    public async Task A_memory_line_is_staged_for_review_and_a_shell_line_without_the_shell_tool_runs_nothing()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        var result = await session.RememberAsync("The release branch is main.", TestContext.Current.CancellationToken);
        Assert.Equal(Corvid.Memory.MemoryWriteStatus.Staged, result.Status);
        Assert.StartsWith("Staged for review: \"The release branch is main.\"", ComposerLine.Describe(result, "The release branch is main.").Text,
            StringComparison.Ordinal);
        Assert.Contains(runtime.Memory!.Writer!.ListPending(), pending => pending.Proposal.Text == "The release branch is main.");
        // Not a message: the model never sees the line.
        Assert.DoesNotContain(session.Snapshot().Transcript, entry => entry.Content is UserMessage);

        var delivery = await (await session.PostShellAsync("!ls", new ShellLine("ls"), TestContext.Current.CancellationToken));
        Assert.Equal(TurnStatus.Failed, delivery.Result!.Status);
        Assert.Contains("Corvid:Tools:Builtin", delivery.Result.Text, StringComparison.Ordinal);
        Assert.Empty(host.Models.Generator.Calls);
    }
}
