using System.Collections.Concurrent;
using System.Collections.Immutable;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Approvals.Shell;
using Corvid.Fabric;

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
/// <param name="Risk">The <c>risk</c> decision, when the fabric was asked.</param>
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
}

/// <summary>A prompt shown to the user. <see cref="Deadline"/> is when an unanswered prompt is denied.</summary>
public sealed record ApprovalPrompt(
    string ApprovalId,
    string SessionId,
    ActionAnalysis Analysis,
    ImmutableArray<ApprovalReason> Reasons,
    ImmutableArray<ApprovalScope> Offered,
    DateTimeOffset Deadline);

/// <param name="Scope">Session or always stores a structural rule (allow or deny) for each side-effecting command.</param>
public sealed record ApprovalAnswer(bool Approved, ApprovalScope Scope = ApprovalScope.Once)
{
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
        _time = timeProvider ?? TimeProvider.System;
    }

    public ApprovalEnvironment Environment { get; }

    public ApprovalOptions Options { get; }

    public ImmutableArray<ApprovalRule> SessionRules(string sessionId) =>
        _session.TryGetValue(sessionId, out var rules) ? rules : [];

    public ImmutableArray<ApprovalRule> AlwaysRules => _always.Rules;

    /// <summary>Parses and describes an action without deciding anything.</summary>
    public ActionAnalysis Analyze(ProposedAction action) => action switch
    {
        ShellAction shell => ShellAnalyzer.Analyze(shell.Command, Environment),
        ToolAction tool => ToolAnalyzer.Analyze(tool, Environment),
        null => throw new ArgumentNullException(nameof(action)),
        _ => throw new ArgumentException($"Unknown action type {action.GetType().Name}.", nameof(action)),
    };

    public async Task<ApprovalAssessment> AssessAsync(ApprovalContext context, ProposedAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.SessionId);
        var analysis = Analyze(action);

        var hardline = Hardline.Check(analysis, Environment);
        if (!hardline.IsEmpty)
            return new ApprovalAssessment(ApprovalOutcome.Deny, analysis, hardline, [], null, null);

        var forced = Forced(analysis, action, context);
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
            risk = await RiskAsync(context, analysis, cancellationToken);
            var mapped = Map(context.Mode, risk.Verdict, analysis, action);
            reasons.Add(new("risk", $"{risk.Verdict.Action}: {risk.Verdict.Reason}"));
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
            ActionDocuments.Json(assessment.Analysis, context.Tainted, context.Reversible), assessment.Summary), cancellationToken);

        var prompt = new ApprovalPrompt(approvalId, context.SessionId, assessment.Analysis, assessment.Reasons, assessment.Offered, deadline);
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
        if (scope != ApprovalScope.Once)
            Remember(context.SessionId, assessment.Analysis, answer.Approved ? RuleDecision.Allow : RuleDecision.Deny, scope);
        var result = Resolve(context, assessment, approvalId, answer.Approved ? ApprovalResolution.UserApproved : ApprovalResolution.UserDenied,
            $"user: {(answer.Approved ? "approved" : "denied")} ({WireNames.Scope(scope)})");
        return result with { Scope = scope };
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

    private void Remember(string sessionId, ActionAnalysis analysis, RuleDecision decision, ApprovalScope scope)
    {
        var now = _time.GetUtcNow();
        var commands = analysis.Commands.Where(command => !command.ReadOnly).ToList();
        if (commands.Count == 0)
            commands = [.. analysis.Commands];
        var rules = commands.Select(command => ApprovalRule.From(decision, analysis.Kind, command, analysis.Text, now)).ToList();
        if (scope == ApprovalScope.Always)
            _always.Add(rules);
        else
            _session.AddOrUpdate(sessionId, _ => [.. rules], (_, existing) => existing.AddRange(rules));
    }

    private async Task<FabricVerdict> RiskAsync(ApprovalContext context, ActionAnalysis analysis, CancellationToken cancellationToken)
    {
        var entry = _fabric.Policy.Catalogue["risk"];
        var instructions = DecisionContent.FromJson(ActionDocuments.Risk(entry.Instructions, analysis, context.Tainted, context.Reversible));
        IReadOnlySet<string>? conditions = context.Mode == ApprovalMode.Permissive
            ? new HashSet<string>(StringComparer.Ordinal) { FabricConditions.PermissiveMode }
            : null;
        var result = await _fabric.DecideAsync(context.SessionId, Checkpoint.PreExecute, context.TurnState,
            [KeyValuePair.Create(entry.Id, entry.ToQuestion(instructions: instructions))], conditions, cancellationToken);
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
                var destructive = action is ToolAction { Tool.Risk.Destructive: true };
                var contained = analysis.Targets.All(target => target.Kind != TargetKind.Path
                    || target.Location == PathLocation.Inside
                    || target.Location == PathLocation.WorkspaceRoot && target.Access is not TargetAccess.Delete);
                var effects = analysis.Effects;
                return level <= 1 && contained && !destructive
                       && !effects.HasFlag(ActionEffects.Network) && !effects.HasFlag(ActionEffects.Elevated)
                       && !effects.HasFlag(ActionEffects.System)
                    ? ApprovalOutcome.Auto
                    : ApprovalOutcome.Ask;
            }
            default:
                return ApprovalOutcome.Ask;
        }
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
