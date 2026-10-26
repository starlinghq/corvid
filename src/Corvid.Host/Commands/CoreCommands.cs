using System.Collections.Immutable;
using System.Globalization;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Kernel.Checkpoints;
using Corvid.Fabric;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Memory;
using Corvid.Models;
using Corvid.Mcp;
using Corvid.Skills;
using Corvid.Tools;

namespace Corvid.Host.Commands;

/// <summary>
/// The v1 core set (plan §17 row 1.3): <c>/model</c>, <c>/status</c>, <c>/goal</c>, <c>/approvals</c>, <c>/memory</c>,
/// <c>/undo</c> and <c>/rollback</c> (C-014), <c>/skills</c> (C-017), <c>/mcp</c> (C-018), <c>/compact</c> and <c>/cron</c> (C-035),
/// <c>/help</c>, <c>/exit</c>, and commands that map directly onto existing APIs: <c>/tools</c>, <c>/todos</c> and <c>/cancel</c>.
/// </summary>
public static class CoreCommands
{
    public static SlashCommandRegistry Register(SlashCommandRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry
            .Add(new SlashCommand("help", "List the commands.", "", Help))
            .Add(new SlashCommand("model", "Pick a model: lists a slot's models numbered, or switches one for this session.",
                ModelUsage, ModelAsync) { Aliases = ["models"], DuringTurn = true })
            .Add(new SlashCommand("status", "Slots with their device, queue depth and median latency; the session's state.", "", Status))
            .Add(new SlashCommand("goal", "Show, set or clear the standing goal.", "[text | clear]", Goal))
            .Add(new SlashCommand("approvals", "Pending approvals and the remembered approval rules.", "", Approvals))
            .Add(new SlashCommand("memory", "Review staged memory writes and this project's lessons; import Hermes memories for review.",
                MemoryUsage, MemoryAsync))
            .Add(new SlashCommand("skills", "Saved skills: list, review drafts, history, import, evals.", SkillCommands.Usage, SkillCommands.RunAsync))
            .Add(new SlashCommand("tools", "The tools the model can be offered, with their risk.", "", Tools))
            .Add(new SlashCommand("mcp", "Configured MCP servers: state, trust and the tools each one added.", McpCommands.Usage, McpCommands.RunAsync))
            .Add(new SlashCommand("todos", "The session's todo list.", "", Todos))
            .Add(new SlashCommand("undo", "Revert the last turn's file changes and take it out of the conversation.", "", UndoAsync))
            .Add(new SlashCommand("compact", "Compact the conversation before the latest turn now, keeping what you name in full.",
                "[<what to keep>]", CompactAsync))
            .Add(new SlashCommand("cron", "Scheduled jobs: list, run history, pause, resume or remove one.", CronCommands.Usage, CronCommands.RunAsync))
            .Add(new SlashCommand("agents", "This session's child sessions: list them, show one's answer, or stop one.", AgentCommands.AgentsUsage, AgentCommands.AgentsAsync))
            .Add(new SlashCommand("delegate", "Start a child session on a goal, with the tools, model mix and budget you give.", AgentCommands.DelegateUsage,
                AgentCommands.DelegateAsync))
            .Add(new SlashCommand("rollback", "List checkpoints, or go back to before the Nth latest one (files and conversation).",
                "[N]", RollbackAsync))
            .Add(new SlashCommand("cancel", "Cancel the running turn.", "", Cancel))
            .Add(new SlashCommand("sessions", "Recent conversations with their titles, pinned first.", "", SessionCommands.Sessions))
            .Add(new SlashCommand("search", "Search what was said across sessions, with each match's session and turn.", SessionCommands.SearchUsage,
                SessionCommands.SearchAsync))
            .Add(new SlashCommand("rename", "Show or change this session's title.", "[<title>]", SessionCommands.Rename))
            .Add(new SlashCommand("recap", "A few lines on what happened in this session since its last recap, by the local generator.", "",
                SessionCommands.RecapAsync))
            .Add(new SlashCommand("pin", "Keep this session at the top of the app's rail and of /sessions.", "[off]", SessionCommands.Pin))
            .Add(new SlashCommand("fork", "Fork this session into a new one; during a turn, at its last completed step.", ForkCommand.Usage,
                ForkCommand.RunAsync) { DuringTurn = true })
            .Add(new SlashCommand("workspace", "Where this session works; start a session in another directory or a new git worktree.",
                WorkspaceCommands.Usage, WorkspaceCommands.RunAsync))
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

    internal const string ModelUsage = "[slot] [<model> | <number>] [--pin | --default] | unpin <slot> | reset <slot>";

    private static async ValueTask<CommandOutput> ModelAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        var arguments = invocation.Arguments.Where(argument => argument is not ("--pin" or "--default")).ToArray();
        var pin = invocation.Arguments.Contains("--pin");
        // A5: "default for new sessions" is a separate choice from this session's (corvid.json, as `corvid model switch` writes it).
        var asDefault = invocation.Arguments.Contains("--default");
        if (pin && asDefault)
            return CommandOutput.Error("--pin is for this session and --default for new sessions: pick one.");
        if (arguments.Length == 0)
            return ModelPicker(context, ModelSlots.Generator);
        if (arguments is ["reset", var reset])
            return await ResetModelAsync(context, reset, cancellationToken).ConfigureAwait(false);
        if (arguments is ["unpin", var unpinned])
        {
            ModelSlots.ContractOf(unpinned);
            await context.Session.Slots.UnpinAsync(unpinned, cancellationToken).ConfigureAwait(false);
            // A pin only stops de-escalation (plan §6, C-006): escalation and fallback move a pinned slot too.
            return CommandOutput.Success($"{unpinned} is unpinned: when a turn ends it returns to this session's choice again, wherever escalation took it.");
        }

        string slot, name;
        if (arguments is [var only])
        {
            // A slot alone lists that slot's models; a number picks from the generator's list.
            if (ModelSlots.All.Contains(only, StringComparer.Ordinal))
                return ModelPicker(context, only);
            if (IsPickNumber(only))
            {
                slot = ModelSlots.Generator;
                name = PickModel(context, slot, only);
            }
            else
            {
                // A model alone switches the generator when it can generate, else the slot it serves.
                var model = context.Runtime.Catalogue.Find(only);
                slot = model.Serves(ModelSlots.Generator) ? ModelSlots.Generator : model.Slots[0];
                name = model.Name;
            }
        }
        else if (arguments is [var slotName, var modelName])
        {
            ModelSlots.ContractOf(slotName);
            slot = slotName;
            name = IsPickNumber(modelName) ? PickModel(context, slot, modelName) : context.Runtime.Catalogue.Find(modelName).Name;
        }
        else
        {
            return CommandOutput.Error($"Usage: /model {ModelUsage}. /model alone lists the models to pick from.");
        }

        if (slot == ModelSlots.TextEmbedder && !context.Runtime.IsConfigured(slot, name))
            return CommandOutput.Error($"Memory embeds only with the embedder.text models configured at start. Add {name} to " +
                "Corvid:Models:Slots:embedder.text:Fallbacks, then restart.");
        if (asDefault)
            return DefaultModel(context, slot, name);
        var availability = context.Runtime.Catalogue.Check(name);
        if (!availability.IsReady)
            return CommandOutput.Error(availability.Detail + (availability.Acquire is { } acquire ? $" Run `{acquire}`." : ""));

        // A5: a turn running now takes the switch at its next step, once its issued tool calls finish.
        var running = context.Runtime.IsTurnRunning(context.Session.Id);
        // A5: the switch is this session's choice until changed (de-escalation returns to it); the decider too, per session.
        // `corvid model switch` changes the default for new sessions.
        var result = await context.Session.SetModelAsync(slot, name, pin ? SwitchScope.Pin : SwitchScope.Session, cancellationToken).ConfigureAwait(false);
        var carried = result.PendingApprovals.IsEmpty ? "" : $" {result.PendingApprovals.Length} pending approval(s) carry over.";
        var target = result.To?.Id ?? name;
        var when = running ? " The running turn takes it at its next step." : "";
        return result.Status switch
        {
            SwitchStatus.Switched => CommandOutput.Success($"{slot}: {result.From.Id} → {target}{(pin ? " (pinned)" : "")} for this session.{when}{carried}"),
            SwitchStatus.Unchanged when result.SessionDefaultChanged => CommandOutput.Success($"{slot} already uses {target}; it is this session's choice now."),
            SwitchStatus.Unchanged => CommandOutput.Text($"{slot} already uses {target}."),
            SwitchStatus.Refused => CommandOutput.Error($"{slot}: the privacy gate refused {target}: {result.Detail}"),
            _ => CommandOutput.Error($"{slot}: nothing to switch to ({result.Detail})."),
        };
    }

