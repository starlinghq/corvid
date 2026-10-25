using Corvid.Abstractions.Events;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Host.Sessions;
using Corvid.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Corvid.Host.Tests;

/// <summary>A5: switching a session's models (any slot, for the session, pinned or one turn), the runtime default, and the updates.</summary>
public sealed class SessionModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_session_switch_lasts_past_the_turn_and_is_reported()
    {
        var models = new FakeModels();
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        var heard = new List<(string Session, ModelChanged Change)>();
        runtime.ModelChanged += (id, change) =>
        {
            lock (heard)
                heard.Add((id, change));
        };

        var switched = await session.SetModelAsync(ModelSlots.Generator, "fake-gen-large", cancellationToken: Ct);
        var first = await session.SendAsync("hello", Ct);
        var second = await session.SendAsync("hello again", Ct);

        Assert.Equal(SwitchStatus.Switched, switched.Status);
        // Both turns ran on the larger model (its script has one answer, then "Done."); the default saw no turn.
        Assert.Equal(("Hello from the larger model.", "Done."), (first.Result!.Text, second.Result!.Text));
        Assert.Equal(2, models.Larger.Calls.Count);
        Assert.Empty(models.Generator.Calls);
        Assert.Equal("session", Assert.Single(session.Snapshot().ProfilesOf(runtime)).Reason);
        var (id, change) = Assert.Single(heard);
        Assert.Equal((session.Id, ModelSlots.Generator, "fake-gen@cpu", "fake-gen-large@cpu", "session", true), (id, change.Slot, change.From, change.To, change.Reason, change.SessionDefault));
    }

    [Fact]
    public async Task A_turn_switch_returns_to_the_session_default_when_the_turn_ends()
    {
        var models = new FakeModels();
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        await session.SetModelAsync(ModelSlots.Generator, "fake-gen-large", SwitchScope.Turn, Ct);
        var first = await session.SendAsync("hello", Ct);
        var second = await session.SendAsync("hello again", Ct);

        Assert.Equal(("Hello from the larger model.", "Hello from the fake generator."), (first.Result!.Text, second.Result!.Text));
        Assert.Equal(["user", "de_escalation"], Switches(runtime, session.Id).Select(change => change.Reason));
    }

    [Fact]
    public async Task The_decider_is_switchable_per_session()
    {
        var models = new FakeModels();
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        var other = runtime.OpenSession();
        Assert.Null(runtime.Fabric.CalibratedDecider);

        var switched = await session.SetModelAsync(ModelSlots.Decider, "fake-kev", cancellationToken: Ct);
        await session.SendAsync("hello", Ct);
        await other.SendAsync("hello", Ct);

        Assert.Equal((SwitchStatus.Switched, ModelProfile.UnboundId), (switched.Status, switched.From.Id));
        Assert.NotEmpty(models.Kev.Requests);
        Assert.Equal("fake-kev", runtime.Deciders.DeciderName(session.Slots));
        Assert.All(Decisions(runtime, session.Id), decision => Assert.StartsWith("fake-kev", decision.Decider, StringComparison.Ordinal));
        Assert.All(Decisions(runtime, other.Id), decision => Assert.StartsWith("degraded", decision.Decider, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_model_that_cannot_serve_or_is_missing_is_refused_before_anything_changes()
    {
        var models = new FakeModels(kevReady: false);
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        Assert.Throws<ArgumentException>(() => { _ = session.SetModelAsync(ModelSlots.Generator, "fake-embed", cancellationToken: Ct); });
        Assert.Throws<ArgumentException>(() => { _ = session.SetModelAsync(ModelSlots.Generator, "no-such-model", cancellationToken: Ct); });
        Assert.Throws<ArgumentException>(() => { _ = session.SetModelAsync("brain", "fake-gen", cancellationToken: Ct); });
        Assert.Throws<ModelUnavailableException>(() => { _ = session.SetModelAsync(ModelSlots.Decider, "fake-kev", cancellationToken: Ct); });
        Assert.Empty(Switches(runtime, session.Id));
    }

    [Fact]
    public async Task The_runtime_default_and_a_sessions_choice_are_separate()
    {
        var models = new FakeModels();
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        var plain = runtime.OpenSession();
        var chose = runtime.OpenSession();
        await chose.SetModelAsync(ModelSlots.Generator, "fake-gen", cancellationToken: Ct);

        runtime.SetDefaultModel(ModelSlots.Generator, "fake-gen-large");
        var fresh = runtime.OpenSession();

        Assert.Equal("fake-gen-large@cpu", fresh.Slots.Current(ModelSlots.Generator).Profile.Id);
        // A session that chose nothing returns to the new default when its turn ends; one that chose keeps its choice.
        Assert.Equal("Hello from the fake generator.", (await plain.SendAsync("hi", Ct)).Result!.Text);
        Assert.Equal("fake-gen-large@cpu", plain.Slots.Current(ModelSlots.Generator).Profile.Id);
        await chose.SendAsync("hi", Ct);
        Assert.Equal("fake-gen@cpu", chose.Slots.Current(ModelSlots.Generator).Profile.Id);
    }

    [Fact]
    public async Task Clearing_a_choice_goes_back_to_the_configured_model()
    {
        await using var host = TestHost.Create(new FakeModels());
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        await session.SetModelAsync(ModelSlots.Generator, "fake-gen-large", cancellationToken: Ct);

        var cleared = await session.ClearModelChoiceAsync(ModelSlots.Generator, Ct);

        Assert.Equal(SwitchStatus.Switched, cleared!.Status);
        Assert.False(session.Slots.HasSessionChoice(ModelSlots.Generator));
        Assert.Equal("fake-gen@cpu", session.Slots.Current(ModelSlots.Generator).Profile.Id);
    }

    [Fact]
    public async Task A_replay_carries_model_switches_and_children_only_when_asked()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("one"), Reply.Say("child")));
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        await session.SetModelAsync(ModelSlots.Generator, "fake-gen", SwitchScope.Turn, Ct);
        await session.SetModelAsync(ModelSlots.Generator, "fake-gen-large", cancellationToken: Ct);
        await runtime.RunChildAsync(session.Id, new ChildRequest("go"), Ct);
        var tools = new ToolPresentation(runtime);

        var plain = await SessionReplay.ReadAsync(runtime, session.Id, tools, cancellationToken: Ct);
        var full = await SessionReplay.ReadAsync(runtime, session.Id, tools, ReplayOptions.Full with { Models = true, Children = true }, cancellationToken: Ct);

        Assert.DoesNotContain(plain.Updates, update => update is ModelChanged or ChildStarted or ChildEnded);
        var change = Assert.Single(full.Updates.OfType<ModelChanged>());
        Assert.Equal((ModelSlots.Generator, "fake-gen@cpu", "fake-gen-large@cpu", "session", true), (change.Slot, change.From, change.To, change.Reason, change.SessionDefault));
        Assert.Single(full.Updates.OfType<ChildStarted>());
        Assert.Equal(ChildStatuses.Answered, Assert.Single(full.Updates.OfType<ChildEnded>()).Child.Status);
    }

    [Fact]
    public async Task The_agents_settings_are_checked()
    {
        await using var host = TestHost.Create(settings: new Dictionary<string, string?>
        {
            ["Corvid:Agents:MaxConcurrent"] = "11",
            ["Corvid:Agents:MaxDepth"] = "2",
            ["Corvid:Agents:Models:0"] = "gpt-9",
            ["Corvid:Agents:Mixes:bad:brain:Model"] = "fake-gen",
            ["Corvid:Agents:Mixes:wrong:generator:Model"] = "fake-embed",
        });

        var failure = Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value);
        var text = string.Join("\n", failure.Failures);

        Assert.Contains("Corvid:Agents:MaxConcurrent must be between 1 and 10", text, StringComparison.Ordinal);
        Assert.Contains("Corvid:Agents:MaxDepth must be 0", text, StringComparison.Ordinal);
        Assert.Contains("Corvid:Agents:Models: no model is named 'gpt-9'", text, StringComparison.Ordinal);
        Assert.Contains("Corvid:Agents:Mixes:bad: There is no slot named \"brain\"", text, StringComparison.Ordinal);
        Assert.Contains("Corvid:Agents:Mixes:wrong:generator: 'fake-embed' cannot serve generator", text, StringComparison.Ordinal);
    }

    private static IEnumerable<ModelSwitched> Switches(CorvidRuntime runtime, string sessionId) =>
        runtime.Log.Read(sessionId, limit: 4096).Select(item => item.Payload).OfType<ModelSwitched>();

    private static IEnumerable<Decision> Decisions(CorvidRuntime runtime, string sessionId) =>
        runtime.Log.Read(sessionId, limit: 4096).Select(item => item.Payload).OfType<Decision>();
}

internal static class SnapshotSwitches
{
    /// <summary>The session's logged switches (the snapshot keeps only the profiles).</summary>
    public static IEnumerable<ModelSwitched> ProfilesOf(this Corvid.Kernel.Events.SessionSnapshot snapshot, CorvidRuntime runtime) =>
        runtime.Log.Read(snapshot.SessionId, limit: 4096).Select(item => item.Payload).OfType<ModelSwitched>();
}
