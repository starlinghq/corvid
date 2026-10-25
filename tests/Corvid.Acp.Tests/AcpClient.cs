using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Corvid.Host.Commands;
using Corvid.Host.Tests;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Acp.Tests;

/// <summary>
/// An editor on the other end of <c>corvid acp</c>: newline-delimited JSON-RPC over in-memory pipes. Every line in both
/// directions is kept, in order, as the exchange a conformance test compares with its recording. Permission requests are
/// answered by <see cref="Permission"/>.
/// </summary>
internal sealed class AcpClient : IAsyncDisposable
{
    private readonly Pipe _toAgent = new();
    private readonly Pipe _fromAgent = new();
    private readonly Stream _input;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _responses = new();
    private readonly Channel<JsonObject> _orphans = Channel.CreateUnbounded<JsonObject>();
    private readonly TaskCompletionSource<Corvid.Host.Composition.CorvidRuntime> _runtime = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _server;
    private readonly Task _reader;
    private readonly StringWriter _log = new();
    private int _nextId;

    private AcpClient(TestHost host)
    {
        Host = host;
        _input = _toAgent.Writer.AsStream();
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        _server = Task.Run(() => AcpServer.RunAsync(new AcpServerOptions(StartAsync, commands, _toAgent.Reader.AsStream(),
            _fromAgent.Writer.AsStream(), TextWriter.Synchronized(_log))));
        _reader = Task.Run(ReadAsync);
    }

    public TestHost Host { get; }

    /// <summary>The runtime the agent started, for looking at the log.</summary>
    public Task<Corvid.Host.Composition.CorvidRuntime> Runtime => _runtime.Task;

    private async Task<Corvid.Host.Composition.CorvidRuntime> StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var runtime = await Host.StartAsync();
            _runtime.TrySetResult(runtime);
            return runtime;
        }
        catch (Exception exception)
        {
            _runtime.TrySetException(exception);
            throw;
        }
    }

    /// <summary>Every message, as sent: <c>("client", line)</c> or <c>("agent", line)</c>.</summary>
    public List<(string From, JsonObject Message)> Exchange { get; } = [];

    /// <summary>Notifications from the agent, in order.</summary>
    public List<JsonObject> Updates { get; } = [];

    /// <summary>How the editor answers a permission request (default: allow once).</summary>
    public Func<JsonObject, Task<JsonNode>> Permission { get; set; } = _ => Task.FromResult(Selected(PermissionPrompter.Once));

    public List<JsonObject> PermissionRequests { get; } = [];

    public string StandardError => _log.ToString();

    public static AcpClient Start(TestHost host) => new(host);

    public static JsonNode Selected(string option) => new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = option } };

    public static JsonNode Cancelled() => new JsonObject { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } };

    /// <summary>Sends a request and waits for its response (result or error).</summary>
    public async Task<JsonObject> RequestAsync(string method, object parameters, TimeSpan? timeout = null) =>
        await ResponseAsync(await BeginAsync(method, parameters), timeout);

    /// <summary>Sends a request without waiting; <see cref="ResponseAsync"/> waits for it.</summary>
    public async Task<int> BeginAsync(string method, object parameters)
    {
        var id = Interlocked.Increment(ref _nextId);
        _responses[id] = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = JsonSerializer.SerializeToNode(parameters) });
        return id;
    }

    public Task<JsonObject> ResponseAsync(int id, TimeSpan? timeout = null) =>
        _responses[id].Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(60));

    public Task NotifyAsync(string method, object parameters) =>
        SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = JsonSerializer.SerializeToNode(parameters) });

    /// <summary>Writes a raw line, for malformed-input tests.</summary>
    public async Task SendRawAsync(string line)
    {
        await _writing.WaitAsync();
        try
        {
            await _input.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
            await _input.FlushAsync();
        }
        finally
        {
            _writing.Release();
        }
    }

    /// <summary>The next response that answers no request of ours (an error to a malformed message).</summary>
    public async Task<JsonObject> NextErrorWithoutIdAsync()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        return await _orphans.Reader.ReadAsync(cancel.Token);
    }

    /// <summary>The session/update notifications for a session, as their <c>update</c> objects.</summary>
    public List<JsonObject> UpdatesFor(string sessionId)
    {
        lock (Updates)
            return [.. Updates.Where(update => update["method"]?.GetValue<string>() == "session/update" && update["params"]?["sessionId"]?.GetValue<string>() == sessionId)
                .Select(update => update["params"]!["update"]!.AsObject())];
    }

    public async Task<string> NewSessionAsync()
    {
        var response = await RequestAsync("session/new", new { cwd = Workspace, mcpServers = Array.Empty<object>() }, TimeSpan.FromSeconds(120));
        return response["result"]!["sessionId"]!.GetValue<string>();
    }

    public string Workspace => Path.Combine(Host.Root, "work");

    private readonly SemaphoreSlim _writing = new(1, 1);

    private async Task SendAsync(JsonObject message)
    {
        await _writing.WaitAsync();
        try
        {
            lock (Exchange)
                Exchange.Add(("client", (JsonObject)message.DeepClone()));
            await _input.WriteAsync(Encoding.UTF8.GetBytes(message.ToJsonString() + "\n"));
            await _input.FlushAsync();
        }
        finally
        {
            _writing.Release();
        }
    }

    private async Task ReadAsync()
    {
        using var reader = new StreamReader(_fromAgent.Reader.AsStream(), Encoding.UTF8);
        while (await reader.ReadLineAsync() is { } line)
        {
            // The transport: one JSON object per line, nothing else on the agent's output.
            var message = JsonNode.Parse(line)!.AsObject();
            lock (Exchange)
                Exchange.Add(("agent", (JsonObject)message.DeepClone()));
            if (message["method"] is not null)
            {
                if (message["id"] is { } id)
                {
                    lock (PermissionRequests)
                        PermissionRequests.Add(message);
                    // Answered off the reading loop, so an editor that holds a prompt open still reads everything else.
                    var answerId = id.DeepClone();
                    _ = Task.Run(async () =>
                    {
                        var result = await Permission(message);
                        await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = answerId, ["result"] = result });
                    });
                }
                else
                {
                    lock (Updates)
                        Updates.Add(message);
                }
            }
            else if (message["id"] is JsonValue value && value.TryGetValue<int>(out var number) && _responses.TryGetValue(number, out var waiter))
                waiter.TrySetResult(message);
            else
                _orphans.Writer.TryWrite(message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _toAgent.Writer.CompleteAsync();
        await _server.WaitAsync(TimeSpan.FromSeconds(60));
        await _fromAgent.Writer.CompleteAsync();
        await _reader.WaitAsync(TimeSpan.FromSeconds(10));
        await Host.DisposeAsync();
    }
}
