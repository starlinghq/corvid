using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Corvid.App.Core.Diffs;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Review;

/// <summary>
/// The Review area (docs/plan/ui.md §4.7, F4; Review.png): the selected session's changed files for a scope, the selected
/// file's diff side by side or unified, draft line comments, and the actions under the diff: send the comments as one
/// message, ask about the file, open it in the editor, put it back to its checkpoint.
/// </summary>
public sealed class ReviewViewModel : AreaViewModel
{
    private readonly IReviewService _service;
    private readonly ShellViewModel _shell;
    private readonly SessionHub _sessions;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, List<ReviewCommentView>> _drafts = new(StringComparer.Ordinal);
    private readonly ReviewScopeOption _thisTurn = new(ReviewScope.ThisTurn, "This turn");
    private readonly ReviewScopeOption _sinceReview = new(ReviewScope.SinceLastReview, "Since last review");
    private readonly ReviewScopeOption _thisSession = new(ReviewScope.ThisSession, "This session");
    private string? _sessionId;
    private ReviewChanges? _changes;
    private int _scopeIndex = 1;
    private bool _sideBySide = true;
    private ReviewFileView? _selected;
    private FileDiff? _diff;
    private IReadOnlyList<SideBySideRow> _sideRows = [];
    private IReadOnlyList<DiffRow> _unifiedRows = [];
    private CommentEditor? _editor;
    private string _notice = "";
    private Tone _noticeTone = Tone.Grey;
    private string? _preferredPath;

