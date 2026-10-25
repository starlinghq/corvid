using System.Diagnostics;
using Corvid.Abstractions.Events;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Review;
using Corvid.Host.Tests;

namespace Corvid.App.Engine.Tests;

/// <summary>Review on the engine: the ledger's changes per scope, comments as one message, one file put back, and the editor.</summary>
public sealed class EngineReviewTests
{
    private const string Story = "one\ntwo\nthree\nfour\nfive\n";
    private const string Corvids = "ONE\ntwo\nthree\nfour\nfive\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Turn 1 rewrites story.txt; turn 2 creates notes.md and edits story.txt again.</summary>
    private static async Task<string> TwoTurnsAsync(HistoryFixture fixture)
    {
        fixture.Write("story.txt", Story);
        var id = fixture.Session();
        await fixture.WriteTurnAsync(id, "call-1", "story.txt", Corvids, "capitalise the story");
        fixture.Append(id, new UserMessage("more"));
        await fixture.WriteCallAsync(id, "call-2", "notes.md", "a note\n");
        await fixture.WriteCallAsync(id, "call-3", "story.txt", Corvids + "six\n");
        fixture.Append(id, new AssistantMessage("Done."));
        return id;
    }

    [Fact]
    public async Task TheSessionsChangesRunFromBeforeTheFirstCallToTheFileNow()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = await TwoTurnsAsync(fixture);
        using var review = fixture.Review();
        fixture.Runtime.TryGetSession(id, out var session);
        var ledger = session.Checkpoints();

        var changes = await review.GetChangesAsync(id, ReviewScope.ThisSession, Ct);

