using Corvid.App.Core.Composition;
using Corvid.App.Core.Diffs;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Review;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Tests;

public sealed class ReviewTests
{
    private const string Seccomp = DemoFiles.SeccompTestsPath;

    private static AppComposition Compose(params string[] args)
    {
        Assert.True(AppOptions.TryParse(args, out var options, out var error), error);
        return new AppComposition(options, new InlineDispatcher());
    }

    [Fact]
    public void TheDemoOpensAtReviewPngsMoment()
    {
        var review = Compose("--page", "review").Review;

        Assert.Equal("Fix the flaky sandbox test on CI", review.SessionTitle);
        Assert.Equal(["This turn (turn 12)", "Since last review (turn 11)", "This session"], review.Scopes.Select(scope => scope.Label));
        Assert.Equal(ReviewScope.SinceLastReview, review.Scope);
        Assert.Equal("last review point: turn 11 · 2 comments sent at 14:02", review.ReviewPointText);
        Assert.Equal([DemoFiles.BubblewrapPath, Seccomp, ".github/workflows/ci.yml", "vault.json"], review.Files.Select(file => file.Path));
        Assert.Equal(["+19 −5", "+9 −4", "+3 −0"], review.Files.Where(file => file.ShowCounts).Select(file => $"{file.AddedText} {file.RemovedText}"));
        Assert.Equal("not readable by Corvid", review.Files[3].Note);
        Assert.Equal("3 changed · +31 −9", review.FilesSummary);
        // The checkpoints are the ledger's (Rewind.png's): the before checkpoint of the first call that changed each file.
        // Review.png says "c-41 to c-44", which names checkpoints that already hold the changes.
        Assert.Equal("c-38 to c-42", review.CheckpointRange);

        Assert.Equal(Seccomp, review.SelectedFile?.Path);
        Assert.True(review.Files[1].IsSelected);
        Assert.True(review.IsSideBySide);
        Assert.Equal(("reversible · c-42", "patch_file · turn 11"), (review.ReversibleText, review.DiffSource));
        Assert.Equal("Revert this file to c-42", review.RevertText);
        Assert.Equal([46, 54], review.FileComments.Select(comment => comment.Line));
        Assert.Equal("you · line 46 · draft", review.FileComments[0].Meta);
        Assert.Equal("Send 2 comments", review.SendCommentsText);
        Assert.True(review.SendCommentsCommand.CanExecute(null));
    }

    [Fact]
    public void EachFilesCountsAgreeWithTheDiffItDraws()
    {
        var review = Compose("--page", "review").Review;

        // SeccompTests.cs is left out: Review.png itself lists it +9 −4 beside the +6 −2 hunk it draws, and the session
        // demo's text is that hunk, which the side-by-side rows are pinned to.
        foreach (var file in review.Files.Where(file => file.File.NewText is not null && file.Path != Seccomp))
        {
            var diff = FileDiff.Compute(file.Path, file.File.OldText, file.File.NewText!);
            Assert.Equal((file.File.Added, file.File.Removed), (diff.Added, diff.Removed));
        }
    }

    [Fact]
    public void TheSideBySideRowsAreReviewPngsLines()
    {
        var review = Compose("--page", "review").Review;
        var rows = review.SideRows.Take(15).ToList();

        // Left: 41 to 51, then four empty cells beside the four added lines; right: 41 to 55.
        Assert.Equal([41, 42, 43, 44, 45, 46, 47, 48, 49, 50, 51, 0, 0, 0, 0], rows.Select(row => row.Left?.Number ?? 0));
        Assert.Equal(Enumerable.Range(41, 15), rows.Select(row => row.Right!.Number));
        Assert.Equal([44, 46], rows.Where(row => row.Left?.Kind == DiffRowKind.Removed).Select(row => row.Left!.Number));
        Assert.Equal([44, 46, 52, 53, 54, 55], rows.Where(row => row.Right?.Kind == DiffRowKind.Added).Select(row => row.Right!.Number));
        Assert.Contains(rows[5].Left!.Segments, segment => segment.Changed && segment.Text.Contains("Assert.Equal(0, control.ExitCode);", StringComparison.Ordinal));
        Assert.Equal("    private static bool UserNamespacesAreOff() =>", rows[12].Right!.Text);
        // No fold before the first hunk or after the last: the view starts at line 41 as the mockup does.
        Assert.DoesNotContain(review.SideRows, row => row.IsSkip);
        Assert.DoesNotContain(review.UnifiedRows, row => row.Kind == DiffRowKind.Skip);
    }

