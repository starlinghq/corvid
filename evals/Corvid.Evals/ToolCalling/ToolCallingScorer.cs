using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Turns;

namespace Corvid.Evals.ToolCalling;

/// <summary>How one case ended in one configuration (S5's outcome classes, plus the turn loop's own endings).</summary>
public enum CaseOutcome
{
    /// <summary>The expected tool (or tools, in order), or no call on a no-tool case.</summary>
    Correct,
    /// <summary>An offered tool ran, but not the expected one (or a multi-step chain went off its steps).</summary>
    WrongTool,
    /// <summary>Every call named a tool that was not offered.</summary>
    InvalidName,
    /// <summary>Calls named offered tools but none had arguments that passed the schema.</summary>
    Malformed,
    /// <summary>Prose instead of a call, on a case that needs a tool.</summary>
    NoCall,
    /// <summary>A call on a case that needs none.</summary>
    CalledOnNoTool,
    /// <summary><c>clarify</c> (or <c>intent</c>) acted: the turn asked the user a question.</summary>
    Clarified,
    /// <summary>The generator escalated (difficulty, repair, stuck or context); a miss in the headline.</summary>
    Escalated,
    /// <summary>The turn failed or threw.</summary>
    Failed,
}

/// <summary>One issued call as the log shows it.</summary>
/// <param name="Rejected">Its result was an <c>invalid arguments</c> error (schema or binding), so it never ran.</param>
/// <param name="InvalidName">It named a tool that was not offered.</param>
public sealed record ScoredCall(string Tool, JsonElement Arguments, int? Round, bool Rejected, bool InvalidName);

/// <summary>One case scored in one configuration.</summary>
public sealed record CaseScore(
    string Id,
    string Configuration,
    ImmutableArray<string> Tags,
    string Expected,
    CaseOutcome Outcome,
    CaseOutcome LocalOutcome,
    string? EscalationReason,
    string Status,
    ImmutableArray<ScoredCall> Calls,
    bool? ArgsValid,
    bool? ArgsMatch,
    ImmutableArray<string> ArgumentMisses,
    bool FirstResponseCorrect,
    bool RepairedToCorrect,
    bool Nudged,
    int Steps,
    int StepsMatched,
    int StepsExpected,
    string? KevToolPick,
    double? KevToolConfidence,
    bool KevToolActed,
    ImmutableArray<string> Acted,
    double TurnMs,
    double GeneratorMs,
    int GeneratorCalls,
    double DeciderMs,
    int DeciderCalls,
    double EmbedMs,
    int? PromptTokens,
    ImmutableArray<string> Offered,
    string Text,
    string? Error)
{
    /// <summary>The headline's tool choice: correct, with an escalation counted as a miss.</summary>
    public bool ToolCorrect => Outcome == CaseOutcome.Correct;

    /// <summary>The headline's argument match: correct, and every constraint holds (S2's "argument exact match").</summary>
    public bool ArgumentsCorrect => Outcome == CaseOutcome.Correct && ArgsMatch != false;

    /// <summary>C-033's ingress description check acted: some shortlisted MCP tools were withheld from this turn.</summary>
    public bool WithheldDescriptions => Acted.Any(acted => acted.StartsWith("injection/mcp:", StringComparison.Ordinal));
}

