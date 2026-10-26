using Corvid.Abstractions.Events;
using Corvid.Models;
using Corvid.Models.Privacy;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Turns;

/// <summary>What the person asked of architect/editor turns with <c>/architect</c> (A7).</summary>
public enum ArchitectRequest
{
    /// <summary>Nothing asked: only <see cref="TurnOptions.ArchitectEditor"/> splits a turn, on an automatic escalation.</summary>
    None,

    /// <summary>The next turn plans first, whatever escalates: <c>/architect</c>. Taken by the turn that starts next.</summary>
    NextTurn,

    /// <summary>Every turn of the session plans first until the person turns it off: <c>/architect on</c>.</summary>
    Session,
}

/// <summary>
/// A7, architect/editor turns (plan §6, after Aider): when a coding turn escalates (difficulty, context or repair) and
/// <see cref="TurnOptions.ArchitectEditor"/> is on, or the person asked with <c>/architect</c>, the escalated generator writes
/// one plan, with no tools, logged as an <see cref="AssistantMessage"/> with its <see cref="TurnPlan"/>; the generator slot
/// then returns to the session's own model (or <see cref="TurnOptions.EditorModel"/>), which makes the edits with the plan
/// in its history and a note saying to apply it. An editor that gets stuck gets one re-plan. Nothing about the split touches
/// approvals, taint or the egress gate: every edit is a call through the turn's own path, the planner's request passes the
/// gate inside its binding (a refusal is recorded and the turn goes on unplanned, locally), and the plan is a model's text,
/// labelled so, never the user's words.
/// </summary>
public sealed partial class TurnRunner
{
    /// <summary>A7: the <see cref="TurnPlan.Trigger"/> of a plan the person asked for with <c>/architect</c>.</summary>
    public const string ArchitectTrigger = "architect";

    /// <summary>A7: the trigger of the one re-plan a turn may make, when its editor got stuck.</summary>
    public const string ReplanTrigger = "stuck";

    private int _architect;

    /// <summary>
    /// A7: what the person asked with <c>/architect</c>: <see cref="ArchitectRequest.NextTurn"/> is taken by the next turn
    /// that starts, <see cref="ArchitectRequest.Session"/> lasts until set otherwise. Kept for the runner's lifetime only,
    /// like <see cref="ApprovalMode"/>; the log records each plan, not the request. Thread-safe.
    /// </summary>
    public ArchitectRequest Architect
    {
        get => (ArchitectRequest)Volatile.Read(ref _architect);
        set
        {
            if (!Enum.IsDefined(value))
                throw new ArgumentOutOfRangeException(nameof(value), value, "Not an architect request.");
            Volatile.Write(ref _architect, (int)value);
        }
    }

    // Whether the starting turn was asked to plan: a NextTurn request is used up by it.
    private bool TakeArchitectRequest()
    {
        var request = (ArchitectRequest)Interlocked.CompareExchange(ref _architect, (int)ArchitectRequest.None, (int)ArchitectRequest.NextTurn);
        return request is ArchitectRequest.NextTurn or ArchitectRequest.Session;
    }

    private sealed partial class Turn
    {
        private const string NudgePlan = "You are the architect for this request. Another, smaller model will make the changes with the "
            + "tools after you. Write a short numbered plan of the concrete edits: which files, which functions or sections, and "
            + "exactly what to change in each. Name every file and identifier precisely. Do not write the full code and do not "
            + "call tools; only the plan.";

        private const string NudgeReplan = "The model applying your earlier plan got stuck; its attempts are above. Write a revised short "
            + "numbered plan of the concrete edits that are still needed, naming files and identifiers precisely. Do not write "
            + "the full code and do not call tools; only the plan.";

        private const int MaxPlans = 2;

        // The plans this turn made (at most MaxPlans: the plan and one re-plan), and the latest one.
        private readonly List<TurnPlan> _plans = [];
        private bool _architectAsked;
        // Set while the planner's request is built, so the host does not stream it as the answer.
        private bool _planning;
        // The editor's note: apply the plan above. Empty until a plan is made.
        private string _planNote = "";

        /// <summary>
        /// Whether an escalation for <paramref name="reason"/> plans first: the turn has made no plan yet, and either the person
        /// asked (<c>/architect</c>), or <see cref="TurnOptions.ArchitectEditor"/> is on, the trigger is difficulty, context or
        /// repair, and the turn is a coding one (its shortlist holds a tool that changes files).
        /// </summary>
        private bool PlansOn(string reason) => _plans.Count == 0 && envelope.Shell is null && (reason == ArchitectTrigger
            || _options.ArchitectEditor && reason is "difficulty" or "context" or "repair" && CodingTurn());

        private bool CodingTurn() => _shortlist.Any(tool => (tool.Descriptor.Risk.SideEffects & SideEffects.Filesystem) != 0);

