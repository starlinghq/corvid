using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;

namespace Corvid.Models.Tests;

/// <summary>A5: the session default, the sticky "this session" switch, a session's own mix, and the change callback.</summary>
public sealed class SessionDefaultTests : IDisposable
{
    private const string Session = "s";
    private const string Generator = ModelSlots.Generator;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempLog _db = new();
    private readonly ModelBinding _local = Profiles.Chat(Profiles.Local("qwen3.5-0.8b"));
    private readonly ModelBinding _server = Profiles.Chat(Profiles.LocalServer("qwen3.5-27b"));
    private readonly ModelBinding _cloudA = Profiles.Chat(Profiles.Cloud("cloud-a"));
    private readonly ModelBinding _other = Profiles.Chat(Profiles.Local("qwen3.5-9b"));
    private readonly List<SwitchResult> _changes = [];

    public void Dispose() => _db.Dispose();

    private ModelSlotRegistry Registry() => new ModelSlotRegistry().Bind(Generator, _local, escalation: [_server, _cloudA]);

    private SessionModelSlots Slots(ModelSlotRegistry? registry = null, IReadOnlyDictionary<string, SlotConfiguration>? own = null,
        bool allowCloud = true) =>
        new(registry ?? Registry(), _db.Log, Session, new RecordingHooks(allowCloud).Options with { Changed = _changes.Add }, own);

    private IEnumerable<ModelSwitched> Switches() =>
        _db.Log.Read(Session, cancellationToken: Ct).Select(item => item.Payload).OfType<ModelSwitched>();

    [Fact]
    public async Task A_session_switch_is_logged_as_session_and_lasts_past_the_turn()
    {
        var slots = Slots();

        var result = await slots.SetSessionDefaultAsync(Generator, _other, cancellationToken: Ct);
        var after = await slots.CompleteTurnAsync(Ct);

        Assert.Equal((SwitchStatus.Switched, true), (result.Status, result.SessionDefaultChanged));
        Assert.Empty(after);
        Assert.Same(_other, slots.Current(Generator));
        Assert.Same(_other, slots.SessionDefault(Generator));
        Assert.True(slots.HasSessionChoice(Generator));
        Assert.Equal("session", Assert.Single(Switches()).Reason);
    }

    [Fact]
    public async Task Escalation_moves_off_the_session_choice_and_de_escalation_returns_to_it_not_to_the_registry_default()
    {
        var slots = Slots();
        await slots.SetSessionDefaultAsync(Generator, _other, cancellationToken: Ct);

        var up = await slots.EscalateAsync(Generator, Ct);
        var back = Assert.Single(await slots.CompleteTurnAsync(Ct));

        Assert.Equal((SwitchStatus.Switched, _server.Profile), (up.Status, up.To));
        Assert.Equal((SwitchStatus.Switched, _other.Profile), (back.Status, back.To));
        Assert.Same(_other, slots.Current(Generator));
        Assert.Equal(["session", "escalation", "de_escalation"], Switches().Select(change => change.Reason));
    }

    [Fact]
    public async Task A_session_choice_on_the_ladder_escalates_past_its_rung()
    {
        var slots = Slots();
        await slots.SetSessionDefaultAsync(Generator, _server, cancellationToken: Ct);

        var up = await slots.EscalateAsync(Generator, Ct);

        Assert.Equal(_cloudA.Profile, up.To);
    }

    [Fact]
    public async Task A_turn_switch_still_returns_to_the_session_default()
    {
        var slots = Slots();
        await slots.SetSessionDefaultAsync(Generator, _other, cancellationToken: Ct);

        await slots.SwitchAsync(Generator, _server, SwitchScope.Turn, Ct);
        var back = Assert.Single(await slots.CompleteTurnAsync(Ct));

        Assert.Equal(_other.Profile, back.To);
        Assert.Equal(["session", "user", "de_escalation"], Switches().Select(change => change.Reason));
    }

