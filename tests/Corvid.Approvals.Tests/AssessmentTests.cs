using Corvid.Abstractions.Events;

namespace Corvid.Approvals.Tests;

/// <summary>G-2: a tool call that asked nobody leaves the pipeline's assessment in the log, so a surface says why it ran.</summary>
public sealed class AssessmentTests
{
    private const string Session = Fixtures.Session;

    private static CancellationToken Ct => Fixtures.Ct;

    private static ApprovalContext Call(string callId, ApprovalMode mode = ApprovalMode.Smart) => Fixtures.Context(mode) with { CallId = callId };

    private static List<ApprovalAssessed> Assessed(TempLog db) =>
        [.. db.Log.Read(Session, cancellationToken: Ct).Select(e => e.Payload).OfType<ApprovalAssessed>()];

    [Fact]
    public async Task A_call_smart_mode_ran_unasked_records_the_mode_the_risk_level_and_what_the_action_does()
    {
        using var db = new TempLog();
        var pipeline = Fixtures.Pipeline(db, new FixedRiskDecider(1, 0.93));

        var result = await pipeline.AuthorizeAsync(Call("call-7"), new ShellAction("touch build/stamp"), Ct);

        Assert.Equal(ApprovalResolution.Automatic, result.Resolution);
        var assessed = Assert.Single(Assessed(db));
        Assert.Equal(("call-7", ApprovalResolutions.Automatic, "smart", null, null), (assessed.CallId, assessed.Resolution, assessed.Mode, assessed.Rule, assessed.RuleScope));
        var risk = assessed.Risk!;
        Assert.Equal((1, "low", true, "fake-kev"), (risk.Level, risk.Option, risk.Calibrated, risk.Decider));
        // The confidence the risk decision logged, as the card shows it.
        var decision = db.Log.Read(Session, cancellationToken: Ct).Select(e => e.Payload).OfType<Decision>().Single();
        Assert.Equal((decision.DecisionId, decision.Confidence), (risk.DecisionId, risk.Confidence!.Value));
        Assert.Contains("write", assessed.Effects!.Value);
        Assert.DoesNotContain("network", assessed.Effects!.Value);
        Assert.True(assessed.Contained);
        Assert.Contains(assessed.Reasons, reason => reason.Code == "risk");
    }

    [Fact]
    public async Task A_call_a_session_rule_allowed_names_the_rule_and_its_scope_and_an_asked_call_has_no_assessment()
    {
        using var db = new TempLog();
        var pipeline = Fixtures.Pipeline(db, prompter: ScriptedPrompter.Always(ApprovalAnswer.Approve(ApprovalScope.Session)));

        await pipeline.AuthorizeAsync(Call("call-1", ApprovalMode.Manual), new ShellAction("rm -rf build"), Ct);
        Assert.Empty(Assessed(db));

        var again = await pipeline.AuthorizeAsync(Call("call-2", ApprovalMode.Manual), new ShellAction("rm -rf build/cache"), Ct);

        Assert.Equal(ApprovalResolution.RuleAllowed, again.Resolution);
        var assessed = Assert.Single(Assessed(db));
        Assert.Equal(("call-2", ApprovalResolutions.RuleAllowed, "manual", "session"), (assessed.CallId, assessed.Resolution, assessed.Mode, assessed.RuleScope));
        Assert.Equal($"`rm` under {Fixtures.Workspace}/build", assessed.Rule);
        Assert.Null(assessed.Risk);
    }

    [Fact]
    public async Task An_always_rule_is_named_as_always()
    {
        using var db = new TempLog();
        var always = new InMemoryApprovalRuleStore();
        var pipeline = Fixtures.Pipeline(db, prompter: ScriptedPrompter.Always(ApprovalAnswer.Approve(ApprovalScope.Always)), always: always);
        await pipeline.AuthorizeAsync(Call("call-1", ApprovalMode.Manual), new ShellAction("rm -rf build"), Ct);

        await pipeline.AuthorizeAsync(Call("call-2", ApprovalMode.Manual), new ShellAction("rm -r build"), Ct);

        Assert.Equal("always", Assert.Single(Assessed(db)).RuleScope);
    }

    [Fact]
    public async Task Refusals_that_asked_nobody_and_reads_are_recorded_with_their_resolution()
    {
        using var db = new TempLog();
        var pipeline = Fixtures.Pipeline(db, new FixedRiskDecider(0), ScriptedPrompter.Always(ApprovalAnswer.Approve()));

        await pipeline.AuthorizeAsync(Call("call-h", ApprovalMode.Permissive), new ShellAction("sudo rm -rf --no-preserve-root /"), Ct);
        await pipeline.AuthorizeAsync(Call("call-n", ApprovalMode.Manual) with { Interactive = false }, new ShellAction("rm -rf build"), Ct);
        await pipeline.AuthorizeAsync(Call("call-r"), new ShellAction("ls -la src"), Ct);

        var assessed = Assessed(db);
        Assert.Equal([ApprovalResolutions.Hardline, ApprovalResolutions.NoApprover, ApprovalResolutions.Automatic],
            assessed.Select(item => item.Resolution));
        Assert.Contains(assessed[0].Reasons, reason => reason.Code == HardlineRules.RecursiveDeleteOutsideWorkspace);
        Assert.Contains(assessed[1].Reasons, reason => reason.Code == "mode");
        Assert.Equal(["read_only"], assessed[2].Reasons.Select(reason => reason.Code));
        Assert.Null(assessed[2].Risk);
    }

    [Fact]
    public async Task Without_a_call_nothing_is_recorded()
    {
        using var db = new TempLog();
        var pipeline = Fixtures.Pipeline(db, new FixedRiskDecider(0));

        await pipeline.AuthorizeAsync(Fixtures.Context(ApprovalMode.Permissive), new ShellAction("touch build/stamp"), Ct);

        Assert.Empty(Assessed(db));
    }
}
