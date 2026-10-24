using System.Collections.Concurrent;
using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Approvals.Shell;
using Corvid.Fabric;
using Corvid.Rules;

namespace Corvid.Approvals;

/// <summary>Approval modes (plan §10.2 step 5). The hardline list applies in every mode.</summary>
public enum ApprovalMode
{
    /// <summary>Every side effect asks.</summary>
    Manual,
    /// <summary>
    /// Asks unless a calibrated <c>risk</c> answer clears its floor at level 0–1 and the action stays in the workspace,
    /// without network, elevation or a destructive tool.
    /// </summary>
    Smart,
    /// <summary>Follows the fabric: a calibrated level 0–1 runs, levels 2–4 ask (plan §5.3).</summary>
    Permissive,
}

/// <summary>What the turn knows that the action does not.</summary>
/// <param name="TurnState">The checkpoint state (plan §5.1) the <c>risk</c> question is asked against.</param>
public sealed record ApprovalContext(string SessionId, ApprovalMode Mode, TurnState TurnState)
{
    /// <summary>The turn's context holds content from outside the user (plan §10.3).</summary>
    public bool Tainted { get; init; }

    /// <summary>Recipients the user has already messaged; others are new. Null means none are known.</summary>
    public IReadOnlySet<string>? KnownRecipients { get; init; }

    /// <summary>Whether the action can be undone from a checkpoint (C-014), for the risk state; null when unknown.</summary>
    public bool? Reversible { get; init; }

    /// <summary>False for scheduled and background work with nobody to ask: an ask becomes a denial.</summary>
    public bool Interactive { get; init; } = true;

    /// <summary>The tool call the action belongs to, recorded on <c>ApprovalRequested</c> so the log links the two.</summary>
    public string? CallId { get; init; }
}

public enum ApprovalOutcome
{
    Auto,
    Ask,
    Deny,
}

/// <summary>The pipeline's verdict before anyone is asked.</summary>
/// <param name="Reasons">Hardline denials, reasons that force a prompt, and the policy's own reason, in that order.</param>
/// <param name="Offered">The scopes a prompt may offer: once only when anything forced the prompt or is unresolved.</param>
/// <param name="Rule">The session or always rule that decided, if one did.</param>
/// <param name="Risk">The logged <c>risk</c> decision, when there is one: asked, or skipped under reduced checkpoints
/// (C-030), where <see cref="FabricVerdict.Skipped"/> is true and no decider scored the action.</param>
public sealed record ApprovalAssessment(
    ApprovalOutcome Outcome,
    ActionAnalysis Analysis,
    ImmutableArray<ApprovalReason> Reasons,
    ImmutableArray<ApprovalScope> Offered,
    ApprovalRule? Rule,
    FabricVerdict? Risk)
{
    public bool Hardline => Reasons.Any(reason => reason.Code is HardlineRules.DiskFormat or HardlineRules.RawDeviceWrite
        or HardlineRules.RecursiveDeleteOutsideWorkspace or HardlineRules.CredentialStoreWrite or HardlineRules.VaultAccess
        or HardlineRules.SafetyConfigurationChange);

    public string Summary => Reasons.IsEmpty ? Outcome.ToString().ToLowerInvariant() : string.Join("; ", Reasons);

    /// <summary>
    /// Detection rules (C-023) the call completed a match of, enforced or not. An enforced one is also among
    /// <see cref="Reasons"/> as an <see cref="ApprovalReasons.Detection"/> reason. Empty after a hardline denial, which is
    /// decided before any rule runs.
    /// </summary>
    public ImmutableArray<DetectionFinding> Detections { get; init; } = [];
}

/// <summary>A prompt shown to the user. <see cref="Deadline"/> is when an unanswered prompt is denied.</summary>
public sealed record ApprovalPrompt(
    string ApprovalId,
    string SessionId,
    ActionAnalysis Analysis,
    ImmutableArray<ApprovalReason> Reasons,
    ImmutableArray<ApprovalScope> Offered,
    DateTimeOffset Deadline)
{
    /// <summary>The tool call waiting on the prompt (<see cref="ApprovalContext.CallId"/>), so a surface can show the two together.</summary>
    public string? CallId { get; init; }

    /// <summary>
    /// The action as it runs, with its in-memory arguments. The logged <see cref="ToolCallIssued"/> holds them credential-scrubbed
    /// (C-040), so a surface that shows what runs or what is written (a diff, the raw input) builds it from this.
    /// </summary>
    public ProposedAction? Action { get; init; }
}

