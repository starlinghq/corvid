using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace Corvid.Mcp;

public enum McpServerState
{
    /// <summary>Configured; the background start has not reached it yet.</summary>
    NotStarted,
    Starting,
    Ready,

    /// <summary>The last start failed; calls fail at once until <see cref="McpServerConnection.RetryDelay"/> has passed.</summary>
    Failed,
    Stopped,
}

/// <summary>What <c>/mcp</c> and <c>corvid mcp list</c> show about one server.</summary>
/// <param name="Tools">The Corvid names of the tools registered from this server.</param>
/// <param name="Issues">Tools left out, and why (a schema refused, a name taken, over the limit).</param>
/// <param name="Detail">Why the server failed, with the end of its standard error; or its name and version when ready.</param>
public sealed record McpServerStatus(
    string Name,
    McpTransportKind Transport,
    McpTrust Trust,
    McpServerState State,
    string? Detail,
    ImmutableArray<string> Tools,
    ImmutableArray<string> Issues,
    bool Tasks);

/// <summary>A server could not be reached; nothing was sent.</summary>
public sealed class McpServerUnavailableException(string message) : InvalidOperationException(message)
{
    public McpServerUnavailableException()
        : this("The MCP server is unavailable.")
    {
    }

    public McpServerUnavailableException(string message, Exception innerException)
        : this(message + " " + innerException.Message)
    {
    }
}

/// <summary>Builds the transport for a server: stdio through the sandbox launcher, or streamable HTTP through Vault.</summary>
public interface IMcpTransports
{
    /// <param name="standardError">Receives the server's standard error, line by line, for a stdio server.</param>
    IClientTransport Create(McpServerDefinition server, Action<string> standardError);
}

/// <summary>
/// One configured server: connected lazily, reconnected after the server exits, and bounded in time everywhere, so a server
/// that hangs or crashes costs a turn at most a timeout and never stalls the host (plan §12: lazy, isolated adapters).
/// Sampling, elicitation and roots are not offered: sampling is deprecated in MCP 2026-07-28 and Corvid does not rely on it,
/// and nothing a server asks for reaches the user or a model except through a tool result.
/// </summary>
public sealed class McpServerConnection : IAsyncDisposable
{
    /// <summary>After a failed start, calls fail at once for this long instead of waiting on the server again.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private const int StandardErrorLines = 20;

