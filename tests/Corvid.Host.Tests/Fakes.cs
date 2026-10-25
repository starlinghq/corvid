using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Corvid.Abstractions.Extraction;
using Corvid.Abstractions.Fabric;
using Corvid.Abstractions.Tokens;
using Corvid.Host;
using Corvid.Host.Composition;
using Corvid.Host.Models;
using Corvid.Models;
using Corvid.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

// Shared with Corvid.Cli.Tests (linked), so both drive the host with the same fake slots.
namespace Corvid.Host.Tests;

/// <summary>
/// The generator slot. Turn calls follow a script, one step per call (after it ends, plain text). The degraded decider's
/// yes/no questions, recognised by its system prompt, are answered by <see cref="YesNo"/> and never consume the script.
/// Streaming splits each text answer into words, so a watching surface sees deltas.
/// </summary>
internal sealed class FakeGenerator(params Func<IReadOnlyList<ChatMessage>, ChatResponse>[] script) : IChatClient
{
    private int _next;

    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    /// <summary>The tools offered with each turn call, in the order of <see cref="Calls"/>.</summary>
    public List<IReadOnlyList<string>> OfferedTools { get; } = [];

    public int StreamedCalls;

    public Func<string, string> YesNo { get; set; } = _ => "no";

    public ITokenCounter? Counter { get; init; }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        if (list[0].Text.StartsWith("You answer one question", StringComparison.Ordinal))
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, YesNo(list[^1].Text))));
        cancellationToken.ThrowIfCancellationRequested();
        lock (Calls)
        {
            Calls.Add(list);
            OfferedTools.Add([.. options?.Tools?.Select(tool => tool.Name) ?? []]);
        }
        var step = Interlocked.Increment(ref _next) - 1;
        var response = step < script.Length ? script[step](list) : Reply.Text("Done.");
        response.Usage = new UsageDetails { InputTokenCount = 42, OutputTokenCount = 7 };
        response.ModelId = "fake-gen";
        return Hold?.Invoke(step, cancellationToken) is { } hold ? HeldAsync(hold, response) : Task.FromResult(response);
    }

    /// <summary>Called with each script step's index (from 0) and the call's token; a task it returns is awaited before the answer.</summary>
    public Func<int, CancellationToken, Task?>? Hold { get; set; }

    private static async Task<ChatResponse> HeldAsync(Task hold, ChatResponse response)
    {
        await hold;
        return response;
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref StreamedCalls);
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var message in response.Messages)
        {
            foreach (var content in message.Contents)
            {
                if (content is TextContent text)
                    foreach (var word in SplitWords(text.Text))
                        yield return new ChatResponseUpdate(ChatRole.Assistant, word) { MessageId = "m1", ModelId = response.ModelId };
                else
                    yield return new ChatResponseUpdate(ChatRole.Assistant, [content]) { MessageId = "m1", ModelId = response.ModelId };
            }
        }
        yield return new ChatResponseUpdate(ChatRole.Assistant, [new UsageContent(response.Usage!)]) { MessageId = "m1", FinishReason = ChatFinishReason.Stop };
    }

    private static IEnumerable<string> SplitWords(string text)
    {
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
            if (i == text.Length || text[i] == ' ')
            {
                yield return text[start..i];
                start = i;
            }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ITokenCounter) ? Counter : serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

internal static class Reply
{
    private static int s_ids;

    public static ChatResponse Text(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    public static Func<IReadOnlyList<ChatMessage>, ChatResponse> Say(string text) => _ => Text(text);

    public static Func<IReadOnlyList<ChatMessage>, ChatResponse> Call(string tool, string arguments = "{}") =>
        _ => new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new FunctionCallContent($"c{Interlocked.Increment(ref s_ids)}", tool,
                JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments)!.ToDictionary(pair => pair.Key, pair => (object?)pair.Value)),
        ]));
}

/// <summary>Embeds by keyword: one axis per keyword the text contains, plus a constant axis so no vector is zero.</summary>
internal sealed class KeywordEmbedder(params string[] keywords) : IEmbeddingGenerator<string, Embedding<float>>
{
    public int Calls;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new GeneratedEmbeddings<Embedding<float>>(values.Select(text => new Embedding<float>(Embed(text)))));
    }

    private float[] Embed(string text)
    {
        var vector = new float[keywords.Length + 1];
        for (var i = 0; i < keywords.Length; i++)
            vector[i] = text.Contains(keywords[i], StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        vector[^1] = 0.1f;
        return vector;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class EmptyExtractor : IExtractor
{
    public int Calls;

    public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref Calls);
        return Task.FromResult(new ExtractionResult([]));
    }
}

/// <summary>A hand-written tool with the risk shape a test needs.</summary>
internal sealed class TestTool(ToolDescriptor descriptor, Func<AIFunctionArguments, object?> invoke) : ToolFunction(descriptor)
{
    public int Invocations;

    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref Invocations);
        return ValueTask.FromResult(invoke(arguments));
    }
}

