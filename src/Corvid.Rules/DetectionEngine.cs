using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Corvid.Abstractions.Events;

namespace Corvid.Rules;

/// <summary>A rule that matched in one session.</summary>
/// <param name="Sequences">The logged events that matched, ascending. The pending call (inline) is not among them.</param>
/// <param name="Posture">The effective posture: the rule's, or <c>detect</c> when enforcement is off.</param>
/// <param name="Pending">Inline: the pending tool call took part in the match.</param>
public sealed record DetectionFinding(DetectionRule Rule, string SessionId, ImmutableArray<long> Sequences, string Posture, bool Pending = false)
{
    public bool Enforced => Posture == RulePosture.Enforce;
}

/// <summary>A rule that could not be evaluated (a regex timed out, or a correlation exceeded its bounds): neither a match nor not.</summary>
public sealed record DetectionError(DetectionRule Rule, string SessionId, string Message);

public sealed record DetectionScan(ImmutableArray<DetectionFinding> Findings, ImmutableArray<DetectionError> Errors)
{
    public static DetectionScan None { get; } = new([], []);
}

/// <summary>The outcome of one embedded test.</summary>
public sealed record RuleTestResult(DetectionRule Rule, RuleTest Test, bool Matched, string? Error)
{
    public bool Passed => Error is null && Matched == Test.ExpectMatch;
}

/// <summary>
/// Evaluates a <see cref="RulePack"/> over session events (C-023): post-hoc over a whole log (<see cref="Scan"/>), or inline
/// at pre_execute with the pending tool call as a hypothetical next event (<see cref="Inline"/>). Rules see each event's
/// persisted JSON form plus fields derived from the session's own earlier events. A rule matches at most once per session.
/// </summary>
public sealed class DetectionEngine
{
    /// <summary>Candidates kept per correlation step; more are ignored (oldest kept).</summary>
    public const int MaxCandidates = 4096;

    /// <summary>Sequences listed in one single-event finding.</summary>
    public const int MaxSequences = 64;

    /// <summary>The built-in <c>shell</c> tool's command parameter.</summary>
    public static IReadOnlyDictionary<string, string> DefaultShellTools { get; } =
        ImmutableDictionary.CreateRange(StringComparer.Ordinal, [KeyValuePair.Create("shell", "command")]);

    private readonly IReadOnlyDictionary<string, string> _shellTools;

    /// <param name="shellTools">Tools whose call runs a shell command line, with the parameter that holds it.</param>
    /// <param name="enforce">False treats every enforce rule as detect.</param>
    public DetectionEngine(RulePack pack, IReadOnlyDictionary<string, string>? shellTools = null, bool enforce = true)
    {
        ArgumentNullException.ThrowIfNull(pack);
        Pack = pack;
        _shellTools = shellTools ?? DefaultShellTools;
        Enforce = enforce;
    }

    public RulePack Pack { get; }

    public bool Enforce { get; }

    public string PostureOf(DetectionRule rule) => Enforce ? rule.Posture : RulePosture.Detect;

    /// <summary>A shell command as a tool call, for an action that is only a command line (a job's pre-run script).</summary>
    public ToolCallIssued ShellCall(string callId, string command)
    {
        var (tool, parameter) = _shellTools.FirstOrDefault();
        tool ??= "shell";
        parameter ??= "command";
        var arguments = new JsonObjectBuilder().Add(parameter, command).Build();
        return new ToolCallIssued(callId, tool, arguments);
    }

    /// <summary>Every rule over every session in <paramref name="events"/> (grouped by session, in sequence order).</summary>
    public DetectionScan Scan(IReadOnlyList<StoredEvent> events) => Scan(events, Pack.Rules);

    internal DetectionScan Scan(IReadOnlyList<StoredEvent> events, IReadOnlyList<DetectionRule> rules)
    {
        ArgumentNullException.ThrowIfNull(events);
        var findings = ImmutableArray.CreateBuilder<DetectionFinding>();
        var errors = ImmutableArray.CreateBuilder<DetectionError>();
        foreach (var session in events.GroupBy(stored => stored.SessionId, StringComparer.Ordinal))
            Evaluate(session.Key, [.. session.OrderBy(stored => stored.Sequence)], pending: false, findings, errors, rules);
        return new DetectionScan(findings.ToImmutable(), errors.ToImmutable());
    }

