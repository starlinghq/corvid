using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;

namespace Corvid.Evals;

/// <summary>A trajectory loaded into a real event log, ready to be projected at any position.</summary>
/// <remarks>Owns a temporary SQLite database unless the caller supplies a log; disposal removes it.</remarks>
public sealed class ReplaySession : IDisposable
{
    private readonly string? _root;

    private ReplaySession(IEventLog log, string sessionId, long head, string? root)
    {
        Log = log;
        SessionId = sessionId;
        Head = head;
        _root = root;
    }

    public IEventLog Log { get; }

    public string SessionId { get; }

    public long Head { get; }

    /// <summary>The temporary database this session owns, or null when the caller supplied the log.</summary>
    public string? DatabasePath => _root is null ? null : Path.Combine(_root, "events.db");

    /// <summary>Appends the trajectory to a fresh SQLite log under the temporary directory (honours TMPDIR).</summary>
    public static ReplaySession Create(GoldenTrajectory trajectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trajectory);
        var root = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "corvid-replay-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new SqliteEventLog(Path.Combine(root, "events.db"));
            return new(log, trajectory.Id, Append(log, trajectory.Id, trajectory, cancellationToken), root);
        }
        catch
        {
            Delete(root);
            throw;
        }
    }

    /// <summary>Appends the trajectory to a caller-owned log. The session must be empty.</summary>
    public static ReplaySession Create(GoldenTrajectory trajectory, IEventLog log, string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trajectory);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return new(log, sessionId, Append(log, sessionId, trajectory, cancellationToken), null);
    }

    public SessionSnapshot Project(long throughSequence, bool expandCompactions = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(throughSequence);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(throughSequence, Head);
        return SessionProjection.Load(Log, SessionId, throughSequence, expandCompactions, cancellationToken);
    }

    public void Dispose()
    {
        if (_root is not null)
            Delete(_root);
    }

    private static long Append(IEventLog log, string sessionId, GoldenTrajectory trajectory, CancellationToken cancellationToken)
    {
        if (trajectory.Events.IsEmpty)
            return 0;
        // One batch at head zero: a session that already has history fails with a conflict instead of mixing logs.
        var stored = log.Append(sessionId, 0, [.. trajectory.Events.Select(EventData.Create)], cancellationToken);
        return stored[^1].Sequence;
    }

    private static void Delete(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }
}

public sealed record CheckResult(long Through, bool ExpandCompactions, ImmutableArray<string> Differences)
{
    public bool Passed => Differences.IsEmpty;
}

public sealed record ReplayReport(string TrajectoryId, ImmutableArray<CheckResult> Checks, ImmutableArray<string> Failures)
{
    public bool Passed => Failures.IsEmpty && Checks.All(check => check.Passed);

    public string Describe()
    {
        var lines = new List<string> { $"{TrajectoryId}: {(Passed ? "passed" : "FAILED")}" };
        lines.AddRange(Failures.Select(failure => "  " + failure));
        foreach (var check in Checks.Where(check => !check.Passed))
        {
            lines.Add($"  through {check.Through}{(check.ExpandCompactions ? " (expanded)" : "")}:");
            lines.AddRange(check.Differences.Select(difference => "    " + difference));
        }
        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>Replays golden trajectories through the event log and compares projections with their recorded form.</summary>
public static class ReplayRunner
{
    /// <summary>Checks every recorded expectation, and that every log position projects and survives persistence unchanged.</summary>
    public static ReplayReport Verify(GoldenTrajectory trajectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trajectory);
        using var session = ReplaySession.Create(trajectory, cancellationToken);
        var failures = ImmutableArray.CreateBuilder<string>();
        if (trajectory.Checks.IsEmpty)
            failures.Add("The trajectory has no checks.");

        // Replay from any position: the log round trip must not change what the in-memory events project to.
        var memory = InMemory(session.SessionId, trajectory.Events);
        for (long position = 0; position <= session.Head; position++)
        {
            foreach (var expand in (bool[])[false, true])
            {
                SessionSnapshot replayed;
                try
                {
                    replayed = session.Project(position, expand, cancellationToken);
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
                {
                    failures.Add($"Position {position} does not project: {exception.Message}");
                    continue;
                }
                var direct = SessionProjection.Build(session.SessionId, memory.Take((int)position), position, expand);
                var differences = JsonDiff.Compare(SnapshotJson.ToJson(direct), SnapshotJson.ToJson(replayed));
                if (differences.Count > 0)
                    failures.Add($"Position {position}{(expand ? " (expanded)" : "")} differs after the log round trip: {differences[0]}");
            }
        }

        var checks = ImmutableArray.CreateBuilder<CheckResult>();
        foreach (var check in trajectory.Checks)
        {
            var actual = SnapshotJson.ToJson(session.Project(check.Through, check.ExpandCompactions, cancellationToken));
            var differences = check.Expected is { } expected
                ? JsonDiff.Compare(expected, actual)
                : ["The expected projection has not been recorded."];
            checks.Add(new(check.Through, check.ExpandCompactions, [.. differences]));
        }
        return new(trajectory.Id, checks.ToImmutable(), failures.ToImmutable());
    }

    /// <summary>Returns the trajectory with every check's expected projection replaced by what replay produces now.</summary>
    /// <remarks>For authoring fixtures. Review the recorded output: recording accepts whatever the projection does today.</remarks>
    public static GoldenTrajectory Record(GoldenTrajectory trajectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(trajectory);
        using var session = ReplaySession.Create(trajectory, cancellationToken);
        return trajectory with
        {
            Checks = [.. trajectory.Checks.Select(check => check with
            {
                Expected = SnapshotJson.ToJson(session.Project(check.Through, check.ExpandCompactions, cancellationToken))
            })]
        };
    }

    private static StoredEvent[] InMemory(string sessionId, ImmutableArray<SessionEvent> events) =>
        [.. events.Select((payload, index) => new StoredEvent(sessionId, index + 1L, Guid.Empty, DateTimeOffset.UnixEpoch, payload))];
}
