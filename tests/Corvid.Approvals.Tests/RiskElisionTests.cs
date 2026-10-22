using Corvid.Abstractions.Events;
using Corvid.Fabric;
using Corvid.Tools;

namespace Corvid.Approvals.Tests;

/// <summary>
/// C-030: with reduced checkpoints, pre_execute does not ask <c>risk</c> where the outcome is ask whatever it says (a forced
/// reason, or Smart mode and an action Smart never auto-runs). Every outcome and offered scope stays what the full profile
/// gives, for every scripted risk level; the skipped question is still logged, so the user's answer is recorded against it.
/// </summary>
public sealed class RiskElisionTests
{
    private static readonly CheckpointReduction Cpu = new("fake-kev on cpu");

    private static CancellationToken Ct => Fixtures.Ct;

    private static ToolAction DestructiveTool => new(Fixtures.Tool("delete_tree", SideEffects.Filesystem, destructive: true, parameters: "path"),
        Fixtures.Json("""{"path":"build"}"""));

    /// <summary>Each action, and the modes in which reduced checkpoints skip <c>risk</c> for it.</summary>
    private static readonly (string Name, Func<ProposedAction> Action, ApprovalMode[] Skipped, ApprovalMode[] Asked)[] Actions =
    [
        // A signal is a forced reason: it asks whatever risk says, in every mode.
        ("forced-reason shell", () => new ShellAction("kill 1234"), [ApprovalMode.Smart, ApprovalMode.Permissive], []),
        ("network curl", () => new ShellAction("curl -o out.html https://example.org"), [ApprovalMode.Smart], [ApprovalMode.Permissive]),
        ("outside-workspace write", () => new ShellAction("touch /tmp/notes.txt"), [ApprovalMode.Smart], [ApprovalMode.Permissive]),
        ("destructive tool", () => DestructiveTool, [ApprovalMode.Smart], [ApprovalMode.Permissive]),
        ("contained write", () => new ShellAction("touch notes.txt"), [], [ApprovalMode.Smart, ApprovalMode.Permissive]),
        ("read-only", () => new ShellAction("ls -la"), [], []),
    ];

    [Fact]
    public async Task Reduced_and_full_give_the_same_outcome_and_scopes_for_every_action_mode_and_risk_level()
    {
        var cases = 0;
        foreach (var (name, action, skippedIn, askedIn) in Actions)
            foreach (var mode in Fixtures.Modes)
                for (var level = 0; level <= 4; level++)
                {
                    using var fullDb = new TempLog();
                    using var reducedDb = new TempLog();
                    var fullKev = new FixedRiskDecider(level);
                    var reducedKev = new FixedRiskDecider(level);
                    var full = await Fixtures.Pipeline(fullDb, fullKev).AssessAsync(Fixtures.Context(mode), action(), Ct);
                    var reduced = await Fixtures.Pipeline(reducedDb, reducedKev, reduction: Cpu).AssessAsync(Fixtures.Context(mode), action(), Ct);
                    var label = $"{name}, {mode}, level {level}";

                    Assert.True(full.Outcome == reduced.Outcome, $"{label}: {full.Outcome} vs {reduced.Outcome}");
                    Assert.True(full.Offered.SequenceEqual(reduced.Offered), label);
                    Assert.True(full.Risk?.Skipped is null or false, label);
                    var skipped = skippedIn.Contains(mode);
                    var asked = askedIn.Contains(mode);
                    Assert.True(skipped == (reduced.Risk?.Skipped == true), $"{label}: skipped");
                    Assert.True(asked == (reducedKev.Requests.Count == 1), $"{label}: asked");
                    Assert.True((skipped || asked) == (reduced.Risk is not null), $"{label}: logged");
                    Assert.True((skipped || asked) == (full.Risk is not null), $"{label}: risk in full");
                    if (skipped)
                    {
                        Assert.Equal(ApprovalOutcome.Ask, reduced.Outcome);
                        Assert.Empty(reducedKev.Requests);
                        var decision = Assert.IsType<Decision>(Assert.Single(reducedDb.Log.Read(Fixtures.Session, cancellationToken: Ct)).Payload);
                        Assert.Equal("risk", decision.QuestionId);
                        Assert.Equal(FabricActions.Ask, decision.Action);
                        Assert.StartsWith("degraded skipped: the outcome is ask whatever risk says; fake-kev on cpu", decision.Decider, StringComparison.Ordinal);
                        Assert.Empty(decision.Probabilities);
                        Assert.Contains(reduced.Reasons, reason => reason.Code == "risk"
                            && reason.Detail == "ask: risk not asked, the outcome is ask whatever it says (fake-kev on cpu)");
                    }
                    cases++;
                }
        Assert.Equal(Actions.Length * 3 * 5, cases);
    }

    [Fact]
    public async Task A_tainted_network_call_keeps_its_forced_ask_and_once_only_scope()
    {
        using var db = new TempLog();
        var kev = new FixedRiskDecider(level: 0);
        var pipeline = Fixtures.Pipeline(db, kev, reduction: Cpu);

        var assessment = await pipeline.AssessAsync(Fixtures.Context(ApprovalMode.Permissive, tainted: true),
            new ShellAction("curl -o out.html https://example.org"), Ct);

        Assert.Equal(ApprovalOutcome.Ask, assessment.Outcome);
        Assert.Equal([ApprovalScope.Once], assessment.Offered);
        Assert.Contains(assessment.Reasons, reason => reason.Code == ApprovalReasons.TaintedEgress);
        Assert.True(assessment.Risk!.Skipped);
        Assert.Empty(kev.Requests);
    }

    [Fact]
    public async Task The_users_answer_is_recorded_against_the_skipped_risk_decision()
    {
        using var db = new TempLog();
        var pipeline = Fixtures.Pipeline(db, new FixedRiskDecider(level: 0), ScriptedPrompter.Always(ApprovalAnswer.Approve()), reduction: Cpu);

        var result = await pipeline.AuthorizeAsync(Fixtures.Context(ApprovalMode.Smart), DestructiveTool, Ct);

        Assert.Equal(ApprovalResolution.UserApproved, result.Resolution);
        var payloads = db.Log.Read(Fixtures.Session, cancellationToken: Ct).Select(item => item.Payload).ToList();
        var decision = Assert.Single(payloads.OfType<Decision>());
        var outcome = Assert.Single(payloads.OfType<DecisionOutcomeRecorded>());
        Assert.Equal(decision.DecisionId, outcome.DecisionId);
        Assert.Equal("user_approved", outcome.Outcome);
    }

    [Fact]
    public async Task Without_reduction_nothing_is_skipped_and_the_degraded_tier_still_asks()
    {
        using var db = new TempLog();
        var assessment = await Fixtures.Pipeline(db).AssessAsync(Fixtures.Context(ApprovalMode.Smart), DestructiveTool, Ct);

        Assert.Equal(ApprovalOutcome.Ask, assessment.Outcome);
        Assert.False(assessment.Risk!.Skipped);
        Assert.Equal("degraded", assessment.Risk.Decider);
    }
}
