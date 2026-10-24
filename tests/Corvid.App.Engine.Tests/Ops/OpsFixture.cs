using Corvid.App.Core.Tests;
using Corvid.Host.Composition;
using Corvid.Host.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.App.Engine.Tests.Ops;

/// <summary>
/// The ops pages' engine: a started runtime on the host tests' fakes in a temporary directory, with the settings and
/// services a test adds (the scheduler, an MCP server, a gateway adapter, a stand-in Vault's socket). Never the user's own
/// Corvid home: the data, workspace, skills and rules live under the test host's root, its configuration is the
/// <c>corvid.json</c> there.
/// </summary>
internal sealed class OpsFixture : IAsyncDisposable
{
    private OpsFixture(TestHost host, CorvidRuntime runtime)
    {
        Host = host;
        Runtime = runtime;
        Engine = AppEngine.ForRuntime(host.Host, runtime);
    }

    public TestHost Host { get; }

    public CorvidRuntime Runtime { get; }

    public AppEngine Engine { get; }

    public ManualTimeProvider Time { get; } = ManualTimeProvider.Demo();

    public static async Task<OpsFixture> StartAsync(IReadOnlyDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null,
        FakeModels? models = null)
    {
        var host = TestHost.Create(models, settings, services);
        try
        {
            return new OpsFixture(host, await host.StartAsync());
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public string ConfigText => File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : "";

    public string ConfigPath => Path.Combine(Host.Root, "corvid.json");

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        await Host.DisposeAsync();
    }
}
