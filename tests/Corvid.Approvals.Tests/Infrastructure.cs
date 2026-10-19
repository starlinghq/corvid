using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using Corvid.Kernel.Events;
using Corvid.Tools;
using Microsoft.Extensions.AI;

namespace Corvid.Approvals.Tests;

/// <summary>An event database in its own temporary directory, removed with its WAL files on dispose.</summary>
internal sealed class TempLog : IDisposable
{
    public TempLog()
    {
        Root = Path.Combine(Path.GetTempPath(), "corvid-approvals-" + Guid.NewGuid().ToString("N"));
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

/// <summary>The degraded tier must never need a model for <c>risk</c>: it is a gate. These fail the test if called.</summary>
internal sealed class UnusedChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new InvalidOperationException("The generator was called.");

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.Yield();
        throw new InvalidOperationException("The generator was called.");
#pragma warning disable CS0162 // An async iterator needs a yield.
        yield break;
#pragma warning restore CS0162
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class UnusedEmbedder : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The embedder was called.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>A calibrated decider that answers <c>risk</c> with one level at the given probability.</summary>
internal sealed class FixedRiskDecider(int level, double probability = 0.98) : IDecider
{
    public List<DecisionRequest> Requests { get; } = [];

    public string Name => "fake-kev";

    public ValueTask<DeciderResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        var answers = request.Questions.ToImmutableDictionary(pair => pair.Key, pair =>
        {
            var count = pair.Value.Options.Length;
            var rest = (1 - probability) / (count - 1);
            return DecisionAnswer.For(pair.Value, Enumerable.Range(0, count).Select(i => i == level ? probability : rest));
        }, StringComparer.Ordinal);
        return ValueTask.FromResult<DeciderResult>(new DeciderAnswers(Name, answers));
    }
}

/// <summary>Answers prompts from a script, records them, or never answers.</summary>
internal sealed class ScriptedPrompter(Func<ApprovalPrompt, ApprovalAnswer?> answer) : IApprovalPrompter
{
    public List<ApprovalPrompt> Prompts { get; } = [];

    public static ScriptedPrompter Always(ApprovalAnswer answer) => new(_ => answer);

    /// <summary>Waits until cancelled: an unanswered prompt.</summary>
    public static ScriptedPrompter Silent() => new(_ => null);

    public async ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
    {
        Prompts.Add(prompt);
        if (answer(prompt) is { } given)
            return given;
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new InvalidOperationException("unreachable");
    }
}

internal static class Fixtures
{
    public const string Home = "/home/user";
    public const string Workspace = "/home/user/work/project";
    public const string VaultData = "/home/user/.local/share/corvid-vault";
    public const string VaultConfig = "/home/user/.config/corvid-vault";
    public const string VaultRuntime = "/run/user/1000/corvid-vault";
    public const string Session = "s-1";

    public static ApprovalEnvironment Environment { get; } =
        ApprovalEnvironment.Create(Workspace, Home, runtimeDirectory: "/run/user/1000");

    public static readonly ApprovalMode[] Modes = [ApprovalMode.Manual, ApprovalMode.Smart, ApprovalMode.Permissive];

    public static TurnState State(string message = "clean up the build output") =>
        new TurnStateBuilder().Build(new TurnStateInput(message, "cli", "qwen3.5-0.8b local, 8k"));

    public static ApprovalContext Context(ApprovalMode mode = ApprovalMode.Smart, bool tainted = false) =>
        new(Session, mode, State()) { Tainted = tainted };

    /// <param name="calibrated">A fake calibrated decider; without one the degraded tier answers (it never answers a gate).</param>
    public static ApprovalPipeline Pipeline(TempLog db, IDecider? calibrated = null, IApprovalPrompter? prompter = null,
        IApprovalRuleStore? always = null, ApprovalOptions? options = null)
    {
        var fabric = new DecisionFabric(DecisionPolicy.Default, new DegradedDecider(new UnusedEmbedder(), new UnusedChatClient()), db.Log,
            calibrated);
        return new ApprovalPipeline(Environment, fabric, db.Log, prompter, always, options);
    }

    public static ActionAnalysis Analyze(string command) => Shell.ShellAnalyzer.Analyze(command, Environment);

    public static ToolDescriptor Tool(string name, SideEffects effects = SideEffects.None, bool network = false, bool destructive = false,
        params string[] parameters) =>
        new(name, $"{name} for tests", """{"type":"object"}""", new ToolRisk(effects, network, destructive),
            [.. parameters.Select(parameter => new ToolParameter(parameter, null, true, false))]);

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;
}
