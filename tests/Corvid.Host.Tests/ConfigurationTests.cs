using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Corvid.Host.Tests;

public sealed class ConfigurationTests
{
    private static readonly ModelCatalogue Catalogue = new([new FakeModels().Provider]);

    private static ValidateOptionsResult Validate(Action<CorvidOptions> change)
    {
        var options = new CorvidOptions();
        options.Models.Slots[ModelSlots.Generator] = new SlotSettings { Model = "fake-gen" };
        options.Models.Slots[ModelSlots.TextEmbedder] = new SlotSettings { Model = "fake-embed" };
        options.Models.Slots[ModelSlots.Extractor] = new SlotSettings { Model = "fake-extract" };
        change(options);
        return new CorvidOptionsValidator(Catalogue).Validate(null, options);
    }

    [Fact]
    public void ValidConfigurationPasses() => Assert.True(Validate(_ => { }).Succeeded);

    [Fact]
    public void TheDefaultsNameStarlingModels()
    {
        var starling = new ModelCatalogue([new StarlingModelProvider(new Corvid.Models.Starling.StarlingModelStore(Path.GetTempPath()))]);
        Assert.True(new CorvidOptionsValidator(starling).Validate(null, new CorvidOptions()).Succeeded);
        Assert.Equal("qwen3.5-0.8b", new CorvidOptions().Models.EffectiveSlots()[ModelSlots.Generator].Model);
        // No decider model until Kev ships: the degraded tier is the decider.
        Assert.False(new CorvidOptions().Models.EffectiveSlots().ContainsKey(ModelSlots.Decider));
    }

    [Theory]
    [InlineData("generatr", "fake-gen", "'generatr' is not a slot")]
    [InlineData(ModelSlots.Generator, "qwen-9000", "no model is named 'qwen-9000'")]
    [InlineData(ModelSlots.Extractor, "fake-gen", "'fake-gen' cannot serve extractor")]
    public void SlotsAndModelsMustExistAndFit(string slot, string model, string expected)
    {
        var result = Validate(options => options.Models.Slots[slot] = new SlotSettings { Model = model });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void TheGeneratorAndTextEmbedderAreRequired()
    {
        var result = Validate(options =>
        {
            options.Models.Slots[ModelSlots.Generator] = new SlotSettings { Model = "" };
            options.Models.Slots[ModelSlots.TextEmbedder] = new SlotSettings { Model = "" };
        });
        Assert.Contains(result.Failures!, failure => failure.Contains("generator must be bound", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("embedder.text must be bound", StringComparison.Ordinal));
    }

    [Fact]
    public void EscalationAndFallbackModelsAreCheckedToo()
    {
        var result = Validate(options =>
        {
            options.Models.Slots[ModelSlots.Generator].Escalation.Add("fake-embed");
            options.Models.Slots[ModelSlots.Generator].Fallbacks.Add("fake-gen");
        });
        Assert.Contains(result.Failures!, failure => failure.Contains("Escalation: 'fake-embed' cannot serve generator", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("cannot also be its own escalation or fallback", StringComparison.Ordinal));
    }

    [Fact]
    public void EveryProblemIsReportedAtOnce()
    {
        var result = Validate(options =>
        {
            options.Devices.CpuThreads = 0;
            options.Approvals.TimeoutSeconds = 0;
            options.Turn.MaxSteps = 0;
            options.Turn.MaxOutputTokens = 1;
            options.Telemetry.OtlpEndpoint = "localhost:4317";
            options.DataDirectory = "relative/data";
            options.Approvals.Mode = (Corvid.Approvals.ApprovalMode)42;
        });
        Assert.Equal(7, result.Failures!.Count());
    }

    [Fact]
    public void AHomeRelativePathIsAccepted() =>
        Assert.True(Validate(options => options.DataDirectory = "~/corvid-data").Succeeded);

    [Fact]
    public async Task TheHostValidatesAtStartAndNothingLoads()
    {
        await using var host = TestHost.Create(settings: new Dictionary<string, string?>
        {
            ["Corvid:Models:Slots:generator:Model"] = "missing-model",
            ["Corvid:Devices:CpuThreads"] = "0",
        });
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains(exception.Failures, failure => failure.Contains("no model is named 'missing-model'", StringComparison.Ordinal));
        Assert.Contains(exception.Failures, failure => failure.Contains("CpuThreads", StringComparison.Ordinal));
        Assert.Empty(host.Models.Provider.Loaded);
    }

    [Fact]
    public async Task OverridesAndTheConfigFileBind()
    {
        var root = Path.Combine(Path.GetTempPath(), "corvid-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "corvid.json");
            await File.WriteAllTextAsync(file, """
                { "Corvid": { "Approvals": { "Mode": "Manual", "TimeoutSeconds": 30 }, "Turn": { "MaxSteps": 3 } } }
                """, TestContext.Current.CancellationToken);
            var builder = CorvidHost.CreateBuilder(new Dictionary<string, string?> { ["Corvid:Turn:MaxSteps"] = "5" }, file);
            using var host = builder.Build();
            var options = host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;
            Assert.Equal(Corvid.Approvals.ApprovalMode.Manual, options.Approvals.Mode);
            Assert.Equal(30, options.Approvals.TimeoutSeconds);
            Assert.Equal(5, options.Turn.MaxSteps);
            Assert.Equal(file, host.Services.GetRequiredService<CorvidConfigFile>().Path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StarlingIsTheDefaultProvider()
    {
        using var host = CorvidHost.CreateBuilder(configFile: Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "corvid.json")).Build();
        var catalogue = host.Services.GetRequiredService<ModelCatalogue>();
        Assert.Equal(["embeddinggemma-2", "gliner2-base-v1", "multilingual-e5-small", "qwen3.5-0.8b"], catalogue.Models.Select(model => model.Name));
        Assert.True(catalogue.Find("qwen3.5-0.8b").Serves(ModelSlots.Summarizer));
        Assert.NotNull(host.Services.GetRequiredService<CorvidRuntimeFactory>());
    }

    [Fact]
    public void PathsFollowXdgAndExpandHome()
    {
        Assert.Equal(Path.Combine(CorvidPaths.Home, "x"), CorvidPaths.ExpandHome("~/x"));
        Assert.Equal("/abs", CorvidPaths.ExpandHome("/abs"));
        var options = new CorvidOptions { DataDirectory = "/data/corvid" };
        Assert.Equal("/data/corvid/events.db", CorvidPaths.EventLog(options));
        Assert.Equal("/data/corvid/memory.db", CorvidPaths.MemoryDatabase(options));
    }
}