    [Fact]
    public async Task A_pin_is_kept_distinct_from_the_session_default()
    {
        var slots = Slots();
        await slots.SwitchAsync(Generator, _other, SwitchScope.Pin, Ct);
        await slots.EscalateAsync(Generator, Ct);

        // Pinned: the turn's end leaves the escalated model.
        Assert.Empty(await slots.CompleteTurnAsync(Ct));
        Assert.Same(_server, slots.Current(Generator));

        // Unpinned: back to the session choice, not the registry default.
        Assert.True(await slots.UnpinAsync(Generator, Ct));
        Assert.Equal(_other.Profile, Assert.Single(await slots.CompleteTurnAsync(Ct)).To);
    }

    [Fact]
    public async Task Choosing_the_model_the_slot_already_uses_changes_the_default_and_logs_it_in_place()
    {
        var slots = Slots();
        await slots.EscalateAsync(Generator, Ct);

        var result = await slots.SetSessionDefaultAsync(Generator, _server, cancellationToken: Ct);
        var again = await slots.SetSessionDefaultAsync(Generator, _server, cancellationToken: Ct);

        Assert.Equal((SwitchStatus.Unchanged, true), (result.Status, result.SessionDefaultChanged));
        Assert.NotNull(result.Sequence);
        Assert.Empty(await slots.CompleteTurnAsync(Ct));
        // A5 review: logged from the model to itself, so a session opened again restores the choice; the same choice again
        // (same pin) logs nothing more.
        var logged = Switches().ToArray();
        Assert.Equal(["escalation", "session"], logged.Select(change => change.Reason));
        Assert.Equal((_server.Profile.Id, _server.Profile.Id, (bool?)false),
            (ModelProfile.FromSnapshot(logged[1].OldProfile).Id, ModelProfile.FromSnapshot(logged[1].NewProfile).Id, logged[1].Pinned));
        Assert.Equal((SwitchStatus.Unchanged, false, (long?)null), (again.Status, again.SessionDefaultChanged, again.Sequence));
        Assert.Contains(_changes, change => change is { Status: SwitchStatus.Unchanged, SessionDefaultChanged: true });
    }

    [Fact]
    public async Task Pinning_the_choice_in_use_and_unpinning_it_are_logged()
    {
        var slots = Slots();
        await slots.SetSessionDefaultAsync(Generator, _other, cancellationToken: Ct);

        await slots.SetSessionDefaultAsync(Generator, _other, pin: true, Ct);
        Assert.True(slots.IsPinned(Generator));
        Assert.True(await slots.UnpinAsync(Generator, Ct));
        Assert.False(await slots.UnpinAsync(Generator, Ct));

        var logged = Switches().ToArray();
        Assert.Equal(["session", "session", "unpin"], logged.Select(change => change.Reason));
        Assert.Equal(new bool?[] { false, true, false }, logged.Select(change => change.Pinned));
        Assert.All(logged.Skip(1), change => Assert.Equal(_other.Profile.Id, ModelProfile.FromSnapshot(change.NewProfile).Id));
    }

    [Fact]
    public async Task A_reset_that_leaves_a_pinned_slot_where_it_is_is_still_logged()
    {
        var slots = Slots();
        await slots.SwitchAsync(Generator, _other, SwitchScope.Pin, Ct);
        await slots.EscalateAsync(Generator, Ct);

        var cleared = await slots.ClearSessionDefaultAsync(Generator, Ct);

        Assert.Equal((SwitchStatus.Unchanged, true), (cleared!.Status, cleared.SessionDefaultChanged));
        Assert.False(slots.HasSessionChoice(Generator));
        Assert.Same(_server, slots.Current(Generator));
        var reset = Switches().Last();
        Assert.Equal(("de_escalation", (bool?)true), (reset.Reason, reset.Reset));
        // A6 (A5 left): logged with its pin, so a replay shows the slot still pinned.
        Assert.True(reset.Pinned);
        Assert.True(slots.IsPinned(Generator));
        Assert.Equal(_server.Profile.Id, ModelProfile.FromSnapshot(reset.NewProfile).Id);
        Assert.Equal(_db.Log.Read(Session, cancellationToken: Ct).Last(item => item.Payload is ModelSwitched).Sequence, cleared.Sequence);
    }

