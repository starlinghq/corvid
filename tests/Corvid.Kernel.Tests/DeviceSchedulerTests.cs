using System.Collections.Concurrent;
using Corvid.Abstractions.Extraction;
using Corvid.Kernel.Scheduling;
using Microsoft.Extensions.AI;

namespace Corvid.Kernel.Tests;

public sealed class DeviceSchedulerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Waiting_work_runs_by_priority_interactive_first()
    {
        using var device = new DeviceScheduler("cpu");
        var gate = new ManualResetEventSlim();
        var order = new ConcurrentQueue<string>();
        var blocker = device.RunAsync(DevicePriority.Background, _ => gate.Wait(Ct), Ct);
        await WaitUntil(() => device.QueueDepth(DevicePriority.Background) == 0);

        var work = new[]
        {
            device.RunAsync(DevicePriority.Background, _ => order.Enqueue("background"), Ct),
            device.RunAsync(DevicePriority.Subagent, _ => order.Enqueue("subagent"), Ct),
            device.RunAsync(DevicePriority.Fabric, _ => order.Enqueue("fabric"), Ct),
            device.RunAsync(DevicePriority.Interactive, _ => order.Enqueue("interactive"), Ct),
        };
        Assert.Equal(1, device.QueueDepth(DevicePriority.Interactive));
        gate.Set();
        await Task.WhenAll([blocker, .. work]);

        Assert.Equal(["interactive", "fabric", "subagent", "background"], order);
    }

    [Fact]
    public async Task Work_runs_on_the_device_thread_and_the_caller_resumes_elsewhere()
    {
        using var device = new DeviceScheduler("cpu");

        var ran = await device.RunAsync(DevicePriority.Interactive, _ => (Thread.CurrentThread.Name, device.IsCurrentThread), Ct);

        Assert.Equal(("corvid-device cpu #0", true), ran);
        Assert.False(device.IsCurrentThread);
    }

    [Fact]
    public async Task Work_cancelled_while_queued_never_runs()
    {
        using var device = new DeviceScheduler("cpu");
        var gate = new ManualResetEventSlim();
        var blocker = device.RunAsync(DevicePriority.Interactive, _ => gate.Wait(Ct), Ct);
        using var cancel = new CancellationTokenSource();
        var ran = false;
        var queued = device.RunAsync(DevicePriority.Background, _ => ran = true, cancel.Token);

        await cancel.CancelAsync();
        gate.Set();
        await blocker;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.False(ran);
    }

    [Fact]
    public async Task Failures_reach_the_caller()
    {
        using var device = new DeviceScheduler("cpu");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            device.RunAsync<int>(DevicePriority.Fabric, _ => throw new InvalidOperationException("native failure"), Ct));

        Assert.Equal("native failure", failure.Message);
    }

    [Fact]
    public async Task Its_task_scheduler_runs_tasks_on_the_device_and_awaiters_do_not_inline_there()
    {
        // The shape Gliner2Extractor uses: Task.Factory.StartNew on the scheduler it was given (C-010).
        using var device = new DeviceScheduler("cpu");
        var scheduler = device.GetTaskScheduler(DevicePriority.Fabric);

        var onDevice = await Task.Factory.StartNew(() => device.IsCurrentThread, Ct, TaskCreationOptions.DenyChildAttach, scheduler);
        var resumedOnDevice = device.IsCurrentThread;

        Assert.True(onDevice);
        Assert.False(resumedOnDevice);
    }

    [Fact]
    public async Task Scheduled_services_start_their_calls_on_the_device()
    {
        using var device = new DeviceScheduler("cuda:0");
        var chat = new ThreadRecordingChatClient(device);
        var embedder = new ThreadRecordingEmbedder(device);
        var extractor = new ThreadRecordingExtractor(device);

        var response = await chat.Schedule(device, DevicePriority.Interactive).GetResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct);
        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in chat.Schedule(device, DevicePriority.Interactive).GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hi")], cancellationToken: Ct))
            updates.Add(update);
        await embedder.Schedule(device, DevicePriority.Fabric).GenerateAsync(["text"], cancellationToken: Ct);
        await extractor.Schedule(device, DevicePriority.Fabric).ExtractAsync(new ExtractionRequest("text", ["person"]), Ct);

        Assert.Equal("on device", response.Text);
        Assert.Equal(2, updates.Count);
        Assert.Equal([true, true, true], chat.OnDevice);
        Assert.True(embedder.OnDevice);
        Assert.True(extractor.OnDevice);
    }

    [Fact]
    public async Task Dispose_cancels_queued_work_and_refuses_more()
    {
        var device = new DeviceScheduler("cpu");
        var gate = new ManualResetEventSlim();
        var running = new ManualResetEventSlim();
        var blocker = device.RunAsync(DevicePriority.Interactive, _ =>
        {
            running.Set();
            return gate.Wait(TimeSpan.FromSeconds(30));
        }, Ct);
        var queued = device.RunAsync(DevicePriority.Background, _ => 1, Ct);
        // Running work finishes on dispose; only work still queued is cancelled.
        Assert.True(running.Wait(TimeSpan.FromSeconds(30), Ct));

        var disposing = Task.Run(device.Dispose, Ct);
        // Dispose joins the busy thread, so release it only once the device has closed.
        SpinWait.SpinUntil(() =>
        {
            try
            {
                device.RunAsync(DevicePriority.Interactive, _ => 1, Ct);
                return false;
            }
            catch (ObjectDisposedException)
            {
                return true;
            }
        }, TimeSpan.FromSeconds(30));
        gate.Set();
        await disposing.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.True(blocker.IsCompletedSuccessfully);
        Assert.True(queued.IsCanceled);
        Assert.Throws<ObjectDisposedException>(() => { _ = device.RunAsync(DevicePriority.Interactive, _ => 1, Ct); });
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
            await Task.Delay(10, Ct);
        Assert.True(condition());
    }

    private sealed class ThreadRecordingChatClient(DeviceScheduler device) : IChatClient
    {
        public List<bool> OnDevice { get; } = [];

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            OnDevice.Add(device.IsCurrentThread);
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "on device")));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            // Starling decodes synchronously: each step runs where MoveNext is called.
            for (var i = 0; i < 2; i++)
            {
                if (i == 1)
                    OnDevice.Add(device.IsCurrentThread);
                yield return new ChatResponseUpdate(ChatRole.Assistant, "token");
            }
            OnDevice.Add(device.IsCurrentThread);
            await Task.CompletedTask;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ThreadRecordingEmbedder(DeviceScheduler device) : IEmbeddingGenerator<string, Embedding<float>>
    {
        public bool OnDevice { get; private set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            OnDevice = device.IsCurrentThread;
            return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new float[] { 1 }))));
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ThreadRecordingExtractor(DeviceScheduler device) : IExtractor
    {
        public bool OnDevice { get; private set; }

        public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
        {
            OnDevice = device.IsCurrentThread;
            return Task.FromResult(new ExtractionResult([]));
        }
    }
}
