using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;
using Corvid.Vault.Contracts;

namespace Corvid.Kernel.Tests;

/// <summary>The credential scrub on the event log (C-040): which fields of which events, and the log applying it.</summary>
public sealed class EventScrubberTests : IDisposable
{
    // Fictitious, in GitHub's classic shape.
    private const string Token = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8";
    private const string Marker = "ghp_[REDACTED:github-token:";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-scrub-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string Persisted(SessionEvent payload) => EventJson.Serialize(payload);

    [Fact]
    public void Tool_call_arguments_are_scrubbed_and_the_call_keeps_its_id_name_and_round()
    {
        var call = new ToolCallIssued("call-1", "shell", Json($$"""{"command":"export GH_TOKEN={{Token}} && gh repo list"}"""), 2, false);

        var scrubbed = Assert.IsType<ToolCallIssued>(EventScrubber.Scrub(call));

        Assert.Equal(("call-1", "shell", 2, (bool?)false), (scrubbed.CallId, scrubbed.ToolName, scrubbed.Round, scrubbed.ReadOnly));
        Assert.StartsWith("export GH_TOKEN=" + Marker, scrubbed.Arguments.GetProperty("command").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Token, Persisted(scrubbed), StringComparison.Ordinal);
    }

    [Fact]
    public void A_tool_result_is_scrubbed_in_a_string_and_in_an_object()
    {
        var text = Assert.IsType<ToolResult>(EventScrubber.Scrub(new ToolResult("c", Json($"\"GH_TOKEN={Token}\""))));
        var json = Assert.IsType<ToolResult>(EventScrubber.Scrub(new ToolResult("c", Json("""{"stdout":"ok","password":"hunter2-x"}"""), IsError: true)));

        Assert.StartsWith("GH_TOKEN=" + Marker, text.Result.GetString(), StringComparison.Ordinal);
        Assert.Equal("[REDACTED:labelled-value]", json.Result.GetProperty("password").GetString());
        Assert.True(json.IsError);
    }

    [Fact]
    public void Assistant_text_approval_text_and_queued_deliveries_are_scrubbed()
    {
        var reply = Assert.IsType<AssistantMessage>(EventScrubber.Scrub(new AssistantMessage($"Your token is {Token}.")));
        var approval = Assert.IsType<ApprovalRequested>(EventScrubber.Scrub(new ApprovalRequested("a1",
            $$"""{"tool":"shell","command":"curl -H 'Authorization: Bearer {{Token}}'"}""", $"runs with {Token}", "c1")));
        var delivery = Assert.IsType<DeliveryQueued>(EventScrubber.Scrub(new DeliveryQueued("d1", "slack", "C1", $"token {Token}", "reply",
            SourceSession: "s", SourceSequence: 4)));

        Assert.StartsWith($"Your token is {Marker}", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, approval.Action, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, approval.Reason, StringComparison.Ordinal);
        Assert.Equal(("a1", "c1"), (approval.ApprovalId, approval.CallId));
        Assert.DoesNotContain(Token, delivery.Text, StringComparison.Ordinal);
        Assert.Equal(("d1", "C1", 4L), (delivery.DeliveryId, delivery.ChatId, delivery.SourceSequence!.Value));
    }

    [Fact]
    public void A_decisions_state_and_question_instructions_are_scrubbed_and_its_scores_kept()
    {
        var decision = new Decision("d1", "pre_execute/c1", ImmutableDictionary<string, double>.Empty.Add("yes", 0.9), 0.9, 0.8, "acted",
            "v1", "kev", Json($$"""{"last_tool_result":"read: GH_TOKEN={{Token}}"}"""),
            new AskedQuestion("noul", Json($"\"Run shell with GH_TOKEN={Token}?\""), [new AskedOption("yes")]), true);

        var scrubbed = Assert.IsType<Decision>(EventScrubber.Scrub(decision));

        Assert.DoesNotContain(Token, Persisted(scrubbed), StringComparison.Ordinal);
        Assert.Contains(Marker, scrubbed.State!.Value.GetRawText(), StringComparison.Ordinal);
        Assert.Contains(Marker, scrubbed.Question!.Instructions.GetString(), StringComparison.Ordinal);
        Assert.Equal(("noul", "yes"), (scrubbed.Question.Kind, scrubbed.Question.Options.Single().Name));
        Assert.Equal((0.9, 0.8, "acted", true), (scrubbed.Confidence, scrubbed.Threshold, scrubbed.Action, scrubbed.Calibrated!.Value));
    }

    [Fact]
    public void A_user_message_is_a_recorded_decision_and_is_not_scrubbed()
    {
        var message = new UserMessage($"here is my token {Token}");

        Assert.Same(message, EventScrubber.Scrub(message));
    }

    [Fact]
    public void Ids_revisions_and_paths_are_not_scrubbed()
    {
        SessionEvent[] events =
        [
            new CheckpointTaken("cp-1", "3f786850e387550fdab836ed7e6dc881de23001b", "c1", CheckpointKinds.After, ["secrets/token=abc123.txt"]),
            new MemoryWritten("fact-1", "add"),
            new SkillChanged("deploy", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", "agent"),
        ];

        foreach (var payload in events)
            Assert.Same(payload, EventScrubber.Scrub(payload));
    }

    [Fact]
    public void An_event_with_nothing_to_scrub_keeps_its_instance_and_persisted_form()
    {
        var placeholder = Placeholders.Format("github-token", "Ab3dEf6hIj9kLm2nOp5qRs8t");
        SessionEvent[] events =
        [
            new ToolCallIssued("c1", "shell", Json($$"""{"command":"curl -H 'Authorization: Bearer {{placeholder}}' https://api.github.com"}""")),
            new ToolResult("c1", Json("""{"stdout":"HEAD is now at 3f786850e387550fdab836ed7e6dc881de23001b","exit":0}""")),
            new AssistantMessage("Done."),
        ];

        foreach (var payload in events)
            Assert.Same(payload, EventScrubber.Scrub(payload));
    }

    [Fact]
    public void The_log_stores_the_scrubbed_event_and_returns_what_it_stored()
    {
        var log = new SqliteEventLog(Path.Combine(_root, "events.db"));
        var ct = TestContext.Current.CancellationToken;

        var appended = log.Append("s", 0, [EventData.Create(new ToolResult("c1", Json($"\"export GH_TOKEN={Token}\"")))], ct);

        var returned = Assert.IsType<ToolResult>(appended.Single().Payload);
        var read = Assert.IsType<ToolResult>(log.Read("s", cancellationToken: ct).Single().Payload);
        Assert.StartsWith("export GH_TOKEN=" + Marker, returned.Result.GetString(), StringComparison.Ordinal);
        Assert.Equal(returned.Result.GetRawText(), read.Result.GetRawText());
    }
}
