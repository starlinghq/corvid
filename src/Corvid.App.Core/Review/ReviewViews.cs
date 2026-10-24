using System.Globalization;
using System.Windows.Input;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Review;

/// <summary>A file in Review's list (Review.png <c>.files li</c>): the path cut at its start, the counts or why there is no diff.</summary>
public sealed class ReviewFileView : ObservableObject
{
    private bool _isSelected;
    private int _comments;

    internal ReviewFileView(ReviewFile file, ICommand select)
    {
        File = file;
        Select = select;
    }

    public ReviewFile File { get; }

    public string Path => File.Path;

    public ICommand Select { get; }

    public bool IsReadable => File.Readable;

    /// <summary>The counts show for a readable file; a note ("not readable by Corvid", "reverted to c-44") otherwise.</summary>
    public bool ShowCounts => File.Readable && !File.Reverted;

    public string AddedText => string.Create(CultureInfo.InvariantCulture, $"+{File.Added}");

    public string RemovedText => string.Create(CultureInfo.InvariantCulture, $"−{File.Removed}");

    public string Note => File.Reverted ? $"reverted to {File.CheckpointId}" : File.Note ?? "";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    /// <summary>Draft comments on this file, for its accessible name.</summary>
    public int Comments
    {
        get => _comments;
        set
        {
            if (SetProperty(ref _comments, value))
                OnPropertyChanged(nameof(AutomationName));
        }
    }

    public string AutomationName
    {
        get
        {
            var what = ShowCounts ? $"{File.Added} added, {File.Removed} removed" : Note;
            var comments = _comments switch { 0 => "", 1 => ", 1 comment", _ => $", {_comments} comments" };
            return $"{File.Path}, {what}{comments}";
        }
    }
}

/// <summary>A draft line comment (Review.png <c>.comment</c>): "you · line 46 · draft", its text, and remove.</summary>
public sealed class ReviewCommentView
{
    internal ReviewCommentView(ReviewComment comment, Action<ReviewCommentView> remove)
    {
        Comment = comment;
        Remove = new RelayCommand(() => remove(this));
    }

    public ReviewComment Comment { get; }

    public string Path => Comment.Path;

    public DiffSide Side => Comment.Side;

    public int Line => Comment.Line;

    public string Text => Comment.Text;

    public string Meta => Comment.Side == DiffSide.Old
        ? string.Create(CultureInfo.InvariantCulture, $"you · line {Comment.Line} before · draft")
        : string.Create(CultureInfo.InvariantCulture, $"you · line {Comment.Line} · draft");

    public RelayCommand Remove { get; }

    public string AutomationName => $"{Meta}: {Comment.Text}";
}

/// <summary>The open comment field under a line: what it is on, the text being written, save and cancel.</summary>
public sealed class CommentEditor : ObservableObject
{
    private string _text = "";

    internal CommentEditor(string path, DiffLineRef line, Action<CommentEditor> save, Action cancel)
    {
        Path = path;
        Line = line;
        Save = new RelayCommand(() => save(this), () => _text.Trim().Length > 0);
        Cancel = new RelayCommand(cancel);
    }

    public string Path { get; }

    public DiffLineRef Line { get; }

    public DiffSide Side => Line.Side;

    public int Number => Line.Line;

    public string Text
    {
        get => _text;
        set
        {
            if (SetProperty(ref _text, value ?? ""))
                Save.NotifyCanExecuteChanged();
        }
    }

    /// <summary>"Comment on line 46", or "on line 44 before the change" for the old side.</summary>
    public string Title => Line.Side == DiffSide.Old
        ? string.Create(CultureInfo.InvariantCulture, $"Comment on line {Line.Line} before the change")
        : string.Create(CultureInfo.InvariantCulture, $"Comment on line {Line.Line}");

    public RelayCommand Save { get; }

    public RelayCommand Cancel { get; }
}

/// <summary>One of the scope picker's options: "Since last review (turn 11)".</summary>
public sealed class ReviewScopeOption : ObservableObject
{
    private string _label;

    internal ReviewScopeOption(ReviewScope scope, string label)
    {
        Scope = scope;
        _label = label;
    }

    public ReviewScope Scope { get; }

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public override string ToString() => _label;
}