    [Fact]
    public void ALineTakesACommentThatCanBeRemoved()
    {
        var review = Compose("--page", "review").Review;
        var line = new DiffLineRef(DiffSide.New, 49, "        Assert.NotEqual(0, inside.ExitCode);");

        review.CommentOnLineCommand.Execute(line);
        var editor = Assert.IsType<CommentEditor>(review.Editor);
        Assert.Equal("Comment on line 49", editor.Title);
        Assert.False(editor.Save.CanExecute(null));
        editor.Text = "  Check the message too.  ";
        editor.Save.Execute(null);

        Assert.Null(review.Editor);
        Assert.Equal([46, 49, 54], review.FileComments.Select(comment => comment.Line));
        Assert.Equal("Check the message too.", review.FileComments[1].Text);
        Assert.Equal("Assert.NotEqual(0, inside.ExitCode);", review.FileComments[1].Comment.LineText.Trim());
        Assert.Equal("Send 3 comments", review.SendCommentsText);
        Assert.Equal(3, review.Files[1].Comments);

        review.FileComments[0].Remove.Execute(null);
        Assert.Equal([49, 54], review.FileComments.Select(comment => comment.Line));
        Assert.Equal("Send 2 comments", review.SendCommentsText);

        review.CommentOnLineCommand.Execute(new DiffLineRef(DiffSide.Old, 44, ""));
        Assert.Equal("Comment on line 44 before the change", review.Editor!.Title);
        review.Editor.Cancel.Execute(null);
        Assert.Null(review.Editor);
    }

    [Fact]
    public async Task SendingPostsOneMessageQuotingFileAndLineAndMovesTheReviewPoint()
    {
        var composition = Compose("--page", "review");
        var review = composition.Review;
        var session = composition.SessionHub.Get(DemoReviewService.MainSessionId);

        await review.SendCommentsCommand.ExecuteAsync();

        // Turn 12 is running, so the message waits in the queue behind "also check the retry count".
        var message = Assert.Single(session.State.Queue, queued => queued.Text.StartsWith("Review comments", StringComparison.Ordinal)).Text;
        Assert.StartsWith("Review comments (2):\n\n`tests/Corvid.Sandbox.Tests/SeccompTests.cs` line 46:\n> Assert.True(control.ExitCode == 0 || UserNamespacesAreOff(), control.StandardError);\nKeep the control strict on CI", message, StringComparison.Ordinal);
        Assert.Contains("`tests/Corvid.Sandbox.Tests/SeccompTests.cs` line 54:\n> File.Exists(\"/proc/sys/kernel/unprivileged_userns_clone\")\nAlso treat", message, StringComparison.Ordinal);
        Assert.Empty(review.FileComments);
        Assert.Equal("Send comments", review.SendCommentsText);
        Assert.False(review.SendCommentsCommand.CanExecute(null));
        Assert.Equal("last review point: turn 12 · 2 comments sent at 14:05", review.ReviewPointText);
        Assert.Equal("Since last review (turn 12)", review.Scopes[1].Label);
        Assert.Equal(Tone.Green, review.NoticeTone);
        Assert.StartsWith("Sent 2 comments to the session as one message at 14:05.", review.Notice, StringComparison.Ordinal);
        // Nothing changed after the new review point yet.
        Assert.Empty(review.Files);
        Assert.Equal("Nothing changed since the last review point. The next turn's changes show here.", review.EmptyText);
        // The diff column says so too, and the rail's badge follows the cleared range.
        Assert.False(review.HasSelection);
        Assert.Equal(review.EmptyText, review.NoSelectionText);
        Assert.Equal(0, composition.Shell.RailItem(Area.Review).Badge);
    }

    [Fact]
    public async Task ACallRunningAtTheSendCountsAsSinceTheReviewOnceItEnds()
    {
        Assert.True(AppOptions.TryParse(["--page", "review", "--scene", "approval"], out var options, out var error), error);
        var time = ManualTimeProvider.Demo();
        var composition = new AppComposition(options, new InlineDispatcher(), time);
        var review = composition.Review;

        // Turn 13 is waiting on a-32 when the comments go: the review point is turn 13, and nothing is new after it.
        await review.SendCommentsCommand.ExecuteAsync();
        Assert.Equal("last review point: turn 13 · 2 comments sent at 14:05", review.ReviewPointText);
        Assert.Empty(review.Files);

        // The call a-32 lets run writes in turn 13 itself, after the point: it counts.
        await composition.SessionService.AnswerApprovalAsync(DemoSessionService.MainSessionId, "a-32", new ApprovalAnswer(ApprovalChoice.Session), TestContext.Current.CancellationToken);
        time.Advance(5);

        var file = Assert.Single(review.Files);
        Assert.Equal(("build/", "c-45"), (file.Path, file.File.CheckpointId));
        Assert.Equal(1, composition.Shell.RailItem(Area.Review).Badge);
    }

