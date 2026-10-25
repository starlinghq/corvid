using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

/// <summary>A5: model switches in the session's view: the header's models, rows per slot, the privacy retreat, and the model per step.</summary>
public sealed class ModelSwitchViewTests
{
    private const string Idle = "cli-a410";

    /// <summary>The demo's sessions, with the view's update callback kept so a test can send it live updates.</summary>
    private sealed class PushingService(DemoSessionService inner) : ISessionService
    {
        private Action<SessionUpdate>? _push;

        public void Push(SessionUpdate update) => _push!(update);

        public event EventHandler<SessionSummary>? SessionChanged
        {
            add => inner.SessionChanged += value;
            remove => inner.SessionChanged -= value;
        }

        public async Task<ISessionSubscription> OpenAsync(string sessionId, Action<SessionUpdate> onUpdate, CancellationToken cancellationToken = default)
        {
            _push = onUpdate;
            return await inner.OpenAsync(sessionId, onUpdate, cancellationToken);
        }

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public Task SendAsync(string sessionId, string text, CancellationToken cancellationToken = default) => inner.SendAsync(sessionId, text, cancellationToken);
        public Task<QueuedMessage> QueueAsync(string sessionId, string text, CancellationToken cancellationToken = default) => inner.QueueAsync(sessionId, text, cancellationToken);
        public Task RemoveQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) => inner.RemoveQueuedAsync(sessionId, queuedId, cancellationToken);
        public Task MoveQueuedAsync(string sessionId, string queuedId, int index, CancellationToken cancellationToken = default) => inner.MoveQueuedAsync(sessionId, queuedId, index, cancellationToken);
        public Task<string?> TakeBackQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) => inner.TakeBackQueuedAsync(sessionId, queuedId, cancellationToken);
        public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default) => inner.SteerAsync(sessionId, text, cancellationToken);
        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default) => inner.InterruptAsync(sessionId, cancellationToken);
        public Task AnswerApprovalAsync(string sessionId, string approvalId, ApprovalAnswer answer, CancellationToken cancellationToken = default) => inner.AnswerApprovalAsync(sessionId, approvalId, answer, cancellationToken);
        public Task AnswerQuestionAsync(string sessionId, string questionId, string answer, CancellationToken cancellationToken = default) => inner.AnswerQuestionAsync(sessionId, questionId, answer, cancellationToken);
        public Task RunCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default) => inner.RunCommandAsync(sessionId, line, cancellationToken);
        public Task SetApprovalModeAsync(string sessionId, ApprovalMode mode, CancellationToken cancellationToken = default) => inner.SetApprovalModeAsync(sessionId, mode, cancellationToken);
        public Task SetGoalAsync(string sessionId, string? goal, CancellationToken cancellationToken = default) => inner.SetGoalAsync(sessionId, goal, cancellationToken);
        public Task CompactAsync(string sessionId, string? focus, CancellationToken cancellationToken = default) => inner.CompactAsync(sessionId, focus, cancellationToken);
        public Task<string> ForkAsync(string sessionId, long? throughPosition, CancellationToken cancellationToken = default) => inner.ForkAsync(sessionId, throughPosition, cancellationToken);
        public Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken = default) => inner.RenameAsync(sessionId, title, cancellationToken);
    }

    private static async Task<(SessionViewModel Session, PushingService Service, ManualTimeProvider Time)> Open()
    {
        var time = ManualTimeProvider.Demo();
        var service = new PushingService(new DemoSessionService(new InlineDispatcher(), time));
        var session = new SessionViewModel(Idle, service, new InlineDispatcher(), time);
        await session.OpenAsync();
        return (session, service, time);
    }

    [Fact]
    public async Task TheHeaderShowsThisSessionsGeneratorAndDeciderAsTheySwitch()
    {
        var (session, service, time) = await Open();
        service.Push(new EngineStatus { Model = "qwen3.5-0.8b", Decider = "kev-0.8b", At = time.GetUtcNow() });
        Assert.Equal("qwen3.5-0.8b · decider kev-0.8b", session.ModelsText);
        Assert.True(session.HasModels);

        // The decider's own row names its slot, and leaves the generator alone.
        service.Push(new ModelSwitched("decider", "kev-0.8b", "kev-4b", ModelSwitchKind.Switched, "for this session") { At = time.GetUtcNow() });
        service.Push(new EngineStatus { Decider = "kev-4b", At = time.GetUtcNow() });
        var row = session.Blocks.OfType<SwitchBlock>().Last();
        Assert.Equal(("decider switched to", "kev-4b"), (row.Lead, row.Model));
        Assert.Equal("qwen3.5-0.8b · decider kev-4b", session.ModelsText);

        // A configured name holding hidden characters is shown cleaned.
        service.Push(new ModelSwitched("generator", "qwen3.5-0.8b", "big‮qwen", ModelSwitchKind.Switched, "for this session") { At = time.GetUtcNow() });
        Assert.DoesNotContain("‮", session.ModelsText, StringComparison.Ordinal);
        Assert.DoesNotContain("‮", session.Blocks.OfType<SwitchBlock>().Last().Model, StringComparison.Ordinal);
    }

    [Fact]
    public async Task APrivacyRetreatIsShownAsSuchAndMovesTheGenerator()
    {
        var (session, service, time) = await Open();
        service.Push(new ModelSwitched("generator", "qwen3.5-0.8b", "claude", ModelSwitchKind.Escalated, "escalated") { At = time.GetUtcNow(), Cloud = true });
        service.Push(new ModelSwitched("generator", "claude", "qwen3.5-0.8b", ModelSwitchKind.KeptLocal, "the privacy gate kept it local") { At = time.GetUtcNow() });

        var rows = session.Blocks.OfType<SwitchBlock>().TakeLast(2).ToList();
        Assert.Equal(("escalated to", "claude", true), (rows[0].Lead, rows[0].Model, rows[0].HasEgressLink));
        Assert.Equal(("kept local on", "qwen3.5-0.8b", false), (rows[1].Lead, rows[1].Model, rows[1].HasEgressLink));
        Assert.StartsWith("qwen3.5-0.8b", session.ModelsText, StringComparison.Ordinal);

        // A refused escalation that stayed where it was still reads "kept local".
        service.Push(new ModelSwitched("generator", "qwen3.5-0.8b", "", ModelSwitchKind.KeptLocal, "the privacy gate refused claude") { At = time.GetUtcNow() });
        Assert.Equal(("kept local", ""), (session.Blocks.OfType<SwitchBlock>().Last().Lead, session.Blocks.OfType<SwitchBlock>().Last().Model));
    }

    [Fact]
    public async Task EachStepAndCallSaysItsGeneratorOnceTheTurnHasSwitched()
    {
        var (session, service, time) = await Open();
        var at = time.GetUtcNow();
        service.Push(new UserMessage("fix the test") { At = at });
        service.Push(new TurnStarted(20, "qwen3.5-0.8b") { At = at, MaxSteps = 6 });
        service.Push(new TurnProgress(1) { At = at, MaxSteps = 6, Model = "qwen3.5-0.8b" });
        service.Push(new ToolCall("c1", "read_file", ToolKind.Read, ToolCallStatus.InProgress) { At = at, Model = "qwen3.5-0.8b" });
        service.Push(new ModelSwitched("generator", "qwen3.5-0.8b", "qwen-27b", ModelSwitchKind.Escalated, "escalated") { At = at });
        service.Push(new TurnProgress(2) { At = at, MaxSteps = 6, Model = "qwen-27b" });
        service.Push(new ToolCall("c2", "edit_file", ToolKind.Edit, ToolCallStatus.InProgress) { At = at, Model = "qwen-27b" });

        var cards = session.Blocks.OfType<ToolCardBlock>().Where(card => card.CallId is "c1" or "c2").ToList();
        Assert.Equal(("generated by qwen3.5-0.8b", false), (cards[0].GeneratedBy, cards[0].ShowGeneratedBy));
        Assert.Equal(("generated by qwen-27b", true), (cards[1].GeneratedBy, cards[1].ShowGeneratedBy));
        var header = session.Blocks.OfType<TurnHeaderBlock>().Last();
        Assert.Contains("qwen3.5-0.8b → qwen-27b", header.Detail, StringComparison.Ordinal);
        Assert.Contains("step 2 of 6 on qwen-27b", header.Detail, StringComparison.Ordinal);
    }
}
