using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Corvid.Abstractions.Events;
using Corvid.Cli.Chat;
using Corvid.Kernel.Events;

namespace Corvid.Cli.Tests;

/// <summary><c>corvid traces export</c> (C-027) end to end, on a temporary data directory.</summary>
public sealed class TracesCommandTests
{
    private static async Task<(int Exit, string Stdout)> Export(CliRun run, params string[] args)
    {
        using var stdout = new MemoryStream();
        var context = new CliContext(run.Console, new ScriptedChatInput([]))
        {
            ConfigureServices = run.Models!.Register,
            Streams = new CliStreams(Stream.Null, stdout, TextWriter.Null),
        };
        var exit = await CorvidCli.RunAsync([.. run.Settings(), "traces", "export", .. args], context, TestContext.Current.CancellationToken);
        return (exit, Encoding.UTF8.GetString(stdout.ToArray()));
    }

    private static string LogPath(CliRun run) => Path.Combine(run.Root, "data", "events.db");

    private static void Seed(CliRun run)
    {
        var log = new SqliteEventLog(LogPath(run));
        log.Append("s-a", 0,
        [
            EventData.Create(new UserMessage("hello")),
            EventData.Create(new ToolCallIssued("c1", "shell", JsonDocument.Parse("""{"command":"ls"}""").RootElement.Clone())),
            EventData.Create(new ToolResult("c1", JsonDocument.Parse("""{"exit_code":0,"stdout":"a.txt","stderr":""}""").RootElement.Clone())),
            EventData.Create(new GoalSet("skipped")),
            EventData.Create(new AssistantMessage("one file")),
        ]);
        log.Append("s-b", 0, [EventData.Create(new UserMessage("second session"))]);
    }

    private static List<JsonObject> Lines(string text) =>
        [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonNode.Parse(line)!.AsObject())];

    [Fact]
    public async Task One_session_is_written_to_stdout_as_beacon_lines()
    {
        using var run = new CliRun();
        Seed(run);
        var (exit, stdout) = await Export(run, "--session", "s-a");

        Assert.Equal(ExitCodes.Success, exit);
        var lines = Lines(stdout);
        Assert.Equal(["prompt.submitted", "tool.invoked", "command.executed", "agent.message"],
            lines.Select(line => line["event"]!["action"]!.GetValue<string>()));
        Assert.All(lines, line =>
        {
            Assert.Equal("s-a", line["session"]!["id"]!.GetValue<string>());
            Assert.Equal(Path.Combine(run.Root, "work"), line["session"]!["working_directory"]!.GetValue<string>());
            Assert.Equal("corvid", line["harness"]!["name"]!.GetValue<string>());
            // The CLI's own version, as before the export moved to the host (A3 review nit).
            Assert.Equal(Corvid.Host.Audit.TraceExport.VersionOf(typeof(TracesCommand).Assembly), line["harness"]!["version"]!.GetValue<string>());
            Assert.Equal("1.0", line["schema_version"]!.GetValue<string>());
        });
        Assert.Equal("hello", lines[0]["prompt"]!["text"]!.GetValue<string>());
        Assert.Equal("ls", lines[2]["command"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public async Task All_sessions_export_with_the_same_ids_as_one_session_and_metadata_drops_content()
    {
        using var run = new CliRun();
        Seed(run);
        var (_, single) = await Export(run, "--session", "s-a");
        var (exit, all) = await Export(run, "--all", "--retention", "metadata");

        Assert.Equal(ExitCodes.Success, exit);
        var lines = Lines(all);
        Assert.Equal(["s-a", "s-a", "s-a", "s-a", "s-b"], lines.Select(line => line["session"]!["id"]!.GetValue<string>()));
        Assert.Equal(Lines(single).Select(line => line["event"]!["id"]!.GetValue<string>()),
            lines.Take(4).Select(line => line["event"]!["id"]!.GetValue<string>()));
        Assert.DoesNotContain("hello", all);
        Assert.DoesNotContain("second session", all);
        Assert.Contains("\"command\":\"ls\"", all);
    }

    [Fact]
    public async Task Out_writes_an_owner_only_file_and_refuses_to_overwrite()
    {
        using var run = new CliRun();
        Seed(run);
        var file = Path.Combine(run.Root, "trace.jsonl");

        var (exit, stdout) = await Export(run, "--all", "--out", file);
        Assert.Equal(ExitCodes.Success, exit);
        Assert.Equal("", stdout);
        Assert.Equal(5, Lines(File.ReadAllText(file)).Count);
        Assert.Contains("Wrote 5 event(s) from 2 session(s)", run.Output, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));

        var before = File.ReadAllText(file);
        (exit, _) = await Export(run, "--session", "s-b", "--out", file);
        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Contains("exists", run.Output, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(file));
    }

    [Fact]
    public async Task An_unknown_session_a_missing_log_and_bad_arguments_fail_without_creating_a_log()
    {
        using var run = new CliRun();
        var (exit, _) = await Export(run, "--all");
        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Contains("There is no event log", run.Output, StringComparison.Ordinal);
        Assert.False(File.Exists(LogPath(run)));

        Seed(run);
        (exit, _) = await Export(run, "--session", "nope");
        Assert.Equal(ExitCodes.Failure, exit);
        Assert.Contains("Session 'nope' has no events.", run.Output, StringComparison.Ordinal);

        Assert.Equal(ExitCodes.Usage, (await Export(run)).Exit);
        Assert.Equal(ExitCodes.Usage, (await Export(run, "--all", "--session", "s-a")).Exit);
        Assert.Equal(ExitCodes.Usage, (await Export(run, "--all", "--retention", "redacted")).Exit);
        Assert.Equal(ExitCodes.Usage, (await Export(run, "--all", "--bogus")).Exit);
    }
}
