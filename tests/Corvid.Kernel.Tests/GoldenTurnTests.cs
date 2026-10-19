using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Corvid.Evals;
using Corvid.Kernel.Turns;

namespace Corvid.Kernel.Tests;

/// <summary>
/// Golden trajectories recorded from the turn loop itself (C-009's format): each scenario runs a real turn on fakes and
/// must reproduce the committed fixture's events and projection, ids aside. <c>CORVID_RECORD_GOLDENS=1</c> rewrites the
/// fixtures, as for the seed set; review the diff.
/// </summary>
public sealed partial class GoldenTurnTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Scenarios() => ["turn-read-only-tool", "turn-repair-then-approval"];

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task The_turn_loop_reproduces_its_golden_trajectory(string id)
    {
        var (description, harness, text) = Scenario(id);
        using (harness)
        {
            await harness.RunAsync(text);
            var recorded = ReplayRunner.Record(new GoldenTrajectory(id, description, [.. harness.Payloads()],
                [new GoldenCheck(harness.Events().Count, false, null)]), Ct);
            var actual = GoldenTrajectoryFile.Serialize(recorded);

            if (Environment.GetEnvironmentVariable("CORVID_RECORD_GOLDENS") == "1")
            {
                File.WriteAllText(Path.Combine(SourceGoldenDirectory(), id + ".json"), actual);
                return;
            }
            var expected = Normalize(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "golden", id + ".json")));
            actual = Normalize(actual);
            // Structural: a decision's probability map is persisted in hash order, which differs between processes.
            using var expectedJson = JsonDocument.Parse(expected);
            using var actualJson = JsonDocument.Parse(actual);
            Assert.True(JsonElement.DeepEquals(expectedJson.RootElement, actualJson.RootElement), $"Expected:\n{expected}\nActual:\n{actual}");
        }
    }

    private static (string Description, TurnHarness Harness, string Text) Scenario(string id)
    {
        switch (id)
        {
            case "turn-read-only-tool":
            {
                var generator = new FakeGenerator(Reply.Call("list_notes", """{"limit":5}""", "call-1"), Reply.Say("One note: buy milk."))
                {
                    YesNo = FakeGenerator.YesTo("fully handled"),
                };
                var harness = new TurnHarness(generator);
                harness.Notes.AddNote("buy milk");
                return ("A turn on the degraded decider: ingress picks list_notes by embedding margin, so the generator is offered that "
                    + "one schema; the read-only call runs without approval; the step checkpoint's done stops the loop and the answer is "
                    + "written with no tools; the tool decision's outcome is tool_succeeded.", harness, "list my notes");
            }
            case "turn-repair-then-approval":
            {
                var generator = new FakeGenerator(
                    Reply.Call("add_note", """{"text":42}""", "call-1"),
                    Reply.Call("add_note", """{"text":"buy milk"}""", "call-2"),
                    Reply.Say("Saved: buy milk."));
                var harness = new TurnHarness(generator, prompter: FakePrompter.Always(approve: true));
                return ("A side-effecting call with invalid arguments is answered with the error and the expected schema, repaired "
                    + "once, then sent through the approval pipeline: risk is a gate, so without Kev it asks; the user approves and "
                    + "the prompt names its call.", harness, "note: buy milk");
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(id));
        }
    }

    // Decision and approval ids are random; their first-appearance order in the events is what must match. The projection
    // lists decisions sorted by id, so after renaming they are sorted again.
    private static string Normalize(string json)
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        var renamed = HexId().Replace(json, match =>
        {
            if (!ids.TryGetValue(match.Value, out var name))
                ids[match.Value] = name = "id-" + ids.Count.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
            return name;
        });
        var root = JsonNode.Parse(renamed)!;
        foreach (var check in root["checks"]!.AsArray())
        {
            if (check!["expected"]?["decisions"] is JsonArray decisions)
            {
                var sorted = decisions.OrderBy(item => (string?)item!["decision"]!["decision_id"], StringComparer.Ordinal)
                    .Select(item => item!.DeepClone()).ToArray();
                decisions.Clear();
                foreach (var item in sorted)
                    decisions.Add(item);
            }
        }
        return root.ToJsonString();
    }

    [GeneratedRegex("\\b[0-9a-f]{32}\\b")]
    private static partial Regex HexId();

    private static string SourceGoldenDirectory([CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "evals", "golden"));
}