    /// <summary>A5: <c>/model [slot] &lt;model&gt; --default</c>: the default for new sessions, written to corvid.json; this session stays.</summary>
    private static CommandOutput DefaultModel(SlashCommandContext context, string slot, string name)
    {
        if (context.Runtime.ConfigWriter is null)
            return CommandOutput.Error("This Corvid has no configuration file to write a default to; use `corvid model switch`.");
        var change = context.Runtime.WriteDefaultModel(slot, name);
        return change.Write.Written ? CommandOutput.Success(change.Describe()) : CommandOutput.Error(change.Describe());
    }

    /// <summary>A5: <c>/model reset &lt;slot&gt;</c>: forgets this session's choice, so the slot follows the default again.</summary>
    private static async Task<CommandOutput> ResetModelAsync(SlashCommandContext context, string slot, CancellationToken cancellationToken)
    {
        ModelSlots.ContractOf(slot);
        var result = await context.Session.ClearModelChoiceAsync(slot, cancellationToken).ConfigureAwait(false);
        if (result is null)
            return CommandOutput.Text($"{slot}: this session made no choice; it follows the default.");
        return result.Status == SwitchStatus.Switched
            ? CommandOutput.Success($"{slot}: back to the default, {result.To?.Id ?? "none"}, for this session.")
            : CommandOutput.Success($"{slot}: this session's choice is forgotten; it returns to the default, {result.To?.Id ?? "none"}, when the slot moves back.");
    }

