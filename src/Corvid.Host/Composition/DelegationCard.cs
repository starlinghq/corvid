using System.Globalization;
using Corvid.Approvals;
using Corvid.Host.Commands;
using Corvid.Kernel.Turns;
using Corvid.Models;

namespace Corvid.Host.Composition;

/// <summary>
/// A5: what an approval card adds for child sessions, the same on every surface: a <c>delegate_task</c> spawn shows each
/// child's plan (goal, toolset with what was left out, budget, model mix, approval mode, taint), and a prompt from a child
/// says which child asks. Names and goals are model or configuration text: they come back with hidden characters shown as
/// escapes (<see cref="TerminalText.Visible"/>), so the person sees what they grant.
/// </summary>
public static class DelegationCard
{
    /// <summary>The longest goal a card line shows, in characters.</summary>
    public const int GoalChars = 400;

    /// <summary>Whether the prompt is for a <c>delegate_task</c> call (a spawn).</summary>
    public static bool IsSpawn(ApprovalPrompt prompt) =>
        prompt?.Action is ToolAction { Tool.Name: DelegateTaskTool.ToolName };

    /// <summary>
    /// The card's extra lines for a prompt: the spawn's children, or which child a child's prompt comes from; empty for any
    /// other prompt. Never throws for a plan it cannot make: the refusal is a line.
    /// </summary>
    public static async Task<IReadOnlyList<string>> LinesAsync(CorvidRuntime runtime, ApprovalPrompt prompt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(prompt);
        var lines = new List<string>();
        if (runtime.TryGetSession(prompt.SessionId, out var session) && session.Child is { } child)
            lines.Add($"Asked by child session {prompt.SessionId} of {child.ParentId}, working on: {Goal(child.Goal)}");
        if (IsSpawn(prompt))
        {
            var previews = await runtime.PreviewDelegationAsync(prompt.SessionId, ((ToolAction)prompt.Action!).Arguments, cancellationToken)
                .ConfigureAwait(false);
            for (var i = 0; i < previews.Count; i++)
                lines.AddRange(Lines(previews[i], previews.Count == 1 ? null : i + 1));
        }
        return lines;
    }

    /// <summary>One child's lines: its goal, then its plan or why it would not start.</summary>
    public static IReadOnlyList<string> Lines(DelegationPreview preview, int? number = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        var lines = new List<string> { (number is { } n ? $"Child {n}: " : "Child: ") + Goal(preview.Goal) };
        if (preview.Plan is not { } plan)
        {
            lines.Add("  would not start: " + TerminalText.Visible(preview.Refusal ?? "unknown"));
            return lines;
        }
        lines.Add("  tools: " + (plan.Tools.IsEmpty ? "none" : string.Join(", ", plan.Tools.Select(TerminalText.Visible))));
        foreach (var dropped in plan.Dropped)
            lines.Add($"  left out: {TerminalText.Visible(dropped.Name)} ({dropped.Reason})");
        lines.Add("  budget: " + Budget(plan.Budget));
        lines.Add("  models: " + Models(plan));
        lines.Add($"  approvals: {plan.ApprovalMode.ToString().ToLowerInvariant()}" + (plan.Tainted ? "; starts holding outside content (tainted)" : ""));
        if (plan.OutputSchema is not null)
            lines.Add("  answer: a JSON object checked against the output schema");
        return lines;
    }

    /// <summary>"8 steps, 64,000 tokens, 600 s".</summary>
    public static string Budget(Corvid.Abstractions.Events.ChildBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return string.Create(CultureInfo.InvariantCulture, $"{budget.MaxSteps} steps, {budget.MaxTokens:N0} tokens, {budget.MaxSeconds} s");
    }

    /// <summary>
    /// "generator qwen3.5-9b (local, escalates to claude); decider kev-0.8b (local)": the generator and the decider, and every
    /// slot the mix names; the rest (embedder, extractor…) run on the parent's and are left out.
    /// </summary>
    public static string Models(ChildPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var shown = plan.Slots
            .Where(slot => slot.Slot is ModelSlots.Generator or ModelSlots.Decider || plan.Mix?.Slots.ContainsKey(slot.Slot) == true)
            .Select(slot =>
            {
                var where = slot.Privacy switch { PrivacyClass.Local => "local", PrivacyClass.LocalServer => "local server", _ => "cloud" };
                var ladder = slot.Escalate && !slot.Ladder.IsEmpty ? ", escalates to " + string.Join(" then ", slot.Ladder.Select(TerminalText.Visible))
                    : !slot.Escalate && plan.Mix?.Slots.ContainsKey(slot.Slot) == true ? ", no escalation" : "";
                return $"{slot.Slot} {TerminalText.Visible(slot.Model)} ({where}{ladder})";
            }).ToList();
        return shown.Count == 0 ? "the parent's" : string.Join("; ", shown);
    }

    private static string Goal(string goal)
    {
        var visible = TerminalText.Visible(goal.ReplaceLineEndings(" ").Trim());
        return visible.Length > GoalChars ? visible[..GoalChars] + "…" : visible;
    }
}
