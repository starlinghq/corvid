using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Fabric;
using Corvid.Kernel.Events;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Skills;

/// <summary>What a replay or eval turn did, against what was expected.</summary>
/// <param name="Expected">The tool names expected, in order.</param>
/// <param name="Actual">The tool names the turn called, in order.</param>
public sealed partial record ReplayOutcome(bool Passed, ImmutableArray<string> Expected, ImmutableArray<string> Actual, string Status, string Detail)
{
    public string Describe() =>
        $"{(Passed ? "passed" : "failed")}: expected [{string.Join(", ", Expected)}], got [{string.Join(", ", Actual)}], turn {Status}" +
        (Detail.Length > 0 ? $"; {Detail}" : "");

    /// <summary>
    /// Reads back what <see cref="Describe"/> wrote (a staged draft and an eval record keep only that line), or null when the
    /// text is not in its form. Tool names are identifiers, so the lists split on their commas.
    /// </summary>
    public static ReplayOutcome? Parse(string? described)
    {
        if (described is null || DescribedPattern().Match(described) is not { Success: true } match)
            return null;
        static ImmutableArray<string> Names(string list) => [.. list.Split(", ", StringSplitOptions.RemoveEmptyEntries)];
        return new ReplayOutcome(match.Groups["verdict"].Value == "passed", Names(match.Groups["expected"].Value), Names(match.Groups["actual"].Value),
            match.Groups["status"].Value, match.Groups["detail"].Value);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\A(?<verdict>passed|failed): expected \[(?<expected>[^\]]*)\], got \[(?<actual>[^\]]*)\], turn (?<status>[^;\s]+)(?:; (?<detail>.*))?\z",
        System.Text.RegularExpressions.RegexOptions.Singleline)]
    private static partial System.Text.RegularExpressions.Regex DescribedPattern();
}

/// <summary>What a replay or eval turn runs on: the models, a fabric over its own log, the live tools' descriptors.</summary>
public sealed record SkillReplayServices
{
    /// <summary>The generator (and text embedder) the turn uses; the host binds them at background priority.</summary>
    public required ModelSlotRegistry Registry { get; init; }

    /// <summary>A fabric over the replay's own log (the degraded decider, until Kev).</summary>
    public required Func<IEventLog, DecisionFabric> Fabric { get; init; }

    /// <summary>The live tools: only their names, descriptions and schemas are used; none of them runs.</summary>
    public required ToolRegistry Tools { get; init; }

    public TurnOptions Turn { get; init; } = TurnOptions.Default;

    /// <summary>Where replay logs are created (and removed); default the temporary directory, which honours TMPDIR.</summary>
    public string? TemporaryDirectory { get; init; }
}

/// <summary>
/// The learning loop's test (plan §11, step 3) and skill evals. A replay copies the source session's log up to the
/// source turn into a fresh SQLite log (as C-009's replay harness does), runs the turn again with the draft skill's body
/// loaded, and checks that the agent calls the same tools in the same order and answers. Tools never run: each is replaced
/// by a read-only stand-in with the same name and schema that returns the result recorded in the log for that call, so a
/// replay has no side effects, needs no approval and touches no network. A call the source turn never made gets an error
/// and fails the test. An eval runs its prompt on an empty log with stand-ins that return a placeholder, and checks that
/// the calls begin with the expected tools.
/// </summary>
public sealed class SkillReplayer(SkillReplayServices services)
{
    private const string ReplaySession = "skill-replay";

    private readonly SkillReplayServices _services = services ?? throw new ArgumentNullException(nameof(services));