/// <param name="Scope">Session or always stores a structural rule (allow or deny) for each side-effecting command.</param>
public sealed record ApprovalAnswer(bool Approved, ApprovalScope Scope = ApprovalScope.Once)
{
    /// <summary>
    /// For a session or always answer (G-11): each rule's paths as the user narrowed them, in the order of
    /// <see cref="ApprovalPipeline.RulesFor"/>; an empty entry, or none at all, keeps that rule's paths. Only narrower paths
    /// are written: a narrowing the pipeline cannot verify (<see cref="ApprovalPipeline.Narrow"/>) writes no rule, and the
    /// answer applies to this action only.
    /// </summary>
    public ImmutableArray<ImmutableArray<string>> RulePaths { get; init; } = [];

    public static ApprovalAnswer Approve(ApprovalScope scope = ApprovalScope.Once) => new(true, scope);

    public static ApprovalAnswer Deny(ApprovalScope scope = ApprovalScope.Once) => new(false, scope);
}

/// <summary>The surface that asks the user (CLI, gateway). Implementations should honour cancellation.</summary>
public interface IApprovalPrompter
{
    ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken);
}

public enum ApprovalResolution
{
    /// <summary>Read-only, or allowed by the mode and the risk answer.</summary>
    Automatic,
    RuleAllowed,
    RuleDenied,
    Hardline,
    UserApproved,
    UserDenied,
    /// <summary>Nobody answered before the deadline.</summary>
    TimedOut,
    /// <summary>An ask with no prompter, or in a non-interactive context.</summary>
    NoApprover,
    /// <summary>The prompter failed; the action is denied.</summary>
    Failed,
}

/// <param name="ApprovalId">Set when the user was asked (an <c>ApprovalRequested</c> event was appended).</param>
public sealed record ApprovalResult(bool Allowed, ApprovalResolution Resolution, ApprovalAssessment Assessment, string? ApprovalId,
    ApprovalScope? Scope);

public sealed record ApprovalOptions
{
    public static ApprovalOptions Default { get; } = new();

    /// <summary>An unanswered prompt is denied after this long (plan §10.2, as in Hermes).</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Tools whose call runs a shell command line, by tool name, with the string parameter that holds the command (the
    /// built-in <c>shell</c> tool's is <c>command</c>). A call to one is assessed as the <see cref="ShellAction"/> it
    /// runs, parsed and unwrapped, so the hardline list, the rules and <c>risk</c> see the commands rather than an opaque
    /// tool call, and a read-only command runs without asking.
    /// </summary>
    public IReadOnlyDictionary<string, string> ShellTools { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// Detection rules evaluated at pre_execute (C-023), after the hardline list and before everything else. Null (the
    /// default) leaves the pipeline exactly as it is without them. Build the engine with the same <see cref="ShellTools"/>.
    /// </summary>
    public DetectionEngine? Detections { get; init; }

    /// <summary>How many of the session's latest events detection rules see inline; older steps are found only by a scan.</summary>
    public int DetectionHistory { get; init; } = 512;
}

/// <summary>
/// The structural approval pipeline (plan §10.2): parse and unwrap, extract targets, apply the hardline list, consult
/// remembered rules, ask the fabric's <c>risk</c> question about the parsed action, map score and mode to auto, ask or
/// deny, then ask the user with a deadline and record <c>ApprovalRequested</c> and <c>ApprovalResolved</c>.
/// </summary>
/// <remarks>
/// Only prompts are recorded as approval events; automatic runs and denials that asked nobody are the caller's to record
/// with the tool result. The <c>risk</c> question is a <c>Decision</c> event appended by the fabric, and the user's answer
/// is recorded as its outcome, which is the calibration data the degraded tier exists to collect.
/// </remarks>
public sealed class ApprovalPipeline
{
    private const int MaxAppendAttempts = 8;
    private static readonly ImmutableArray<ApprovalScope> AllScopes = [ApprovalScope.Once, ApprovalScope.Session, ApprovalScope.Always];
    private static readonly ImmutableArray<ApprovalScope> OnceOnly = [ApprovalScope.Once];

