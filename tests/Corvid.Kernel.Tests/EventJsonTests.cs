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
        [typeof(MemoryWritten)] = "memory_written",
        [typeof(SkillChanged)] = "skill_changed",
        [typeof(GoalSet)] = "goal_set",
        [typeof(TodosSet)] = "todos_set",
        [typeof(CheckpointTaken)] = "checkpoint_taken",
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
