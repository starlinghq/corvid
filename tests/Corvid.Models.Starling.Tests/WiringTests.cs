using Corvid.Abstractions.Extraction;
using Corvid.Abstractions.Tokens;
using Microsoft.Extensions.AI;
using Starlings.Acquisition;
using Starlings.Models.E5;
using Starlings.Models.E5.Extensions.AI;
using Starlings.Models.Gliner2;
using Starlings.Runtime.Onnx;

namespace Corvid.Models.Starling.Tests;

/// <summary>Adapter wiring with no model loaded: mapping, scheduling, validation and slot binding.</summary>
public sealed class WiringTests
{
    private const string Text = "Apple CEO Tim Cook announced the iPhone 15 in Cupertino.";

    [Fact]
    public async Task Extractor_maps_Starling_entities_to_spans_in_order()
    {
        IReadOnlyList<string>? seenLabels = null;
        float? seenThreshold = null;
        using var extractor = new Gliner2Extractor((text, labels, threshold, _) =>
        {
            seenLabels = labels;
            seenThreshold = threshold;
            return [Entity("person", text, "Tim Cook", 0.98f), Entity("location", text, "Cupertino", 0.91f)];
        }, owned: null, scheduler: null);

        var result = await extractor.ExtractAsync(new ExtractionRequest(Text, ["person", "location"], 0.4f),
            TestContext.Current.CancellationToken);

        Assert.Equal(["person", "location"], seenLabels);
        Assert.Equal(0.4f, seenThreshold);
        Assert.Equal(
            [new ExtractedSpan("person", "Tim Cook", 10, 18, 0.98f), new ExtractedSpan("location", "Cupertino", 46, 55, 0.91f)],
            result.Spans);
        Assert.All(result.Spans, s => Assert.Equal(s.Text, Text[s.Start..s.End]));
    }

    [Fact]
    public async Task Extraction_runs_on_the_given_scheduler_not_the_caller()
    {
        var scheduler = new CountingScheduler();
        var caller = Environment.CurrentManagedThreadId;
        int? ranOn = null;
        using var extractor = new Gliner2Extractor((_, _, _, _) =>
        {
            ranOn = Environment.CurrentManagedThreadId;
            return [];
        }, owned: null, scheduler);

        var result = await extractor.ExtractAsync(new ExtractionRequest(Text, ["person"]), TestContext.Current.CancellationToken);

        Assert.Empty(result.Spans);
        Assert.Equal(1, scheduler.Queued);
        Assert.NotEqual(caller, ranOn);
    }

    [Fact]
    public async Task Extraction_runs_on_the_device_scheduler_at_its_priority()
    {
        using var device = new Corvid.Kernel.Scheduling.DeviceScheduler("cpu");
        var onDevice = false;
        using var extractor = new Gliner2Extractor((_, _, _, _) =>
        {
            onDevice = device.IsCurrentThread;
            return [];
        }, owned: null, device.GetTaskScheduler(Corvid.Kernel.Scheduling.DevicePriority.Fabric));

        await extractor.ExtractAsync(new ExtractionRequest(Text, ["person"]), TestContext.Current.CancellationToken);

        Assert.True(onDevice);
        // The caller resumes off the device, so the turn loop never runs on a model thread.
        Assert.False(device.IsCurrentThread);
    }

