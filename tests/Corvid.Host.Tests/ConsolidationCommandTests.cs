using Corvid.Abstractions.Events;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Kernel.Events;
using Corvid.Memory;
using Corvid.Models;
using Corvid.Models.Privacy;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>The block rewrite job (plan §9.5) composed: <c>/memory consolidate</c>, the egress gate and the sleep-time loop.</summary>
public sealed class ConsolidationCommandTests
{
    private const string Full =
        "Prefers metric units and short answers.\nPrefers metric units, always.\nWorks on corvid in the evening; CI first.\n"
        + "Likes answers short, no preamble at all.\nShort answers please, thanks.";

    private const string Rewrite = "Prefers metric units and short answers, no preamble.\nWorks on corvid in the evening; CI first.";

    private static readonly ModelProfile Cloud = new("claude@anthropic", 200_000, 200_000, ToolCallSupport.Native,
        StructuredOutputSupport.None, PromptCachingStyle.Breakpoints, ModelCost.Free, PrivacyClass.Cloud, "api.anthropic.com");

    private static ValueTask<CommandOutput> Run(CorvidRuntime runtime, CorvidSession session, SlashCommandRegistry commands, string line) =>
        commands.ExecuteAsync(line, new SlashCommandContext(runtime, session, commands), TestContext.Current.CancellationToken);

    /// <summary>The consolidator's requests get the rewrite (and are recorded); anything else, a turn, gets "Hello.".</summary>
    private static FakeGenerator Generator(List<IReadOnlyList<ChatMessage>> rewrites) =>
        new([.. Enumerable.Repeat<Func<IReadOnlyList<ChatMessage>, ChatResponse>>(messages =>
        {
            if (messages[0].Text != BlockConsolidator.SystemPrompt)
                return Reply.Text("Hello.");
            lock (rewrites)
                rewrites.Add(messages);
            return Reply.Text(Rewrite);
        }, 8)]);

    private static void FillAgentBlock(CorvidRuntime runtime)
    {
        runtime.Memory!.Blocks.SetBudget("agent", 50);
        Assert.Equal(BlockWriteStatus.Applied, runtime.Memory.Blocks.Set("agent", Full).Status);
    }

    [Fact]
    public async Task Memory_consolidate_stages_the_rewrite_as_a_diff_and_approve_applies_it()
    {
        var rewrites = new List<IReadOnlyList<ChatMessage>>();
        var host = TestHost.Create(new FakeModels(Generator(rewrites)),
            new Dictionary<string, string?> { ["Corvid:Memory:Consolidation:Background"] = "false" });
        var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        await using (host)
        await using (runtime)
        {
            var nothing = await Run(runtime, session, commands, "/memory consolidate");
            Assert.StartsWith("No block is near its budget (user 0 of 350 tokens, agent 0 of 550 tokens)", nothing.Lines[0], StringComparison.Ordinal);
            Assert.Equal(OutputTone.Error, (await Run(runtime, session, commands, "/memory consolidate persona")).Tone);
            Assert.Empty(rewrites);

            FillAgentBlock(runtime);
            var output = await Run(runtime, session, commands, "/memory consolidate");

            Assert.Equal((OutputTone.Success, "Consolidation staged for review"), (output.Tone, output.Title));
            var pending = Assert.Single(runtime.Memory!.Writer!.ListPending());
            Assert.Equal((session.Id, true, Rewrite), (pending.SessionId, pending.Proposal.Consolidation, pending.Proposal.Content));
            Assert.StartsWith(pending.PendingId + ": the agent block, ", output.Lines[0], StringComparison.Ordinal);
            Assert.Contains("    Works on corvid in the evening; CI first.", output.Lines);
            Assert.Contains("  - Prefers metric units, always.", output.Lines);
            Assert.Contains("  + Prefers metric units and short answers, no preamble.", output.Lines);
            // Staged, not applied.
            Assert.Equal(Full, runtime.Memory.Blocks.Get("agent").Content);
            Assert.Single(rewrites);
            var ran = runtime.Log.Read(session.Id, cancellationToken: TestContext.Current.CancellationToken).Select(item => item.Payload)
                .OfType<MemoryJobRan>().Single();
            Assert.Equal(("consolidate_block", "block:agent", "staged", "cli", pending.PendingId),
                (ran.Job, ran.Subject, ran.Outcome, ran.Trigger, ran.PendingId));

            var listed = await Run(runtime, session, commands, "/memory pending");
            Assert.StartsWith("consolidation: ", listed.Table!.Rows[0][3], StringComparison.Ordinal);

            await Run(runtime, session, commands, $"/memory approve {pending.PendingId}");
            Assert.Equal(Rewrite, runtime.Memory.Blocks.Get("agent").Content);
        }
    }

