using System.Collections.Immutable;
using System.Globalization;
using Corvid.Abstractions.Events;
using Corvid.Host.Composition;
using Corvid.Kernel.Turns;
using Corvid.Models;

namespace Corvid.Host.Commands;

/// <summary>
/// A5 (plan §14): <c>/agents</c> lists, shows and stops this session's child sessions; <c>/delegate</c> starts one with the
/// goal, toolset, model mix and budget the person gives. The person typed it, so <c>/delegate</c> asks no approval; the
/// child's own calls ask as the session's would, and it runs within the same ceilings as an agent's (<c>Corvid:Agents</c>).
/// </summary>
public static class AgentCommands
{
    public const string AgentsUsage = "[list] | show <child> | stop <child>";

    public const string DelegateUsage = "[--mix <name>] [--model <slot>=<model>]… [--tools <a,b>] [--toolset <name>] [--steps <n>] " +
        "[--tokens <n>] [--seconds <n>] <goal>";

    private const int GoalChars = 60;

    internal static ValueTask<CommandOutput> AgentsAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken) =>
        ValueTask.FromResult(Agents(context.Runtime, context.Session, invocation.Arguments));

    internal static CommandOutput Agents(CorvidRuntime runtime, CorvidSession session, ImmutableArray<string> arguments)
    {
        var children = runtime.Children(session.Id);
        switch (arguments)
        {
            case [] or ["list"]:
                if (children.Count == 0)
                    return CommandOutput.Text("This session has started no child sessions. /delegate <goal> starts one; the agent starts them with delegate_task.");
                return new CommandOutput
                {
                    Title = "Child sessions",
                    Table = CommandTable.Of(["#", "child", "status", "by", "goal", "tools", "budget"], children.Select((child, i) => new[]
                    {
                        (i + 1).ToString(CultureInfo.InvariantCulture),
                        child.ChildId,
                        child.Status,
                        child.Spawned.By,
                        Short(child.Spawned.Goal, GoalChars),
                        child.Spawned.Tools.IsEmpty ? "none" : string.Join(", ", child.Spawned.Tools.Select(TerminalText.Visible)),
                        DelegationCard.Budget(child.Spawned.Budget),
                    })),
                    Lines = ["/agents show <child> for its answer; /agents stop <child> stops a running one. <child> is its id or number."],
                };
            case ["show", var which]:
                return Find(children, which) is { } shown ? Show(shown) : NotFound(which);
            case ["stop", var which]:
                if (Find(children, which) is not { } stopping)
                    return NotFound(which);
                return runtime.StopChild(stopping.ChildId)
                    ? CommandOutput.Success($"Stopping {stopping.ChildId}: its turn is cancelled and it ends 'cancelled'.")
                    : CommandOutput.Warning($"{stopping.ChildId} is not running in this process ({stopping.Status}).");
            default:
                return CommandOutput.Error("Usage: /agents " + AgentsUsage);
        }
    }

    private static ChildSummary? Find(IReadOnlyList<ChildSummary> children, string which) =>
        int.TryParse(which, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1 && number <= children.Count
            ? children[number - 1]
            : children.FirstOrDefault(child => child.ChildId == which || child.ChildId.EndsWith(which, StringComparison.Ordinal) && which.Length >= 4);

    private static CommandOutput NotFound(string which) =>
        CommandOutput.Error($"This session has no child '{TerminalText.Visible(which)}'; /agents lists them.");

    private static CommandOutput Show(ChildSummary child)
    {
        var spawned = child.Spawned;
        var lines = new List<string>
        {
            "goal: " + TerminalText.Visible(spawned.Goal),
            "status: " + child.Status + (child.Finished?.Detail is { Length: > 0 } detail ? " (" + TerminalText.Visible(detail) + ")" : ""),
            "started by: " + (spawned.By == ChildSessions.ByUser ? "you (/delegate or the app)" : "the agent (delegate_task" + (spawned.CallId is { } call ? ", call " + call : "") + ")"),
            "tools: " + (spawned.Tools.IsEmpty ? "none" : string.Join(", ", spawned.Tools.Select(TerminalText.Visible))),
            "budget: " + DelegationCard.Budget(spawned.Budget),
            "models: " + (spawned.Mix is { } mix ? TerminalText.Visible(mix.GetRawText()) : "the parent's"),
        };
        if (spawned.Tainted || child.Finished?.Tainted == true)
            lines.Add("taint: holds outside content");
        if (spawned.OutputSchema is not null)
            lines.Add("output schema: " + (child.Finished?.OutputValid switch { true => "met", false => "not met", null => "not checked yet" }));
        if (child.Finished is { } finished)
        {
            lines.Add($"used: {finished.Steps} steps, {finished.Tokens.ToString("N0", CultureInfo.InvariantCulture)} tokens");
            if (finished.Text is { Length: > 0 } text)
                lines.Add("answer: " + TerminalText.Visible(text));
        }
        lines.Add($"corvid chat --session {child.ChildId} cannot drive it (a child is read-only); the app's Board shows it under this session.");
        return new CommandOutput { Title = "Child " + child.ChildId, Lines = [.. lines] };
    }

    internal static async ValueTask<CommandOutput> DelegateAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        if (!TryParse(invocation.Arguments, out var task, out var error))
            return CommandOutput.Error(error! + " Usage: /delegate " + DelegateUsage);
        var runtime = context.Runtime;
        try
        {
            var request = runtime.DelegationRequest(context.Session.Id, task!, offered: null, ChildSessions.ByUser, callId: null);
            var plan = await runtime.PlanChildAsync(context.Session.Id, request, cancellationToken).ConfigureAwait(false);
            var child = runtime.StartChild(plan);
            return new CommandOutput
            {
                Title = "Started " + child.Id,
                Tone = OutputTone.Success,
                Lines = [.. DelegationCard.Lines(new DelegationPreview(plan.Goal, plan, null)),
                    "It runs in the background; its end is reported here. /agents lists it, /agents stop " + child.Id + " stops it."],
            };
        }
        catch (ChildRefusedException exception)
        {
            return CommandOutput.Error("/delegate: " + exception.Message);
        }
    }

    /// <summary>Reads <c>/delegate</c>'s options, then the goal as the rest of the line.</summary>
    internal static bool TryParse(ImmutableArray<string> arguments, out DelegationTask? task, out string? error)
    {
        task = null;
        error = null;
        string? mixName = null, toolset = null;
        int? steps = null, tokens = null, seconds = null;
        var tools = new List<string>();
        var slots = new List<KeyValuePair<string, ModelMixSlot>>();
        var i = 0;
        for (; i < arguments.Length && arguments[i].StartsWith("--", StringComparison.Ordinal); i++)
        {
            var option = arguments[i];
            if (i + 1 >= arguments.Length)
            {
                error = $"{option} needs a value.";
                return false;
            }
            var value = arguments[++i];
            switch (option)
            {
                case "--mix":
                    mixName = value;
                    break;
                case "--toolset":
                    toolset = value;
                    break;
                case "--tools":
                    tools.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--model":
                    var equals = value.IndexOf('=', StringComparison.Ordinal);
                    if (equals <= 0 || equals == value.Length - 1)
                    {
                        error = "--model takes <slot>=<model>, such as generator=qwen3.5-9b.";
                        return false;
                    }
                    slots.Add(KeyValuePair.Create(value[..equals].Trim(), new ModelMixSlot(value[(equals + 1)..].Trim())));
                    break;
                case "--steps" or "--tokens" or "--seconds":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
                    {
                        error = $"{option} takes a positive whole number.";
                        return false;
                    }
                    if (option == "--steps")
                        steps = number;
                    else if (option == "--tokens")
                        tokens = number;
                    else
                        seconds = number;
                    break;
                default:
                    error = $"Unknown option {TerminalText.Visible(option)}.";
                    return false;
            }
        }
        var goal = string.Join(' ', arguments.Skip(i)).Trim();
        if (goal.Length == 0)
        {
            error = "Give the child a goal.";
            return false;
        }
        ModelMix? mix = null;
        if (slots.Count > 0)
        {
            try
            {
                mix = ModelMix.Create(slots);
            }
            catch (ModelMixException exception)
            {
                error = exception.Message;
                return false;
            }
        }
        task = new DelegationTask(goal)
        {
            Tools = [.. tools],
            Toolset = toolset,
            Mix = mix,
            MixName = mixName,
            MaxSteps = steps,
            MaxTokens = tokens,
            MaxSeconds = seconds,
        };
        return true;
    }

    private static string Short(string text, int max)
    {
        var line = TerminalText.Visible(text.ReplaceLineEndings(" ").Trim());
        return line.Length > max ? line[..max] + "…" : line;
    }

    /// <summary>A child's end as a chat line: "child-… answered in 2 steps: <answer>".</summary>
    public static string Ended(ChildFinished finished)
    {
        ArgumentNullException.ThrowIfNull(finished);
        var what = finished.Status == ChildStatuses.Answered && finished.Text is { Length: > 0 } text ? ": " + Short(text, 200)
            : finished.Detail is { Length: > 0 } detail ? " (" + Short(detail, 200) + ")" : "";
        return $"{finished.ChildId} {finished.Status.Replace('_', ' ')} after {finished.Steps} steps{what}";
    }

    /// <summary>A child's start as a chat line.</summary>
    public static string Started(ChildSpawned spawned)
    {
        ArgumentNullException.ThrowIfNull(spawned);
        return $"{spawned.ChildId} started" + (spawned.By == ChildSessions.ByUser ? "" : " by the agent") + ": " + Short(spawned.Goal, 120);
    }

}
