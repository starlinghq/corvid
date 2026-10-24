using Corvid.App.Core;
using Corvid.App.Core.Audit;
using Corvid.App.Core.Board;
using Corvid.App.Core.Composition;
using Corvid.App.Core.FirstRun;
using Corvid.App.Core.Gateway;
using Corvid.App.Core.Jobs;
using Corvid.App.Core.Mcp;
using Corvid.App.Core.Memory;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Palette;
using Corvid.App.Core.Review;
using Corvid.App.Core.Rewind;
using Corvid.App.Core.Safety;
using Corvid.App.Core.Secrets;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Settings;
using Corvid.App.Core.Skills;
using Corvid.App.Core.Tests;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Tests;
using Microsoft.Extensions.Hosting;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// The composition's seam: demo mode builds the demo's services, engine mode the engine's (and refusals for the areas
/// still to come, never demo data), and disposing the composition disposes the services and their source.
/// </summary>
public sealed class SeamTests
{
    private static readonly AppOptions Engine = new() { Demo = false };

    [Fact]
    public void DemoModeBuildsTheDemosServices()
    {
        var source = new Tracking(new DemoAppServices());
        var composition = new AppComposition(new AppOptions(), new InlineDispatcher(), ManualTimeProvider.Demo(), source);

        Assert.IsType<DemoSessionService>(composition.SessionService);
        Assert.IsType<DemoReviewService>(composition.ReviewService);
        Assert.IsType<DemoRewindService>(composition.RewindService);
        Assert.IsType<DemoModelService>(composition.ModelService);
        Assert.IsType<DemoBoardService>(composition.BoardService);
        Assert.Equal(MemoryEngineStatus.Demo, source.Inner.CreateMemoryStatus(composition));
        // The demo's Review and Rewind work over the demo sessions it made, so the composition casts nothing.
        Assert.Same(composition.SessionService, source.Created.OfType<DemoSessionService>().Single());

        composition.Dispose();
        Assert.True(source.Disposed);
    }

    [Fact]
    public void TheDefaultServicesAreTheDemosInDemoModeAndARefusalWithoutAnEngine()
    {
        using var demo = new AppComposition(new AppOptions(), new InlineDispatcher(), ManualTimeProvider.Demo());
        Assert.IsType<DemoAppServices>(demo.Services);

        using var noEngine = new AppComposition(Engine, new InlineDispatcher(), ManualTimeProvider.Demo());
        Assert.IsType<UnavailableAppServices>(noEngine.Services);
        Assert.IsType<Unavailable.Sessions>(noEngine.SessionService);
        // The rail says why there are no sessions rather than showing the demo's.
        Assert.Contains(AppComposition.NoEngine, noEngine.SessionHub.StartError, StringComparison.Ordinal);
        Assert.Empty(noEngine.Shell.SessionGroups.SelectMany(group => group.Sessions));
    }

    [Fact]
    public async Task EngineModeBuildsTheEnginesSessionsAndRefusesTheAreasStillToCome()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var source = new Tracking(new EngineAppServices(fixture.Engine));
        var composition = new AppComposition(Engine, new InlineDispatcher(), fixture.Time, source);

        var sessions = Assert.IsType<EngineSessionService>(composition.SessionService);
        Assert.Empty(await sessions.ListAsync(TestContext.Current.CancellationToken));
        // The runtime is up, so the Memory page's engine line is the runtime's.
        Assert.StartsWith("fake-gen", source.Inner.CreateMemoryStatus(composition)!.Generator, StringComparison.Ordinal);
        // A3 history's areas are the engine's own.
        Assert.IsType<Corvid.App.Engine.Review.EngineReviewService>(composition.ReviewService);
        Assert.IsType<Corvid.App.Engine.Rewind.EngineRewindService>(composition.RewindService);
        Assert.IsType<Corvid.App.Engine.Audit.EngineAuditService>(composition.AuditService);
        // A3 setup's too.
        Assert.IsType<Corvid.App.Engine.Models.EngineModelService>(composition.ModelService);
        Assert.IsType<Corvid.App.Engine.FirstRun.EngineFirstRunService>(composition.FirstRunService);
        Assert.IsType<Corvid.App.Engine.Settings.EngineSettingsService>(composition.SettingsService);
        // A3 knowledge's too.
        Assert.IsType<Corvid.App.Engine.Memory.EngineMemoryService>(composition.MemoryService);
        Assert.IsType<Corvid.App.Engine.Skills.EngineSkillsService>(composition.SkillsService);
        Assert.IsType<Corvid.App.Engine.Safety.EngineSafetyService>(composition.SafetyService);

        // Every other area opens on a refusal that names the group that brings it.
        (object Service, string Group)[] areas =
        [
            (composition.JobsService, "ops"), (composition.McpService, "ops"), (composition.GatewayService, "ops"),
            (composition.SecretsService, "ops"), (composition.BoardService, "integration"), (composition.PromptHistory, "integration"),
        ];
        foreach (var (service, group) in areas)
        {
            Assert.Equal(typeof(Unavailable), service.GetType().DeclaringType);
            var refusal = await Assert.ThrowsAsync<ServiceUnavailableException>(() => Load(service));
            Assert.StartsWith($"This comes with A3 {group}:", refusal.Message, StringComparison.Ordinal);
        }