    private readonly DecisionFabric _fabric;
    private readonly IEventLog _log;
    private readonly IApprovalRuleStore _always;
    private readonly IApprovalPrompter? _prompter;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, ImmutableArray<ApprovalRule>> _session = new(StringComparer.Ordinal);
    // Rules already recorded per session, beside the DetectionMatched events in the inline window (C-023).
    private readonly ConcurrentDictionary<(string Session, string Rule), byte> _detected = new();

    public ApprovalPipeline(ApprovalEnvironment environment, DecisionFabric fabric, IEventLog log, IApprovalPrompter? prompter = null,
        IApprovalRuleStore? alwaysRules = null, ApprovalOptions? options = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(fabric);
        ArgumentNullException.ThrowIfNull(log);
        Environment = environment;
        _fabric = fabric;
        _log = log;
        _prompter = prompter;
        _always = alwaysRules ?? new InMemoryApprovalRuleStore();
        Options = options ?? ApprovalOptions.Default;
        if (Options.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "The approval timeout must be positive.");
        if (Options.DetectionHistory < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "The detection history must hold at least one event.");
        _time = timeProvider ?? TimeProvider.System;
    }

    public ApprovalEnvironment Environment { get; }

    public ApprovalOptions Options { get; }

    public ImmutableArray<ApprovalRule> SessionRules(string sessionId) =>
        _session.TryGetValue(sessionId, out var rules) ? rules : [];

    public ImmutableArray<ApprovalRule> AlwaysRules => _always.Rules;

    /// <summary>Parses and describes an action without deciding anything.</summary>
    public ActionAnalysis Analyze(ProposedAction action) => Resolve(action) switch
    {
        ShellAction shell => ShellAnalyzer.Analyze(shell.Command, Environment),
        ToolAction tool => ToolAnalyzer.Analyze(tool, Environment),
        null => throw new ArgumentNullException(nameof(action)),
        _ => throw new ArgumentException($"Unknown action type {action.GetType().Name}.", nameof(action)),
    };

    /// <remarks>
    /// Order (plan §10.2, C-023): the hardline list first, so nothing can relax or pre-empt its denials; then detection
    /// rules, where an enforced match is a forced reason; then forced reasons, remembered rules, <c>risk</c> and the mode.
    /// Like the fabric's <c>risk</c> <c>Decision</c>, a detection is recorded here: a <c>DetectionMatched</c> event, once per
    /// rule per session.
    /// </remarks>
    public async Task<ApprovalAssessment> AssessAsync(ApprovalContext context, ProposedAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.SessionId);
        var proposed = action;
        action = Resolve(action);
        var analysis = Analyze(action);

        var hardline = Hardline.Check(analysis, Environment);
        if (!hardline.IsEmpty)
            return new ApprovalAssessment(ApprovalOutcome.Deny, analysis, hardline, [], null, null);

        var (detections, enforced) = Detect(context, proposed, cancellationToken);
        var assessment = await DecideAsync(context, action, analysis, enforced, cancellationToken);
        return detections.IsEmpty ? assessment : assessment with { Detections = detections };
    }

