using System.Runtime.CompilerServices;
using Corvid.Abstractions.Events;
using Corvid.Fabric;
using Microsoft.Extensions.AI;
using static Corvid.Memory.Tests.Fixtures;

namespace Corvid.Memory.Tests;

/// <summary>The block rewrite job (plan §9.5, §9.2 step 4): rewrites staged for review, never applied without approval.</summary>
public sealed class ConsolidationTests
{
    private const string Token = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8";

    // 50 tokens at four characters a token: 45 is the 90% threshold, 37 the target.
    private const int Budget = 50;

    private static readonly string Full = string.Join("\n",
        "Prefers metric units and short answers.",
        "Prefers metric units, always.",
        "Works on corvid in the evening; CI first.",
        "Likes answers short, no preamble at all.",
        "Short answers please, thanks.");

    private const string Rewrite = "Prefers metric units and short answers, no preamble.\nWorks on corvid in the evening; CI first.";

    private static MemoryWriter Writer(TempStore store) =>
        new(store.Database, store.Facts, store.Blocks, store.Retriever(), new FakeExtractor(),
            new DecisionFabric(DecisionPolicy.Default, Degraded(), store.Log, null), store.Log, time: store.Time);

    private static (BlockConsolidator Job, ScriptedChat Chat, List<(string Block, bool LocalOnly)> Scopes) Job(TempStore store,
        MemoryWriter writer, params string[] replies)
    {
        var chat = new ScriptedChat(replies);
        var scopes = new List<(string, bool)>();
        var job = new BlockConsolidator(store.Blocks, writer, chat, store.Log, block =>
        {
            scopes.Add((block.Name, block.LocalOnly));
            return chat.EnterScope();
        }, new BlockConsolidatorOptions { IdleDelay = TimeSpan.Zero, Interval = Timeout.InfiniteTimeSpan }, store.Time);
        return (job, chat, scopes);
    }

    private static TempStore FullAgentBlock()
    {
        var store = new TempStore();
        store.Blocks.SetBudget("agent", Budget);
        Assert.Equal(BlockWriteStatus.Applied, store.Blocks.Set("agent", Full).Status);
        return store;
    }

    private static List<SessionEvent> Journey(TempStore store) =>
        [.. store.Log.Read(MemoryJobs.Session, cancellationToken: Ct).Select(e => e.Payload)];

    [Fact]
    public async Task A_pass_that_stages_says_so_and_one_that_stages_nothing_is_silent()
    {
        // A7 e2e: the app's Memory page never showed what an idle pass staged; it hears of it through PassStaged.
        using var store = FullAgentBlock();
        var writer = Writer(store);
        var (job, _, _) = Job(store, writer, Rewrite);
        var heard = new List<int>();
        job.PassStaged += heard.Add;

        await job.RunOnceAsync(cancellationToken: Ct);
        Assert.Equal([1], heard);

        // A consolidation waits: the next pass stages nothing and says nothing.
        await job.RunOnceAsync(cancellationToken: Ct);
        Assert.Equal([1], heard);
    }

