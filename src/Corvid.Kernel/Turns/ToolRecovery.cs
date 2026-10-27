using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;

namespace Corvid.Kernel.Turns;

/// <summary>The <c>error</c> values the turn loop writes into a failed call's result. They are persisted: renaming one changes the log.</summary>
public static class ToolErrorKinds
{
    /// <summary>The call never ran: the approval was denied or pending at cancellation, or no checkpoint could be taken.</summary>
    public const string NotRun = "not run";
    public const string Cancelled = "cancelled";
    /// <summary>The arguments did not match the schema; C-011 asks the generator to repair them.</summary>
    public const string InvalidArguments = "invalid arguments";
    public const string ToolFailed = "tool failed";
}

/// <summary>A counted tool failure followed by a different, successful call of the same family (C-026).</summary>
/// <param name="FailedSequence">The failed call's <see cref="ToolCallIssued"/> sequence.</param>
/// <param name="RecoveredSequence">The recovering call's <see cref="ToolCallIssued"/> sequence.</param>
public sealed record RecoveryStep(string FailedTool, long FailedSequence, string RecoveredTool, long RecoveredSequence)
{
    public override string ToString() => $"{FailedTool} failed, then {RecoveredTool} succeeded";
}

/// <summary>What <see cref="ToolRecovery.Scan"/> found in a turn's events.</summary>
/// <param name="Calls">Tool calls issued.</param>
/// <param name="Failures">Counted failures: error results other than a denial, a cancellation or rejected arguments.</param>
/// <param name="Unrecovered">
/// Counted failures that no later successful call resolves: one of the same name, or of the same family unless the failed
/// call changes something and the later one only reads (<see cref="ToolCallIssued.ReadOnly"/>; unknown counts as a change
/// for the failure and as a read for the success).
/// </param>
/// <param name="Recoveries">
/// Counted failures followed by a different successful call of the same family, in order: the hint for a question or the
/// drafter, looser than <paramref name="Unrecovered"/> (a read after a failed write is listed but resolves nothing).
/// </param>
public sealed record ToolRecoveryScan(int Calls, int Failures, int Unrecovered, ImmutableArray<RecoveryStep> Recoveries)
{
    /// <summary>The recoveries as one line for a question or a prompt, or null when there are none.</summary>
    public string? Describe() => Recoveries.IsEmpty ? null : string.Join("; ", Recoveries.Take(3));
}

/// <summary>
/// The recovery pattern of C-026, computed deterministically from a turn's events: a tool call that failed, followed by a
/// different call of the same tool family that succeeded, is the strongest sign the turn found a procedure worth keeping.
/// The egress checkpoint and the skill learner run the same scan over the same log span, so they agree.
/// </summary>
public static class ToolRecovery
{
    // Verbs say what a tool does, not what it works on; the family is the object.
    private static readonly ImmutableHashSet<string> Verbs = ImmutableHashSet.Create(StringComparer.Ordinal,
        "read", "write", "list", "search", "get", "set", "add", "remove", "create", "delete", "update", "run", "fetch", "patch",
        "approve", "reject", "pending");

    public static ToolRecoveryScan Scan(IEnumerable<StoredEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var issued = new Dictionary<string, (ToolCallIssued Call, long Sequence)>(StringComparer.Ordinal);
        var outcomes = new List<(ToolCallIssued Call, long CallSequence, long ResultSequence, bool Succeeded, bool Counted)>();
        var calls = 0;
        foreach (var item in events)
        {
            switch (item.Payload)
            {
                // A7 review (M2): the model's own calls only. A nested call (a code run's script calling a tool) is part
                // of its parent's run: a skill replays the model's calls, so it is neither a step nor a recovery.
                case ToolCallIssued { ParentCallId: not null }:
                    break;
                case ToolCallIssued call:
                    calls++;
                    issued[call.CallId] = (call, item.Sequence);
                    break;
                case ToolResult result when issued.TryGetValue(result.CallId, out var source):
                    outcomes.Add((source.Call, source.Sequence, item.Sequence, !result.IsError, result.IsError && Counts(result.Result)));
                    break;
            }
        }

        var failures = 0;
        var unrecovered = 0;
        var recoveries = ImmutableArray.CreateBuilder<RecoveryStep>();
        foreach (var failure in outcomes.Where(outcome => outcome.Counted))
        {
            failures++;
            var later = outcomes.Where(outcome => outcome.Succeeded && Responds(outcome.Call, outcome.CallSequence, failure.Call, failure.ResultSequence)
                && SameFamily(outcome.Call.ToolName, failure.Call.ToolName)).ToArray();
            // A read after a failed change looks at the problem; it does not fix it, so it leaves the failure unrecovered.
            if (!Array.Exists(later, outcome => Resolves(outcome.Call, failure.Call)))
                unrecovered++;
            if (later.Length == 0)
                continue;
            // The same call again is a retry, not another approach.
            var different = Array.FindIndex(later, outcome => outcome.Call.ToolName != failure.Call.ToolName
                || !JsonElement.DeepEquals(outcome.Call.Arguments, failure.Call.Arguments));
            if (different >= 0)
                recoveries.Add(new RecoveryStep(failure.Call.ToolName, failure.CallSequence, later[different].Call.ToolName, later[different].CallSequence));
        }
        return new ToolRecoveryScan(calls, failures, unrecovered, recoveries.ToImmutable());
    }

    // Later means issued in a later response than the failure's, after its result was known. Calls that need approval run
    // one at a time, so a call of the failure's own response can be issued after its result: the recorded round tells them
    // apart. Events written before C-026 have no round, and only the order is left.
    private static bool Responds(ToolCallIssued call, long callSequence, ToolCallIssued failed, long failedResultSequence) =>
        callSequence > failedResultSequence && (call.Round is not { } round || failed.Round is not { } failedRound || round > failedRound);

    private static bool Resolves(ToolCallIssued call, ToolCallIssued failed) =>
        call.ToolName == failed.ToolName || failed.ReadOnly == true || call.ReadOnly == false;

    /// <summary>
    /// A tool's family: an MCP <c>server__tool</c> name maps to its server; otherwise the name's <c>_</c>-separated tokens,
    /// lower-cased, with a trailing <c>s</c> trimmed and the verbs removed. A name made only of verbs is its own family.
    /// </summary>
    public static ImmutableArray<string> Family(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var server = name.IndexOf("__", StringComparison.Ordinal);
        if (server > 0)
            return [name[..server].ToLowerInvariant()];
        var tokens = name.ToLowerInvariant().Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(token => token.Length > 3 && token[^1] == 's' && token[^2] != 's' ? token[..^1] : token)
            .Where(token => !Verbs.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        return tokens.IsEmpty ? [name.ToLowerInvariant()] : tokens;
    }

    /// <summary>Two tools are in one family when their names are equal or their families share a token.</summary>
    public static bool SameFamily(string first, string second) =>
        string.Equals(first, second, StringComparison.Ordinal) || Family(first).Intersect(Family(second), StringComparer.Ordinal).Any();

    // Denials, cancellations and schema rejections are not the procedure failing: the call never ran, or C-011's repair
    // handles the arguments. Any other error result counts, including a tool's own error without the kernel's shape.
    private static bool Counts(JsonElement result) =>
        !(result.ValueKind == JsonValueKind.Object && result.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
          && error.GetString() is ToolErrorKinds.NotRun or ToolErrorKinds.Cancelled or ToolErrorKinds.InvalidArguments);
}
