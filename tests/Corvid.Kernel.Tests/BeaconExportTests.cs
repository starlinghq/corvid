using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Traces;

namespace Corvid.Kernel.Tests;

/// <summary>
/// The Beacon export (C-027) against checked-in golden lines. <c>CORVID_RECORD_GOLDENS=1</c> rewrites
/// <c>Fixtures/beacon-export.*.jsonl</c>; review the diff against Beacon's schema before committing it.
/// </summary>
public sealed partial class BeaconExportTests
{
    private const string Session = "s-golden";

    private static readonly BeaconExportOptions Options = new("/work/project", "0.1.0-test", "linux");

    private static readonly string LongSecretOutput =
        "token=hunter2hunter2 and Authorization: Bearer abcdefghijklmnop.qrstuv " + new string('x', 5000) + " ghp_" + new string('A', 36);

    /// <summary>A hand-built session with fixed times: SqliteEventLog stamps the clock, so the golden is built from stored events.</summary>
    internal static IReadOnlyList<StoredEvent> Fixture(string session = Session)
    {
        var start = new DateTimeOffset(2026, 8, 21, 9, 14, 3, TimeSpan.Zero).AddTicks(1234567);
        SessionEvent[] payloads =
        [
            new UserMessage("Run the tests, note the failure, and check the weather."),
            new AssistantMessage("Running the tests first."),
            new ToolCallIssued("call-1", "shell", Json("""{"command":"dotnet test"}""")),
            new ApprovalRequested("approval-1", "shell", "dotnet test writes build output", "call-1"),
            new ApprovalResolved("approval-1", true, "allowed once"),
            new ToolResult("call-1", Json("""{"exit_code":1,"stdout":"Passed: 3","stderr":"Failed: 1"}""")),
            new ToolCallIssued("call-2", "write_file", Json("""{"path":"notes/todo.md","content":"- fix the failing test"}""")),
            new ToolResult("call-2", Json("""{"path":"notes/todo.md","bytes":22,"created":true}""")),
            new ToolCallIssued("call-3", "read_file", Json("""{"path":"README.md"}""")),
            new ToolResult("call-3", Json("\"1: # Readme\"")),
            new ToolCallIssued("call-4", "weather__lookup", Json("""{"city":"Leeds"}""")),
            new ToolResult("call-4", Json("""{"forecast":"sunny"}""")),
            new ToolCallIssued("call-5", "memory_search", Json("""{"query":"Sam's address"}""")),
            new ToolResult("call-5", Json("""{"items":[{"text":"Sam lives at 4 Elm Street"}]}""")),
            new ToolCallIssued("call-6", "read_file", Json("""{"path":"missing.txt"}""")),
            new ToolResult("call-6", Json("\"There is no file 'missing.txt'.\""), IsError: true),
            new ToolCallIssued("call-7", "shell", Json("""{"command":"cat .env"}""")),
            new ApprovalRequested("approval-2", "shell", "reads a secrets file", "call-7"),
            new ApprovalResolved("approval-2", false, "denied by the user"),
            new GoalSet("ship the fix"),
            new ToolCallIssued("call-8", "web_fetch", Json("""{"url":"https://example.com/status"}""")),
            new ToolResult("call-8", JsonSerializer.SerializeToElement(LongSecretOutput, BeaconTestJson.Default.String)),
            new AssistantMessage("One test fails; the weather is sunny."),
        ];
        return [.. payloads.Select((payload, i) => new StoredEvent(session, i + 1, Guid.Empty, start.AddSeconds(i), payload))];
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    [Theory]
    [InlineData(BeaconRetention.Full, "beacon-export.full.jsonl")]
    [InlineData(BeaconRetention.Metadata, "beacon-export.metadata.jsonl")]
    public void The_export_reproduces_its_golden_lines(BeaconRetention retention, string file)
    {
        var lines = BeaconExporter.Export(Fixture(), Options with { Retention = retention }).ToList();
        var actual = string.Join('\n', lines) + "\n";
        if (Environment.GetEnvironmentVariable("CORVID_RECORD_GOLDENS") == "1")
        {
            File.WriteAllText(Path.Combine(SourceFixtureDirectory(), file), actual);
            return;
        }
        var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", file)).ReplaceLineEndings("\n").Split('\n');
        var actualLines = actual.Split('\n');
        Assert.Equal(expected.Length, actualLines.Length);
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], actualLines[i]);
    }

    [Theory]
    [InlineData(BeaconRetention.Full)]
    [InlineData(BeaconRetention.Metadata)]
    public void Every_line_passes_beacons_validator(BeaconRetention retention)
    {
        foreach (var line in BeaconExporter.Export(Fixture(), Options with { Retention = retention }))
        {
            Assert.True(Encoding.UTF8.GetByteCount(line) <= BeaconExporter.MaxEventBytes);
            Assert.Null(Validate(JsonNode.Parse(line)!.AsObject()));
        }
    }

    [Fact]
    public void Skipped_kinds_produce_no_line_and_the_rest_map_to_beacon_actions()
    {
        var actions = BeaconExporter.Export(Fixture(), Options).Select(line => Node(line)["event"]!["action"]!.GetValue<string>()).ToList();
        Assert.Equal(
        [
            "prompt.submitted", "agent.message", "tool.invoked", "approval.requested", "approval.allowed", "command.executed",
            "tool.invoked", "file.modified", "tool.invoked", "file.read", "tool.invoked", "mcp.tool_invoked", "tool.invoked",
            "tool.completed", "tool.invoked", "tool.failed", "tool.invoked", "approval.requested", "approval.denied",
            "tool.invoked", "tool.completed", "agent.message",
        ], actions);
    }

    [Fact]
    public void Ids_are_the_same_in_both_retentions_and_with_local_memory_included()
    {
        var full = Ids(Options);
        Assert.Equal(full, Ids(Options with { Retention = BeaconRetention.Metadata }));
        Assert.Equal(full, Ids(Options with { IncludeLocalMemory = true }));
        Assert.Equal(full.Count, full.Distinct().Count());
        Assert.All(full, id => Assert.Matches(UuidV5(), id));
    }

    [Fact]
    public void Tool_and_command_events_get_beacons_call_identity()
    {
        var command = BeaconExporter.Export(Fixture("s-1"), Options).Select(Node)
            .Single(line => line["event"]!["action"]!.GetValue<string>() == "command.executed");
        // Python's uuid.uuid5(namespace, "call" + "s-1\0command.executed\0command:dotnet test|dotnet test\0call-1"): the
        // target is Beacon's canonicalTarget over command.command and tool.command, which repeats the line.
        Assert.Equal("812090eb-7fcf-5fca-b2c8-c192d46b37d8", command["event"]!["id"]!.GetValue<string>());
        Assert.Equal("0f908ad5-f1ce-535b-bd66-72166fa0fb6e", BeaconEventId.Uuid("content", "{}"));
    }

    [Fact]
    public void Memory_content_is_withheld_in_a_full_export_unless_asked_for()
    {
        static JsonObject MemoryResult(BeaconExportOptions options) => BeaconExporter.Export(Fixture(), options).Select(Node)
            .Single(line => line["gen_ai"]?["tool"]?["name"]?.GetValue<string>() == "memory_search"
                && line["event"]!["action"]!.GetValue<string>() == "tool.completed");

        var withheld = MemoryResult(Options);
        Assert.DoesNotContain("Elm Street", withheld.ToJsonString());
        Assert.Equal("metadata", withheld["content"]!["retention"]!.GetValue<string>());
        Assert.False(withheld["content"]!["included"]!.GetValue<bool>());
        Assert.Equal("Corvid tool completed", withheld["message"]!.GetValue<string>());

        var included = MemoryResult(Options with { IncludeLocalMemory = true });
        Assert.Contains("Elm Street", included["gen_ai"]!["tool"]!["call"]!["result"]!.GetValue<string>());
        Assert.Equal("full", included["content"]!["retention"]!.GetValue<string>());
        Assert.Equal(withheld["content"]!["hash"]!.GetValue<string>(), included["content"]!["hash"]!.GetValue<string>());
    }

    [Fact]
    public void Long_content_is_redacted_before_it_is_cut_and_keeps_its_hash_and_size()
    {
        var fetched = BeaconExporter.Export(Fixture(), Options).Select(Node)
            .Single(line => line["gen_ai"]?["tool"]?["name"]?.GetValue<string>() == "web_fetch"
                && line["event"]!["action"]!.GetValue<string>() == "tool.completed");
        var stored = fetched["gen_ai"]!["tool"]!["call"]!["result"]!.GetValue<string>();
        Assert.True(Encoding.UTF8.GetByteCount(stored) <= BeaconContent.StringLimit);
        Assert.EndsWith("...[truncated]", stored);
        Assert.DoesNotContain("hunter2", stored);
        Assert.DoesNotContain("abcdefghijklmnop", stored);
        Assert.StartsWith("token=[REDACTED] and Authorization: Bearer [REDACTED] ", stored);
        var content = fetched["content"]!.AsObject();
        Assert.True(content["redacted"]!.GetValue<bool>());
        Assert.True(content["truncated"]!.GetValue<bool>());
        Assert.Equal(Encoding.UTF8.GetByteCount(LongSecretOutput), content["bytes"]!.GetValue<int>());
        Assert.True(fetched["field_truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Metadata_retention_keeps_names_paths_commands_and_decisions_but_no_content()
    {
        var lines = BeaconExporter.Export(Fixture(), Options with { Retention = BeaconRetention.Metadata }).ToList();
        var text = string.Join('\n', lines);
        foreach (var content in new[] { "Run the tests", "Passed: 3", "Readme", "sunny", "Elm Street", "allowed once", "secrets file", "\"message\"" })
            Assert.DoesNotContain(content, text);
        foreach (var metadata in new[] { "dotnet test", "/work/project/notes/todo.md", "\"decision\":\"denied\"", "weather", "\"exit_code\":1" })
            Assert.Contains(metadata, text);
        Assert.All(lines.Select(Node).Where(line => line["content"] is not null), line =>
        {
            Assert.Equal("metadata", line["content"]!["retention"]!.GetValue<string>());
            Assert.Matches("^[0-9a-f]{64}$", line["content"]!["hash"]!.GetValue<string>());
        });
    }

    [Fact]
    public void An_event_over_the_cap_drops_content_in_order_and_says_so()
    {
        var capped = Options with { MaxEventBytes = 1500 };
        var command = BeaconExporter.Export(Fixture(), capped).Select(Node)
            .Single(line => line["event"]!["action"]!.GetValue<string>() == "tool.completed"
                && line["gen_ai"]!["tool"]!["name"]!.GetValue<string>() == "web_fetch");
        Assert.Null(command["gen_ai"]!["tool"]!["call"]!["result"]);
        Assert.Equal("call-8", command["gen_ai"]!["tool"]!["call"]!["id"]!.GetValue<string>());
        Assert.True(command["field_truncated"]!.GetValue<bool>());
        Assert.True(command["content"]!["truncated"]!.GetValue<bool>());
        Assert.True(Encoding.UTF8.GetByteCount(command.ToJsonString()) <= 1500);
    }

    [Fact]
    public void Timestamps_have_nine_fraction_digits_in_utc()
    {
        var value = new DateTimeOffset(2026, 8, 21, 11, 14, 3, TimeSpan.FromHours(2)).AddTicks(1234567);
        Assert.Equal("2026-08-21T09:14:03.123456700Z", BeaconExporter.Timestamp(value));
    }

    [Theory]
    [InlineData("export GITHUB_TOKEN=abc123def", "export GITHUB_TOKEN=[REDACTED]")]
    [InlineData("""{"api_key": "abc123", "n": 1}""", """{"api_key": "[REDACTED]", "n": 1}""")]
    [InlineData("password: 'two words'", "password: '[REDACTED]'")]
    [InlineData("curl -H 'Authorization: Bearer abc.def.ghi123'", "curl -H 'Authorization: Bearer [REDACTED]'")]
    [InlineData("key AKIAABCDEFGHIJKLMNOP here", "key AKIA[REDACTED] here")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", "ghp_[REDACTED]")]
    [InlineData("github_pat_abcdefghijklmnopqrstuvwxyz_0123", "github_pat_[REDACTED]")]
    [InlineData("xoxb-1234567890-abcdef", "xoxb-[REDACTED]")]
    [InlineData("sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "sk-[REDACTED]")]
    [InlineData("eyJhbGciOiJI.eyJzdWIiOiIx.abcdefghijk", "eyJ[REDACTED]")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEAabcdefgh\n-----END RSA PRIVATE KEY-----\nafter", "[REDACTED]\nafter")]
    [InlineData("input_tokens: 512, max_tokens=4096", "input_tokens: 512, max_tokens=4096")]
    [InlineData("password=[REDACTED] token: null", "password=[REDACTED] token: null")]
    public void The_redactor_replaces_known_credential_shapes(string text, string expected) =>
        Assert.Equal(expected, SecretRedactor.Redact(text));

    [Fact]
    public void Truncation_never_splits_a_character()
    {
        var text = new string('a', 4080) + "ééééééééééééééééééé";
        var cut = BeaconContent.Truncate(text, 4096);
        Assert.True(Encoding.UTF8.GetByteCount(cut) <= 4096);
        Assert.EndsWith("...[truncated]", cut);
        Assert.DoesNotContain((char)0xFFFD, cut);
        Assert.Equal("short", BeaconContent.Truncate("short", 4096));
    }

    [Fact]
    public void A_cut_is_flagged_even_when_the_marker_restores_the_length()
    {
        // 4095 UTF-16 chars, 4097 UTF-8 bytes: the cut text plus the 14-char marker is 4095 chars again.
        var text = new string('a', 4081) + new string('b', 13) + "\u4e2d";
        var retained = BeaconContent.Retain(text);
        Assert.Equal(text.Length, retained.Stored.Length);
        Assert.EndsWith("...[truncated]", retained.Stored);
        Assert.True(retained.Truncated);

        var line = JsonNode.Parse(BeaconExporter.Export(
            [new StoredEvent(Session, 1, Guid.Empty, DateTimeOffset.UnixEpoch, new UserMessage(text))], Options).Single())!.AsObject();
        Assert.True(line["field_truncated"]!.GetValue<bool>());
        Assert.True(line["content"]!["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Content_ids_do_not_depend_on_the_build_workspace_or_host()
    {
        var lines = BeaconExporter.Export(Fixture(), Options).ToList();
        var rebuilt = BeaconExporter.Export(Fixture(), Options with { HarnessVersion = "1.0.0+0123456789abcdef", Os = "darwin" });
        Assert.Equal(lines.Select(Id), rebuilt.Select(Id));
        Assert.Equal(lines.Select(Id), BeaconExporter.Export(Fixture(), Options with { HarnessVersion = "" }).Select(Id));

        // Another workspace: every event that names no workspace path keeps its id. A tool event resolves a relative path
        // against the workspace, so it acts on a different file and is rightly a different event.
        var moved = BeaconExporter.Export(Fixture(), Options with { WorkingDirectory = "/elsewhere" }).ToList();
        var pathless = Enumerable.Range(0, lines.Count).Where(i => !lines[i].Contains("/work/project/", StringComparison.Ordinal)).ToList();
        Assert.Contains(pathless, i => Action(lines[i]) == "prompt.submitted");
        Assert.Contains(pathless, i => Action(lines[i]) == "agent.message");
        Assert.Contains(pathless, i => Action(lines[i]).StartsWith("approval.", StringComparison.Ordinal));
        Assert.Equal(pathless.Select(i => Id(lines[i])), pathless.Select(i => Id(moved[i])));

        static string Id(string line) => JsonNode.Parse(line)!["event"]!["id"]!.GetValue<string>();
        static string Action(string line) => JsonNode.Parse(line)!["event"]!["action"]!.GetValue<string>();
    }

    // ---- Beacon's Event.Validate (pkg/asymptoteobserve/event.go), plus the timestamp and id formats ----

    internal static string? Validate(JsonObject e)
    {
        string? S(params string[] path)
        {
            JsonNode? node = e;
            foreach (var key in path)
                node = node is JsonObject o ? o[key] : null;
            return node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        }

        if (S("vendor") != "beacon")
            return "vendor must be beacon";
        if (S("product") != "endpoint-agent")
            return "product must be endpoint-agent";
        if (string.IsNullOrEmpty(S("schema_version")))
            return "schema_version is required";
        if (string.IsNullOrEmpty(S("event", "kind")) || string.IsNullOrEmpty(S("event", "action")))
            return "event.kind and event.action are required";
        if (string.IsNullOrEmpty(S("severity")))
            return "severity is required";
        if (string.IsNullOrEmpty(S("endpoint", "os")))
            return "endpoint.os is required";
        if (string.IsNullOrEmpty(S("harness", "name")))
            return "harness.name is required";
        if (S("origin") is { } origin && origin is not ("local" or "cloud" or "ci"))
            return "origin must be local, cloud, or ci";
        if (S("event", "fidelity") is { Length: > 0 } fidelity && fidelity is not ("observed" or "inferred"))
            return "event.fidelity must be observed or inferred";
        if (S("harness", "collection_method") is { Length: > 0 } method && method is not ("hook" or "otlp" or "plugin" or "poll"))
            return "harness.collection_method must be hook, otlp, plugin, or poll";
        if (S("content", "retention") is { Length: > 0 } retention && retention is not ("metadata" or "redacted" or "full"))
            return "content.retention must be metadata, redacted, or full";
        if (!BeaconTimestamp().IsMatch(S("timestamp") ?? ""))
            return "timestamp must be RFC 3339 UTC with nine fraction digits";
        if (!UuidV5().IsMatch(S("event", "id") ?? ""))
            return "event.id must be a version 5 UUID";
        return null;
    }

    private static List<string> Ids(BeaconExportOptions options) =>
        [.. BeaconExporter.Export(Fixture(), options).Select(line => Node(line)["event"]!["id"]!.GetValue<string>())];

    private static JsonObject Node(string line) => JsonNode.Parse(line)!.AsObject();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{9}Z$")]
    private static partial Regex BeaconTimestamp();

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-5[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$")]
    private static partial Regex UuidV5();

    private static string SourceFixtureDirectory([CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "Fixtures");
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class BeaconTestJson : System.Text.Json.Serialization.JsonSerializerContext;