    [Fact]
    public async Task A_reset_of_a_choice_that_is_the_configured_default_is_logged()
    {
        var slots = Slots();
        await slots.SetSessionDefaultAsync(Generator, _local, cancellationToken: Ct);

        var cleared = await slots.ClearSessionDefaultAsync(Generator, Ct);

        Assert.Equal((SwitchStatus.Unchanged, true), (cleared!.Status, cleared.SessionDefaultChanged));
        Assert.False(slots.HasSessionChoice(Generator));
        var reset = Switches().Last();
        Assert.Equal(("de_escalation", (bool?)true), (reset.Reason, reset.Reset));
        // Not pinned: the reset says nothing of a pin.
        Assert.Null(reset.Pinned);
        Assert.Equal(_local.Profile.Id, ModelProfile.FromSnapshot(reset.NewProfile).Id);
    }

    [Fact]
    public async Task A_reset_that_switches_back_says_it_was_a_reset()
    {
        var slots = Slots();
        await slots.SetSessionDefaultAsync(Generator, _other, cancellationToken: Ct);

        await slots.ClearSessionDefaultAsync(Generator, Ct);

        var reset = Switches().Last();
        Assert.Equal(("de_escalation", (bool?)true), (reset.Reason, reset.Reset));
        Assert.Equal(_local.Profile.Id, ModelProfile.FromSnapshot(reset.NewProfile).Id);
    }

    [Fact]
    public async Task A_refused_session_switch_changes_neither_the_binding_nor_the_default()
    {
        var slots = Slots(allowCloud: false);

        var result = await slots.SetSessionDefaultAsync(Generator, _cloudA, cancellationToken: Ct);

        Assert.Equal(SwitchStatus.Refused, result.Status);
        Assert.Same(_local, slots.Current(Generator));
        Assert.False(slots.HasSessionChoice(Generator));
        Assert.Empty(Switches());
        Assert.Empty(_changes);
    }

    [Fact]
    public async Task Clearing_the_choice_returns_to_the_configured_default()
    {
        var registry = Registry();
        var slots = Slots(registry);
        await slots.SetSessionDefaultAsync(Generator, _other, cancellationToken: Ct);

        var cleared = await slots.ClearSessionDefaultAsync(Generator, Ct);
        var again = await slots.ClearSessionDefaultAsync(Generator, Ct);

        Assert.Equal((SwitchStatus.Switched, true), (cleared!.Status, cleared.SessionDefaultChanged));
        Assert.Null(again);
        Assert.Same(_local, slots.Current(Generator));
        // With no choice, a registry rebinding is followed again.
        registry.Bind(Generator, _server);
        Assert.Same(_server, slots.SessionDefault(Generator));
    }

    [Fact]
    public async Task A_session_that_chose_nothing_follows_the_registry_and_one_that_chose_does_not()
    {
        var registry = Registry();
        var plain = Slots(registry);
        var chose = new SessionModelSlots(registry, _db.Log, "t", new RecordingHooks(true).Options);
        await chose.SetSessionDefaultAsync(Generator, _other, cancellationToken: Ct);

        registry.Bind(Generator, _server);

        Assert.Same(_server, plain.SessionDefault(Generator));
        Assert.Same(_other, chose.SessionDefault(Generator));
        Assert.Empty(await chose.CompleteTurnAsync(Ct));
    }

    [Fact]
    public async Task A_sessions_own_mix_replaces_the_registry_for_its_slots()
    {
        var own = new Dictionary<string, SlotConfiguration>
        {
            [Generator] = new(Generator, _other, [_cloudA], []),
        };
        var slots = Slots(own: own);

        Assert.Same(_other, slots.Current(Generator));
        Assert.Equal([Generator], slots.OwnSlots);
        var up = await slots.EscalateAsync(Generator, Ct);
        Assert.Equal(_cloudA.Profile, up.To);
        Assert.Equal(_other.Profile, Assert.Single(await slots.CompleteTurnAsync(Ct)).To);
    }