    [Fact]
    public async Task A_block_at_its_threshold_is_rewritten_by_the_generator_and_staged_never_applied()
    {
        using var store = FullAgentBlock();
        var writer = Writer(store);
        var (job, chat, scopes) = Job(store, writer, Rewrite);
        var before = store.Blocks.Get("agent");
        Assert.True(store.Blocks.CountTokens(Full) >= 45);

        var result = Assert.Single(await job.RunOnceAsync(cancellationToken: Ct));

        Assert.Equal(BlockRewriteStatus.Staged, result.Status);
        Assert.Equal((store.Blocks.CountTokens(Full), store.Blocks.CountTokens(Rewrite), Budget), (result.TokensBefore, result.TokensAfter, result.Budget));
        // Nothing applied: a core block enters every prompt, so a person approves the rewrite.
        Assert.Equal((Full, before.Revision), (store.Blocks.Get("agent").Content, store.Blocks.Get("agent").Revision));
        var pending = Assert.Single(writer.ListPending());
        Assert.Equal(result.Pending!.PendingId, pending.PendingId);
        Assert.Equal((PendingKind.Block, "agent", Rewrite, before.Revision, true), (pending.Kind, pending.Proposal.Block,
            pending.Proposal.Content, pending.Proposal.BlockRevision, pending.Proposal.Consolidation));
        Assert.Equal(MemoryJobs.Session, pending.SessionId);
        Assert.Equal($"consolidation: {result.TokensBefore} of 50 tokens rewritten to {result.TokensAfter}", pending.Reason);

        // The generator saw the block as data with its budget, inside the host's scope for the block (local-only by default).
        var call = Assert.Single(chat.Calls);
        Assert.Equal(BlockConsolidator.SystemPrompt, call[0].Text);
        Assert.Contains("at most 37 tokens", call[1].Text, StringComparison.Ordinal);
        Assert.Contains(Full, call[1].Text, StringComparison.Ordinal);
        Assert.Equal([("agent", true)], scopes);
        Assert.Equal([true], chat.InScope);

        // For /journey: the staging and the job's run, in the job's own session.
        Assert.Equal([new MemoryWritten(pending.PendingId, "staged"),
            new MemoryJobRan("consolidate_block", "block:agent", "staged", pending.Reason, "idle", pending.PendingId)], Journey(store));
    }

