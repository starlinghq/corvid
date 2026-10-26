using Corvid.App.Core.Audit;
using Corvid.App.Core.Board;
using Corvid.App.Core.FirstRun;
using Corvid.App.Core.Gateway;
using Corvid.App.Core.Jobs;
using Corvid.App.Core.Mcp;
using Corvid.App.Core.Memory;
using Corvid.App.Core.Models;
using Corvid.App.Core.Palette;
using Corvid.App.Core.Review;
using Corvid.App.Core.Rewind;
using Corvid.App.Core.Safety;
using Corvid.App.Core.Secrets;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Settings;
using Corvid.App.Core.Shell;
using Corvid.App.Core.Skills;

namespace Corvid.App.Core.Composition;

/// <summary>
/// A request the service behind a page cannot serve: its engine side is not built yet ("comes with A3 history: …"), or
/// the engine could not start. Pages show the message as their red notice, so it says what is missing in a sentence.
/// </summary>
public sealed class ServiceUnavailableException(string message) : InvalidOperationException(message);

/// <summary>
/// Services that refuse: every request fails with <see cref="ServiceUnavailableException"/> and the given reason, events
/// never fire, and the synchronous members return empty values, so the page opens, shows the reason and offers nothing
/// to act on. Engine mode uses them for areas whose engine service is not built yet, and for every area when the head has
/// no engine (the WebAssembly head before attached mode). They hold no data, so they never show the demo's.
/// </summary>
public static class Unavailable
{
    /// <summary>The refusal's sentence for an area that comes with a later group of the engine phase.</summary>
    /// <param name="group">The A3 group that brings it: sessions, history, setup, knowledge, ops or integration.</param>
    /// <param name="what">What the page would show, as a sentence's end ("the checkpoint timeline over the ledger").</param>
    public static string ComesWith(string group, string what) => $"This comes with A3 {group}: {what} is not built on the engine yet.";

    private static Task Fail(string message) => Task.FromException(new ServiceUnavailableException(message));

    private static Task<T> Fail<T>(string message) => Task.FromException<T>(new ServiceUnavailableException(message));

    public sealed class Sessions(string message) : ISessionService
    {
        public event EventHandler<SessionSummary>? SessionChanged { add { } remove { } }

        public Task<IReadOnlyList<SessionSummary>> ListAsync(CancellationToken cancellationToken = default) => Fail<IReadOnlyList<SessionSummary>>(message);

        public Task<ISessionSubscription> OpenAsync(string sessionId, Action<SessionUpdate> onUpdate, CancellationToken cancellationToken = default) =>
            Fail<ISessionSubscription>(message);

        public Task SendAsync(string sessionId, string text, CancellationToken cancellationToken = default) => Fail(message);

        public Task<QueuedMessage> QueueAsync(string sessionId, string text, CancellationToken cancellationToken = default) => Fail<QueuedMessage>(message);

        public Task RemoveQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) => Fail(message);

        public Task MoveQueuedAsync(string sessionId, string queuedId, int index, CancellationToken cancellationToken = default) => Fail(message);

        public Task<string?> TakeBackQueuedAsync(string sessionId, string queuedId, CancellationToken cancellationToken = default) => Fail<string?>(message);

        public Task SteerAsync(string sessionId, string text, CancellationToken cancellationToken = default) => Fail(message);

        public Task InterruptAsync(string sessionId, CancellationToken cancellationToken = default) => Fail(message);

        public Task AnswerApprovalAsync(string sessionId, string approvalId, ApprovalAnswer answer, CancellationToken cancellationToken = default) => Fail(message);

        public Task AnswerQuestionAsync(string sessionId, string questionId, string answer, CancellationToken cancellationToken = default) => Fail(message);

