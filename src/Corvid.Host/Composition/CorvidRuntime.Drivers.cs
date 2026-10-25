using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Corvid.Abstractions.IO;
using Corvid.Host.Configuration;
using Corvid.Kernel.Sessions;

namespace Corvid.Host.Composition;

/// <summary>
/// G-12, one driver per session: the process that opens a session to post to it (<see cref="OpenSession"/>) holds its
/// driver lock until it exits or lets go (<see cref="ReleaseDriverAsync"/>, when nothing of it is open here), and another
/// process that tries is refused with who holds it (<see cref="SessionHeldException"/>). Reading a session (its log, a
/// replay, a plan) needs no lock, so another process can show it read-only and follow it as it grows. Job runs are the
/// scheduler's and are never opened, so they take none.
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
    /// snapshot: the other process may let go a moment later. The lock is tried whenever its file exists (L6): a holder
    /// that could not write its note still holds it. Asked under the driver gate, so this process's own probe never collides
    /// with its own <see cref="OpenSession"/> taking the lock.
    /// </summary>
    public LockProbe Driver(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (_drivers.TryGetValue(sessionId, out var held))
            return new LockProbe(LockState.HeldHere, held.Holder);
        lock (_driverGate)
        {
            if (_drivers.TryGetValue(sessionId, out held))
                return new LockProbe(LockState.HeldHere, held.Holder);
            return ProcessLock.Probe(DriverLockPath(Options, sessionId), withoutNote: true);
        }
    }

    /// <summary>
    /// G-12 for a write that does not open the session (a rename, a recap): refused while another process drives it. The
    /// lock is not kept, so a window that only shows a session never keeps the terminal that drives it from opening it.
    /// </summary>
    /// <exception cref="SessionHeldException">Another process drives the session.</exception>
    private void RefuseIfHeld(string sessionId)
    {
        var driver = Driver(sessionId);
        if (driver.State == LockState.HeldElsewhere)
            throw new SessionHeldException(sessionId, driver.Holder);
    }

    /// <summary>
    /// L7: a write that does not open the session holds the driver lock for the write itself, when nobody drives it, so a
    /// terminal cannot open the session and append between the check and the write. The first <paramref name="probes"/>
    /// checks only look (a recap's check before it generates, which takes seconds); every later one takes the lock, kept
    /// until the guard is disposed. A session this process drives needs nothing more.
    /// </summary>
    internal WriteGuard GuardWrite(string sessionId, int probes = 0) => new(this, sessionId, probes);

    internal sealed class WriteGuard(CorvidRuntime runtime, string sessionId, int probes) : IDisposable
    {
        private int _checks;
        private bool _ours;
        private ProcessLock? _held;

        public void Check()
        {
            if (++_checks <= probes)
            {
                runtime.RefuseIfHeld(sessionId);
                return;
            }
            if (_ours || _held is not null)
                return;
            lock (runtime._driverGate)
            {
                if (runtime._drivers.ContainsKey(sessionId))
                {
                    _ours = true;
                    return;
                }
                var path = DriverLockPath(runtime.Options, sessionId);
                _held = Acquire(sessionId, path) ?? throw new SessionHeldException(sessionId, ProcessLock.ReadNote(path));
            }
        }

        public void Dispose() => _held?.Dispose();
    }

    /// <summary>Takes the session's driver lock for this process, once; refuses when another process holds it.</summary>
    /// <exception cref="SessionHeldException">Another process holds the lock.</exception>
    /// <exception cref="SessionLockUnavailableException">The lock file cannot be opened at all (L5): nothing is posted unguarded.</exception>
    private void TakeDriver(string sessionId)
    {
        if (_drivers.ContainsKey(sessionId))
            return;
        lock (_driverGate)
        {
            if (_drivers.ContainsKey(sessionId) || _driversReleased)
                return;
            var path = DriverLockPath(Options, sessionId);
            var taken = Acquire(sessionId, path) ?? throw new SessionHeldException(sessionId, ProcessLock.ReadNote(path));
            _drivers[sessionId] = taken;
        }
    }

    /// <summary>The lock, or null when another process holds it; a lock file nobody can open is refused (L5).</summary>
    private static ProcessLock? Acquire(string sessionId, string path)
    {
        try
        {
            // A prober (another window listing sessions) holds the lock for an instant: a few tries before "held".
            return ProcessLock.TryAcquire(path, attempts: 4);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            // Not another driver: a read-only or full data directory. Going on without the lock would let two processes
            // drive the session, so the session is not opened.
            throw new SessionLockUnavailableException(sessionId, FileLocks.Unopenable(path, exception), exception);
        }
    }

    /// <summary>
    /// M2 (A4 review): lets go of a session this process drives when nothing of it is open here (no turn running or queued,
    /// no exclusive work such as a restore), so another process (<c>corvid chat --session</c>) can drive it. The session's
    /// actor goes with the lock; the next <see cref="OpenSession"/> takes both again. True when it was released; false when
    /// this process does not drive it or it is busy. The caller decides that no view, approval or question of its own is
    /// open (the window's session service).
    /// </summary>
    public async Task<bool> ReleaseDriverAsync(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        SessionActor? actor;
        lock (_driverGate)
        {
            if (_driversReleased || !_drivers.TryGetValue(sessionId, out var held))
                return false;
            if (_sessions.TryGetValue(sessionId, out var session) && !session.Actor.IsIdle)
                return false;
            _sessions.TryRemove(sessionId, out _);
            Sessions.TryRemove(sessionId, out actor);
            _drivers.TryRemove(sessionId, out _);
            held.Dispose();
        }
        if (actor is not null)
            await actor.DisposeAsync().ConfigureAwait(false);
        return true;
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

    /// <summary>
    /// The id as a file name: itself when it is plain (lower-case letters, digits, '-', '_', '.'), else its plain characters
    /// and a hash of the whole id. Upper-case letters count as not plain, so two ids that differ only in case never share a
    /// lock file on a case-insensitive file system (macOS, Windows).
    /// </summary>
    internal static string DriverFileName(string sessionId)
    {
        var plain = sessionId.Length <= 100 && sessionId[0] != '.'
            && sessionId.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_' or '.');
        if (plain)
            return sessionId;
        var safe = new string([.. sessionId.Take(80).Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? char.ToLowerInvariant(c) : '_')]);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..12];
        return string.Create(CultureInfo.InvariantCulture, $"{safe}~{hash}");
    }
}

/// <summary>
/// A session's driver lock cannot be opened at all (a read-only or full data directory, L5): not another driver, so it is
/// not said to be one, and the session is not opened unguarded.
/// </summary>
public sealed class SessionLockUnavailableException : InvalidOperationException
{
    public SessionLockUnavailableException(string sessionId, string why, Exception innerException)
        : base($"Session {sessionId} cannot be opened to post to: its driver lock cannot be taken, {why}.", innerException)
    {
        SessionId = sessionId;
    }

    public SessionLockUnavailableException()
        : base("A session's driver lock cannot be taken.")
    {
        SessionId = "";
    }

    public SessionLockUnavailableException(string message)
        : base(message)
    {
        SessionId = "";
    }

    public SessionLockUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
        SessionId = "";
    }

    public string SessionId { get; }
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