    [Fact]
    public async Task Extractor_rejects_bad_requests_and_surfaces_model_refusals()
    {
        using var extractor = new Gliner2Extractor((_, _, _, _) =>
            throw new Gliner2InputTooLongException("Input 0 has 600 tokens; the limit is 512."), owned: null, scheduler: null);
        var token = TestContext.Current.CancellationToken;

        await Assert.ThrowsAsync<ArgumentException>(() => extractor.ExtractAsync(new ExtractionRequest(Text, []), token));
        await Assert.ThrowsAsync<ArgumentException>(() => extractor.ExtractAsync(new ExtractionRequest(Text, default), token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => extractor.ExtractAsync(new ExtractionRequest(Text, ["x"], 1.5f), token));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => extractor.ExtractAsync(new ExtractionRequest(Text, ["x"], float.NaN), token));
        // Too long is an ArgumentException, as the contract states; nothing is truncated.
        await Assert.ThrowsAnyAsync<ArgumentException>(() => extractor.ExtractAsync(new ExtractionRequest(Text, ["x"]), token));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync(new ExtractionRequest(Text, ["x"]), cancelled.Token));
    }

    [Fact]
    public void Extractor_disposes_the_model_it_owns()
    {
        var owned = new Disposable();
        new Gliner2Extractor((_, _, _, _) => [], owned, scheduler: null).Dispose();
        Assert.True(owned.Disposed);
    }

    [Fact]
    public void Adapter_services_bind_to_their_slots_and_nowhere_else()
    {
        using var extractor = new Gliner2Extractor((_, _, _, _) => [], owned: null, scheduler: null);
        using var chat = new TokenCountingChatClient(new EchoChatClient(), new LengthCounter());
        var registry = new ModelSlotRegistry()
            .Bind(ModelSlots.Generator, new ModelBinding(StarlingProfiles.Qwen35, chat))
            .Bind(ModelSlots.Summarizer, new ModelBinding(StarlingProfiles.Qwen35, chat))
            .Bind(ModelSlots.Extractor, new ModelBinding(StarlingProfiles.Gliner2, extractor));

        Assert.Same(extractor, registry.Get(ModelSlots.Extractor).Default.GetService<IExtractor>());
        Assert.Throws<ArgumentException>(() => registry.Bind(ModelSlots.Extractor, new ModelBinding(StarlingProfiles.Qwen35, chat)));
        Assert.Throws<ArgumentException>(() => registry.Bind(ModelSlots.Generator, new ModelBinding(StarlingProfiles.Gliner2, extractor)));
        Assert.Throws<ArgumentException>(() => registry.Bind(ModelSlots.TextEmbedder, new ModelBinding(StarlingProfiles.E5, extractor)));
    }

    [Fact]
    public void The_generator_offers_its_token_counter_and_passes_other_services_through()
    {
        var inner = new EchoChatClient();
        var counter = new LengthCounter();
        using IChatClient client = new TokenCountingChatClient(inner, counter);

        Assert.Same(counter, client.GetService<ITokenCounter>());
        Assert.Same(inner, client.GetService<EchoChatClient>());
        Assert.Null(client.GetService<ITokenCounter>("keyed"));
    }

    [Fact]
    public void Cuda_options_are_refused_before_any_model_loads()
    {
        // CPU natives only: a CUDA request would fail later with a provider error, so it fails here with the reason.
        var snapshot = (ModelSnapshot)null!;
        var gliner = new Gliner2ModelFiles("/nonexistent/model.onnx", "/nonexistent/tokenizer.json", snapshot);
        var e5 = new E5ModelFiles("/nonexistent/model.onnx", "/nonexistent/tokenizer.json", snapshot);
        var cuda = new OnnxSessionOptions { Provider = OnnxExecutionProvider.Cuda };

        Assert.Throws<NotSupportedException>(() => Gliner2Extractor.Create(gliner, new Gliner2ExtractorOptions { Session = cuda }));
        Assert.Throws<NotSupportedException>(() => StarlingBindings.E5Embedder(e5,
            new E5EmbeddingGeneratorOptions { Encoder = new E5EncoderOptions { Session = cuda } }));
    }

    private static Gliner2Entity Entity(string label, string text, string span, float confidence)
    {
        var start = text.IndexOf(span, StringComparison.Ordinal);
        return new Gliner2Entity { Label = label, Text = span, Start = start, End = start + span.Length,
            ScalarStart = start, ScalarEnd = start + span.Length, Confidence = confidence };
    }

    private sealed class CountingScheduler : TaskScheduler
    {
        public int Queued { get; private set; }

        protected override void QueueTask(Task task)
        {
            Queued++;
            new Thread(() => TryExecuteTask(task)) { IsBackground = true }.Start();
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;

        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }

    private sealed class Disposable : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class LengthCounter : ITokenCounter
    {
        public int CountTokens(string text) => text.Length;
    }

    private sealed class EchoChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "echo")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }
}
