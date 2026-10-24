using System.Collections.Immutable;
using Corvid.App.Core.Board;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Safety;

/// <summary>
/// The Safety area in demo mode, consistent with the session demo (<see cref="DemoSessionService"/>) and Audit's
/// (<c>DemoAuditService</c>). Session rules, taint and detection rows are read from the sessions themselves, through
/// <see cref="ISessionWatcher"/>: a rule the approval card writes ("this session" or "always") is listed here as soon as
/// the session reports it, with the approval and turn that wrote it. Beside them are two always rules already in
/// corvid.json, the built-in detection pack with its one match so far (<c>credential-read-then-egress</c> v3 in cli-9f2e,
/// events 1182 and 1189, which forced an ask that was denied), and the hardline list. Nothing is written anywhere: a
/// removal or a switch changes this object only.
/// <para>
/// The match is the one Audit's demo logs as event 1190, and Safety's Open in Audit lands on it, so the pack names that
/// rule as Audit's demo does (<c>credential-read-then-egress</c> v3). The real pack calls it <c>secret-read-then-egress</c>
/// v1 (<see cref="SafetyCatalog"/>); when Corvid.App.Engine reads the pack and the log (A3), both pages take the engine's
/// name and this renaming goes.
/// </para>
/// </summary>
public sealed class DemoSafetyService : ISafetyService, IDisposable
{
    public const string BuildRuleId = "config:dotnet-build";
    public const string GhRuleId = "config:gh-pr-list";
    public const string ForcePushRuleId = "config:git-push-force";

    /// <summary>The demo's one detection match's rule, named as Audit's demo names it.</summary>
    public const string EgressRuleId = "credential-read-then-egress";

    /// <summary>The built-in pack as the demo shows it: the engine's rules, with the egress rule named as Audit's demo does.</summary>
    private static readonly ImmutableArray<DetectionRule> DemoRules =
    [
        .. SafetyCatalog.BuiltInRules.Select(rule => rule.Id == "secret-read-then-egress" ? rule with { Id = EgressRuleId, Version = 3 } : rule),
    ];

    private readonly TimeProvider _time;
    private readonly ISessionService? _sessions;
    private readonly ISessionWatcher? _watcher;
    private readonly Dictionary<string, IDisposable> _watches = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SessionView> _views = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private readonly List<SafetyRule> _configRules;
    private readonly List<SafetyDetection> _seededDetections;
    private readonly Dictionary<string, bool> _enforce = new(StringComparer.Ordinal) { [SafetyCatalog.BuiltInPack] = true };
    private ImmutableArray<SafetyRule> _sessionRules = [];
    private bool _starting;
    private bool _disposed;

    /// <param name="sessions">The sessions to read rules and taint from; without them only corvid.json's rules show.</param>
    /// <param name="watcher">How the sessions are read: their folded state, replayed and then live.</param>
    public DemoSafetyService(TimeProvider time, ISessionService? sessions = null, ISessionWatcher? watcher = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _sessions = sessions;
        _watcher = watcher;
        _configRules =
        [
            new SafetyRule(BuildRuleId, RuleDecision.Allow, RuleScope.Always, "`dotnet build` in workspace, no network")
            {
                Origin = "corvid.json", WrittenAt = Day(-5, 10, 12), Uses = 31,
            },
            new SafetyRule(GhRuleId, RuleDecision.Allow, RuleScope.Always, "`gh pr list` in workspace, network to api.github.com")
            {
                Origin = "corvid.json", WrittenAt = Day(-3, 16, 40), Uses = 4,
            },
            new SafetyRule(ForcePushRuleId, RuleDecision.Deny, RuleScope.Always, "`git push --force` anywhere")
            {
                Origin = "corvid.json", WrittenAt = Day(-12, 9, 5), Uses = 1,
            },
        ];
        _seededDetections =
        [
            new SafetyDetection(EgressRuleId, DetectionSeverity.High, DemoSessionService.MainSessionId, Day(0, 14, 3, 49), 1190, [1182, 1189])
            {
                Enforced = true,
                Outcome = "forced an ask · denied",
                Summary = "`printenv` read a credential (event 1182), then a POST to hooks.example.org would have sent data out (event 1189).",
            },
        ];
        if (_sessions is not null)
            _sessions.SessionChanged += OnSessionChanged;
    }

