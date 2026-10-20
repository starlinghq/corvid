using Corvid.Host.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

public sealed class DoctorTests
{
    [Fact]
    public async Task ReportsEveryAreaWithoutLoadingAModel()
    {
        await using var host = TestHost.Create();
        var checks = await host.Services.GetRequiredService<Doctor>().RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["config", "models", "sandbox", "checkpoints", "vault"], checks.Select(check => check.Area).Distinct());
        Assert.Contains(checks, check => check is { Name: "configuration", Status: CheckStatus.Ok });
        Assert.Contains(checks, check => check is { Name: "data directory", Status: CheckStatus.Ok });
        Assert.Contains(checks, check => check is { Name: "fake-gen", Status: CheckStatus.Ok } && check.Detail.Contains("generator", StringComparison.Ordinal));
        // The fakes unbind the decider: the degraded tier, said as such.
        Assert.Contains(checks, check => check is { Name: "decider", Status: CheckStatus.Info } && check.Detail.Contains("degraded tier", StringComparison.Ordinal));
        Assert.Contains(checks, check => check.Name == "bubblewrap");
        Assert.Contains(checks, check => check.Name == "corvid-sandbox");
        Assert.Contains(checks, check => check.Area == "vault");
        Assert.Contains(checks, check => check.Name == Doctor.MallocVariable);
        Assert.Empty(host.Models.Provider.Loaded);
    }

    [Fact]
    public async Task AMissingConfiguredModelFailsWithTheCommandThatFetchesIt()
    {
        await using var host = TestHost.Create(new FakeModels(generatorReady: false));
        var checks = await host.Services.GetRequiredService<Doctor>().RunAsync(cancellationToken: TestContext.Current.CancellationToken);
        var generator = Assert.Single(checks, check => check.Name == "fake-gen");
        Assert.Equal(CheckStatus.Failed, generator.Status);
        Assert.Equal("corvid model download fake-gen", generator.Fix);
    }

    [Fact]
    public async Task AnInvalidConfigurationIsAFailedCheckNotACrash()
    {
        await using var host = TestHost.Create(settings: new Dictionary<string, string?> { ["Corvid:Models:Slots:generator:Model"] = "nope" });
        var checks = await host.Services.GetRequiredService<Doctor>().RunAsync(cancellationToken: TestContext.Current.CancellationToken);
        var configuration = Assert.Single(checks, check => check.Name == "configuration");
        Assert.Equal(CheckStatus.Failed, configuration.Status);
        Assert.Contains("no model is named 'nope'", configuration.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSandboxLauncherIsFoundWhereConfigured()
    {
        var launcher = Path.Combine(Path.GetTempPath(), "corvid-sandbox-" + Guid.NewGuid().ToString("N"));
        await File.WriteAllTextAsync(launcher, "", TestContext.Current.CancellationToken);
        try
        {
            await using var host = TestHost.Create(settings: new Dictionary<string, string?> { ["Corvid:Sandbox:LauncherPath"] = launcher });
            var checks = await host.Services.GetRequiredService<Doctor>().RunAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(CheckStatus.Ok, Assert.Single(checks, check => check.Name == "corvid-sandbox").Status);
        }
        finally
        {
            File.Delete(launcher);
        }
    }
}