    private static readonly string Version =
        typeof(McpServerConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0";

    private readonly IMcpTransports _transports;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Queue<string> _standardError = new();
    private McpClient? _client;
    private McpServerState _state = McpServerState.NotStarted;
    private string? _detail;
    private DateTimeOffset _retryAt;
    private ImmutableArray<string> _tools = [];
    private ImmutableArray<string> _issues = [];
    private bool _disposed;

    public McpServerConnection(McpServerDefinition definition, IMcpTransports transports, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(transports);
        var problems = definition.Validate();
        if (problems.Count > 0)
            throw new ArgumentException(string.Join(" ", problems), nameof(definition));
        Definition = definition;
        _transports = transports;
        _time = time ?? TimeProvider.System;
    }

    public McpServerDefinition Definition { get; }

    /// <summary>A live connection exists: the server has started and its connection has not ended.</summary>
    internal bool IsConnected => Volatile.Read(ref _client) is { Completion.IsCompleted: false };

    public McpServerStatus Status
    {
        get
        {
            lock (_lock)
            {
                var tasks = _client is { } client && SupportsTasks(client);
                return new McpServerStatus(Definition.Name, Definition.Transport, Definition.Trust, _state, _detail, _tools, _issues, tasks);
            }
        }
    }

    internal void SetTools(ImmutableArray<string> tools, ImmutableArray<string> issues)
    {
        lock (_lock)
        {
            _tools = tools;
            _issues = issues;
        }
    }

    /// <summary>The server's tools, connecting first if needed; the whole is bounded by the start timeout.</summary>
    public async Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Definition.StartTimeout);
        try
        {
            var client = await ConnectAsync(timeout.Token, cancellationToken).ConfigureAwait(false);
            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            return [.. tools.Select(tool => tool.ProtocolTool)];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw Fail($"did not list its tools within {Seconds(Definition.StartTimeout)}");
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or McpServerUnavailableException))
        {
            throw Fail(exception.Message);
        }
    }

    /// <summary>
    /// Calls one tool with the call timeout, or, when the server offers the tasks extension, starts it as a task and polls it
    /// up to the task timeout, cancelling the task on the server if Corvid stops waiting.
    /// </summary>
    /// <param name="tainted">Whether the calling session holds outside content; Vault applies the secret's taint rule.</param>
    public async Task<CallToolResult> CallAsync(string tool, IDictionary<string, JsonElement> arguments, bool tainted,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(tool);
        ArgumentNullException.ThrowIfNull(arguments);
        McpClient client;
        using (var start = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            start.CancelAfter(Definition.StartTimeout);
            try
            {
                client = await ConnectAsync(start.Token, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw Fail($"did not start within {Seconds(Definition.StartTimeout)}");
            }
        }

        using var scope = McpRequestScope.Enter(tainted, Definition.Name);
        var request = new CallToolRequestParams { Name = tool, Arguments = arguments };
        if (SupportsTasks(client))
            return await CallAsTaskAsync(client, request, cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Definition.CallTimeout);
        try
        {
            return await client.CallToolAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"MCP server '{Definition.Name}' did not answer {tool} within {Seconds(Definition.CallTimeout)}.");
        }
    }

    private async Task<CallToolResult> CallAsTaskAsync(McpClient client, CallToolRequestParams request, CancellationToken cancellationToken)
    {
        ResultOrCreatedTask<CallToolResult> started;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(Definition.CallTimeout);
            try
            {
                started = await client.CallToolAsTaskAsync(request, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"MCP server '{Definition.Name}' did not answer {request.Name} within {Seconds(Definition.CallTimeout)}.");
            }
        }

        if (!started.IsTask)
            return started.Result!;
        var task = started.TaskCreated!;
        var interval = task.PollIntervalMs;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(Definition.TaskTimeout);
        try
        {
            while (true)
            {
                await Task.Delay(PollInterval(interval), _time, deadline.Token).ConfigureAwait(false);
                var state = await client.GetTaskAsync(task.TaskId, deadline.Token).ConfigureAwait(false);
                switch (state)
                {
                    case CompletedTaskResult completed:
                        return completed.Result.Deserialize(McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(CallToolResult))) as CallToolResult
                            ?? throw new McpToolException($"MCP server '{Definition.Name}' completed task {task.TaskId} without a tool result.");
                    case FailedTaskResult failed:
                        throw new McpToolException($"{Definition.Name}/{request.Name} failed: {failed.Error.GetRawText()}");
                    case CancelledTaskResult:
                        throw new McpToolException($"{Definition.Name}/{request.Name} was cancelled by the server.");
                    case InputRequiredTaskResult:
                        await CancelTaskQuietlyAsync(client, task.TaskId).ConfigureAwait(false);
                        throw new McpToolException($"{Definition.Name}/{request.Name} asked for input, which Corvid does not give servers; the task was cancelled.");
                }
                interval = state.PollIntervalMs ?? interval;
            }
        }
        catch (OperationCanceledException)
        {
            await CancelTaskQuietlyAsync(client, task.TaskId).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new TimeoutException($"MCP server '{Definition.Name}' did not finish {request.Name} within {Seconds(Definition.TaskTimeout)}; the task was cancelled.");
        }
    }

    private static async Task CancelTaskQuietlyAsync(McpClient client, string taskId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await client.CancelTaskAsync(taskId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or McpException or IOException or InvalidOperationException)
        {
            // The server may already have finished, dropped the task or exited; Corvid has stopped waiting either way.
        }
    }

    private static TimeSpan PollInterval(long? milliseconds) =>
        TimeSpan.FromMilliseconds(Math.Clamp(milliseconds ?? 1_000, 100, 5_000));

    private static bool SupportsTasks(McpClient client) =>
        client.ServerCapabilities?.Extensions?.ContainsKey(TasksProtocol.ExtensionId) == true;

    /// <summary>The live client, starting the server when there is none or the last one ended.</summary>
    /// <param name="cancellationToken">The caller's token with the start timeout.</param>
    /// <param name="caller">The caller's own token, to tell a cancelled turn from a server that did not start in time.</param>
    private async Task<McpClient> ConnectAsync(CancellationToken cancellationToken, CancellationToken caller)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _client) is { Completion.IsCompleted: false } current)
            return current;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_client is { } client)
            {
                if (!client.Completion.IsCompleted)
                    return client;
                // The server exited or the connection dropped: start it again for this call.
                var completion = await client.Completion.ConfigureAwait(false);
                _client = null;
                await client.DisposeAsync().ConfigureAwait(false);
                Note(McpServerState.Stopped, "ended: " + (completion.Exception?.Message ?? "the connection closed"));
            }

            lock (_lock)
            {
                if (_state == McpServerState.Failed && _time.GetUtcNow() < _retryAt)
                    throw new McpServerUnavailableException($"MCP server '{Definition.Name}' is unavailable: {_detail}");
                _state = McpServerState.Starting;
                _standardError.Clear();
            }

            var options = new McpClientOptions
            {
                ClientInfo = new Implementation { Name = "corvid", Version = Version },
                // The tasks extension is the only capability declared: no sampling, elicitation or roots.
                Capabilities = new ClientCapabilities
                {
                    Extensions = new Dictionary<string, object> { [TasksProtocol.ExtensionId] = EmptyObject() },
                },
                InitializationTimeout = Definition.StartTimeout,
            };
            try
            {
                var transport = _transports.Create(Definition, AddStandardError);
                var created = await McpClient.CreateAsync(transport, options, loggerFactory: null, cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _client, created);
                var info = created.ServerInfo is { } server ? $"{server.Name} {server.Version}" : null;
                Note(McpServerState.Ready, info);
                return created;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A cancelled turn or a stopping host is not the server's fault: no fail-fast window, and the next call starts it.
                if (caller.IsCancellationRequested)
                {
                    Note(McpServerState.Stopped, "the start was cancelled");
                    throw;
                }
                throw Fail("did not start in time");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw Fail(exception.Message);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private McpServerUnavailableException Fail(string reason)
    {
        string detail;
        lock (_lock)
        {
            var errors = _standardError.Count > 0 ? " Standard error: " + string.Join(" | ", _standardError) : "";
            detail = reason + errors;
            _state = McpServerState.Failed;
            _detail = detail;
            _retryAt = _time.GetUtcNow() + RetryDelay;
        }
        return new McpServerUnavailableException($"MCP server '{Definition.Name}' is unavailable: {detail}");
    }

    private void Note(McpServerState state, string? detail)
    {
        lock (_lock)
        {
            _state = state;
            _detail = detail;
        }
    }

    private void AddStandardError(string line)
    {
        lock (_lock)
        {
            // Only the end is kept: enough to say why a server would not start, never a growing buffer.
            _standardError.Enqueue(line.Length > 300 ? line[..300] + "…" : line);
            while (_standardError.Count > StandardErrorLines)
                _standardError.Dequeue();
        }
    }

    private static JsonElement EmptyObject()
    {
        using var document = JsonDocument.Parse("{}");
        return document.RootElement.Clone();
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture) + " s";

    public async ValueTask DisposeAsync()
    {
        McpClient? client;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;
            client = _client;
            _client = null;
        }
        finally
        {
            _gate.Release();
        }
        if (client is not null)
            await client.DisposeAsync().ConfigureAwait(false);
        Note(McpServerState.Stopped, null);
    }
}

/// <summary>
/// The calling session's taint and the server, carried in the async flow to the HTTP pipeline under the SDK, so Vault
/// applies the secret's taint and toolset rules to each request (vault.md §6).
/// </summary>
internal static class McpRequestScope
{
    private static readonly AsyncLocal<(bool Tainted, string Server)?> Current = new();

    public static (bool Tainted, string Server)? Value => Current.Value;

    public static IDisposable Enter(bool tainted, string server)
    {
        var previous = Current.Value;
        Current.Value = (tainted, server);
        return new Restore(previous);
    }

    private sealed class Restore((bool, string)? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
