using System.Collections.Immutable;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Rewind;

/// <summary>What a ledger entry is: a call that changed files (or ran with none changed), a call still running or waiting, or a restore point.</summary>
public enum LedgerEntryKind
{
    Call,
    /// <summary>The call's before checkpoint is taken and it has not finished: running, or waiting on an approval.</summary>
    Pending,
    Restore,
}

/// <summary>
/// One entry of the checkpoint ledger as Rewind lists it (Rewind.png): one per call with its tool, target, paths and
/// checkpoints, or a restore point with what it put back. The ledger is the session's own (<c>CheckpointTaken</c>).
/// </summary>
public sealed record LedgerEntry(string CheckpointId, LedgerEntryKind Kind, DateTimeOffset At, int Turn)
{
    public string? Tool { get; init; }

    /// <summary>The call's target ("dotnet build"), or a restore's label ("undo of turn 8").</summary>
    public string? Target { get; init; }

    /// <summary>The checkpoint after the call; null for a pending call or a restore.</summary>
    public string? AfterId { get; init; }

    public ImmutableArray<FileChange> Changes { get; init; } = [];

    /// <summary>The call ended with no after checkpoint: cancelled, or its turn went back.</summary>
    public bool Stopped { get; init; }

    /// <summary>A pending call waits on an approval rather than running.</summary>
    public bool WaitingOnApproval { get; init; }

    /// <summary>A restore point's result, when it is known ("2 put back, 1 merged").</summary>
    public RestoreOutcome? Outcome { get; init; }
}

/// <summary>A session's ledger, newest first, and the latest restore's result.</summary>
public sealed record RewindTimeline(string SessionId)
{
    public ImmutableArray<LedgerEntry> Entries { get; init; } = [];

    public RestoreOutcome? LastRestore { get; init; }

    /// <summary>The session's latest turn.</summary>
    public int Turn { get; init; }
}

/// <summary>What a restore does to one file: put it back, merge it three ways with the person's edit, or refuse.</summary>
public enum PutBackKind
{
    Restore,
    Merge,
    Conflict,
}

/// <summary>
/// A file a restore puts back, with the counts it undoes (the call added 19 and removed 5, so going back removes 19 and
/// adds 5), and for a merge or a conflict the person's edit that causes it.
/// </summary>
public sealed record PutBackFile(string Path, PutBackKind Kind, int Added, int Removed)
{
    /// <summary>The person's own edit since the checkpoint, for a merge or a conflict.</summary>
    public PersonEdit? Edit { get; init; }

    /// <summary>Why a conflict refuses ("putting it back to c-31 would delete your edit").</summary>
    public string? Reason { get; init; }
}

/// <summary>The person edited a file after a checkpoint ("SeccompTests.cs was edited by you at 14:05, after c-43").</summary>
public sealed record PersonEdit(string Path, DateTimeOffset At, string AfterCheckpoint);

/// <summary>
/// What going back to before a checkpoint would do (the plan the panel shows before Restore): the files it puts back,
/// the turns that leave the transcript, and the command the terminal would use for it (<c>/rollback 4</c>).
/// </summary>
public sealed record RestorePlan(string CheckpointId, DateTimeOffset At, int Turn)
{
    public ImmutableArray<PutBackFile> Files { get; init; } = [];

    /// <summary>The first and last turns that leave the transcript; <see cref="ToTurn"/> is below <see cref="FromTurn"/> when none do.</summary>
    public int FromTurn { get; init; }

    public int ToTurn { get; init; }

    /// <summary>
    /// The turn the checkpoint is part-way through, when it keeps its message and loses only the call and what came after
    /// it (the engine's rollback goes back to just before the call); null when only whole turns leave.
    /// </summary>
    public int? PartTurn { get; init; }

    /// <summary>"/rollback 4": the Nth latest call's before checkpoint; null for a restore point.</summary>
    public string? Command { get; init; }

    /// <summary>
    /// The plan was worked out while a turn of the session was running: the files are as the turn has left them so far,
    /// and the restore itself waits for the turn to end, so what it puts back can differ.
    /// </summary>
    public bool MidTurn { get; init; }

    public bool HasConflicts => Files.Any(file => file.Kind == PutBackKind.Conflict);

    public int TurnsLeaving => Math.Max(0, ToTurn - FromTurn + 1);
}

/// <summary>A file's diff in a restore's preview: the file now beside the file as the restore would leave it.</summary>
public sealed record RestorePreviewFile(string Path, PutBackKind Kind)
{
    public string? CurrentText { get; init; }

    public string? RestoredText { get; init; }

    /// <summary>Why there is no diff to show ("no text in the demo").</summary>
    public string? Note { get; init; }
}

/// <summary>
/// What a restore did, exactly as <c>RestoreResult</c> reports it: restored, merged and refused paths, the restore
/// checkpoint taken first, or a refusal with its reason and nothing changed.
/// </summary>
public sealed record RestoreOutcome(string Label, DateTimeOffset At, RewindScope Scope)
{
    public ImmutableArray<string> Restored { get; init; } = [];

    public ImmutableArray<string> Merged { get; init; } = [];

    public ImmutableArray<RestoreConflict> Conflicts { get; init; } = [];

    public string? CheckpointId { get; init; }

    public bool Refused { get; init; }

    public string? Detail { get; init; }

    /// <summary>"2 put back, 1 merged": a restore point's paths cell in the ledger.</summary>
    public string Summary => Refused
        ? "refused, nothing changed"
        : Restored.IsEmpty && Merged.IsEmpty
            ? "files kept"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{Restored.Length} put back{(Merged.IsEmpty ? "" : $", {Merged.Length} merged")}");
}
