using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Host.Telemetry;
using Corvid.Models;
using Corvid.Models.Starling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Corvid.Host.Tests;

/// <summary>
/// C-029: <c>Corvid:Models:Placement</c>. Validated at start; Kev placed on CUDA gets a placed profile (its own device
/// scheduler); when the GPU is not usable, <see cref="PlacementFallback.Cpu"/> loads on the CPU with a notice and
/// <see cref="PlacementFallback.Fail"/> leaves the decider on the degraded tier. No model is loaded: the provider's loader
/// is replaced.
/// </summary>
public sealed class PlacementTests
{
    private static StarlingModelStore EmptyStore() => new(Path.Combine(Path.GetTempPath(), "corvid-placement-" + Guid.NewGuid().ToString("N")));

    private static Dictionary<string, ModelPlacementSettings> Placement(string model, string device, PlacementFallback fallback = PlacementFallback.Fail) =>
        new(StringComparer.OrdinalIgnoreCase) { [model] = new ModelPlacementSettings { Device = device, Fallback = fallback } };

    private static ValidateOptionsResult Validate(Action<CorvidOptions> change)
    {
        var catalogue = new ModelCatalogue([new StarlingModelProvider(EmptyStore()), new FakeModels().Provider]);
        var options = new CorvidOptions();
        change(options);
        return new CorvidOptionsValidator(catalogue).Validate(null, options);
    }

    [Theory]
    [InlineData("kev-0.8b", "cuda", null)]
    [InlineData("KEV-0.8B", "cuda:1", null)]
    [InlineData("kev-4b", "cuda:0", null)]
    [InlineData("qwen3.5-0.8b", "cpu", null)]
    [InlineData("kev-9b", "cuda", "no model is named 'kev-9b'")]
    [InlineData("fake-gen", "cuda", "'fake-gen' is not an in-process Starling model")]
    [InlineData("kev-0.8b", "gpu", "Device must be cpu, cuda or cuda:0 to cuda:15, not 'gpu'")]
    [InlineData("kev-0.8b", "cuda:16", "Device must be")]
    [InlineData("kev-0.8b", "cuda:-1", "Device must be")]
    [InlineData("qwen3.5-0.8b", "cuda", "CUDA placement for qwen3.5-0.8b is not built yet")]
    [InlineData("embeddinggemma-2", "cuda:0", "CUDA placement for embeddinggemma-2 is not built yet")]
    [InlineData("gliner2-base-v1", "cuda", "CUDA placement for gliner2-base-v1 is not built yet")]
    public void Placement_is_validated_at_start(string model, string device, string? error)
    {
        var result = Validate(options => options.Models.Placement = Placement(model, device, PlacementFallback.Cpu));

        if (error is null)
            Assert.True(result.Succeeded, string.Join(" ", result.Failures ?? []));
        else
            Assert.Contains(result.Failures!, failure => failure.Contains(error, StringComparison.Ordinal)
                && failure.StartsWith($"Corvid:Models:Placement:{model}", StringComparison.Ordinal));
    }

    [Fact]
    public void An_undefined_fallback_and_an_out_of_range_state_budget_are_refused()
    {
        Assert.Contains(Validate(options => options.Models.Placement = Placement("kev-0.8b", "cuda", (PlacementFallback)7)).Failures!,
            failure => failure.Contains("Fallback must be Fail or Cpu", StringComparison.Ordinal));
        Assert.Contains(Validate(options => options.Turn.DualShortlistStateTokens = 50).Failures!,
            failure => failure.Contains("Corvid:Turn:DualShortlistStateTokens", StringComparison.Ordinal));
        Assert.True(Validate(options => options.Turn.DualShortlistStateTokens = 600).Succeeded);
    }