    [Fact]
    public async Task Approving_the_rewrite_replaces_the_block()
    {
        using var store = FullAgentBlock();
        var writer = Writer(store);
        var (job, _, _) = Job(store, writer, Rewrite);
        var staged = Assert.Single(await job.RunOnceAsync(cancellationToken: Ct)).Pending!;

        var approved = await writer.ApproveAsync(staged.PendingId, cancellationToken: Ct);

        Assert.Equal(MemoryWriteStatus.Applied, approved.Status);
        Assert.Equal(Rewrite, store.Blocks.Get("agent").Content);
        Assert.Empty(writer.ListPending());
        Assert.Equal(new MemoryWritten("block:agent", "update"), Journey(store)[^1]);
        // Now under the threshold, a pass asks nothing.
        Assert.Empty(await job.RunOnceAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task Rejecting_the_rewrite_leaves_the_block_and_the_job_does_not_propose_it_again_until_the_block_changes()
    {
        using var store = FullAgentBlock();
        var writer = Writer(store);
        var (job, chat, _) = Job(store, writer, Rewrite, Rewrite);
        var staged = Assert.Single(await job.RunOnceAsync(cancellationToken: Ct)).Pending!;

        writer.Reject(staged.PendingId, cancellationToken: Ct);

        Assert.Equal(Full, store.Blocks.Get("agent").Content);
        Assert.Empty(await job.RunOnceAsync(cancellationToken: Ct));
        Assert.Single(chat.Calls);

        // The block changed (still full): a new rewrite is proposed.
        store.Blocks.Set("agent", Full + " ok");
        Assert.Equal(BlockRewriteStatus.Staged, Assert.Single(await job.RunOnceAsync(cancellationToken: Ct)).Status);
    }

    [Fact]
    public async Task One_consolidation_waits_per_block()
    {
        using var store = FullAgentBlock();
        var writer = Writer(store);
        var (job, chat, _) = Job(store, writer, Rewrite, Rewrite);
        await job.RunOnceAsync(cancellationToken: Ct);

        Assert.Empty(await job.RunOnceAsync(cancellationToken: Ct));
        var again = await job.StageAsync("cli-1", "agent", "cli", Ct);

        Assert.Equal(BlockRewriteStatus.NotNeeded, again.Status);
        Assert.Single(chat.Calls);
        Assert.Single(writer.ListPending());
    }

    [Fact]
    public async Task A_block_holding_a_credential_is_never_sent_and_nothing_is_staged()
    {
        using var store = new TempStore();
        store.Blocks.SetBudget("agent", Budget);
        // The user's own edit is not checked; the job still never sends the value to a model.
        Assert.Equal(BlockWriteStatus.Applied, store.Blocks.Set("agent", ($"Deploy with GH_TOKEN={Token}.\n" + Full)[..190]).Status);
        var writer = Writer(store);
        var (job, chat, _) = Job(store, writer, Rewrite);

        var result = Assert.Single(await job.RunOnceAsync(cancellationToken: Ct));

        Assert.Equal(BlockRewriteStatus.Refused, result.Status);
        Assert.Contains("github-token", result.Detail, StringComparison.Ordinal);
        Assert.Empty(chat.Calls);
        Assert.Empty(writer.ListPending());
        var ran = Assert.IsType<MemoryJobRan>(Assert.Single(Journey(store)));
        Assert.Equal("refused", ran.Outcome);
        Assert.DoesNotContain(Token, ran.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rewrite_that_adds_a_credential_is_refused_and_nothing_is_staged()
    {
        using var store = FullAgentBlock();
        var writer = Writer(store);
        var (job, _, _) = Job(store, writer, $"Metric, short answers. Token {Token}.");

        var result = Assert.Single(await job.RunOnceAsync(cancellationToken: Ct));

        Assert.Equal((BlockRewriteStatus.Refused, null), (result.Status, result.Text));
        Assert.Empty(writer.ListPending());
        Assert.Equal(Full, store.Blocks.Get("agent").Content);
        Assert.DoesNotContain(Token, Assert.IsType<MemoryJobRan>(Assert.Single(Journey(store))).Detail, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => writer.StageConsolidation("s", "persona", "x", 0, "r"));
        Assert.Equal(MemoryWriteStatus.Refused, writer.StageConsolidation("s", "agent", $"t {Token}", 1, "r").Status);
    }

    [Fact]
    public async Task A_rewrite_over_the_budget_is_retried_once_then_declined_and_not_asked_again()
    {
        using var store = FullAgentBlock();
        var writer = Writer(store);
        var tooLong = Full + "\nAnd one more line that makes it longer still.";
        var (job, chat, _) = Job(store, writer, tooLong, Full.Replace("always", "always, every single time", StringComparison.Ordinal));

        var result = Assert.Single(await job.RunOnceAsync(cancellationToken: Ct));

        Assert.Equal(BlockRewriteStatus.Declined, result.Status);
        Assert.Equal(2, chat.Calls.Count);
        Assert.StartsWith("That is ", chat.Calls[1][^1].Text, StringComparison.Ordinal);
        Assert.Empty(writer.ListPending());
        Assert.Empty(await job.RunOnceAsync(cancellationToken: Ct));
        Assert.Equal(2, chat.Calls.Count);
    }

    [Fact]
    public async Task A_second_attempt_that_fits_is_staged()
    {
        using var store = FullAgentBlock();
        var writer = Writer(store);
        var (job, chat, _) = Job(store, writer, Full + " Still too long, by a lot of words.", "```\n" + Rewrite + "\n```");

        var result = Assert.Single(await job.RunOnceAsync(cancellationToken: Ct));

        Assert.Equal((BlockRewriteStatus.Staged, Rewrite), (result.Status, result.Pending!.Proposal.Content));
        Assert.Equal(2, chat.Calls.Count);
    }

    [Theory]
    [InlineData("Prefers metric units.‮evil")]
    [InlineData("")]
    public async Task A_rewrite_hiding_characters_or_empty_is_declined(string reply)
    {
        using var store = FullAgentBlock();
        var writer = Writer(store);
        var (job, _, _) = Job(store, writer, reply);

        Assert.Equal(BlockRewriteStatus.Declined, Assert.Single(await job.RunOnceAsync(cancellationToken: Ct)).Status);
        Assert.Empty(writer.ListPending());
    }

    [Fact]
    public async Task Blocks_below_the_threshold_and_the_persona_are_left_alone()
    {
        using var store = new TempStore();
        store.Blocks.SetBudget("persona", Budget);
        store.Blocks.Set("persona", Full);
        store.Blocks.SetBudget("user", Budget);
        store.Blocks.Set("user", "Prefers metric units.");
        var writer = Writer(store);
        var (job, chat, _) = Job(store, writer, Rewrite);

        Assert.Empty(await job.RunOnceAsync(cancellationToken: Ct));
        Assert.Equal(BlockRewriteStatus.Refused, (await job.ProposeAsync("persona", cancellationToken: Ct)).Status);
        Assert.Empty(chat.Calls);
    }

    [Fact]
    public async Task An_edit_staged_over_its_budget_gets_a_rewrite_that_folds_it_in()
    {
        using var store = new TempStore();
        store.Blocks.SetBudget("agent", Budget);
        store.Blocks.Set("agent", "Prefers metric units and short answers.");
        var writer = Writer(store);
        var edit = (await writer.EditBlockAsync("s-1", "agent", Full + "\nAlso: run the linter before every push.", cancellationToken: Ct)).Pending!;
        Assert.StartsWith("over_budget", edit.Reason, StringComparison.Ordinal);
        var (job, chat, _) = Job(store, writer, Rewrite + "\nLint before pushing.");

        var result = Assert.Single(await job.RunOnceAsync(cancellationToken: Ct));

        Assert.Equal(BlockRewriteStatus.Staged, result.Status);
        Assert.Contains("Also: run the linter", chat.Calls[0][1].Text, StringComparison.Ordinal);
        Assert.Equal(edit.PendingId, result.Pending!.Proposal.Supersedes);
        Assert.Equal(2, writer.ListPending().Count);

        await writer.ApproveAsync(result.Pending.PendingId, cancellationToken: Ct);

        Assert.Equal(Rewrite + "\nLint before pushing.", store.Blocks.Get("agent").Content);
        Assert.Empty(writer.ListPending());
        Assert.Equal(("consolidated into " + result.Pending.PendingId, PendingStatus.Approved),
            (writer.GetPending(edit.PendingId)!.Resolution, writer.GetPending(edit.PendingId)!.Status));
    }

    [Fact]
    public async Task Rejecting_the_folded_edit_rejects_its_rewrite()
    {
        using var store = new TempStore();
        store.Blocks.SetBudget("agent", Budget);
        var writer = Writer(store);
        var edit = (await writer.EditBlockAsync("s-1", "agent", Full + "\nAlso: lint before every push.", cancellationToken: Ct)).Pending!;
        var (job, _, _) = Job(store, writer, Rewrite);
        var rewrite = Assert.Single(await job.RunOnceAsync(cancellationToken: Ct)).Pending!;

        writer.Reject(edit.PendingId, cancellationToken: Ct);

        Assert.Empty(writer.ListPending());
        Assert.Equal(PendingStatus.Rejected, writer.GetPending(rewrite.PendingId)!.Status);
        Assert.Equal("", store.Blocks.Get("agent").Content);
    }

    [Fact]
    public async Task Approving_a_rewrite_after_the_block_changed_writes_nothing()
    {
        using var store = FullAgentBlock();
        var writer = Writer(store);
        var (job, _, _) = Job(store, writer, Rewrite);
        var staged = Assert.Single(await job.RunOnceAsync(cancellationToken: Ct)).Pending!;
        store.Blocks.Set("agent", Full + " changed");

        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.ApproveAsync(staged.PendingId, cancellationToken: Ct));

        Assert.Equal(Full + " changed", store.Blocks.Get("agent").Content);
    }

    [Fact]
    public async Task The_loop_runs_a_pass_only_when_every_session_is_idle()
    {
        using var store = FullAgentBlock();
        var writer = Writer(store);
        var (job, chat, _) = Job(store, writer, Rewrite);
        var idle = false;
        var asked = new SemaphoreSlim(0);
        var processed = new TaskCompletionSource<BlockRewrite>(TaskCreationOptions.RunContinuationsAsynchronously);
        job.Processed += rewrite => processed.TrySetResult(rewrite);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = job.RunAsync(() =>
        {
            var answer = Volatile.Read(ref idle);
            asked.Release();
            return answer;
        }, stop.Token);

        // A turn ended while another session is busy: the loop looks and does nothing.
        job.Nudge();
        await asked.WaitAsync(Ct);
        Assert.Empty(chat.Calls);
        Assert.Empty(writer.ListPending());

        // Every session idle: the next nudge runs the pass, which stages the rewrite.
        Volatile.Write(ref idle, true);
        job.Nudge();
        var rewrite = await processed.Task.WaitAsync(Ct);
        Assert.Equal(BlockRewriteStatus.Staged, rewrite.Status);
        Assert.Equal("idle", Assert.IsType<MemoryJobRan>(Journey(store)[^1]).Trigger);
        Assert.Equal(Full, store.Blocks.Get("agent").Content);

        await stop.CancelAsync();
        await loop.WaitAsync(Ct);
    }

    [Fact]
    public async Task The_loop_survives_an_idle_check_that_throws()
    {
        using var store = FullAgentBlock();
        var writer = Writer(store);
        var (job, _, _) = Job(store, writer, Rewrite);
        var seen = System.Threading.Channels.Channel.CreateUnbounded<BlockRewrite>();
        job.Processed += rewrite => seen.Writer.TryWrite(rewrite);
        var checks = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = job.RunAsync(() => Interlocked.Increment(ref checks) == 1 ? throw new InvalidOperationException("session going away") : true,
            stop.Token);

        job.Nudge();
        var failed = await seen.Reader.ReadAsync(Ct);
        Assert.Equal(BlockRewriteStatus.Failed, failed.Status);
        Assert.Contains("session going away", failed.Detail, StringComparison.Ordinal);
        Assert.Empty(writer.ListPending());

        job.Nudge();
        Assert.Equal(BlockRewriteStatus.Staged, (await seen.Reader.ReadAsync(Ct)).Status);
        Assert.False(loop.IsCompleted);

        await stop.CancelAsync();
        await loop.WaitAsync(Ct);
    }

    [Fact]
    public async Task A_proposal_stages_nothing()
    {
        using var store = FullAgentBlock();
        var writer = Writer(store);
        var (job, _, _) = Job(store, writer, Rewrite);

        var proposal = await job.ProposeAsync("agent", Full + "\nOne more line.", Ct);

        Assert.Equal((BlockRewriteStatus.Proposed, Rewrite), (proposal.Status, proposal.Text));
        Assert.Empty(writer.ListPending());
        Assert.Empty(Journey(store));
    }

    [Theory]
    [InlineData("```text\nA\nB\n```", "A\nB")]
    [InlineData("<<<BLOCK\nA\nBLOCK>>>", "A")]
    [InlineData("  A\r\nB  ", "A\nB")]
    public void The_reply_is_cleaned_to_the_block(string reply, string expected) =>
        Assert.Equal(expected, BlockConsolidator.Clean(reply));

    /// <summary>Replies in order (the last repeats); records each call and whether it ran inside the request scope.</summary>
    private sealed class ScriptedChat(params string[] replies) : IChatClient
    {
        private int _next;
        private int _depth;

        public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

        public List<bool> InScope { get; } = [];

        public IDisposable EnterScope()
        {
            _depth++;
            return new Exit(this);
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add([.. messages]);
            InScope.Add(_depth > 0);
            var reply = replies[Math.Min(_next++, replies.Length - 1)];
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }

        private sealed class Exit(ScriptedChat chat) : IDisposable
        {
            public void Dispose() => chat._depth--;
        }
    }
}
