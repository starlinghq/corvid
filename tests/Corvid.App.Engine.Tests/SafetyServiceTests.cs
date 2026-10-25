using System.Collections.Immutable;
using System.Text.Json.Nodes;
using Corvid.Abstractions.Events;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Safety;
using Corvid.App.Core.Tests;
using Corvid.App.Engine.Safety;
using Corvid.Approvals;
using Corvid.Fabric;
using Corvid.Host.Tests;
using Corvid.Kernel.Events;
using AppRuleDecision = Corvid.App.Core.Safety.RuleDecision;

namespace Corvid.App.Engine.Tests;

/// <summary>The Safety page over the runtime: rules with their scope and removal (K-7), the pack's switch, matches and taint.</summary>
public sealed class SafetyServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RulesAreListedWithTheirScopeAndRemovedK7()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var runtime = fixture.Runtime;
        // An "always" answer and a "this session" answer, through the pipeline as a turn would ask them.
        var answers = new Queue<ApprovalAnswer>([ApprovalAnswer.Approve(ApprovalScope.Always), ApprovalAnswer.Approve(ApprovalScope.Session)]);
        using (runtime.AttachPrompter("cli-rules", new Answering(answers)))
        {
            var context = new ApprovalContext("cli-rules", ApprovalMode.Manual,
                new TurnStateBuilder().Build(new TurnStateInput("tidy up", "cli", "fake-gen")));
            Assert.True((await runtime.Approvals.AuthorizeAsync(context, new ShellAction("make build"), Ct)).Allowed);
            Assert.True((await runtime.Approvals.AuthorizeAsync(context, new ShellAction("rm -rf out"), Ct)).Allowed);
        }

        var changed = 0;
        using var service = new EngineSafetyService(fixture.Engine, new InlineDispatcher(), fixture.Time);
        service.Changed += (_, _) => changed++;
        var snapshot = await service.LoadAsync(Ct);

        var always = Assert.Single(snapshot.Rules, rule => rule.Scope == RuleScope.Always);
        Assert.Equal(AppRuleDecision.Allow, always.Decision);
        Assert.StartsWith("`make build`", always.Text, StringComparison.Ordinal);
        Assert.Equal("approvals.json", always.Origin);
        Assert.Null(always.SessionId);
        Assert.Equal(0, always.Uses);
        var session = Assert.Single(snapshot.Rules, rule => rule.Scope == RuleScope.Session);
        Assert.Equal("cli-rules", session.SessionId);
        Assert.StartsWith("`rm -rf out` at or under `", session.Text, StringComparison.Ordinal);
        // Ids are stable from load to load.
        Assert.Equal(snapshot.Rules.Select(rule => rule.Id), (await service.LoadAsync(Ct)).Rules.Select(rule => rule.Id));

        await service.RemoveRuleAsync(session.Id, Ct);
        await service.RemoveRuleAsync(always.Id, Ct);
        Assert.Equal(2, changed);
        Assert.Empty((await service.LoadAsync(Ct)).Rules);
        Assert.Empty(runtime.Approvals.AlwaysRules);
        Assert.Empty(runtime.Approvals.SessionRules("cli-rules"));
        Assert.Empty(JsonFileApprovalRuleStore.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Host.Root, "approvals.json"), Ct)));

        // A rule already gone changes nothing.
        await service.RemoveRuleAsync(always.Id, Ct);
        Assert.Equal(2, changed);
    }

    [Fact]
    public async Task TheEnforceSwitchHoldsAtOnceAndIsWrittenForTheNextStart()
    {
        await using var fixture = await EngineFixture.StartAsync();
        using var service = new EngineSafetyService(fixture.Engine, new InlineDispatcher(), fixture.Time);
        var pack = Assert.Single((await service.LoadAsync(Ct)).Packs);
        Assert.Equal(SafetyCatalog.BuiltInPack, pack.Name);
        Assert.True(pack.Enforce);
        // The engine's own pack, not the catalogue's copy.
        Assert.Contains(pack.Rules, rule => rule.Id == "secret-read-then-egress" && rule.Version == 1 && rule.Enforces);
        Assert.Equal(SafetyCatalog.Hardline.Select(code => code.Code), (await service.LoadAsync(Ct)).Hardline.Select(code => code.Code));

        await service.SetEnforceAsync(SafetyCatalog.BuiltInPack, false, Ct);

        Assert.False(fixture.Runtime.Approvals.Options.Detections!.Enforce);
        Assert.False(Assert.Single((await service.LoadAsync(Ct)).Packs).Enforce);
        var file = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Host.Root, "corvid.json"), Ct))!;
        Assert.False(file["Corvid"]!["Detections"]!["Enforce"]!.GetValue<bool>());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SetEnforceAsync("another pack", true, Ct));
    }

    [Fact]
    public async Task MatchesComeFromTheLogWithTheirOutcomeAndTaintFromTheOpenSessions()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var log = fixture.Runtime.Log;
        Append(log, "cli-seen",
            new ApprovalRequested("a-1", "{}", "detection", "c-9"),
            new DetectionMatched("secret-read-then-egress", 1, "high", "enforce", "A credential read, then egress.", [1, 2], "c-9", "ask"),
            new ApprovalResolved("a-1", false, "denied"),
            new DetectionMatched("credential-file-read", 1, "medium", "detect", "A credential file is read.", [1]));
        // Taint is known for a session this process holds open; the log lists sessions that have events.
        Append(log, "cli-open", new UserMessage("hello"));
        fixture.Runtime.OpenSession("cli-open");

        using var service = new EngineSafetyService(fixture.Engine, new InlineDispatcher(), fixture.Time);
        var snapshot = await service.LoadAsync(Ct);

        Assert.Equal(2, snapshot.Detections.Length);
        var enforced = Assert.Single(snapshot.Detections, match => match.Enforced);
        Assert.Equal(("secret-read-then-egress", DetectionSeverity.High, "cli-seen", 2L), (enforced.RuleId, enforced.Severity, enforced.SessionId, enforced.Sequence));
        Assert.Equal([1L, 2L], enforced.Events);
        Assert.Equal("forced an ask · denied", enforced.Outcome);
        Assert.Equal("recorded only", Assert.Single(snapshot.Detections, match => !match.Enforced).Outcome);
        var taint = Assert.Single(snapshot.Taint);
        Assert.Equal(("cli-open", false), (taint.SessionId, taint.Tainted));

        // A later load reads only what is new, and finds the new match.
        Append(log, "cli-seen", new DetectionMatched("fork-bomb", 1, "high", "detect", "A fork bomb.", [5]));
        Assert.Equal(3, (await service.LoadAsync(Ct)).Detections.Length);
    }

    [Fact]
    public async Task TheCompositionBuildsTheEnginesSafetyAndItsViewModelLoads()
    {
        await using var fixture = await EngineFixture.StartAsync();
        using var composition = new AppComposition(new AppOptions { Demo = false }, new InlineDispatcher(), fixture.Time, new EngineAppServices(fixture.Engine, fixture.Host.Root, _ => null));

        Assert.IsType<EngineSafetyService>(composition.SafetyService);
        await composition.Safety.Loaded;
        Assert.False(composition.Safety.HasNotice);
    }

    private static void Append(SqliteEventLog log, string sessionId, params SessionEvent[] events) =>
        log.Append(sessionId, log.GetHead(sessionId, Ct), [.. events.Select(EventData.Create)], Ct);

    private sealed class Answering(Queue<ApprovalAnswer> answers) : IApprovalPrompter
    {
        public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken) => ValueTask.FromResult(answers.Dequeue());
    }
}