    [Fact]
    public void ReviewShowsTheHiddenCharactersOfAPathItActsOn()
    {
        const string Path = "src/evil‮cs.txt";
        var composition = Compose("--page", "review");
        var review = new ReviewViewModel(new OneFileReview(new ReviewFile(Path, 1, 0) { Readable = false }), composition.Shell, composition.SessionHub, composition.Time);

        Assert.Equal("src/evil⟨U+202E⟩cs.txt", review.DiffPath);
        Assert.Equal("Corvid may not read src/evil⟨U+202E⟩cs.txt, so its change is listed by name only.", review.NoDiffText);
        Assert.StartsWith("src/evil⟨U+202E⟩cs.txt, ", review.Files[0].AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRevertNoticeShowsTheHiddenCharactersOfTheName()
    {
        const string Path = "src/evil‮cs.txt";
        var composition = Compose("--page", "review");
        var review = new ReviewViewModel(new OneFileReview(new ReviewFile(Path, 1, 0) { NewText = "x\n", CheckpointId = "c-1" }), composition.Shell, composition.SessionHub, composition.Time);

        await review.RevertFileCommand.ExecuteAsync();
        Assert.Equal("evil⟨U+202E⟩cs.txt is back at c-1.", review.Notice);

        await review.OpenInEditorCommand.ExecuteAsync();
        Assert.Equal("Opened in the editor: edit src/evil⟨U+202E⟩cs.txt", review.Notice);
    }

    /// <summary>A review service with one changed file, for what the view model makes of its path.</summary>
    private sealed class OneFileReview(ReviewFile file) : IReviewService
    {
        public event EventHandler<string>? ChangesChanged { add { } remove { } }

        public Task<ReviewChanges> GetChangesAsync(string sessionId, ReviewScope scope, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReviewChanges(sessionId, scope) { Files = [file] });

        public Task<ReviewPoint> SendCommentsAsync(string sessionId, IReadOnlyList<ReviewComment> comments, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FileRevertResult> RevertFileAsync(string sessionId, string path, string checkpointId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FileRevertResult(path, checkpointId, false, false));

        public Task<string> OpenInEditorAsync(string path, int line, CancellationToken cancellationToken = default) =>
            Task.FromResult($"edit {path}");
    }

    [Fact]
    public void TheBadgeCountsTheReadableFilesSinceTheLastReview()
    {
        var composition = Compose("--page", "review");

        Assert.Equal(3, composition.Shell.RailItem(Area.Review).Badge);
        composition.Review.ScopeIndex = 2;
        Assert.Equal(3, composition.Shell.RailItem(Area.Review).Badge);
    }

    [Fact]
    public void TheScopePickerChoosesTheTurnOrTheSession()
    {
        var review = Compose("--page", "review").Review;

        review.ScopeIndex = 0;
        Assert.Equal([DemoFiles.BubblewrapPath, Seccomp], review.Files.Select(file => file.Path));
        Assert.Equal("2 changed · +16 −4", review.FilesSummary);
        Assert.Equal(Seccomp, review.SelectedFile?.Path);

        review.ScopeIndex = 2;
        Assert.Equal(4, review.Files.Count);
    }

    [Fact]
    public void AFileCorvidMayNotReadIsListedByNameWithNoDiff()
    {
        var review = Compose("--page", "review").Review;

        review.Files[3].Select.Execute(null);

        Assert.False(review.HasDiff);
        Assert.Empty(review.SideRows);
        Assert.Equal("Corvid may not read vault.json, so its change is listed by name only.", review.NoDiffText);
        Assert.False(review.CommentOnLineCommand.CanExecute(new DiffLineRef(DiffSide.New, 1, "")));
        Assert.False(review.RevertFileCommand.CanExecute(null));
        Assert.Equal("vault.json, not readable by Corvid", review.Files[3].AutomationName);
    }

    [Fact]
    public void AskingAboutAFileStartsAMessageWithItsPathInTheSession()
    {
        var composition = Compose("--page", "review");
        var review = composition.Review;

        review.AskAboutFileCommand.Execute(null);

        Assert.Equal(Area.Sessions, composition.Shell.CurrentArea);
        Assert.Equal($"@{Seccomp} ", composition.SessionHub.Get(DemoReviewService.MainSessionId).Draft);
    }

    [Fact]
    public async Task RevertingAFileMergesWithTheEditMadeByHand()
    {
        var composition = Compose("--page", "review");
        var review = composition.Review;
        var session = composition.SessionHub.Get(DemoReviewService.MainSessionId);

        await review.RevertFileCommand.ExecuteAsync();

        Assert.Equal("SeccompTests.cs is back at c-42, merged three ways with your own edit.", review.Notice);
        // It stays listed, last and selected, as reverted; it is no longer counted, here or on the rail.
        var file = review.Files[^1];
        Assert.Equal((Seccomp, false, "reverted to c-42"), (file.Path, file.ShowCounts, file.Note));
        Assert.Same(file, review.SelectedFile);
        Assert.Equal("reverted · c-42", review.ReversibleText);
        Assert.False(review.HasDiff);
        Assert.Equal($"{Seccomp} is back at c-42: nothing of this range is left in it to show.", review.NoDiffText);
        Assert.False(review.RevertFileCommand.CanExecute(null));
        Assert.Equal("2 changed · +22 −5", review.FilesSummary);
        Assert.Equal("c-38 to c-40", review.CheckpointRange);
        Assert.Equal(2, composition.Shell.RailItem(Area.Review).Badge);

        // The revert went on the session's stream as a restore: the turn runs on, the change lists drop the file, and
        // Rewind lists the restore point with what it put back.
        Assert.True(session.State.Running);
        Assert.DoesNotContain(session.State.SessionChanges, change => change.Path == Seccomp);
        var marker = Assert.IsType<RewindEntry>(session.State.Transcript[^1]).Rewind;
        Assert.Equal((RewindScope.FilesOnly, "revert", "c-49"), (marker.Scope, marker.Reason, marker.CheckpointId));
        Assert.Equal([Seccomp], marker.Merged);
        var rewind = composition.Rewind;
        Assert.Equal(("revert SeccompTests.cs to c-42 · c-49", "0 put back, 1 merged"), (rewind.Entries[0].Detail, rewind.Entries[0].Paths));
        Assert.Equal("Last restore · 14:05 · revert SeccompTests.cs to c-42", rewind.LastRestoreTitle);
    }

    [Fact]
    public async Task AfterARestoreReviewListsOnlyWhatIsStillChanged()
    {
        var composition = Compose("--page", "review");
        var review = composition.Review;
        // The list shows the session's scope; the badge still follows "since last review".
        review.ScopeIndex = 2;
        Assert.Equal(3, composition.Shell.RailItem(Area.Review).Badge);

        // Rewind.png's restore: back to before c-38, files and conversation.
        await composition.Rewind.RestoreCommand.ExecuteAsync();

        Assert.Equal(["vault.json"], review.Files.Select(file => file.Path));
        Assert.Equal("nothing changed", review.FilesSummary);
        Assert.False(review.HasCheckpoints);
        Assert.Equal(0, composition.Shell.RailItem(Area.Review).Badge);
        // Turns 10 to 12 left: "this turn" is turn 9, whose README.md patch the ledger still has.
        Assert.Equal("This turn (turn 9)", review.Scopes[0].Label);
        review.ScopeIndex = 0;
        var readme = Assert.Single(review.Files);
        Assert.Equal(("README.md", "+2", "−1"), (readme.Path, readme.AddedText, readme.RemovedText));
        Assert.Equal("c-36", review.CheckpointRange);
    }

    [Fact]
    public async Task OpenInEditorSaysWhatWouldRun()
    {
        var review = Compose("--page", "review").Review;

        await review.OpenInEditorCommand.ExecuteAsync();

        Assert.Equal("Opened in the editor: code --goto tests/Corvid.Sandbox.Tests/SeccompTests.cs:46", review.Notice);
    }

    [Fact]
    public void ASessionWithoutChangesSaysSo()
    {
        var composition = Compose("--page", "review", "--session", DemoSessionService.VaultSessionId);
        var review = composition.Review;

        Assert.Empty(review.Files);
        Assert.Empty(review.FileComments);
        Assert.False(review.HasSelection);
        Assert.Equal("nothing changed", review.FilesSummary);
    }

    [Fact]
    public void TheMessageQuotesTheOldSideAsBeforeTheChange()
    {
        var text = ReviewMessage.Compose([new ReviewComment("a.cs", DiffSide.Old, 3, "  int x;  ", " why? ")]);

        Assert.Equal("Review comments (1):\n\n`a.cs` line 3 (before the change):\n> int x;\nwhy?", text);
        Assert.Equal("", ReviewMessage.Compose([]));
    }
}