    /// <summary>
    /// The matches <paramref name="pending"/> takes part in when it follows <paramref name="history"/>: single-event rules on
    /// it, and correlations where it fills a step and every other step is in the history within the window.
    /// </summary>
    public DetectionScan Inline(IReadOnlyList<StoredEvent> history, StoredEvent pending)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(pending);
        var findings = ImmutableArray.CreateBuilder<DetectionFinding>();
        var errors = ImmutableArray.CreateBuilder<DetectionError>();
        List<StoredEvent> events = [.. history.Where(stored => stored.SessionId == pending.SessionId).OrderBy(stored => stored.Sequence), pending];
        Evaluate(pending.SessionId, events, pending: true, findings, errors);
        return new DetectionScan(findings.ToImmutable(), errors.ToImmutable());
    }

    /// <summary>Runs a rule's embedded tests: each test's events are scanned and the rule must match exactly when expected.</summary>
    public ImmutableArray<RuleTestResult> RunTests(DetectionRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var engine = new DetectionEngine(RulePack.Empty, _shellTools, Enforce);
        return [.. rule.Tests.Select(test =>
        {
            var findings = ImmutableArray.CreateBuilder<DetectionFinding>();
            var errors = ImmutableArray.CreateBuilder<DetectionError>();
            foreach (var session in test.Events.GroupBy(stored => stored.SessionId, StringComparer.Ordinal))
                engine.Evaluate(session.Key, [.. session], pending: false, findings, errors, [rule]);
            return new RuleTestResult(rule, test, findings.Count > 0, errors.Count > 0 ? errors[0].Message : null);
        })];
    }

    private void Evaluate(string sessionId, List<StoredEvent> events, bool pending, ImmutableArray<DetectionFinding>.Builder findings,
        ImmutableArray<DetectionError>.Builder errors, IReadOnlyList<DetectionRule>? only = null)
    {
        if (events.Count == 0)
            return;
        var session = new SessionEvents(events, Derive(events));
        var last = pending ? events.Count - 1 : -1;
        foreach (var rule in only ?? Pack.Rules)
        {
            try
            {
                var matched = rule.Correlation is { } correlation ? Correlate(correlation, session, last) : Single(rule.Match!, session, last);
                if (matched is not null)
                {
                    var sequences = matched.Where(index => index != last).Select(index => events[index].Sequence).Order().ToImmutableArray();
                    findings.Add(new DetectionFinding(rule, sessionId, sequences, PostureOf(rule), last >= 0 && matched.Contains(last)));
                }
            }
            catch (RegexMatchTimeoutException exception)
            {
                errors.Add(new DetectionError(rule, sessionId, $"a regex timed out after {exception.MatchTimeout.TotalMilliseconds} ms"));
            }
        }
    }

    private static List<int>? Single(RuleStep step, SessionEvents session, int pending)
    {
        if (pending >= 0)
            return session.Matches(step, pending) ? [pending] : null;
        var matched = new List<int>();
        for (var i = 0; i < session.Count && matched.Count < MaxSequences; i++)
        {
            if (session.Matches(step, i))
                matched.Add(i);
        }
        return matched.Count > 0 ? matched : null;
    }

    private static List<int>? Correlate(RuleCorrelation correlation, SessionEvents session, int pending)
    {
        var steps = correlation.Steps;
        // Inline: the pending event must fill a step, or this rule has nothing new to say about it.
        var pendingSteps = pending >= 0 ? Enumerable.Range(0, steps.Length).Where(s => session.Matches(steps[s], pending)).ToList() : null;
        if (pendingSteps is { Count: 0 })
            return null;

        var candidates = steps.Select(step =>
        {
            var list = new List<int>();
            for (var i = 0; i < session.Count && list.Count < MaxCandidates; i++)
            {
                if (i != pending && session.Matches(step, i))
                    list.Add(i);
            }
            return list;
        }).ToArray();

        if (pendingSteps is null)
            return correlation.Sequence ? Sequence(correlation, session, candidates) : AnyOrder(correlation, session, candidates);

        // The pending event is the newest, so in a sequence it can only be the last step.
        foreach (var forced in pendingSteps)
        {
            if (correlation.Sequence && forced != steps.Length - 1)
                continue;
            var restricted = candidates.Select((list, s) => s == forced ? [pending] : list).ToArray();
            var found = correlation.Sequence ? Sequence(correlation, session, restricted) : AnyOrder(correlation, session, restricted);
            if (found is not null)
                return found;
        }
        return null;
    }

    // Ordered steps: for each anchor of the first step, the earliest later candidate of each next step. Earliest is never
    // worse for the steps after it or for the window, so no other choice needs trying.
    private static List<int>? Sequence(RuleCorrelation correlation, SessionEvents session, List<int>[] candidates)
    {
        foreach (var anchor in candidates[0])
        {
            var chain = new List<int> { anchor };
            var previous = anchor;
            for (var s = 1; s < candidates.Length; s++)
            {
                var next = candidates[s].FindIndex(index => index > previous);
                if (next < 0)
                    return null;
                previous = candidates[s][next];
                if (session.Time(previous) - session.Time(anchor) > correlation.Window)
                    break;
                chain.Add(previous);
            }
            if (chain.Count == candidates.Length)
                return chain;
        }
        return null;
    }

    // Any order: each event in turn is the earliest; it takes a step it satisfies, and the other steps need distinct events
    // after it within the window (a bipartite matching). With n steps, n candidates per step are always enough.
    private static List<int>? AnyOrder(RuleCorrelation correlation, SessionEvents session, List<int>[] candidates)
    {
        var steps = candidates.Length;
        foreach (var anchor in candidates.SelectMany(list => list).Distinct().Order())
        {
            var end = session.Time(anchor) + correlation.Window;
            for (var s = 0; s < steps; s++)
            {
                if (!candidates[s].Contains(anchor))
                    continue;
                var lists = new List<int>[steps];
                for (var other = 0; other < steps; other++)
                {
                    lists[other] = other == s
                        ? [anchor]
                        : [.. candidates[other].Where(index => index > anchor && session.Time(index) <= end).Take(steps)];
                }
                if (Assign(lists) is { } assigned)
                    return assigned;
            }
        }
        return null;
    }

    private static List<int>? Assign(List<int>[] lists)
    {
        var owner = new Dictionary<int, int>();
        for (var s = 0; s < lists.Length; s++)
        {
            if (!Augment(s, lists, owner, []))
                return null;
        }
        return [.. owner.Keys];
    }

    private static bool Augment(int step, List<int>[] lists, Dictionary<int, int> owner, HashSet<int> visited)
    {
        foreach (var candidate in lists[step])
        {
            if (!visited.Add(candidate))
                continue;
            if (!owner.TryGetValue(candidate, out var holder) || Augment(holder, lists, owner, visited))
            {
                owner[candidate] = step;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// The derived fields: <c>command</c> for a call to a shell tool; on a tool result, the call's <c>tool_name</c> and
    /// <c>command</c>; on an approval resolution, the <c>call_id</c> its request named, that call's <c>tool_name</c> and
    /// <c>command</c> (or the command line from the request's action, for a job script), and the request's <c>action</c>.
    /// </summary>
    private List<IReadOnlyDictionary<string, string>> Derive(List<StoredEvent> events)
    {
        var calls = new Dictionary<string, (string Tool, string? Command)>(StringComparer.Ordinal);
        var requests = new Dictionary<string, ApprovalRequested>(StringComparer.Ordinal);
        var derived = new List<IReadOnlyDictionary<string, string>>(events.Count);
        foreach (var stored in events)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            switch (stored.Payload)
            {
                case ToolCallIssued call:
                    var command = ShellCommand(call.ToolName, call.Arguments);
                    calls[call.CallId] = (call.ToolName, command);
                    Set(fields, EventFields.Command, command);
                    break;
                case ToolResult result when calls.TryGetValue(result.CallId, out var issued):
                    fields[EventFields.ToolName] = issued.Tool;
                    Set(fields, EventFields.Command, issued.Command);
                    break;
                case ApprovalRequested request:
                    requests[request.ApprovalId] = request;
                    break;
                case ApprovalResolved resolved when requests.TryGetValue(resolved.ApprovalId, out var request):
                    fields[EventFields.Action] = request.Action;
                    Set(fields, EventFields.CallId, request.CallId);
                    if (request.CallId is { } callId && calls.TryGetValue(callId, out var asked))
                    {
                        fields[EventFields.ToolName] = asked.Tool;
                        Set(fields, EventFields.Command, asked.Command);
                    }
                    else
                        Set(fields, EventFields.Command, ActionCommand(request.Action));
                    break;
            }
            derived.Add(fields);
        }
        return derived;
    }

    private static void Set(Dictionary<string, string> fields, string name, string? value)
    {
        if (value is not null)
            fields[name] = value;
    }

    private string? ShellCommand(string tool, JsonElement arguments) =>
        _shellTools.TryGetValue(tool, out var parameter) && arguments.ValueKind == JsonValueKind.Object
        && arguments.TryGetProperty(parameter, out var command) && command.ValueKind == JsonValueKind.String
            ? command.GetString()
            : null;

    // An approval's action is the approval pipeline's action document: {"kind":"shell","text":"<command line>",...}.
    private static string? ActionCommand(string action)
    {
        try
        {
            using var document = JsonDocument.Parse(action);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && root.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.ValueEquals("shell")
                   && root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
                ? text.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>One session's events with their names, derived fields and (on demand) persisted JSON.</summary>
    private sealed class SessionEvents(List<StoredEvent> events, List<IReadOnlyDictionary<string, string>> derived)
    {
        private readonly string?[] _names = new string?[events.Count];
        private readonly EventView?[] _views = new EventView?[events.Count];

        public int Count => events.Count;

        public DateTimeOffset Time(int index) => events[index].RecordedAt;

        public bool Matches(RuleStep step, int index) => Name(index) == step.Event && step.Where.Evaluate(View(index));

        private string Name(int index) => _names[index] ??= EventFields.NameOf(events[index].Payload);

        private EventView View(int index) => _views[index] ??= new EventView(Name(index),
            JsonSerializer.SerializeToElement(events[index].Payload, RuleEventJsonContext.Default.SessionEvent), derived[index]);
    }
}

/// <summary>Builds a one-level JSON object of strings without reflection.</summary>
internal sealed class JsonObjectBuilder
{
    private readonly List<KeyValuePair<string, string>> _values = [];

    public JsonObjectBuilder Add(string name, string value)
    {
        _values.Add(KeyValuePair.Create(name, value));
        return this;
    }

    public JsonElement Build()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in _values)
                writer.WriteString(name, value);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}
