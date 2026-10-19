using System.Globalization;
using Corvid.Approvals;
using Corvid.Fabric;
using Corvid.Host.Composition;
using Corvid.Memory;
using Corvid.Models;
using Corvid.Tools;

namespace Corvid.Host.Commands;

/// <summary>
/// The v1 core set (plan §17 row 1.3): <c>/model</c>, <c>/status</c>, <c>/goal</c>, <c>/approvals</c>, <c>/memory</c>,
/// <c>/help</c>, <c>/exit</c>, and commands that map directly onto existing APIs: <c>/tools</c>, <c>/todos</c> and <c>/cancel</c>.
/// </summary>
public static class CoreCommands
{
    public static SlashCommandRegistry Register(SlashCommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry
            .Add(new SlashCommand("help", "List the commands.", "", Help))
            .Add(new SlashCommand("model", "List the model slots, or switch one for this session.",
                "[slot] <model> [--pin] | unpin <slot>", ModelAsync))
            .Add(new SlashCommand("status", "Slots with their device, queue depth and median latency; the session's state.", "", Status))
            .Add(new SlashCommand("goal", "Show, set or clear the standing goal.", "[text | clear]", Goal))
            .Add(new SlashCommand("approvals", "Pending approvals and the remembered approval rules.", "", Approvals))
            .Add(new SlashCommand("memory", "Review staged memory writes.", "[pending] | approve <id> [operation] | reject <id>", MemoryAsync))
            .Add(new SlashCommand("tools", "The tools the model can be offered, with their risk.", "", Tools))
            .Add(new SlashCommand("todos", "The session's todo list.", "", Todos))
            .Add(new SlashCommand("cancel", "Cancel the running turn.", "", Cancel))
            .Add(new SlashCommand("exit", "Leave the session.", "", Exit) { Aliases = ["quit"] });
    }

    private static ValueTask<CommandOutput> Help(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CommandOutput
        {
            Title = "Commands",
            Table = CommandTable.Of(["command", "arguments", ""], context.Registry.Commands.Select(command => new[]
            {
                "/" + command.Name + (command.Aliases.IsEmpty ? "" : " (" + string.Join(", ", command.Aliases.Select(alias => "/" + alias)) + ")"),
                command.Usage, command.Summary,
            })),
        });

    private static ValueTask<CommandOutput> Exit(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new CommandOutput { Exit = true, Lines = [$"Leaving session {context.Session.Id}."] });

    private static ValueTask<CommandOutput> Cancel(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        context.Session.Cancel();
        return ValueTask.FromResult(CommandOutput.Text("Cancelling the running turn, if any."));
    }

    // ---- /model ----

    private static async ValueTask<CommandOutput> ModelAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        var arguments = invocation.Arguments.Where(argument => argument != "--pin").ToArray();
        var pin = invocation.Arguments.Contains("--pin");
        if (arguments.Length == 0)
            return ListModels(context);
        if (arguments is ["unpin", var unpinned])
        {
            ModelSlots.ContractOf(unpinned);
            context.Session.Slots.Unpin(unpinned);
            return CommandOutput.Success($"{unpinned} is unpinned: it returns to its default when the turn ends.");
        }

        string slot, name;
        if (arguments is [var only])
        {
            // A model alone switches the generator when it can generate, else the slot it serves.
            var model = context.Runtime.Catalogue.Find(only);
            slot = model.Serves(ModelSlots.Generator) ? ModelSlots.Generator : model.Slots[0];
            name = model.Name;
        }
        else if (arguments is [var slotName, var modelName])
        {
            ModelSlots.ContractOf(slotName);
            slot = slotName;
            name = context.Runtime.Catalogue.Find(modelName).Name;
        }
        else
        {
            return CommandOutput.Error("Usage: /model [slot] <model> [--pin], or /model unpin <slot>.");
        }

        if (slot == ModelSlots.TextEmbedder && !context.Runtime.IsConfigured(slot, name))
            return CommandOutput.Error($"Memory embeds only with the embedder.text models configured at start. Add {name} to " +
                "Corvid:Models:Slots:embedder.text:Fallbacks, then restart.");
        var availability = context.Runtime.Catalogue.Check(name);
        if (!availability.IsReady)
            return CommandOutput.Error(availability.Detail + (availability.Acquire is { } acquire ? $" Run `{acquire}`." : ""));

