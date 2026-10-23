using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Extraction;
using Corvid.Abstractions.Fabric;
using Corvid.Abstractions.Sessions;
using Corvid.Abstractions.Tokens;
using Corvid.Approvals;
using Corvid.Fabric;
using Corvid.Kernel.Checkpoints;
using Corvid.Kernel.Context;
using Corvid.Kernel.Events;
using Corvid.Memory;
using Corvid.Models;
using Corvid.Models.Privacy;
using Corvid.Secrets;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Turns;

/// <summary>
/// One session's turn loop (plan §5.2, §7, §8, §10.3). A turn appends the user message; runs the ingress checkpoint (one
/// decider request: intent, difficulty, needs_recall, clarify and tool over the embedding shortlist); calls the generator
/// with one tool schema on a confident pick or the shortlist otherwise; validates each call against its schema and its
/// binding (extracted slots are hints beside a failed argument), repairing once and escalating on a second failure; sends
/// side-effecting calls through the approval pipeline (the pre-execute checkpoint); runs a step checkpoint after each round of results (done, stuck,
/// tool, clear_results, injection); and at egress asks remember (starting the memory write path) and deliver. Before every
/// generator call the prompt is fitted to the generator's budget with the levers of plan §8 in cost order: clearing results,
/// dropping completed skill bodies, then compaction; after a switch to a larger window, compacted ranges are expanded.
/// </summary>
/// <remarks>
/// Not thread-safe: the session actor runs one turn at a time. Every append re-reads the head, because the fabric, the
/// approval pipeline and the memory writer append to the same log. Taint persists across turns until a user message.
/// </remarks>
public sealed class TurnRunner
{
    private const int MaxAppendAttempts = 8;
    private const string None = "none";
    private const string NudgeRequired = "Call the {0} tool now with the arguments the request gives. Do not describe a result before the tool has run.";
    private const string NudgeClarify = "The request is ambiguous. Ask the user one short question that would let you act. Do not call tools.";
    private const string NudgeFinish = "Answer the user now from the results above. Do not call tools.";
    /// <summary>The reason on a <see cref="TurnResult.Escalations"/> entry when the egress gate kept the turn local.</summary>
    public const string PrivacyRetreat = "privacy";

    private const string NudgeBudget = "The step budget for this turn is used up. Answer the user now with what you have. Do not call tools.";

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly TurnServices _services;
    // The project this runner last bound or found bound (C-025), so a turn reads the projection for it only when it changes.
    private string? _boundProject;
    private readonly TurnOptions _options;
    private readonly ContextCompactor _compactor;
    // C-022: the MCP tool descriptions injection was asked about this session, by origin and the chunk's text (C-033: every
    // MCP origin, trusted ones too, one chunk of whole lines per question).
    private readonly HashSet<string> _describedOrigins = new(StringComparer.Ordinal);
    // C-033: a description chunk's lines (whole "- name: description" lines) up to this many characters per question.
    private const int DescriptionBudget = 1_200;
    // C-033: description questions per ingress batch for trusted text, so the batch stays within S1's measured 8-question bound
    // (340 ms on GPU); chunks with an outside description are asked regardless, ahead of these (review of #47, finding 1).
    private const int MaxDescriptionQuestions = 2;
    private const string McpOrigin = "mcp:";
    // Set when the generator moved to a larger window (plan §4.2 step 4); the next generator call expands compacted ranges.
    private int _expansionDue;

