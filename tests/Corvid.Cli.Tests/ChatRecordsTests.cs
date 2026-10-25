using Corvid.Host.State;
using Corvid.Host.Tests;

namespace Corvid.Cli.Tests;

/// <summary>
/// The chat's side of A4's records: the <c>!</c> and <c>#</c> lines, <c>/rename</c> and <c>/sessions</c>, and what a resumed
/// session shows from <c>ui.db</c> (the app's draft, a recap after an absence).
/// </summary>
public sealed class ChatRecordsTests
{
    private static string Session(CliRun run) =>
        System.Text.RegularExpressions.Regex.Match(run.Output, "session (cli-[0-9a-f-]+)").Groups[1].Value;

    [Fact]
    public async Task A_memory_line_is_staged_for_review_and_never_sent_to_the_model()
    {
        using var run = new CliRun();

        await run.RunAsync(["chat"], "# the release branch is main", "/memory pending", "#", "/exit");

        Assert.Contains("Staged for review: \"the release branch is main\"", run.Output, StringComparison.Ordinal);
        Assert.Contains("the release branch is main", run.Output[run.Output.IndexOf("Staged memory writes", StringComparison.Ordinal)..],
            StringComparison.Ordinal);
        Assert.Contains("Type the fact to remember after #", run.Output, StringComparison.Ordinal);
        Assert.Empty(run.Models!.Generator.Calls);
    }

    [Fact]
    public async Task A_shell_line_goes_through_the_shell_tool_and_says_so_when_it_is_off()
    {
        using var run = new CliRun();

        await run.RunAsync(["chat"], "!git status", "/exit");

        Assert.Contains("The shell tool is off (Corvid:Tools:Builtin)", run.Output, StringComparison.Ordinal);
        Assert.Empty(run.Models!.Generator.Calls);
    }

    [Fact]
    public async Task Rename_shows_in_sessions_and_on_resume_with_the_apps_draft()
    {
        using var run = new CliRun();
        await run.RunAsync(["chat"], "first message", "/rename Release planning", "/sessions", "/exit");
        var id = Session(run);
        Assert.Contains("Renamed to \"Release planning\".", run.Output, StringComparison.Ordinal);
        Assert.Contains("Release planning", run.Output[run.Output.IndexOf("Sessions", StringComparison.Ordinal)..], StringComparison.Ordinal);
        // The app keeps a draft for the session in ui.db.
        new UiStateStore(Path.Combine(run.Root, "data", "ui.db")).SetDraft(id, "and then check the changelog", null, DateTimeOffset.UtcNow);

        await run.RunAsync(["--session", id], "/exit");

        Assert.Contains("Title: \"Release planning\"", run.Output, StringComparison.Ordinal);
        Assert.Contains("A draft from the app is waiting for this session: and then check the changelog", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_resumed_after_an_absence_shows_a_recap()
    {
        using var run = new CliRun(new FakeModels(new FakeGenerator(Reply.Say("Planned."), Reply.Say("You planned the release."))));
        await run.RunAsync(["chat"], "plan the release", "/exit");
        var id = Session(run);
        // Seen two hours ago: the person has been away.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(run.Root, "data", "ui.db")};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE seen SET at=$at;";
            command.Parameters.AddWithValue("$at", DateTime.UtcNow.AddHours(-2).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        // The log's own last event is recent, so only ui.db's seen time says the person was away.

        await run.RunAsync(["--session", id, "--set", "Sessions:RecapAfterMinutes=60"], "/exit");

        Assert.Contains("While you were away", run.Output, StringComparison.Ordinal);
        Assert.Contains("You planned the release.", run.Output, StringComparison.Ordinal);
    }
}
