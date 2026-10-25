using System.Net;
using System.Text.Json.Nodes;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.App.Engine.Models;
using Corvid.Host.Models;
using Corvid.Host.Tests;
using Corvid.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// The Models area over the test host: the fakes' catalogue plus a provider of its own whose download waits to be let go,
/// a local server answering through a fake network, and the host's corvid.json in its temporary directory.
/// </summary>
public sealed class EngineModelTests
{
    private const string Slow = "slow-extract";
    private const string Spare = "spare-gen";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class Setup(EngineFixture fixture, SlowProvider provider, FakeServer server, EngineModelService service) : IAsyncDisposable
    {
        public EngineFixture Fixture { get; } = fixture;

        public SlowProvider Provider { get; } = provider;

        public FakeServer Server { get; } = server;

        public EngineModelService Service { get; } = service;

        public string? Session { get; set; }

        public string ConfigFile => Path.Combine(Fixture.Host.Root, "corvid.json");

        public async Task<JsonNode> FileAsync() => JsonNode.Parse(await File.ReadAllTextAsync(ConfigFile, Token))!;

        public async ValueTask DisposeAsync()
        {
            Service.Dispose();
            await Fixture.DisposeAsync();
        }
    }

    private static async Task<Setup> StartAsync()
    {
        var provider = new SlowProvider();
        var fixture = await EngineFixture.StartAsync(new FakeModels(), new Dictionary<string, string?>
        {
            // No Vault here, whatever runs on this machine.
            ["Corvid:Vault:SocketPath"] = "{root}/no-vault.sock",
        }, services => services.AddSingleton<IModelProvider>(provider));
        var server = new FakeServer();
        Setup? setup = null;
        var service = new EngineModelService(fixture.Engine, new InlineDispatcher(), fixture.Time, () => setup!.Session, server);
        setup = new Setup(fixture, provider, server, service);
        return setup;
    }

    /// <summary>Waits until a change the service announces makes the snapshot pass; never by the clock.</summary>
    private static async Task<ModelsSnapshot> WaitForAsync(EngineModelService service, Func<ModelsSnapshot, bool> condition)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check(object? sender, EventArgs e)
        {
            if (condition(service.Snapshot))
                reached.TrySetResult();
        }