    public event EventHandler? Changed;

    /// <summary>The removals asked for so far, by rule id.</summary>
    public IReadOnlyCollection<string> Removed => _removed;

    /// <summary>Sessions being read now.</summary>
    public int Watching => _watches.Count;

    /// <summary>Starts reading every session the service lists. Called once by the first load.</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_sessions is null || _watcher is null || _disposed)
            return;
        // The first reading of each session is what this load returns, so it raises nothing.
        _starting = true;
        try
        {
            var listed = await _sessions.ListAsync(cancellationToken).ConfigureAwait(true);
            foreach (var summary in listed)
                await WatchAsync(summary.Id, cancellationToken).ConfigureAwait(true);
        }
        finally
        {
            _starting = false;
        }
    }

    public async Task<SafetySnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_watches.Count == 0)
            await StartAsync(cancellationToken).ConfigureAwait(true);
        var rules = _configRules.Concat(_sessionRules).Where(rule => !_removed.Contains(rule.Id)).ToImmutableArray();
        var enforce = _enforce[SafetyCatalog.BuiltInPack];
        var packs = ImmutableArray.Create(new DetectionPack(SafetyCatalog.BuiltInPack, enforce, DemoRules));
        return new SafetySnapshot(rules, packs, Detections(), SafetyCatalog.Hardline, Taint());
    }

    public Task RemoveRuleAsync(string ruleId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ruleId);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var known = _configRules.Any(rule => rule.Id == ruleId) || _sessionRules.Any(rule => rule.Id == ruleId);
        if (!known)
            return Task.FromException(new KeyNotFoundException($"No rule {ruleId}: it may have been removed elsewhere."));
        if (_removed.Add(ruleId))
            Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task SetEnforceAsync(string pack, bool enforce, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_enforce.TryGetValue(pack, out var current))
            return Task.FromException(new KeyNotFoundException($"No detection pack named {pack}."));
        if (current != enforce)
        {
            _enforce[pack] = enforce;
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_sessions is not null)
            _sessions.SessionChanged -= OnSessionChanged;
        foreach (var watch in _watches.Values)
            watch.Dispose();
        _watches.Clear();
    }

    private async Task WatchAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_watcher is null || _disposed || _watches.ContainsKey(sessionId))
            return;
        // Reserved first, so a session reported again while the watch is being made is not watched twice.
        _watches[sessionId] = Placeholder.Instance;
        try
        {
            var watch = await _watcher.WatchAsync(sessionId, view => OnView(sessionId, view), cancellationToken).ConfigureAwait(true);
            if (_disposed)
                watch.Dispose();
            else
                _watches[sessionId] = watch;
        }
        catch
        {
            _watches.Remove(sessionId);
            throw;
        }
    }

    private async void OnSessionChanged(object? sender, SessionSummary summary)
    {
        if (_watches.Count == 0 || _watches.ContainsKey(summary.Id))
            return;
        try
        {
            await WatchAsync(summary.Id, CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A session that cannot be read adds no rules; the rest still show.
        }
    }

    /// <summary>A session's state changed: its rules, taint and detections are read again, and the page told only when they differ.</summary>
    private void OnView(string sessionId, SessionView view)
    {
        var before = _views.TryGetValue(sessionId, out var old) ? old : null;
        _views[sessionId] = view;
        var rules = _views.Values.SelectMany(RulesOf).ToImmutableArray();
        var changed = !rules.SequenceEqual(_sessionRules)
                      || before is null
                      || before.Tainted != view.Tainted || before.TaintSource != view.TaintSource || before.Title != view.Title
                      || DetectionsOf(before).Count() != DetectionsOf(view).Count();
        _sessionRules = rules;
        if (changed && !_starting && !_disposed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// The rules a session's calls show: an answer of "this session" or "always" writes one (the verdict carries its words
    /// and the approval), and a later call a rule let run names it too, with the turn that wrote it. One rule per approval.
    /// </summary>
    private static IEnumerable<SafetyRule> RulesOf(SessionView view)
    {
        var rules = new Dictionary<string, SafetyRule>(StringComparer.Ordinal);
        foreach (var tool in view.Transcript.OfType<ToolEntry>())
        {
            if (tool.Verdict is not { Rule: { Length: > 0 } text } verdict)
                continue;
            var scope = verdict.Kind switch
            {
                VerdictKind.ApprovedSession or VerdictKind.SessionRule => RuleScope.Session,
                VerdictKind.ApprovedAlways or VerdictKind.AlwaysRule => RuleScope.Always,
                _ => (RuleScope?)null,
            };
            if (scope is null)
                continue;
            var written = verdict.Kind is VerdictKind.ApprovedSession or VerdictKind.ApprovedAlways;
            var id = $"{view.SessionId}:{verdict.ApprovalId ?? text}";
            rules.TryGetValue(id, out var known);
            rules[id] = (known ?? new SafetyRule(id, RuleDecision.Allow, scope.Value, text)
            {
                SessionId = view.SessionId,
                SessionTitle = view.Title,
                ApprovalId = verdict.ApprovalId,
            }) with
            {
                Turn = written ? tool.Turn : verdict.RuleTurn ?? known?.Turn,
                WrittenAt = written ? tool.StartedAt : known?.WrittenAt,
                Uses = (known?.Uses ?? 0) + 1,
            };
        }

        return rules.Values;
    }

    private static IEnumerable<DetectionEntry> DetectionsOf(SessionView view) => view.Transcript.OfType<DetectionEntry>();

    private ImmutableArray<SafetyDetection> Detections()
    {
        var all = new List<SafetyDetection>(_seededDetections);
        foreach (var view in _views.Values)
        {
            foreach (var entry in DetectionsOf(view))
            {
                var info = entry.Detection;
                var events = info.Events.Select(e => e.Sequence).ToImmutableArray();
                if (all.Any(d => d.RuleId == info.RuleId && d.SessionId == view.SessionId && d.Events.SequenceEqual(events)))
                    continue;
                all.Add(new SafetyDetection(info.RuleId, Severity(info.Severity), view.SessionId, entry.At, events.LastOrDefault(), events)
                {
                    Enforced = info.Enforced,
                    Outcome = info.Enforced ? "forced an ask" : "recorded only",
                    Summary = info.Reason,
                });
            }
        }

        return [.. all.OrderByDescending(d => d.At)];
    }

    private ImmutableArray<SessionTaint> Taint() =>
    [
        .. _views.Values
            .OrderByDescending(view => view.Tainted)
            .ThenBy(view => view.SessionId, StringComparer.Ordinal)
            .Select(view => new SessionTaint(view.SessionId, view.Title, view.Tainted)
            {
                Source = view.Tainted ? view.TaintSource : view.Transcript.OfType<TaintEntry>().LastOrDefault(t => t.Tainted)?.Source,
                Since = view.Tainted ? view.TaintedAt : view.Transcript.OfType<TaintEntry>().LastOrDefault(t => t.Tainted)?.At,
                ClearedAt = view.Tainted ? null : view.Transcript.OfType<TaintEntry>().LastOrDefault(t => !t.Tainted)?.At,
                ReadOnly = view.ReadOnly,
            }),
    ];

    private static DetectionSeverity Severity(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => DetectionSeverity.Critical,
        "high" => DetectionSeverity.High,
        "medium" => DetectionSeverity.Medium,
        _ => DetectionSeverity.Low,
    };

    /// <summary>A wall-clock time on a day relative to the demo's, in the clock's zone.</summary>
    private DateTimeOffset Day(int days, int hour, int minute, int second = 0)
    {
        var zone = _time.LocalTimeZone;
        var today = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone).Date.AddDays(days);
        var wall = new DateTime(today.Year, today.Month, today.Day, hour, minute, second, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    private sealed class Placeholder : IDisposable
    {
        public static Placeholder Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
