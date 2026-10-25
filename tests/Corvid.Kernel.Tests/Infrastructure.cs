using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;
using Microsoft.Data.Sqlite;

namespace Corvid.Kernel.Tests;

/// <summary>An event database in its own temporary directory, removed with its WAL files on dispose.</summary>
internal sealed class TempLog : IDisposable
{
    public TempLog()
    {
        Root = Path.Combine(Path.GetTempPath(), "corvid-events-" + Guid.NewGuid().ToString("N"));
        FilePath = Path.Combine(Root, "nested", "events.db");
        Log = new SqliteEventLog(FilePath);
    }

    public string Root { get; }

    public string FilePath { get; }

    public SqliteEventLog Log { get; }

    public SqliteEventLog Reopen() => new(FilePath);

    public SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = FilePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

internal static class Samples
{
    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>One instance of every event type, with nested values populated.</summary>
    public static IReadOnlyList<SessionEvent> All() =>
    [
        new UserMessage("move the dentist reminder to friday"),
        new AssistantMessage("Done."),
        new ToolCallIssued("call-1", "cron.update", Json("""{"id":14,"day":"friday"}""")),
        new ToolResult("call-1", Json("""{"ok":true}"""), IsError: false),
        new Decision("d-1", "intent", ImmutableDictionary<string, double>.Empty.Add("use_tools", 0.82).Add("answer_directly", 0.18),
            0.82, 0.6, "use_tools"),
        new Decision("d-2", "tool", ImmutableDictionary<string, double>.Empty.Add("cron.update", 0.9).Add("none", 0.1),
            0.8, 0.45, "offer_tool:cron.update", "2026-10-18.1", "kev-0.8b", Json("""{"user_message":"move it","step":0}"""),
            new AskedQuestion("choice", Json("\"Which tool should run first?\""),
                [new AskedOption("cron.update", "change a reminder"), new AskedOption("none")])),
        new DecisionOutcomeRecorded("d-1", "tool_succeeded"),
        new ApprovalRequested("a-1", "rm -rf build", "deletes files"),
        new ApprovalResolved("a-1", Approved: true, "user approved"),
        new ModelSwitched("generator", Json("""{"name":"qwen3.5-0.8b"}"""), Json("""{"name":"cloud"}"""), "escalated"),
        new Compacted(1, 2, new CompactionSummary("move reminder", ["no cloud"], ["use cron.update"], ["notes.md"],
            ["tell sam?"], "message sam")),
        new Compacted(1, 4, new CompactionSummary(null, [], [], [], [], null), Summarizer: "qwen3.5-0.8b@cpu", Chunks: 2),
        new CompactionExpanded(5),
        new MemoryWritten("m-1", "insert"),
        new SkillChanged("s-1", "r2", "agent"),
        new GoalSet("ship C-002"),
        new GoalSet(null),
        new TodosSet([new TodoItem("t-1", "write tests", Completed: false), new TodoItem("t-2", "docs", Completed: true)]),
        new CheckpointTaken("c-1", "abc123"),
        new CheckpointTaken("c-2", "def456", "call-1", CheckpointKinds.After, ["notes.md", "src/a b.cs"]),
        new SessionRewound(4, "undo", ["notes.md"], "c-3"),
        new DeliveryQueued("d-1", "telegram", "42", "hello", "reply", ReplyTo: "7", SourceSession: "gw-telegram-42", SourceSequence: 9),
        new DeliveryQueued("d-2", "slack", "C1", "job done", "job", ThreadId: "1700000000.0001", Tainted: true),
        new DeliveryResolved("d-1", DeliveryStatus.Delivered, "8"),
        new DeliveryResolved("d-2", DeliveryStatus.Failed, Detail: "chat not found"),
        new DetectionMatched("secret-read-then-egress", 1, "high", "enforce", "credential read then egress", [3, 7], "call-9", "ask"),
        new DetectionMatched("fork-bomb", 2, "high", "detect", "fork bomb", [4]),
        new ProjectBound("0f0e0d0c0b0a09080706050403020100f0e0d0c0b0a090807060504030201000", "remote"),
        new McpToolHeld("notes", "add", "changed", "ab12", PinnedHash: "cd34"),
        new McpToolHeld("notes", "save", "flagged", "ef56", Detail: "<IMPORTANT"),
        new McpToolAccepted("notes", "add", "ab12"),
        new McpToolAccepted("notes", "save", "ef56", Flagged: true),
        new McpToolHeld("notes", "send", "injection", "ab78", PinnedHash: "ab78", Detail: "d-9"),
        new McpPinsForgotten("notes", 3),
        new UserSteered("also check the retry count"),
        new ClarificationAsked("q-1", "0.2.0, or 0.2.0-rc1?", ["0.2.0", "0.2.0-rc1"]),
        new UserMessage("!!cat notes.md", LocalOnly: true),
        new ToolCallIssued("user-shell", "shell", Json("""{"command":"cat notes.md"}"""), 1, false, LocalOnly: true),
        new ToolResult("user-shell", Json("""{"exit_code":0}"""), LocalOnly: true),
        new SessionTitled("Move the dentist reminder", TitleSources.Generated),
        new SessionRecapped("You moved the reminder to Friday.", 0, 4, RecapSources.Requested),
        new ApprovalAssessed("call-1", ApprovalResolutions.RuleAllowed, "smart", [new AssessmentReason("rule_allowed", "a remembered approval matches")],
            Rule: "`dotnet test` under /work", RuleScope: "session", Effects: ["execute", "write"], Contained: true),
        new ApprovalAssessed("call-2", ApprovalResolutions.Automatic, "permissive", [],
            Risk: new AssessedRisk("d-9", "auto_run", 0, "none", 0.97, true, "kev-0.8b")),
        new EgressRecorded("r-1", "claude@anthropic", EgressOutcomes.Redacted,
            ImmutableDictionary<string, int>.Empty.Add("email", 1).Add("person", 2), 3, 4096, "redacted email, person", Tainted: true,
            Slot: "generator", Host: "api.anthropic.com", Secret: "anthropic.key"),
        new EgressRecorded("r-2", "claude@anthropic", EgressOutcomes.Sent, ImmutableDictionary<string, int>.Empty, 0, 12, "no PII found",
            Unkept: "the egress store could not be written"),
        new ChildSpawned("cli-child-1", "summarize the notes", ["list_notes"], new ChildBudget(4, 8_000, 120), "agent", Tainted: true,
            Mix: Json("""{"generator":"qwen3.5-9b"}"""), OutputSchema: Json("""{"type":"object"}"""), CallId: "call-9"),
        new ChildSpawned("cli-child-2", "look it up", [], new ChildBudget(8, 64_000, 600), "user"),
        new ChildFinished("cli-child-1", ChildStatuses.Answered, 2, 1_234, Text: "{\"notes\":3}", OutputValid: true),
        new ChildFinished("cli-child-2", ChildStatuses.TimedOut, 1, 99, Tainted: true, Detail: "ran past 120 s"),
        new ChildOf("cli-parent", 1, "summarize the notes", ["list_notes"], new ChildBudget(4, 8_000, 120), Json("""{"generator":"qwen3.5-9b"}""")),
    ];

    public static EventData[] Data(params SessionEvent[] events) => events.Select(EventData.Create).ToArray();
}
