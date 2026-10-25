using Corvid.Approvals;
using Corvid.Host.Models;
using Corvid.Host.Sessions;
using Corvid.Models;

namespace Corvid.Host.Tests;

/// <summary>
/// The session update stream (K-10): a live turn's report and the replay of the same session from the log carry the same
/// prompt, call, result, ledger diff and answer, so every surface draws a loaded session as it drew the live one.
/// </summary>
public sealed class SessionStreamTests
{
    private const string Prompt = "write hello to the file notes.txt";

    private static FakeModels FileWrite()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("write_file", """{"path":"notes.txt","content":"hello\n"}"""), Reply.Say("Wrote it.")));
        // An embedder that ranks write_file first for "write ..." requests, so the shortlist offers it.
        models.Provider.Add("file-embed", [ModelSlots.TextEmbedder], () => new KeywordEmbedder("write", "save"), context: 512, tools: ToolCallSupport.None);
        return models;
    }

    private static readonly Dictionary<string, string?> FileTools = new()
    {
        ["Corvid:Tools:Builtin"] = "true",
        ["Corvid:Models:Slots:embedder.text:Model"] = "file-embed",
    };

    private sealed class Approve : IApprovalPrompter
    {
        public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken) => ValueTask.FromResult(ApprovalAnswer.Approve());
    }

    [Fact]
    public async Task ALiveTurnReportsTheGeneratorsReasoningAndItsTokensOnTheStream()
    {
        var reasoned = new Microsoft.Extensions.AI.ChatResponse(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.Assistant,
            [new Microsoft.Extensions.AI.TextReasoningContent("The user wants a greeting."), new Microsoft.Extensions.AI.TextContent("Hello there.")]));
        await using var host = TestHost.Create(new FakeModels(new FakeGenerator(_ => reasoned)));
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        var live = new List<SessionStreamUpdate>();
        var reporter = new SessionUpdateReporter(runtime, session.Id, new ToolPresentation(runtime), 0, update =>
        {
            lock (live)
                live.Add(update);
        });

        using (runtime.Feed.Subscribe(session.Id, reporter))
        {
            var delivery = await session.SendAsync("say hello", TestContext.Current.CancellationToken);
            await reporter.FinishAsync(delivery.Result?.Text);
        }

        // K-1: the reasoning before the answer it led to; K-5: the generator call's tokens as the fake provider reports them.
        var thought = Assert.Single(live.OfType<ThoughtChunk>());
        Assert.Equal("The user wants a greeting.", thought.Text);
        Assert.True(live.IndexOf(thought) < live.FindIndex(update => update is AnswerChunk));
        Assert.Equal("Hello there.", string.Concat(live.OfType<AnswerChunk>().Select(chunk => chunk.Text)));
        var usage = Assert.Single(live.OfType<UsageReported>());
        Assert.Equal((42L, 7L), (usage.InputTokens, usage.OutputTokens));
        // Neither is in the log, so a replay has none.
        var replay = await SessionReplay.ReadAsync(runtime, session.Id, new ToolPresentation(runtime), cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(replay.Updates, update => update is ThoughtChunk or UsageReported);
    }

    [Fact]
    public async Task ALiveTurnAndItsReplayCarryTheSamePromptCallDiffAndAnswer()
    {
        await using var host = TestHost.Create(FileWrite(), FileTools);
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        var tools = new ToolPresentation(runtime);
        var live = new List<SessionStreamUpdate>();
        var reporter = new SessionUpdateReporter(runtime, session.Id, tools, runtime.Log.GetHead(session.Id, TestContext.Current.CancellationToken), update =>
        {
            lock (live)
                live.Add(update);
        });

        using (runtime.AttachPrompter(session.Id, new Approve()))
        using (runtime.Feed.Subscribe(session.Id, reporter))
        {
            var delivery = await session.SendAsync(Prompt, TestContext.Current.CancellationToken);
            await reporter.FinishAsync(delivery.Result?.Text);
        }

        var file = Path.Combine(host.Root, "work", "notes.txt");
        var prompt = Assert.IsType<PromptSent>(live[0]);
        Assert.Equal(Prompt, prompt.Text);
        var started = Assert.Single(live.OfType<ToolCallStarted>());
        // It needs approval, so it is announced pending.
        Assert.Equal(("write_file", ToolCallKinds.Edit, ToolCallState.Pending), (started.Tool, started.Kind, started.Status));
        var finished = Assert.Single(live.OfType<ToolCallChanged>());
        Assert.Equal((ToolCallState.Completed, "succeeded", "write_file: notes.txt"), (finished.Status, finished.Outcome, finished.Title));
        Assert.Equal([file], finished.Locations);
        Assert.Equal(new FileDiff(file, null, "hello\n"), Assert.Single(finished.Diffs));
        Assert.Equal("Wrote it.", string.Concat(live.OfType<AnswerChunk>().Select(chunk => chunk.Text)).Trim());
        Assert.Contains(live, update => update is CheckpointLogged { Checkpoint.Kind: "after" });

        var replay = await SessionReplay.ReadAsync(runtime, session.Id, tools, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(runtime.Log.GetHead(session.Id, TestContext.Current.CancellationToken), replay.Head);
        Assert.Equal(prompt with { At = default }, Assert.IsType<PromptSent>(replay.Updates[0]) with { At = default });
        var call = Assert.Single(replay.Updates.OfType<ToolCallStarted>());
        Assert.Equal((started.CallId, "write_file: notes.txt", ToolCallState.Completed), (call.CallId, call.Title, call.Status));
        Assert.Equal("notes.txt", call.Input?.GetProperty("path").GetString());
        var result = Assert.Single(replay.Updates.OfType<ToolCallChanged>());
        Assert.Equal(finished.Diffs, result.Diffs);
        Assert.Equal(finished.OutputText, result.OutputText);
        Assert.Equal("Wrote it.", Assert.Single(replay.Updates.OfType<AnswerChunk>()).Text.Trim());
        Assert.Equal(live.OfType<CheckpointLogged>().Select(item => item.Checkpoint.CheckpointId),
            replay.Updates.OfType<CheckpointLogged>().Select(item => item.Checkpoint.CheckpointId));
        // Every update after the prompt, in the order the log has them.
        Assert.True(replay.Updates.IndexOf(call) < replay.Updates.IndexOf(result));
    }

    [Fact]
    public async Task TheTranscriptReplayIsTheConversationAlone()
    {
        await using var host = TestHost.Create(FileWrite(), FileTools);
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        using (runtime.AttachPrompter(session.Id, new Approve()))
            await session.SendAsync(Prompt, TestContext.Current.CancellationToken);

        var replay = await SessionReplay.ReadAsync(runtime, session.Id, new ToolPresentation(runtime), ReplayOptions.Transcript,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["PromptSent", "ToolCallStarted", "ToolCallChanged", "AnswerChunk"], replay.Updates.Select(update => update.GetType().Name));
        Assert.Empty(Assert.Single(replay.Updates.OfType<ToolCallChanged>()).Diffs);
        Assert.All(replay.Updates, update => Assert.NotEqual(default, update.At));
    }

    [Fact]
    public async Task ACallTheLogHoldsNoResultForReplaysAsCancelledInTheFullReplayOnly()
    {
        await using var host = TestHost.Create();
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        using var arguments = System.Text.Json.JsonDocument.Parse("""{"path":"notes.txt"}""");
        // The process stopped under the call: the log has the call and no result.
        runtime.Log.Append(session.Id, runtime.Log.GetHead(session.Id, TestContext.Current.CancellationToken),
        [
            Corvid.Abstractions.Events.EventData.Create(new Corvid.Abstractions.Events.UserMessage("read my notes")),
            Corvid.Abstractions.Events.EventData.Create(new Corvid.Abstractions.Events.ToolCallIssued("c1", "read_file", arguments.RootElement.Clone())),
        ], TestContext.Current.CancellationToken);
        var tools = new ToolPresentation(runtime);

        var full = await SessionReplay.ReadAsync(runtime, session.Id, tools, ReplayOptions.Full, cancellationToken: TestContext.Current.CancellationToken);
        var transcript = await SessionReplay.ReadAsync(runtime, session.Id, tools, ReplayOptions.Transcript,
            cancellationToken: TestContext.Current.CancellationToken);

        // As a live turn closes a call its interrupt cut: failed, cancelled; never completed.
        Assert.Equal(ToolCallState.Failed, Assert.Single(full.Updates.OfType<ToolCallStarted>()).Status);
        var closed = Assert.Single(full.Updates.OfType<ToolCallChanged>());
        Assert.Equal(("c1", ToolCallState.Failed, "cancelled"), (closed.CallId, closed.Status, closed.Outcome));
        // ACP's session/load (the transcript replay) is unchanged: its recorded exchanges keep such a call as completed.
        Assert.Equal(ToolCallState.Completed, Assert.Single(transcript.Updates.OfType<ToolCallStarted>()).Status);
        Assert.Empty(transcript.Updates.OfType<ToolCallChanged>());
    }

    private sealed class DenyWithReason(string reason) : IApprovalPrompter
    {
        public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken) =>
            ValueTask.FromResult(ApprovalAnswer.Deny() with { Reason = reason });
    }

    [Fact]
    public async Task AReplayedCallWhoseApprovalWasRefusedIsDeniedWithThePersonsReason()
    {
        await using var host = TestHost.Create(FileWrite(), FileTools);
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        var tools = new ToolPresentation(runtime);
        var live = new List<SessionStreamUpdate>();
        var reporter = new SessionUpdateReporter(runtime, session.Id, tools, runtime.Log.GetHead(session.Id, TestContext.Current.CancellationToken), update =>
        {
            lock (live)
                live.Add(update);
        });
        using (runtime.AttachPrompter(session.Id, new DenyWithReason(" do not write scripts ")))
        using (runtime.Feed.Subscribe(session.Id, reporter))
        {
            var delivery = await session.SendAsync(Prompt, TestContext.Current.CancellationToken);
            await reporter.FinishAsync(delivery.Result?.Text);
        }

        var finished = Assert.Single(live.OfType<ToolCallChanged>());
        Assert.Equal((ToolCallState.Failed, "denied"), (finished.Status, finished.Outcome));
        var replay = await SessionReplay.ReadAsync(runtime, session.Id, tools, cancellationToken: TestContext.Current.CancellationToken);
        var result = Assert.Single(replay.Updates.OfType<ToolCallChanged>());
        Assert.Equal((ToolCallState.Failed, "denied"), (result.Status, result.Outcome));
        var approval = Assert.IsType<CallApproval>(result.Approval);
        Assert.Equal((false, true, "once", "do not write scripts"), (approval.Approved, approval.Answered, approval.Scope, approval.Note));
        Assert.Equal("user: denied (once)", approval.Reason);

        // The conversation alone (ACP's session/load) stays as it was: a failed call, no approval.
        var transcript = await SessionReplay.ReadAsync(runtime, session.Id, tools, ReplayOptions.Transcript,
            cancellationToken: TestContext.Current.CancellationToken);
        var plain = Assert.Single(transcript.Updates.OfType<ToolCallChanged>());
        Assert.Equal((ToolCallState.Failed, null, null), (plain.Status, plain.Outcome, plain.Approval));
    }

    [Fact]
    public void ATitleNamesTheTargetOnOneLineAndCutsALongOne()
    {
        using var document = System.Text.Json.JsonDocument.Parse("""{"command":"echo a\necho b"}""");
        Assert.Equal("shell: echo a echo b", ToolPresentation.Title("shell", document.RootElement));
        Assert.Equal("echo a echo b", ToolPresentation.Target(document.RootElement));
        Assert.Equal("list_notes", ToolPresentation.Title("list_notes", null));
        using var longer = System.Text.Json.JsonDocument.Parse($$"""{"path":"{{new string('a', 300)}}"}""");
        Assert.Equal(160, ToolPresentation.Title("read_file", longer.RootElement).Length);
    }
}