        public Task RunCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default) => Fail(message);

        public Task SetApprovalModeAsync(string sessionId, Corvid.App.Core.Shell.ApprovalMode mode, CancellationToken cancellationToken = default) => Fail(message);

        public Task SetGoalAsync(string sessionId, string? goal, CancellationToken cancellationToken = default) => Fail(message);

        public Task CompactAsync(string sessionId, string? focus, CancellationToken cancellationToken = default) => Fail(message);

        public Task<string> ForkAsync(string sessionId, long? throughPosition, CancellationToken cancellationToken = default) => Fail<string>(message);

        public Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken = default) => Fail(message);
    }

    public sealed class Board(string message) : IBoardService
    {
        public event EventHandler? AttentionChanged { add { } remove { } }

        public Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(CancellationToken cancellationToken = default) => Fail<IReadOnlyList<AttentionItem>>(message);

        public Task ResolveAsync(string itemId, string actionId, CancellationToken cancellationToken = default) => Fail(message);

        public Task<BoardStatus> GetStatusAsync(CancellationToken cancellationToken = default) => Fail<BoardStatus>(message);
    }

    public sealed class Review(string message) : IReviewService
    {
        public event EventHandler<string>? ChangesChanged { add { } remove { } }

        public Task<ReviewChanges> GetChangesAsync(string sessionId, ReviewScope scope, CancellationToken cancellationToken = default) => Fail<ReviewChanges>(message);

        public Task<ReviewPoint> SendCommentsAsync(string sessionId, IReadOnlyList<ReviewComment> comments, CancellationToken cancellationToken = default) =>
            Fail<ReviewPoint>(message);

        public Task<FileRevertResult> RevertFileAsync(string sessionId, string path, string checkpointId, CancellationToken cancellationToken = default) =>
            Fail<FileRevertResult>(message);

        public Task<string> OpenInEditorAsync(string path, int line, CancellationToken cancellationToken = default) => Fail<string>(message);
    }

    public sealed class Rewind(string message) : IRewindService
    {
        public event EventHandler<string>? TimelineChanged { add { } remove { } }

        public Task<RewindTimeline> GetTimelineAsync(string sessionId, CancellationToken cancellationToken = default) => Fail<RewindTimeline>(message);

        public Task<RestorePlan> PlanAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default) => Fail<RestorePlan>(message);

        public Task<IReadOnlyList<RestorePreviewFile>> PreviewAsync(string sessionId, string checkpointId, CancellationToken cancellationToken = default) =>
            Fail<IReadOnlyList<RestorePreviewFile>>(message);

        public Task<RestoreOutcome> RestoreAsync(string sessionId, string checkpointId, Corvid.App.Core.Sessions.RewindScope scope, CancellationToken cancellationToken = default) =>
            Fail<RestoreOutcome>(message);
    }

    public sealed class Audit(string message) : IAuditService
    {
        public Task<AuditLogInfo> GetLogAsync(CancellationToken cancellationToken) => Fail<AuditLogInfo>(message);

        public Task<IReadOnlyList<AuditSession>> ListSessionsAsync(CancellationToken cancellationToken) => Fail<IReadOnlyList<AuditSession>>(message);

        public Task<IReadOnlyList<AuditEvent>> ReadEventsAsync(string? sessionId, CancellationToken cancellationToken) => Fail<IReadOnlyList<AuditEvent>>(message);

        public Task<IReadOnlyList<EgressRequest>> ListEgressAsync(string? sessionId, CancellationToken cancellationToken) => Fail<IReadOnlyList<EgressRequest>>(message);

        public Task<IReadOnlyList<DetectionMatch>> ListDetectionsAsync(string? sessionId, CancellationToken cancellationToken) =>
            Fail<IReadOnlyList<DetectionMatch>>(message);

        public Task<ScanResult?> GetLastScanAsync(string? sessionId, CancellationToken cancellationToken) => Fail<ScanResult?>(message);

        public Task<ScanResult> ScanAsync(string? sessionId, string failOn, CancellationToken cancellationToken) => Fail<ScanResult>(message);

        public Task<TraceExportResult> ExportAsync(string? sessionId, TraceExportOptions options, CancellationToken cancellationToken) => Fail<TraceExportResult>(message);
    }

    public sealed class Memory(string message) : IMemoryService
    {
        public Task<MemorySnapshot> LoadAsync(CancellationToken cancellationToken) => Fail<MemorySnapshot>(message);

        public Task<FactWrite> ApproveFactAsync(string id, MemoryOperation operation, string text, string? targetFactId, CancellationToken cancellationToken) =>
            Fail<FactWrite>(message);

        public Task<CoreBlock> ApproveBlockUpdateAsync(string id, string text, bool replacesBlock, CancellationToken cancellationToken) => Fail<CoreBlock>(message);

        public Task RejectAsync(string id, string reason, CancellationToken cancellationToken) => Fail(message);

        public Task<BlockConsolidation> ConsolidateAsync(string updateId, CancellationToken cancellationToken) => Fail<BlockConsolidation>(message);

        public Task<StagedBlockUpdate> ConsolidateBlockAsync(string block, CancellationToken cancellationToken) => Fail<StagedBlockUpdate>(message);

        public Task<int> CountTokensAsync(string text, CancellationToken cancellationToken) => Fail<int>(message);

        public Task<Lesson> ApproveLessonAsync(string id, string? supersedesId, CancellationToken cancellationToken) => Fail<Lesson>(message);

        public Task RejectLessonAsync(string id, string reason, CancellationToken cancellationToken) => Fail(message);

        public Task<IReadOnlyList<FactRecord>> SearchFactsAsync(string query, CancellationToken cancellationToken) => Fail<IReadOnlyList<FactRecord>>(message);

        public Task<IReadOnlyList<ImportEntry>> DryRunImportAsync(CancellationToken cancellationToken) => Fail<IReadOnlyList<ImportEntry>>(message);

        public Task<IReadOnlyList<StagedFact>> ImportForReviewAsync(IReadOnlyList<string> entryIds, CancellationToken cancellationToken) =>
            Fail<IReadOnlyList<StagedFact>>(message);
    }

    public sealed class Skills(string message) : ISkillsService
    {
        public Task<SkillsSnapshot> LoadAsync(CancellationToken cancellationToken) => Fail<SkillsSnapshot>(message);

        public Task<SkillRecord> ApproveDraftAsync(string draftId, string? editedContent, CancellationToken cancellationToken) => Fail<SkillRecord>(message);

        public Task RejectDraftAsync(string draftId, string reason, CancellationToken cancellationToken) => Fail(message);

        public Task<IReadOnlyList<SkillCommit>> LogAsync(string? skill, int limit, CancellationToken cancellationToken) => Fail<IReadOnlyList<SkillCommit>>(message);

        public Task<SkillCommitDetail> ShowAsync(string revision, CancellationToken cancellationToken) => Fail<SkillCommitDetail>(message);

        public Task<SkillCommit> RevertAsync(string revision, CancellationToken cancellationToken) => Fail<SkillCommit>(message);

        public Task<IReadOnlyList<EvalRun>> EvalHistoryAsync(string skill, int limit, CancellationToken cancellationToken) => Fail<IReadOnlyList<EvalRun>>(message);

        public Task<EvalRun?> RunEvalsAsync(string skill, CancellationToken cancellationToken) => Fail<EvalRun?>(message);

        public Task SetPinnedAsync(string skill, bool pinned, CancellationToken cancellationToken) => Fail(message);

        public Task<IReadOnlyList<string>> ArchiveAsync(IReadOnlyList<string> skills, CancellationToken cancellationToken) => Fail<IReadOnlyList<string>>(message);

        public Task<ImportScan> ScanImportAsync(SkillImportKind kind, string? folder, string? source, CancellationToken cancellationToken) => Fail<ImportScan>(message);

        public Task<SkillImportResult> ImportAsync(ImportScan scan, IReadOnlyList<string> candidateIds, CancellationToken cancellationToken) =>
            Fail<SkillImportResult>(message);
    }

    public sealed class Safety(string message) : ISafetyService
    {
        public event EventHandler? Changed { add { } remove { } }

        public Task<SafetySnapshot> LoadAsync(CancellationToken cancellationToken = default) => Fail<SafetySnapshot>(message);

        public Task RemoveRuleAsync(string ruleId, CancellationToken cancellationToken = default) => Fail(message);

        public Task SetEnforceAsync(string pack, bool enforce, CancellationToken cancellationToken = default) => Fail(message);
    }

    public sealed class Jobs(string message) : IJobsService
    {
        public event EventHandler? Changed { add { } remove { } }

        public Task<JobsSnapshot> LoadAsync(CancellationToken cancellationToken) => Fail<JobsSnapshot>(message);

        public Task<IReadOnlyList<JobRunRecord>> RunsAsync(string jobId, CancellationToken cancellationToken) => Fail<IReadOnlyList<JobRunRecord>>(message);

        public Task<SchedulePreview> PreviewAsync(string schedule, CancellationToken cancellationToken) => Fail<SchedulePreview>(message);

        public Task<JobRecord> CreateAsync(JobDraft draft, CancellationToken cancellationToken) => Fail<JobRecord>(message);

        public Task<JobRecord> PauseAsync(string jobId, CancellationToken cancellationToken) => Fail<JobRecord>(message);

        public Task<JobRecord> ResumeAsync(string jobId, CancellationToken cancellationToken) => Fail<JobRecord>(message);

        public Task RemoveAsync(string jobId, CancellationToken cancellationToken) => Fail(message);
    }

    public sealed class Mcp(string message) : IMcpService
    {
        public event EventHandler? Changed { add { } remove { } }

        public Task<McpSnapshot> LoadAsync(CancellationToken cancellationToken) => Fail<McpSnapshot>(message);

        public Task AcceptAsync(string server, string tool, string hash, CancellationToken cancellationToken) => Fail(message);

        public Task AllowAsync(string server, string tool, string hash, CancellationToken cancellationToken) => Fail(message);

        public Task KeepBlockedAsync(string server, string tool, CancellationToken cancellationToken) => Fail(message);

        public Task<int> ForgetAsync(string server, CancellationToken cancellationToken) => Fail<int>(message);

        public Task SetTrustAsync(string server, Corvid.App.Core.Mcp.McpTrust trust, CancellationToken cancellationToken) => Fail(message);

        public Task<McpServerRecord> AddServerAsync(McpServerDraft draft, CancellationToken cancellationToken) => Fail<McpServerRecord>(message);
    }

    public sealed class Gateway(string message) : IGatewayService
    {
        public event EventHandler? Changed { add { } remove { } }

        public Task<GatewaySnapshot> LoadAsync(CancellationToken cancellationToken) => Fail<GatewaySnapshot>(message);

        public Task<PairedUser> PairAsync(string requestId, PairingRole role, CancellationToken cancellationToken) => Fail<PairedUser>(message);

        public Task IgnoreAsync(string requestId, CancellationToken cancellationToken) => Fail(message);

        public Task RevokeAsync(string userId, CancellationToken cancellationToken) => Fail(message);

        public Task<PairedUser> SetAllowlistAsync(string userId, IReadOnlyList<string> tools, CancellationToken cancellationToken) => Fail<PairedUser>(message);
    }

    public sealed class Secrets(string message) : ISecretsService
    {
        public event EventHandler? Changed { add { } remove { } }

        public Task<SecretsSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Fail<SecretsSnapshot>(message);

        public Task<SetterLaunch> StartSetAsync(string name, CancellationToken cancellationToken = default) => Fail<SetterLaunch>(message);

        public Task BindAsync(string consumerId, string secretName, CancellationToken cancellationToken = default) => Fail(message);
    }

    /// <summary>An empty store: no slots, no catalogue; loading and every change fail with the reason.</summary>
    public sealed class Models(string message) : IModelService
    {
        public event EventHandler? Changed { add { } remove { } }

        // Not loaded: the pages then claim neither Vault's absence nor a degraded decider from its empty values.
        public ModelsSnapshot Snapshot { get; } = new("", false, [], [], new CloudPrivacy("", false, false, 0, 0, 0),
            new DeciderStatus("", "", DeciderTier.Degraded, "", "", "", ""), [], [], "") { IsLoaded = false };

        public Task<ModelsSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Fail<ModelsSnapshot>(message);

        public Task<ConfigWrite> SetLadderAsync(string slot, IReadOnlyList<string> ladder, CancellationToken cancellationToken = default) => Fail<ConfigWrite>(message);

        public Task<ConfigWrite> SwitchAsync(string slot, string model, CancellationToken cancellationToken = default) => Fail<ConfigWrite>(message);

        public Task<ConfigWrite> SetPinnedAsync(string slot, bool pinned, CancellationToken cancellationToken = default) => Fail<ConfigWrite>(message);

        public Task<ConfigWrite> SetPlacementAsync(string model, string device, CancellationToken cancellationToken = default) => Fail<ConfigWrite>(message);

        public Task DownloadAsync(IEnumerable<string> models, CancellationToken cancellationToken = default) => Fail(message);

        public Task CancelDownloadAsync(string model, CancellationToken cancellationToken = default) => Fail(message);

        public Task<ServerCheck> CheckServerAsync(string model, CancellationToken cancellationToken = default) => Fail<ServerCheck>(message);

        public Task<ConfigWrite> AddServerAsync(string name, string url, CancellationToken cancellationToken = default) => Fail<ConfigWrite>(message);
    }

    public sealed class FirstRun(string message) : IFirstRunService
    {
        public Task<DoctorReport> RunChecksAsync(CancellationToken cancellationToken = default) => Fail<DoctorReport>(message);

        public Task<IReadOnlyList<string>> SuggestWorkspacesAsync(CancellationToken cancellationToken = default) => Fail<IReadOnlyList<string>>(message);

        public Task<WorkspaceInfo> DescribeWorkspaceAsync(string path, CancellationToken cancellationToken = default) => Fail<WorkspaceInfo>(message);

        public Task<HermesImport?> FindHermesAsync(CancellationToken cancellationToken = default) => Fail<HermesImport?>(message);

        public Task SaveAsync(SetupChoices choices, CancellationToken cancellationToken = default) => Fail(message);
    }

    /// <summary>No definitions to edit; loading and saving fail with the reason, and any text parses to the same refusal.</summary>
    public sealed class Settings(string message) : ISettingsService
    {
        public IReadOnlyList<SettingDefinition> Definitions => [];

        public Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken = default) => Fail<SettingsSnapshot>(message);

        public IReadOnlyList<SettingProblem> Validate(IReadOnlyDictionary<string, string> values) => [new SettingProblem(null, message)];

        public SettingsParse Parse(string json) => new(new Dictionary<string, string>(), [new SettingProblem(null, message)]);

        public Task<SettingsWrite> SaveAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default) => Fail<SettingsWrite>(message);

        public Task<SettingsWrite> SaveJsonAsync(string json, CancellationToken cancellationToken = default) => Fail<SettingsWrite>(message);
    }

    public sealed class PromptHistory(string message) : IPromptHistory
    {
        public Task<IReadOnlyList<PromptHistoryEntry>> LoadAsync(CancellationToken cancellationToken = default) => Fail<IReadOnlyList<PromptHistoryEntry>>(message);
    }
}

