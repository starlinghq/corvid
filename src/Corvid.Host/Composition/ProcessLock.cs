using System.Globalization;
using System.Text.Json;
using Corvid.Abstractions.IO;

namespace Corvid.Host.Composition;

/// <summary>Who holds a <see cref="ProcessLock"/>: the process as it names itself, its id, and since when.</summary>
/// <param name="Process">"corvid chat", "the Corvid app", "corvid gateway".</param>
public sealed record LockHolder(string Process, int ProcessId, DateTimeOffset Since)
{
    /// <summary>"corvid chat (pid 4127)".</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Process} (pid {ProcessId})");
}

/// <summary>A lock's state as a prober sees it: free, held by this process, or held by another (with who, when it said so).</summary>
public enum LockState
{
    Free,
    HeldHere,
    HeldElsewhere,
}

/// <param name="Holder">Who holds it, from its note; null when free, or when the holder left no readable note.</param>
public sealed record LockProbe(LockState State, LockHolder? Holder = null)
{
    public static LockProbe Free { get; } = new(LockState.Free);
}

/// <summary>
/// The one-process rule as a file (U-8, G-12): an exclusive lock on a file (<see cref="FileShare.None"/>, which .NET takes
/// as <c>flock(LOCK_EX)</c> on Linux, so the kernel releases it when the process exits however it ends) and, beside it, a
/// note (<c>&lt;lock&gt;.holder</c>) saying who holds it and since when, so another process can say more than "held". The
/// scheduler's lock beside <c>jobs.db</c>, each session's driver lock and the gateway's status read it.
/// <para>
/// The lock file itself is never deleted: a process that opened the path just before it was unlinked would lock the old
/// file while the next one locks a new one, and both would hold "the" lock. Only the note goes with a release; a note left
/// by a process that crashed is ignored, because the lock under it is free.
/// </para>
/// </summary>
public sealed class ProcessLock : IDisposable
{
    private static readonly JsonSerializerOptions NoteJson = new(JsonSerializerDefaults.Web);
    private static string? _processName;
    private readonly FileStream _stream;
    private int _disposed;

    private ProcessLock(string path, FileStream stream, LockHolder holder)
    {
        Path = path;
        _stream = stream;
        Holder = holder;
    }

    /// <summary>
    /// How this process names itself in the notes it leaves: set by the surface at start ("corvid chat", "the Corvid app");
    /// otherwise <c>corvid</c> with its command, from the command line.
    /// </summary>
    public static string ProcessName
    {
        get => Volatile.Read(ref _processName) ?? DefaultName();
        set => Volatile.Write(ref _processName, string.IsNullOrWhiteSpace(value) ? null : value.Trim());
    }

    public string Path { get; }

    public LockHolder Holder { get; }

    /// <summary>The note beside a lock file.</summary>
    public static string NotePath(string lockPath) => lockPath + ".holder";

    /// <summary>
    /// Takes the lock, or returns null when another holder has it. A prober (<see cref="Probe"/>) takes the lock for an
    /// instant to see whether it is free, so a failed attempt is retried <paramref name="attempts"/> times, a few
    /// milliseconds apart, before it counts as held.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The lock file cannot be written.</exception>
    /// <exception cref="IOException">The lock file cannot be opened for another reason than a holder (a read-only or full file system).</exception>
    public static ProcessLock? TryAcquire(string path, TimeProvider? time = null, int attempts = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = System.IO.Path.GetFullPath(path);
        // A4 verification: a lock can be the first to make the data directory (or drivers/ in it): owner only.
        Corvid.Abstractions.IO.PrivateDirectory.CreateFor(path);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                var holder = new LockHolder(ProcessName, Environment.ProcessId, (time ?? TimeProvider.System).GetUtcNow());
                WriteNote(path, holder);
                return new ProcessLock(path, stream, holder);
            }
            catch (IOException exception) when (FileLocks.IsHeldElsewhere(exception) && attempt < attempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(15 * attempt));
            }
            catch (IOException exception) when (FileLocks.IsHeldElsewhere(exception))
            {
                // Only a holder makes it "held"; any other refusal (EROFS, ENOSPC) is the caller's to report (L5).
                return null;
            }
        }
    }

    /// <summary>
    /// Whether the lock at <paramref name="path"/> is free or held, and by whom. No note means never taken or released
    /// cleanly, so free without touching the lock; with a note, the lock is tried for an instant (a note a crashed process
    /// left over a free lock is stale). A lock this process holds must be asked of its own handle, not probed: the probe
    /// would find it held "elsewhere".
    /// </summary>
    /// <param name="withoutNote">Try the lock even when no note is beside it: a holder that writes none (an older corvid) still holds it.</param>
    public static LockProbe Probe(string path, bool withoutNote = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        path = System.IO.Path.GetFullPath(path);
        if ((!withoutNote && !File.Exists(NotePath(path))) || !File.Exists(path))
            return LockProbe.Free;
        try
        {
            using var probe = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return LockProbe.Free;
        }
        catch (FileNotFoundException)
        {
            return LockProbe.Free;
        }
        catch (IOException exception) when (FileLocks.IsHeldElsewhere(exception))
        {
            return new LockProbe(LockState.HeldElsewhere, ReadNote(path));
        }
        catch (IOException)
        {
            // Not a holder (a read-only file system refuses the open itself): nobody can hold it from here, and the note,
            // if any, says who wrote it last. Free only when it names nobody, so a crashed holder's note does not flap it.
            return ReadNote(path) is { } noted ? new LockProbe(LockState.HeldElsewhere, noted) : LockProbe.Free;
        }
        catch (UnauthorizedAccessException)
        {
            // Not ours to try: say it is held, by whoever the note names.
            return new LockProbe(LockState.HeldElsewhere, ReadNote(path));
        }
    }

    /// <summary>The note's holder, or null when there is none or it cannot be read.</summary>
    public static LockHolder? ReadNote(string lockPath)
    {
        try
        {
            var note = JsonSerializer.Deserialize<Note>(File.ReadAllText(NotePath(lockPath)), NoteJson);
            return note is { Process.Length: > 0 } ? new LockHolder(note.Process, note.Pid, note.Since) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Releases the lock; the note goes first, while the lock still guards it.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        DeleteNote(Path, Holder);
        _stream.Dispose();
    }

    /// <summary>Deletes the note if it is still <paramref name="holder"/>'s; a note left behind over a free lock reads as stale.</summary>
    internal static void DeleteNote(string path, LockHolder holder)
    {
        try
        {
            if (ReadNote(path) is { } note && note.ProcessId == holder.ProcessId && note.Since == holder.Since)
                File.Delete(NotePath(path));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Writes the note for a lock this process holds by other means (the gateway's, which <c>GatewayHost</c> takes).</summary>
    internal static void WriteNote(string path, LockHolder holder)
    {
        try
        {
            var note = NotePath(path);
            var temporary = note + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Note(holder.Process, holder.ProcessId, holder.Since), NoteJson));
            File.Move(temporary, note, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The lock holds without its note; others then know only that it is held.
        }
    }

    private static string DefaultName()
    {
        var arguments = Environment.GetCommandLineArgs();
        var command = arguments.Length > 1 && arguments[1] is { Length: > 0 } first && !first.StartsWith('-') && first.All(char.IsAsciiLetterLower)
            ? " " + first
            : "";
        var name = System.IO.Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? arguments[0]);
        return (name is "dotnet" or "" ? "corvid" : name) + command;
    }

    private sealed record Note(string Process, int Pid, DateTimeOffset Since);
}
