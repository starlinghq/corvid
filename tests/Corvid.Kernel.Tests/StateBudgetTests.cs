using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using Corvid.Kernel.Turns;

namespace Corvid.Kernel.Tests;

/// <summary>
/// C-029 (spike S1): the ingress checkpoint that asks both <c>tool</c> and <c>skill</c> gets a 300-token TurnState, so
/// Kev-0.8B answers its six questions under 300 ms on the GPU; every other checkpoint keeps the 600-token default.
/// Sizes are the builder's own count (chars/4 here: the fake generator offers no tokenizer).
/// </summary>
public sealed class StateBudgetTests
{
    private sealed class Skills(params SkillOption[] options) : ISkillSource
    {
        public IReadOnlyList<SkillOption> Available(IReadOnlyCollection<string> tools) => options;

        public SkillBody? Load(string name) => null;
    }

    /// <summary>Records every request's question keys and the size of its state; answers nothing, so every gate stays below its floor.</summary>
    private sealed class RecordingDecider : IDecider
    {
        public List<(IReadOnlyList<string> Keys, int Tokens)> Requests { get; } = [];

        public string Name => "kev-test";

        public ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        {
            lock (Requests)
                Requests.Add(([.. request.Questions.Select(question => question.Key)], CharsPerTokenCounter.Instance.CountTokens(request.State.GetRawText())));
            return ValueTask.FromResult<DeciderResult>(new DeciderAnswers(Name, []));
        }
    }

    // An oversized turn, as S1's harness used: a pasted log in the message, a long history and a goal.
    private static readonly string Message = "list my notes, and here is the build log I mentioned: " +
        string.Join(' ', Enumerable.Range(0, 200).Select(i => $"line {i}: warning CS{1000 + i} in Notes{i}.cs, nullable reference"));

    private static async Task<RecordingDecider> RunAsync(bool skills, TurnOptions? options = null)
    {
        var decider = new RecordingDecider();
        // The generator narrates instead of calling the offered tool, so the turn also asks a step checkpoint (done, tool).
        using var harness = new TurnHarness(new FakeGenerator(Reply.Say("You have notes.")), calibrated: decider, options: options);
        for (var i = 0; i < 24; i++)
            harness.Seed(new UserMessage($"earlier request {i}: please tidy the notes about the quarterly planning meeting"),
                new AssistantMessage($"Done {i}: I moved the planning notes into one file and fixed the headings."));
        harness.Seed(new GoalSet("Keep the project notes tidy and summarise the build warnings"));
        var services = harness.Services();
        var runner = new TurnRunner(TurnHarness.Session, new TurnServices
        {
            Log = services.Log,
            Slots = services.Slots,
            Fabric = services.Fabric,
            Tools = services.Tools,
            Approvals = services.Approvals,
            Options = services.Options,
            Skills = skills
                ? new Skills(new SkillOption("tidy-notes", "Tidy a notes file: merge duplicates and fix headings.", [], true),
                    new SkillOption("build-report", "Summarise a build log's warnings by project.", [], true))
                : null,
        });

        await harness.RunAsync(runner, Message);
        return decider;
    }

    [Fact]
    public async Task An_ingress_with_both_shortlists_gets_the_300_token_state_and_the_step_keeps_600()
    {
        var decider = await RunAsync(skills: true);

        var ingress = Assert.Single(decider.Requests, request => request.Keys.Contains("intent"));
        Assert.Contains("tool", ingress.Keys);
        Assert.Contains("skill", ingress.Keys);
        Assert.InRange(ingress.Tokens, 200, TurnOptions.DefaultDualShortlistStateTokens);
        var step = Assert.Single(decider.Requests, request => request.Keys.Contains("done"));
        Assert.InRange(step.Tokens, TurnOptions.DefaultDualShortlistStateTokens + 1, TurnStateBudget.Default.Total);
        TestContext.Current.TestOutputHelper?.WriteLine($"ingress state {ingress.Tokens} tokens, step state {step.Tokens} tokens");
    }

    [Fact]
    public async Task An_ingress_with_only_the_tool_shortlist_keeps_the_600_token_default()
    {
        var decider = await RunAsync(skills: false);

        var ingress = Assert.Single(decider.Requests, request => request.Keys.Contains("intent"));
        Assert.Contains("tool", ingress.Keys);
        Assert.DoesNotContain("skill", ingress.Keys);
        Assert.InRange(ingress.Tokens, TurnOptions.DefaultDualShortlistStateTokens + 1, TurnStateBudget.Default.Total);
    }

    [Fact]
    public async Task Without_the_dual_budget_both_shortlists_get_the_default()
    {
        var decider = await RunAsync(skills: true, TurnOptions.Default with { DualShortlistStateBudget = null });

        var ingress = Assert.Single(decider.Requests, request => request.Keys.Contains("intent"));
        Assert.Contains("skill", ingress.Keys);
        Assert.InRange(ingress.Tokens, TurnOptions.DefaultDualShortlistStateTokens + 1, TurnStateBudget.Default.Total);
    }
}