        service.Changed += Check;
        try
        {
            Check(null, EventArgs.Empty);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
            return service.Snapshot;
        }
        finally
        {
            service.Changed -= Check;
        }
    }

    [Fact]
    public async Task TheSnapshotIsTheConfigurationTheCatalogueAndTheRunningSlots()
    {
        await using var setup = await StartAsync();
        Assert.False(setup.Service.Snapshot.IsLoaded);

        var snapshot = await setup.Service.LoadAsync(Token);

        Assert.True(snapshot.IsLoaded);
        Assert.True(snapshot.Validated);
        Assert.Equal(setup.ConfigFile, snapshot.ConfigPath);
        Assert.Equal(ModelSlotNames.All, snapshot.Slots.Select(slot => slot.Name));
        var generator = snapshot.Slot(ModelSlotNames.Generator)!;
        Assert.Equal("fake-gen", generator.Model);
        Assert.Equal(["fake-gen-large"], generator.Escalation);
        // Running on the fakes' CPU, with no call yet to time.
        Assert.Equal(new SlotPlacement("cpu", 0, -1), generator.Placement);
        Assert.Equal("cpu · q0 · no calls yet", ModelText.Placement(generator.Placement!));
        Assert.True(generator.Pinnable);
        Assert.Equal("fake-embed-alt", snapshot.Slot(ModelSlotNames.EmbedderText)!.Fallback);
        // The test host's decider is unbound: the degraded tier.
        Assert.Null(snapshot.Slot(ModelSlotNames.Decider)!.Model);
        Assert.Equal(DeciderTier.Degraded, snapshot.Decider.Tier);
        Assert.True(snapshot.Slot(ModelSlotNames.Decider)!.Shared);

        var gen = snapshot.Find("fake-gen")!;
        Assert.Equal(ModelKind.Local, gen.Kind);
        Assert.Equal(ModelState.Cached, gen.State);
        Assert.True(gen.InDefaultSet);
        Assert.Equal(["generator", "summarizer"], gen.CanServe);
        Assert.Contains("generator escalation 1", snapshot.Find("fake-gen-large")!.Serves, StringComparison.Ordinal);
        var slow = snapshot.Find(Slow)!;
        Assert.Equal(ModelState.Missing, slow.State);
        Assert.False(slow.InDefaultSet);
        Assert.Equal(2000, slow.SizeBytes);

        Assert.False(snapshot.Privacy.VaultPresent);
        Assert.True(snapshot.Privacy.ExtractorBound);
        Assert.Equal("redact", snapshot.Privacy.Posture);
        Assert.NotNull(snapshot.Privacy.TodayUnknown);
        Assert.Contains("1 thread", snapshot.Cpu, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASwitchIsWrittenAndAppliesToNewSessionsAndTheOpenOne()
    {
        await using var setup = await StartAsync();
        var runtime = setup.Fixture.Runtime;
        var session = runtime.OpenSession(surface: "app");
        setup.Session = session.Id;

        var first = await setup.Service.SwitchAsync(ModelSlotNames.Summarizer, "fake-gen", Token);
        Assert.True(first.Written, first.Message);
        var answer = await setup.Service.SwitchAsync(ModelSlotNames.Summarizer, Spare, Token);

        Assert.True(answer.Written, answer.Message);
        Assert.Contains($"switches after its issued tool calls", answer.Message, StringComparison.Ordinal);
        Assert.Equal(Spare, (string?)(await setup.FileAsync())["Corvid"]!["Models"]!["Slots"]!["summarizer"]!["Model"]);
        Assert.Equal($"{Spare}@cpu", runtime.Registry.Get(ModelSlotNames.Summarizer).Default.Profile.Id);
        Assert.Equal($"{Spare}@cpu", session.Slots.Current(ModelSlotNames.Summarizer).Profile.Id);
        Assert.Equal(Spare, setup.Service.Snapshot.Slot(ModelSlotNames.Summarizer)!.Model);
        Assert.Equal($"The summarizer is already {Spare}.", (await setup.Service.SwitchAsync(ModelSlotNames.Summarizer, Spare, Token)).Message);
    }

    [Fact]
    public async Task ASwitchTheRunOverridesOrTheDeciderTakesSaysWhenItApplies()
    {
        await using var setup = await StartAsync();

        // The test host sets the generator for this run, over the file.
        var generator = await setup.Service.SwitchAsync(ModelSlotNames.Generator, Spare, Token);
        Assert.True(generator.Written, generator.Message);
        Assert.Contains("overrides", generator.Message, StringComparison.Ordinal);
        Assert.Contains("New sessions in this window start with it.", generator.Message, StringComparison.Ordinal);

        var decider = await setup.Service.SwitchAsync(ModelSlotNames.Decider, "fake-kev", Token);
        Assert.True(decider.Written, decider.Message);
        Assert.Contains("shared by every session", decider.Message, StringComparison.Ordinal);
        Assert.Null(setup.Fixture.Runtime.Fabric.CalibratedDecider);
    }

    [Fact]
    public async Task ASwitchTheModelCannotServeOrHasNotDownloadedIsRefused()
    {
        await using var setup = await StartAsync();

        Assert.Equal("There is no slot named nonsense.", (await setup.Service.SwitchAsync("nonsense", "fake-gen", Token)).Message);
        Assert.StartsWith("There is no model named", (await setup.Service.SwitchAsync(ModelSlotNames.Generator, "nope", Token)).Message, StringComparison.Ordinal);
        var cannot = await setup.Service.SwitchAsync(ModelSlotNames.Extractor, "fake-gen", Token);
        Assert.False(cannot.Written);
        Assert.Contains("cannot serve as the extractor", cannot.Message, StringComparison.Ordinal);
        var missing = await setup.Service.SwitchAsync(ModelSlotNames.Extractor, Slow, Token);
        Assert.False(missing.Written);
        Assert.Contains("not downloaded", missing.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(setup.ConfigFile));
    }

    [Fact]
    public async Task ALadderIsValidatedAndWrittenWhole()
    {
        await using var setup = await StartAsync();

        var written = await setup.Service.SetLadderAsync(ModelSlotNames.Generator, [Spare, "fake-gen-large"], Token);
        Assert.True(written.Written, written.Message);
        Assert.Contains("next starts", written.Message, StringComparison.Ordinal);
        var ladder = (JsonArray)(await setup.FileAsync())["Corvid"]!["Models"]!["Slots"]!["generator"]!["Escalation"]!;
        Assert.Equal([Spare, "fake-gen-large"], ladder.Select(step => (string?)step));

        Assert.False((await setup.Service.SetLadderAsync(ModelSlotNames.Generator, ["fake-gen"], Token)).Written);
        Assert.False((await setup.Service.SetLadderAsync(ModelSlotNames.Generator, ["fake-embed"], Token)).Written);
        Assert.False((await setup.Service.SetLadderAsync(ModelSlotNames.Generator, [Spare, Spare], Token)).Written);
        // The test host leaves the decider unbound for this run, so it has no ladder to set.
        var unbound = await setup.Service.SetLadderAsync(ModelSlotNames.Decider, ["fake-kev-large"], Token);
        Assert.Equal("The decider is unbound, so it has no ladder.", unbound.Message);

        // An emptied ladder removes the key; the slot keeps its model.
        Assert.True((await setup.Service.SetLadderAsync(ModelSlotNames.Generator, [], Token)).Written);
        Assert.Null((await setup.FileAsync())["Corvid"]!["Models"]!["Slots"]!["generator"]!["Escalation"]);
    }

    [Fact]
    public async Task APinHoldsForTheOpenSession()
    {
        await using var setup = await StartAsync();

        Assert.StartsWith("Open a session first", (await setup.Service.SetPinnedAsync(ModelSlotNames.Generator, true, Token)).Message, StringComparison.Ordinal);
        var session = setup.Fixture.Runtime.OpenSession(surface: "app");
        setup.Session = session.Id;

        var pinned = await setup.Service.SetPinnedAsync(ModelSlotNames.Generator, true, Token);
        Assert.True(pinned.Written, pinned.Message);
        Assert.True(session.Slots.IsPinned(ModelSlotNames.Generator));
        Assert.True(setup.Service.Snapshot.Slot(ModelSlotNames.Generator)!.Pinned);

        Assert.True((await setup.Service.SetPinnedAsync(ModelSlotNames.Generator, false, Token)).Written);
        Assert.False(session.Slots.IsPinned(ModelSlotNames.Generator));
        Assert.False(setup.Service.Snapshot.Slot(ModelSlotNames.Generator)!.Pinned);
    }

    [Fact]
    public async Task PlacementGoesThroughTheValidator()
    {
        await using var setup = await StartAsync();

        var cuda = await setup.Service.SetPlacementAsync("fake-gen", "cuda", Token);
        Assert.False(cuda.Written);
        Assert.Contains("Not written", cuda.Message, StringComparison.Ordinal);
        Assert.False((await setup.Service.SetPlacementAsync("fake-gen", "gpu-ish", Token)).Written);
        Assert.False(File.Exists(setup.ConfigFile));
    }

    [Fact]
    public async Task ADownloadReportsItsProgressAndEndsCached()
    {
        await using var setup = await StartAsync();
        await setup.Service.LoadAsync(Token);
        setup.Provider.BeforeReport = () => setup.Fixture.Time.Advance(1);

        await setup.Service.DownloadAsync([Slow, "fake-gen"], Token);
        var moving = await WaitForAsync(setup.Service, snapshot => snapshot.Find(Slow) is { State: ModelState.Downloading, Download.Bytes: 1500 });

        var progress = moving.Find(Slow)!.Download!;
        Assert.Equal(2000, progress.Total);
        Assert.True(progress.BytesPerSecond > 0);
        Assert.Equal(ModelState.Cached, moving.Find("fake-gen")!.State);
        Assert.Equal($"{Slow} 75%", Assert.Single(ModelsViewModel.StatusDownloads(moving)).Value);

        setup.Provider.Release.TrySetResult();
        var done = await WaitForAsync(setup.Service, snapshot => snapshot.Find(Slow) is { State: ModelState.Cached });
        Assert.Null(done.Find(Slow)!.Download);
        Assert.Equal(0, setup.Provider.PartialsRemoved);
    }

    [Fact]
    public async Task ACancelledDownloadLeavesNoPartialFileAndAQueuedOneIsDropped()
    {
        await using var setup = await StartAsync();
        await setup.Service.LoadAsync(Token);

        await setup.Service.DownloadAsync([Slow, "slow-embed"], Token);
        await WaitForAsync(setup.Service, snapshot => snapshot.Find(Slow) is { State: ModelState.Downloading });
        Assert.Equal(ModelState.Queued, setup.Service.Snapshot.Find("slow-embed")!.State);

        await setup.Service.CancelDownloadAsync("slow-embed", Token);
        Assert.Equal(ModelState.Missing, setup.Service.Snapshot.Find("slow-embed")!.State);
        await setup.Service.CancelDownloadAsync(Slow, Token);

        var snapshot = await WaitForAsync(setup.Service, item => item.Find(Slow) is { State: ModelState.Missing });
        Assert.Null(snapshot.Find(Slow)!.Download);
        Assert.Equal(1, setup.Provider.PartialsRemoved);
        Assert.Equal(1, setup.Provider.Started);
    }

    [Fact]
    public async Task AFailedDownloadSaysWhyOnItsRow()
    {
        await using var setup = await StartAsync();
        await setup.Service.LoadAsync(Token);
        setup.Provider.Failure = "the mirror refused the request";

        await setup.Service.DownloadAsync([Slow], Token);
        setup.Provider.Release.TrySetResult();

        var snapshot = await WaitForAsync(setup.Service, item => item.Find(Slow) is { State: ModelState.Missing, Download: null } model
            && model.Fit.Contains("failed", StringComparison.Ordinal));
        Assert.Contains("the last download failed: the mirror refused the request", snapshot.Find(Slow)!.Fit, StringComparison.Ordinal);
    }

    /// <summary>
    /// A3 review M2: a download that ends over a corvid.json that no longer parses says the list may be out of date, and the
    /// next download still starts (the pump does not stay marked as running).
    /// </summary>
    [Fact]
    public async Task ADownloadEndingOverABrokenConfigurationSaysSoAndTheNextOneStarts()
    {
        await using var setup = await StartAsync();
        await setup.Service.LoadAsync(Token);

        await setup.Service.DownloadAsync([Slow], Token);
        await WaitForAsync(setup.Service, snapshot => snapshot.Find(Slow) is { State: ModelState.Downloading });
        await File.WriteAllTextAsync(setup.ConfigFile, "{ \"Corvid\": ", Token);
        setup.Provider.Release.TrySetResult();

        var stale = await WaitForAsync(setup.Service, snapshot => snapshot.Problem is not null);
        Assert.StartsWith($"{Slow} downloaded, but the models could not be read again", stale.Problem, StringComparison.Ordinal);
        Assert.Null(stale.Find(Slow)!.Download);

        File.Delete(setup.ConfigFile);
        await setup.Service.DownloadAsync(["slow-embed"], Token);
        var done = await WaitForAsync(setup.Service, snapshot => snapshot.Find("slow-embed") is { State: ModelState.Cached });
        Assert.Equal(2, setup.Provider.Started);
        Assert.Null(done.Problem);
        Assert.Equal(ModelState.Cached, done.Find(Slow)!.State);
    }

    /// <summary>A3 review L11: a switch written to the file whose live switch then fails says it was written.</summary>
    [Fact]
    public async Task ALiveSwitchThatFailsAfterTheWriteSaysTheFileWasWritten()
    {
        await using var setup = await StartAsync();
        setup.Provider.LoadFailure = new IOException("the model file vanished");

        var answer = await setup.Service.SwitchAsync(ModelSlotNames.Summarizer, Spare, Token);

        Assert.True(answer.Written, answer.Message);
        Assert.Contains("Written; the live switch failed: the model file vanished", answer.Message, StringComparison.Ordinal);
        Assert.Equal(Spare, (string?)(await setup.FileAsync())["Corvid"]!["Models"]!["Slots"]!["summarizer"]!["Model"]);
    }

    /// <summary>A3 security review L3: a server name, or the model id a server answers with, holding hidden characters is not written.</summary>
    [Fact]
    public async Task AServerWithHiddenCharactersInItsNameOrModelIsNotAdded()
    {
        await using var setup = await StartAsync();
        setup.Server.Models = ["qwen‮coder"];

        var model = await setup.Service.AddServerAsync("coder", "http://127.0.0.1:11434/v1", Token);
        Assert.False(model.Written);
        Assert.Contains("names its model with hidden characters (qwen⟨U+202E⟩coder)", model.Message, StringComparison.Ordinal);

        setup.Server.Models = ["coder"];
        var name = await setup.Service.AddServerAsync("co​der", "http://127.0.0.1:11434/v1", Token);
        Assert.False(name.Written);
        Assert.Contains("hidden characters", name.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(setup.ConfigFile));
    }

    [Fact]
    public async Task AServerThatAnswersIsAddedWithTheModelItServes()
    {
        await using var setup = await StartAsync();
        setup.Server.Models = ["qwen-coder"];

        var added = await setup.Service.AddServerAsync("coder", "http://127.0.0.1:11434/v1", Token);

        Assert.True(added.Written, added.Message);
        Assert.Contains("next starts", added.Message, StringComparison.Ordinal);
        var remote = (await setup.FileAsync())["Corvid"]!["Models"]!["Remote"]!["coder"]!;
        Assert.Equal("openai-compatible", (string?)remote["Provider"]);
        Assert.Equal("qwen-coder", (string?)remote["Model"]);
        Assert.Equal("http://127.0.0.1:11434/v1", (string?)remote["Endpoint"]);
        Assert.Equal("http://127.0.0.1:11434/v1/models", setup.Server.Asked.Last().ToString());
        var row = setup.Service.Snapshot.Find("coder")!;
        Assert.Equal(ModelKind.Server, row.Kind);
        Assert.Equal(ModelState.Reachable, row.State);
        Assert.Contains("after a restart", row.Where, StringComparison.Ordinal);

        var check = await setup.Service.CheckServerAsync("coder", Token);
        Assert.True(check.Reachable, check.Message);
        Assert.Contains("serves qwen-coder", check.Message, StringComparison.Ordinal);
        setup.Server.Down = true;
        var down = await setup.Service.CheckServerAsync("coder", Token);
        Assert.False(down.Reachable);
        Assert.Equal(ModelState.Unreachable, setup.Service.Snapshot.Find("coder")!.State);
    }

    [Fact]
    public async Task AServerIsNotAddedWhenItDoesNotAnswerOrTheValidatorRefusesIt()
    {
        await using var setup = await StartAsync();
        setup.Server.Down = true;

        var down = await setup.Service.AddServerAsync("coder", "http://127.0.0.1:11434/v1", Token);
        Assert.False(down.Written);
        Assert.Contains("Nothing was written", down.Message, StringComparison.Ordinal);

        setup.Server.Down = false;
        setup.Server.Models = ["a", "b"];
        Assert.Contains("serves a, b", (await setup.Service.AddServerAsync("coder", "http://127.0.0.1:11434/v1", Token)).Message, StringComparison.Ordinal);
        setup.Server.Models = ["coder"];
        // A server elsewhere on the internet is a cloud model, which needs a key in Vault.
        var remote = await setup.Service.AddServerAsync("coder", "http://203.0.113.9:8080/v1", Token);
        Assert.False(remote.Written);
        Assert.Contains("not a loopback or private address", remote.Message, StringComparison.Ordinal);
        Assert.False((await setup.Service.AddServerAsync("fake-gen", "http://127.0.0.1:1/v1", Token)).Written);
        Assert.False((await setup.Service.AddServerAsync("two words", "http://127.0.0.1:1/v1", Token)).Written);
        Assert.False(File.Exists(setup.ConfigFile));
    }

    [Fact]
    public async Task ThePagesShowTheEnginesModelsInEngineMode()
    {
        await using var fixture = await EngineFixture.StartAsync();
        using var composition = new AppComposition(new AppOptions { Demo = false }, new InlineDispatcher(), fixture.Time, new EngineAppServices(fixture.Engine, fixture.Host.Root, _ => null));

        var page = composition.Models;
        Assert.IsType<EngineModelService>(composition.ModelService);
        await WaitForAsync((EngineModelService)composition.ModelService, snapshot => snapshot.IsLoaded);

        Assert.False(page.HasNotice, string.Concat(page.Notice.Select(run => run.Text)));
        Assert.Contains(page.Slots, slot => slot.Name == ModelSlotNames.Generator && slot.Model == "fake-gen");
        Assert.Contains(page.Catalogue, row => row.Model.Name == "fake-gen");
    }

    /// <summary>
    /// Two local models to download (an extractor and an embedder nobody configured) whose download reports three steps
    /// and then waits to be let go, and a ready generator to switch to.
    /// </summary>
    internal sealed class SlowProvider : IModelProvider
    {
        private readonly HashSet<string> _ready = new(StringComparer.OrdinalIgnoreCase) { Spare };

        public string Name => "slow";

        public IReadOnlyList<ModelDescriptor> Models { get; } =
        [
            Descriptor(Slow, ModelSlots.Extractor),
            Descriptor("slow-embed", ModelSlots.TextEmbedder),
            Descriptor(Spare, ModelSlots.Generator, ModelSlots.Summarizer),
        ];

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Action? BeforeReport { get; set; }

        public string? Failure { get; set; }

        public int Started;

        public int PartialsRemoved;

        private static ModelDescriptor Descriptor(string name, params string[] slots) =>
            new(name, [.. slots], new ModelProfile(name + "@cpu", 8_192, 8_192, ToolCallSupport.TemplateRendered, StructuredOutputSupport.None,
                PromptCachingStyle.None, ModelCost.Free, PrivacyClass.Local, "cpu"), "slow", 2000);

        public ModelAvailability Check(string model, bool thorough = false)
        {
            var descriptor = Models.Single(item => item.Name == model);
            return _ready.Contains(model)
                ? new ModelAvailability(descriptor, ModelReadiness.Ready, $"{model} is ready.")
                : new ModelAvailability(descriptor, ModelReadiness.Missing, $"{model} is not downloaded.", $"corvid model download {model}");
        }

        public Exception? LoadFailure { get; set; }

        public ModelBinding Load(string model, ModelLoadContext context) => LoadFailure is { } failure
            ? throw failure
            : new(Models.Single(item => item.Name == model).Profile, new FakeGenerator(Reply.Say("Hello from the spare.")));

        public async Task<ModelAvailability> DownloadAsync(string model, IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Started);
            foreach (var (file, bytes, total) in new[] { ("a.onnx", 0L, 1000L), ("a.onnx", 1000L, 1000L), ("b.onnx", 500L, 1000L) })
            {
                BeforeReport?.Invoke();
                progress?.Report(new ModelDownloadProgress(file, bytes, total));
            }

            await Release.Task.WaitAsync(cancellationToken);
            if (Failure is { } failure)
                throw new InvalidOperationException(failure);
            _ready.Add(model);
            return Check(model);
        }

        public int RemovePartialDownloads(string model)
        {
            Interlocked.Increment(ref PartialsRemoved);
            return 1;
        }
    }

    /// <summary>A local server's <c>/models</c>, or none answering.</summary>
    internal sealed class FakeServer : HttpMessageHandler
    {
        public IReadOnlyList<string> Models { get; set; } = [];

        public bool Down { get; set; }

        public List<Uri> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Asked)
                Asked.Add(request.RequestUri!);
            if (Down)
                throw new HttpRequestException("Connection refused (127.0.0.1:11434)");
            var body = new JsonObject { ["data"] = new JsonArray([.. Models.Select(id => (JsonNode)new JsonObject { ["id"] = id })]) };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) });
        }
    }
}
