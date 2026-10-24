using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Corvid.Abstractions.Events;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Safety;
using Corvid.Approvals;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Rules;
using Microsoft.Extensions.DependencyInjection;
using AppRuleDecision = Corvid.App.Core.Safety.RuleDecision;
using AppDetectionRule = Corvid.App.Core.Safety.DetectionRule;
using EngineRuleDecision = Corvid.Approvals.RuleDecision;

namespace Corvid.App.Engine.Safety;

/// <summary>
/// The Safety page over the runtime (docs/plan/ui.md §4.4): the approval pipeline's remembered rules, each session's and the
/// rules file's, listed with their scope and source and removable (K-7); the built-in detection pack and its live enforce
/// switch; the matches the log holds (<c>detection_matched</c> events); the hardline list; and the taint of the sessions open
/// in this process.
/// <para>
/// What the engine does not keep is shown as unknown rather than guessed: how often a rule was used (no event records a
/// rule's allow), which approval and turn wrote it, a session's title, and the taint of a session this process has not
/// opened. Rules an approval writes after the page loaded show on its next load (Refresh); there is no rule event to follow.
/// </para>
/// </summary>
public sealed class EngineSafetyService : ISafetyService, IDisposable
{
    /// <summary>The configuration key the enforce switch writes, for the next start.</summary>
    public const string EnforceKey = "Corvid:Detections:Enforce";

    private const int Page = 4096;

    private readonly AppEngine _engine;
    private readonly IDispatcher _dispatcher;
    private readonly TimeProvider _time;
    // What each session's log has been scanned through, and the matches found so far: a load reads only what is new.
    private readonly ConcurrentDictionary<string, Scanned> _scanned = new(StringComparer.Ordinal);
    private volatile bool _disposed;

    public EngineSafetyService(AppEngine engine, IDispatcher dispatcher, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        _engine = engine;
        _dispatcher = dispatcher;
        _time = time;
    }

    public event EventHandler? Changed;

    public async Task<SafetySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        // The rules file and the log are read synchronously: off the caller's thread, which is the UI's.
        return await Task.Run(() => Snapshot(runtime, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveRuleAsync(string ruleId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ruleId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var removed = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pipeline = runtime.Approvals;
            // A rule already gone changes nothing (ISafetyService.RemoveRuleAsync).
            if (pipeline.AlwaysRules.FirstOrDefault(rule => AlwaysId(rule) == ruleId) is { } always)
                return pipeline.RemoveAlwaysRule(always);
            foreach (var (sessionId, rules) in pipeline.AllSessionRules)
                if (rules.FirstOrDefault(rule => SessionId(sessionId, rule) == ruleId) is { } session)
                    return pipeline.RemoveSessionRule(sessionId, session);
            return false;
        }, cancellationToken).ConfigureAwait(false);
        if (removed)
            RaiseChanged();
    }

