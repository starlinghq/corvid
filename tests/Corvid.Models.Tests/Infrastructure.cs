using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Tests;

/// <summary>An event database in its own temporary directory, removed with its WAL files on dispose.</summary>
internal sealed class TempLog : IDisposable
{
    public TempLog()
    {
        Root = Path.Combine(Path.GetTempPath(), "corvid-models-" + Guid.NewGuid().ToString("N"));
        Log = new SqliteEventLog(Path.Combine(Root, "events.db"));
    }

    public string Root { get; }

    public SqliteEventLog Log { get; }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }
}

/// <summary>Answers with its own name, or fails with the queued errors first. Records every call.</summary>
internal sealed class FakeChatClient(string name) : IChatClient
{
    private readonly Queue<Exception> _failures = new();

    public string Name { get; } = name;

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    /// <summary>When set, the next call waits for it, so a test can act while a request is in flight.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public FakeChatClient FailWith(params Exception[] failures)
    {
        foreach (var failure in failures)
            _failures.Enqueue(failure);
        return this;
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Calls.Add(messages.ToList());
        if (Gate is { } gate)
            await gate.Task.WaitAsync(cancellationToken);
        if (_failures.TryDequeue(out var failure))
            throw failure;
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, Name));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Calls.Add(messages.ToList());
        await Task.Yield();
        if (_failures.TryDequeue(out var failure))
            throw failure;
        yield return new ChatResponseUpdate(ChatRole.Assistant, Name);
        yield return new ChatResponseUpdate(ChatRole.Assistant, "!");
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

internal sealed class FakeEmbedder : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(_ => new Embedding<float>(new float[] { 1, 0 }))));

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>Records every hook call and answers with a fixed verdict.</summary>
internal sealed class RecordingHooks(bool allowCloud) : IBudgetPolicy, IPrivacyGate
{
    public List<BudgetChange> BudgetChanges { get; } = [];

    public List<PrivacyCheck> PrivacyChecks { get; } = [];

    public async ValueTask<BudgetAction> OnProfileChangedAsync(BudgetChange change, CancellationToken cancellationToken)
    {
        BudgetChanges.Add(change);
        return await AcceptedContextBudgetPolicy.Instance.OnProfileChangedAsync(change, cancellationToken);
    }

    public ValueTask<PrivacyVerdict> CheckAsync(PrivacyCheck check, CancellationToken cancellationToken)
    {
        PrivacyChecks.Add(check);
        return ValueTask.FromResult(allowCloud ? PrivacyVerdict.Allow() : PrivacyVerdict.Refuse("contains PII"));
    }

    public ModelSwitchOptions Options => new() { Budget = this, Privacy = this };
}

/// <summary>Lets a test append from "another writer" just before the next append, to force a head conflict.</summary>
internal sealed class InterferingLog(IEventLog inner) : IEventLog
{
    public Func<SessionEvent>? BeforeNextAppend { get; set; }

    public long GetHead(string sessionId, CancellationToken cancellationToken = default) => inner.GetHead(sessionId, cancellationToken);

    public IReadOnlyList<StoredEvent> Append(string sessionId, long expectedHead, IReadOnlyList<EventData> events,
        CancellationToken cancellationToken = default)
    {
        if (BeforeNextAppend is { } interfere)
        {
            BeforeNextAppend = null;
            var head = inner.GetHead(sessionId, cancellationToken);
            inner.Append(sessionId, head, [EventData.Create(interfere())], cancellationToken);
        }
        return inner.Append(sessionId, expectedHead, events, cancellationToken);
    }

    public IReadOnlyList<StoredEvent> Read(string sessionId, long afterSequence = 0, long throughSequence = long.MaxValue,
        int limit = 256, CancellationToken cancellationToken = default) =>
        inner.Read(sessionId, afterSequence, throughSequence, limit, cancellationToken);
}

internal static class Profiles
{
    public static ModelProfile Local(string id, int accepted = 8_000) =>
        new(id, 32_768, accepted, ToolCallSupport.TemplateRendered, StructuredOutputSupport.Grammar, PromptCachingStyle.Automatic,
            ModelCost.Free, PrivacyClass.Local, "cuda:0");

    public static ModelProfile LocalServer(string id, int accepted = 32_000) =>
        new(id, 65_536, accepted, ToolCallSupport.Native, StructuredOutputSupport.JsonSchema, PromptCachingStyle.Automatic,
            ModelCost.Free, PrivacyClass.LocalServer, "http://127.0.0.1:8080");

    public static ModelProfile Cloud(string id, int accepted = 180_000) =>
        new(id, 200_000, accepted, ToolCallSupport.Native, StructuredOutputSupport.JsonSchema, PromptCachingStyle.Breakpoints,
            new ModelCost(3m, 15m, 0.3m), PrivacyClass.Cloud, "remote");

    public static ModelBinding Chat(ModelProfile profile) => new(profile, new FakeChatClient(profile.Id));

    public static FakeChatClient Client(this ModelBinding binding) => (FakeChatClient)binding.Service;

    public static HttpRequestException Status(HttpStatusCode code) => new($"HTTP {(int)code}", null, code);

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