        composition.Dispose();
        Assert.True(source.Disposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => sessions.OpenAsync("cli-any", _ => { }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WhenModelsAreMissingTheEngineStartsNothingAndEveryRequestSaysWhy()
    {
        // The generator is not in the fake cache: the head opens First run, and the sessions say what to fetch.
        await using var host = TestHost.Create(new FakeModels(generatorReady: false));
        await using var engine = await AppEngine.StartAsync(host.Host, ownsHost: false, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("fake-gen", Assert.Single(engine.MissingModels).Model.Name);
        Assert.Contains("fake-gen", engine.StartFailure, StringComparison.Ordinal);
        using var composition = new AppComposition(Engine with { Page = Area.FirstRun }, new InlineDispatcher(), ManualTimeProvider.Demo(),
            new EngineAppServices(engine));

        Assert.Equal(Area.FirstRun, composition.Shell.CurrentArea);
        Assert.Contains("Corvid cannot start: Model 'fake-gen' is not in the fake cache. Run `corvid model download fake-gen`.",
            composition.SessionHub.StartError, StringComparison.Ordinal);
        var refusal = await Assert.ThrowsAsync<ServiceUnavailableException>(() =>
            composition.SessionService.SendAsync("cli-any", "hello", TestContext.Current.CancellationToken));
        Assert.StartsWith("Corvid cannot start:", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEngineThatStartsItsRuntimeInTheBackgroundReportsEachStage()
    {
        await using var host = TestHost.Create();
        var steps = new List<(string Description, bool Ended)>();
        var progress = new Steps(step =>
        {
            lock (steps)
                steps.Add((step.Description, step.Elapsed is not null));
        });
        await using var engine = await AppEngine.StartAsync(host.Host, ownsHost: false, progress, TestContext.Current.CancellationToken);

        var runtime = await engine.RuntimeAsync();

        Assert.Null(engine.StartFailure);
        Assert.Empty(engine.MissingModels);
        Assert.Same(runtime, await engine.RuntimeAsync());
        Assert.Null(engine.CurrentStep);
        lock (steps)
        {
            Assert.Contains(("Opening the event log", false), steps);
            Assert.Contains(("Opening the event log", true), steps);
        }
    }

    /// <summary>Calls the member each area's page loads with first, so a refusal shows where the page would show it.</summary>
    private static Task Load(object service) => service switch
    {
        IReviewService review => review.GetChangesAsync("s", ReviewScope.ThisTurn),
        IRewindService rewind => rewind.GetTimelineAsync("s"),
        IAuditService audit => audit.GetLogAsync(CancellationToken.None),
        IModelService models => models.LoadAsync(),
        IFirstRunService firstRun => firstRun.RunChecksAsync(),
        ISettingsService settings => settings.LoadAsync(),
        IMemoryService memory => memory.LoadAsync(CancellationToken.None),
        ISkillsService skills => skills.LoadAsync(CancellationToken.None),
        ISafetyService safety => safety.LoadAsync(),
        IJobsService jobs => jobs.LoadAsync(CancellationToken.None),
        IMcpService mcp => mcp.LoadAsync(CancellationToken.None),
        IGatewayService gateway => gateway.LoadAsync(CancellationToken.None),
        ISecretsService secrets => secrets.LoadAsync(),
        IBoardService board => board.ListAttentionAsync(),
        IPromptHistory history => history.LoadAsync(),
        _ => throw new ArgumentException(service.GetType().Name),
    };

    private sealed class Steps(Action<Corvid.Host.Composition.StartupStep> report) : IProgress<Corvid.Host.Composition.StartupStep>
    {
        public void Report(Corvid.Host.Composition.StartupStep value) => report(value);
    }

    /// <summary>A source that records what it made and whether the composition disposed it.</summary>
    private sealed class Tracking(IAppServices inner) : IAppServices, IDisposable
    {
        public IAppServices Inner { get; } = inner;

        public List<object> Created { get; } = [];

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            (Inner as IDisposable)?.Dispose();
        }

        private T Made<T>(T service) where T : notnull
        {
            Created.Add(service);
            return service;
        }

        public ISessionService CreateSessionService(AppComposition app) => Made(Inner.CreateSessionService(app));
        public IBoardService CreateBoardService(AppComposition app) => Made(Inner.CreateBoardService(app));
        public IReviewService CreateReviewService(AppComposition app) => Made(Inner.CreateReviewService(app));
        public IRewindService CreateRewindService(AppComposition app) => Made(Inner.CreateRewindService(app));
        public IAuditService CreateAuditService(AppComposition app) => Made(Inner.CreateAuditService(app));
        public IMemoryService CreateMemoryService(AppComposition app) => Made(Inner.CreateMemoryService(app));
        public MemoryEngineStatus? CreateMemoryStatus(AppComposition app) => Inner.CreateMemoryStatus(app);
        public ISkillsService CreateSkillsService(AppComposition app) => Made(Inner.CreateSkillsService(app));
        public ISafetyService CreateSafetyService(AppComposition app) => Made(Inner.CreateSafetyService(app));
        public IJobsService CreateJobsService(AppComposition app) => Made(Inner.CreateJobsService(app));
        public IMcpService CreateMcpService(AppComposition app) => Made(Inner.CreateMcpService(app));
        public IGatewayService CreateGatewayService(AppComposition app) => Made(Inner.CreateGatewayService(app));
        public ISecretsService CreateSecretsService(AppComposition app) => Made(Inner.CreateSecretsService(app));
        public IModelService CreateModelService(AppComposition app) => Made(Inner.CreateModelService(app));
        public IFirstRunService CreateFirstRunService(AppComposition app) => Made(Inner.CreateFirstRunService(app));
        public ISettingsService CreateSettingsService(AppComposition app) => Made(Inner.CreateSettingsService(app));
        public IPromptHistory CreatePromptHistory(AppComposition app) => Made(Inner.CreatePromptHistory(app));
    }
}
