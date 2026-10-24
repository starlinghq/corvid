using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Sessions;
using Corvid.App.Core.Tests;
using Corvid.App.Engine.Sessions;
using Corvid.Host.Composition;
using Corvid.Host.Tests;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// A started runtime on the host tests' fake models in a temporary directory, wrapped as the window's engine. Never the
/// user's own Corvid home: the test host's settings place the data, workspace, skills and rules under its root, and its
/// configuration file is <c>corvid.json</c> there.
/// </summary>
internal sealed class EngineFixture : IAsyncDisposable
{
    private EngineFixture(TestHost host, CorvidRuntime runtime, AppEngine engine)
    {
        Host = host;
        Runtime = runtime;
        Engine = engine;
    }

    public TestHost Host { get; }

    public CorvidRuntime Runtime { get; }

    public AppEngine Engine { get; }

    public ManualTimeProvider Time { get; } = ManualTimeProvider.Demo();

    public static async Task<EngineFixture> StartAsync(FakeModels? models = null)
    {
        var host = TestHost.Create(models);
        var runtime = await host.StartAsync();
        return new EngineFixture(host, runtime, AppEngine.ForRuntime(host.Host, runtime));
    }

    /// <summary>A session service over the engine, reporting inline on the caller's thread.</summary>
    public EngineSessionService Sessions(IDispatcher? dispatcher = null) => new(Engine, dispatcher ?? new InlineDispatcher(), Time);

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        await Host.DisposeAsync();
    }
}

/// <summary>Updates as a subscriber hears them, from whichever thread reports them.</summary>
internal sealed class Recorder
{
    private readonly List<SessionUpdate> _updates = [];

    public void Add(SessionUpdate update)
    {
        lock (_updates)
            _updates.Add(update);
    }

    public IReadOnlyList<SessionUpdate> Updates
    {
        get
        {
            lock (_updates)
                return [.. _updates];
        }
    }
}
