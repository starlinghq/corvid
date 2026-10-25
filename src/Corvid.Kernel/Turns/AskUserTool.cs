using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Turns;

/// <summary>
/// <c>ask_user</c> (K-6, Hermes' <c>clarify</c>): the generator asks the person one question, with up to four answers to pick
/// from. The turn loop recognises the call rather than running it: the call and its result are logged, the round's other
/// calls run, and the turn ends <see cref="TurnStatus.Clarifying"/> with the question as its answer and as a
/// <c>ClarificationAsked</c> event. The person's answer is their next message, which only they can send: nothing answers the
/// question on their behalf. Offered only in interactive turns: a scheduled run has nobody to ask.
/// </summary>
public sealed class AskUserTool : ToolFunction
{
    public const string ToolName = "ask_user";

    /// <summary>
    /// Requests it is shortlisted for (plan §5.4's multi-vector examples): ones that leave out what the action needs, in the
    /// user's words, so a vague request ranks it beside the tools it might mean.
    /// </summary>
    public static ImmutableArray<string> Examples { get; } =
    [
        "tag the release", "deploy it", "which one should I use", "move it to the other one", "fix the thing from before",
        "send it to them", "update the version", "do the usual",
    ];

    /// <summary>Answers offered at most; more are dropped.</summary>
    public const int MaxOptions = 4;

    private const string Schema = """
        {"type":"object","properties":{"question":{"type":"string","description":"The one question to ask, in a sentence."},"options":{"type":"array","items":{"type":"string"},"description":"Up to four short answers the person can pick from; leave out for a free-text answer."}},"required":["question"]}
        """;

    public AskUserTool()
        : base(new ToolDescriptor(ToolName,
            "Ask the user one short clarifying question when the request is ambiguous and you cannot act without the answer. " +
            "The turn ends with the question; the user's reply comes as their next message.",
            Schema, new ToolRisk(SideEffects.None, Network: false, Destructive: false),
            [new ToolParameter("question", "The one question to ask.", true, false), new ToolParameter("options", "Answers to pick from.", false, false)]))
    {
    }

    /// <summary>The question a valid call asks: its text, and its options trimmed, without blanks or repeats, at most four.</summary>
    public static (string Text, ImmutableArray<string> Options) Read(JsonElement arguments)
    {
        var text = arguments.TryGetProperty("question", out var question) && question.ValueKind == JsonValueKind.String
            ? question.GetString()!.Trim()
            : "";
        var options = ImmutableArray<string>.Empty;
        if (arguments.TryGetProperty("options", out var list) && list.ValueKind == JsonValueKind.Array)
            options = [.. list.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!.Trim())
                .Where(item => item.Length > 0).Distinct(StringComparer.Ordinal).Take(MaxOptions)];
        return (text, options);
    }

    // The turn loop never invokes it; anything else that does (a surface listing tools) is told so rather than answered.
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("ask_user is handled by the turn loop: it ends the turn with the question for the user.");
}