    public async Task SetEnforceAsync(string pack, bool enforce, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!string.Equals(pack, SafetyCatalog.BuiltInPack, StringComparison.Ordinal))
            throw new KeyNotFoundException($"No detection pack named {pack}.");
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        if (runtime.Approvals.Options.Detections is not { } detections)
            throw new InvalidOperationException("Detections are off (Corvid:Detections:Enabled is false): turn them on in Settings, then restart Corvid.");
        if (detections.Enforce == enforce)
            return;
        var writer = _engine.Services.GetRequiredService<CorvidConfigWriter>();
        // The file first, so a refused write changes nothing; then the running engine, so the switch holds at once.
        var written = await Task.Run(() => writer.Apply(new ConfigEdit(EnforceKey, JsonValue.Create(enforce))), cancellationToken).ConfigureAwait(false);
        if (!written.Written)
            throw new InvalidOperationException("corvid.json could not be written: " + written.FailureText);
        detections.Enforce = enforce;
        RaiseChanged();
    }

    public void Dispose() => _disposed = true;

    private void RaiseChanged()
    {
        if (!_disposed)
            _dispatcher.Post(() =>
            {
                if (!_disposed)
                    Changed?.Invoke(this, EventArgs.Empty);
            });
    }

    private SafetySnapshot Snapshot(CorvidRuntime runtime, CancellationToken cancellationToken)
    {
        var pipeline = runtime.Approvals;
        var origin = Path.GetFileName(RulesFile(runtime));
        var rules = ImmutableArray.CreateBuilder<SafetyRule>();
        foreach (var rule in pipeline.AlwaysRules)
            rules.Add(Rule(rule, AlwaysId(rule), RuleScope.Always) with { Origin = origin });
        foreach (var (sessionId, sessionRules) in pipeline.AllSessionRules)
            foreach (var rule in sessionRules)
                rules.Add(Rule(rule, SessionId(sessionId, rule), RuleScope.Session) with { SessionId = sessionId });

        var sessions = runtime.Log.ListSessions(cancellationToken);
        var detections = ImmutableArray.CreateBuilder<SafetyDetection>();
        var taint = ImmutableArray.CreateBuilder<SessionTaint>();
        foreach (var id in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            detections.AddRange(Detections(runtime.Log, id, cancellationToken));
            // Taint is known only for the sessions this process holds open; a session it never opened is not listed.
            if (runtime.TryGetSession(id, out var session))
                taint.Add(new SessionTaint(id, id, session.Tainted));
        }

        return new SafetySnapshot(rules.ToImmutable(), [Pack(runtime)], [.. detections.OrderByDescending(match => match.At)],
            Hardline(), [.. taint.OrderByDescending(item => item.Tainted).ThenBy(item => item.SessionId, StringComparer.Ordinal)]);
    }

    /// <summary>The rule's words: its action in backticks, then the paths and names it covers.</summary>
    internal static SafetyRule Rule(ApprovalRule rule, string id, RuleScope scope)
    {
        var text = new StringBuilder(Code(rule.Source));
        if (!rule.Paths.IsDefaultOrEmpty)
            text.Append(rule.Decision == EngineRuleDecision.Allow ? " at or under " : " touching ").AppendJoin(", ", rule.Paths.Select(Code));
        if (!rule.Names.IsDefaultOrEmpty)
            text.Append(" for ").AppendJoin(", ", rule.Names.Select(Code));
        if (rule.Elevated)
            text.Append(", elevated");
        return new SafetyRule(id, rule.Decision == EngineRuleDecision.Allow ? AppRuleDecision.Allow : AppRuleDecision.Deny, scope, text.ToString())
        {
            WrittenAt = rule.CreatedAt,
        };
    }

    /// <summary>A rule's id while it lives: its content's hash, so the same rule is named the same on every load.</summary>
    internal static string AlwaysId(ApprovalRule rule) => "always:" + Hash(rule);

    internal static string SessionId(string sessionId, ApprovalRule rule) => $"session:{sessionId}:{Hash(rule)}";

    private static string Hash(ApprovalRule rule) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonFileApprovalRuleStore.Serialize([rule]))))[..16];

    /// <summary>Text inside backticks, its own backticks and backslashes escaped as the rule row reads them.</summary>
    private static string Code(string text) => "`" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("`", "\\`", StringComparison.Ordinal) + "`";

    private static string RulesFile(CorvidRuntime runtime) =>
        runtime.Options.Approvals.RulesFile is { Length: > 0 } file ? CorvidPaths.ExpandHome(file) : JsonFileApprovalRuleStore.DefaultPath(runtime.Approvals.Environment);

    private static DetectionPack Pack(CorvidRuntime runtime)
    {
        var engine = runtime.Approvals.Options.Detections;
        var rules = (engine?.Pack ?? RulePack.BuiltIn()).Rules.Select(rule => new AppDetectionRule(rule.Id, rule.Version, rule.Title,
            Severity(rule.Severity), rule.Posture == RulePosture.Enforce) { Category = rule.Category });
        // Off (Corvid:Detections:Enabled false), nothing is evaluated: the pack is listed as recording nothing, never as enforcing.
        return new DetectionPack(SafetyCatalog.BuiltInPack, engine?.Enforce ?? false, [.. rules]);
    }

    /// <summary>The engine's hardline codes, in its order, with the catalogue's sentence for each.</summary>
    private static ImmutableArray<HardlineCode> Hardline() =>
    [
        .. new[]
        {
            HardlineRules.DiskFormat, HardlineRules.RawDeviceWrite, HardlineRules.RecursiveDeleteOutsideWorkspace,
            HardlineRules.CredentialStoreWrite, HardlineRules.VaultAccess, HardlineRules.SafetyConfigurationChange,
        }.Select(code => SafetyCatalog.Hardline.FirstOrDefault(known => known.Code == code) ?? new HardlineCode(code, code)),
    ];

    /// <summary>The session's matches, reading only the events added since the last load.</summary>
    private IReadOnlyList<SafetyDetection> Detections(IEventLog log, string sessionId, CancellationToken cancellationToken)
    {
        var scanned = _scanned.GetOrAdd(sessionId, _ => new Scanned());
        lock (scanned)
        {
            while (true)
            {
                var events = log.Read(sessionId, scanned.Through, limit: Page, cancellationToken: cancellationToken);
                foreach (var stored in events)
                {
                    switch (stored.Payload)
                    {
                        case ApprovalRequested { CallId: { } call } requested:
                            scanned.Approvals[call] = requested.ApprovalId;
                            break;
                        case ApprovalResolved resolved:
                            scanned.Answers[resolved.ApprovalId] = resolved.Approved;
                            break;
                        case DetectionMatched matched:
                            scanned.Matches.Add((stored, matched));
                            break;
                    }
                    scanned.Through = stored.Sequence;
                }
                if (events.Count < Page)
                    break;
            }

            return [.. scanned.Matches.Select(match => Detection(sessionId, match.Stored, match.Matched, scanned))];
        }
    }

    private static SafetyDetection Detection(string sessionId, StoredEvent stored, DetectionMatched matched, Scanned scanned)
    {
        var enforced = matched.Enforced is not null;
        string outcome;
        if (!enforced)
            outcome = "recorded only";
        else if (matched.CallId is { } call && scanned.Approvals.TryGetValue(call, out var approval))
            outcome = scanned.Answers.TryGetValue(approval, out var approved) ? $"forced an ask · {(approved ? "approved" : "denied")}" : "forced an ask";
        else
            outcome = "forced an ask";
        return new SafetyDetection(matched.RuleId, Severity(matched.Severity), sessionId, stored.RecordedAt, stored.Sequence,
            matched.Sequences.IsDefault ? [] : matched.Sequences)
        {
            Enforced = enforced,
            Outcome = outcome,
            Summary = matched.Reason,
        };
    }

    private static DetectionSeverity Severity(string severity) => severity switch
    {
        RuleSeverity.Critical => DetectionSeverity.Critical,
        RuleSeverity.High => DetectionSeverity.High,
        RuleSeverity.Medium => DetectionSeverity.Medium,
        _ => DetectionSeverity.Low,
    };

    private sealed class Scanned
    {
        public long Through;

        public Dictionary<string, string> Approvals { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, bool> Answers { get; } = new(StringComparer.Ordinal);

        public List<(StoredEvent Stored, DetectionMatched Matched)> Matches { get; } = [];
    }
}
