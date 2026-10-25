using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;

namespace Corvid.Kernel.Tests;

public sealed class EventJsonTests
{
    // Persisted discriminators. Changing one silently orphans every stored event of that type.
    private static readonly Dictionary<Type, string> WireNames = new()
    {
        [typeof(UserMessage)] = "user_message",
        [typeof(AssistantMessage)] = "assistant_message",
        [typeof(ToolCallIssued)] = "tool_call_issued",
        [typeof(ToolResult)] = "tool_result",
        [typeof(Decision)] = "decision",
        [typeof(DecisionOutcomeRecorded)] = "decision_outcome_recorded",
        [typeof(ApprovalRequested)] = "approval_requested",
        [typeof(ApprovalResolved)] = "approval_resolved",
        [typeof(ModelSwitched)] = "model_switched",
        [typeof(Compacted)] = "compacted",
        [typeof(CompactionExpanded)] = "compaction_expanded",
        [typeof(MemoryWritten)] = "memory_written",
        [typeof(SkillChanged)] = "skill_changed",
        [typeof(GoalSet)] = "goal_set",
        [typeof(TodosSet)] = "todos_set",
        [typeof(CheckpointTaken)] = "checkpoint_taken",
        [typeof(SessionRewound)] = "session_rewound",
        [typeof(DeliveryQueued)] = "delivery_queued",
        [typeof(DeliveryResolved)] = "delivery_resolved",
        [typeof(DetectionMatched)] = "detection_matched",
        [typeof(ProjectBound)] = "project_bound",
        [typeof(McpToolHeld)] = "mcp_tool_held",
        [typeof(McpToolAccepted)] = "mcp_tool_accepted",
        [typeof(McpPinsForgotten)] = "mcp_pins_forgotten",
        [typeof(UserSteered)] = "user_steered",
        [typeof(ClarificationAsked)] = "clarification_asked",
    };

    [Fact]
    public void Discriminators_are_the_pinned_wire_names()
    {
        var declared = typeof(SessionEvent).GetCustomAttributes<JsonDerivedTypeAttribute>()
            .ToDictionary(attribute => attribute.DerivedType, attribute => (string)attribute.TypeDiscriminator!);

        Assert.Equal(WireNames.OrderBy(pair => pair.Value), declared.OrderBy(pair => pair.Value));
        var concrete = typeof(SessionEvent).Assembly.GetTypes()
            .Where(type => type.IsSubclassOf(typeof(SessionEvent)) && !type.IsAbstract);
        Assert.All(concrete, type => Assert.Contains(type, declared.Keys));
    }

    public static TheoryData<int> EveryEvent() => [.. Enumerable.Range(0, Samples.All().Count)];

