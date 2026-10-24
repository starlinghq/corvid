using System.Globalization;
using System.Windows.Input;
using Corvid.App.Core.Diffs;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Rewind;

/// <summary>A row of the checkpoint ledger table (Rewind.png): when, the call, its paths, and what it changed.</summary>
public sealed class LedgerEntryView : ObservableObject
{
    private bool _isSelected;

    internal LedgerEntryView(LedgerEntry entry, TimeZoneInfo zone, ICommand select)
    {
        Entry = entry;
        When = TimeZoneInfo.ConvertTime(entry.At, zone).ToString("HH:mm", CultureInfo.InvariantCulture);
        Select = select;
    }

    public LedgerEntry Entry { get; }

    public string CheckpointId => Entry.CheckpointId;

    public ICommand Select { get; }

    /// <summary>"14:03", local time.</summary>
    public string When { get; }

    public bool IsRestore => Entry.Kind == LedgerEntryKind.Restore;

    public bool IsCall => !IsRestore;

    /// <summary>The call's tool in mono ("patch_file"); empty for a restore point.</summary>
    public string Tool => IsRestore ? "" : SafeText.Clean(Entry.Tool ?? "call");

    /// <summary>
    /// The rest of the call cell: a shell call's command, then its checkpoints ("dotnet build · c-33 → c-34"); for a
    /// restore point, its label and checkpoint ("undo of turn 8 · c-35").
    /// </summary>
    public string Detail
    {
        get
        {
            if (IsRestore)
                // A restore's label can name a file (a revert from Review): shown with any hidden character in it.
                return $"{SafeText.Reveal(Entry.Target ?? "restore")} · {Entry.CheckpointId}";
            var command = Entry.Tool == "shell" && !string.IsNullOrEmpty(Entry.Target) ? SafeText.Clean(Entry.Target) : "";
            var checkpoints = Entry.AfterId is { } after ? $"· {Entry.CheckpointId} → {after}" : "";
            return string.Join(' ', new[] { command, checkpoints }.Where(part => part.Length > 0));
        }
    }

    /// <summary>The paths cell: the files the call changed, a restore's summary, or "no files changed".</summary>
    public string Paths => Entry switch
    {
        { Kind: LedgerEntryKind.Restore, Outcome: { } outcome } => outcome.Summary,
        { Kind: LedgerEntryKind.Restore } => "",
        { Kind: LedgerEntryKind.Pending } => Entry.Tool == "shell" ? "" : SafeText.Clean(Entry.Target ?? ""),
        { Changes.IsEmpty: true } => "no files changed",
        _ => string.Join(", ", Entry.Changes.Select(change => SafeText.Clean(change.Path))),
    };

    /// <summary>Paths in mono; "no files changed" in muted prose.</summary>
    public bool PathsAreFiles => !(Entry.Kind == LedgerEntryKind.Call && Entry.Changes.IsEmpty);

    public bool PathsAreNote => !PathsAreFiles;

    public bool ShowCounts => Entry.Kind == LedgerEntryKind.Call && !Entry.Changes.IsEmpty;

    public string AddedText => string.Create(CultureInfo.InvariantCulture, $"+{Entry.Changes.Sum(change => change.Added)}");

    public string RemovedText => string.Create(CultureInfo.InvariantCulture, $"−{Entry.Changes.Sum(change => change.Removed)}");

    /// <summary>The change cell's muted words: "restore point", or "0" for a call that changed nothing.</summary>
    public string ChangeNote => Entry switch
    {
        { Kind: LedgerEntryKind.Restore } => "restore point",
        { Kind: LedgerEntryKind.Call, Stopped: true } => "stopped",
        { Kind: LedgerEntryKind.Call, Changes.IsEmpty: true } => "0",
        _ => "",
    };

    public bool HasChangeTag => Entry.Kind == LedgerEntryKind.Pending;

    public string ChangeTag => Entry.WaitingOnApproval ? "waiting on approval" : "running";

    public Tone ChangeTagTone => Entry.WaitingOnApproval ? Tone.Amber : Tone.Blue;

    /// <summary>A pending call has nothing to go back from yet.</summary>
    public bool IsSelectable => Entry.Kind != LedgerEntryKind.Pending;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string AutomationName
    {
        get
        {
            var what = IsRestore ? $"restore point, {Detail}, {Paths}" : $"{Tool} {Detail}, {Paths}";
            var change = ShowCounts ? $", {Entry.Changes.Sum(change => change.Added)} added, {Entry.Changes.Sum(change => change.Removed)} removed"
                : HasChangeTag ? $", {ChangeTag}" : "";
            return $"{When}, {what}{change}";
        }
    }
}

/// <summary>A file in the plan's "Files put back": "to c-38 · −19 +5", "to c-38 · merge", or a conflict.</summary>
public sealed class PutBackFileView(PutBackFile file, string checkpointId)
{
    public PutBackFile File { get; } = file;

