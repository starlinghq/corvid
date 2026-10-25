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

    [Fact]
    public async Task Two_processes_writing_at_once_lose_nothing()
    {
        var first = new UiStateStore(File);
        var second = new UiStateStore(File);
        first.Pinned();
        using var start = new Barrier(2);
        var token = TestContext.Current.CancellationToken;

        void Write(UiStateStore store, string prefix)
        {
            start.SignalAndWait(token);
            for (var i = 0; i < 100; i++)
            {
                store.SetDraft($"{prefix}-{i}", $"draft {i}", null, At);
                store.SetPinned($"{prefix}-{i}", true, At);
                store.SetPreferences(new Dictionary<string, string> { [$"{prefix}:Last"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture) }, At);
            }
        }

        await Task.WhenAll(Task.Run(() => Write(first, "app"), token), Task.Run(() => Write(second, "cli"), token));

        var pinned = new UiStateStore(File).Pinned();
        Assert.Equal(200, pinned.Count);
        Assert.Equal("draft 99", first.GetDraft("cli-99")!.Text);
        Assert.Equal("99", second.Preferences()["app:Last"]);
    }

    [Fact]
    public void A_file_from_a_newer_corvid_is_refused()
    {
        Directory.CreateDirectory(_root);
        using (var connection = new SqliteConnection($"Data Source={File};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version=2;";
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<InvalidDataException>(() => new UiStateStore(File).Pinned());
        Assert.Contains("newer Corvid", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_draft_longer_than_the_limit_is_cut()
    {
        var store = new UiStateStore(File);
        store.SetDraft("cli-1", new string('x', UiStateStore.MaxDraftLength + 10), null, At);
        Assert.Equal(UiStateStore.MaxDraftLength, store.GetDraft("cli-1")!.Text.Length);
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