    [Fact]
    public async Task A_mix_slot_that_may_not_escalate_has_nowhere_to_go()
    {
        var own = new Dictionary<string, SlotConfiguration> { [Generator] = new(Generator, _other, [], []) };
        var slots = Slots(own: own);

        var up = await slots.EscalateAsync(Generator, Ct);

        Assert.Equal(SwitchStatus.NoTarget, up.Status);
        Assert.Same(_other, slots.Current(Generator));
    }

    [Fact]
    public void A_mix_configuration_is_checked_against_the_slot_contract()
    {
        var embedder = new ModelBinding(Profiles.Local("e5"), new object());
        var wrong = new Dictionary<string, SlotConfiguration> { [Generator] = new(Generator, embedder, ImmutableArray<ModelBinding>.Empty, []) };
        var misfiled = new Dictionary<string, SlotConfiguration> { [Generator] = new(ModelSlots.Summarizer, _other, [], []) };

        Assert.Throws<ArgumentException>(() => Slots(own: wrong));
        Assert.Throws<ArgumentException>(() => Slots(own: misfiled));
    }

    [Fact]
    public async Task The_change_callback_hears_every_switch_and_a_throwing_handler_does_not_fail_it()
    {
        var heard = new List<SwitchReason>();
        var slots = new SessionModelSlots(Registry(), _db.Log, Session, new RecordingHooks(true).Options with
        {
            Changed = result =>
            {
                heard.Add(result.Reason);
                throw new InvalidOperationException("a surface failed");
            },
        });

        await slots.SetSessionDefaultAsync(Generator, _other, cancellationToken: Ct);
        await slots.EscalateAsync(Generator, Ct);
        await slots.CompleteTurnAsync(Ct);

        Assert.Equal([SwitchReason.Session, SwitchReason.Escalation, SwitchReason.DeEscalation], heard);
    }

    [Fact]
    public async Task A_slot_nothing_binds_can_be_chosen_for_the_session_and_let_go_of()
    {
        var decider = new ModelBinding(Profiles.Local("kev-4b"), new FakeDecider());
        var slots = Slots();
        Assert.False(slots.TryCurrent(ModelSlots.Decider, out _));

        var chosen = await slots.SetSessionDefaultAsync(ModelSlots.Decider, decider, cancellationToken: Ct);
        Assert.Empty(await slots.CompleteTurnAsync(Ct));
        var cleared = await slots.ClearSessionDefaultAsync(ModelSlots.Decider, Ct);

        Assert.Equal((SwitchStatus.Switched, ModelProfile.UnboundId), (chosen.Status, chosen.From.Id));
        Assert.Equal((SwitchStatus.Switched, ModelProfile.UnboundId), (cleared!.Status, cleared.To!.Id));
        Assert.False(slots.TryCurrent(ModelSlots.Decider, out _));
        var logged = Switches().ToArray();
        Assert.Equal(["session", "de_escalation"], logged.Select(change => change.Reason));
        Assert.Equal(ModelProfile.UnboundId, ModelProfile.FromSnapshot(logged[0].OldProfile).Id);
    }

    private sealed class FakeDecider : Corvid.Abstractions.Fabric.IDecider
    {
        public string Name => "kev-4b";

        public ValueTask<Corvid.Abstractions.Fabric.DeciderResult> DecideAsync(Corvid.Abstractions.Fabric.DecisionRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task A_session_switch_waits_for_a_running_tool_call()
    {
        var slots = Slots();
        _db.Log.Append(Session, 0, [EventData.Create(new ToolCallIssued("c1", "files.list", Profiles.Json("{}")))], Ct);
        var lease = slots.TrackToolCall("c1");

        var switching = slots.SetSessionDefaultAsync(Generator, _other, cancellationToken: Ct);
        Assert.False(switching.IsCompleted);
        _db.Log.Append(Session, 1, [EventData.Create(new ToolResult("c1", Profiles.Json("[]")))], Ct);
        lease.Dispose();

        Assert.Equal(SwitchStatus.Switched, (await switching).Status);
    }
}