/// <summary>
/// Scores a <see cref="CaseRun"/> from its session's events and the turn's result (C-034). The decisive call of a
/// single-step case is the first one that ran; a rejected call before it is a repair. A multi-step case is correct when the
/// calls that ran match its steps in order, repeats of a matched step aside, with nothing else run.
/// </summary>
public static class ToolCallingScorer
{
    public static string Name(CaseOutcome outcome) => outcome switch
    {
        CaseOutcome.Correct => "correct",
        CaseOutcome.WrongTool => "wrong_tool",
        CaseOutcome.InvalidName => "invalid_name",
        CaseOutcome.Malformed => "malformed",
        CaseOutcome.NoCall => "no_call",
        CaseOutcome.CalledOnNoTool => "called_on_no_tool",
        CaseOutcome.Clarified => "clarified",
        CaseOutcome.Escalated => "escalated",
        CaseOutcome.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    public static CaseScore Score(CaseRun run, ToolCallingSet set)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(set);
        var item = run.Case;
        var offeredEver = run.GeneratorCalls.SelectMany(call => call.Offered).ToHashSet(StringComparer.Ordinal);
        var results = run.Events.OfType<ToolResult>().GroupBy(result => result.CallId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var calls = run.Events.OfType<ToolCallIssued>().Select(call =>
        {
            var result = results.GetValueOrDefault(call.CallId);
            var rejected = result is { IsError: true } && ErrorKind(result.Result) == ToolErrorKinds.InvalidArguments;
            var invalidName = rejected && (!set.Tools.ContainsKey(call.ToolName) || !offeredEver.Contains(call.ToolName)
                || Details(result!.Result).Any(detail => detail.Contains("is not one of the tools offered", StringComparison.Ordinal)));
            return new ScoredCall(call.ToolName, call.Arguments, call.Round, rejected, invalidName);
        }).ToImmutableArray();
        var ran = calls.Where(call => !call.Rejected).ToList();

        var misses = new List<string>();
        CaseOutcome local;
        bool? argsMatch = null;
        var matched = 0;
        ScoredCall? decisive = null;
        if (item.ExpectsNoTool)
        {
            local = calls.IsEmpty ? CaseOutcome.Correct : CaseOutcome.CalledOnNoTool;
        }
        else if (ran.Count > 0)
        {
            if (item.MultiStep)
            {
                var extra = false;
                var chain = new List<(ExpectedStep Step, ScoredCall Call)>();
                foreach (var call in ran)
                {
                    if (matched < item.Steps.Length && item.Steps[matched].Expect.Accepts(call.Tool))
                        chain.Add((item.Steps[matched++], call));
                    else if (!chain.Any(link => link.Call.Tool == call.Tool))
                        extra = true;
                }
                local = matched == item.Steps.Length && !extra ? CaseOutcome.Correct : CaseOutcome.WrongTool;
                argsMatch = chain.Count == 0 ? null : chain.Aggregate(true, (all, link) => Matches(link.Step, link.Call, set, misses) & all);
                decisive = ran[0];
            }
            else
            {
                decisive = ran[0];
                local = item.Steps[0].Expect.Accepts(decisive.Tool) ? CaseOutcome.Correct : CaseOutcome.WrongTool;
                if (local == CaseOutcome.Correct)
                    argsMatch = Matches(item.Steps[0], decisive, set, misses);
                matched = local == CaseOutcome.Correct ? 1 : 0;
            }
        }
        else if (!calls.IsEmpty)
        {
            local = calls[^1].InvalidName ? CaseOutcome.InvalidName : CaseOutcome.Malformed;
        }
        else
        {
            local = CaseOutcome.NoCall;
        }

        var escalation = run.Result?.Escalations.FirstOrDefault();
        var reason = escalation is null ? null : escalation[(escalation.LastIndexOf(": ", StringComparison.Ordinal) + 2)..];
        var outcome = run.Error is not null ? CaseOutcome.Failed
            : reason is not null ? CaseOutcome.Escalated
            : run.Result!.Status == TurnStatus.Clarifying ? CaseOutcome.Clarified
            : run.Result.Status == TurnStatus.Failed ? CaseOutcome.Failed
            : local;

        var firstOffered = calls.FirstOrDefault(call => !call.InvalidName);
        var argsValid = firstOffered is null ? (bool?)null : !firstOffered.Rejected;
        var correct = outcome == CaseOutcome.Correct;
        var firstResponse = correct && (item.ExpectsNoTool || calls.Length > 0 && !calls[0].Rejected && argsMatch != false);
        var repaired = correct && !item.ExpectsNoTool && calls.Any(call => call.Rejected);

        var decisions = run.Events.OfType<Decision>().ToList();
        var tool = decisions.FirstOrDefault(decision => decision.QuestionId == "tool");
        var kevTool = tool is { Calibrated: true } ? tool : null;
        var acted = decisions.Where(decision => decision.Action is not null && Acted(decision)).Select(decision => decision.QuestionId + "=" + decision.Action)
            .ToImmutableArray();

        var generatorMs = run.GeneratorCalls.Sum(call => call.Ms);
        return new CaseScore(item.Id, ToolCallingConfigurations.Name(run.Configuration), item.Tags,
            item.MultiStep ? string.Join(" → ", item.Steps.Select(step => step.Expect.ToString())) : item.Steps[0].Expect.ToString(),
            outcome, local, reason, run.Error is not null ? "error" : TurnRunner.StatusName(run.Result!.Status), calls, argsValid, argsMatch,
            [.. misses], firstResponse, repaired, run.GeneratorCalls.Any(call => call.Nudge?.StartsWith("required:", StringComparison.Ordinal) == true),
            run.Result?.Steps ?? 0, matched, item.ExpectsNoTool ? 0 : item.Steps.Length,
            kevTool is null ? null : Pick(kevTool), kevTool?.Confidence, kevTool is not null && Acted(kevTool), acted,
            run.TurnMs, generatorMs, run.GeneratorCalls.Length, run.DeciderMs.Sum(), run.DeciderMs.Length, run.EmbedMs.Sum(),
            run.GeneratorCalls.FirstOrDefault()?.InputTokens, run.GeneratorCalls.FirstOrDefault()?.Offered ?? [],
            TimedChatClient.Short(run.Result?.Text ?? "", 300), run.Error);
    }

    /// <summary>Whether every constraint that applies to the called tool holds. With any_of, a constraint applies when the tool declares it.</summary>
    private static bool Matches(ExpectedStep step, ScoredCall call, ToolCallingSet set, List<string> misses)
    {
        var tool = set.Tools[call.Tool];
        var all = true;
        foreach (var (parameter, constraint) in step.Args.Where(pair => tool.Declares(pair.Key)))
        {
            JsonElement? value = call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty(parameter, out var found) ? found : null;
            if (constraint.Holds(value))
                continue;
            all = false;
            misses.Add($"{call.Tool}.{parameter}: {constraint} (got {(value is { } v ? v.GetRawText() : "nothing")})");
        }
        return all;
    }

    // The policy's action for a choice that acted is "<act>:<option>"; a below-floor action never has the act's prefix.
    private static bool Acted(Decision decision) => decision.QuestionId switch
    {
        "tool" => decision.Action.StartsWith("offer_tool:", StringComparison.Ordinal),
        "intent" => decision.Action.StartsWith("route:", StringComparison.Ordinal),
        "clarify" => decision.Action == "ask_clarifying_question",
        "difficulty" => decision.Action == "escalate_generator",
        "done" => decision.Action == "stop",
        "stuck" => decision.Action == "change_strategy",
        // C-033: a description question that acted withholds that chunk's MCP tools from the turn.
        _ when decision.QuestionId.StartsWith("injection/mcp:", StringComparison.Ordinal) => decision.Action == "mark_tainted",
        _ => false,
    };

    private static string? Pick(Decision decision) =>
        decision.Probabilities.IsEmpty ? null : decision.Probabilities.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).First().Key;

    private static string? ErrorKind(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;

    private static IEnumerable<string> Details(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array
            ? details.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)
            : [];
}