        Assert.Equal(2, changes.Turn);
        Assert.Null(changes.ReviewPoint);
        Assert.Equal(["story.txt", "notes.md"], changes.Files.Select(file => file.Path));
        var story = changes.Files[0];
        Assert.Equal((Story, Corvids + "six\n"), (story.OldText, story.NewText));
        Assert.Equal((2, 1), (story.Added, story.Removed));
        Assert.Equal(ledger[0].Before.Checkpoint.CheckpointId, story.CheckpointId);
        Assert.Equal(("write_file", 2), (story.Tool, story.Turn));
        var notes = changes.Files[1];
        Assert.Null(notes.OldText);
        Assert.Equal(("a note\n", 1, 0), (notes.NewText, notes.Added, notes.Removed));
        Assert.Equal(ledger[0].Before.Checkpoint.CheckpointId, changes.FirstCheckpoint);
        Assert.Equal(ledger[^1].After!.Checkpoint.CheckpointId, changes.LastCheckpoint);
    }

    [Fact]
    public async Task ThisTurnListsOnlyTheLatestTurnsCallsAndThePersonsEditShows()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = await TwoTurnsAsync(fixture);
        fixture.Write("story.txt", Corvids + "six\nseven, by me\n");
        using var review = fixture.Review();

        var changes = await review.GetChangesAsync(id, ReviewScope.ThisTurn, Ct);

        var story = changes.Files.Single(file => file.Path == "story.txt");
        // From before turn 2's own call on it, not turn 1's.
        Assert.Equal(Corvids, story.OldText);
        Assert.Equal(Corvids + "six\nseven, by me\n", story.NewText);
        Assert.Contains(changes.Files, file => file.Path == "notes.md");
    }

    [Fact]
    public async Task SendingCommentsGoesAsOneMessageAndMovesTheReviewPoint()
    {
        await using var fixture = await HistoryFixture.StartAsync(new FakeModels(new FakeGenerator(Reply.Say("I will."))));
        var id = await TwoTurnsAsync(fixture);
        using var review = fixture.Review();
        ReviewComment[] comments = [new("story.txt", DiffSide.New, 6, "six", "Say seven too.")];

        var point = await review.SendCommentsAsync(id, comments, Ct);
        await fixture.Sessions.IdleAsync();

        Assert.Equal((2, 1), (point.Turn, point.CommentsSent));
        var sent = fixture.Runtime.Log.Read(id, 0, long.MaxValue, 4096, Ct).Select(item => item.Payload).OfType<UserMessage>().Last();
        Assert.Equal(ReviewMessage.Compose(comments), sent.Text);
        // Nothing changed since the comments went.
        var since = await review.GetChangesAsync(id, ReviewScope.SinceLastReview, Ct);
        Assert.Empty(since.Files);
        Assert.Equal(point, since.ReviewPoint);
        // A call after them counts.
        fixture.Append(id, new UserMessage("again"));
        await fixture.WriteCallAsync(id, "call-4", "later.txt", "later\n");
        since = await review.GetChangesAsync(id, ReviewScope.SinceLastReview, Ct);
        Assert.Equal(["later.txt"], since.Files.Select(file => file.Path));
        await Assert.ThrowsAsync<ArgumentException>(() => review.SendCommentsAsync(id, [], Ct));
    }

    [Fact]
    public async Task OneFileGoesBackAloneAndStaysListedAsReverted()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = await TwoTurnsAsync(fixture);
        using var review = fixture.Review();
        var raised = new Raised();
        review.ChangesChanged += raised.Add;
        var story = (await review.GetChangesAsync(id, ReviewScope.ThisSession, Ct)).Files[0];

        var result = await review.RevertFileAsync(id, story.Path, story.CheckpointId!, Ct);

        Assert.Equal(new FileRevertResult("story.txt", story.CheckpointId!, false, false), result);
        Assert.Equal(Story, fixture.Read("story.txt"));
        Assert.Equal("a note\n", fixture.Read("notes.md"));
        Assert.Equal([id], raised.Ids);
        var after = await review.GetChangesAsync(id, ReviewScope.ThisSession, Ct);
        var reverted = after.Files.Single(file => file.Path == "story.txt");
        Assert.True(reverted.Reverted);
        Assert.Equal(story.CheckpointId, reverted.CheckpointId);
        Assert.Equal(1, after.Files.Count(file => !file.Reverted));
    }

    [Fact]
    public async Task PuttingAFileBackMergesWithThePersonsEditOrRefusesToLoseIt()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        var id = await TwoTurnsAsync(fixture);
        fixture.Write("notes.md", "my own notes\n");
        fixture.Write("story.txt", "ONE\ntwo\nthree\nfour, by me\nfive\nsix\n");
        using var review = fixture.Review();
        var files = (await review.GetChangesAsync(id, ReviewScope.ThisSession, Ct)).Files;

        var notes = files.Single(file => file.Path == "notes.md");
        var refused = await review.RevertFileAsync(id, notes.Path, notes.CheckpointId!, Ct);
        Assert.True(refused.Refused);
        Assert.Contains("created it", refused.Reason, StringComparison.Ordinal);
        Assert.Equal("my own notes\n", fixture.Read("notes.md"));

        var story = files.Single(file => file.Path == "story.txt");
        var merged = await review.RevertFileAsync(id, story.Path, story.CheckpointId!, Ct);
        Assert.Equal((true, false), (merged.Merged, merged.Refused));
        Assert.Equal("one\ntwo\nthree\nfour, by me\nfive\n", fixture.Read("story.txt"));
    }

    [Fact]
    public async Task OpenInEditorIsRefusedWithoutAGraphicalEditor()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        fixture.Write("story.txt", Story);

        using var none = fixture.Review(_ => null, _ => throw new InvalidOperationException("must not start"));
        var missing = await Assert.ThrowsAsync<ServiceUnavailableException>(() => none.OpenInEditorAsync("story.txt", 3, Ct));
        Assert.Contains("$VISUAL", missing.Message, StringComparison.Ordinal);

        using var vim = fixture.Review(name => name == "EDITOR" ? "/usr/bin/vim" : null, _ => throw new InvalidOperationException("must not start"));
        var terminal = await Assert.ThrowsAsync<ServiceUnavailableException>(() => vim.OpenInEditorAsync("story.txt", 3, Ct));
        Assert.Contains("vim", terminal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenInEditorRunsTheEditorAtTheLineWithNoShell()
    {
        await using var fixture = await HistoryFixture.StartAsync();
        fixture.Write("story.txt", Story);
        var started = new List<ProcessStartInfo>();
        using var review = fixture.Review(name => name == "VISUAL" ? "code --reuse-window" : "vim", info =>
        {
            started.Add(info);
            return Process.GetCurrentProcess();
        });

        var ran = await review.OpenInEditorAsync("story.txt", 3, Ct);

        var info = Assert.Single(started);
        Assert.False(info.UseShellExecute);
        Assert.Equal("code", info.FileName);
        var full = Path.Combine(Path.GetFullPath(fixture.Workspace), "story.txt");
        Assert.Equal(["--reuse-window", "--goto", $"{full}:3"], info.ArgumentList);
        Assert.Equal($"code --reuse-window --goto {full}:3", ran);
        await Assert.ThrowsAsync<InvalidOperationException>(() => review.OpenInEditorAsync("../outside.txt", 1, Ct));
    }
}