    [Theory]
    [MemberData(nameof(EveryEvent))]
    public void Every_event_round_trips(int index)
    {
        var value = Samples.All()[index];

        var json = EventJson.Serialize(value);
        var restored = EventJson.Deserialize(EventJson.Version, json);

        Assert.IsType(value.GetType(), restored);
        // Records holding JsonElement or immutable collections have reference equality, so compare the wire form.
        Assert.Equal(json, EventJson.Serialize(restored));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(WireNames[value.GetType()], document.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public void Project_bound_has_its_documented_persisted_form()
    {
        var json = EventJson.Serialize(new ProjectBound("abc", "checkout"));
        Assert.Equal("""{"type":"project_bound","project_id":"abc","source":"checkout"}""", json);
        Assert.Equal(new ProjectBound("abc", "checkout"), EventJson.Deserialize(EventJson.Version, json));
    }

    [Fact]
    public void Persisted_form_is_snake_case_with_a_leading_discriminator()
    {
        var json = EventJson.Serialize(new ToolResult("call-1", Samples.Json("""{"ok":true}"""), IsError: true));

        Assert.Equal("""{"type":"tool_result","call_id":"call-1","result":{"ok":true},"is_error":true}""", json);
    }

    [Fact]
    public void Stored_payloads_from_the_documented_format_still_read()
    {
        const string stored = """
            {"type":"decision","decision_id":"d-9","question_id":"risk","probabilities":{"low":0.9,"high":0.1},
             "confidence":0.9,"threshold":0.75,"action":"execute"}
            """;

        var decision = Assert.IsType<Decision>(EventJson.Deserialize(1, stored));

        Assert.Equal("risk", decision.QuestionId);
        Assert.Equal(0.1, decision.Probabilities["high"]);
        Assert.Equal("execute", decision.Action);
        Assert.Null(decision.CatalogueVersion);
        Assert.Null(decision.Decider);
    }

    [Fact]
    public void Decision_fabric_fields_are_written_only_when_set()
    {
        var probabilities = ImmutableDictionary<string, double>.Empty.Add("yes", 1);
        var before = new Decision("d-1", "done", probabilities, 1, 0.8, "stop");

        // Events written before C-004a keep their persisted form.
        Assert.Equal("""{"type":"decision","decision_id":"d-1","question_id":"done","probabilities":{"yes":1},"confidence":1,"threshold":0.8,"action":"stop"}""",
            EventJson.Serialize(before));
        var json = EventJson.Serialize(before with { CatalogueVersion = "2026-10-18.1", Decider = "degraded" });
        Assert.EndsWith("""
            "action":"stop","catalogue_version":"2026-10-18.1","decider":"degraded"}
            """, json, StringComparison.Ordinal);
        var restored = Assert.IsType<Decision>(EventJson.Deserialize(EventJson.Version, json));
        Assert.Equal(("2026-10-18.1", "degraded"), (restored.CatalogueVersion, restored.Decider));
    }

    [Fact]
    public void Decision_state_and_question_are_written_only_when_set()
    {
        var probabilities = ImmutableDictionary<string, double>.Empty.Add("yes", 1);
        var decision = new Decision("d-1", "done", probabilities, 1, 0.8, "stop", "v1", "degraded",
            Samples.Json("""{"step":2}"""), new AskedQuestion("noul", Samples.Json("\"Done?\""), [new AskedOption("yes"), new AskedOption("no")]), Calibrated: false);

        var json = EventJson.Serialize(decision);

        Assert.EndsWith("""
            "decider":"degraded","state":{"step":2},"question":{"kind":"noul","instructions":"Done?","options":[{"name":"yes"},{"name":"no"}]},"calibrated":false}
            """, json, StringComparison.Ordinal);
        var restored = Assert.IsType<Decision>(EventJson.Deserialize(EventJson.Version, json));
        Assert.Equal(2, restored.State!.Value.GetProperty("step").GetInt32());
        Assert.Equal(["yes", "no"], restored.Question!.Options.Select(option => option.Name));
        Assert.False(restored.Calibrated);
        var bare = EventJson.Serialize(decision with { State = null, Question = null, Calibrated = null });
        Assert.DoesNotContain("\"state\"", bare, StringComparison.Ordinal);
        Assert.DoesNotContain("\"calibrated\"", bare, StringComparison.Ordinal);
    }

    [Fact]
    public void Checkpoint_ledger_fields_are_written_only_when_set_so_older_checkpoints_keep_their_form()
    {
        Assert.Equal("""{"type":"checkpoint_taken","checkpoint_id":"c-1","revision":"abc"}""", EventJson.Serialize(new CheckpointTaken("c-1", "abc")));
        Assert.Equal("""{"type":"checkpoint_taken","checkpoint_id":"c-2","revision":"def","call_id":"call-1","kind":"after","paths":["a.txt"]}""",
            EventJson.Serialize(new CheckpointTaken("c-2", "def", "call-1", CheckpointKinds.After, ["a.txt"])));
        var stored = Assert.IsType<CheckpointTaken>(EventJson.Deserialize(1, """{"type":"checkpoint_taken","checkpoint_id":"k","revision":"r"}"""));
        Assert.Null(stored.CallId);
        Assert.Null(stored.Paths);
    }

    [Fact]
    public void An_approvals_scope_and_note_are_written_only_when_set_so_older_answers_keep_their_form()
    {
        Assert.Equal("""{"type":"approval_resolved","approval_id":"a-1","approved":false,"reason":"user: denied (once)"}""",
            EventJson.Serialize(new ApprovalResolved("a-1", false, "user: denied (once)")));
        var json = EventJson.Serialize(new ApprovalResolved("a-1", false, "user: denied (once)", "once", "not now"));
        Assert.EndsWith("""
            "reason":"user: denied (once)","scope":"once","note":"not now"}
            """, json, StringComparison.Ordinal);
        var restored = Assert.IsType<ApprovalResolved>(EventJson.Deserialize(EventJson.Version, json));
        Assert.Equal(("once", "not now"), (restored.Scope, restored.Note));
        var stored = Assert.IsType<ApprovalResolved>(EventJson.Deserialize(1, """{"type":"approval_resolved","approval_id":"a","approved":true,"reason":"user: approved (session)"}"""));
        Assert.Equal((null, null), (stored.Scope, stored.Note));
    }

    [Theory]
    [InlineData("""{"type":"no_such_event","text":"x"}""")]
    [InlineData("""{"text":"missing discriminator"}""")]
    [InlineData("""{"type":"user_message"}""")]
    [InlineData("""{"type":"user_message","text":null}""")]
    [InlineData("null")]
    [InlineData("not json")]
    public void Invalid_payloads_are_rejected_as_invalid_data(string json)
    {
        Assert.Throws<InvalidDataException>(() => EventJson.Deserialize(EventJson.Version, json));
    }

    [Fact]
    public void Unknown_payload_versions_are_rejected()
    {
        var json = EventJson.Serialize(new UserMessage("hi"));

        Assert.Throws<InvalidDataException>(() => EventJson.Deserialize(EventJson.Version + 1, json));
    }
}