    /// <summary>Replays the turn whose events are (<paramref name="from"/> … <paramref name="through"/>) in <paramref name="source"/>.</summary>
    public async Task<ReplayOutcome> ReplayAsync(IEventLog source, string sessionId, long from, long through, SkillDocument draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(draft);
        var prefix = ReadAll(source, sessionId, 0, from - 1, cancellationToken);
        var turn = ReadAll(source, sessionId, from - 1, through, cancellationToken);
        if (turn.FirstOrDefault()?.Payload is not UserMessage request)
            return new ReplayOutcome(false, [], [], "not_run", $"event {from} is not a user message");

        // The source turn's successful calls, in order, with their recorded results. A7 review (M2): the model's own calls
        // only; a nested call (a code run's script calling a tool) is part of its parent's run, which the replay's stand-in
        // returns whole, so the replayed model never makes it.
        var results = turn.Select(item => item.Payload).OfType<ToolResult>().ToDictionary(result => result.CallId, StringComparer.Ordinal);
        var recorded = turn.Select(item => item.Payload).OfType<ToolCallIssued>()
            .Where(call => call.ParentCallId is null && results.TryGetValue(call.CallId, out var result) && !result.IsError)
            .Select(call => (call.ToolName, Result: results[call.CallId].Result)).ToList();
        var expected = recorded.Select(call => call.ToolName).ToImmutableArray();
        var queues = recorded.GroupBy(call => call.ToolName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => new ConcurrentQueue<JsonElement>(group.Select(call => call.Result)), StringComparer.Ordinal);

        return await RunAsync(prefix.Select(item => item.Payload).ToList(), request.Text, draft, expected,
            name => queues.TryGetValue(name, out var queue) && queue.TryDequeue(out var result)
                ? result
                : throw new InvalidOperationException($"the recorded turn has no further call to {name}"),
            prefixOnly: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Runs one eval case with <paramref name="skill"/> loaded.</summary>
    public Task<ReplayOutcome> EvalAsync(SkillDocument skill, SkillEvalCase evalCase, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skill);
        ArgumentNullException.ThrowIfNull(evalCase);
        var placeholder = JsonSerializer.SerializeToElement("[skill eval: tools do not run; continue as if this step succeeded]",
            SkillsJsonContext.Default.String);
        return RunAsync([], evalCase.Prompt, skill, evalCase.ExpectedTools, _ => placeholder, prefixOnly: true, cancellationToken);
    }

    private async Task<ReplayOutcome> RunAsync(IReadOnlyList<SessionEvent> prefix, string request, SkillDocument skill,
        ImmutableArray<string> expected, Func<string, JsonElement> results, bool prefixOnly, CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetFullPath(_services.TemporaryDirectory ?? Path.GetTempPath()), "corvid-skill-replay-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new SqliteEventLog(Path.Combine(root, "events.db"));
            if (prefix.Count > 0)
                log.Append(ReplaySession, 0, [.. prefix.Select(EventData.Create)], cancellationToken);
            var tools = new ToolRegistry();
            foreach (var descriptor in _services.Tools.List().Where(descriptor => !descriptor.HasSecretParameters))
                tools.Add(new StandIn(descriptor, results));
            var slots = new SessionModelSlots(_services.Registry, log, ReplaySession);
            var runner = new TurnRunner(ReplaySession, new TurnServices
            {
                Log = log,
                Slots = slots,
                Fabric = _services.Fabric(log),
                Tools = tools,
                Skills = new SingleSkill(skill),
                Options = _services.Turn with
                {
                    PinnedSkill = skill.Name,
                    Interactive = false,
                    EscalationAllowed = false,
                    AskDeliver = false,
                },
            });
            var result = await runner.RunAsync(new Envelope(ReplaySession, "skill-replay", request, EnvelopeOrigin.User), cancellationToken)
                .ConfigureAwait(false);
            var events = ReadAll(log, ReplaySession, prefix.Count, long.MaxValue, cancellationToken).Select(item => item.Payload).ToList();
            var outcomes = events.OfType<ToolResult>().ToDictionary(item => item.CallId, StringComparer.Ordinal);
            var calls = events.OfType<ToolCallIssued>().Where(call => call.ParentCallId is null).ToList();
            var actual = calls.Where(call => outcomes.TryGetValue(call.CallId, out var outcome) && !outcome.IsError).Select(call => call.ToolName).ToImmutableArray();
            var errors = calls.Count(call => !outcomes.TryGetValue(call.CallId, out var outcome) || outcome.IsError);
            var status = TurnRunner.StatusName(result.Status);
            var sequenceMatches = prefixOnly ? actual.Take(expected.Length).SequenceEqual(expected, StringComparer.Ordinal) && actual.Length >= expected.Length
                : actual.SequenceEqual(expected, StringComparer.Ordinal);
            var passed = sequenceMatches && result.Status == TurnStatus.Answered && (prefixOnly || errors == 0);
            var detail = errors > 0 ? $"{errors} call(s) failed or were not in the recorded turn" : "";
            return new ReplayOutcome(passed, expected, actual, status, detail);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static List<StoredEvent> ReadAll(IEventLog log, string sessionId, long after, long through, CancellationToken cancellationToken)
    {
        var events = new List<StoredEvent>();
        while (after < through)
        {
            var page = log.Read(sessionId, after, through, 1_024, cancellationToken);
            if (page.Count == 0)
                break;
            events.AddRange(page);
            after = page[^1].Sequence;
        }
        return events;
    }

    /// <summary>A tool with a live tool's name and schema that returns a recorded result. Read-only: it changes nothing.</summary>
    private sealed class StandIn(ToolDescriptor live, Func<string, JsonElement> results)
        : ToolFunction(new ToolDescriptor(live.Name, live.Description, live.JsonSchema.GetRawText(),
            new ToolRisk(SideEffects.None, Network: false, Destructive: false), live.Parameters))
    {
        protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(results(Name));
    }

    private sealed class SingleSkill(SkillDocument skill) : ISkillSource
    {
        public IReadOnlyList<SkillOption> Available(IReadOnlyCollection<string> tools) =>
            [new SkillOption(skill.Name, skill.Description, skill.Examples, Trusted: true)];

        public SkillBody? Load(string name) => name == skill.Name ? new SkillBody(skill.Name, skill.Body, true, []) : null;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class SkillsJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
