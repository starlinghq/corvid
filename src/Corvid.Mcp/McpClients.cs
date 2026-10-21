using System.Collections.Immutable;
using Corvid.Tools;

namespace Corvid.Mcp;

/// <summary>
/// The configured MCP servers (plan §7, §12). <see cref="Start"/> connects to each in the background and registers its tools
/// in the <see cref="ToolRegistry"/> as they are listed; the turn loop reads the registry per turn, so a server that is slow
/// or never answers delays only its own tools and never the host. Tools are registered once per process under their
/// prefixed names: a server that restarts keeps the definitions first listed (a later listing that changed them is not
/// taken), and a call that names a tool the server no longer has fails with the server's error.
/// </summary>
public sealed class McpClients : IAsyncDisposable
{
    private readonly ToolRegistry _registry;
    private readonly ImmutableArray<McpServerConnection> _servers;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private Task _started = Task.CompletedTask;
    private bool _running;

    public McpClients(IEnumerable<McpServerDefinition> servers, IMcpTransports transports, ToolRegistry registry, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(transports);
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        var list = servers.ToList();
        var duplicate = list.GroupBy(server => server.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new ArgumentException($"MCP server '{duplicate.Key}' is configured more than once.", nameof(servers));
        _servers = [.. list.Select(server => new McpServerConnection(server, transports, time))];
    }

    public ImmutableArray<McpServerConnection> Servers => _servers;

    /// <summary>Completes when every server's first start has finished, ready or failed. Never faults.</summary>
    public Task Started
    {
        get
        {
            lock (_gate)
                return _started;
        }
    }

    public IReadOnlyList<McpServerStatus> Status() => [.. _servers.Select(server => server.Status)];

    /// <summary>Starts every server in the background and returns at once.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_running)
                return;
            _running = true;
            var token = _stopping.Token;
            _started = Task.WhenAll(_servers.Select(server => Task.Run(() => StartAsync(server, token), CancellationToken.None)));
        }
    }

    private async Task StartAsync(McpServerConnection server, CancellationToken cancellationToken)
    {
        IReadOnlyList<ModelContextProtocol.Protocol.Tool> tools;
        try
        {
            tools = await server.ListToolsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is McpServerUnavailableException or OperationCanceledException or ObjectDisposedException)
        {
            // The connection keeps the reason for /mcp; the host goes on without this server's tools.
            return;
        }
        Register(server, tools);
    }

    internal void Register(McpServerConnection server, IReadOnlyList<ModelContextProtocol.Protocol.Tool> tools)
    {
        var definition = server.Definition;
        var registered = new List<string>();
        var issues = new List<string>();
        var allowed = definition.Tools.IsEmpty ? null : definition.Tools.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (allowed is not null && !allowed.Contains(tool.Name))
                continue;
            if (!seen.Add(tool.Name))
            {
                issues.Add($"'{tool.Name}': listed twice");
                continue;
            }
            if (registered.Count >= definition.MaxTools)
            {
                issues.Add($"'{tool.Name}': over the limit of {definition.MaxTools} tools");
                continue;
            }
            var (descriptor, problem) = McpToolMapping.Describe(definition, tool);
            if (descriptor is null)
            {
                issues.Add(problem!);
                continue;
            }
            try
            {
                _registry.Add(new McpTool(server, tool.Name, descriptor));
                registered.Add(descriptor.Name);
            }
            catch (InvalidOperationException)
            {
                // Another tool (built-in, or from another server) already has the name; the first one keeps it.
                issues.Add($"'{tool.Name}': the name {descriptor.Name} is already taken");
            }
        }
        if (allowed is not null)
            issues.AddRange(allowed.Where(name => !seen.Contains(name)).Order(StringComparer.Ordinal).Select(name => $"'{name}': not offered by the server"));
        server.SetTools([.. registered], [.. issues]);
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (var server in _servers)
            await server.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