    /// <summary>
    /// A5: the decider this session's checkpoints run on (per session now): the runtime's state when it is the shared one,
    /// else the session's own with the default for new sessions beside it.
    /// </summary>
    private static string SessionDecider(SlashCommandContext context)
    {
        var deciders = context.Runtime.Deciders;
        if (ReferenceEquals(deciders.For(context.Session.Slots), deciders.Shared))
            return context.Runtime.DeciderState;
        var name = deciders.DeciderName(context.Session.Slots) is { } named ? TerminalText.Visible(named) : "degraded tier";
        return $"{name} for this session (new sessions: {context.Runtime.DeciderState})";
    }

    private static bool IsPickNumber(string argument) => argument.Length is > 0 and < 4 && argument.All(char.IsAsciiDigit);

    /// <summary>The model a number names in a slot's list. The list is ordered by name, so the numbers are stable.</summary>
    private static string PickModel(SlashCommandContext context, string slot, string number)
    {
        var models = context.Runtime.Catalogue.ForSlot(slot);
        if (models.Count == 0)
            throw new ArgumentException($"No model can serve {slot}.");
        var pick = int.Parse(number, NumberStyles.None, CultureInfo.InvariantCulture);
        if (pick < 1 || pick > models.Count)
            throw new ArgumentException($"{slot} has {models.Count} model(s), numbered 1 to {models.Count}; /model {slot} lists them.");
        return models[pick - 1].Name;
    }