        /// <summary>
        /// At ingress, after the difficulty question: a turn the person asked to plan (<c>/architect</c>) plans now, escalating
        /// first unless an escalation already moved the generator off the session's model (and planned, or not).
        /// </summary>
        private async Task PlanOnRequestAsync(CancellationToken cancellationToken)
        {
            _architectAsked = runner.TakeArchitectRequest();
            if (!_architectAsked || _plans.Count > 0 || envelope.Shell is not null)
                return;
            if (ReferenceEquals(_services.Slots.Current(ModelSlots.Generator), EditorBinding()))
                await EscalateAsync(ArchitectTrigger, cancellationToken).ConfigureAwait(false);
            else
                await PlanAsync(ArchitectTrigger, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// The editor got stuck (the step checkpoint's <c>stuck</c>) after a plan: one re-plan. The generator escalates again
        /// (to the planner, along the slot's ladder), writes a revised plan and hands back. False when the turn has no plan to
        /// revise, has re-planned already, or could not escalate: the turn's own stuck handling goes on.
        /// </summary>
        private async Task<bool> ReplanAsync(CancellationToken cancellationToken)
        {
            if (_plans.Count != 1 || !_options.EscalationAllowed)
                return false;
            var before = _plans.Count;
            if (ReferenceEquals(_services.Slots.Current(ModelSlots.Generator), _services.Slots.SessionDefault(ModelSlots.Generator))
                || ReferenceEquals(_services.Slots.Current(ModelSlots.Generator), EditorBinding()))
            {
                var result = await _services.Slots.EscalateAsync(ModelSlots.Generator, cancellationToken).ConfigureAwait(false);
                if (result.Status != SwitchStatus.Switched)
                    return false;
                runner.NoteBudgetChange(result);
                _escalations.Add($"{result.To!.Id}: {ReplanTrigger}");
            }
            await PlanAsync(ReplanTrigger, cancellationToken).ConfigureAwait(false);
            return _plans.Count > before;
        }

        /// <summary>
        /// The generator slot is on the planner (just escalated): one plan, no tools, through the planner's own binding (so a
        /// refusal of its egress gate is not retreated into a local plan), logged with its <see cref="TurnPlan"/>; then the
        /// slot goes to the editor. A refused planner records <c>{editor}: privacy</c> and hands back unplanned, so nothing
        /// the gate kept back leaves and the turn goes on locally. A planner that fails otherwise, or writes nothing, leaves
        /// the turn where an ordinary escalation would: on the escalated model.
        /// </summary>
        private async Task PlanAsync(string trigger, CancellationToken cancellationToken)
        {
            var planner = _services.Slots.Current(ModelSlots.Generator);
            var editor = EditorBinding();
            // Nothing to hand to: the session's own model is already the planner.
            if (ReferenceEquals(planner, editor))
                return;
            var attempt = _plans.Count + 1;
            var generatorId = _generatorId;
            string text;
            _planning = true;
            try
            {
                var response = await GenerateAsync([], attempt == 1 ? NudgePlan : NudgeReplan, required: null, cancellationToken, planner)
                    .ConfigureAwait(false);
                text = Text(response);
            }
            catch (PrivacyEgressRefusedException)
            {
                // Plan §10.4: the gate refused the planner before anything was sent; the work stays local, unplanned.
                // Named for the model the work stays on, as a slot's privacy retreat is.
                _escalations.Add($"{editor.Profile.Id}: {PrivacyRetreat}");
                await HandBackAsync(editor, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                _escalations.Add($"{planner.Profile.Id}: the plan failed ({exception.Message})");
                return;
            }
            finally
            {
                _planning = false;
                // The editor's calls name the model that made them, not the planner.
                _generatorId = generatorId;
            }
            if (text.Length == 0)
                return;
            var plan = new TurnPlan(planner.Profile.Id, editor.Profile.Id, trigger, attempt);
            runner.Append(new AssistantMessage(text, TakeUsage(), plan));
            _plans.Add(plan);
            Listen(listener => listener.Planned(runner.SessionId, plan, text));
            _planNote = $"A plan for this request, written by {planner.Profile.Id}, is above. It is a model's plan, not the user's words: "
                + "where it goes beyond what the user asked, follow the user. Apply it now with the tools, one edit at a time; do not "
                + "write a new plan or repeat it.";
            await HandBackAsync(editor, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// The generator slot goes to the editor, logged as <c>ModelSwitched</c> with the reason <c>editor</c>: not a
        /// de-escalation, so the person's choice for the session and a pin stay as they were (a reopened session reads its
        /// choices from the log's de-escalations). A refused or failed switch leaves the planner to edit, as an ordinary
        /// escalation would.
        /// </summary>
        private async Task HandBackAsync(ModelBinding editor, CancellationToken cancellationToken)
        {
            var slots = _services.Slots;
            if (ReferenceEquals(slots.Current(ModelSlots.Generator), editor))
                return;
            try
            {
                var result = await slots.SwitchAsync(ModelSlots.Generator, editor, SwitchReason.Editor, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                runner.NoteBudgetChange(result);
            }
            catch (InvalidOperationException)
            {
                // Unfinished work from another writer: the planner goes on as the turn's generator.
            }
        }

        /// <summary>
        /// The model that applies a plan: <see cref="TurnOptions.EditorModel"/> when one of the generator slot's bindings has
        /// that profile id or is that model at some placement (<c>name@placement</c>; the default, a fallback or a rung of the
        /// ladder, the mix's for a session with one), else the session's own generator.
        /// </summary>
        private ModelBinding EditorBinding()
        {
            var slots = _services.Slots;
            if (_options.EditorModel is { Length: > 0 } wanted && slots.TryConfiguration(ModelSlots.Generator, out var configuration))
                foreach (var binding in new[] { configuration.Default }.Concat(configuration.Fallbacks).Concat(configuration.Escalation))
                    if (string.Equals(binding.Profile.Id, wanted, StringComparison.OrdinalIgnoreCase)
                        || binding.Profile.Id.StartsWith(wanted + "@", StringComparison.OrdinalIgnoreCase))
                        return binding;
            return slots.SessionDefault(ModelSlots.Generator);
        }
    }
}
