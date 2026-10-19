using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Evals.Calibration;
using Corvid.Kernel.Events;

namespace Corvid.Evals.Tests;

/// <summary>A synthetic event log in its own temporary directory, removed with its WAL files on dispose.</summary>
internal sealed class SyntheticLog : IDisposable
{
    public SyntheticLog()
    {
        Root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "corvid-calibration-" + Guid.NewGuid().ToString("N"));
        DatabasePath = Path.Combine(Root, "events.db");
        Log = new SqliteEventLog(DatabasePath);
    }

    public string Root { get; }

    public string DatabasePath { get; }

    public SqliteEventLog Log { get; }

    public SyntheticLog Add(string session, params SessionEvent[] events)
    {
        Log.Append(session, Log.GetHead(session), events.Select(EventData.Create).ToArray());
        return this;
    }

    public DecisionSet Load(OutcomeClassifier? classifier = null) => DecisionSet.Load([DatabasePath], classifier: classifier);

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

/// <summary>Builds Decision events the way the fabric records them.</summary>
internal static class Decisions
{
    public const string Kev = "kev-0.8b";
    public const string Degraded = "degraded";
    public const string FloorsVersion = "2026-10-18.1";

    private static int _next;

    public static string NewId() => "d-" + Interlocked.Increment(ref _next).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static ImmutableDictionary<string, double> Map(params (string Option, double P)[] pairs) =>
        pairs.ToImmutableDictionary(pair => pair.Option, pair => pair.P, StringComparer.Ordinal);

    /// <summary>A Noul decision acted on when P(yes) clears the threshold, as the policy decides it.</summary>
    public static Decision Noul(string key, double yes, double threshold, string act, string below, string decider = Kev,
        string? id = null) =>
        new(id ?? NewId(), key, Map(("yes", yes), ("no", 1 - yes)), Math.Abs(2 * yes - 1), threshold,
            yes >= threshold ? act : below, FloorsVersion, decider);

    public static Decision Done(double yes, string decider = Kev, string? id = null) =>
        Noul("done", yes, 0.8, "stop", "continue", decider, id);

    /// <summary>A memory_op decision with the chosen option at <paramref name="p"/> and the rest spread evenly.</summary>
    public static Decision MemoryOp(double p, string decider = Kev, double threshold = 0.6, string? id = null)
    {
        string[] options = ["add", "update", "invalidate", "noop"];
        var rest = (1 - p) / 3;
        var confidence = (p - 0.25) / 0.75;
        var acted = decider == Kev && confidence >= threshold;
        return new(id ?? NewId(), "memory_op", Map([.. options.Select((option, i) => (option, i == 0 ? p : rest))]), confidence,
            threshold, acted ? "apply:add" : "stage_for_review", FloorsVersion, decider);
    }

    public static SessionEvent[] WithOutcome(Decision decision, string outcome) =>
        [decision, new DecisionOutcomeRecorded(decision.DecisionId, outcome)];

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

/// <summary>A calibrated decider that answers every question with fixed probabilities: the first option at <c>top</c>.</summary>
internal sealed class FixedDecider(string name, double top) : IDecider
{
    public string Name => name;

    public ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        var answers = request.Questions.ToImmutableDictionary(pair => pair.Key, pair =>
        {
            var count = pair.Value.Options.Length;
            return DecisionAnswer.For(pair.Value, pair.Value.Options.Select((_, i) => i == 0 ? top : (1 - top) / (count - 1)));
        }, StringComparer.Ordinal);
        return ValueTask.FromResult<DeciderResult>(new DeciderAnswers(name, answers));
    }
}