    /// <summary>
    /// One slot's models, numbered, with the current one marked and whether each can be used now, and the other slots on a
    /// line under it. Each model a switch would accept is a choice, so the chat can ask for a number.
    /// </summary>
    private static CommandOutput ModelPicker(SlashCommandContext context, string slot)
    {
        var runtime = context.Runtime;
        var slots = context.Session.Slots;
        var bound = slots.TryCurrent(slot, out var current);
        var configured = runtime.Registry.TryGet(slot, out var configuration);
        var models = runtime.Catalogue.ForSlot(slot);

        var rows = new List<string[]>();
        var choices = ImmutableArray.CreateBuilder<CommandChoice>();
        for (var i = 0; i < models.Count; i++)
        {
            var model = models[i];
            var isCurrent = bound && Names(current!.Profile.Id, model);
            var availability = runtime.Catalogue.Check(model.Name);
            var status = isCurrent ? "● current" + (slots.IsPinned(slot) ? " (pinned)" : "")
                : availability.IsReady ? "ready"
                : availability.Readiness.ToString().ToLowerInvariant() + (availability.Acquire is { } acquire ? $": {acquire}" : "");
            // Memory embeds only with the models configured at start; another one needs a configuration change and a restart.
            var restart = slot == ModelSlots.TextEmbedder && !runtime.IsConfigured(slot, model.Name);
            var role = restart ? "needs restart"
                : !configured ? ""
                : Names(configuration!.Default.Profile.Id, model) ? "default"
                : configuration.Escalation.Any(item => Names(item.Profile.Id, model)) ? "escalation" : "";
            rows.Add([(i + 1).ToString(CultureInfo.InvariantCulture), model.Name, model.Profile.Privacy.ToString().ToLowerInvariant(), status, role]);
            // A5: the decider is switchable per session too.
            if (!isCurrent && !restart && availability.IsReady)
                choices.Add(new CommandChoice(rows[^1][0], model.Name, slot == ModelSlots.Generator ? $"/model {i + 1}" : $"/model {slot} {i + 1}"));
        }

        var others = ModelSlots.All.Where(other => other != slot)
            .Select(other => slots.TryCurrent(other, out var binding) ? $"{other} {binding.Profile.Id}"
                : other == ModelSlots.Decider ? $"{other} degraded tier" : null)
            .OfType<string>();
        var prefix = slot == ModelSlots.Generator ? "/model" : $"/model {slot}";
        string[] lines = models.Count == 0 ? [$"No model can serve {slot}."]
            : slot == ModelSlots.Decider ? [$"decider: {SessionDecider(context)}.",
                $"Switch this session's decider with {prefix} <number> or {prefix} <name>; add --default for new sessions instead. /model reset {slot} forgets this session's choice."]
            : [$"Switch this session with {prefix} <number> or {prefix} <name> (it lasts until you change it); add --pin to keep the slot where it is when a turn ends (escalation still moves it). " +
                $"Add --default to make it the default for new sessions instead; /model reset {slot} forgets this session's choice."];
        return new CommandOutput
        {
            Title = $"{slot}: {(bound ? current!.Profile.Id : slot == ModelSlots.Decider ? "degraded tier" : "unbound")}",
            Table = models.Count == 0 ? null : CommandTable.Of(["#", "model", "runs", "status", "role"], rows),
            Lines = [.. lines, "Other slots: " + string.Join(" · ", others) + ". /model <slot> lists one."],
            Choices = choices.ToImmutable(),
            ChoicePrompt = $"Switch {slot} to",
        };

        // A binding's id carries its device (qwen3.5-0.8b@cuda0), which can differ from the catalogue's after a fallback.
        static bool Names(string id, ModelDescriptor model) =>
            string.Equals(id, model.Profile.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(id, model.Name, StringComparison.OrdinalIgnoreCase)
            || id.StartsWith(model.Name + "@", StringComparison.OrdinalIgnoreCase);
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
        // Kev's second tier, when configured, answers only gates below their floor (§5.5).
        if (runtime.Registry.TryGet(ModelSlots.Decider, out var kev))
            foreach (var second in kev.Escalation)
                rows.Add(Row(ModelSlots.Decider, second.Profile.Id + " (second tier)", second.Profile.Placement,
                    runtime.Metrics.TryGet(ModelSlots.Decider, second.Profile.Id, out var tier) ? tier : null));
        // The degraded tier: the generator's and embedder's yes/no and similarity answers at fabric priority. Without Kev it is
        // the decider; with Kev, the fallback for states over the hard stop and requests Kev refuses.
        if (runtime.Registry.TryGet(ModelSlots.Generator, out var generator))
            rows.Add(Row(ModelSlots.Decider, DegradedDecider.DeciderName + (kev is null ? "" : " (fallback)"), generator.Default.Profile.Placement,
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
                $"workspace: {TerminalText.Visible(session.Workspace.Root)}",
                $"project: {(session.Workspace.Project is { } project ? $"{project.Id[..12]} ({project.Source})" : "none (lessons off)")}",
                $"decider: {SessionDecider(context)}",
                $"scheduler: {runtime.SchedulerState}",
                $"skills: {(runtime.Skills.Store is { } skills ? $"{skills.Skills.Count} in {skills.Root}" : runtime.Skills.State)}" +
                (runtime.Skills.Database?.Pending() is { Count: > 0 } drafts ? $", {drafts.Count} draft(s) to review" : ""),
                $"privacy egress: cloud {runtime.Options.Privacy.Cloud.ToString().ToLowerInvariant()}" +
                (runtime.Egress.HasExtractor ? "" : ", no extractor bound: every cloud request is refused"),
                .. runtime.UnavailableModels.Select(item => $"unavailable: {item.Detail}"),
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

    private const string MemoryUsage = "[pending] | approve <id> [operation] | reject <id> | import hermes [<hermes home>] [--dry-run]"
        + " | lessons [pending | approve <id> [supersedes <old>] | reject <id> [reason] | supersede <old> <new>]";

    private static async ValueTask<CommandOutput> MemoryAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        // Lessons (C-025) need no extractor, so they are reviewed whether or not facts can be written.
        if (invocation.Arguments is ["lessons", .. var lessonArguments])
            return Lessons(context, [.. lessonArguments], cancellationToken);
        if (context.Runtime.Memory?.Writer is not { } writer)
            return CommandOutput.Warning(context.Runtime.Memory is null
                ? "Memory is off (Corvid:Memory:Enabled)."
                : "Memory writes need the extractor slot; bind one to stage and review writes.");
        switch (invocation.Arguments)
        {
            case [] or ["pending"]:
                var pending = writer.ListPending();
                var lessonCount = LessonCandidates(context.Runtime, context.Session);
                var lessonLine = lessonCount > 0 ? $"{lessonCount} lesson candidate(s): /memory lessons pending" : null;
                if (pending.Count == 0)
                    return CommandOutput.Text(lessonLine is null ? "Nothing is waiting for review." : "No memory write is waiting for review. " + lessonLine + ".");
                return new CommandOutput
                {
                    Title = "Staged memory writes",
                    Lines = ["Without a calibrated decider every write is staged here (plan §9.2). /memory approve <id> or /memory reject <id>.",
                        .. lessonLine is null ? Array.Empty<string>() : [lessonLine]],
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
            case ["import", "hermes", .. var rest] when rest.Count(argument => argument != "--dry-run") <= 1:
                return await ImportHermesAsync(context, writer, rest, cancellationToken).ConfigureAwait(false);
            default:
                return CommandOutput.Error("Usage: /memory " + MemoryUsage + ".");
        }
    }

    // ---- /memory lessons (C-025) ----

    private const string LessonsUsage =
        "Usage: /memory lessons [pending] | approve <id> [supersedes <old>] | reject <id> [reason] | supersede <old> <new>.";

    private static int LessonCandidates(CorvidRuntime runtime, CorvidSession session) =>
        runtime.Memory?.Lessons is { } lessons && session.Workspace.Project is { } project ? lessons.Pending(project.Id).Count : 0;

    private static CommandOutput Lessons(SlashCommandContext context, string[] arguments, CancellationToken cancellationToken)
    {
        if (context.Runtime.Memory?.Lessons is not { } lessons || context.Session.Workspace.Project is not { } project)
            return CommandOutput.Warning(context.Runtime.Memory is null
                ? "Memory is off (Corvid:Memory:Enabled)."
                : "Lessons are off (Corvid:Memory:Lessons:Enabled).");
        try
        {
            switch (arguments)
            {
                case [] or ["pending"]:
                    var pending = lessons.Pending(project.Id);
                    var approved = lessons.Store.List(project.Id, LessonStates.Approved).Count;
                    var summary = $"Project {project.Id[..12]} ({project.Source}): {approved} approved lesson(s).";
                    if (pending.Count == 0)
                        return CommandOutput.Text(summary + " No lesson is waiting for review.");
                    return new CommandOutput
                    {
                        Title = "Lesson candidates",
                        Lines = [summary, "Without a calibrated decider every lesson waits here. /memory lessons approve <id> or reject <id> [reason]."],
                        Table = CommandTable.Of(["id", "kind", "lesson", "evidence", "why waiting"], pending.Select(lesson => new[]
                        {
                            ShortId(lesson.LessonId),
                            lesson.Kind,
                            $"{lesson.Title} — {lesson.Applicability}: {lesson.Body.ReplaceLineEndings(" / ")}",
                            string.Join("; ", lesson.Evidence.Select(e => $"{e.SessionId} events {string.Join(',', e.Positions)}")),
                            lesson.ReviewReason ?? "",
                        })),
                    };
                case ["approve", var id]:
                    return Approved(lessons.Approve(Find(lessons, project, id), cancellationToken: cancellationToken), null);
                case ["approve", var id, "supersedes", var old]:
                    var replaced = Find(lessons, project, old);
                    return Approved(lessons.Approve(Find(lessons, project, id), replaced, cancellationToken), replaced);
                case ["reject", var id, .. var reason]:
                    var rejected = lessons.Reject(Find(lessons, project, id), reason.Length == 0 ? "rejected by the user" : string.Join(' ', reason),
                        cancellationToken);
                    return CommandOutput.Success($"Rejected {ShortId(rejected.LessonId)}: {rejected.Title}.");
                case ["supersede", var old, var replacement]:
                    var oldId = Find(lessons, project, old);
                    var newId = Find(lessons, project, replacement);
                    lessons.Supersede(oldId, newId, context.Session.Id);
                    return CommandOutput.Success($"{ShortId(oldId)} is superseded by {ShortId(newId)}.");
                default:
                    return CommandOutput.Error(LessonsUsage);
            }
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            return CommandOutput.Error(exception.Message);
        }

        static CommandOutput Approved(Lesson lesson, string? superseded) =>
            CommandOutput.Success($"Approved {ShortId(lesson.LessonId)}: {lesson.Title}." + (superseded is null ? "" : $" It supersedes {ShortId(superseded)}."));
    }

    /// <summary>The first 12 hex characters after <c>lesson:</c>, which <see cref="Find"/> accepts back.</summary>
    private static string ShortId(string lessonId) => lessonId.StartsWith("lesson:", StringComparison.Ordinal) ? lessonId[7..19] : lessonId;

    /// <summary>A lesson of the current project by its full id or a unique prefix of its hash.</summary>
    private static string Find(LessonWriter lessons, ProjectIdentity project, string id)
    {
        var prefix = id.StartsWith("lesson:", StringComparison.Ordinal) ? id : "lesson:" + id;
        var matches = lessons.Store.List(project.Id).Where(lesson => lesson.LessonId.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        return matches.Count switch
        {
            1 => matches[0].LessonId,
            0 => throw new KeyNotFoundException($"No lesson '{id}' in this project."),
            _ => throw new InvalidOperationException($"'{id}' matches {matches.Count} lessons; give more of the id."),
        };
    }

    /// <summary>
    /// <c>/memory import hermes</c> (D-9, C-027): each entry of Hermes' <c>MEMORY.md</c> and <c>USER.md</c> becomes one staged,
    /// local-only fact proposal, never a write. An entry the skill scanner flags is refused, as Hermes blocks a threat pattern
    /// at load; one the extractor refuses (too long) is reported, never cut; one already pending or already a current fact is
    /// skipped, so importing again stages nothing new.
    /// </summary>
    private static async Task<CommandOutput> ImportHermesAsync(SlashCommandContext context, MemoryWriter writer,
        ImmutableArray<string> arguments, CancellationToken cancellationToken)
    {
        var dryRun = arguments.Contains("--dry-run");
        var home = arguments.FirstOrDefault(argument => argument != "--dry-run");
        var location = HermesHome.Resolve(CorvidPaths.Home, home is null ? null : CorvidPaths.ExpandHome(home));
        if (location.Home is not { } hermesHome)
            return CommandOutput.Warning(location.Problem + " Usage: /memory import hermes <hermes home>.");
        var directory = Path.Combine(hermesHome, "memories");
        if (!Directory.Exists(directory))
            return CommandOutput.Warning($"There is no Hermes memories directory at {directory}.");

        var lines = new List<string>();
        var rows = new List<string[]>();
        int staged = 0, problems = 0;
        var scan = Knowledge.HermesMemoryImport.Scan(directory);
        foreach (var file in scan.Files)
        {
            if (file.Problem is { } problem)
            {
                lines.Add($"refused {file.File}: {problem}; nothing from it was imported.");
                problems++;
                rows.Add([file.File, "-", "-", "-", "-", "-"]);
                continue;
            }
            int offered = 0, duplicates = 0, refused = 0, flagged = 0;
            foreach (var entry in scan.Entries.Where(entry => entry.File == file.File))
            {
                if (entry.Flagged)
                {
                    flagged++;
                    lines.Add($"flagged {file.File} entry {entry.Index}: {string.Join(", ", entry.Findings.Select(finding => finding.Rule).Distinct())}; not imported.");
                    continue;
                }
                if (dryRun)
                {
                    offered++;
                    continue;
                }
                try
                {
                    var result = await Knowledge.HermesMemoryImport.StageAsync(writer, context.Session.Id, entry, cancellationToken)
                        .ConfigureAwait(false);
                    if (result.Status == MemoryImportStatus.Staged)
                        offered++;
                    else
                        duplicates++;
                }
                catch (ArgumentException exception)
                {
                    refused++;
                    lines.Add($"refused {file.File} entry {entry.Index}: {exception.Message}");
                }
            }
            staged += offered;
            problems += refused + flagged;
            rows.Add([file.File, file.Exists ? file.Entries.Length.ToString(CultureInfo.InvariantCulture) : "missing",
                offered.ToString(CultureInfo.InvariantCulture), duplicates.ToString(CultureInfo.InvariantCulture),
                refused.ToString(CultureInfo.InvariantCulture), flagged.ToString(CultureInfo.InvariantCulture)]);
            if (file.Blocked > 0)
                lines.Add($"skipped {file.Blocked} entry(ies) of {file.File} shaped like Hermes' [BLOCKED: …] placeholder.");
        }
        lines.Insert(0, dryRun
            ? $"Dry run: {staged} entry(ies) would be offered for review; entries already pending or known are found on import."
            : staged == 0 ? "Nothing new to review."
            : $"Staged {staged} local-only memory(ies) for review: /memory pending, then /memory approve <id> or /memory reject <id>.");
        return new CommandOutput
        {
            Title = $"Hermes memories from {directory}" + (location.Profile is { } profile ? $" (profile {profile})" : ""),
            Lines = [.. lines],
            Table = CommandTable.Of(["file", "entries", dryRun ? "would stage" : "staged", "duplicates", "refused", "flagged"], rows),
            Tone = problems > 0 ? OutputTone.Warning : OutputTone.Success,
        };
    }

    // ---- /compact (C-035) ----

    /// <summary>The longest focus <c>/compact</c> takes (C-041); every map chunk repeats it, so it costs summarizer room.</summary>
    public const int MaxCompactFocusChars = 500;

    /// <summary>
    /// Compacts everything before the latest turn now, by the turn loop's own rule and budget; the user acting directly, so
    /// there is no approval step. The rest of the line, when there is one, is what the summary must keep (C-041). The output
    /// ends with the rendered history's tokens before and after, on the generator's counter.
    /// </summary>
    private static async ValueTask<CommandOutput> CompactAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        var focus = invocation.Text.Trim();
        if (focus.Length > MaxCompactFocusChars)
            return CommandOutput.Error(string.Create(CultureInfo.InvariantCulture,
                $"Usage: /compact [<what to keep>], at most {MaxCompactFocusChars} characters (this one has {focus.Length})."));
        var result = await context.Session.CompactAsync(focus.Length == 0 ? null : focus, cancellationToken).ConfigureAwait(false);
        switch (result.Status)
        {
            case Kernel.Context.CompactionStatus.Compacted:
                var lines = new List<string> { result.Describe() + "." };
                if (focus.Length > 0)
                    lines.Add("kept in full: " + focus);
                if (result.Record?.Goal is { Length: > 0 } goal)
                    lines.Add("goal: " + goal);
                if (result.Record?.NextStep is { Length: > 0 } next)
                    lines.Add("next step: " + next);
                if (Figures(result) is { } figures)
                    lines.Add(figures);
                return new CommandOutput { Lines = [.. lines], Tone = OutputTone.Success };
            case Kernel.Context.CompactionStatus.NothingToCompact:
                return new CommandOutput
                {
                    Lines = Figures(result) is { } history
                        ? ["Nothing before the latest turn needs compacting.", history]
                        : ["Nothing before the latest turn needs compacting."],
                };
            default:
                return CommandOutput.Warning(result.Describe() + ".");
        }
    }

    // History only: the system prompt, the offered tools and the memory and skill notes are chosen per turn.
    private const string FiguresNote = " tokens (generator's counter; system prompt, tools and notes not counted).";

    internal static string? Figures(Kernel.Context.CompactionResult result) => (result.PromptTokensBefore, result.PromptTokensAfter) switch
    {
        ({ } before, { } after) => string.Create(CultureInfo.InvariantCulture, $"history {before:N0} → {after:N0}") + FiguresNote,
        ({ } before, null) => string.Create(CultureInfo.InvariantCulture, $"history {before:N0}") + FiguresNote,
        _ => null,
    };

    // ---- /undo, /rollback (C-014) ----

    private static async ValueTask<CommandOutput> UndoAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        if (!invocation.Arguments.IsEmpty)
            return CommandOutput.Error("Usage: /undo.");
        var result = await context.Session.UndoAsync(cancellationToken).ConfigureAwait(false);
        return result is null ? CommandOutput.Text("Nothing to undo: the conversation has no turn left.") : Restored(context, result, "Undid the last turn");
    }

    private static async ValueTask<CommandOutput> RollbackAsync(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        var ledger = context.Session.Checkpoints();
        switch (invocation.Arguments)
        {
            case []:
                if (ledger.IsEmpty)
                    return CommandOutput.Text(context.Session.Workspace.Checkpoints?.Store.UnavailableReason is { } reason
                        ? $"No checkpoints: they are unavailable ({reason})."
                        : "No checkpoints in this conversation yet: one is taken before each tool call that changes files.");
                return new CommandOutput
                {
                    Title = "Checkpoints",
                    Lines = ["/rollback N puts the files back as they were before checkpoint N (and every later one) and takes those calls " +
                        "out of the conversation. /undo does the same for the last turn."],
                    Table = CommandTable.Of(["#", "call", "tool", "changed"], ledger.Reverse().Select((entry, index) => new[]
                    {
                        (index + 1).ToString(CultureInfo.InvariantCulture), entry.CallId, entry.Tool,
                        entry.After is null ? "not recorded" : entry.Paths.IsEmpty ? "nothing" : string.Join(", ", entry.Paths.Take(5))
                            + (entry.Paths.Length > 5 ? $" and {entry.Paths.Length - 5} more" : ""),
                    })),
                };
            case [var text] when int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1:
                var result = await context.Session.RollbackAsync(number, cancellationToken).ConfigureAwait(false);
                return result is null
                    ? CommandOutput.Error($"There {(ledger.Length == 1 ? "is 1 checkpoint" : $"are {ledger.Length} checkpoints")}; /rollback lists them.")
                    : Restored(context, result, $"Rolled back to before checkpoint {number}");
            default:
                return CommandOutput.Error("Usage: /rollback [N], where N counts from 1, the latest checkpoint.");
        }
    }

    private static CommandOutput Restored(SlashCommandContext context, RestoreResult result, string done)
    {
        if (result.Status == RestoreStatus.Refused)
            return new CommandOutput
            {
                Tone = OutputTone.Error,
                Lines = [$"Nothing was changed: {result.Detail}."],
                Table = result.Conflicts.IsEmpty ? null
                    : CommandTable.Of(["path", "why it was kept"], result.Conflicts.Select(conflict => new[] { conflict.Path, conflict.Reason })),
            };
        var lines = new List<string>
        {
            result.Restored.IsEmpty
                ? $"{done}. No files needed putting back."
                : $"{done}. Put back {result.Restored.Length} file(s): {string.Join(", ", result.Restored)}.",
        };
        if (!result.Merged.IsEmpty)
            lines.Add($"Kept your own edits in {string.Join(", ", result.Merged)} (merged).");
        var last = context.Session.Snapshot().Transcript.LastOrDefault(entry => entry.Content is UserMessage)?.Content as UserMessage;
        lines.Add(last is null ? "The conversation is back at its start." : $"The conversation is back to after \"{Short(last.Text)}\".");
        if (result.CheckpointId is { } id)
            lines.Add($"The workspace as it was before this is checkpoint {id}.");
        return new CommandOutput { Tone = OutputTone.Success, Lines = [.. lines] };

        static string Short(string text) => text.Length > 60 ? text[..60] + "…" : text;
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