    [Fact]
    public async Task A_local_only_block_never_reaches_a_cloud_generator()
    {
        var root = Path.Combine(Path.GetTempPath(), "corvid-consolidate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var database = new MemoryDatabase(Path.Combine(root, "memory.db"));
            var log = new SqliteEventLog(Path.Combine(root, "events.db"));
            var blocks = new CoreBlockStore(database);
            blocks.SetBudget("agent", 50);
            blocks.Set("agent", Full);
            var facts = new FactGraph(database);
            var models = new FakeModels();
            var writer = new MemoryWriter(database, facts, blocks, new HybridRetriever(new EpisodicIndex(database), facts), models.Extractor,
                new Fabric.DecisionFabric(Fabric.DecisionPolicy.Default, models.Kev, log), log);
            var provider = new FakeGenerator(Reply.Say(Rewrite), Reply.Say(Rewrite));
            // The background generator bound to a cloud model, behind the egress gate as the composer binds it.
            var cloud = new PrivacyEgressChatClient(provider, Cloud, new PrivacyEgress(models.Extractor, new PrivacyEgressOptions { PolicyFor = _ => EgressPolicy.Default }));
            var job = new BlockConsolidator(blocks, writer, cloud, log, CorvidRuntimeFactory.ConsolidationRequest)
            {
                IsEgressRefusal = CorvidRuntimeFactory.EgressRefusal,
            };
            Assert.True(blocks.Get("agent").LocalOnly);

            var refused = await job.StageAsync("s-1", "agent", "cli", TestContext.Current.CancellationToken);

            Assert.Equal(BlockRewriteStatus.Refused, refused.Status);
            Assert.True(refused.EgressRefused);
            Assert.Contains("local-only memory", refused.Detail, StringComparison.Ordinal);
            Assert.Empty(provider.Calls);
            Assert.Empty(writer.ListPending());
            // A7V1-5: remembered for this revision while the block stays local-only: the idle pass does not try again, and
            // memory-jobs gets no "failed" event at every turn.
            var jobs = log.Read(MemoryJobs.Session, cancellationToken: TestContext.Current.CancellationToken).Count();
            Assert.Empty(await job.RunOnceAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Empty(await job.RunOnceAsync(cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(jobs, log.Read(MemoryJobs.Session, cancellationToken: TestContext.Current.CancellationToken).Count());

            // Marked shareable by its owner, the same block may go to the cloud generator.
            blocks.SetLocalOnly("agent", false);
            Assert.Equal(BlockRewriteStatus.Staged, (await job.StageAsync("s-1", "agent", "cli", TestContext.Current.CancellationToken)).Status);
            Assert.Single(provider.Calls);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task After_a_turn_the_idle_sessions_get_the_block_rewritten_in_the_jobs_own_session()
    {
        var rewrites = new List<IReadOnlyList<ChatMessage>>();
        var host = TestHost.Create(new FakeModels(Generator(rewrites)), new Dictionary<string, string?>
        {
            ["Corvid:Memory:Consolidation:IdleSeconds"] = "0",
            ["Corvid:Memory:Consolidation:IntervalMinutes"] = "0",
        });
        var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        await using (host)
        await using (runtime)
        {
            var job = runtime.Memory!.Consolidator!;
            var processed = new TaskCompletionSource<BlockRewrite>(TaskCreationOptions.RunContinuationsAsynchronously);
            job.Processed += rewrite => processed.TrySetResult(rewrite);
            FillAgentBlock(runtime);

            await session.SendAsync("hi", TestContext.Current.CancellationToken);
            var rewrite = await processed.Task.WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(BlockRewriteStatus.Staged, rewrite.Status);
            Assert.True(runtime.SessionsIdle());
            var pending = Assert.Single(runtime.Memory.Writer!.ListPending());
            Assert.Equal(MemoryJobs.Session, pending.SessionId);
            Assert.Equal(Full, runtime.Memory.Blocks.Get("agent").Content);
            var journey = runtime.Log.Read(MemoryJobs.Session, cancellationToken: TestContext.Current.CancellationToken)
                .Select(item => item.Payload).ToList();
            Assert.Equal(new MemoryWritten(pending.PendingId, "staged"), journey[0]);
            Assert.Equal("idle", Assert.IsType<MemoryJobRan>(journey[1]).Trigger);
            // The job's session is Corvid's own log, not a conversation.
            Assert.False(SessionCommands.IsConversation(MemoryJobs.Session));
            Assert.False(CorvidRuntime.IsSearchable(MemoryJobs.Session));
        }
    }
}
