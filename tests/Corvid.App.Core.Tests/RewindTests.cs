using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Rewind;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Tests;

public sealed class RewindTests
{
    private static AppComposition Compose(params string[] args)
    {
        Assert.True(AppOptions.TryParse(["--demo", .. args], out var options, out var error), error);
        return new AppComposition(options, new InlineDispatcher());
    }

    /// <summary>A ledger with no entries whose last restore was refused before any file was looked at.</summary>
    private sealed class RefusedRewind(RestoreOutcome last) : IRewindService
    {
        public event EventHandler<string>? TimelineChanged { add { } remove { } }

        public Task<RewindTimeline> GetTimelineAsync(string sessionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RewindTimeline(sessionId) { LastRestore = last });

        public Task<RestorePlan> PlanAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RestorePreviewFile>> PreviewAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<RestoreOutcome> RestoreAsync(string sessionId, string checkpointId, RewindScope scope, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void ARestoreRefusedBeforeAnyFileSaysWhy()
    {
        var time = ManualTimeProvider.Demo();
        var shell = new Shell.ShellViewModel(new Shell.StatusBarViewModel());
        var item = shell.CreateSession("cli-busy", "busy", Shell.SessionState.Idle, "1 m");
        shell.SetSessions([new Shell.SessionGroup("Recent", [item])]);
        shell.SelectSession(item);
        // G-12: another process drives the session, so nothing was restored; the reason is the panel's line.
        var rewind = new RewindViewModel(new RefusedRewind(new RestoreOutcome("rollback to before cp-1", time.GetUtcNow(), RewindScope.FilesAndConversation)
        {
            Refused = true,
            Detail = "Session cli-busy is driven by corvid chat (pid 7) since 2026-10-24 14:00 UTC; only that process can post to it, change it or restore it until it exits. Nothing was changed.",
        }), shell, time);

        Assert.True(rewind.LastRestoreRefused);
        Assert.StartsWith("Session cli-busy is driven by corvid chat (pid 7)", rewind.LastRestoreSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLedgerIsRewindPngsNewestFirst()
    {
        var rewind = Compose("--page", "rewind").Rewind;

        Assert.Equal("Fix the flaky sandbox test on CI · 8 ledger entries", rewind.Subtitle);
        Assert.Equal(["14:04", "14:03", "14:02", "13:59", "13:52", "13:41", "13:30", "13:22"], rewind.Entries.Select(entry => entry.When));
        Assert.Equal(["shell", "patch_file", "patch_file", "write_file", "patch_file", "", "shell", "write_file"], rewind.Entries.Select(entry => entry.Tool));
        Assert.Equal("· c-42 → c-43", rewind.Entries[1].Detail);
        Assert.Equal("dotnet build · c-33 → c-34", rewind.Entries[6].Detail);
        Assert.Equal(DemoFiles.SeccompTestsPath, rewind.Entries[1].Paths);
        Assert.Equal(("+9", "−4"), (rewind.Entries[1].AddedText, rewind.Entries[1].RemovedText));

        // Turn 12's shell call is still running: nothing of it to go back from.
        var running = rewind.Entries[0];
        Assert.Equal(("running", Tone.Blue, false), (running.ChangeTag, running.ChangeTagTone, running.IsSelectable));

        var restore = rewind.Entries[5];
        Assert.True(restore.IsRestore);
        Assert.Equal(("undo of turn 8 · c-35", "2 put back, 1 merged", "restore point"), (restore.Detail, restore.Paths, restore.ChangeNote));
        Assert.Equal(("no files changed", "0", false), (rewind.Entries[6].Paths, rewind.Entries[6].ChangeNote, rewind.Entries[6].PathsAreFiles));

        Assert.Equal("c-38", rewind.Selected?.CheckpointId);
        Assert.True(rewind.Entries[3].IsSelected);
    }

    [Fact]
    public void ThePlanForC38IsRewindPngs()
    {
        var rewind = Compose("--page", "rewind").Rewind;

        Assert.Equal("Go back to before c-38 · turn 10, 13:59", rewind.PlanTitle);
        Assert.Equal("/rollback 4", rewind.PlanCommand);
        Assert.Equal("3", rewind.PlanFileCount);
        Assert.Equal([".github/workflows/ci.yml", DemoFiles.BubblewrapPath, DemoFiles.SeccompTestsPath], rewind.PlanFiles.Select(file => file.Path));
        Assert.Equal(["to c-38 · −3", "to c-38 · −19 +5", "to c-38 · merge"],
            rewind.PlanFiles.Select(file => string.Join(' ', new[] { file.Prefix, file.ShowCounts ? file.RemoveText : file.Word, file.ShowCounts ? file.AddText : "" }.Where(part => part.Length > 0))));
        var note = Assert.Single(rewind.PlanNotes);
        Assert.Equal(("SeccompTests.cs", Tone.Amber), (note.FileName, note.Tone));
        Assert.Equal("was edited by you at 14:05, after c-43. The restore merges three ways and stops on a conflict rather than overwrite your edit.", note.Text);
        Assert.Equal("turns 10 to 12", rewind.ConversationRange);
        Assert.Equal("Three turns leave the transcript. They stay in the log and in Audit, and the fork tree keeps a branch for them.", rewind.ConversationText);
        Assert.Equal(["Files and conversation: the workspace and the transcript as they were at c-38",
            "Conversation only: keep the files, drop turns 10 to 12 from the context",
            "Files only: put the files back, keep the conversation"], rewind.Options.Select(option => option.AutomationName));
        Assert.Equal([true, false, false], rewind.Options.Select(option => option.IsSelected));
    }

    [Fact]
    public void TheLastRestoreIsTheUndoOfTurn8()
    {
        var rewind = Compose("--page", "rewind").Rewind;

        Assert.Equal("Last restore · 13:41 · undo of turn 8", rewind.LastRestoreTitle);
        Assert.Equal(("done", Tone.Green), (rewind.LastRestoreTag, rewind.LastRestoreTone));
        Assert.Equal("restored 2 · merged 1 · refused 0 · restore checkpoint c-35", rewind.LastRestoreSummary);
        Assert.Equal(["src/Corvid.Cli/Chat/ChatRenderer.cs restored", "tests/Corvid.Cli.Tests/RendererTests.cs restored", "README.md merged with your edit"],
            rewind.LastRestoreFiles.Select(file => $"{file.Path} {file.What}"));
    }

    [Fact]
    public async Task RestoringFilesAndConversationRewindsTheSessionAndListsTheResult()
    {
        var composition = Compose("--page", "rewind");
        var rewind = composition.Rewind;
        var session = composition.SessionHub.Get(DemoSessionService.MainSessionId);

        await rewind.RestoreCommand.ExecuteAsync();

        // Turns 10 to 12 left the transcript; the marker says what went back, exactly as the result lists it.
        Assert.DoesNotContain(session.State.Transcript, entry => entry.Turn >= 10);
        Assert.False(session.State.Running);
        var marker = Assert.IsType<RewindEntry>(session.State.Transcript[^1]).Rewind;
        Assert.Equal((10, RewindScope.FilesAndConversation, false), (marker.Turn, marker.Scope, marker.Refused));
        Assert.Equal([".github/workflows/ci.yml", DemoFiles.BubblewrapPath], marker.Restored);
        Assert.Equal([DemoFiles.SeccompTestsPath], marker.Merged);
        Assert.Equal("c-49", marker.CheckpointId);

        // The ledger keeps every entry and gains the restore point at the top.
        Assert.Equal(9, rewind.Entries.Count);
        Assert.Equal(("14:05", "rollback to before c-38 · c-49", "2 put back, 1 merged"), (rewind.Entries[0].When, rewind.Entries[0].Detail, rewind.Entries[0].Paths));
        Assert.Equal("Last restore · 14:05 · rollback to before c-38", rewind.LastRestoreTitle);
        Assert.Equal("restored 2 · merged 1 · refused 0 · restore checkpoint c-49", rewind.LastRestoreSummary);
        Assert.Null(rewind.Selected);
        Assert.False(rewind.HasPlan);
    }

    [Fact]
    public async Task ASecondRestorePutsBackOnlyWhatTheFirstLeft()
    {
        var composition = Compose("--page", "rewind");
        var rewind = composition.Rewind;
        var session = composition.SessionHub.Get(DemoSessionService.MainSessionId);
        await rewind.RestoreCommand.ExecuteAsync();
        Assert.Equal(9, session.State.Turn);

        // c-40's call was put back by the first restore and its turn has left: nothing of it is left to go back from.
        rewind.Entries.Single(entry => entry.CheckpointId == "c-40").Select.Execute(null);
        Assert.False(rewind.HasPlanFiles);
        Assert.Equal(("no turns", "No turns leave the transcript."), (rewind.ConversationRange, rewind.ConversationText));

        // Before c-36 (turn 9's README.md patch): that file alone, and turn 9 alone leaves.
        rewind.Entries.Single(entry => entry.CheckpointId == "c-36").Select.Execute(null);
        Assert.Equal(["README.md"], rewind.PlanFiles.Select(file => file.Path));
        Assert.Equal(("turn 9", "One turn leaves the transcript. It stays in the log and in Audit, and the fork tree keeps a branch for it."),
            (rewind.ConversationRange, rewind.ConversationText));

        await rewind.RestoreCommand.ExecuteAsync();

        var marker = Assert.IsType<RewindEntry>(session.State.Transcript[^1]).Rewind;
        Assert.Equal((9, "c-50"), (marker.Turn, marker.CheckpointId));
        Assert.Equal(["README.md"], marker.Restored);
        Assert.Empty(marker.Merged);
        Assert.Equal(8, session.State.Turn);
        Assert.Equal(10, rewind.Entries.Count);
        Assert.Equal(("rollback to before c-36 · c-50", "1 put back"), (rewind.Entries[0].Detail, rewind.Entries[0].Paths));
        // The first restore's row still has its own result.
        Assert.Equal(("rollback to before c-38 · c-49", "2 put back, 1 merged"), (rewind.Entries[1].Detail, rewind.Entries[1].Paths));
        Assert.Equal("restored 1 · merged 0 · refused 0 · restore checkpoint c-50", rewind.LastRestoreSummary);
    }

    [Fact]
    public async Task GoingBackToBeforeARestorePointPutsBackItsOwnPaths()
    {
        var composition = Compose("--page", "rewind");
        var rewind = composition.Rewind;
        await rewind.RestoreCommand.ExecuteAsync();

        rewind.Entries[0].Select.Execute(null);

        Assert.Equal("Go back to before c-49 · turn 12, 14:05", rewind.PlanTitle);
        Assert.Equal([".github/workflows/ci.yml", DemoFiles.BubblewrapPath, DemoFiles.SeccompTestsPath], rewind.PlanFiles.Select(file => file.Path));
        Assert.Equal(["to c-49", "to c-49", "to c-49 ·"], rewind.PlanFiles.Select(file => file.Prefix));
        Assert.Equal("merge", rewind.PlanFiles[2].Word);
    }

    [Fact]
    public void ARestoreShowsTheHiddenCharactersOfThePathsItActsOn()
    {
        const string Path = "src/evil\u202Ecs.txt";
        var restore = new LedgerEntryView(new LedgerEntry("c-50", LedgerEntryKind.Restore, DateTimeOffset.UnixEpoch, 3) { Target = $"revert {Path} to c-1" },
            TimeZoneInfo.Utc, new RelayCommand(() => { }));
        Assert.Equal("revert src/evil⟨U+202E⟩cs.txt to c-1 · c-50", restore.Detail);

        var putBack = new PutBackFileView(new PutBackFile(Path, PutBackKind.Restore, 2, 1), "c-1");
        Assert.Equal("src/evil⟨U+202E⟩cs.txt, back to c-1, 2 lines removed, 1 restored", putBack.AutomationName);
        Assert.Equal("src/evil⟨U+202E⟩cs.txt, restored", new OutcomeFileView(Path, "restored", false).AutomationName);
    }

    [Fact]
    public async Task AConflictRefusesTheRestoreAndChangesNothing()
    {
        var composition = Compose("--page", "rewind");
        var rewind = composition.Rewind;
        var session = composition.SessionHub.Get(DemoSessionService.MainSessionId);
        var transcript = session.State.Transcript.Count;

        rewind.Entries[7].Select.Execute(null);
        Assert.Equal("Go back to before c-31 · turn 8, 13:22", rewind.PlanTitle);
        Assert.Equal("/rollback 7", rewind.PlanCommand);
        var conflict = Assert.Single(rewind.PlanNotes, note => note.IsConflict);
        Assert.Equal("notes.md", conflict.FileName);
        Assert.Equal("conflict", rewind.PlanFiles.Single(file => file.IsConflict).Word);

        await rewind.RestoreCommand.ExecuteAsync();

        Assert.True(session.State.Running);
        var marker = Assert.IsType<RewindEntry>(session.State.Transcript[^1]).Rewind;
        Assert.True(marker.Refused);
        Assert.Equal(transcript + 1, session.State.Transcript.Count);
        Assert.Equal(8, rewind.Entries.Count);
        Assert.Equal(("refused", Tone.Red, true), (rewind.LastRestoreTag, rewind.LastRestoreTone, rewind.LastRestoreRefused));
        Assert.Equal("refused 1 · nothing was restored or overwritten", rewind.LastRestoreSummary);
        // Nothing changed, so the plan stays open on the same entry.
        Assert.Equal("c-31", rewind.Selected?.CheckpointId);
        Assert.True(rewind.HasPlan);
        var file = Assert.Single(rewind.LastRestoreFiles);
        Assert.Equal("docs/notes.md, refused: you edited it at 13:35, after c-32, and going back to c-31 would delete it", file.AutomationName);
    }

    [Fact]
    public async Task ConversationOnlyKeepsTheFiles()
    {
        var composition = Compose("--page", "rewind");
        var rewind = composition.Rewind;

        rewind.Options[1].Choose.Execute(null);
        Assert.Equal(RewindScope.ConversationOnly, rewind.Scope);
        Assert.Equal([false, true, false], rewind.Options.Select(option => option.IsSelected));
        await rewind.RestoreCommand.ExecuteAsync();

        var marker = Assert.IsType<RewindEntry>(composition.SessionHub.Get(DemoSessionService.MainSessionId).State.Transcript[^1]).Rewind;
        Assert.Equal(RewindScope.ConversationOnly, marker.Scope);
        Assert.Empty(marker.Restored);
        Assert.Equal("files kept", rewind.Entries[0].Paths);
    }

    [Fact]
    public async Task ThePreviewShowsEachFilesDiffFromNowToRestored()
    {
        var rewind = Compose("--page", "rewind").Rewind;

        await rewind.PreviewCommand.ExecuteAsync();

        Assert.True(rewind.IsPreviewing);
        Assert.False(rewind.ShowPlan);
        Assert.Equal("Preview · going back to before c-38", rewind.PreviewTitle);
        Assert.Equal(3, rewind.PreviewFiles.Count);
        Assert.All(rewind.PreviewFiles, file => Assert.True(file.HasDiff));
        Assert.Equal("goes back · +0 −3", rewind.PreviewFiles[0].What);
        Assert.Equal("merges with your edit", rewind.PreviewFiles[2].What);

        rewind.ClosePreviewCommand.Execute(null);
        Assert.False(rewind.IsPreviewing);
    }

    [Fact]
    public void TheKeyboardMovesOverEntriesThatCannotBeSelected()
    {
        var rewind = Compose("--page", "rewind").Rewind;

        Assert.Equal("c-40", rewind.MoveSelection(-1)?.CheckpointId);
        Assert.Equal("c-42", rewind.MoveSelection(-1)?.CheckpointId);
        // The running call above cannot be selected: the selection stays.
        Assert.Equal("c-42", rewind.MoveSelection(-1)?.CheckpointId);
        Assert.Equal("c-40", rewind.MoveSelection(1)?.CheckpointId);
        Assert.Equal("Go back to before c-40 · turn 11, 14:02", rewind.PlanTitle);
    }

    [Fact]
    public void BackToTheSessionGoesToSessions()
    {
        var composition = Compose("--page", "rewind");

        composition.Rewind.BackToSessionCommand.Execute(null);

        Assert.Equal(Area.Sessions, composition.Shell.CurrentArea);
    }
}