/// <summary>Notes tools, registered the way C-012's tools will be: an <see cref="IToolProvider"/> in DI.</summary>
internal sealed class NoteTools : IToolProvider
{
    public List<string> Notes { get; } = ["buy milk", "call Ada"];

    public TestTool List { get; }

    public TestTool Add { get; }

    public NoteTools()
    {
        List = new TestTool(new ToolDescriptor("list_notes", "List the user's notes.",
            """{"type":"object","properties":{}}""", new ToolRisk(SideEffects.None, Network: false, Destructive: false), []),
            _ => string.Join("; ", Notes));
        Add = new TestTool(new ToolDescriptor("add_note", "Add a note to the user's notes.",
            """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}""",
            new ToolRisk(SideEffects.Remote, Network: false, Destructive: false), [new ToolParameter("text", "the note", true, false)]),
            arguments =>
            {
                Notes.Add(arguments["text"]?.ToString() ?? "");
                return "added";
            });
    }

    public IReadOnlyList<ToolFunction> CreateTools() => [List, Add];
}

/// <summary>An in-memory model provider: models are ready or missing, and loading one counts.</summary>
internal sealed class FakeModelProvider : IModelProvider
{
    private readonly Dictionary<string, (ModelDescriptor Model, Func<object> Create)> _models = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);

    public string Name => "fake";

    public List<string> Loaded { get; } = [];

    public IReadOnlyList<ModelDescriptor> Models => [.. _models.Values.Select(entry => entry.Model)];

    public FakeModelProvider Add(string name, string[] slots, Func<object> create, int context = 8_192,
        ToolCallSupport tools = ToolCallSupport.TemplateRendered, bool ready = true, PrivacyClass privacy = PrivacyClass.Local)
    {
        var profile = new ModelProfile(name + "@cpu", context, context, tools, StructuredOutputSupport.None, PromptCachingStyle.None,
            ModelCost.Free, privacy, privacy == PrivacyClass.Local ? "cpu" : "remote");
        _models[name] = (new ModelDescriptor(name, [.. slots], profile, Name, 1L << 20), create);
        if (!ready)
            _missing.Add(name);
        return this;
    }

    public ModelAvailability Check(string model, bool thorough = false) =>
        _missing.Contains(model)
            ? new ModelAvailability(_models[model].Model, ModelReadiness.Missing, $"Model '{model}' is not in the fake cache.", $"corvid model download {model}")
            : new ModelAvailability(_models[model].Model, ModelReadiness.Ready, $"Model '{model}' is ready.");

    public ModelBinding Load(string model, ModelLoadContext context)
    {
        lock (Loaded)
            Loaded.Add(model);
        if (_missing.Contains(model))
            throw new ModelUnavailableException(Check(model));
        var (descriptor, create) = _models[model];
        return new ModelBinding(descriptor.Profile, create());
    }

    public Task<ModelAvailability> DownloadAsync(string model, IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new ModelDownloadProgress("model.onnx", 0, 1L << 20));
        _missing.Remove(model);
        return Task.FromResult(Check(model));
    }
}

/// <summary>
/// A calibrated decider standing in for Kev: each question gets the probabilities <see cref="Answer"/> gives, in option
/// order (default: uniform, so confidence 0 and every question below its floor), and every request is recorded.
/// </summary>
internal sealed class FakeKev(string name) : IDecider
{
    public string Name => name;

    public Func<string, DecisionQuestion, double[]?> Answer { get; set; } =
        (_, question) => [.. question.Options.Select(_ => 1.0 / question.Options.Length)];

    public List<DecisionRequest> Requests { get; } = [];

    public ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        lock (Requests)
            Requests.Add(request);
        var answers = ImmutableDictionary.CreateBuilder<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (key, question) in request.Questions)
            if (Answer(key, question) is { } probabilities)
                answers[key] = DecisionAnswer.For(question, probabilities);
        return ValueTask.FromResult<DeciderResult>(new DeciderAnswers(name, answers.ToImmutable()));
    }
}

/// <summary>Fake slots: a scripted generator (and a larger one to switch to), a keyword embedder, an extractor and two fake Kevs.</summary>
internal sealed class FakeModels
{
    public static readonly string[] Keywords = ["list", "note", "add", "weather", "fetch", "page", "send"];