    private async Task<ApprovalAssessment> DecideAsync(ApprovalContext context, ProposedAction action, ActionAnalysis analysis,
        ImmutableArray<ApprovalReason> detected, CancellationToken cancellationToken)
    {
        var forced = Forced(analysis, action, context).AddRange(detected);
        var parsed = analysis.Unsupported is null;
        // A tool declared read-only (C-005) needs no approval even when it reads the network; taint is a forced reason.
        var sideEffecting = action is ToolAction { Tool.Risk.ReadOnly: true }
            ? []
            : analysis.Commands.Where(command => !command.ReadOnly).ToList();
        if (parsed && forced.IsEmpty && sideEffecting.Count == 0)
            return new ApprovalAssessment(ApprovalOutcome.Auto, analysis, [new("read_only", "reads only")], [], null, null);

        var rules = SessionRules(context.SessionId).AddRange(_always.Rules);
        if (rules.FirstOrDefault(rule => analysis.Commands.Any(command => rule.Denies(analysis.Kind, command))) is { } denial)
            return new ApprovalAssessment(ApprovalOutcome.Deny, analysis,
                [new("rule_denied", $"a remembered denial matches: {denial.Source}")], [], denial, null);
        if (parsed && forced.IsEmpty
            && sideEffecting.All(command => rules.Any(rule => rule.Allows(analysis.Kind, command))))
        {
            var allowing = rules.First(rule => rule.Allows(analysis.Kind, sideEffecting[0]));
            return new ApprovalAssessment(ApprovalOutcome.Auto, analysis,
                [new("rule_allowed", $"a remembered approval matches: {allowing.Source}")], [], allowing, null);
        }

        var reasons = forced.ToBuilder();
        var outcome = ApprovalOutcome.Ask;
        FabricVerdict? risk = null;
        if (context.Mode == ApprovalMode.Manual || !parsed)
            reasons.Add(new("mode", parsed ? "manual mode asks before every side effect" : "the command could not be parsed"));
        else
        {
            // C-030: with reduced checkpoints (Kev on the CPU), risk is not asked where no answer could change the outcome:
            // a forced reason asks anyway, and Smart mode asks for an action it would never auto-run. It is still logged, as
            // a skipped Decision with the action ask, so the user's answer is recorded against it.
            var elide = _fabric.Reduction is not null
                && (!forced.IsEmpty || context.Mode == ApprovalMode.Smart && !SmartEligible(analysis, action));
            risk = await RiskAsync(context, analysis, elide, cancellationToken);
            var mapped = Map(context.Mode, risk.Verdict, analysis, action);
            reasons.Add(new("risk", risk.Skipped
                ? $"{risk.Verdict.Action}: risk not asked, the outcome is ask whatever it says ({_fabric.Reduction!.Reason})"
                : $"{risk.Verdict.Action}: {risk.Verdict.Reason}"));
            if (forced.IsEmpty)
                outcome = mapped;
        }

        var offered = forced.IsEmpty && parsed ? AllScopes : OnceOnly;
        return new ApprovalAssessment(outcome, analysis, reasons.ToImmutable(), outcome == ApprovalOutcome.Ask ? offered : [], null, risk);
    }

    /// <summary>Assesses the action and, when it must ask, asks the user and records the request and its resolution.</summary>
    public async Task<ApprovalResult> AuthorizeAsync(ApprovalContext context, ProposedAction action,
        CancellationToken cancellationToken = default)
    {
        var assessment = await AssessAsync(context, action, cancellationToken);
        switch (assessment.Outcome)
        {
            case ApprovalOutcome.Auto:
                return new(true, assessment.Rule is null ? ApprovalResolution.Automatic : ApprovalResolution.RuleAllowed, assessment, null, null);
            case ApprovalOutcome.Deny:
                return new(false, assessment.Hardline ? ApprovalResolution.Hardline : ApprovalResolution.RuleDenied, assessment, null, null);
        }

        if (_prompter is null || !context.Interactive)
        {
            RecordOutcome(context, assessment, "no_approver");
            return new(false, ApprovalResolution.NoApprover, assessment, null, null);
        }

        var approvalId = Guid.NewGuid().ToString("N");
        var deadline = _time.GetUtcNow() + Options.Timeout;
        Append(context.SessionId, new ApprovalRequested(approvalId,
            ActionDocuments.Json(assessment.Analysis, context.Tainted, context.Reversible), assessment.Summary, context.CallId), cancellationToken);

        var prompt = new ApprovalPrompt(approvalId, context.SessionId, assessment.Analysis, assessment.Reasons, assessment.Offered, deadline)
        {
            CallId = context.CallId,
            Action = action,
        };
        ApprovalAnswer answer;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            try
            {
                answer = await _prompter.AskAsync(prompt, timeout.Token).AsTask().WaitAsync(Options.Timeout, _time, cancellationToken);
            }
            catch (TimeoutException)
            {
                await timeout.CancelAsync();
                return Resolve(context, assessment, approvalId, ApprovalResolution.TimedOut, "timeout: unanswered after " + Options.Timeout);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The turn was cancelled: close the prompt in the log so it does not stay pending, then propagate.
                Append(context.SessionId, new ApprovalResolved(approvalId, false, "cancelled"), CancellationToken.None);
                RecordOutcome(context, assessment, "approval_cancelled");
                throw;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Resolve(context, assessment, approvalId, ApprovalResolution.Failed, "prompt failed: " + exception.Message);
            }
            catch (OperationCanceledException)
            {
                return Resolve(context, assessment, approvalId, ApprovalResolution.Failed, "prompt cancelled itself");
            }
        }