    public TurnRunner(string sessionId, TurnServices services)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(services);
        if (services.Slots.SessionId != sessionId)
            throw new ArgumentException("The slots belong to another session.", nameof(services));
        SessionId = sessionId;
        _services = services;
        _options = services.Options.Validate();
        _compactor = new ContextCompactor(services.Log, services.Slots, _options.Compaction, _options.ContextReserve, _options.EscalationAllowed);
    }

    /// <summary>
    /// Acts on a switch's budget direction (C-006's hook reports it; plan §4.2 step 4): a generator moved to a larger window
    /// expands compacted ranges before its next call, where the budget allows; a smaller one needs nothing here, because every
    /// call is fitted to the current generator's budget and compacts when it does not fit. Thread-safe: a <c>/model</c>
    /// switch arrives beside a running turn.
    /// </summary>
    public void NoteBudgetChange(SwitchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.Status != SwitchStatus.Switched || result.Slot != ModelSlots.Generator)
            return;
        if (result.Budget == BudgetAction.ExpandCompactedRanges)
            Volatile.Write(ref _expansionDue, 1);
        else if (result.Budget == BudgetAction.CompactBeforeNextCall)
            Volatile.Write(ref _expansionDue, 0);
    }

    public string SessionId { get; }

    /// <summary>
    /// <c>/compact</c> (C-035): compacts now what a turn would compact first when its prompt did not fit, by the loop's own
    /// rule (<see cref="ContextCompactor.Ranges"/> with the latest <c>UserMessage</c> as the turn's start): everything before
    /// the latest turn's message, and only when it holds something a summary would shorten. The latest turn stays, so
    /// <c>/undo</c> still finds it. The record budget is the turn loop's, a quarter of the generator's prompt budget. An
    /// expansion pending from an earlier switch to a larger window is dropped, so the next call does not undo an explicit
    /// compaction; a later switch to a larger window expands it again by design (plan §4.2 step 4). Nothing compacted (unfit,
    /// cancelled) keeps the pending expansion.
    /// </summary>
    /// <remarks>Not thread-safe, like <see cref="RunAsync"/>: run it on the session actor between turns.</remarks>
    /// <returns>The compaction's result, or <see cref="CompactionStatus.NothingToCompact"/> when there is no earlier turn.</returns>
    public Task<CompactionResult> CompactNowAsync(CancellationToken cancellationToken = default) => CompactNowAsync(null, cancellationToken);

    /// <summary>
    /// <c>/compact &lt;focus&gt;</c> (C-041): as <see cref="CompactNowAsync(CancellationToken)"/>, with what the user asked the
    /// summary to keep passed to every map chunk (<see cref="ContextCompactor"/>) and recorded on the <c>Compacted</c> event.
    /// The focus is one-shot: later automatic compactions do not reuse it. The result carries the rendered history's tokens
    /// before (always) and after (when the compaction was written), on the generator's counter
    /// (<see cref="CompactionResult.PromptTokensBefore"/>).
    /// </summary>
    public async Task<CompactionResult> CompactNowAsync(string? focus, CancellationToken cancellationToken = default)
    {
        var snapshot = Snapshot();
        var generator = _services.Slots.GetChatClient(ModelSlots.Generator);
        var counter = Counter(generator);
        var before = HistoryTokens(snapshot, counter);
        var latest = snapshot.Transcript.LastOrDefault(entry => entry.Content is UserMessage)?.FromSequence;
        if (latest is not { } turnStart || ContextCompactor.Ranges(snapshot, turnStart, keepFrom: null) is not [var range, ..])
            return new CompactionResult(CompactionStatus.NothingToCompact, 1, Math.Max(0, (latest ?? 1) - 1)) { PromptTokensBefore = before };

        var profile = _services.Slots.Current(ModelSlots.Generator).Profile;
        var budget = profile.AcceptedContext - _options.MaxOutputTokens - _options.ContextReserve;
        // The summarizer may be a cloud binding: its gate and Vault need the session and its taint, as in a turn.
        using var scope = new ModelRequestContext(SessionId, Tainted).Enter();
        var result = await _compactor.CompactAsync(range, counter, Math.Max(1, budget / 4), focus, cancellationToken).ConfigureAwait(false);
        // Only a compaction that was written drops a pending expansion: an unfit summary or a cancelled command compacted
        // nothing, so the next call still expands for the larger window (review of PR #46, finding 1). The record is appended
        // after any switch that arrived meanwhile, so the explicit compaction is the later act and wins.
        if (result.Status == CompactionStatus.Compacted)
        {
            Volatile.Write(ref _expansionDue, 0);
            return result with { PromptTokensBefore = before, PromptTokensAfter = HistoryTokens(Snapshot(), counter) };
        }
        return result with { PromptTokensBefore = before };
    }

    // The history as the generator would read it, with no system prompt and no budget pressure: nothing is stubbed beyond
    // the log's own clear_results decisions.
    private static int HistoryTokens(SessionSnapshot snapshot, ITokenCounter counter) =>
        PromptRenderer.Render(snapshot, "", counter, int.MaxValue).Tokens;

    private static ITokenCounter Counter(IChatClient generator) =>
        generator.GetService(typeof(ITokenCounter)) as ITokenCounter ?? CharsPerTokenCounter.Instance;

    /// <summary>
    /// The session's context holds content from outside the user (§10.3): a network tool's result, or an envelope from
    /// someone else. Only an envelope from the user clears it.
    /// </summary>
    public bool Tainted { get; private set; }

    public async Task<TurnResult> RunAsync(Envelope envelope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (envelope.SessionId != SessionId)
            throw new ArgumentException("The envelope is for another session.", nameof(envelope));
        var turn = new Turn(this, envelope);
        using var activity = KernelTelemetry.Source.StartActivity(ActivityKind.Internal, tags:
        [
            new(KernelTelemetry.OperationName, "invoke_agent"),
            new(KernelTelemetry.AgentNameTag, KernelTelemetry.AgentName),
            new(KernelTelemetry.ConversationId, SessionId),
            new("corvid.surface", envelope.Surface),
        ], name: KernelTelemetry.InvokeAgentSpan);
        try
        {
            var result = await turn.RunAsync(cancellationToken).ConfigureAwait(false);
            Describe(activity, result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            turn.CloseOpenCalls("cancelled: the turn was cancelled before the call finished");
            await DeEscalateAsync(CancellationToken.None).ConfigureAwait(false);
            var result = turn.Result(TurnStatus.Cancelled, null);
            Describe(activity, result);
            return result;
        }
        catch (Exception exception)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            // A prompter, decider or log failure: an issued call left without a result would make every later switch
            // refuse it as orphaned, so it is closed before the failure reaches the actor.
            try
            {
                turn.CloseOpenCalls("failed: " + exception.Message);
            }
            catch
            {
                // The original failure is the one to report; a log that cannot take the result fails again on its own.
            }
            await DeEscalateAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static void Describe(Activity? activity, TurnResult result)
    {
        if (activity is null)
            return;
        activity.SetTag("corvid.turn.status", StatusName(result.Status));
        activity.SetTag("corvid.turn.steps", result.Steps);
        activity.SetTag("corvid.turn.tool_calls", result.ToolCalls);
        activity.SetTag("corvid.tainted", result.Tainted);
        if (!result.Escalations.IsEmpty)
            activity.SetTag("corvid.turn.escalations", result.Escalations.ToArray());
        if (result.Status == TurnStatus.Failed)
            activity.SetStatus(ActivityStatusCode.Error, result.Text);
    }

    /// <summary>The status as spans and surfaces show it.</summary>
    public static string StatusName(TurnStatus status) => status switch
    {
        TurnStatus.Answered => "answered",
        TurnStatus.Clarifying => "clarifying",
        TurnStatus.Stuck => "stuck",
        TurnStatus.StepBudgetExhausted => "step_budget_exhausted",
        TurnStatus.Failed => "failed",
        TurnStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private async Task DeEscalateAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var result in await _services.Slots.CompleteTurnAsync(cancellationToken).ConfigureAwait(false))
                NoteBudgetChange(result);
        }
        catch (InvalidOperationException)
        {
            // A slot the registry no longer binds, or unfinished calls from another writer: the next turn retries.
        }
    }

    private StoredEvent Append(SessionEvent payload) => Append([payload])[0];

    /// <summary>Appends events as one batch, so no other writer (or a concurrent switch's read) sees only part of them.</summary>
    private IReadOnlyList<StoredEvent> Append(params SessionEvent[] payloads)
    {
        var events = payloads.Select(EventData.Create).ToArray();
        for (var attempt = 1; ; attempt++)
        {
            // Never across an await: other components append between this turn's own events.
            var head = _services.Log.GetHead(SessionId);
            try
            {
                return _services.Log.Append(SessionId, head, events);
            }
            catch (EventLogConflictException) when (attempt < MaxAppendAttempts)
            {
            }
        }
    }

    private SessionSnapshot Snapshot() => SessionProjection.Load(_services.Log, SessionId);

    private sealed record Offered(AIFunction Function, ToolDescriptor Descriptor);

    /// <param name="Ask">Goes through the pre-execute checkpoint: a side effect, or a network read in a tainted session.</param>
    /// <param name="Spans">Extracted spans the arguments did not match: hints if the tool's binding refuses one of those arguments.</param>
    private sealed record ValidCall(string CallId, Offered Tool, JsonElement Arguments, bool Ask, IReadOnlyList<SlotDisagreement> Spans);

    private enum CallOutcome
    {
        Succeeded,
        Failed,
        BindingFailed,
        Denied,
    }

    /// <summary>One turn's state. Lives only while the turn runs.</summary>
    private sealed class Turn(TurnRunner runner, Envelope envelope)
    {
        private readonly TurnServices _services = runner._services;
        private readonly TurnOptions _options = runner._options;
        private readonly Lock _gate = new();
        private readonly HashSet<string> _open = new(StringComparer.Ordinal);
        private readonly List<string> _escalations = [];
        private readonly List<SlotDisagreement> _disagreements = [];
        // The tool decision that picked a tool, by tool name, so its call's result becomes the decision's outcome.
        private readonly Dictionary<string, string> _pickDecisions = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Offered> _catalogue = new(StringComparer.Ordinal);
        // C-033: the tools each description question of this turn carried, by question key, so a yes withholds exactly those.
        private readonly Dictionary<string, (string Origin, ImmutableArray<string> Tools, string Marker)> _described = new(StringComparer.Ordinal);
        // The calls of the latest generator response: budget clearing spares their results, and injection is asked about them.
        private readonly HashSet<string> _lastRound = new(StringComparer.Ordinal);
        private ImmutableSortedDictionary<string, ImmutableArray<string>> _entities = ImmutableSortedDictionary<string, ImmutableArray<string>>.Empty;
        private ImmutableArray<Offered> _shortlist = [];
        private ImmutableArray<RetrievedItem> _recalled = [];
        private ImmutableArray<SkillOption> _skills = [];
        private string _skillNote = "";
        private string? _skill;
        private bool _skillQueued;
        private bool _lessonQueued;
        // Whether any prompt of the turn held local-only memory (§10.4): a lesson drafted from the turn is local-only too.
        private bool _heldLocalOnly;
        private string? _lastToolResult;
        private int _steps;
        private int _toolCalls;
        private bool _prefillChecked;
        private string? _memoryWrite;
        private readonly List<string> _compactions = [];
        // The turn's own UserMessage: compaction during the turn never summarizes it.
        private long _turnStart;

        public async Task<TurnResult> RunAsync(CancellationToken cancellationToken)
        {
            // §10.3: only the user's own message clears taint; anyone else's text is outside content.
            runner.Tainted = envelope.Origin != EnvelopeOrigin.User;
            // C-025: the session records its project before the message, when it has none yet or another one.
            if (_services.Project is { } project && runner._boundProject != project.Id)
            {
                _turnStart = runner.Snapshot().ProjectId == project.Id
                    ? runner.Append(new UserMessage(envelope.Text)).Sequence
                    : runner.Append(new ProjectBound(project.Id, project.Source), new UserMessage(envelope.Text))[^1].Sequence;
                runner._boundProject = project.Id;
            }
            else
            {
                _turnStart = runner.Append(new UserMessage(envelope.Text)).Sequence;
            }

            await ExtractEntitiesAsync(cancellationToken).ConfigureAwait(false);
            await BuildShortlistAsync(cancellationToken).ConfigureAwait(false);
            await BuildSkillShortlistAsync(cancellationToken).ConfigureAwait(false);

            // Ingress: one request for every question this turn needs before the first generator call.
            var ingress = new List<KeyValuePair<string, DecisionQuestion>> { Ask("intent"), Ask("difficulty"), Ask("clarify") };
            // A guest's turn (C-020) recalls none of the user's memory: what it retrieved would be read out to the guest.
            if (_services.Retriever is not null && envelope.Guest is null)
                ingress.Add(Ask("needs_recall"));
            if (!_shortlist.IsEmpty)
                ingress.Add(ToolQuestion());
            if (!_skills.IsEmpty)
                ingress.Add(SkillQuestion());
            ingress.AddRange(DescriptionQuestions());
            var verdicts = await DecideAsync(Checkpoint.Ingress, ingress, cancellationToken).ConfigureAwait(false);
            // C-033: before anything reads the shortlist, so no generator call of this turn sees a withheld description.
            WithholdDescriptions(verdicts);
            await LoadSkillAsync(verdicts, cancellationToken).ConfigureAwait(false);

            if (Acted(verdicts, "difficulty", out _) && _options.EscalationAllowed)
                await EscalateAsync("difficulty", cancellationToken).ConfigureAwait(false);
            if (verdicts.Verdicts.ContainsKey("needs_recall"))
                await RecallAsync(Acted(verdicts, "needs_recall", out _), cancellationToken).ConfigureAwait(false);

            // An embedding pick of ask_clarifying_question is no confidence either: like clarify itself (BlocksTurn), only a
            // calibrated answer diverts the turn unless the floors allow the degraded tier to.
            var clarify = Acted(verdicts, "clarify", out _)
                || Acted(verdicts, "intent", out var intent) && intent == "ask_clarifying_question"
                    && (verdicts["intent"].Answer?.Calibrated == true || _services.Fabric.Policy.Floors.DegradedTurnBlocking);
            if (clarify)
            {
                var question = Text(await GenerateAsync([], NudgeClarify, required: null, cancellationToken).ConfigureAwait(false));
                return await FinishAsync(TurnStatus.Clarifying, question.Length > 0 ? question : "Could you say more about what you need?",
                    cancellationToken).ConfigureAwait(false);
            }

            var offered = OfferFrom(verdicts, "tool");
            if (offered is null && Acted(verdicts, "intent", out var route) && route == "answer_directly")
                offered = [];
            return await LoopAsync(offered ?? _shortlist, cancellationToken).ConfigureAwait(false);
        }

        private async Task<TurnResult> LoopAsync(ImmutableArray<Offered> offered, CancellationToken cancellationToken)
        {
            var failures = 0;
            var noOpChecked = false;
            string? required = null;
            var finishing = false;
            var status = TurnStatus.Answered;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var budgetSpent = _steps >= _options.MaxSteps;
                var tools = finishing || budgetSpent ? [] : offered;
                var nudge = budgetSpent && !finishing ? NudgeBudget : finishing ? NudgeFinish : null;
                if (budgetSpent && status == TurnStatus.Answered)
                    status = TurnStatus.StepBudgetExhausted;

                ChatResponse response;
                try
                {
                    response = await GenerateAsync(tools, nudge, required, cancellationToken).ConfigureAwait(false);
                }
                catch (ContextOverBudgetException exception)
                {
                    if (await EscalateAsync("context", cancellationToken).ConfigureAwait(false))
                        continue;
                    return await FinishAsync(TurnStatus.Failed,
                        $"This conversation no longer fits the model's context ({exception.Message}).", cancellationToken).ConfigureAwait(false);
                }
                required = null;

                var calls = tools.IsEmpty ? [] : response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().ToList();
                if (calls.Count == 0)
                {
                    // No-op detection (§7 step 6): the generator narrated instead of calling. If done is low and tool is high,
                    // re-prompt once requiring that tool; the narration is not logged.
                    if (!tools.IsEmpty && _toolCalls == 0 && !noOpChecked)
                    {
                        noOpChecked = true;
                        var check = new List<KeyValuePair<string, DecisionQuestion>> { Ask("done") };
                        if (!_shortlist.IsEmpty)
                            check.Add(ToolQuestion());
                        var verdicts = await DecideAsync(Checkpoint.Step, check, cancellationToken).ConfigureAwait(false);
                        if (!Acted(verdicts, "done", out _) && OfferFrom(verdicts, "tool") is [var pick])
                        {
                            offered = [pick];
                            required = pick.Function.Name;
                            continue;
                        }
                    }
                    var text = Text(response);
                    return await FinishAsync(status, text.Length > 0 ? text : "I have nothing to add.", cancellationToken).ConfigureAwait(false);
                }

                _steps++;
                var (anyRan, failed) = await RunCallsAsync(calls, tools, cancellationToken).ConfigureAwait(false);
                if (failed)
                {
                    // Repair once (§7 step 5): the error results carry the expected shape. A second failure escalates (§6).
                    if (++failures >= 2)
                    {
                        if (!await EscalateAsync("repair", cancellationToken).ConfigureAwait(false))
                            return await FinishAsync(TurnStatus.Failed,
                                "I could not produce a valid tool call for this request, and no larger model is available.", cancellationToken)
                                .ConfigureAwait(false);
                        failures = 0;
                    }
                }
                if (!anyRan)
                    continue;

                var step = await StepAsync(cancellationToken).ConfigureAwait(false);
                if (Acted(step, "stuck", out _))
                {
                    if (!_options.EscalationAllowed || !await EscalateAsync("stuck", cancellationToken).ConfigureAwait(false))
                    {
                        finishing = true;
                        status = TurnStatus.Stuck;
                    }
                }
                if (Acted(step, "done", out _))
                    finishing = true;
                offered = OfferFrom(step, "tool") ?? _shortlist;
            }
        }

        // ---- generator ----

        private async Task<ChatResponse> GenerateAsync(ImmutableArray<Offered> tools, string? nudge, string? required,
            CancellationToken cancellationToken)
        {
            var generator = _services.Slots.GetChatClient(ModelSlots.Generator);
            var profile = _services.Slots.Current(ModelSlots.Generator).Profile;
            var counter = Counter(generator);
            var budget = profile.AcceptedContext - _options.MaxOutputTokens - _options.ContextReserve;
            var toolTokens = tools.Sum(tool => counter.CountTokens(tool.Function.Name) + counter.CountTokens(tool.Function.Description)
                + counter.CountTokens(tool.Function.JsonSchema.GetRawText()));
            // Prompt caching (plan §8): a model that caches on breakpoints keeps a frozen system prompt, so this turn's recalled
            // memory and skill go after the history as a one-shot note instead of into the system prompt.
            var notes = string.Join("\n\n", new[] { MemoryNote(profile), _skillNote }.Where(part => part.Length > 0));
            var frozen = profile.Caching == PromptCachingStyle.Breakpoints;
            var noteTokens = frozen && notes.Length > 0 ? counter.CountTokens(notes) : 0;
            var system = SystemPrompt(profile, frozen ? "" : notes);
            var rendered = await FitAsync((snapshot, clearMore) =>
                PromptRenderer.Render(snapshot, system, counter, budget, _lastRound, toolTokens + noteTokens, clearMore,
                    // A guest (C-020) sees what was said in the chat before its turn, never the user's tool results there.
                    envelope.Guest is null ? null : _turnStart), profile, counter,
                budget, cancellationToken).ConfigureAwait(false);
            if (rendered.OverBudget)
                throw new ContextOverBudgetException($"{rendered.Tokens} tokens against a budget of {rendered.Budget}");
            // C-022 (§10.3): a description from outside (an untrusted MCP server's) shown to the generator taints the turn
            // like an untrusted result, before the request's context is built so the egress gate and Vault see it.
            if (tools.Any(tool => tool.Descriptor.Risk.OutsideDescription))
                lock (_gate)
                    runner.Tainted = true;

            var messages = rendered.Messages.ToList();
            if (noteTokens > 0)
                messages.Add(new ChatMessage(ChatRole.User, notes));
            // The session and its taint travel with the request: a cloud binding's egress gate keeps placeholders stable per
            // session and is stricter for outside content, and the Vault handler tells Vault (plan §10.3, §10.4). A prompt
            // rendered with local-only memory says so, so a fallback to a cloud model refuses it rather than sending it.
            var holdsLocalOnly = HoldsLocalOnly(profile);
            _heldLocalOnly |= holdsLocalOnly;
            var options = new ModelRequestContext(runner.SessionId, runner.Tainted, holdsLocalOnly)
                .Apply(new ChatOptions { MaxOutputTokens = _options.MaxOutputTokens });
            if (!tools.IsEmpty)
            {
                options.Tools = [.. tools.Select(tool => (AITool)tool.Function)];
                options.ToolMode = ChatToolMode.Auto;
            }
            if (nudge is not null)
                messages.Add(new ChatMessage(ChatRole.User, nudge));

            var head = _services.Log.GetHead(runner.SessionId, cancellationToken);
            if (required is not null)
            {
                // Native tool calling can be forced. Template-rendered clients (Starling's Qwen) reject required modes, so
                // they get an instruction instead.
                if (profile.ToolCalls == ToolCallSupport.Native)
                {
                    var forced = options.Clone();
                    forced.ToolMode = ChatToolMode.RequireSpecific(required);
                    try
                    {
                        var answer = await generator.GetResponseAsync(messages, forced, cancellationToken).ConfigureAwait(false);
                        NotePrivacyRetreat(head, cancellationToken);
                        return answer;
                    }
                    catch (NotSupportedException)
                    {
                    }
                }
                messages.Add(new ChatMessage(ChatRole.User, string.Format(CultureInfo.InvariantCulture, NudgeRequired, required)));
            }
            var response = await generator.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
            NotePrivacyRetreat(head, cancellationToken);
            return response;
        }

        // The egress gate refused the cloud model and the slot moved to a local one (plan §10.4); the user is told, as
        // escalation is (plan §6).
        private void NotePrivacyRetreat(long head, CancellationToken cancellationToken)
        {
            foreach (var item in _services.Log.Read(runner.SessionId, head, cancellationToken: cancellationToken))
                if (item.Payload is ModelSwitched { Reason: "privacy", Slot: ModelSlots.Generator } change)
                    _escalations.Add($"{ModelProfile.FromSnapshot(change.NewProfile).Id}: {PrivacyRetreat}");
        }

        /// <summary>
        /// Plan §8 in cost order. Lever 1 (clearing results) is in the renderer. When the prompt is still over the budget:
        /// lever 2 (completed skill bodies), then lever 3, compaction of the turn's earlier history and then of its earlier
        /// rounds. A range that cannot be compacted to a record that fits is kept, and more results are cleared instead.
        /// </summary>
        private async Task<RenderedPrompt> FitAsync(Func<SessionSnapshot, bool, RenderedPrompt> render, ModelProfile profile,
            ITokenCounter counter, int budget, CancellationToken cancellationToken)
        {
            // The summarizer may be a cloud binding: its gate and Vault need the session and its taint like the generator's call.
            using var scope = RequestScope();
            if (Interlocked.Exchange(ref runner._expansionDue, 0) == 1)
            {
                var limit = (int)Math.Min(budget, budget * _options.Compaction.ExpansionFill);
                var expanded = runner._compactor.Expand(snapshot => render(snapshot, false), limit, cancellationToken);
                if (expanded > 0)
                    _compactions.Add(string.Create(CultureInfo.InvariantCulture, $"expanded {expanded} compacted range(s) for {profile.Id}"));
            }

            var current = runner.Snapshot();
            var rendered = render(current, false);
            if (!rendered.OverBudget)
                return rendered;
            if (_services.SkillBodies is { } skills && await skills.DropCompletedAsync(runner.SessionId, cancellationToken).ConfigureAwait(false))
            {
                current = runner.Snapshot();
                rendered = render(current, false);
                if (!rendered.OverBudget)
                    return rendered;
            }

            // The record may take a quarter of the budget at most, so a compaction always leaves room for the turn itself.
            var recordTokens = Math.Max(1, budget / 4);
            foreach (var range in ContextCompactor.Ranges(current, _turnStart, KeepFrom(current)))
            {
                var result = await runner._compactor.CompactAsync(range, counter, recordTokens, cancellationToken).ConfigureAwait(false);
                _compactions.Add(result.Describe());
                if (result.Status != CompactionStatus.Compacted)
                    continue;
                rendered = render(runner.Snapshot(), false);
                if (!rendered.OverBudget)
                    return rendered;
            }
            return render(runner.Snapshot(), true);
        }

        /// <summary>The first event of the latest round of calls, which the generator is about to act on.</summary>
        private long? KeepFrom(SessionSnapshot snapshot)
        {
            var calls = snapshot.Transcript.Where(entry => entry.Content is ToolCallIssued call && _lastRound.Contains(call.CallId)).ToList();
            return calls.Count == 0 ? null : calls.Min(entry => entry.FromSequence);
        }

        // Local-only core blocks and facts (plan §10.4) reach local and local-server models only; a local server is the user's
        // own (C-015's configuration refuses a public endpoint for that class).
        private string SystemPrompt(ModelProfile profile, string note)
        {
            var prompt = _options.SystemPrompt;
            if (envelope.Guest is null && _services.CoreBlocks is { } blocks && blocks.Render(includeLocalOnly: profile.Privacy != PrivacyClass.Cloud) is { Length: > 0 } core)
                prompt += "\n\n" + core;
            if (note.Length > 0)
                prompt += "\n\n" + note;
            return prompt;
        }

        // Whether SystemPrompt and MemoryNote put local-only memory into this profile's prompt.
        private bool HoldsLocalOnly(ModelProfile profile) =>
            profile.Privacy != PrivacyClass.Cloud
            && (envelope.Guest is null && _services.CoreBlocks?.Blocks.Any(block => !block.IsEmpty && block.LocalOnly) == true
                || _recalled.Any(item => item.LocalOnly));

        // Fabric questions and memory writes reach the generator without the turn's options; the ambient context carries the
        // session and its taint to a cloud binding's gate and to Vault all the same.
        private IDisposable RequestScope() => new ModelRequestContext(runner.SessionId, runner.Tainted).Enter();

        private string MemoryNote(ModelProfile profile)
        {
            var items = profile.Privacy == PrivacyClass.Cloud ? _recalled.Where(item => !item.LocalOnly).ToList() : [.. _recalled];
            return items.Count == 0 ? "" : "Relevant memory:\n" + string.Join("\n", items.Select(item => "- " + item.Text));
        }

        private static string Text(ChatResponse response) => response.Text.Trim();

        // ---- tool calls ----

        private async Task<(bool AnyRan, bool Failed)> RunCallsAsync(List<FunctionCallContent> calls, ImmutableArray<Offered> tools,
            CancellationToken cancellationToken)
        {
            // Every id the log holds, including calls an undo took out of the transcript: ids must stay unique in the log.
            var issued = new HashSet<string>(runner.Snapshot().IssuedCallIds, StringComparer.Ordinal);
            var valid = new List<ValidCall>();
            var failed = false;
            _lastRound.Clear();
            foreach (var call in calls)
            {
                _toolCalls++;
                // Small models reuse ids ("call_0") across turns; the log needs them unique within the session.
                var callId = string.IsNullOrWhiteSpace(call.CallId) ? "call" : call.CallId;
                for (var n = 2; issued.Contains(callId); n++)
                    callId = $"{(string.IsNullOrWhiteSpace(call.CallId) ? "call" : call.CallId)}-{n}";
                issued.Add(callId);
                _lastRound.Add(callId);
                var arguments = JsonValues.Object(call.Arguments);

                // Nothing is logged until the call is judged: an issued call must never wait on an await without a lease
                // (tracked or parked), or a concurrent /model switch would find it orphaned and refuse.
                var tool = tools.FirstOrDefault(candidate => candidate.Function.Name == call.Name);
                var errors = tool is null
                    ? [$"'{call.Name}' is not one of the tools offered: {string.Join(", ", tools.Select(candidate => candidate.Function.Name))}"]
                    : ArgumentValidator.Validate(tool.Function.JsonSchema, arguments).ToList();
                // Slot pre-fill (§7 step 4): extracted spans are hints, never vetoes. A call whose arguments validate runs as the
                // generator wrote it; a span is shown only beside an argument that failed, here or in the tool's own binding.
                IReadOnlyList<SlotDisagreement> spans = [];
                if (tool is not null && !PrefillShown())
                    spans = await PrefillAsync(callId, tool, arguments, cancellationToken).ConfigureAwait(false);
                if (errors.Count > 0)
                {
                    failed = true;
                    var hints = tool is null ? []
                        : await HintsAsync(tool, arguments,
                            spans.Where(item => errors.Any(error => error.Contains($"'{item.Parameter}'", StringComparison.Ordinal))),
                            cancellationToken).ConfigureAwait(false);
                    // The call and its error result go in as one append: a concurrent switch never sees the call alone.
                    IssueRejected(callId, call.Name, arguments, tool?.Descriptor.Risk.ReadOnly,
                        Error(ToolErrorKinds.InvalidArguments, [.. errors, .. Describe(hints)], tool?.Function.JsonSchema, Extracted(hints)));
                    continue;
                }
                // §10.3: in a tainted session a network read asks too (C-007b forces the tainted_network_egress reason), or
                // injected text could carry the user's data out in a URL. Taint is judged as of the response that made the call.
                // C-023: an untainted network read goes through the pipeline too when detection rules have something to say
                // about it (a web_fetch after a credential read), with this response's earlier calls as if already logged.
                var risk = tool!.Descriptor.Risk;
                var ask = !risk.ReadOnly || risk.Network && (runner.Tainted
                    || _services.Approvals is { } approvals && approvals.Detects(runner.SessionId, callId,
                        new ToolAction(tool.Descriptor, arguments),
                        [.. valid.Select(earlier => new ToolCallIssued(earlier.CallId, earlier.Tool.Function.Name, earlier.Arguments))],
                        cancellationToken));
                valid.Add(new ValidCall(callId, tool, arguments, Ask: ask, spans));
            }

            // Calls that need no approval run in parallel; the rest one at a time, in the order the model gave (§3.3).
            var ran = false;
            for (var i = 0; i < valid.Count;)
            {
                if (!valid[i].Ask)
                {
                    var group = valid.Skip(i).TakeWhile(call => !call.Ask).ToList();
                    var outcomes = await Task.WhenAll(group.Select(call => ExecuteAsync(call, cancellationToken))).ConfigureAwait(false);
                    failed |= outcomes.Contains(CallOutcome.BindingFailed);
                    i += group.Count;
                }
                else
                {
                    failed |= await ExecuteAsync(valid[i], cancellationToken).ConfigureAwait(false) == CallOutcome.BindingFailed;
                    i++;
                }
                ran = true;
            }
            return (ran, failed);
        }

        private async Task<CallOutcome> ExecuteAsync(ValidCall call, CancellationToken cancellationToken)
        {
            // The span covers the approval wait too: that is where a side-effecting call spends its time.
            using var activity = StartToolActivity(call.CallId, call.Tool.Function.Name, call.Ask);
            try
            {
                var outcome = await ExecuteCoreAsync(call, cancellationToken).ConfigureAwait(false);
                activity?.SetTag(KernelTelemetry.ToolOutcome, outcome switch
                {
                    CallOutcome.Succeeded => "succeeded",
                    CallOutcome.BindingFailed => "invalid_arguments",
                    CallOutcome.Denied => "denied",
                    _ => "failed",
                });
                if (outcome != CallOutcome.Succeeded)
                    activity?.SetStatus(ActivityStatusCode.Error);
                return outcome;
            }
            catch (Exception exception)
            {
                activity?.SetTag(KernelTelemetry.ToolOutcome, exception is OperationCanceledException ? "cancelled" : "failed");
                activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
                throw;
            }
        }

        // Tags given at creation, so a listener's ActivityStarted (the CLI's tool panel) already sees them.
        private Activity? StartToolActivity(string callId, string tool, bool approval) =>
            KernelTelemetry.Source.StartActivity(ActivityKind.Internal, tags:
            [
                new(KernelTelemetry.OperationName, "execute_tool"),
                new(KernelTelemetry.ToolName, tool),
                new(KernelTelemetry.ToolCallId, callId),
                new(KernelTelemetry.ToolType, "function"),
                new(KernelTelemetry.ConversationId, runner.SessionId),
                new(KernelTelemetry.ToolApproval, approval),
            ], name: KernelTelemetry.ExecuteToolPrefix + tool);

        private async Task<CallOutcome> ExecuteCoreAsync(ValidCall call, CancellationToken cancellationToken)
        {
            var descriptor = call.Tool.Descriptor;
            IDisposable running;
            ApprovalResult? approval = null;
            CheckpointTaken? before = null;
            bool tainted;
            lock (_gate)
                tainted = runner.Tainted;
            // Each call takes its lease before its ToolCallIssued is appended, so a switch always finds it tracked or parked.
            if (!call.Ask)
            {
                running = _services.Slots.TrackToolCall(call.CallId);
                Issue(call, running);
            }
            else
            {
                // Parked, not running, while the approval is open: a /model switch neither waits for it nor calls it
                // orphaned. A denial's result is logged while still parked; an approval moves it to tracked in one step.
                using var parked = _services.Slots.ParkToolCall(call.CallId);
                Issue(call, parked);
                (var denial, approval) = await AuthorizeAsync(call, cancellationToken).ConfigureAwait(false);
                if (denial is not null)
                {
                    Result(call.CallId, Error(ToolErrorKinds.NotRun, [denial], null, null), isError: true);
                    return CallOutcome.Denied;
                }
                // Plan §15: a checkpoint before anything that changes files. The approval was given knowing whether the change
                // was reversible, so a store that works but cannot take this checkpoint stops the call (fail closed).
                if (_services.Checkpoints is { Store.IsAvailable: true } checkpoints
                    && CheckpointService.Covers(descriptor, approval!.Assessment.Analysis))
                {
                    try
                    {
                        var taken = await checkpoints.BeforeAsync(call.CallId, approval.Assessment.Analysis).ConfigureAwait(false);
                        lock (_gate)
                            runner.Append(taken);
                        before = taken;
                    }
                    catch (CheckpointException exception)
                    {
                        Result(call.CallId, Error(ToolErrorKinds.NotRun, ["no checkpoint could be taken before this change, so it was not made: " + exception.Message],
                            null, null), isError: true);
                        return CallOutcome.Denied;
                    }
                }
                running = _services.Slots.RunParkedToolCall(call.CallId);
            }

            // The ledger's after-state, recorded before the result whatever happened, so partial writes can be undone too.
            async Task RecordAfterAsync()
            {
                if (before is null)
                    return;
                try
                {
                    var after = await _services.Checkpoints!.AfterAsync(call.CallId, before, approval!.Assessment.Analysis).ConfigureAwait(false);
                    lock (_gate)
                        runner.Append(after);
                }
                catch (CheckpointException)
                {
                    // The ledger entry stays without its after-state; a restore across it is refused rather than guessed.
                }
            }

            CallOutcome outcome;
            ToolCallContext? callContext = null;
            using (running)
            {
                try
                {
                    // The turn's context travels beside the arguments, never in them: tools that need the session or the
                    // approval (the shell tool runs only with one) read it from there.
                    var context = callContext = new ToolCallContext(runner.SessionId, call.CallId)
                    {
                        Tainted = tainted,
                        Interactive = _options.Interactive,
                        Approval = approval,
                    };
                    var arguments = context.AttachTo(JsonValues.ToArguments(call.Arguments));
                    var value = await call.Tool.Function.InvokeAsync(arguments, cancellationToken).ConfigureAwait(false);
                    var result = JsonValues.ToElement(value);
                    await RecordAfterAsync().ConfigureAwait(false);
                    // The result is logged before the lease is released: a model switch refuses an issued call with no result.
                    // Kev's last tool result is what the log holds (C-040): the log scrubs the result, and this copy too.
                    Result(call.CallId, result, isError: false,
                        CredentialScrub.Scrub($"{descriptor.Name}: {JsonValues.Text(result)}", CredentialDetector.Egress).Text);
                    outcome = CallOutcome.Succeeded;
                }
                catch (ToolArgumentException exception)
                {
                    await RecordAfterAsync().ConfigureAwait(false);
                    // The binding names the argument it refused; a span for that argument is the hint (no name, no hint).
                    var hints = exception.ParamName is { } refused && !PrefillShown()
                        ? await HintsAsync(call.Tool, call.Arguments, call.Spans.Where(item => item.Parameter == refused),
                            cancellationToken).ConfigureAwait(false)
                        : [];
                    Result(call.CallId, Error(ToolErrorKinds.InvalidArguments, [exception.Message, .. Describe(hints)], call.Tool.Function.JsonSchema,
                        Extracted(hints)), isError: true, $"{descriptor.Name}: invalid arguments");
                    return CallOutcome.BindingFailed;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    await RecordAfterAsync().ConfigureAwait(false);
                    Result(call.CallId, Error(ToolErrorKinds.Cancelled, ["the turn was cancelled while the tool ran"], null, null), isError: true);
                    throw;
                }
                catch (Exception exception)
                {
                    await RecordAfterAsync().ConfigureAwait(false);
                    Result(call.CallId, Error(ToolErrorKinds.ToolFailed, [exception.Message], null, null), isError: true, $"{descriptor.Name}: failed");
                    outcome = CallOutcome.Failed;
                }
            }
            string? decisionId;
            lock (_gate)
            {
                // Plan §10.3: fetched or external content taints the session until the user speaks. A tool that returns outside
                // content without the network (a hub skill's file) says so on its context.
                if (descriptor.Risk.Network || callContext?.ResultTainted == true)
                    runner.Tainted = true;
                _pickDecisions.Remove(descriptor.Name, out decisionId);
            }
            if (decisionId is not null)
                _services.Fabric.RecordOutcome(runner.SessionId, decisionId,
                    outcome == CallOutcome.Succeeded ? "tool_succeeded" : "tool_failed", CancellationToken.None);
            return outcome;
        }

        /// <summary>
        /// The pre-execute checkpoint (§5.2) through the approval pipeline. Returns why the call may not run, or the
        /// pipeline's result when it may.
        /// </summary>
        private async Task<(string? Denial, ApprovalResult? Result)> AuthorizeAsync(ValidCall call, CancellationToken cancellationToken)
        {
            if (_services.Approvals is not { } approvals)
                return ("no approval pipeline is configured, so side-effecting tools do not run", null);
            var action = new ToolAction(call.Tool.Descriptor, call.Arguments);
            var context = new ApprovalContext(runner.SessionId, _options.ApprovalMode, State())
            {
                Tainted = runner.Tainted,
                KnownRecipients = _options.KnownRecipients,
                CallId = call.CallId,
                Interactive = _options.Interactive,
                Reversible = await ReversibleAsync(approvals, action).ConfigureAwait(false),
            };
            ApprovalResult result;
            try
            {
                result = await approvals.AuthorizeAsync(context, action, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // "not run": the call never started, which is how an undo knows it changed nothing (C-014).
                Result(call.CallId, Error(ToolErrorKinds.NotRun, ["the turn was cancelled while the approval was pending"], null, null), isError: true);
                throw;
            }
            // The call's execute_tool span is current here (ExecuteAsync started it); tagged before a denial returns too.
            if (!result.Assessment.Detections.IsEmpty
                && Activity.Current is { } span && span.DisplayName.StartsWith(KernelTelemetry.ExecuteToolPrefix, StringComparison.Ordinal))
                span.SetTag(KernelTelemetry.DetectionRules, result.Assessment.Detections.Select(finding => finding.Rule.Id).ToArray());
            return result.Allowed ? (null, result) : ($"{WireName(result.Resolution)}: {result.Assessment.Summary}", null);
        }

        /// <summary>
        /// The <c>reversible</c> the <c>risk</c> question sees (plan §10.2 step 4): whether a checkpoint could undo the parsed
        /// action. Null without checkpoints; false when the store cannot say.
        /// </summary>
        private async Task<bool?> ReversibleAsync(ApprovalPipeline approvals, ToolAction action)
        {
            if (_services.Checkpoints is not { } checkpoints)
                return null;
            try
            {
                return await checkpoints.IsReversibleAsync(approvals.Analyze(action)).ConfigureAwait(false);
            }
            catch (CheckpointException)
            {
                return false;
            }
        }

        private static string WireName(ApprovalResolution resolution) => resolution switch
        {
            ApprovalResolution.UserDenied => "denied by the user",
            ApprovalResolution.TimedOut => "the approval timed out",
            ApprovalResolution.Hardline => "refused by the hardline list",
            ApprovalResolution.RuleDenied => "refused by a remembered rule",
            ApprovalResolution.NoApprover => "nobody could approve it",
            _ => "the approval failed",
        };

        private async Task<IReadOnlyList<SlotDisagreement>> PrefillAsync(string callId, Offered tool, JsonElement arguments,
            CancellationToken cancellationToken)
        {
            if (!_services.Slots.TryCurrent(ModelSlots.Extractor, out var binding) || binding.Service is not IExtractor extractor)
                return [];
            // Labels from the tool's free-text parameters, as GLiNER2 reads them (§7 step 4).
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (tool.Function.JsonSchema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
                foreach (var parameter in tool.Descriptor.Parameters.Where(parameter => !parameter.IsSecret))
                    if (properties.TryGetProperty(parameter.Name, out var schema) && IsFreeText(schema))
                        labels.TryAdd(parameter.Description is { Length: > 0 } description ? description : parameter.Name.Replace('_', ' '), parameter.Name);
            if (labels.Count == 0)
                return [];

            ExtractionResult extraction;
            try
            {
                extraction = await extractor.ExtractAsync(new ExtractionRequest(envelope.Text, [.. labels.Keys], _options.SlotPrefillConfidence),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return [];
            }
            var found = new List<SlotDisagreement>();
            foreach (var span in extraction.Spans.Where(span => span.Confidence >= _options.SlotPrefillConfidence).GroupBy(span => span.Label)
                         .Select(group => group.MaxBy(span => span.Confidence)!))
            {
                if (!labels.TryGetValue(span.Label, out var parameter))
                    continue;
                string? argument = arguments.TryGetProperty(parameter, out var value) && value.ValueKind != JsonValueKind.Null ? JsonValues.Text(value) : null;
                if (argument is not null && (argument.Contains(span.Text, StringComparison.OrdinalIgnoreCase)
                    || span.Text.Contains(argument, StringComparison.OrdinalIgnoreCase)))
                    continue;
                found.Add(new SlotDisagreement(callId, tool.Function.Name, parameter, argument, span.Text, span.Confidence));
            }
            return found;
        }

        // Hints are shown once per turn: after the repair the generator's value stands. Read-only calls fail in parallel.
        private bool PrefillShown()
        {
            lock (_gate)
                return _prefillChecked;
        }

        /// <summary>
        /// The spans worth showing beside a failed call: each one substituted into the arguments must pass the schema and, for
        /// a tool that declares [Probe], the tool's own binding (a probe run; a span naming a path that does not exist is dropped).
        /// The hints kept are recorded and end pre-fill for the turn.
        /// </summary>
        private async Task<IReadOnlyList<SlotDisagreement>> HintsAsync(Offered tool, JsonElement arguments,
            IEnumerable<SlotDisagreement> spans, CancellationToken cancellationToken)
        {
            var hints = new List<SlotDisagreement>();
            foreach (var span in spans)
                if (await HintValidatesAsync(tool, arguments, span, cancellationToken).ConfigureAwait(false))
                    hints.Add(span);
            if (hints.Count == 0)
                return hints;
            lock (_gate)
            {
                if (_prefillChecked)
                    return [];
                _prefillChecked = true;
                _disagreements.AddRange(hints);
            }
            return hints;
        }

        private async Task<bool> HintValidatesAsync(Offered tool, JsonElement arguments, SlotDisagreement span,
            CancellationToken cancellationToken)
        {
            var values = JsonValues.ToDictionary(arguments);
            values[span.Parameter] = span.Extracted;
            var substituted = JsonValues.Object(values);
            // Other arguments may still be wrong (the call failed); only errors about this one count against the hint.
            var errors = ArgumentValidator.Validate(tool.Function.JsonSchema, substituted);
            if (errors.Any(error => error.Contains($"'{span.Parameter}'", StringComparison.Ordinal)))
                return false;
            // Only a tool that declares [Probe] (read-only, local, leaves no trace) is tried; the probe's result is discarded.
            // ReadOnly alone is not enough: for an MCP tool it is the server's annotation or the user's configuration, and a
            // probe runs unlogged and unasked with the user's words, so an outside server would see them.
            var risk = tool.Descriptor.Risk;
            if (errors.Count > 0 || !risk.Probe || !risk.ReadOnly || risk.Network)
                return true;
            bool tainted;
            lock (_gate)
                tainted = runner.Tainted;
            // The trial gets an id of its own, never the real call's: it is no call in the log, and nothing keyed by call id
            // (events, approvals, checkpoints, telemetry, a tool's own records) may take the discarded trial for the call.
            var context = new ToolCallContext(runner.SessionId, ProbeCallId()) { Tainted = tainted, Interactive = _options.Interactive };
            try
            {
                await tool.Function.InvokeAsync(context.AttachTo(JsonValues.ToArguments(substituted)), cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (ToolArgumentException exception) when (exception.ParamName == span.Parameter)
            {
                return false;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Some other failure (a tool's own timeout included) says nothing about the hint's value and must not end the
                // turn: only the turn's cancellation escapes a probe.
                return true;
            }
        }

        /// <summary>A probe's own call id, unique and unlike any issued id (those are the model's, or <c>call</c>, with a counter).</summary>
        private static string ProbeCallId() => "probe-" + Guid.NewGuid().ToString("N");

        private static IEnumerable<string> Describe(IReadOnlyList<SlotDisagreement> hints) => hints.Select(item => item.Argument is null
            ? $"'{item.Parameter}' was omitted, but the request says \"{item.Extracted}\""
            : $"'{item.Parameter}' is \"{item.Argument}\", but the request says \"{item.Extracted}\"");

        private static Dictionary<string, object?>? Extracted(IReadOnlyList<SlotDisagreement> hints) => hints.Count == 0 ? null
            : hints.ToDictionary(item => item.Parameter, item => (object?)item.Extracted, StringComparer.Ordinal);

        private static bool IsFreeText(JsonElement schema)
        {
            if (schema.TryGetProperty("enum", out _) || !schema.TryGetProperty("type", out var type))
                return false;
            return type.ValueKind == JsonValueKind.String ? type.GetString() == "string"
                : type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String && item.GetString() == "string");
        }

        // The round is the step: one per generator response with calls. Calls that need approval run one at a time, so a
        // later call of the same response is issued after an earlier one's result; the round keeps it in its response (C-026).
        /// <param name="lease">Released if the append fails, so a failed call never stays tracked or parked.</param>
        private void Issue(ValidCall call, IDisposable? lease = null)
        {
            try
            {
                lock (_gate)
                {
                    runner.Append(new ToolCallIssued(call.CallId, call.Tool.Function.Name, call.Arguments, _steps, call.Tool.Descriptor.Risk.ReadOnly));
                    _open.Add(call.CallId);
                }
            }
            catch
            {
                lease?.Dispose();
                throw;
            }
        }

        private void IssueRejected(string callId, string tool, JsonElement arguments, bool? readOnly, JsonElement error)
        {
            // A call that never ran still gets its span, so a trace shows the repair the next response is asked for.
            using var activity = StartToolActivity(callId, tool, approval: false);
            activity?.SetTag(KernelTelemetry.ToolOutcome, "invalid_arguments");
            activity?.SetStatus(ActivityStatusCode.Error);
            lock (_gate)
                runner.Append(new ToolCallIssued(callId, tool, arguments, _steps, readOnly), new ToolResult(callId, error, IsError: true));
        }

        // Read-only calls finish concurrently, so the shared turn state they touch is updated under the lock.
        private void Result(string callId, JsonElement result, bool isError, string? lastToolResult = null)
        {
            lock (_gate)
            {
                runner.Append(new ToolResult(callId, result, isError));
                _open.Remove(callId);
                if (lastToolResult is not null)
                    _lastToolResult = lastToolResult;
            }
        }

        /// <summary>After cancellation: every issued call gets a result, so the log has no orphan a later switch would refuse.</summary>
        public void CloseOpenCalls(string reason)
        {
            foreach (var callId in _open.ToArray())
                Result(callId, Error(ToolErrorKinds.Cancelled, [reason], null, null), isError: true);
        }

        private static JsonElement Error(string error, IEnumerable<string> details, JsonElement? expected, object? extracted)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("error", error);
                writer.WriteStartArray("details");
                foreach (var detail in details)
                    writer.WriteStringValue(detail);
                writer.WriteEndArray();
                if (expected is { } schema)
                {
                    writer.WritePropertyName("expected");
                    schema.WriteTo(writer);
                }
                if (extracted is not null)
                {
                    writer.WritePropertyName("from_request");
                    JsonValues.ToElement(extracted).WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            return document.RootElement.Clone();
        }

        // ---- checkpoints ----

        private async Task<CheckpointResult> StepAsync(CancellationToken cancellationToken)
        {
            var snapshot = runner.Snapshot();
            var questions = new List<KeyValuePair<string, DecisionQuestion>> { Ask("done"), Ask("stuck") };
            if (!_shortlist.IsEmpty)
                questions.Add(ToolQuestion());

            var names = snapshot.Transcript.Select(entry => entry.Content).OfType<ToolCallIssued>()
                .ToDictionary(call => call.CallId, call => call.ToolName, StringComparer.Ordinal);
            var results = snapshot.Transcript.Where(entry => entry.Content is ToolResult).ToList();
            var cleared = PromptRenderer.DecidedViews(snapshot);
            // clear_results is asked about each full result older than the last few (its below-floor rule stubs them).
            foreach (var entry in results.Take(Math.Max(0, results.Count - _options.KeepRecentResults))
                         .Where(entry => !cleared.ContainsKey(((ToolResult)entry.Content).CallId)).Take(_options.MaxClearQuestions))
            {
                var result = (ToolResult)entry.Content;
                questions.Add(KeyValuePair.Create(PromptRenderer.ClearResultsPrefix + result.CallId,
                    Catalogue["clear_results"].ToQuestion(instructions: Instructions(Catalogue["clear_results"], names.GetValueOrDefault(result.CallId, "tool"),
                        result, entry.FromSequence))));
            }
            // injection is asked about content that came from outside (a network tool); either answer leaves it tainted.
            var latest = _lastRound;
            foreach (var entry in results.Where(entry => latest.Contains(((ToolResult)entry.Content).CallId)))
            {
                var result = (ToolResult)entry.Content;
                var tool = names.GetValueOrDefault(result.CallId, "");
                if (_catalogue.TryGetValue(tool, out var offered) && offered.Descriptor.Risk.Network && !result.IsError)
                    questions.Add(KeyValuePair.Create("injection/" + result.CallId,
                        Catalogue["injection"].ToQuestion(instructions: Instructions(Catalogue["injection"], tool, result, entry.FromSequence))));
            }
            return await DecideAsync(Checkpoint.Step, questions, cancellationToken).ConfigureAwait(false);
        }

        private static DecisionContent Instructions(CatalogueQuestion question, string tool, ToolResult result, long sequence)
        {
            var text = JsonValues.Text(result.Result);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                writer.WriteStartObject();
                writer.WriteString("question", question.Instructions);
                writer.WriteString("tool", tool);
                writer.WriteNumber("event", sequence);
                // Kev's row budget is about 1,000 tokens (§5.1); an excerpt is enough to judge relevance or intent.
                writer.WriteString("result", text.Length > 1_200 ? text[..1_200] + "…" : text);
                writer.WriteEndObject();
            }
            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            return DecisionContent.FromJson(document.RootElement);
        }

        private Task<CheckpointResult> DecideAsync(Checkpoint checkpoint, List<KeyValuePair<string, DecisionQuestion>> questions,
            CancellationToken cancellationToken, params string[] extraConditions) =>
            DecideAsync(checkpoint, questions, skip: null, cancellationToken, extraConditions);

        /// <param name="skip">Keys not to ask, with why (C-030): the fabric gives each its below-floor action.</param>
        private async Task<CheckpointResult> DecideAsync(Checkpoint checkpoint, List<KeyValuePair<string, DecisionQuestion>> questions,
            IReadOnlyDictionary<string, string>? skip, CancellationToken cancellationToken, params string[] extraConditions)
        {
            var conditions = new HashSet<string>(extraConditions, StringComparer.Ordinal);
            if (_options.EscalationAllowed)
                conditions.Add(FabricConditions.EscalationAllowed);
            CheckpointResult result;
            using (RequestScope())
                result = await _services.Fabric.DecideAsync(runner.SessionId, checkpoint, State(BudgetFor(checkpoint, questions)), questions,
                    conditions, skip, cancellationToken).ConfigureAwait(false);
            if (result.Verdicts.TryGetValue("tool", out var tool) && tool.Verdict.Acted && tool.Verdict.Option is { } pick && pick != None)
                lock (_gate)
                    _pickDecisions[pick] = tool.DecisionId;
            return result;
        }

        private QuestionCatalogue Catalogue => _services.Fabric.Policy.Catalogue;

        private KeyValuePair<string, DecisionQuestion> Ask(string id) =>
            KeyValuePair.Create(id, Catalogue[id].ToQuestion());

        /// <summary>The <c>tool</c> question: the shortlist as <c>name: description</c> options, plus none (§5.2).</summary>
        private KeyValuePair<string, DecisionQuestion> ToolQuestion() =>
            KeyValuePair.Create("tool", Catalogue["tool"].ToQuestion(
                [.. _shortlist.Where(tool => tool.Function.Name != None).Select(tool => new QuestionOption(tool.Function.Name, tool.Function.Description)),
                 new QuestionOption(None, "no tool is needed")]));

        /// <summary>
        /// C-022, C-033: <c>injection</c> about the shortlisted MCP tools' descriptions, trusted servers' included, in the
        /// ingress batch, the shape <see cref="LoadSkillAsync"/> uses for a hub skill's body. Each origin's tools are packed
        /// by name into chunks of whole <c>- name: description</c> lines up to <see cref="DescriptionBudget"/> characters (a
        /// longer line is cut and asked alone), keyed <c>injection/mcp:&lt;server&gt;</c>, then <c>#2</c> and on; each chunk is
        /// asked once per session for the same text and records the tools it carried, so a confident yes withholds exactly
        /// those (<see cref="WithholdDescriptions"/>). A chunk with an outside description (an untrusted server's, or an allowed
        /// flagged tool's) is asked first and always, as every untrusted origin was before C-033, and asking it taints; chunks
        /// of only trusted text fill the batch up to <see cref="MaxDescriptionQuestions"/> description questions in all (the
        /// rest wait for a later turn), and their text goes to the decider, not to the generator, so they do not taint.
        /// Review of #47, finding 1: the cap no longer lets trusted origins that sort first starve an untrusted one.
        /// </summary>
        private List<KeyValuePair<string, DecisionQuestion>> DescriptionQuestions()
        {
            var questions = new List<KeyValuePair<string, DecisionQuestion>>();
            var question = Catalogue["injection"];
            var candidates = new List<(string Origin, int Index, string Text, List<Offered> Tools, bool Outside)>();
            foreach (var group in _shortlist.Where(tool => tool.Descriptor.Origin?.StartsWith(McpOrigin, StringComparison.Ordinal) == true)
                         .GroupBy(tool => tool.Descriptor.Origin!, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var chunks = DescriptionChunks(group.OrderBy(tool => tool.Function.Name, StringComparer.Ordinal));
                for (var index = 0; index < chunks.Count; index++)
                    candidates.Add((group.Key, index, chunks[index].Text, chunks[index].Tools,
                        chunks[index].Tools.Any(tool => tool.Descriptor.Risk.OutsideDescription)));
            }
            var taint = false;
            // Outside chunks first (stable: by origin, then chunk), then trusted ones while the cap allows.
            foreach (var (origin, index, text, tools, outside) in candidates.Where(chunk => chunk.Outside).Concat(candidates.Where(chunk => !chunk.Outside)))
            {
                if (!outside && questions.Count >= MaxDescriptionQuestions)
                    break;
                var marker = origin + "\n" + text;
                // Marked only when asked, so a chunk over the cap is asked in a later turn.
                if (!runner._describedOrigins.Add(marker))
                    continue;
                var key = "injection/" + origin + (index == 0 ? "" : "#" + (index + 1).ToString(CultureInfo.InvariantCulture));
                questions.Add(KeyValuePair.Create(key, question.ToQuestion(instructions: DecisionContent.FromText(
                    $"{question.Instructions}\n{origin} tool descriptions:\n{text}"))));
                _described[key] = (origin, [.. tools.Select(tool => tool.Function.Name)], marker);
                taint |= outside;
            }
            if (taint)
                lock (_gate)
                    runner.Tainted = true;
            return questions;
        }

        /// <summary>Whole description lines packed in order into chunks of at most <see cref="DescriptionBudget"/> characters.</summary>
        private static List<(string Text, List<Offered> Tools)> DescriptionChunks(IEnumerable<Offered> tools)
        {
            var chunks = new List<(string Text, List<Offered> Tools)>();
            var lines = new List<string>();
            var members = new List<Offered>();
            var length = 0;
            foreach (var tool in tools)
            {
                var line = $"- {tool.Function.Name}: {tool.Function.Description}";
                // Cut to the budget, a long line fills its chunk alone.
                if (line.Length > DescriptionBudget)
                    line = line[..(DescriptionBudget - 1)] + "…";
                if (members.Count > 0 && length + 1 + line.Length > DescriptionBudget)
                {
                    chunks.Add((string.Join("\n", lines), members));
                    lines = [];
                    members = [];
                    length = 0;
                }
                length += (members.Count > 0 ? 1 : 0) + line.Length;
                lines.Add(line);
                members.Add(tool);
            }
            if (members.Count > 0)
                chunks.Add((string.Join("\n", lines), members));
            return chunks;
        }

        /// <summary>
        /// C-033: for each description question whose verdict acted (<c>injection</c> degrades to its conservative default, so
        /// only a calibrated answer at or above the floor acts), the tools in that chunk leave this turn's shortlist and
        /// catalogue, so no generator call and no <c>tool</c> pick of the turn offers them, and their source is told to hold
        /// them until the user allows them. The generator never saw them, so withholding does not taint. A guest's turn
        /// (C-020; review of #47, finding 4) withholds them from that turn only: what a guest says is in the state the question
        /// is conditioned on, so it must not hold the owner's tools for every session; the chunk is unmarked, so the owner's
        /// next turn asks about it again.
        /// </summary>
        private void WithholdDescriptions(CheckpointResult verdicts)
        {
            foreach (var (key, (origin, tools, marker)) in _described)
            {
                if (!Acted(verdicts, key, out _))
                    continue;
                var withheld = tools.ToHashSet(StringComparer.Ordinal);
                _shortlist = [.. _shortlist.Where(tool => !withheld.Contains(tool.Function.Name))];
                foreach (var name in tools)
                    _catalogue.Remove(name);
                if (envelope.Guest is not null)
                    runner._describedOrigins.Remove(marker);
                else
                    _services.DescriptionReview?.Withhold(new ToolWithholding(origin, tools, verdicts[key].DecisionId));
            }
            _described.Clear();
        }

        private static bool Acted(CheckpointResult result, string key, out string? option)
        {
            option = null;
            if (!result.Verdicts.TryGetValue(key, out var verdict) || !verdict.Verdict.Acted)
                return false;
            option = verdict.Verdict.Option;
            return true;
        }

        /// <summary>What the generator may see after a <c>tool</c> verdict: one tool, none, or null for the shortlist (§5.3 rule 2).</summary>
        private ImmutableArray<Offered>? OfferFrom(CheckpointResult result, string key)
        {
            if (!Acted(result, key, out var pick) || pick is null)
                return null;
            if (pick == None)
                return [];
            return _shortlist.FirstOrDefault(tool => tool.Function.Name == pick) is { } chosen ? [chosen] : null;
        }

        /// <summary>
        /// C-029: the ingress checkpoint that asks both <c>tool</c> and <c>skill</c> (six questions, the two longest rows)
        /// gets <see cref="TurnOptions.DualShortlistStateBudget"/>, which keeps it under S1's 300 ms line on the GPU; every
        /// other checkpoint gets the configured state budget. It only ever shrinks the state.
        /// </summary>
        private TurnStateBudget? BudgetFor(Checkpoint checkpoint, List<KeyValuePair<string, DecisionQuestion>> questions) =>
            checkpoint == Checkpoint.Ingress && _options.DualShortlistStateBudget is { } dual
            && dual.Total < (_options.StateBudget ?? TurnStateBudget.Default).Total
            && questions.Exists(question => question.Key == "tool") && questions.Exists(question => question.Key == "skill")
                ? dual
                : _options.StateBudget;

        private TurnState State(TurnStateBudget? budget = null)
        {
            var generator = _services.Slots.GetChatClient(ModelSlots.Generator);
            var profile = _services.Slots.Current(ModelSlots.Generator).Profile;
            var snapshot = runner.Snapshot();
            // The generator's own tokenizer when its binding offers one (Starling's Qwen does), so budgets are in its units.
            var builder = new TurnStateBuilder(Counter(generator), budget ?? _options.StateBudget);
            return builder.Build(new TurnStateInput(envelope.Text, envelope.Surface, Describe(profile), Recent(snapshot), snapshot.Goal,
                _entities.IsEmpty ? null : _entities, _steps, _lastToolResult));
        }

        private static string Describe(ModelProfile profile) =>
            string.Create(CultureInfo.InvariantCulture, $"{profile.Id} {PrivacyName(profile.Privacy)}, {Math.Max(1, profile.AcceptedContext / 1024)}k");

        private static string PrivacyName(PrivacyClass privacy) => privacy switch
        {
            PrivacyClass.Local => "local",
            PrivacyClass.LocalServer => "local-server",
            _ => "cloud",
        };

        /// <summary>One-liners for the transcript entries before this turn's message (§5.1: events, not message text).</summary>
        private ImmutableArray<string> Recent(SessionSnapshot snapshot)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var lines = new List<string>();
            foreach (var entry in snapshot.Transcript)
            {
                switch (entry.Content)
                {
                    case UserMessage user: lines.Add("user: " + Short(user.Text)); break;
                    case AssistantMessage assistant: lines.Add("assistant: " + Short(assistant.Text)); break;
                    case ToolCallIssued call:
                        names[call.CallId] = call.ToolName;
                        lines.Add("called " + call.ToolName);
                        break;
                    case ToolResult result: lines.Add($"{names.GetValueOrDefault(result.CallId, "tool")} {(result.IsError ? "failed" : "returned")}"); break;
                    case Compacted compacted: lines.Add($"summary of events {compacted.FromSequence}–{compacted.ThroughSequence}"); break;
                }
            }
            // The latest user line is this turn's message, which the state carries as user_message.
            var current = lines.FindLastIndex(line => line.StartsWith("user: ", StringComparison.Ordinal));
            if (current >= 0)
                lines.RemoveAt(current);
            return [.. lines.TakeLast(_options.RecentEvents)];

            static string Short(string text) => text.Length > 80 ? text[..80] + "…" : text;
        }

        // ---- ingress helpers ----

        private async Task ExtractEntitiesAsync(CancellationToken cancellationToken)
        {
            if (_options.EntityLabels.IsDefaultOrEmpty || !_services.Slots.TryCurrent(ModelSlots.Extractor, out var binding)
                || binding.Service is not IExtractor extractor)
                return;
            try
            {
                var result = await extractor.ExtractAsync(new ExtractionRequest(envelope.Text, _options.EntityLabels), cancellationToken)
                    .ConfigureAwait(false);
                _entities = result.Spans.GroupBy(span => span.Label, StringComparer.Ordinal).ToImmutableSortedDictionary(
                    group => group.Key, group => group.Select(span => span.Text).Distinct(StringComparer.Ordinal).ToImmutableArray(),
                    StringComparer.Ordinal);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Over-long input throws (the extractor never truncates); the state goes without entities.
            }
        }

        private async Task BuildShortlistAsync(CancellationToken cancellationToken)
        {
            // A guest's turn (C-020) knows only the tools its scope names: they are never offered, and a call to any other is
            // refused as not offered. By name, so a tool registered later (an MCP server's, a skill's) is out unless named. A
            // tool that writes memory is never a guest's, named or not: the owner's memory and review queue are not theirs.
            foreach (var function in _services.Tools.GetFunctions(_options.SecretNames))
                if (function.GetService(typeof(ToolDescriptor)) is ToolDescriptor descriptor
                    && (envelope.Guest is null || envelope.Guest.Allows(function.Name) && !descriptor.Risk.SideEffects.HasFlag(SideEffects.Memory)))
                    _catalogue[function.Name] = new Offered(function, descriptor);
            if (_catalogue.Count == 0 || !_services.Slots.TryCurrent(ModelSlots.TextEmbedder, out var binding)
                || binding.Service is not IEmbeddingGenerator<string, Embedding<float>> embedder)
            {
                // Without an embedder there is no ranking; the hard filters still apply and the list is capped.
                _shortlist = [.. _catalogue.Values.Where(tool => _options.ToolFilter?.Invoke(tool.Descriptor) ?? true)
                    .OrderBy(tool => tool.Function.Name, StringComparer.Ordinal).Take(_services.Shortlist.Options.Size)];
                return;
            }

            var usesAgo = new Dictionary<string, int>(StringComparer.Ordinal);
            var calls = runner.Snapshot().Transcript.Select(entry => entry.Content).OfType<ToolCallIssued>().Reverse().ToList();
            for (var i = 0; i < calls.Count; i++)
                usesAgo.TryAdd(calls[i].ToolName, i);
            var items = _catalogue.Values.Select(tool => new ShortlistItem(tool.Function.Name, tool.Function.Description,
                _options.ToolExamples?.GetValueOrDefault(tool.Function.Name) ?? []));
            var ranked = await _services.Shortlist.RankAsync(envelope.Text, items, embedder,
                item => _options.ToolFilter?.Invoke(_catalogue[item.Name].Descriptor) ?? true, usesAgo, cancellationToken).ConfigureAwait(false);
            _shortlist = [.. ranked.Items.Select(item => _catalogue[item.Name])];
        }

        // ---- skills (plan §11) ----

        private async Task BuildSkillShortlistAsync(CancellationToken cancellationToken)
        {
            // The user's skills are theirs: a guest's turn is offered none (C-020).
            if (_services.Skills is not { } skills || _options.PinnedSkill is not null || envelope.Guest is not null)
                return;
            var available = skills.Available([.. _catalogue.Keys]);
            if (available.Count == 0)
                return;
            var byName = available.DistinctBy(skill => skill.Name, StringComparer.Ordinal).ToDictionary(skill => skill.Name, StringComparer.Ordinal);
            var size = _services.SkillShortlist.Options.Size;
            if (!_services.Slots.TryCurrent(ModelSlots.TextEmbedder, out var binding)
                || binding.Service is not IEmbeddingGenerator<string, Embedding<float>> embedder)
            {
                _skills = [.. byName.Values.OrderBy(skill => skill.Name, StringComparer.Ordinal).Take(size)];
                return;
            }
            var ranked = await _services.SkillShortlist.RankAsync(envelope.Text,
                byName.Values.Select(skill => new ShortlistItem(skill.Name, skill.Description, skill.Examples)), embedder,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            _skills = [.. ranked.Items.Select(item => byName[item.Name])];
        }

        /// <summary>The <c>skill</c> question: the skill shortlist as <c>name: description</c> options, plus none.</summary>
        private KeyValuePair<string, DecisionQuestion> SkillQuestion() =>
            KeyValuePair.Create("skill", Catalogue["skill"].ToQuestion(
                [.. _skills.Where(skill => skill.Name != None).Select(skill => new QuestionOption(skill.Name, skill.Description)),
                 new QuestionOption(None, "no saved skill applies")]));

        /// <summary>
        /// A confident <c>skill</c> pick (or a pinned skill) loads the body (L1) into the system prompt; otherwise only the
        /// shortlist's names and descriptions (L0) are shown. A body from outside (a hub skill) taints the session (§10.3),
        /// and <c>injection</c> is asked about it, which taints either way.
        /// </summary>
        private async Task LoadSkillAsync(CheckpointResult verdicts, CancellationToken cancellationToken)
        {
            if (_services.Skills is not { } skills || envelope.Guest is not null)
                return;
            var picked = Acted(verdicts, "skill", out var pick) ? pick : null;
            var name = _options.PinnedSkill ?? (picked is not null && picked != None ? picked : null);
            var body = name is null ? null : skills.Load(name);
            if (body is null)
            {
                // A confident "none" means no skill applies, so not even the index is shown.
                if (!_skills.IsEmpty && picked != None)
                    _skillNote = "Saved skills that may apply (not loaded" +
                        (_catalogue.ContainsKey(SkillTools.Read) ? $"; {SkillTools.Read} loads one" : "") + "):\n" +
                        string.Join("\n", _skills.Select(skill => $"- {skill.Name}: {skill.Description}"));
                // C-022: a hub skill's L0 description in the index is outside content too.
                if (_skillNote.Length > 0 && _skills.Any(skill => !skill.Trusted))
                    lock (_gate)
                        runner.Tainted = true;
                return;
            }
            _skill = body.Name;
            _skillNote = $"Skill {body.Name}: follow it for this request.\n{body.Text.Trim()}" +
                (body.Resources.IsDefaultOrEmpty ? "" : $"\nFiles in this skill ({SkillTools.Read} reads them): {string.Join(", ", body.Resources)}");
            if (body.Trusted)
                return;
            lock (_gate)
                runner.Tainted = true;
            var question = Catalogue["injection"];
            var instructions = DecisionContent.FromText($"{question.Instructions}\nskill {body.Name}:\n" +
                (body.Text.Length > 1_200 ? body.Text[..1_200] + "…" : body.Text));
            var key = "injection/skill:" + body.Name;
            // C-030: the verdict is never read (the body has tainted the turn already), so with reduced checkpoints the whole
            // request is saved; the question is still logged, as skipped.
            IReadOnlyDictionary<string, string>? skip = _services.Fabric.Reduction is { } reduction
                ? new Dictionary<string, string>(StringComparer.Ordinal) { [key] = "verdict unused, the skill body taints the turn; " + reduction.Reason }
                : null;
            await DecideAsync(Checkpoint.Ingress, [KeyValuePair.Create(key, question.ToQuestion(instructions: instructions))], skip,
                cancellationToken).ConfigureAwait(false);
        }

        private async Task RecallAsync(bool full, CancellationToken cancellationToken)
        {
            var entities = _entities.SelectMany(pair => pair.Value.Select(name => new EntityRef(pair.Key, name))).ToImmutableArray();
            // needs_recall below its floor retrieves the top three core facts only (§5.3). Lessons of the session's project
            // (C-025) join full recall only.
            var request = full
                ? new RetrievalRequest(envelope.Text, _options.RecallTokens, entities, ProjectId: _services.Project?.Id)
                : new RetrievalRequest(envelope.Text, _options.RecallTokens, entities, [MemoryItemKind.Fact]);
            try
            {
                var result = await _services.Retriever!.RetrieveAsync(request, cancellationToken).ConfigureAwait(false);
                var items = full ? result.Items : result.Items.Take(3).ToImmutableArray();
                if (!items.IsEmpty)
                    _recalled = items;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Retrieval is advisory; the turn continues without it.
            }
        }

        private async Task<bool> EscalateAsync(string reason, CancellationToken cancellationToken)
        {
            // The policy governs every trigger (difficulty, stuck, repair, context), not only the ones the fabric is asked about.
            if (!_options.EscalationAllowed)
                return false;
            var result = await _services.Slots.EscalateAsync(ModelSlots.Generator, cancellationToken).ConfigureAwait(false);
            if (result.Status != SwitchStatus.Switched)
                return false;
            runner.NoteBudgetChange(result);
            _escalations.Add($"{result.To!.Id}: {reason}");
            return true;
        }

        // ---- egress ----

        private async Task<TurnResult> FinishAsync(TurnStatus status, string text, CancellationToken cancellationToken)
        {
            var answer = runner.Append(new AssistantMessage(text)).Sequence;
            var deliver = "reply";
            var egress = new List<KeyValuePair<string, DecisionQuestion>>();
            // A guest's message (C-020) is never offered to memory: staged, it would sit in the owner's review queue as a
            // proposal written by someone else, worded to be approved.
            if (_services.Memory is not null && status == TurnStatus.Answered && envelope.Guest is null)
                egress.Add(Ask("remember"));
            if (_options.AskDeliver)
                egress.Add(Ask("deliver"));
            // skill_opportunity acts only on procedure evidence (its required condition): three tool calls, or a failed call
            // recovered by a different one of its family (C-026). Other turns are not asked: in the degraded tier each ask is
            // a generator call. A replay or eval turn is never distilled again.
            var learnable = _services.SkillLearning is not null && status == TurnStatus.Answered && _options.PinnedSkill is null
                && envelope.Guest is null;
            // A recovery needs a failed call and a later one, so shorter turns skip the log read.
            var scan = learnable && _toolCalls >= 2 ? ToolRecovery.Scan(TurnEvents(answer, cancellationToken)) : null;
            var opportunity = scan is not null && (_toolCalls >= 3 || !scan.Recoveries.IsEmpty);
            var conditions = new List<string>();
            if (opportunity)
            {
                // task_succeeded is the precondition with its own floor: the fabric evaluates it first and grants
                // skill_opportunity its task_succeeded condition only when it acted.
                egress.Add(KeyValuePair.Create("skill_opportunity", Catalogue["skill_opportunity"].ToQuestion(
                    instructions: OpportunityInstructions(scan!))));
                egress.Add(KeyValuePair.Create("task_succeeded", Catalogue["task_succeeded"].ToQuestion(
                    instructions: SuccessInstructions(scan!))));
                conditions.Add(FabricConditions.ProcedureEvidence);
                if (scan!.Unrecovered == 0)
                    conditions.Add(FabricConditions.ToolFailuresRecovered);
            }
            // lesson_opportunity (C-025) asks about an answered, interactive turn of the user's own in a project, with evidence
            // to cite (its condition: a tool call, so a failed result or a denied approval too). A replay never teaches.
            var lessonEvidence = _toolCalls >= 1;
            var lesson = _services.LessonLearning is not null && _services.Project is not null && status == TurnStatus.Answered
                && envelope.Guest is null && _options.PinnedSkill is null && _options.Interactive && lessonEvidence;
            if (lesson)
            {
                egress.Add(Ask("lesson_opportunity"));
                conditions.Add(FabricConditions.LessonEvidence);
            }
            if (egress.Count > 0)
            {
                var verdicts = await DecideAsync(Checkpoint.Egress, egress, cancellationToken, [.. conditions]).ConfigureAwait(false);
                // An embedding pick is no confidence: only a calibrated answer sends the reply anywhere but back (C-020), so
                // the degraded tier never silences an answer or moves it to another channel.
                if (Acted(verdicts, "deliver", out var target) && target is not null
                    && (verdicts["deliver"].Answer?.Calibrated == true || _services.Fabric.Policy.Floors.DegradedTurnBlocking))
                    deliver = target;
                if (Acted(verdicts, "remember", out _))
                    await RememberAsync(cancellationToken).ConfigureAwait(false);
                // Acting implies task_succeeded acted: the fabric granted the condition.
                if (Acted(verdicts, "skill_opportunity", out _))
                {
                    var success = verdicts["task_succeeded"];
                    _services.SkillLearning!.Queue(new SkillOpportunity(runner.SessionId, _turnStart, answer,
                        verdicts["skill_opportunity"].DecisionId, envelope.Text, runner.Tainted, success.DecisionId,
                        success.Answer?.Calibrated == true));
                    _skillQueued = true;
                }
                if (Acted(verdicts, "lesson_opportunity", out _))
                {
                    _services.LessonLearning!.Queue(new LessonOpportunity(runner.SessionId, _services.Project!.Id, _turnStart, answer,
                        verdicts["lesson_opportunity"].DecisionId, envelope.Text, runner.Tainted, _heldLocalOnly));
                    _lessonQueued = true;
                }
            }
            await runner.DeEscalateAsync(cancellationToken).ConfigureAwait(false);
            return Result(status, text) with { Deliver = deliver };
        }

        // The turn from the log, not the snapshot: compaction during the turn can fold its early calls into a summary.
        private IEnumerable<StoredEvent> TurnEvents(long through, CancellationToken cancellationToken) =>
            SessionProjection.ReadRange(_services.Log, runner.SessionId, _turnStart - 1, through, cancellationToken);

        private DecisionContent SuccessInstructions(ToolRecoveryScan scan) => JsonInstructions(writer =>
        {
            writer.WriteString("question", Catalogue["task_succeeded"].Instructions);
            writer.WriteNumber("tool_calls", scan.Calls);
            writer.WriteNumber("failed_calls", scan.Failures);
            writer.WriteNumber("unrecovered_failures", scan.Unrecovered);
        });

        private DecisionContent OpportunityInstructions(ToolRecoveryScan scan) => JsonInstructions(writer =>
        {
            writer.WriteString("question", Catalogue["skill_opportunity"].Instructions);
            writer.WriteNumber("tool_calls", scan.Calls);
            if (scan.Describe() is { } recovery)
                writer.WriteString("recovery", recovery);
        });

        private static DecisionContent JsonInstructions(Action<Utf8JsonWriter> write)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                writer.WriteStartObject();
                write(writer);
                writer.WriteEndObject();
            }
            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            return DecisionContent.FromJson(document.RootElement);
        }

        /// <summary>
        /// remember acted: the user's message enters the memory write path (§9.2), which stages it without Kev, and always in a
        /// tainted session (§10.3), where the message may not be the user's.
        /// </summary>
        private async Task RememberAsync(CancellationToken cancellationToken)
        {
            var source = runner.Snapshot().Transcript.LastOrDefault(entry => entry.Content is UserMessage)?.FromSequence;
            var profile = _services.Slots.Current(ModelSlots.Generator).Profile;
            try
            {
                using var scope = RequestScope();
                var written = await _services.Memory!.WriteAsync(new MemoryWriteRequest(runner.SessionId, envelope.Text,
                    SourceSequence: source, Surface: envelope.Surface, Generator: Describe(profile), Tainted: runner.Tainted),
                    cancellationToken).ConfigureAwait(false);
                _memoryWrite = written.Status switch
                {
                    MemoryWriteStatus.Staged => $"staged {written.MemoryId}",
                    MemoryWriteStatus.Refused => "refused: " + written.Reason,
                    _ => $"{written.Operation} {written.MemoryId}",
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failed memory write never loses the answer; it is reported on the result.
                _memoryWrite = "failed: " + exception.Message;
            }
        }

        public TurnResult Result(TurnStatus status, string? text) =>
            new(status, text, _steps, _toolCalls, runner.Tainted, "reply", [.. _escalations], [.. _disagreements], _memoryWrite, [.. _compactions])
            {
                Skill = _skill,
                SkillQueued = _skillQueued,
                LessonQueued = _lessonQueued,
            };
    }

    private sealed class ContextOverBudgetException(string message) : Exception(message);
}
