using System.Net;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Tests;

public sealed class SwitchTests : IDisposable
{
    private const string Session = "s";
    private const string Generator = ModelSlots.Generator;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempLog _db = new();
    private readonly ModelBinding _local = Profiles.Chat(Profiles.Local("qwen3.5-0.8b"));
    private readonly ModelBinding _server = Profiles.Chat(Profiles.LocalServer("qwen3.5-27b"));
    private readonly ModelBinding _cloudA = Profiles.Chat(Profiles.Cloud("cloud-a"));
    private readonly ModelBinding _cloudB = Profiles.Chat(Profiles.Cloud("cloud-b"));

    public void Dispose() => _db.Dispose();

    private SessionModelSlots Slots(RecordingHooks hooks, IEventLog? log = null, IEnumerable<ModelBinding>? fallbacks = null) =>
        new(new ModelSlotRegistry().Bind(Generator, _local, escalation: [_server, _cloudA], fallbacks: fallbacks ?? [_cloudB, _local]),
            log ?? _db.Log, Session, hooks.Options);

    private long Append(params SessionEvent[] events)
    {
        var head = _db.Log.GetHead(Session, Ct);
        return _db.Log.Append(Session, head, events.Select(EventData.Create).ToArray(), Ct)[^1].Sequence;
    }

    private IReadOnlyList<SessionEvent> Events() => _db.Log.Read(Session, cancellationToken: Ct).Select(item => item.Payload).ToArray();

    private IEnumerable<ModelSwitched> Switches() => Events().OfType<ModelSwitched>();

    private static void AssertSwitch(ModelSwitched change, ModelBinding from, ModelBinding to, string reason)
    {
        Assert.Equal(Generator, change.Slot);
        Assert.Equal(from.Profile, ModelProfile.FromSnapshot(change.OldProfile));
        Assert.Equal(to.Profile, ModelProfile.FromSnapshot(change.NewProfile));
        Assert.Equal(reason, change.Reason);
    }