    public FakeModels(FakeGenerator? generator = null, bool generatorReady = true, bool kevReady = true, bool kevLargeReady = true)
    {
        Generator = generator ?? new FakeGenerator(Reply.Say("Hello from the fake generator."));
        Provider = new FakeModelProvider()
            .Add("fake-gen", [ModelSlots.Generator, ModelSlots.Summarizer], () => Generator, ready: generatorReady)
            .Add("fake-gen-large", [ModelSlots.Generator], () => Larger, context: 32_768, tools: ToolCallSupport.Native)
            .Add("fake-embed", [ModelSlots.TextEmbedder], () => Embedder, context: 512, tools: ToolCallSupport.None)
            .Add("fake-embed-alt", [ModelSlots.TextEmbedder], () => new KeywordEmbedder(Keywords), context: 512, tools: ToolCallSupport.None)
            .Add("fake-extract", [ModelSlots.Extractor], () => Extractor, context: 512, tools: ToolCallSupport.None)
            .Add("fake-kev", [ModelSlots.Decider], () => Kev, tools: ToolCallSupport.None, ready: kevReady)
            .Add("fake-kev-large", [ModelSlots.Decider], () => KevLarge, tools: ToolCallSupport.None, ready: kevLargeReady);
    }

    public FakeKev Kev { get; } = new("fake-kev");

    public FakeKev KevLarge { get; } = new("fake-kev-large");

    public FakeGenerator Generator { get; }

    public FakeGenerator Larger { get; } = new(Reply.Say("Hello from the larger model."));

    public KeywordEmbedder Embedder { get; } = new(Keywords);

    public EmptyExtractor Extractor { get; } = new();

    public FakeModelProvider Provider { get; }

    public NoteTools Tools { get; } = new();

    /// <summary>The host's settings for these models, a temporary data directory and rules file.</summary>
    public static Dictionary<string, string?> Settings(string root) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Corvid:DataDirectory"] = Path.Combine(root, "data"),
        ["Corvid:Workspace"] = Path.Combine(root, "work"),
        // Never the user's own skills directory.
        ["Corvid:Skills:Directory"] = Path.Combine(root, "skills"),
        ["Corvid:Approvals:RulesFile"] = Path.Combine(root, "approvals.json"),
        ["Corvid:Models:Slots:generator:Model"] = "fake-gen",
        ["Corvid:Models:Slots:generator:Escalation:0"] = "fake-gen-large",
        ["Corvid:Models:Slots:embedder.text:Model"] = "fake-embed",
        ["Corvid:Models:Slots:embedder.text:Fallbacks:0"] = "fake-embed-alt",
        ["Corvid:Models:Slots:extractor:Model"] = "fake-extract",
        // The degraded tier, as before Kev: tests of the calibrated decider bind fake-kev (DeciderTests).
        ["Corvid:Models:Slots:decider:Model"] = "",
        // The fake scripts would be consumed by tools and jobs they do not expect; tests that want them turn them on.
        ["Corvid:Tools:Builtin"] = "false",
        // A5: delegate_task too; its tests turn it on.
        ["Corvid:Tools:Delegate"] = "false",
        ["Corvid:Scheduler:Enabled"] = "false",
        // G-6: titles and recaps would take the generator's script too; tests of them turn them on.
        ["Corvid:Sessions:Titles"] = "false",
        ["Corvid:Sessions:RecapAfterMinutes"] = "0",
    };

    /// <summary>Replaces the model providers (Starling by default) with these fakes and registers the note tools.</summary>
    public void Register(IServiceCollection services)
    {
        services.RemoveAll<IModelProvider>();
        services.AddSingleton<IModelProvider>(Provider);
        services.AddSingleton<IToolProvider>(Tools);
    }
}

/// <summary>A host on fake models in a temporary directory.</summary>
internal sealed class TestHost : IAsyncDisposable
{
    private TestHost(string root, FakeModels models, IHost host)
    {
        Root = root;
        Models = models;
        Host = host;
    }

    public string Root { get; }

    public FakeModels Models { get; }

    public IHost Host { get; }

    public IServiceProvider Services => Host.Services;

    public static TestHost Create(FakeModels? models = null, IReadOnlyDictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "corvid-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "work"));
        models ??= new FakeModels();
        var overrides = FakeModels.Settings(root);
        // "{root}" in a value is the temporary directory, for settings that place files in it.
        foreach (var (key, value) in settings ?? ImmutableDictionary<string, string?>.Empty)
            overrides[key] = value?.Replace("{root}", root, StringComparison.Ordinal);
        var builder = CorvidHost.CreateBuilder(overrides, Path.Combine(root, "corvid.json"));
        models.Register(builder.Services);
        services?.Invoke(builder.Services);
        return new TestHost(root, models, builder.Build());
    }

    public async Task<CorvidRuntime> StartAsync()
    {
        await Host.StartAsync(TestContext.Current.CancellationToken);
        return await Services.GetRequiredService<CorvidRuntimeFactory>().StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync(CancellationToken.None);
        Host.Dispose();
        // Background work the test started (a skills commit, a job run, the log's last write) can still be finishing
        // under Root: a cleanup that races it is retried rather than failing a test that passed.
        for (var attempt = 1; Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(100 * attempt, CancellationToken.None);
            }
            catch (IOException)
            {
                return;
            }
        }
    }
}
