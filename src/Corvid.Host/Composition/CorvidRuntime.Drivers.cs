using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Corvid.Host.Configuration;

namespace Corvid.Host.Composition;

/// <summary>
/// G-12, one driver per session: the process that opens a session to post to it (<see cref="OpenSession"/>) holds its
/// driver lock until it exits, and another process that tries is refused with who holds it
/// (<see cref="SessionHeldException"/>). Reading a session (its log, a replay, a plan) needs no lock, so another process
/// can show it read-only and follow it as it grows. Job runs are the scheduler's and are never opened, so they take none.
/// </summary>
public sealed partial class CorvidRuntime
{
    private readonly ConcurrentDictionary<string, ProcessLock> _drivers = new(StringComparer.Ordinal);
    private readonly Lock _driverGate = new();
    private bool _driversReleased;

    /// <summary>The driver lock file of a session: <c>drivers/&lt;id&gt;.lock</c> in the data directory, the id made safe for a file name.</summary>
    public static string DriverLockPath(CorvidOptions options, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Path.Combine(CorvidPaths.SessionDrivers(options), DriverFileName(sessionId) + ".lock");
    }

    /// <summary>
    /// Who drives a session now: this process (it opened the session), another process (with its note), or nobody. A
    /// snapshot: the other process may let go a moment later.
    /// </summary>
    public LockProbe Driver(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_drivers.TryGetValue(sessionId, out var held))
            return new LockProbe(LockState.HeldHere, held.Holder);
        return ProcessLock.Probe(DriverLockPath(Options, sessionId));
    }

    /// <summary>Takes the session's driver lock for this process, once; refuses when another process holds it.</summary>
    private void TakeDriver(string sessionId)
    {
        if (_drivers.ContainsKey(sessionId))
            return;
        lock (_driverGate)
        {
            if (_drivers.ContainsKey(sessionId) || _driversReleased)
                return;
            var path = DriverLockPath(Options, sessionId);
            ProcessLock? taken;
            try
            {
                // A prober (another window listing sessions) holds the lock for an instant: a few tries before "held".
                taken = ProcessLock.TryAcquire(path, attempts: 4);
            }
            catch (UnauthorizedAccessException)
            {
                // A data directory this process cannot lock in: the log's own conflict check still keeps appends apart.
                return;
            }
            if (taken is null)
                throw new SessionHeldException(sessionId, ProcessLock.ReadNote(path));
            _drivers[sessionId] = taken;
        }
    }

    private void ReleaseDrivers()
    {
        lock (_driverGate)
        {
            _driversReleased = true;
            foreach (var held in _drivers.Values)
                held.Dispose();
            _drivers.Clear();
        }
    }

    /// <summary>The id as a file name: itself when it is plain, else its plain characters and a hash of the whole id.</summary>
    internal static string DriverFileName(string sessionId)
    {
        var plain = sessionId.Length <= 100 && sessionId[0] != '.'
            && sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
        if (plain)
            return sessionId;
        var safe = new string([.. sessionId.Take(80).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_')]);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..12];
        return string.Create(CultureInfo.InvariantCulture, $"{safe}~{hash}");
    }
}

/// <summary>
/// A session another process drives (G-12): this process may read it and follow it, not post to it, change it or restore
/// it, until that process lets go (it exits).
/// </summary>
public sealed class SessionHeldException : InvalidOperationException
{
    public SessionHeldException(string sessionId, LockHolder? holder)
        : base(Describe(sessionId, holder))
    {
        SessionId = sessionId;
        Holder = holder;
    }

    public SessionHeldException()
        : this("", (LockHolder?)null)
    {
    }

    public SessionHeldException(string message)
        : base(message)
    {
        SessionId = "";
    }

    public SessionHeldException(string message, Exception innerException)
        : base(message, innerException)
    {
        SessionId = "";
    }

    public string SessionId { get; }

    /// <summary>The process that drives it, from its note; null when it left none.</summary>
    public LockHolder? Holder { get; }

    /// <summary>"Session cli-… is driven by corvid chat (pid 4127) since 2026-10-24 10:31 UTC; …".</summary>
    public static string Describe(string sessionId, LockHolder? holder) =>
        $"Session {sessionId} is driven by {HolderText(holder)}; only that process can post to it, change it or restore it until it exits.";

    /// <summary>"corvid chat (pid 4127) since 2026-10-24 10:31 UTC", or "another corvid process".</summary>
    public static string HolderText(LockHolder? holder) => holder is null
        ? "another corvid process"
        : $"{holder} since {holder.Since.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC";
}