    [Fact]
    public void Configuration_is_read_leniently_and_ignores_case()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Corvid:Models:Placement:KEV-0.8B:Device"] = "cuda",
            ["Corvid:Models:Placement:KEV-0.8B:Fallback"] = "cpu",
            ["Corvid:Models:Placement:kev-4b:Device"] = "cuda:1",
            ["Corvid:Models:Placement:kev-4b:Fallback"] = "sometimes",
        }).Build();

        var placement = CorvidHost.ReadPlacement(configuration);

        Assert.Equal(("cuda", PlacementFallback.Cpu), (placement["kev-0.8b"].Device, placement["kev-0.8b"].Fallback));
        Assert.Equal(("cuda:1", PlacementFallback.Fail), (placement["kev-4b"].Device, placement["kev-4b"].Fallback));
        Assert.Empty(CorvidHost.ReadPlacement(new ConfigurationBuilder().Build()));
    }

    [Fact]
    public void A_placed_kev_gets_a_placed_profile_and_the_others_stay_on_the_cpu()
    {
        var placement = Placement("kev-0.8b", "cuda");
        // Refused by the validator; the provider ignores it, so the profile stays the CPU one until start fails.
        placement["qwen3.5-0.8b"] = new ModelPlacementSettings { Device = "cuda" };
        var provider = new StarlingModelProvider(EmptyStore(), placement);

        var kev = provider.Models.Single(model => model.Name == "kev-0.8b").Profile;
        Assert.Equal(("kev-0.8b@cuda:0", "cuda:0"), (kev.Id, kev.Placement));
        Assert.Equal("kev-4b@cpu", provider.Models.Single(model => model.Name == "kev-4b").Profile.Id);
        Assert.Equal("qwen3.5-0.8b@cpu", provider.Models.Single(model => model.Name == "qwen3.5-0.8b").Profile.Id);
        Assert.Equal(StarlingProfiles.Kev08B, new StarlingModelProvider(EmptyStore()).Models.Single(model => model.Name == "kev-0.8b").Profile);
    }

    [Fact]
    public void Fallback_cpu_reloads_on_the_cpu_with_a_notice()
    {
        var provider = Stubbed(Placement("kev-0.8b", "cuda", PlacementFallback.Cpu), cudaWorks: false, out var tried);
        var notices = new List<string>();

        var binding = provider.Load("kev-0.8b", new ModelLoadContext(ModelSlots.Decider, TaskScheduler.Default) { Notice = notices.Add });

        Assert.Equal(["cuda:0", "cpu"], tried);
        Assert.Equal(StarlingProfiles.Kev08B, binding.Profile);
        var notice = Assert.Single(notices);
        Assert.StartsWith("kev-0.8b: configured cuda:0, running on cpu: ", notice, StringComparison.Ordinal);
        Assert.Contains("-p:UseCuda=true", notice, StringComparison.Ordinal);
        Assert.Contains("over 1 s", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void Fallback_fail_propagates_without_a_cpu_retry()
    {
        var provider = Stubbed(Placement("kev-0.8b", "cuda:1"), cudaWorks: false, out var tried);

        var refused = Assert.Throws<ModelPlacementUnavailableException>(() =>
            provider.Load("kev-0.8b", new ModelLoadContext(ModelSlots.Decider, TaskScheduler.Default)));

        Assert.Equal("cuda:1", refused.Placement);
        Assert.Equal(["cuda:1"], tried);
    }

    [Fact]
    public void The_composer_schedules_a_placed_kev_on_its_own_gpu_device()
    {
        var (composer, devices) = Composer(Placement("kev-0.8b", "cuda", PlacementFallback.Cpu), cudaWorks: true);
        using var composed = composer;
        using var scheduled = devices;

        var decider = composer.Compose().Get(ModelSlots.Decider).Default;

        Assert.Equal("cuda:0", decider.Profile.Placement);
        Assert.Empty(composer.DeciderNotices);
        Assert.True(devices.TryGet("cuda:0", out _));
    }

    [Fact]
    public void The_composer_reports_a_cpu_fallback_and_binds_the_cpu_profile()
    {
        var (composer, devices) = Composer(Placement("kev-0.8b", "cuda", PlacementFallback.Cpu), cudaWorks: false);
        using var composed = composer;
        using var scheduled = devices;

        var decider = composer.Compose().Get(ModelSlots.Decider).Default;

        // Effective, not configured: the flag C-030 reads says cpu.
        Assert.Equal("cpu", decider.Profile.Placement);
        Assert.Contains(composer.DeciderNotices, notice => notice.StartsWith("kev-0.8b: configured cuda:0, running on cpu", StringComparison.Ordinal));
    }

    [Fact]
    public void With_fallback_fail_the_decider_degrades_with_a_notice_and_the_start_goes_on()
    {
        var (composer, devices) = Composer(Placement("kev-0.8b", "cuda"), cudaWorks: false);
        using var composed = composer;
        using var scheduled = devices;

        var registry = composer.Compose();

        Assert.False(registry.TryGet(ModelSlots.Decider, out _));
        Assert.True(registry.TryGet(ModelSlots.Generator, out _));
        var notice = Assert.Single(composer.DeciderNotices);
        Assert.Contains("kev-0.8b did not load", notice, StringComparison.Ordinal);
        Assert.Contains("-p:UseCuda=true", notice, StringComparison.Ordinal);
        Assert.Contains("degraded tier", notice, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ReducedCheckpoints.Auto, true, "cpu", "kev-0.8b on cpu")]
    [InlineData(ReducedCheckpoints.Auto, true, "cuda:0", null)]
    [InlineData(ReducedCheckpoints.Auto, true, "cuda:1", null)]
    [InlineData(ReducedCheckpoints.Auto, true, "remote", null)]
    [InlineData(ReducedCheckpoints.Auto, false, null, null)]
    [InlineData(ReducedCheckpoints.On, false, null, "configured")]
    [InlineData(ReducedCheckpoints.On, true, "cuda:0", "configured")]
    [InlineData(ReducedCheckpoints.Off, true, "cpu", null)]
    public void Auto_reduces_checkpoints_only_for_kev_on_the_cpu(ReducedCheckpoints setting, bool calibrated, string? placement, string? reason)
    {
        var reduction = CorvidRuntimeFactory.ReductionFor(setting, calibrated ? new FakeKev("kev-0.8b") : null, placement);

        Assert.Equal(reason, reduction?.Reason);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "kev-0.8b on cpu")]
    public void A_cuda_kev_stays_full_and_one_that_fell_back_to_the_cpu_is_reduced(bool cudaWorks, string? reason)
    {
        var (composer, devices) = Composer(Placement("kev-0.8b", "cuda", PlacementFallback.Cpu), cudaWorks);
        using var composed = composer;
        using var scheduled = devices;

        var decider = composer.Compose().Get(ModelSlots.Decider).Default;
        var reduction = CorvidRuntimeFactory.ReductionFor(ReducedCheckpoints.Auto, decider.GetService<Abstractions.Fabric.IDecider>(),
            decider.Profile.Placement);

        Assert.Equal(reason, reduction?.Reason);
    }

    [Fact]
    public async Task The_runtime_reduces_for_kev_on_the_cpu_and_says_so_in_the_decider_state()
    {
        var kev = new Dictionary<string, string?> { ["Corvid:Models:Slots:decider:Model"] = "fake-kev" };
        await using (var host = TestHost.Create(new FakeModels(), kev))
        await using (var runtime = await host.StartAsync())
        {
            Assert.Equal("fake-kev on cpu", runtime.Fabric.Reduction?.Reason);
            Assert.Equal("fake-kev on cpu; reduced checkpoints (C-030); degraded tier when Kev declines", runtime.DeciderState);
        }

        var off = new Dictionary<string, string?>(kev) { ["Corvid:Turn:ReducedCheckpoints"] = "off" };
        await using (var host = TestHost.Create(new FakeModels(), off))
        await using (var runtime = await host.StartAsync())
        {
            Assert.Null(runtime.Fabric.Reduction);
            Assert.Equal("fake-kev on cpu; degraded tier when Kev declines", runtime.DeciderState);
        }

        // The degraded tier stays full under Auto, so CI (no Kev weights) and the golden turns are unchanged; On reduces it.
        await using (var host = TestHost.Create(new FakeModels(kevReady: false), kev))
        await using (var runtime = await host.StartAsync())
            Assert.Null(runtime.Fabric.Reduction);
        var on = new Dictionary<string, string?> { ["Corvid:Turn:ReducedCheckpoints"] = "On" };
        await using (var host = TestHost.Create(new FakeModels(), on))
        await using (var runtime = await host.StartAsync())
        {
            Assert.Equal("configured", runtime.Fabric.Reduction?.Reason);
            Assert.Equal("degraded tier (no calibrated decider); reduced checkpoints (C-030)", runtime.DeciderState);
        }
    }

    [Fact]
    public void An_undefined_reduced_checkpoints_value_is_refused()
    {
        Assert.Contains(Validate(options => options.Turn.ReducedCheckpoints = (ReducedCheckpoints)9).Failures!,
            failure => failure.Contains("Corvid:Turn:ReducedCheckpoints must be Auto, On or Off", StringComparison.Ordinal));
        Assert.True(Validate(options => options.Turn.ReducedCheckpoints = ReducedCheckpoints.Off).Succeeded);
    }

    // Kev on CUDA fails as a CPU build fails it; on the CPU it binds a fake decider under the CPU profile.
    private static StarlingModelProvider Stubbed(Dictionary<string, ModelPlacementSettings> placement, bool cudaWorks, out List<string> tried)
    {
        var attempts = new List<string>();
        tried = attempts;
        var provider = new StarlingModelProvider(EmptyStore(), placement);
        provider.Loader = (model, device, _) =>
        {
            attempts.Add(device);
            if (device != "cpu" && !cudaWorks)
                throw new ModelPlacementUnavailableException(StarlingModels.Get(model).Name, device,
                    $"{StarlingModels.Get(model).Name} cannot run on {device}: this build has the CPU natives (no CUDAExecutionProvider); build with -p:UseCuda=true.");
            return new ModelBinding(StarlingProfiles.For(model, device), new FakeKev(StarlingModels.Get(model).Name));
        };
        return provider;
    }

    private static (ModelComposer Composer, DeviceSchedulers Devices) Composer(Dictionary<string, ModelPlacementSettings> placement, bool cudaWorks)
    {
        var models = new FakeModels();
        var catalogue = new ModelCatalogue([new ReadyStarling(Stubbed(placement, cudaWorks, out _)), models.Provider]);
        var options = new CorvidOptions();
        options.Models.Slots[ModelSlots.Generator] = new SlotSettings { Model = "fake-gen" };
        options.Models.Slots[ModelSlots.TextEmbedder] = new SlotSettings { Model = "fake-embed" };
        options.Models.Slots[ModelSlots.Extractor] = new SlotSettings { Model = "fake-extract" };
        options.Models.Slots[ModelSlots.Decider] = new SlotSettings { Model = "kev-0.8b" };
        options.Models.Placement = placement;
        var devices = new DeviceSchedulers();
        return (new ModelComposer(catalogue, options, devices, new ModelMetrics(), new TurnFeed()), devices);
    }

    /// <summary>The Starling provider with every model reported ready, so the composer reaches the (stubbed) load.</summary>
    private sealed class ReadyStarling(StarlingModelProvider inner) : IModelProvider
    {
        public string Name => inner.Name;

        public IReadOnlyList<ModelDescriptor> Models => inner.Models;

        public ModelAvailability Check(string model, bool thorough = false) =>
            new(Models.Single(item => item.Name == model), ModelReadiness.Ready, "ready");

        public ModelBinding Load(string model, ModelLoadContext context) => inner.Load(model, context);

        public Task<ModelAvailability> DownloadAsync(string model, IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
