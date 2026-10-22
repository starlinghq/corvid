using Corvid.Abstractions.Fabric;

namespace Corvid.Fabric.Tests;

/// <summary>
/// C-030: reduced checkpoints are never more permissive than today for a gate. The reducer never skips a gate, nor
/// <c>injection</c> at ingress; a gate a caller skips gets exactly what the degraded tier gives it without Kev, which is
/// at most as permissive as any answer the policy could act on.
/// </summary>
public sealed class GateConservatismTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// How permissive each action of a gate (and of <c>injection</c>) is, lower being stricter. A new gate without an entry
    /// fails <see cref="Every_gate_and_injection_has_a_permissiveness_order"/>: decide its order before it can be reduced.
    /// </summary>
    private static readonly Dictionary<string, Func<string, int>> Permissiveness = new(StringComparer.Ordinal)
    {
        ["risk"] = action => action switch
        {
            FabricActions.Ask => 0,
            FabricActions.ApprovalMode => 1,
            FabricActions.AutoRun => 2,
            _ => throw new ArgumentException($"unknown risk action '{action}'"),
        },
        ["memory_op"] = action => action == "stage_for_review" ? 0
            : action.StartsWith("apply:", StringComparison.Ordinal) ? 1 : throw new ArgumentException($"unknown memory_op action '{action}'"),
        ["skill_write_safe"] = action => action switch
        {
            "stage_for_review" => 0,
            "apply_skill_change" => 1,
            _ => throw new ArgumentException($"unknown skill_write_safe action '{action}'"),
        },
        // An acted verdict marks the content tainted and, for MCP descriptions, withholds the tools (C-033).
        ["injection"] = action => action switch
        {
            "mark_tainted" => 0,
            "treat_as_tainted" => 1,
            _ => throw new ArgumentException($"unknown injection action '{action}'"),
        },
    };

    private static IEnumerable<CatalogueQuestion> Guarded =>
        Fixtures.Catalogue.Questions.Where(question => question.Role == QuestionRole.Gate || question.Id == "injection");

    private static IEnumerable<CatalogueQuestion> Gates => Fixtures.Catalogue.Questions.Where(question => question.Role == QuestionRole.Gate);

    [Fact]
    public void Every_gate_and_injection_has_a_permissiveness_order()
    {
        Assert.NotEmpty(Gates);
        Assert.All(Guarded, question =>
        {
            Assert.True(Permissiveness.ContainsKey(question.Id), $"'{question.Id}' needs a permissiveness order");
            Assert.Equal(question.Id == "injection" ? 1 : 0, Permissiveness[question.Id](question.BelowFloor));
        });
        // For a gate, acting is the more permissive side; for injection, acting is the stricter one, hence never skipped at ingress.
        Assert.All(Gates, gate => Assert.True(Permissiveness[gate.Id](gate.Kind == QuestionKind.Choice ? gate.Act + ":add" : gate.Act) > 0));
    }

    [Fact]
    public void The_reducer_never_skips_a_gate_nor_injection_at_ingress()
    {
        var reduction = new CheckpointReduction("test");
        foreach (var checkpoint in Enum.GetValues<Checkpoint>())
        {
            // Every question asked at this checkpoint, so the cap has plenty to cut.
            var keys = Fixtures.Catalogue.Questions.Where(question => question.Checkpoints.Contains(checkpoint))
                .SelectMany(question => new[] { question.Id, question.Id + "/a", question.Id + "/b" }).ToArray();
            var (asked, skipped) = CheckpointReduction.Partition(Fixtures.Catalogue, checkpoint, keys, reduction, null);

            Assert.Equal(keys.Length, asked.Length + skipped.Count);
            foreach (var key in keys.Where(key => Fixtures.Catalogue.Resolve(key).Role == QuestionRole.Gate))
                Assert.Contains(key, asked);
            if (checkpoint == Checkpoint.Ingress)
                Assert.All(keys.Where(key => Fixtures.Catalogue.Resolve(key).Id == "injection"), key => Assert.Contains(key, asked));
            if (checkpoint != Checkpoint.Step)
                Assert.Empty(skipped);
            Assert.All(Fixtures.Catalogue.Questions.Where(question => question.Checkpoints.Contains(checkpoint)),
                question => Assert.Equal(checkpoint == Checkpoint.Step && question.Role != QuestionRole.Gate,
                    CheckpointReduction.Skippable(question, checkpoint)));
        }
        Assert.False(CheckpointReduction.Skippable(Fixtures.Catalogue["injection"], Checkpoint.Ingress));
    }

    [Fact]
    public async Task A_gate_a_caller_skips_gets_the_degraded_tiers_conservative_default()
    {
        foreach (var gate in Gates)
        {
            var checkpoint = gate.Checkpoints[0];
            var all = Fixtures.Conditions(FabricConditions.PermissiveMode, FabricConditions.AgentAuthored);
            using var db = new TempLog();
            // A calibrated decider that would let everything through, never asked.
            var kev = FakeCalibratedDecider.Answering("kev-test", (_, question) =>
                DecisionAnswer.For(question, question.Options.Select((_, i) => i == 0 ? 1.0 : 0.0)));
            var reduced = new DecisionFabric(DecisionPolicy.Default, new DegradedDecider(new KeywordEmbedder("x"), ScriptedChatClient.Always("yes")),
                db.Log, kev, reduction: new("test"));
            var degraded = new DecisionFabric(DecisionPolicy.Default, new DegradedDecider(new KeywordEmbedder("x"), ScriptedChatClient.Always("yes")),
                db.Log);

            var skipped = (await reduced.DecideAsync("s-" + gate.Id, checkpoint, Fixtures.State(), [Fixtures.Ask(gate.Id)], all,
                new Dictionary<string, string> { [gate.Id] = "caller proof" }, Ct))[gate.Id];
            var withoutKev = (await degraded.DecideAsync("d-" + gate.Id, checkpoint, Fixtures.State(), [Fixtures.Ask(gate.Id)], all, Ct))[gate.Id];

            Assert.Empty(kev.Requests);
            Assert.True(skipped.Skipped);
            Assert.Equal(DecisionPolicy.Default.Evaluate(gate.Id, null, all).Action, skipped.Verdict.Action);
            Assert.Equal(withoutKev.Verdict.Action, skipped.Verdict.Action);
            Assert.False(skipped.Verdict.Acted);
            Assert.Contains(skipped.Verdict.Action, new[] { FabricActions.Ask, "stage_for_review" });
            Assert.Equal(0, Permissiveness[gate.Id](skipped.Verdict.Action));
        }
    }

    [Fact]
    public void A_skipped_gate_is_never_more_permissive_than_any_answer()
    {
        var policy = DecisionPolicy.Default;
        string[][] conditionSets =
        [
            [], [FabricConditions.PermissiveMode], [FabricConditions.AgentAuthored],
            [FabricConditions.PermissiveMode, FabricConditions.AgentAuthored],
        ];
        var checkedAnswers = 0;
        foreach (var gate in Gates)
        {
            var question = Fixtures.Ask(gate.Id).Value;
            var count = question.Options.Length;
            var floor = policy.Floors.FloorOf(gate.Id);
            // Peaks giving a measure of 0, the floor, just above it and 1, for every option (or score level).
            var floorPeak = gate.Measure == FloorMeasure.YesProbability ? floor : floor * (1 - 1.0 / count) + 1.0 / count;
            double[] peaks = [1.0 / count, Math.Clamp(floorPeak, 1.0 / count, 1), Math.Min(1, floorPeak + 1e-6), 0.9, 1.0];
            foreach (var conditions in conditionSets)
            {
                var held = Fixtures.Conditions(conditions);
                var skippedRank = Permissiveness[gate.Id](policy.Evaluate(gate.Id, null, held).Action);
                for (var option = 0; option < count; option++)
                    foreach (var peak in peaks)
                        foreach (var calibrated in new[] { true, false })
                        {
                            var rest = count == 1 ? 0 : (1 - peak) / (count - 1);
                            var answer = DecisionAnswer.For(question, Enumerable.Range(0, count).Select(i => i == option ? peak : rest), calibrated);
                            var verdict = policy.Evaluate(gate.Id, answer, held);
                            Assert.True(skippedRank <= Permissiveness[gate.Id](verdict.Action),
                                $"{gate.Id}: skipped is more permissive than {verdict.Action} (option {option}, peak {peak}, calibrated {calibrated})");
                            checkedAnswers++;
                        }
            }
        }
        Assert.True(checkedAnswers > 100);
    }
}
