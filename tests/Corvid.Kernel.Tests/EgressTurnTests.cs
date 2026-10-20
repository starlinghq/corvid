using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Fabric;
using Corvid.Kernel.Events;
using Corvid.Kernel.Turns;
using Corvid.Memory;
using Corvid.Models;
using Corvid.Models.Privacy;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Tests;

/// <summary>Plan §10.4 in the turn loop: what a cloud generator is shown, and the switch-time privacy hook.</summary>
public sealed class EgressTurnTests : IDisposable
{
    private const string Session = "s-egress";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-egress-" + Guid.NewGuid().ToString("N"));
    private readonly SqliteEventLog _log;
    private readonly MemoryStore _memory = new();

    public EgressTurnTests() => _log = new SqliteEventLog(Path.Combine(_root, "events.db"));

    public void Dispose()
    {
        _memory.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static ModelProfile Generator(PrivacyClass privacy, PromptCachingStyle caching) =>
        new("gen-" + privacy, 200_000, 100_000, ToolCallSupport.Native, StructuredOutputSupport.None, caching, ModelCost.Free, privacy, "remote");

    private async Task<GeneratorCall> TurnAsync(PrivacyClass privacy, PromptCachingStyle caching, EnvelopeOrigin origin = EnvelopeOrigin.User)
    {
        var generator = new FakeGenerator(Reply.Say("Main Street.")) { YesNo = FakeGenerator.YesTo("remembered") };
        var embedder = new KeywordEmbedder(TurnHarness.Keywords);
        var registry = new ModelSlotRegistry()
            .Bind(ModelSlots.Generator, new ModelBinding(Generator(privacy, caching), generator))
            .Bind(ModelSlots.TextEmbedder, new ModelBinding(TurnHarness.Profile("keywords", ToolCallSupport.None, 512), embedder));
        var services = new TurnServices
        {
            Log = _log,
            Slots = new SessionModelSlots(registry, _log, Session),
            Fabric = new DecisionFabric(DecisionPolicy.Default, new DegradedDecider(embedder, generator), _log),
            Tools = new ToolRegistry(),
            Retriever = new HybridRetriever(_memory.Episodes, _memory.Facts),
            CoreBlocks = _memory.Blocks.Snapshot(),
        };
        await new TurnRunner(Session, services).RunAsync(new Envelope(Session, "cli", "where is my dentist", origin), Ct);
        return generator.Calls.Single();
    }

    private void Remember()
    {
        _memory.Facts.Add(new FactInput("the dentist is Dr Patel", LocalOnly: true));
        _memory.Facts.Add(new FactInput("the dentist is on Main Street"));
        _memory.Blocks.Set("user", "Lives in Leeds.");
        _memory.Blocks.Set("agent", "Use British spelling.");
        _memory.Blocks.SetLocalOnly("agent", false);
    }

    [Fact]
    public async Task A_cloud_generator_sees_no_local_only_block_or_fact_and_gets_recalled_memory_after_a_frozen_system_prompt()
    {
        Remember();

        var call = await TurnAsync(PrivacyClass.Cloud, PromptCachingStyle.Breakpoints);

        var system = call.Messages[0].Text;
        Assert.Contains("Use British spelling.", system, StringComparison.Ordinal);
        Assert.DoesNotContain("Leeds", call.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Patel", call.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("Relevant memory", system, StringComparison.Ordinal);
        Assert.Equal(ChatRole.User, call.Messages[^1].Role);
        Assert.Equal("Relevant memory:\n- the dentist is on Main Street", call.LastText);

        // The session and its taint travel with the request for the egress gate and Vault.
        Assert.Equal(Session, call.Options!.AdditionalProperties![ModelRequestKeys.SessionId]);
        Assert.False((bool)call.Options.AdditionalProperties[ModelRequestKeys.Tainted]!);
    }

    [Fact]
    public async Task A_local_generator_sees_everything_with_memory_in_the_system_prompt()
    {
        Remember();

        var call = await TurnAsync(PrivacyClass.Local, PromptCachingStyle.None);

        var system = call.Messages[0].Text;
        Assert.Contains("Lives in Leeds.", system, StringComparison.Ordinal);
        Assert.Contains("the dentist is Dr Patel", system, StringComparison.Ordinal);
        Assert.Contains("the dentist is on Main Street", system, StringComparison.Ordinal);
        Assert.Equal("where is my dentist", call.LastText);
    }

    [Fact]
    public async Task An_outside_envelope_marks_the_request_tainted()
    {
        var call = await TurnAsync(PrivacyClass.Cloud, PromptCachingStyle.Breakpoints, EnvelopeOrigin.External);

        Assert.True((bool)call.Options!.AdditionalProperties![ModelRequestKeys.Tainted]!);
    }

    private (SessionModelSlots Slots, ModelBinding Cloud) Escalating(PrivacyEgress egress)
    {
        var local = new ModelBinding(Generator(PrivacyClass.Local, PromptCachingStyle.None), new FakeGenerator());
        var cloud = new ModelBinding(Generator(PrivacyClass.Cloud, PromptCachingStyle.Breakpoints), new FakeGenerator());
        var registry = new ModelSlotRegistry().Bind(ModelSlots.Generator, local, escalation: [cloud]);
        var slots = new SessionModelSlots(registry, _log, Session, new ModelSwitchOptions { Privacy = new ProjectionPrivacyGate(_log, egress) });
        _log.Append(Session, _log.GetHead(Session, Ct), [EventData.Create(new UserMessage("ring Alice Smith about the invoice"))], Ct);
        return (slots, cloud);
    }

    private static PrivacyEgress Egress(EgressAction action, bool extractor = true) =>
        new(extractor ? new FakeExtractor(0.9f, ("person", "Alice Smith")) : null,
            new PrivacyEgressOptions { PolicyFor = profile => profile.Privacy == PrivacyClass.Cloud ? EgressPolicy.Default with { Action = action } : null });

    [Fact]
    public async Task Escalation_passes_over_a_cloud_target_whose_policy_refuses_the_conversations_pii()
    {
        var (slots, _) = Escalating(Egress(EgressAction.Refuse));

        var result = await slots.EscalateAsync(ModelSlots.Generator, Ct);

        Assert.Equal(SwitchStatus.NoTarget, result.Status);
        Assert.Contains("person", result.Detail, StringComparison.Ordinal);
        Assert.Empty(_log.Read(Session, cancellationToken: Ct).Select(item => item.Payload).OfType<ModelSwitched>());
    }

    [Fact]
    public async Task Escalation_to_a_redacting_cloud_target_is_allowed_and_without_an_extractor_it_is_refused()
    {
        var (redacting, cloud) = Escalating(Egress(EgressAction.Redact));
        var allowed = await redacting.EscalateAsync(ModelSlots.Generator, Ct);
        Assert.Equal(SwitchStatus.Switched, allowed.Status);
        Assert.Same(cloud, redacting.Current(ModelSlots.Generator));

        var (blind, _) = Escalating(Egress(EgressAction.Send, extractor: false));
        var refused = await blind.EscalateAsync(ModelSlots.Generator, Ct);
        Assert.Equal(SwitchStatus.NoTarget, refused.Status);
        Assert.Contains("no PII extractor", refused.Detail, StringComparison.Ordinal);
    }
}