/// <summary>
/// Every service refusing with one reason: a head with no engine (the WebAssembly head until attached mode, G-1), or an
/// engine that could not start at all. The base of the engine's own services, which replace areas one by one.
/// </summary>
public class UnavailableAppServices(string reason) : IAppServices
{
    /// <summary>Why every area refuses, unless an area overrides its member.</summary>
    public string Reason { get; } = reason;

    public virtual ISessionService CreateSessionService(AppComposition app) => new Unavailable.Sessions(Reason);

    public virtual IBoardService CreateBoardService(AppComposition app) => new Unavailable.Board(Reason);

    public virtual IReviewService CreateReviewService(AppComposition app) => new Unavailable.Review(Reason);

    public virtual IRewindService CreateRewindService(AppComposition app) => new Unavailable.Rewind(Reason);

    public virtual IAuditService CreateAuditService(AppComposition app) => new Unavailable.Audit(Reason);

    public virtual IMemoryService CreateMemoryService(AppComposition app) => new Unavailable.Memory(Reason);

    public virtual MemoryEngineStatus? CreateMemoryStatus(AppComposition app) => null;

    public virtual ISkillsService CreateSkillsService(AppComposition app) => new Unavailable.Skills(Reason);

    public virtual ISafetyService CreateSafetyService(AppComposition app) => new Unavailable.Safety(Reason);

    public virtual IJobsService CreateJobsService(AppComposition app) => new Unavailable.Jobs(Reason);

    public virtual IMcpService CreateMcpService(AppComposition app) => new Unavailable.Mcp(Reason);

    public virtual IGatewayService CreateGatewayService(AppComposition app) => new Unavailable.Gateway(Reason);

    public virtual ISecretsService CreateSecretsService(AppComposition app) => new Unavailable.Secrets(Reason);

    public virtual IModelService CreateModelService(AppComposition app) => new Unavailable.Models(Reason);

    public virtual IFirstRunService CreateFirstRunService(AppComposition app) => new Unavailable.FirstRun(Reason);

    public virtual ISettingsService CreateSettingsService(AppComposition app) => new Unavailable.Settings(Reason);

    public virtual IPromptHistory CreatePromptHistory(AppComposition app) => new Unavailable.PromptHistory(Reason);

    /// <summary>No episodic index here: the palette searches titles, previews and ids.</summary>
    public virtual ISessionSearch? CreateSessionSearch(AppComposition app) => null;

    public virtual IHostingService CreateHostingService(AppComposition app) => new NoHostingService(Reason);
}
