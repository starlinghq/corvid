using Corvid.Host.Configuration;
using Corvid.Host.Diagnostics;
using Corvid.Models;
using Microsoft.Extensions.DependencyInjection;
using FakeVault = Corvid.Models.Remote.Tests.FakeVault;

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

    private static readonly CudaDevice[] TwoGpus =
    [
        new(0, "NVIDIA GeForce RTX 3090", "GPU-9e21a72e-3a6f-e082-3d74-4b8d361de70c", 24UL << 30, 0),
        new(1, "NVIDIA GeForce RTX 4060 Ti", "GPU-29a0f21a-bfe7-f44b-1c3a-f8983fdf8b23", 16UL << 30, 0),
    ];

    /// <summary>Review of #50, finding 1: NVML lists every GPU; CUDA numbers only the visible ones, up to the first invalid entry.</summary>
    [Theory]
    [InlineData(null, 2)]
    [InlineData("", 0)]
    [InlineData("-1", 0)]
    [InlineData("0", 1)]
    [InlineData("1,0", 2)]
    [InlineData("0,3,1", 1)]
    [InlineData("0,0", 1)]
    [InlineData(" 1 ", 1)]
    [InlineData("GPU-9e21a72e", 1)]
    [InlineData("GPU-29a0f21a-bfe7-f44b-1c3a-f8983fdf8b23,0", 2)]
    [InlineData("GPU-", 0)]
    [InlineData("GPU-ffff", 0)]
    [InlineData("MIG-1,MIG-2", 2)]
    public void Cuda_numbers_the_visible_devices_as_cuda_does(string? visible, int expected) =>
        Assert.Equal(expected, CudaProbe.CountVisible(visible, TwoGpus));

    /// <summary>
    /// Review of #50, finding 1: with every other gpu line Ok, an ordinal at or above the visible count is a miss by the
    /// model's fallback, with the fix naming the range; below it, the line stays Ok.
    /// </summary>
    [Theory]
    [InlineData(0, 1, PlacementFallback.Fail, CheckStatus.Ok)]
    [InlineData(1, 1, PlacementFallback.Fail, CheckStatus.Failed)]
    [InlineData(1, 1, PlacementFallback.Cpu, CheckStatus.Warning)]
    [InlineData(1, 2, PlacementFallback.Fail, CheckStatus.Ok)]
    [InlineData(0, 0, PlacementFallback.Fail, CheckStatus.Failed)]
    public void A_placement_past_the_visible_devices_is_not_ok(int ordinal, int visible, PlacementFallback fallback, CheckStatus expected)
    {
        var device = ModelPlacement.Cuda(ordinal);
        var check = Doctor.PlacedModel("kev-0.8b", device, ordinal, fallback, problems: 0, visible, "0",
            fallback == PlacementFallback.Fail ? CheckStatus.Failed : CheckStatus.Warning);

        Assert.Equal(expected, check.Status);
        if (expected == CheckStatus.Ok)
        {
            Assert.Null(check.Fix);
            return;
        }
        Assert.Contains($"CUDA exposes {visible} device", check.Detail, StringComparison.Ordinal);
        Assert.Contains($"so there is no {device}", check.Detail, StringComparison.Ordinal);
        Assert.Contains(fallback == PlacementFallback.Cpu ? "run on the CPU" : "degraded tier", check.Detail, StringComparison.Ordinal);
        Assert.Contains("CUDA_VISIBLE_DEVICES", check.Fix, StringComparison.Ordinal);
        if (visible == 1)
            Assert.StartsWith("Place kev-0.8b on cuda:0,", check.Fix, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_visible_count_leaves_the_placement_to_the_other_checks()
    {
        var check = Doctor.PlacedModel("kev-0.8b", "cuda:3", 3, PlacementFallback.Fail, problems: 0, visibleCount: null, null, CheckStatus.Failed);
        Assert.Equal(CheckStatus.Ok, check.Status);
        Assert.Equal(CheckStatus.Failed,
            Doctor.PlacedModel("kev-0.8b", "cuda:0", 0, PlacementFallback.Fail, problems: 1, visibleCount: null, null, CheckStatus.Failed).Status);
    }

    /// <summary>V-003b: the shell's network egress, off by default; when on, the launcher and Vault must both support it.</summary>
    [Fact]
    public async Task NetworkEgressIsOffByDefault()
    {
        await using var host = TestHost.Create();
        var checks = await host.Services.GetRequiredService<Doctor>().RunAsync(cancellationToken: TestContext.Current.CancellationToken);

        var egress = Assert.Single(checks, check => check is { Area: "sandbox", Name: "network egress" });
        Assert.Equal(CheckStatus.Info, egress.Status);
        Assert.StartsWith("off", egress.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NetworkEgressNeedsALauncherWithTheProxyOptionsAndAVaultThatOpensSandboxes()
    {
        if (OperatingSystem.IsLinux())
            await EgressChecksAsync();
        else
            Assert.Skip("The sandbox is Linux only.");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static async Task EgressChecksAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var directory = Corvid.Tests.ShortTemp.Create("cd-");
        try
        {
            var current = Path.Combine(directory, "corvid-sandbox");
            await File.WriteAllTextAsync(current, "#!/bin/sh\necho '  --proxy-socket PATH     Corvid Vault per-sandbox proxy socket.' >&2\n", ct);
            var old = Path.Combine(directory, "old-corvid-sandbox");
            await File.WriteAllTextAsync(old, "#!/bin/sh\necho '  --network               Share the host network.' >&2\n", ct);
            foreach (var script in (string[])[current, old])
                File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            await using var vault = new FakeVault(_ => FakeVault.Json("{}"));

            async Task<DoctorCheck> EgressAsync(string launcher, string socket)
            {
                await using var host = TestHost.Create(settings: new Dictionary<string, string?>
                {
                    ["Corvid:Tools:ShellNetwork"] = "true",
                    ["Corvid:Sandbox:LauncherPath"] = launcher,
                    ["Corvid:Vault:SocketPath"] = socket,
                });
                var checks = await host.Services.GetRequiredService<Doctor>().RunAsync(cancellationToken: ct);
                return Assert.Single(checks, check => check is { Area: "sandbox", Name: "network egress" });
            }

            var ok = await EgressAsync(current, vault.SocketPath);
            Assert.Equal(CheckStatus.Ok, ok.Status);
            Assert.Single(vault.SandboxesOpened);
            Assert.Equal(vault.SandboxesOpened, vault.SandboxesClosed); // the probe closes what it opened

            var oldLauncher = await EgressAsync(old, vault.SocketPath);
            Assert.Equal(CheckStatus.Failed, oldLauncher.Status);
            Assert.Contains("--proxy-socket", oldLauncher.Detail, StringComparison.Ordinal);

            var noVault = await EgressAsync(current, Path.Combine(directory, "absent.sock"));
            Assert.Equal(CheckStatus.Failed, noVault.Status);
            Assert.Equal("Start corvid-vault 0.5 or later, or unset Corvid:Tools:ShellNetwork.", noVault.Fix);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ShellNetworkBindsAndReachesTheShell()
    {
        await using (var host = TestHost.Create(settings: new Dictionary<string, string?> { ["Corvid:Tools:Builtin"] = "true" }))
        await using (var runtime = await host.StartAsync())
        {
            Assert.False(host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CorvidOptions>>().Value.Tools.ShellNetwork);
            Assert.False(runtime.Builtin.Shell.Options.Network);
        }

        await using (var host = TestHost.Create(settings: new Dictionary<string, string?>
        {
            ["Corvid:Tools:Builtin"] = "true",
            ["Corvid:Tools:ShellNetwork"] = "true",
            ["Corvid:Vault:SocketPath"] = "/srv/run/vault.sock",
        }))
        await using (var runtime = await host.StartAsync())
        {
            Assert.True(host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CorvidOptions>>().Value.Tools.ShellNetwork);
            Assert.True(runtime.Builtin.Shell.Options.Network);
            // Other sandboxes' proxy sockets are hidden from every command and on the hardline list.
            Assert.Contains("/srv/run/sandbox", runtime.Builtin.Shell.Policy.DeniedPaths);
            Assert.Contains("/srv/run/sandbox", runtime.Builtin.Environment.Protected.Vault);
        }
    }
}