        var binding = context.Runtime.Bind(slot, name);
        var result = await context.Session.Actor.SwitchModelAsync(slot, binding, pin, cancellationToken).ConfigureAwait(false);
        var carried = result.PendingApprovals.IsEmpty ? "" : $" {result.PendingApprovals.Length} pending approval(s) carry over.";
        return result.Status switch
        {
            SwitchStatus.Switched => CommandOutput.Success($"{slot}: {result.From.Id} → {result.To!.Id}{(pin ? " (pinned)" : "")}.{carried}"),
            SwitchStatus.Unchanged => CommandOutput.Text($"{slot} already uses {binding.Profile.Id}."),
            SwitchStatus.Refused => CommandOutput.Error($"{slot}: the privacy gate refused {binding.Profile.Id}: {result.Detail}"),
            _ => CommandOutput.Error($"{slot}: nothing to switch to ({result.Detail})."),
        };
    }

    private static CommandOutput ListModels(SlashCommandContext context)
    {
        var rows = new List<string[]>();
        foreach (var slot in ModelSlots.All)
        {
            var bound = context.Session.Slots.TryCurrent(slot, out var binding);
            var configured = context.Runtime.Registry.TryGet(slot, out var configuration);
            var models = context.Runtime.Catalogue.ForSlot(slot).Select(model => model.Name).ToArray();
            // The decider is shown even unbound: it is the degraded tier, which is worth saying.
            if (!bound && models.Length == 0 && slot != ModelSlots.Decider)
                continue;
            rows.Add([
                slot,
                bound ? binding!.Profile.Id + (context.Session.Slots.IsPinned(slot) ? " (pinned)" : "") : slot == ModelSlots.Decider ? "degraded tier" : "unbound",
                configured ? configuration!.Default.Profile.Id : "",
                configured ? string.Join(", ", configuration!.Escalation.Select(item => item.Profile.Id)) : "",
                string.Join(", ", models),
            ]);
        }
        return new CommandOutput
        {
            Title = "Model slots",
            Table = CommandTable.Of(["slot", "current", "default", "escalation", "models"], rows),
            Lines = ["Switch with /model [slot] <model> [--pin]. The decider runs on the degraded tier until Kev is published."],
        };
    }

    // ---- /status ----

    private static ValueTask<CommandOutput> Status(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        var runtime = context.Runtime;
        var session = context.Session;
        var rows = new List<string[]>();
        foreach (var slot in ModelSlots.All)
        {
            if (!session.Slots.TryCurrent(slot, out var binding))
                continue;
            rows.Add(Row(slot, binding.Profile.Id, binding.Profile.Placement, runtime.Metrics.TryGet(slot, binding.Profile.Id, out var latency) ? latency : null));
        }
        // The decider slot is the degraded tier: the generator's and embedder's yes/no and similarity answers at fabric priority.
        if (!session.Slots.TryCurrent(ModelSlots.Decider, out _) && runtime.Registry.TryGet(ModelSlots.Generator, out var generator))
            rows.Add(Row(ModelSlots.Decider, DegradedDecider.DeciderName, generator.Default.Profile.Placement,
                runtime.Metrics.TryGet(ModelSlots.Decider, DegradedDecider.DeciderName, out var decider) ? decider : null));

        var snapshot = session.Snapshot();
        return ValueTask.FromResult(new CommandOutput
        {
            Title = "Status",
            Table = CommandTable.Of(["slot", "model", "device", "queue", "p50", "calls"], rows),
            Lines =
            [
                $"session {session.Id}: {snapshot.Head} events, {snapshot.PendingApprovals.Count} pending approval(s)" +
                (session.Tainted ? ", tainted by outside content" : ""),
                $"goal: {snapshot.Goal ?? "none"}",
                $"log: {Configuration.CorvidPaths.EventLog(runtime.Options)}",
            ],
        });

        string[] Row(string slot, string model, string placement, Telemetry.ModelLatency? latency) =>
        [
            slot, model, placement,
            Composition.DeviceSchedulers.IsLocalDevice(placement) ? runtime.Devices.QueueDepth(placement).ToString(CultureInfo.InvariantCulture) : "-",
            latency?.P50 is { } p50 ? FormatDuration(p50) : "-",
            latency is null ? "0" : latency.Calls.ToString(CultureInfo.InvariantCulture),
        ];
    }

    public static string FormatDuration(TimeSpan duration) => duration.TotalSeconds >= 1
        ? duration.TotalSeconds.ToString("0.0 s", CultureInfo.InvariantCulture)
        : duration.TotalMilliseconds.ToString("0 ms", CultureInfo.InvariantCulture);

    // ---- /goal ----

    private static ValueTask<CommandOutput> Goal(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Text.Length == 0)
            return ValueTask.FromResult(CommandOutput.Text(context.Session.Snapshot().Goal is { } goal ? $"Goal: {goal}" : "No goal is set. /goal <text> sets one."));
        if (invocation.Text is "clear" or "none")
        {
            context.Session.SetGoal(null);
            return ValueTask.FromResult(CommandOutput.Success("Goal cleared."));
        }
        context.Session.SetGoal(invocation.Text);
        return ValueTask.FromResult(CommandOutput.Success($"Goal set: {invocation.Text}"));
    }

    // ---- /approvals ----

    private static ValueTask<CommandOutput> Approvals(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        var pending = context.Session.Snapshot().PendingApprovals.Values.OrderBy(request => request.ApprovalId, StringComparer.Ordinal).ToArray();
        var rules = context.Runtime.Approvals.SessionRules(context.Session.Id).Select(rule => ("session", rule))
            .Concat(context.Runtime.Approvals.AlwaysRules.Select(rule => ("always", rule))).ToArray();
        var lines = new List<string>
        {
            $"Mode: {context.Runtime.Options.Approvals.Mode.ToString().ToLowerInvariant()}; unanswered prompts are denied after " +
            $"{context.Runtime.Approvals.Options.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s.",
        };
        lines.AddRange(pending.Length == 0 ? ["No approval is pending."] : pending.Select(request => $"pending {request.ApprovalId}: {request.Action} ({request.Reason})"));
        if (rules.Length == 0)
            lines.Add("No remembered rules. Answering a prompt with 'session' or 'always' remembers one.");
        return ValueTask.FromResult(new CommandOutput
        {
            Title = "Approvals",
            Lines = [.. lines],
            Table = rules.Length == 0 ? null : CommandTable.Of(["scope", "rule", "command", "paths and names", "from"], rules.Select(pair => new[]
            {
                pair.Item1,
                pair.rule.Decision == RuleDecision.Allow ? "allow" : "deny",
                pair.rule.Verb + (pair.rule.Subcommand is { } sub ? " " + sub : "") + (pair.rule.Elevated ? " (elevated)" : ""),
                string.Join(", ", pair.rule.Paths.Concat(pair.rule.Names)),
                pair.rule.Source,
            })),
        });
    }

    // ---- /memory ----

    private static async ValueTask<CommandOutput> MemoryAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        if (context.Runtime.Memory?.Writer is not { } writer)
            return CommandOutput.Warning(context.Runtime.Memory is null
                ? "Memory is off (Corvid:Memory:Enabled)."
                : "Memory writes need the extractor slot; bind one to stage and review writes.");
        switch (invocation.Arguments)
        {
            case [] or ["pending"]:
                var pending = writer.ListPending();
                if (pending.Count == 0)
                    return CommandOutput.Text("Nothing is waiting for review.");
                return new CommandOutput
                {
                    Title = "Staged memory writes",
                    Lines = ["Without a calibrated decider every write is staged here (plan §9.2). /memory approve <id> or /memory reject <id>."],
                    Table = CommandTable.Of(["id", "kind", "proposal", "why staged"], pending.Select(write => new[]
                    {
                        write.PendingId,
                        write.Kind == PendingKind.Block ? "block " + write.Proposal.Block : "fact",
                        (write.Proposal.Operation is { } operation ? operation + ": " : "") + (write.Proposal.Text ?? write.Proposal.Content ?? ""),
                        write.Reason,
                    })),
                };
            case ["approve", var id, .. var rest]:
                var operation = rest.FirstOrDefault();
                if (operation is not null && operation is not (MemoryOperations.Add or MemoryOperations.Update or MemoryOperations.Invalidate or MemoryOperations.Noop))
                    return CommandOutput.Error("The operation is add, update, invalidate or noop.");
                var result = await writer.ApproveAsync(id, operation, cancellationToken: cancellationToken).ConfigureAwait(false);
                return CommandOutput.Success($"Approved {id}: {result.Operation ?? "written"} {result.MemoryId}.");
            case ["reject", var id]:
                writer.Reject(id, cancellationToken: cancellationToken);
                return CommandOutput.Success($"Rejected {id}.");
            default:
                return CommandOutput.Error("Usage: /memory [pending] | approve <id> [operation] | reject <id>.");
        }
    }

    // ---- /tools, /todos ----

    private static ValueTask<CommandOutput> Tools(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        var tools = context.Runtime.Tools.List();
        if (tools.Count == 0)
            return ValueTask.FromResult(CommandOutput.Text("No tools are registered. Turns are answered without tools."));
        return ValueTask.FromResult(new CommandOutput
        {
            Title = "Tools",
            Table = CommandTable.Of(["tool", "risk", "description"], tools.Select(tool => new[] { tool.Name, Risk(tool.Risk), tool.Description })),
        });
    }

    private static string Risk(ToolRisk risk)
    {
        var parts = new List<string> { risk.ReadOnly ? "read-only" : "side effects" };
        if (risk.Network)
            parts.Add("network");
        if (risk.Destructive)
            parts.Add("destructive");
        return string.Join(", ", parts);
    }

    private static ValueTask<CommandOutput> Todos(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        var todos = context.Session.Snapshot().Todos;
        return ValueTask.FromResult(todos.IsEmpty
            ? CommandOutput.Text("No todos.")
            : CommandOutput.Text([.. todos.Select(todo => $"[{(todo.Completed ? "x" : " ")}] {todo.Text}")]));
    }
}