    public string Path => File.Path;

    public bool IsConflict => File.Kind == PutBackKind.Conflict;

    /// <summary>
    /// "to c-38 ·" before the counts or the word; a trailing space would be trimmed, so the row spaces them. Just "to c-49"
    /// when nothing follows (a path a restore put back, whose counts its result does not carry).
    /// </summary>
    public string Prefix => IsConflict ? "" : ShowCounts && RemoveText.Length == 0 && AddText.Length == 0 ? $"to {checkpointId}" : $"to {checkpointId} ·";

    /// <summary>The lines going back undo the call's: its added lines are removed ("−19") and its removed ones return ("+5").</summary>
    public bool ShowCounts => File.Kind == PutBackKind.Restore;

    public string RemoveText => ShowCounts && File.Added > 0 ? string.Create(CultureInfo.InvariantCulture, $"−{File.Added}") : "";

    public string AddText => ShowCounts && File.Removed > 0 ? string.Create(CultureInfo.InvariantCulture, $"+{File.Removed}") : "";

    /// <summary>"merge" or "conflict"; empty for a plain restore.</summary>
    public string Word => File.Kind switch
    {
        PutBackKind.Merge => "merge",
        PutBackKind.Conflict => "conflict",
        _ => "",
    };

    public bool ShowWord => !ShowCounts;

    public string AutomationName => File.Kind switch
    {
        PutBackKind.Merge => $"{SafeText.Reveal(File.Path)}, back to {checkpointId}, merged with your edit",
        PutBackKind.Conflict => $"{SafeText.Reveal(File.Path)}, conflict: {SafeText.Reveal(File.Reason)}",
        _ => $"{SafeText.Reveal(File.Path)}, back to {checkpointId}, {File.Added} lines removed, {File.Removed} restored",
    };
}

/// <summary>
/// A note under the files: the person's own edit that a restore merges with (amber) or that refuses it (red). The file
/// name is in mono, the sentence in prose.
/// </summary>
public sealed class RestoreNoteView(string fileName, string text, Tone tone)
{
    public string FileName { get; } = fileName;

    public string Text { get; } = text;

    public Tone Tone { get; } = tone;

    public bool IsConflict => Tone == Tone.Red;

    public string AutomationName => $"{FileName} {Text}";
}

/// <summary>One of the three restore choices: files and conversation, conversation only, files only.</summary>
public sealed class RestoreOptionView : ObservableObject
{
    private bool _isSelected;
    private string _detail;

    internal RestoreOptionView(RewindScope scope, string title, string detail, ICommand choose)
    {
        Scope = scope;
        Title = title;
        _detail = detail;
        Choose = choose;
    }

    public RewindScope Scope { get; }

    public string Title { get; }

    /// <summary>"the workspace and the transcript as they were at c-38".</summary>
    public string Detail
    {
        get => _detail;
        set
        {
            if (SetProperty(ref _detail, value))
                OnPropertyChanged(nameof(AutomationName));
        }
    }

    public ICommand Choose { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string AutomationName => $"{Title}: {_detail}";
}

/// <summary>A path in a restore's result: "restored", "merged with your edit", or refused with the reason.</summary>
public sealed class OutcomeFileView(string path, string what, bool refused)
{
    public string Path { get; } = path;

    public string What { get; } = what;

    public bool IsRefused { get; } = refused;

    public string AutomationName => $"{SafeText.Reveal(Path)}, {SafeText.Reveal(What)}";
}

/// <summary>A file in the restore's preview: its path, what the restore does to it, and the diff from now to restored.</summary>
public sealed class PreviewFileView
{
    private IReadOnlyList<SideBySideRow>? _rows;

    internal PreviewFileView(RestorePreviewFile file)
    {
        File = file;
        Diff = file.CurrentText is not null && file.RestoredText is not null
            ? FileDiff.Compute(file.Path, file.CurrentText, file.RestoredText)
            : null;
    }

    public RestorePreviewFile File { get; }

    public FileDiff? Diff { get; }

    public string Path => File.Path;

    public string What => File.Kind switch
    {
        PutBackKind.Merge => "merges with your edit",
        PutBackKind.Conflict => "conflict: the restore stops here",
        _ => Diff is { } diff ? $"goes back · {FileDiff.Counts(diff.Added, diff.Removed)}" : "goes back",
    };

    public bool HasDiff => Diff is not null;

    public string Note => File.Note is { } note ? $"No diff to show: {note}." : "";

    /// <summary>The side-by-side rows from the first hunk to the last, as Review draws them; made when first drawn.</summary>
    public IReadOnlyList<SideBySideRow> Rows => _rows ??= Review.DiffFolds.Trim(Diff?.SideBySide() ?? [], row => row.IsSkip);
}