    [Fact]
    public async Task Mid_turn_switch_waits_for_issued_calls_keeps_approvals_and_continues_on_the_new_model()
    {
        var hooks = new RecordingHooks(allowCloud: true);
        var slots = Slots(hooks);
        var client = slots.GetChatClient(Generator);

        Append(new UserMessage("tidy my notes folder"));                                       // 1
        var first = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "tidy my notes folder")], cancellationToken: Ct);
        Append(new ToolCallIssued("c1", "files.list", Profiles.Json("""{"path":"notes"}""")));   // 2
        var lease = slots.TrackToolCall("c1");
        Append(new ApprovalRequested("a1", "rm notes/old.md", "deletes a file"));                // 3

        // The user types /model while the tool is still running.
        var switching = slots.SwitchAsync(Generator, _cloudA, SwitchReason.User, cancellationToken: Ct);

        Assert.False(switching.IsCompleted);
        Assert.Equal(3, _db.Log.GetHead(Session, Ct));
        Assert.Empty(hooks.PrivacyChecks);
        Assert.Same(_local, slots.Current(Generator));

        Append(new ToolResult("c1", Profiles.Json("""["old.md","todo.md"]""")));                  // 4
        lease.Dispose();
        var result = await switching;                                                           // 5

        Assert.Equal(SwitchStatus.Switched, result.Status);
        Assert.Equal(5, result.Sequence);
        Assert.Equal(["a1"], result.PendingApprovals);
        Assert.Equal(BudgetAction.ExpandCompactedRanges, result.Budget);
        var check = Assert.Single(hooks.PrivacyChecks);
        Assert.Equal((4L, PrivacyClass.Local, PrivacyClass.Cloud), (check.Head, check.From.Privacy, check.To.Privacy));
        var budget = Assert.Single(hooks.BudgetChanges);
        Assert.Equal((5L, _local.Profile, _cloudA.Profile), (budget.SwitchSequence, budget.From, budget.To));

        // Continue the turn: the same client now reaches the new model.
        var second = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "continue")], cancellationToken: Ct);
        Append(new AssistantMessage(second.Text));                                               // 6

        Assert.Equal(("qwen3.5-0.8b", "cloud-a"), (first.Text, second.Text));
        Assert.Equal((1, 1), (_local.Client().Calls.Count, _cloudA.Client().Calls.Count));
        Assert.Collection(Events(),
            e => Assert.IsType<UserMessage>(e),
            e => Assert.IsType<ToolCallIssued>(e),
            e => Assert.IsType<ApprovalRequested>(e),
            e => Assert.IsType<ToolResult>(e),
            e => AssertSwitch(Assert.IsType<ModelSwitched>(e), _local, _cloudA, "user"),
            e => Assert.Equal("cloud-a", Assert.IsType<AssistantMessage>(e).Text));

        var snapshot = SessionProjection.Load(_db.Log, Session, cancellationToken: Ct);
        Assert.Equal("rm notes/old.md", Assert.Single(snapshot.PendingApprovals).Value.Action);
        Assert.Equal(_cloudA.Profile, ModelProfile.FromSnapshot(snapshot.ModelProfiles[Generator]));
        Assert.Empty(snapshot.Issues);
    }

    [Fact]
    public async Task A_model_call_in_flight_finishes_on_the_old_model()
    {
        var slots = Slots(new RecordingHooks(allowCloud: true));
        var client = slots.GetChatClient(Generator);
        _local.Client().Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        var inFlight = client.GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct);
        var result = await slots.SwitchAsync(Generator, _server, SwitchReason.Escalation, cancellationToken: Ct);
        _local.Client().Gate!.SetResult();

        Assert.Equal(SwitchStatus.Switched, result.Status);
        Assert.Equal("qwen3.5-0.8b", (await inFlight).Text);
        Assert.Equal("qwen3.5-27b", (await client.GetResponseAsync([new ChatMessage(ChatRole.User, "next")], cancellationToken: Ct)).Text);
    }

    [Fact]
    public async Task An_issued_call_with_no_result_and_no_lease_blocks_the_switch()
    {
        var slots = Slots(new RecordingHooks(allowCloud: true));
        Append(new ToolCallIssued("orphan", "shell", Profiles.Json("{}")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => slots.SwitchAsync(Generator, _server, cancellationToken: Ct));

        Assert.Contains("orphan", error.Message, StringComparison.Ordinal);
        Assert.Single(Events());
        Assert.Same(_local, slots.Current(Generator));
    }

    [Fact]
    public async Task A_call_parked_at_an_approval_prompt_does_not_block_a_switch()
    {
        var slots = Slots(new RecordingHooks(allowCloud: true));
        Append(new ToolCallIssued("c1", "shell", Profiles.Json("""{"command":"rm notes/old.md"}""")),
            new ApprovalRequested("a1", "rm notes/old.md", "deletes a file"));
        var parked = slots.ParkToolCall("c1");

        // The user types /model at the approval prompt.
        var result = await slots.SwitchAsync(Generator, _server, cancellationToken: Ct);

        Assert.Equal(SwitchStatus.Switched, result.Status);
        Assert.Equal(["a1"], result.PendingApprovals);
        Assert.Equal(["c1"], result.UnfinishedToolCalls);
        Assert.IsType<ModelSwitched>(Events()[^1]);

        // Approved: the call runs, so the next switch waits for it again.
        parked.Dispose();
        var lease = slots.TrackToolCall("c1");
        Append(new ApprovalResolved("a1", true, "once"));
        var back = slots.SwitchAsync(Generator, _local, cancellationToken: Ct);
        Assert.False(back.IsCompleted);
        Append(new ToolResult("c1", Profiles.Json("{}")));
        lease.Dispose();
        Assert.Equal(SwitchStatus.Switched, (await back).Status);
        Assert.Empty((await back).UnfinishedToolCalls);
    }

    [Fact]
    public async Task An_approved_parked_call_becomes_tracked_without_a_gap()
    {
        var slots = Slots(new RecordingHooks(allowCloud: true));
        Append(new ToolCallIssued("c1", "shell", Profiles.Json("""{"command":"rm notes/old.md"}""")));
        var parked = slots.ParkToolCall("c1");

        var running = slots.RunParkedToolCall("c1");
        // The park's lease is released after the move; the call is still tracked, so a switch waits for its result.
        parked.Dispose();
        var switching = slots.SwitchAsync(Generator, _server, cancellationToken: Ct);
        Assert.False(switching.IsCompleted);
        Append(new ToolResult("c1", Profiles.Json("{}")));
        running.Dispose();

        Assert.Equal(SwitchStatus.Switched, (await switching).Status);
        Assert.Throws<InvalidOperationException>(() => slots.RunParkedToolCall("c1"));
        Assert.Throws<InvalidOperationException>(() => slots.RunParkedToolCall("never-parked"));
    }

    [Fact]
    public async Task A_tool_whose_model_call_fails_falls_back_without_waiting_for_itself()
    {
        var slots = Slots(new RecordingHooks(allowCloud: true), fallbacks: [_server]);
        var client = slots.GetChatClient(Generator);
        Append(new ToolCallIssued("c1", "summarize_page", Profiles.Json("{}")));
        using var lease = slots.TrackToolCall("c1");
        _local.Client().FailWith(Profiles.Status(HttpStatusCode.ServiceUnavailable));

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "summarize")], cancellationToken: Ct)
            .WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal("qwen3.5-27b", response.Text);
        AssertSwitch(Assert.Single(Switches()), _local, _server, "fallback");
    }

    [Fact]
    public async Task A_switch_waiting_for_a_tool_does_not_block_that_tools_fallback()
    {
        var slots = Slots(new RecordingHooks(allowCloud: true), fallbacks: [_server]);
        var client = slots.GetChatClient(Generator);
        Append(new ToolCallIssued("c1", "summarize_page", Profiles.Json("{}")));
        var lease = slots.TrackToolCall("c1");
        var switching = slots.SwitchAsync(Generator, _cloudA, cancellationToken: Ct);
        Assert.False(switching.IsCompleted);

        _local.Client().FailWith(Profiles.Status(HttpStatusCode.TooManyRequests));
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "summarize")], cancellationToken: Ct)
            .WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Append(new ToolResult("c1", Profiles.Json("{}")));
        lease.Dispose();
        var result = await switching.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Equal("qwen3.5-27b", response.Text);
        Assert.Equal(SwitchStatus.Switched, result.Status);
        Assert.Collection(Switches(),
            change => AssertSwitch(change, _local, _server, "fallback"),
            change => AssertSwitch(change, _server, _cloudA, "user"));
    }

    [Fact]
    public async Task A_failing_default_listed_as_the_last_resort_falls_back_from_the_top_of_the_chain()
    {
        // The usual shape: cloud B first, then the local default as the last resort.
        var slots = Slots(new RecordingHooks(allowCloud: true), fallbacks: [_cloudB, _local]);
        _local.Client().FailWith(Profiles.Status(HttpStatusCode.ServiceUnavailable), Profiles.Status(HttpStatusCode.ServiceUnavailable));

        var response = await slots.GetChatClient(Generator).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct);
        Assert.Equal("cloud-b", response.Text);
        _cloudB.Client().FailWith(Profiles.Status(HttpStatusCode.BadGateway));

        // Within one request a binding that already failed is not tried again.
        await slots.SwitchAsync(Generator, _local, cancellationToken: Ct);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            slots.GetChatClient(Generator).GetResponseAsync([new ChatMessage(ChatRole.User, "again")], cancellationToken: Ct));
        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Equal(2, _local.Client().Calls.Count);
        Assert.Collection(Switches(),
            change => AssertSwitch(change, _local, _cloudB, "fallback"),
            change => AssertSwitch(change, _cloudB, _local, "user"),
            change => AssertSwitch(change, _local, _cloudB, "fallback"));
    }

    [Fact]
    public async Task Moving_to_cloud_passes_the_privacy_gate_and_a_refusal_records_nothing()
    {
        var hooks = new RecordingHooks(allowCloud: false);
        var slots = Slots(hooks);
        Append(new UserMessage("my account number is 1234"));

        var refused = await slots.SwitchAsync(Generator, _cloudA, cancellationToken: Ct);
        var local = await slots.SwitchAsync(Generator, _server, cancellationToken: Ct);

        Assert.Equal((SwitchStatus.Refused, "contains PII"), (refused.Status, refused.Detail));
        Assert.Equal(SwitchStatus.Switched, local.Status);
        // Only the cloud target was checked.
        Assert.Equal("cloud-a", Assert.Single(hooks.PrivacyChecks).To.Id);
        AssertSwitch(Assert.Single(Switches()), _local, _server, "user");
        Assert.Same(_server, slots.Current(Generator));
    }

    [Fact]
    public async Task Without_a_configured_gate_cloud_is_refused()
    {
        var slots = new SessionModelSlots(new ModelSlotRegistry().Bind(Generator, _local), _db.Log, Session);

        var result = await slots.SwitchAsync(Generator, _cloudA, cancellationToken: Ct);

        Assert.Equal(SwitchStatus.Refused, result.Status);
        Assert.Empty(Events());
    }

    [Fact]
    public async Task Budget_hook_sees_every_switch_and_reports_the_direction()
    {
        var hooks = new RecordingHooks(allowCloud: true);
        var slots = Slots(hooks);

        var up = await slots.SwitchAsync(Generator, _cloudA, cancellationToken: Ct);
        var down = await slots.SwitchAsync(Generator, _local, cancellationToken: Ct);
        var same = await slots.SwitchAsync(Generator, Profiles.Chat(Profiles.Local("other-0.8b")), cancellationToken: Ct);

        Assert.Equal([BudgetAction.ExpandCompactedRanges, BudgetAction.CompactBeforeNextCall, BudgetAction.None],
            [up.Budget, down.Budget, same.Budget]);
        Assert.Equal([1L, 2L, 3L], hooks.BudgetChanges.Select(change => change.SwitchSequence));
    }

    [Fact]
    public async Task Switching_to_the_current_binding_appends_nothing()
    {
        var hooks = new RecordingHooks(allowCloud: true);
        var slots = Slots(hooks);

        var result = await slots.SwitchAsync(Generator, _local, pin: true, cancellationToken: Ct);

        Assert.Equal(SwitchStatus.Unchanged, result.Status);
        Assert.Empty(Events());
        Assert.Empty(hooks.BudgetChanges);
        Assert.True(slots.IsPinned(Generator));
    }

    [Fact]
    public async Task Fallback_chain_is_a_series_of_switch_events()
    {
        var slots = Slots(new RecordingHooks(allowCloud: true), fallbacks: [_cloudA, _cloudB, _local]);
        await slots.SwitchAsync(Generator, _cloudA, cancellationToken: Ct);
        _cloudA.Client().FailWith(Profiles.Status(HttpStatusCode.TooManyRequests));
        _cloudB.Client().FailWith(Profiles.Status(HttpStatusCode.ServiceUnavailable));

        var response = await slots.GetChatClient(Generator).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct);

        Assert.Equal("qwen3.5-0.8b", response.Text);
        Assert.Collection(Switches(),
            change => AssertSwitch(change, _local, _cloudA, "user"),
            change => AssertSwitch(change, _cloudA, _cloudB, "fallback"),
            change => AssertSwitch(change, _cloudB, _local, "fallback"));
        // The same history went to every provider.
        Assert.Equal("hi", Assert.Single(Assert.Single(_cloudB.Client().Calls)).Text);
    }

    [Fact]
    public async Task Fallback_skips_targets_the_privacy_gate_refuses()
    {
        var hooks = new RecordingHooks(allowCloud: false);
        var slots = Slots(hooks, fallbacks: [_cloudB, _server]);
        _local.Client().FailWith(Profiles.Status(HttpStatusCode.InternalServerError));

        var response = await slots.GetChatClient(Generator).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct);

        Assert.Equal("qwen3.5-27b", response.Text);
        Assert.Equal("cloud-b", Assert.Single(hooks.PrivacyChecks).To.Id);
        AssertSwitch(Assert.Single(Switches()), _local, _server, "fallback");
    }

    [Fact]
    public async Task An_exhausted_chain_rethrows_the_provider_error()
    {
        var slots = Slots(new RecordingHooks(allowCloud: true), fallbacks: [_server]);
        _local.Client().FailWith(Profiles.Status(HttpStatusCode.BadGateway));
        _server.Client().FailWith(Profiles.Status(HttpStatusCode.TooManyRequests));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            slots.GetChatClient(Generator).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct));

        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
        AssertSwitch(Assert.Single(Switches()), _local, _server, "fallback");
    }

    [Fact]
    public async Task Client_errors_do_not_fall_back()
    {
        var slots = Slots(new RecordingHooks(allowCloud: true));
        _local.Client().FailWith(Profiles.Status(HttpStatusCode.BadRequest));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            slots.GetChatClient(Generator).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct));

        Assert.Empty(Events());
        Assert.False(FallbackErrors.IsProviderFailure(new InvalidOperationException()));
        Assert.True(FallbackErrors.IsProviderFailure(new HttpRequestException("connection refused")));
    }

    [Fact]
    public async Task Streaming_falls_back_before_the_first_update()
    {
        var slots = Slots(new RecordingHooks(allowCloud: true), fallbacks: [_cloudB]);
        _local.Client().FailWith(Profiles.Status(HttpStatusCode.ServiceUnavailable));

        var text = string.Concat(await slots.GetChatClient(Generator)
            .GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct)
            .Select(update => update.Text).ToArrayAsync(Ct));

        Assert.Equal("cloud-b!", text);
        AssertSwitch(Assert.Single(Switches()), _local, _cloudB, "fallback");
    }

    [Fact]
    public async Task Escalation_climbs_the_ladder_and_de_escalation_returns_to_the_default()
    {
        var slots = Slots(new RecordingHooks(allowCloud: true));

        var first = await slots.EscalateAsync(Generator, Ct);
        var second = await slots.EscalateAsync(Generator, Ct);
        var top = await slots.EscalateAsync(Generator, Ct);
        var back = Assert.Single(await slots.CompleteTurnAsync(Ct));
        var idle = await slots.CompleteTurnAsync(Ct);

        Assert.Equal((SwitchStatus.Switched, SwitchStatus.Switched, SwitchStatus.NoTarget), (first.Status, second.Status, top.Status));
        Assert.Equal(SwitchStatus.Switched, back.Status);
        Assert.Empty(idle);
        Assert.Same(_local, slots.Current(Generator));
        Assert.Collection(Switches(),
            change => AssertSwitch(change, _local, _server, "escalation"),
            change => AssertSwitch(change, _server, _cloudA, "escalation"),
            change => AssertSwitch(change, _cloudA, _local, "de_escalation"));
    }

    [Fact]
    public async Task A_pinned_model_is_not_de_escalated()
    {
        var slots = Slots(new RecordingHooks(allowCloud: true));

        await slots.SwitchAsync(Generator, _server, SwitchReason.User, pin: true, cancellationToken: Ct);
        Assert.Empty(await slots.CompleteTurnAsync(Ct));
        Assert.Same(_server, slots.Current(Generator));

        Assert.True(await slots.UnpinAsync(Generator, Ct));
        Assert.Equal(SwitchStatus.Switched, Assert.Single(await slots.CompleteTurnAsync(Ct)).Status);
        Assert.Same(_local, slots.Current(Generator));
    }

    [Fact]
    public async Task A_concurrent_append_is_rechecked_before_the_switch_is_recorded()
    {
        var log = new InterferingLog(_db.Log)
        {
            BeforeNextAppend = () => new ApprovalRequested("late", "git push", "publishes"),
        };
        var slots = Slots(new RecordingHooks(allowCloud: true), log);

        var result = await slots.SwitchAsync(Generator, _server, cancellationToken: Ct);

        Assert.Equal((2L, "late"), (result.Sequence, Assert.Single(result.PendingApprovals)));
        Assert.IsType<ModelSwitched>(Events()[1]);
    }

    [Fact]
    public async Task Rebinding_the_registry_does_not_change_a_running_session()
    {
        var registry = new ModelSlotRegistry().Bind(Generator, _local);
        var slots = new SessionModelSlots(registry, _db.Log, Session);

        registry.Bind(Generator, _server);

        Assert.Same(_local, slots.Current(Generator));
        // De-escalation returns to the registry's current default, through a logged switch.
        Assert.Equal(SwitchStatus.Switched, Assert.Single(await slots.CompleteTurnAsync(Ct)).Status);
        AssertSwitch(Assert.Single(Switches()), _local, _server, "de_escalation");
    }
}
