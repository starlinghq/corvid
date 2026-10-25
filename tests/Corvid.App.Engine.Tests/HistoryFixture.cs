using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Tests;
using Corvid.App.Engine.History;
using Corvid.App.Engine.Review;
using Corvid.App.Engine.Rewind;
using Corvid.App.Engine.Sessions;
using Corvid.Approvals;
using Corvid.Host.Composition;
using Corvid.Host.Tests;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// A started runtime with the built-in tools and checkpoints on, in a temporary directory, for Rewind and Review: turns
/// that write files are recorded as the turn loop records them (the call, its before and after checkpoints, the result),
/// with real checkpoints in the store, so the ledger, the restores and the diffs are the kernel's own.
/// </summary>
internal sealed class HistoryFixture : IAsyncDisposable
{
    private HistoryFixture(TestHost host, CorvidRuntime runtime)
    {
        Host = host;
        Runtime = runtime;
        Engine = AppEngine.ForRuntime(host.Host, runtime);
        Sessions = new EngineSessionService(Engine, new InlineDispatcher(), Time);
        Signals = new HistorySignals(Sessions, new InlineDispatcher());
    }

    public TestHost Host { get; }

    public CorvidRuntime Runtime { get; }

    public AppEngine Engine { get; }

    public ManualTimeProvider Time { get; } = ManualTimeProvider.Demo();

    public EngineSessionService Sessions { get; }

    internal HistorySignals Signals { get; }

    public string Workspace => Runtime.Builtin.Environment.Workspace;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<HistoryFixture> StartAsync(FakeModels? models = null)
    {
        var host = EngineHosts.Create(models, new Dictionary<string, string?> { ["Corvid:Tools:Builtin"] = "true" });
        var runtime = await host.StartAsync();
        Assert.NotNull(runtime.Checkpoints);
        return new HistoryFixture(host, runtime);
    }

    internal EngineRewindService Rewind() => new(Engine, Time, Signals);

    internal EngineReviewService Review(Func<string, string?>? environment = null, Func<System.Diagnostics.ProcessStartInfo, System.Diagnostics.Process?>? start = null) =>
        new(Engine, Sessions, Time, Signals, environment, start);

    public string Session() => Runtime.OpenSession(surface: "app").Id;

    public string PathOf(string relative) => Path.Combine(Workspace, relative);

    public void Write(string relative, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf(relative))!);
        File.WriteAllText(PathOf(relative), text);
    }

    public string Read(string relative) => File.ReadAllText(PathOf(relative));

    public void Append(string sessionId, params SessionEvent[] events) =>
        Runtime.Log.Append(sessionId, Runtime.Log.GetHead(sessionId, Ct), [.. events.Select(EventData.Create)], Ct);

    /// <summary>A user message opening a turn, with no call.</summary>
    public void Say(string sessionId, string text) => Append(sessionId, new UserMessage(text), new AssistantMessage("Noted."));

    /// <summary>One turn as the loop records a write: the message, the call, the ledger's checkpoints around it, the result.</summary>
    public async Task WriteTurnAsync(string sessionId, string callId, string path, string content, string? message = null)
    {
        Append(sessionId, new UserMessage(message ?? $"write {path}"));
        await WriteCallAsync(sessionId, callId, path, content);
        Append(sessionId, new AssistantMessage("Written."));
    }

    /// <summary>One write call inside the current turn.</summary>
    public async Task WriteCallAsync(string sessionId, string callId, string path, string content)
    {
        Append(sessionId, new ToolCallIssued(callId, "write_file", JsonSerializer.SerializeToElement(new { path, content })));
        var analysis = Runtime.Approvals.Analyze(new ShellAction("rm " + path));
        var before = await Runtime.Checkpoints!.BeforeAsync(callId, analysis);
        Append(sessionId, before);
        Write(path, content);
        Append(sessionId, await Runtime.Checkpoints.AfterAsync(callId, before, analysis),
            new ToolResult(callId, JsonSerializer.SerializeToElement(new { path })));
    }

    /// <summary>A turn whose write call a crash cut short: the message, the call, its before checkpoint and the write; no after checkpoint, no result.</summary>
    public async Task CrashedCallAsync(string sessionId, string callId, string path, string content)
    {
        Append(sessionId, new UserMessage($"write {path}"),
            new ToolCallIssued(callId, "write_file", JsonSerializer.SerializeToElement(new { path, content })));
        var analysis = Runtime.Approvals.Analyze(new ShellAction("rm " + path));
        Append(sessionId, await Runtime.Checkpoints!.BeforeAsync(callId, analysis));
        Write(path, content);
    }

    public async ValueTask DisposeAsync()
    {
        Signals.Dispose();
        Sessions.Dispose();
        // The engine stops the runtime it was given.
        await Engine.DisposeAsync();
        await Host.DisposeAsync();
    }
}

/// <summary>A session's id each time an event names it, from whichever thread raises it.</summary>
internal sealed class Raised
{
    private readonly List<string> _ids = [];

    public void Add(object? sender, string id)
    {
        lock (_ids)
            _ids.Add(id);
    }

    public IReadOnlyList<string> Ids
    {
        get
        {
            lock (_ids)
                return [.. _ids];
        }
    }
}
