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

    private async Task<GeneratorCall> TurnAsync(PrivacyClass privacy, PromptCachingStyle caching, EnvelopeOrigin origin = EnvelopeOrigin.User,
        GuestScope? guest = null)
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
        await new TurnRunner(Session, services).RunAsync(new Envelope(Session, "cli", "where is my dentist", origin) { Guest = guest }, Ct);
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
        // Marked, so a fallback to a cloud model refuses this request instead of sending local-only memory.
        Assert.True((bool)call.Options!.AdditionalProperties![ModelRequestKeys.LocalOnly]!);
    }

    [Fact]
    public async Task A_cloud_request_is_not_marked_local_only()
    {
        Remember();

        var call = await TurnAsync(PrivacyClass.Cloud, PromptCachingStyle.Breakpoints);

        Assert.False(call.Options!.AdditionalProperties!.ContainsKey(ModelRequestKeys.LocalOnly));
    }

    /// <summary>Records the ambient request context of each call, as the Vault handler under a cloud SDK would see it.</summary>
    private sealed class AmbientRecorder(IChatClient inner) : DelegatingChatClient(inner)
    {
        public List<ModelRequestContext?> Seen { get; } = [];

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            lock (Seen)
                Seen.Add(ModelRequestContext.Current);
            return base.GetResponseAsync(messages, options, cancellationToken);
        }
    }

    [Fact]
    public async Task Fabric_questions_carry_the_session_and_its_taint_to_the_generator()
    {
        var generator = new FakeGenerator(Reply.Say("Main Street."));
        var decider = new AmbientRecorder(generator);
        var embedder = new KeywordEmbedder(TurnHarness.Keywords);
        var registry = new ModelSlotRegistry()
            .Bind(ModelSlots.Generator, new ModelBinding(Generator(PrivacyClass.Cloud, PromptCachingStyle.Breakpoints), generator))
            .Bind(ModelSlots.TextEmbedder, new ModelBinding(TurnHarness.Profile("keywords", ToolCallSupport.None, 512), embedder));
        var services = new TurnServices
        {
            Log = _log,
            Slots = new SessionModelSlots(registry, _log, Session),
            // askTurnBlocking: since C-004b the degraded tier asks the generator nothing else at ingress without a retriever.
            Fabric = new DecisionFabric(DecisionPolicy.Default, new DegradedDecider(embedder, decider, askTurnBlocking: true), _log),
            Tools = new ToolRegistry(),
        };

        await new TurnRunner(Session, services).RunAsync(new Envelope(Session, "cli", "where is my dentist", EnvelopeOrigin.External), Ct);

        Assert.NotEmpty(generator.Questions);
        Assert.All(decider.Seen, context => Assert.Equal(new ModelRequestContext(Session, Tainted: true), context));
    }

    [Fact]
    public async Task An_outside_envelope_marks_the_request_tainted()
    {
        var call = await TurnAsync(PrivacyClass.Cloud, PromptCachingStyle.Breakpoints, EnvelopeOrigin.External);

        Assert.True((bool)call.Options!.AdditionalProperties![ModelRequestKeys.Tainted]!);
    }

    [Fact]
    public async Task A_guests_turn_sees_none_of_the_users_memory_neither_core_blocks_nor_recall()
    {
        Remember();

        var call = await TurnAsync(PrivacyClass.Local, PromptCachingStyle.None, EnvelopeOrigin.External, GuestScope.None);

        Assert.DoesNotContain("Leeds", call.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("British spelling", call.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain("dentist is", call.AllText, StringComparison.Ordinal);
        Assert.False(call.Options!.AdditionalProperties!.ContainsKey(ModelRequestKeys.LocalOnly));
    }

    [Fact]
    public async Task A_turn_the_gate_keeps_local_says_so_in_its_result()
    {
        var local = new FakeGenerator(Reply.Say("Answered locally."));
        var cloudProvider = new FakeGenerator(Reply.Say("Answered in the cloud."));
        var cloudProfile = Generator(PrivacyClass.Cloud, PromptCachingStyle.Breakpoints);
        var cloud = new ModelBinding(cloudProfile, new PrivacyEgressChatClient(cloudProvider, cloudProfile, Egress(EgressAction.Refuse)));
        var embedder = new KeywordEmbedder(TurnHarness.Keywords);
        var registry = new ModelSlotRegistry()
            .Bind(ModelSlots.Generator, new ModelBinding(Generator(PrivacyClass.Local, PromptCachingStyle.None), local), escalation: [cloud])
            .Bind(ModelSlots.TextEmbedder, new ModelBinding(TurnHarness.Profile("keywords", ToolCallSupport.None, 512), embedder));
        var slots = new SessionModelSlots(registry, _log, Session, new ModelSwitchOptions { Privacy = PrivacyGates.AllowAll });
        await slots.SwitchAsync(ModelSlots.Generator, cloud, SwitchReason.User, pin: true, cancellationToken: Ct);
        var services = new TurnServices
        {
            Log = _log,
            Slots = slots,
            Fabric = new DecisionFabric(DecisionPolicy.Default, new DegradedDecider(embedder, local), _log),
            Tools = new ToolRegistry(),
        };

        var result = await new TurnRunner(Session, services).RunAsync(new Envelope(Session, "cli", "remind Alice Smith about lunch"), Ct);

        Assert.Equal("Answered locally.", result.Text);
        Assert.Empty(cloudProvider.Calls);
        Assert.Equal([$"gen-Local: {TurnRunner.PrivacyRetreat}"], result.Escalations);
    }

    [Fact]
    public async Task A_tool_result_carrying_a_token_reaches_a_cloud_generator_redacted_even_under_a_send_policy()
    {
        const string token = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8"; // fictitious, in GitHub's classic shape
        var readPage = TestTool.Create("read_page", "Read a page of the project notes.", new ToolRisk(SideEffects.None, Network: false, Destructive: false),
            """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"]}""",
            (_, _) => ValueTask.FromResult<object?>($"deploy notes: export GH_TOKEN={token}"), new ToolParameter("path", null, true, false));
        var cloudProvider = new FakeGenerator(Reply.Call("read_page", """{"path":"deploy"}"""), Reply.Say("Read it."));
        var cloudProfile = Generator(PrivacyClass.Cloud, PromptCachingStyle.Breakpoints);
        var cloud = new ModelBinding(cloudProfile, new PrivacyEgressChatClient(cloudProvider, cloudProfile, Egress(EgressAction.Send)));
        var local = new FakeGenerator();
        var embedder = new KeywordEmbedder(TurnHarness.Keywords);
        var registry = new ModelSlotRegistry()
            .Bind(ModelSlots.Generator, cloud)
            .Bind(ModelSlots.TextEmbedder, new ModelBinding(TurnHarness.Profile("keywords", ToolCallSupport.None, 512), embedder));
        var tools = new ToolRegistry();
        tools.Add(readPage);
        var services = new TurnServices
        {
            Log = _log,
            Slots = new SessionModelSlots(registry, _log, Session),
            Fabric = new DecisionFabric(DecisionPolicy.Default, new DegradedDecider(embedder, local), _log),
            Tools = tools,
            Options = new TurnOptions { ToolFilter = tool => tool.Name == "read_page" },
        };

        var result = await new TurnRunner(Session, services).RunAsync(new Envelope(Session, "cli", "read the deploy page"), Ct);

        Assert.Equal("Read it.", result.Text);
        Assert.Equal(1, readPage.Invocations);
        var afterTool = cloudProvider.Calls[1].AllText;
        Assert.DoesNotContain(token, afterTool, StringComparison.Ordinal);
        // C-040: the log scrubbed the result before the prompt was rendered from it, so the cloud sees the scrub's marker and
        // the egress gate has no credential left to redact (and so nothing a response could restore).
        Assert.Contains("export GH_TOKEN=ghp_[REDACTED:github-token:", afterTool, StringComparison.Ordinal);
        Assert.DoesNotContain("<CREDENTIAL_", afterTool, StringComparison.Ordinal);
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