        var scope = assessment.Offered.Contains(answer.Scope) ? answer.Scope : ApprovalScope.Once;
        if (scope != ApprovalScope.Once
            && !Remember(context.SessionId, assessment.Analysis, answer.Approved ? RuleDecision.Allow : RuleDecision.Deny, scope, answer.RulePaths))
            scope = ApprovalScope.Once;
        var result = Resolve(context, assessment, approvalId, answer.Approved ? ApprovalResolution.UserApproved : ApprovalResolution.UserDenied,
            $"user: {(answer.Approved ? "approved" : "denied")} ({WireNames.Scope(scope)})");
        return result with { Scope = scope };
    }

    /// <summary>
    /// Detection rules over the session's latest events with the pending call as the next one (C-023). Returns the matches it
    /// completes and the reasons they force: an enforced match, or an enforce rule that could not be evaluated (fail closed).
    /// A detect match only records.
    /// </summary>
    private (ImmutableArray<DetectionFinding> Findings, ImmutableArray<ApprovalReason> Forced) Detect(ApprovalContext context,
        ProposedAction proposed, CancellationToken cancellationToken)
    {
        if (Options.Detections is not { } engine
            || Inline(engine, context.SessionId, context.CallId, proposed, [], cancellationToken) is not var (scan, history))
            return ([], []);

        var reasons = ImmutableArray.CreateBuilder<ApprovalReason>();
        foreach (var finding in scan.Findings.Where(finding => finding.Enforced))
            reasons.Add(new(ApprovalReasons.Detection, $"{finding.Rule.Id}: {finding.Rule.Reason}"));
        foreach (var error in scan.Errors.Where(error => engine.PostureOf(error.Rule) == RulePosture.Enforce))
            reasons.Add(new(ApprovalReasons.Detection, $"{error.Rule.Id}: could not be evaluated ({error.Message})"));

        var recorded = history.Select(stored => stored.Payload).OfType<DetectionMatched>().Select(matched => matched.RuleId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var finding in scan.Findings)
        {
            if (recorded.Contains(finding.Rule.Id) || !_detected.TryAdd((context.SessionId, finding.Rule.Id), 0))
                continue;
            Append(context.SessionId, new DetectionMatched(finding.Rule.Id, finding.Rule.Version, finding.Rule.Severity, finding.Posture,
                finding.Rule.Reason, finding.Sequences, context.CallId, finding.Enforced ? "ask" : null), cancellationToken);
        }
        return (scan.Findings, reasons.ToImmutable());
    }

    /// <summary>
    /// Whether detection rules have something to say about a call (C-023): it completes a match, enforced or not, or an enforce
    /// rule cannot be evaluated on it. The turn asks this of a read-only network call, which otherwise runs without coming
    /// here in an untainted session, so a <c>web_fetch</c> after a credential read still reaches <c>secret-read-then-egress</c>
    /// and a match is still recorded. <paramref name="preceding"/> are calls of the same response not yet logged, in order.
    /// Records nothing; false without an engine.
    /// </summary>
    public bool Detects(string sessionId, string? callId, ProposedAction action, IReadOnlyList<ToolCallIssued> preceding,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(preceding);
        if (Options.Detections is not { } engine
            || Inline(engine, sessionId, callId, action, preceding, cancellationToken) is not var (scan, _))
            return false;
        return !scan.Findings.IsEmpty || scan.Errors.Any(error => engine.PostureOf(error.Rule) == RulePosture.Enforce);
    }

    /// <summary>The engine's inline evaluation of the call over the session's latest events, then <paramref name="preceding"/>.</summary>
    private (DetectionScan Scan, List<StoredEvent> History)? Inline(DetectionEngine engine, string sessionId, string? callId,
        ProposedAction proposed, IReadOnlyList<ToolCallIssued> preceding, CancellationToken cancellationToken)
    {
        var pending = proposed switch
        {
            ShellAction shell => engine.ShellCall(callId ?? "pending", shell.Command),
            ToolAction tool => new ToolCallIssued(callId ?? "pending", tool.Tool.Name, tool.Arguments),
            _ => null,
        };
        if (pending is null)
            return null;

        var head = _log.GetHead(sessionId, cancellationToken);
        var limit = Options.DetectionHistory;
        // The turn appends the call's ToolCallIssued before it asks; the pending event stands for it, so it is not counted twice.
        var history = _log.Read(sessionId, Math.Max(0, head - limit), head, limit, cancellationToken)
            .Where(stored => callId is null || stored.Payload is not ToolCallIssued issued || issued.CallId != callId)
            .ToList();
        var now = _time.GetUtcNow();
        List<StoredEvent> evaluated = preceding.Count == 0 ? history
            : [.. history, .. preceding.Select((call, i) => new StoredEvent(sessionId, head + 1 + i, Guid.Empty, now, call))];
        var scan = engine.Inline(evaluated, new StoredEvent(sessionId, head + 1 + preceding.Count, Guid.Empty, now, pending));
        return (scan, history);
    }

    /// <summary>A call to a shell tool (<see cref="ApprovalOptions.ShellTools"/>) is the command line it runs.</summary>
    private ProposedAction Resolve(ProposedAction action)
    {
        if (action is ToolAction tool && Options.ShellTools.TryGetValue(tool.Tool.Name, out var parameter)
            && tool.Arguments.ValueKind == System.Text.Json.JsonValueKind.Object
            && tool.Arguments.TryGetProperty(parameter, out var command) && command.ValueKind == System.Text.Json.JsonValueKind.String)
            return new ShellAction(command.GetString()!);
        // Without its command the call stays a tool call: it changes files and processes with no target found, so it asks.
        return action;
    }

    private ApprovalResult Resolve(ApprovalContext context, ApprovalAssessment assessment, string approvalId, ApprovalResolution resolution,
        string reason)
    {
        var approved = resolution == ApprovalResolution.UserApproved;
        Append(context.SessionId, new ApprovalResolved(approvalId, approved, reason), CancellationToken.None);
        RecordOutcome(context, assessment, resolution switch
        {
            ApprovalResolution.UserApproved => "user_approved",
            ApprovalResolution.UserDenied => "user_denied",
            ApprovalResolution.TimedOut => "approval_timed_out",
            _ => "approval_failed",
        });
        return new ApprovalResult(approved, resolution, assessment, approvalId, null);
    }

    private void RecordOutcome(ApprovalContext context, ApprovalAssessment assessment, string outcome)
    {
        if (assessment.Risk is { } risk)
            _fabric.RecordOutcome(context.SessionId, risk.DecisionId, outcome, CancellationToken.None);
    }

    /// <summary>Stores the answer's rules, narrowed as it says; false (and nothing stored) when the narrowing is not narrower.</summary>
    private bool Remember(string sessionId, ActionAnalysis analysis, RuleDecision decision, ApprovalScope scope,
        ImmutableArray<ImmutableArray<string>> narrowed)
    {
        if (!Narrow(RulesFor(analysis, decision, _time.GetUtcNow()), narrowed, out var rules, out _))
            return false;
        if (scope == ApprovalScope.Always)
            _always.Add(rules);
        else
            _session.AddOrUpdate(sessionId, _ => rules, (_, existing) => existing.AddRange(rules));
        return true;
    }

    /// <summary>
    /// The rules a session or always answer to an action writes, one per side-effecting command (every command when none
    /// is), in this order: what a surface previews before the user confirms "always" (G-11).
    /// </summary>
    public static ImmutableArray<ApprovalRule> RulesFor(ActionAnalysis analysis, RuleDecision decision, DateTimeOffset createdAt)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var commands = analysis.Commands.Where(command => !command.ReadOnly).ToList();
        if (commands.Count == 0)
            commands = [.. analysis.Commands];
        return [.. commands.Select(command => ApprovalRule.From(decision, analysis.Kind, command, analysis.Text, createdAt))];
    }

    /// <summary>
    /// Narrows each rule's paths to the user's (G-11): <paramref name="paths"/> holds one entry per rule, in order, and an
    /// empty entry (or no entries) keeps that rule's paths. Every narrowed path must be absolute and at or under one of the
    /// rule's own paths (equal to it for a pattern); a rule without paths cannot be narrowed this way. Nothing is narrowed
    /// when any path fails, and <paramref name="error"/> says which.
    /// </summary>
    public static bool Narrow(ImmutableArray<ApprovalRule> rules, ImmutableArray<ImmutableArray<string>> paths,
        out ImmutableArray<ApprovalRule> narrowed, out string? error)
    {
        narrowed = rules;
        error = null;
        if (paths.IsDefaultOrEmpty)
            return true;
        if (paths.Length > rules.Length)
        {
            error = $"{paths.Length} rules were narrowed but the answer writes {rules.Length}.";
            return false;
        }
        var result = rules.ToBuilder();
        for (var i = 0; i < paths.Length; i++)
        {
            if (paths[i].IsDefaultOrEmpty)
                continue;
            var rule = rules[i];
            var kept = new List<string>();
            foreach (var path in paths[i])
            {
                if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                {
                    error = $"'{path}' is not an absolute path.";
                    return false;
                }
                var normal = PathMath.Normalize(path);
                if (!rule.Paths.Any(original => original.AsSpan().IndexOfAny("*?[") >= 0 ? original == normal : PathMath.IsAtOrUnder(normal, original)))
                {
                    error = rule.Paths.IsEmpty
                        ? $"the rule for '{rule.Verb}' names no paths, so it cannot be narrowed to {normal}."
                        : $"{normal} is not under {string.Join(" or ", rule.Paths)}; a rule can only be narrowed.";
                    return false;
                }
                kept.Add(normal);
            }
            result[i] = rule with { Paths = [.. kept.Distinct(StringComparer.Ordinal)] };
        }
        narrowed = result.ToImmutable();
        return true;
    }

    /// <param name="skip">Log <c>risk</c> as skipped (C-030) instead of asking it: its below-floor action, ask, applies.</param>
    private async Task<FabricVerdict> RiskAsync(ApprovalContext context, ActionAnalysis analysis, bool skip, CancellationToken cancellationToken)
    {
        var entry = _fabric.Policy.Catalogue["risk"];
        var instructions = DecisionContent.FromJson(ActionDocuments.Risk(entry.Instructions, analysis, context.Tainted, context.Reversible));
        IReadOnlySet<string>? conditions = context.Mode == ApprovalMode.Permissive
            ? new HashSet<string>(StringComparer.Ordinal) { FabricConditions.PermissiveMode }
            : null;
        IReadOnlyDictionary<string, string>? skipped = skip
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [entry.Id] = "the outcome is ask whatever risk says; " + _fabric.Reduction!.Reason,
            }
            : null;
        var result = await _fabric.DecideAsync(context.SessionId, Checkpoint.PreExecute, context.TurnState,
            [KeyValuePair.Create(entry.Id, entry.ToQuestion(instructions: instructions))], conditions, skipped, cancellationToken);
        return result[entry.Id];
    }

    /// <summary>Maps the fabric's <c>risk</c> verdict and the mode to an outcome (see <see cref="ApprovalMode"/>).</summary>
    private ApprovalOutcome Map(ApprovalMode mode, PolicyVerdict verdict, ActionAnalysis analysis, ProposedAction action)
    {
        switch (verdict.Action)
        {
            case FabricActions.AutoRun when mode == ApprovalMode.Permissive:
                return ApprovalOutcome.Auto;
            case FabricActions.ApprovalMode when mode == ApprovalMode.Smart && verdict.Acted:
            {
                var levels = _fabric.Policy.Catalogue["risk"].Options;
                var level = verdict.Option is null ? int.MaxValue : levels.IndexOf(levels.First(option => option.Name == verdict.Option));
                return level <= 1 && SmartEligible(analysis, action) ? ApprovalOutcome.Auto : ApprovalOutcome.Ask;
            }
            default:
                return ApprovalOutcome.Ask;
        }
    }

    /// <summary>
    /// Whether Smart mode may auto-run the action at all, whatever <c>risk</c> says: its paths stay in the workspace (no
    /// delete at its root), it is not a destructive tool, and it has no network, elevated or system effect. <see cref="Map"/>
    /// and the C-030 risk elision both use it, so the two cannot drift.
    /// </summary>
    private static bool SmartEligible(ActionAnalysis analysis, ProposedAction action)
    {
        var destructive = action is ToolAction { Tool.Risk.Destructive: true };
        var contained = analysis.Targets.All(target => target.Kind != TargetKind.Path
            || target.Location == PathLocation.Inside
            || target.Location == PathLocation.WorkspaceRoot && target.Access is not TargetAccess.Delete);
        var effects = analysis.Effects;
        return contained && !destructive
            && !effects.HasFlag(ActionEffects.Network) && !effects.HasFlag(ActionEffects.Elevated)
            && !effects.HasFlag(ActionEffects.System);
    }

    /// <summary>Reasons that make an action ask whatever its score, mode or remembered rules (and offer only "once").</summary>
    private ImmutableArray<ApprovalReason> Forced(ActionAnalysis analysis, ProposedAction action, ApprovalContext context)
    {
        var reasons = ImmutableArray.CreateBuilder<ApprovalReason>();
        if (analysis.Unsupported is { } unsupported)
            reasons.Add(new(ApprovalReasons.Unparsed, $"outside the parsed shell subset: {unsupported}"));
        foreach (var command in analysis.Commands)
        {
            foreach (var reason in command.Unresolved)
                reasons.Add(new(ApprovalReasons.Unresolved, reason));
            foreach (var target in command.Targets)
            {
                if (target.Dynamic || target.Kind == TargetKind.Path && target.Location is PathLocation.Unresolved)
                    reasons.Add(new(ApprovalReasons.Unresolved,
                        $"'{command.Verb}' {WireNames.Access(target.Access)} target '{target.Written ?? target.Value}' is not known before it runs"));
                Hardline.Sensitive(command, target, Environment, reasons);
            }

            if (command.Effects.HasFlag(ActionEffects.Elevated))
                reasons.Add(new(ApprovalReasons.Elevated, $"'{command.Verb}' runs as another user ({string.Join(", ", command.Via)})"));
            // Signals reach processes Corvid did not start; a PID says nothing about whose work it is.
            if (command.Effects.HasFlag(ActionEffects.Signal))
                reasons.Add(new(ApprovalReasons.Signal, $"'{command.Verb}' signals {Describe(command.Targets.Where(t => t.Kind is TargetKind.Process or TargetKind.ProcessName))}"));
            if (command.Effects.HasFlag(ActionEffects.Configuration))
                reasons.Add(new(ApprovalReasons.Configuration, $"'{command.Verb}' changes configuration"));
            // Plan §10.3: in a tainted turn, network egress and sends to new recipients ask whatever risk says.
            if (context.Tainted && command.Effects.HasFlag(ActionEffects.Network))
                reasons.Add(new(ApprovalReasons.TaintedEgress, $"'{command.Verb}' reaches the network in a turn holding outside content"));
        }

        if (context.Tainted && action is ToolAction tool && tool.Tool.Risk.SideEffects.HasFlag(Corvid.Tools.SideEffects.Messaging))
        {
            var known = context.KnownRecipients;
            var recipients = tool.Recipients;
            if (recipients.IsDefaultOrEmpty)
                reasons.Add(new(ApprovalReasons.TaintedRecipient, $"'{tool.Tool.Name}' sends to recipients Corvid cannot check"));
            else if (recipients.FirstOrDefault(recipient => known is null || !known.Contains(recipient)) is { } stranger)
                reasons.Add(new(ApprovalReasons.TaintedRecipient, $"'{tool.Tool.Name}' sends to a new recipient, {stranger}"));
        }

        return [.. reasons.Distinct()];
    }

    private static string Describe(IEnumerable<ActionTarget> targets)
    {
        var shown = targets.Select(target => target.Written ?? target.Value).ToList();
        return shown.Count == 0 ? "processes" : string.Join(", ", shown);
    }

    // Approval events do not depend on the head, so a concurrent writer only means retrying at the new head.
    private void Append(string sessionId, SessionEvent payload, CancellationToken cancellationToken)
    {
        var events = new[] { EventData.Create(payload) };
        for (var attempt = 1; ; attempt++)
        {
            var head = _log.GetHead(sessionId, cancellationToken);
            try
            {
                _log.Append(sessionId, head, events, cancellationToken);
                return;
            }
            catch (EventLogConflictException) when (attempt < MaxAppendAttempts)
            {
            }
        }
    }
}
