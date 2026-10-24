using System.Collections.Immutable;
using Corvid.App.Core.Diffs;

namespace Corvid.App.Core.Review;

/// <summary>Which changes Review lists (docs/plan/ui.md §4.7): this turn's, those since the last review point, or the session's.</summary>
public enum ReviewScope
{
    ThisTurn,
    SinceLastReview,
    ThisSession,
}

/// <summary>The side of a diff a line, or a comment on it, belongs to: the file before or after.</summary>
public enum DiffSide
{
    Old,
    New,
}

/// <summary>A line of a diff a person pointed at, to comment on: its side, its number on that side and its text.</summary>
public sealed record DiffLineRef(DiffSide Side, int Line, string Text);

/// <summary>
/// A changed file as Review lists it: its counts, and the texts its diff is drawn from. A file Corvid may not read is
/// listed by name with <see cref="Note"/> and no diff; a binary or oversized file has a note and no texts either (F4).
/// </summary>
public sealed record ReviewFile(string Path, int Added, int Removed)
{
    /// <summary>False for a path the file tools' rules keep from Corvid.</summary>
    public bool Readable { get; init; } = true;

    /// <summary>Why there is no diff: "not readable by Corvid", "binary file", "too large to show (4.2 MB)".</summary>
    public string? Note { get; init; }

    /// <summary>The file at the start of the range; null for a file the range created.</summary>
    public string? OldText { get; init; }

    /// <summary>The file now; null when there is no text to show (see <see cref="Note"/>).</summary>
    public string? NewText { get; init; }

    /// <summary>The call that last changed it ("patch_file").</summary>
    public string? Tool { get; init; }

    /// <summary>The turn of that call.</summary>
    public int? Turn { get; init; }

    /// <summary>The checkpoint the whole file can be put back to; null when none covers it (not reversible).</summary>
    public string? CheckpointId { get; init; }

    /// <summary>The file was put back to its checkpoint from Review since the list was taken.</summary>
    public bool Reverted { get; init; }

    public bool HasText => Readable && NewText is not null;

    /// <summary>The line diff of the two texts, or null when the file has none to show.</summary>
    public FileDiff? Diff() => HasText ? FileDiff.Compute(Path, OldText, NewText!) : null;
}

/// <summary>
/// The last review point (G-7 keeps them in <c>ui.db</c>, beside the log and never in it): the turn the comments were sent
/// at, how many went, and when. "Since last review" lists what changed after it.
/// </summary>
public sealed record ReviewPoint(int Turn, int CommentsSent, DateTimeOffset At);

/// <summary>What Review shows for a session and a scope.</summary>
public sealed record ReviewChanges(string SessionId, ReviewScope Scope)
{
    public ImmutableArray<ReviewFile> Files { get; init; } = [];

    /// <summary>The session's current turn, for the scope picker's labels ("This turn (turn 12)").</summary>
    public int Turn { get; init; }

    public ReviewPoint? ReviewPoint { get; init; }

    /// <summary>The first and last checkpoints that cover the listed files ("c-41 to c-44"); null when none do.</summary>
    public string? FirstCheckpoint { get; init; }

    public string? LastCheckpoint { get; init; }
}

/// <summary>A line comment as it goes to the session: the file, the side and line, the line's text, and what was said.</summary>
public sealed record ReviewComment(string Path, DiffSide Side, int Line, string LineText, string Text);

/// <summary>What putting one file back to its checkpoint did: restored, merged with the person's edit, or refused.</summary>
public sealed record FileRevertResult(string Path, string CheckpointId, bool Merged, bool Refused, string? Reason = null);
