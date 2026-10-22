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

        Assert.Equal(["config", "models", "gpu", "sandbox", "checkpoints", "vault", "skills"], checks.Select(check => check.Area).Distinct());
        // Nothing is placed on a GPU (C-029): the gpu area only informs.
        Assert.All(checks.Where(check => check.Area == "gpu"), check => Assert.Equal(CheckStatus.Info, check.Status));
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

    /// <summary>
    /// C-029: a model placed on CUDA turns the gpu area into checks. This test process has the CPU natives, so the ONNX
    /// Runtime check misses: Failed under Fallback=Fail, a Warning under Fallback=Cpu, with the fix naming the CUDA build.
    /// </summary>
    [Theory]
    [InlineData("Fail", CheckStatus.Failed)]
    [InlineData("Cpu", CheckStatus.Warning)]
    public async Task A_model_placed_on_cuda_in_a_cpu_build_is_reported_by_its_fallback(string fallback, CheckStatus expected)
    {
        await using var host = TestHost.Create(settings: new Dictionary<string, string?>
        {
            ["Corvid:Models:Placement:fake-kev:Device"] = "cuda",
            ["Corvid:Models:Placement:fake-kev:Fallback"] = fallback,
        });
        var checks = await host.Services.GetRequiredService<Doctor>().RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runtime = Assert.Single(checks, check => check is { Area: "gpu", Name: "onnx runtime" });
        Assert.Equal(expected, runtime.Status);
        Assert.StartsWith("CPU build", runtime.Detail, StringComparison.Ordinal);
        Assert.Contains("-p:UseCuda=true", runtime.Fix, StringComparison.Ordinal);
        var model = Assert.Single(checks, check => check is { Area: "gpu", Name: "fake-kev" });
        Assert.Equal(expected, model.Status);
        Assert.StartsWith($"fake-kev on cuda:0, fallback {fallback.ToLowerInvariant()}", model.Detail, StringComparison.Ordinal);
        Assert.Empty(host.Models.Provider.Loaded);
    }
}
