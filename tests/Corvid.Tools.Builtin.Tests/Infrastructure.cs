using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Corvid.Abstractions.Extraction;
using Corvid.Approvals;
using Corvid.Fabric;
using Corvid.Kernel.Events;
using Corvid.Sandbox;
using Microsoft.Extensions.AI;

namespace Corvid.Tools.Builtin.Tests;

/// <summary>
/// A throwaway directory under the test output, never under /tmp: the sandbox gives commands an empty /tmp, which would
/// hide a layout there for the wrong reason.
/// </summary>
internal sealed class TempTree : IDisposable
{
    public TempTree()
    {
        Root = System.IO.Path.Combine(AppContext.BaseDirectory, "scratch", RandomNumberGenerator.GetHexString(12, lowercase: true));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

    public string Write(string relative, string contents)
    {
        var path = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, contents);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the output directory is disposable.
        }
    }
}

/// <summary>Whether this machine can run real sandboxed commands. CI installs bubblewrap and requires these tests to run.</summary>
internal static class Host
{
    private static readonly Lazy<string?> Problem = new(Probe);

    public static string LauncherPath => Path.Combine(AppContext.BaseDirectory, "corvid-sandbox");

    public static void RequireBubblewrap() => Assert.SkipWhen(Problem.Value is not null, Problem.Value ?? "");

    private static string? Probe()
    {
        if (!OperatingSystem.IsLinux())
            return "Linux only.";
        var bwrap = new[] { "/usr/bin/bwrap", "/bin/bwrap", "/usr/local/bin/bwrap" }.FirstOrDefault(File.Exists);
        if (bwrap is null)
            return "bubblewrap is not installed.";
        var start = new ProcessStartInfo(bwrap) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in (string[])["--unshare-all", "--unshare-user", "--disable-userns", "--ro-bind", "/", "/", "--", "/bin/true"])
            start.ArgumentList.Add(argument);
        using var probe = Process.Start(start)!;
        var error = probe.StandardError.ReadToEnd();
        probe.WaitForExit();
        return probe.ExitCode == 0 ? null : $"bubblewrap cannot create namespaces here (exit {probe.ExitCode}): {error.Trim()}";
    }
}

/// <summary>Records what would run in the sandbox instead of running it.</summary>
internal sealed class RecordingSandbox(Func<SandboxCommand, SandboxResult>? result = null) : ISandbox
{
    public List<(SandboxPolicy Policy, SandboxCommand Command)> Runs { get; } = [];

    public Task<SandboxResult> RunAsync(SandboxPolicy policy, SandboxCommand command, CancellationToken cancellationToken = default)
    {
        Runs.Add((policy, command));
        return Task.FromResult(result?.Invoke(command) ?? new SandboxResult { ExitCode = 0, StandardOutput = "ok", StandardError = "" });
    }
}

/// <summary>The degraded tier never needs a model for the gates (risk, memory_op); these fail a test that reaches one.</summary>
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

/// <summary>Finds each configured (label, word) in the text.</summary>
internal sealed class FakeExtractor(params (string Label, string Word)[] vocabulary) : IExtractor
{
    public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
    {
        var spans = ImmutableArray.CreateBuilder<ExtractedSpan>();
        foreach (var label in request.Labels)
            foreach (var (_, word) in vocabulary.Where(entry => entry.Label == label))
            {
                var start = request.Text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
                if (start >= 0)
                    spans.Add(new ExtractedSpan(label, request.Text.Substring(start, word.Length), start, start + word.Length, 0.9f));
            }
        return Task.FromResult(new ExtractionResult(spans.ToImmutable()));
    }
}

internal sealed class Answering(bool approve) : IApprovalPrompter
{
    public List<ApprovalPrompt> Prompts { get; } = [];

    public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
    {
        Prompts.Add(prompt);
        return ValueTask.FromResult(approve ? ApprovalAnswer.Approve() : ApprovalAnswer.Deny());
    }
}

internal static class Fixtures
{
    public const string Session = "s-1";

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TurnState State(string message = "run the check") =>
        new TurnStateBuilder().Build(new TurnStateInput(message, "cli", "qwen3.5-0.8b local, 8k"));

    public static DecisionFabric DegradedFabric(SqliteEventLog log) =>
        new(DecisionPolicy.Default, new DegradedDecider(new UnusedEmbedder(), new UnusedChatClient()), log);

    public static ApprovalPipeline Pipeline(SqliteEventLog log, ApprovalEnvironment environment, IApprovalPrompter? prompter = null) =>
        new(environment, DegradedFabric(log), log, prompter, options: new ApprovalOptions { ShellTools = BuiltinTools.ShellToolMapping });

    /// <summary>The approval for a shell command, the way the turn loop gets it.</summary>
    public static async Task<ToolCallContext> Approved(ApprovalPipeline pipeline, string command, string callId = "call-1")
    {
        var shell = new ShellTool(new SandboxedShell(new RecordingSandbox(), new ShellOptions { Workspace = pipeline.Environment.Workspace }));
        var descriptor = shell.CreateTools().Single().Descriptor;
        var result = await pipeline.AuthorizeAsync(new ApprovalContext(Session, ApprovalMode.Smart, State()) { CallId = callId },
            new ToolAction(descriptor, Json(JsonSerializer.Serialize(new Dictionary<string, string> { ["command"] = command }))), Ct);
        return new ToolCallContext(Session, callId) { Approval = result };
    }

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Arguments as a tool-call parser produces them, with the turn's context attached when given.</summary>
    public static AIFunctionArguments Arguments(string json, ToolCallContext? context = null)
    {
        var arguments = new AIFunctionArguments(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!
            .ToDictionary(pair => pair.Key, pair => (object?)pair.Value));
        return context is null ? arguments : context.AttachTo(arguments);
    }

    public static ToolFunction Tool(IToolProvider provider, string name) => provider.CreateTools().Single(tool => tool.Name == name);

    public static async Task<JsonElement> Invoke(IToolProvider provider, string name, string json, ToolCallContext? context = null)
    {
        var value = await Tool(provider, name).InvokeAsync(Arguments(json, context), Ct);
        return value switch
        {
            System.Text.Json.Nodes.JsonNode node => Json(node.ToJsonString()),
            JsonElement element => element,
            _ => throw new InvalidOperationException($"Unexpected result {value}"),
        };
    }
}