    public ReviewViewModel(IReviewService service, ShellViewModel shell, SessionHub sessions, TimeProvider time)
        : base(Area.Review, "The changes of a turn or a session, side by side or unified, with line comments sent as one message.")
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(time);
        _service = service;
        _shell = shell;
        _sessions = sessions;
        _time = time;
        Scopes = [_thisTurn, _sinceReview, _thisSession];
        ShowUnifiedCommand = new RelayCommand(() => IsSideBySide = false);
        ShowSideBySideCommand = new RelayCommand(() => IsSideBySide = true);
        CommentOnLineCommand = new RelayCommand<DiffLineRef>(OpenEditor, line => line is not null && _selected?.File.HasText == true);
        SendCommentsCommand = new AsyncRelayCommand(SendCommentsAsync, () => FileCommentsTotal > 0 && _sessionId is not null);
        AskAboutFileCommand = new RelayCommand(AskAboutFile, () => _selected is not null && _sessionId is not null);
        OpenInEditorCommand = new AsyncRelayCommand(OpenInEditorAsync, () => _selected?.File.HasText == true);
        RevertFileCommand = new AsyncRelayCommand(RevertFileAsync, () => _selected?.File is { CheckpointId: not null, Reverted: false, Readable: true });
        OpenRewindCommand = new RelayCommand(() => _shell.Navigate(Area.Rewind));
        _shell.PropertyChanged += OnShellChanged;
        _service.ChangesChanged += OnChangesChanged;
        SetSession(_shell.CurrentSession?.Id);
    }

    /// <summary>The session whose changes show; empty when none is open.</summary>
    public string SessionTitle => _shell.CurrentSession?.Title ?? "";

    public bool HasSession => _sessionId is not null;

    /// <summary>The scope picker's options: this turn, since the last review point, this session.</summary>
    public IReadOnlyList<ReviewScopeOption> Scopes { get; }

    /// <summary>The picked scope's index in <see cref="Scopes"/>; "since last review" to begin with, as Review.png shows.</summary>
    public int ScopeIndex
    {
        get => _scopeIndex;
        set
        {
            if (value < 0 || value >= Scopes.Count || !SetProperty(ref _scopeIndex, value))
                return;
            Refresh();
        }
    }

    public ReviewScope Scope => Scopes[_scopeIndex].Scope;

    /// <summary>"last review point: turn 11 · 2 comments sent at 14:02"; empty before the first review.</summary>
    public string ReviewPointText => _changes?.ReviewPoint is { } point
        ? string.Create(CultureInfo.InvariantCulture,
            $"last review point: turn {point.Turn} · {Plural(point.CommentsSent, "comment")} sent at {Local(point.At):HH:mm}")
        : "";

    public bool IsSideBySide
    {
        get => _sideBySide;
        set
        {
            if (SetProperty(ref _sideBySide, value))
                OnPropertyChanged(nameof(IsUnified));
        }
    }

    public bool IsUnified => !_sideBySide;

    public RelayCommand ShowUnifiedCommand { get; }

    public RelayCommand ShowSideBySideCommand { get; }

    public ObservableCollection<ReviewFileView> Files { get; } = [];

    /// <summary>
    /// "3 changed · +31 −9": the readable files and their totals; files Corvid may not read, and files put back to their
    /// checkpoint, are listed but not counted.
    /// </summary>
    public string FilesSummary
    {
        get
        {
            var counted = Files.Where(file => file.ShowCounts).ToList();
            return counted.Count == 0
                ? "nothing changed"
                : string.Create(CultureInfo.InvariantCulture,
                    $"{counted.Count} changed · {FileDiff.Counts(counted.Sum(file => file.File.Added), counted.Sum(file => file.File.Removed))}");
        }
    }

    public bool HasFiles => Files.Count > 0;

    /// <summary>The list's one line when the scope has nothing: what to do next.</summary>
    public string EmptyText => !HasSession
        ? "Open a session to review its changes."
        : Scope == ReviewScope.SinceLastReview
            ? "Nothing changed since the last review point. The next turn's changes show here."
            : "No files changed in this scope.";

    /// <summary>What the diff column says while no file is picked: the list's own empty text, or how to pick one.</summary>
    public string NoSelectionText => HasFiles ? "Pick a file in the list to see its diff." : EmptyText;

    /// <summary>"c-41 to c-44": the checkpoints the listed files can be put back from.</summary>
    public string CheckpointRange => _changes is { FirstCheckpoint: { } first, LastCheckpoint: { } last }
        ? first == last ? first : $"{first} to {last}"
        : "";

    public bool HasCheckpoints => CheckpointRange.Length > 0;

    public ReviewFileView? SelectedFile => _selected;

    public bool HasSelection => _selected is not null;

    /// <summary>The selected file's path with any hidden character shown (it is the file the actions below act on).</summary>
    public string DiffPath => SafeText.Reveal(_selected?.Path);

    /// <summary>"reversible · c-44" when a checkpoint covers the file; empty otherwise.</summary>
    public string ReversibleText => _selected?.File is { CheckpointId: { } checkpoint, Readable: true }
        ? _selected.File.Reverted ? $"reverted · {checkpoint}" : $"reversible · {checkpoint}"
        : "";

    /// <summary>"patch_file · turn 12": the call that last changed it.</summary>
    public string DiffSource => _selected?.File is { Tool: { } tool, Turn: { } turn }
        ? string.Create(CultureInfo.InvariantCulture, $"{tool} · turn {turn}")
        : "";

    /// <summary>The file has text to draw; otherwise <see cref="NoDiffText"/> says why not.</summary>
    public bool HasDiff => _diff is not null;

    public string NoDiffText => _selected?.File switch
    {
        null => "",
        { Readable: false } file => $"Corvid may not read {SafeText.Reveal(file.Path)}, so its change is listed by name only.",
        { Reverted: true } file => $"{SafeText.Reveal(file.Path)} is back at {file.CheckpointId}: nothing of this range is left in it to show.",
        { Note: { } note } file => $"{SafeText.Reveal(file.Path)}: {note}. Only its name and counts show.",
        _ => "This file has no changes to show.",
    };

    public IReadOnlyList<SideBySideRow> SideRows => _sideRows;

    public IReadOnlyList<DiffRow> UnifiedRows => _unifiedRows;

    /// <summary>The selected file's draft comments, in line order, for the diff to draw under their lines.</summary>
    public ObservableCollection<ReviewCommentView> FileComments { get; } = [];

    /// <summary>Every draft comment of the session, across its files.</summary>
    public int FileCommentsTotal => _sessionId is not null && _drafts.TryGetValue(_sessionId, out var drafts) ? drafts.Count : 0;

    /// <summary>"Send 2 comments"; "Send comments" when there are none (and the button is off).</summary>
    public string SendCommentsText => FileCommentsTotal switch
    {
        0 => "Send comments",
        1 => "Send 1 comment",
        var count => string.Create(CultureInfo.InvariantCulture, $"Send {count} comments"),
    };

    /// <summary>The comment field open under a line, or null.</summary>
    public CommentEditor? Editor
    {
        get => _editor;
        private set
        {
            if (SetProperty(ref _editor, value))
                OnPropertyChanged(nameof(IsEditing));
        }
    }

    public bool IsEditing => _editor is not null;

    /// <summary>"Revert this file to c-44".</summary>
    public string RevertText => _selected?.File.CheckpointId is { } checkpoint ? $"Revert this file to {checkpoint}" : "Revert this file";

    /// <summary>What the last action did ("Sent 2 comments to the session."), in its tone; empty otherwise.</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value))
                OnPropertyChanged(nameof(HasNotice));
        }
    }

    public Tone NoticeTone
    {
        get => _noticeTone;
        private set => SetProperty(ref _noticeTone, value);
    }

    public bool HasNotice => _notice.Length > 0;

    public RelayCommand<DiffLineRef> CommentOnLineCommand { get; }

    public AsyncRelayCommand SendCommentsCommand { get; }

    public RelayCommand AskAboutFileCommand { get; }

    public AsyncRelayCommand OpenInEditorCommand { get; }

    public AsyncRelayCommand RevertFileCommand { get; }

    public RelayCommand OpenRewindCommand { get; }

    /// <summary>The file to select when the list next loads (the demo's moment selects SeccompTests.cs).</summary>
    public void PreferFile(string path)
    {
        _preferredPath = path;
        if (Files.FirstOrDefault(file => file.Path == path) is { } found)
            Select(found);
    }

    /// <summary>Adds a draft comment on a line of a file of the current session (the demo's two drafts, and Save).</summary>
    public void AddComment(string path, DiffSide side, int line, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (_sessionId is null || string.IsNullOrWhiteSpace(text))
            return;
        var lineText = LineText(path, side, line);
        if (!_drafts.TryGetValue(_sessionId, out var drafts))
            _drafts[_sessionId] = drafts = [];
        drafts.Add(new ReviewCommentView(new ReviewComment(path, side, line, lineText, text.Trim()), RemoveComment));
        CommentsChanged();
    }

    /// <summary>Loads the list again for the current session and scope, keeping the selection where it can.</summary>
    public void Refresh() => _ = RefreshAsync();

    /// <summary>Selects the next or the previous file (Up and Down on the list); returns the file now selected.</summary>
    public ReviewFileView? MoveSelection(int delta)
    {
        if (Files.Count == 0)
            return null;
        var index = _selected is null ? (delta > 0 ? -1 : Files.Count) : Files.IndexOf(_selected);
        Select(Files[Math.Clamp(index + delta, 0, Files.Count - 1)]);
        return _selected;
    }

    private async Task RefreshAsync()
    {
        var sessionId = _sessionId;
        if (sessionId is null)
        {
            Apply(null);
            return;
        }

        try
        {
            var changes = await _service.GetChangesAsync(sessionId, Scope).ConfigureAwait(true);
            if (sessionId == _sessionId && changes.Scope == Scope)
                Apply(changes);
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            Apply(null);
            Say(exception.Message, Tone.Red);
        }
    }

    private void Apply(ReviewChanges? changes)
    {
        _changes = changes;
        var keep = _selected?.Path ?? _preferredPath;
        Files.Clear();
        foreach (var file in changes?.Files ?? [])
        {
            ReviewFileView? view = null;
            view = new ReviewFileView(file, new RelayCommand(() => Select(view!)));
            Files.Add(view);
        }

        _selected = null;
        var turn = changes?.Turn ?? 0;
        _thisTurn.Label = turn > 0 ? string.Create(CultureInfo.InvariantCulture, $"This turn (turn {turn})") : "This turn";
        _sinceReview.Label = changes?.ReviewPoint is { } point
            ? string.Create(CultureInfo.InvariantCulture, $"Since last review (turn {point.Turn})")
            : "Since last review";
        var next = Files.FirstOrDefault(file => file.Path == keep) ?? Files.FirstOrDefault(file => file.File.HasText) ?? Files.FirstOrDefault();
        Select(next, force: true);
        OnPropertyChanged(nameof(FilesSummary));
        OnPropertyChanged(nameof(HasFiles));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(NoSelectionText));
        OnPropertyChanged(nameof(CheckpointRange));
        OnPropertyChanged(nameof(HasCheckpoints));
        OnPropertyChanged(nameof(ReviewPointText));
        CommentsChanged();
        if (changes is { Scope: ReviewScope.SinceLastReview } && changes.SessionId == _shell.CurrentSession?.Id)
            SetBadge(changes);
    }

    /// <summary>The rail's Review badge: the open session's readable files changed since its last review point, less those put back.</summary>
    private void SetBadge(ReviewChanges changes) => _shell.SetBadge(Area.Review, changes.Files.Count(file => file.Readable && !file.Reverted));

    /// <summary>The badge again, whatever scope the list shows: it always counts "since last review".</summary>
    private async Task RefreshBadgeAsync(string sessionId)
    {
        if (Scope == ReviewScope.SinceLastReview || sessionId != _shell.CurrentSession?.Id)
            return;
        try
        {
            var changes = await _service.GetChangesAsync(sessionId, ReviewScope.SinceLastReview).ConfigureAwait(true);
            if (sessionId == _shell.CurrentSession?.Id)
                SetBadge(changes);
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException)
        {
            // The list's own refresh says what went wrong.
        }
    }

    private void Select(ReviewFileView? file, bool force = false)
    {
        if (!force && ReferenceEquals(file, _selected))
            return;
        if (_selected is not null)
            _selected.IsSelected = false;
        _selected = file;
        if (file is not null)
        {
            file.IsSelected = true;
            _preferredPath = file.Path;
        }

        // A list that reloads under an open comment field (a call wrote a file) keeps the field and its text.
        if (_editor is not null && _editor.Path != file?.Path)
            Editor = null;
        _diff = file?.File.Diff();
        _sideRows = DiffFolds.Trim(_diff?.SideBySide() ?? [], row => row.IsSkip);
        _unifiedRows = DiffFolds.Trim(_diff?.Unified() ?? [], row => row.Kind == DiffRowKind.Skip);
        OnPropertyChanged(nameof(SelectedFile));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(DiffPath));
        OnPropertyChanged(nameof(ReversibleText));
        OnPropertyChanged(nameof(DiffSource));
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(NoDiffText));
        OnPropertyChanged(nameof(SideRows));
        OnPropertyChanged(nameof(UnifiedRows));
        OnPropertyChanged(nameof(RevertText));
        RebuildFileComments();
        CommentOnLineCommand.NotifyCanExecuteChanged();
        AskAboutFileCommand.NotifyCanExecuteChanged();
        OpenInEditorCommand.NotifyCanExecuteChanged();
        RevertFileCommand.NotifyCanExecuteChanged();
    }

    private void OpenEditor(DiffLineRef line)
    {
        if (_selected is null)
            return;
        Editor = new CommentEditor(_selected.Path, line, SaveEditor, () => Editor = null);
    }

    private void SaveEditor(CommentEditor editor)
    {
        if (!ReferenceEquals(editor, _editor))
            return;
        AddComment(editor.Path, editor.Side, editor.Number, editor.Text);
        Editor = null;
    }

    private void RemoveComment(ReviewCommentView comment)
    {
        if (_sessionId is not null && _drafts.TryGetValue(_sessionId, out var drafts) && drafts.Remove(comment))
            CommentsChanged();
    }

    private void CommentsChanged()
    {
        RebuildFileComments();
        var drafts = _sessionId is not null && _drafts.TryGetValue(_sessionId, out var list) ? list : [];
        foreach (var file in Files)
            file.Comments = drafts.Count(comment => comment.Path == file.Path);
        OnPropertyChanged(nameof(FileCommentsTotal));
        OnPropertyChanged(nameof(SendCommentsText));
        SendCommentsCommand.NotifyCanExecuteChanged();
    }

    private void RebuildFileComments()
    {
        FileComments.Clear();
        if (_selected is null || _sessionId is null || !_drafts.TryGetValue(_sessionId, out var drafts))
            return;
        foreach (var comment in drafts.Where(comment => comment.Path == _selected.Path).OrderBy(comment => comment.Line).ThenBy(comment => comment.Side))
            FileComments.Add(comment);
    }

    private async Task SendCommentsAsync()
    {
        if (_sessionId is not { } sessionId || !_drafts.TryGetValue(sessionId, out var drafts) || drafts.Count == 0)
            return;
        var order = Files.Select(file => file.Path).ToList();
        var comments = drafts
            .OrderBy(comment => order.IndexOf(comment.Path) is var index && index >= 0 ? index : int.MaxValue)
            .ThenBy(comment => comment.Path, StringComparer.Ordinal)
            .ThenBy(comment => comment.Line)
            .Select(comment => comment.Comment)
            .ToList();
        try
        {
            var point = await _service.SendCommentsAsync(sessionId, comments).ConfigureAwait(true);
            drafts.Clear();
            Say(string.Create(CultureInfo.InvariantCulture,
                $"Sent {Plural(comments.Count, "comment")} to the session as one message at {Local(point.At):HH:mm}. The review point is now turn {point.Turn}."), Tone.Green);
            await RefreshAsync().ConfigureAwait(true);
            // The badge counts what is new since the review point the comments just set, whatever scope the list shows.
            await RefreshBadgeAsync(sessionId).ConfigureAwait(true);
        }
        catch (InvalidOperationException exception)
        {
            Say(exception.Message, Tone.Red);
        }
    }

    private void AskAboutFile()
    {
        if (_selected is null || _sessionId is null)
            return;
        var session = _sessions.Get(_sessionId);
        var mention = $"@{_selected.Path} ";
        session.Draft = session.Draft.Length == 0 ? mention : $"{session.Draft.TrimEnd()} {mention}";
        _shell.Navigate(Area.Sessions);
    }

    private async Task OpenInEditorAsync()
    {
        if (_selected is null)
            return;
        var line = FileComments.FirstOrDefault()?.Line ?? FirstChangedLine();
        try
        {
            var ran = await _service.OpenInEditorAsync(_selected.Path, line).ConfigureAwait(true);
            Say($"Opened in the editor: {SafeText.Reveal(ran)}", Tone.Grey);
        }
        catch (InvalidOperationException exception)
        {
            // No editor configured, or one the window cannot run: the reason is the notice.
            Say(SafeText.Clean(exception.Message), Tone.Red);
        }
    }

    private async Task RevertFileAsync()
    {
        if (_selected?.File is not { CheckpointId: { } checkpoint } file || _sessionId is not { } sessionId)
            return;
        FileRevertResult result;
        try
        {
            result = await _service.RevertFileAsync(sessionId, file.Path, checkpoint).ConfigureAwait(true);
        }
        catch (InvalidOperationException exception)
        {
            Say(SafeText.Clean(exception.Message), Tone.Red);
            return;
        }
        // The name the person acted on, with any hidden character in it shown.
        var name = SafeText.Reveal(System.IO.Path.GetFileName(result.Path));
        if (result.Refused)
            Say($"{name} was not put back: {result.Reason}. Nothing was overwritten.", Tone.Red);
        else
            Say(result.Merged
                ? $"{name} is back at {result.CheckpointId}, merged three ways with your own edit."
                : $"{name} is back at {result.CheckpointId}.", Tone.Green);
        await RefreshAsync().ConfigureAwait(true);
        await RefreshBadgeAsync(sessionId).ConfigureAwait(true);
    }

    private int FirstChangedLine()
    {
        foreach (var row in _unifiedRows)
        {
            if (row.Kind is DiffRowKind.Added or DiffRowKind.Removed)
                return row.NewNumber ?? row.OldNumber ?? 1;
        }

        return 1;
    }

    private string LineText(string path, DiffSide side, int line)
    {
        var file = Files.FirstOrDefault(candidate => candidate.Path == path)?.File;
        var text = side == DiffSide.Old ? file?.OldText : file?.NewText;
        var lines = FileDiff.SplitLines(text);
        return line >= 1 && line <= lines.Count ? lines[line - 1] : "";
    }

    private void Say(string text, Tone tone)
    {
        NoticeTone = tone;
        Notice = text;
    }

    private void SetSession(string? sessionId)
    {
        if (sessionId == _sessionId && _changes is not null)
            return;
        _sessionId = sessionId;
        Editor = null;
        Notice = "";
        OnPropertyChanged(nameof(SessionTitle));
        OnPropertyChanged(nameof(HasSession));
        AskAboutFileCommand.NotifyCanExecuteChanged();
        Refresh();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentSession))
            SetSession(_shell.CurrentSession?.Id);
    }

    private void OnChangesChanged(object? sender, string sessionId)
    {
        if (sessionId != _sessionId)
            return;
        Refresh();
        // The list may show another scope; the rail's badge still follows "since last review".
        _ = RefreshBadgeAsync(sessionId);
    }

    private DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone);

    private static string Plural(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");
}
