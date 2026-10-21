using System.Collections.Immutable;
using Corvid.Tools;

namespace Corvid.Mcp;

/// <summary>What happened to a tool definition (C-022): the host records it as an event and a span.</summary>
/// <param name="Kind">One of <see cref="McpPinKinds"/>.</param>
/// <param name="Hash">The listed definition's hash.</param>
/// <param name="PinnedHash">The pin it differs from, for a changed tool.</param>
/// <param name="Detail">The matched marker for a flagged tool.</param>
public sealed record McpDefinitionNotice(string Server, string Tool, string Kind, string Hash, string? PinnedHash = null, string? Detail = null);

public static class McpPinKinds
{
    public const string New = McpHoldReasons.New;
    public const string Changed = McpHoldReasons.Changed;
    public const string Flagged = McpHoldReasons.Flagged;
    public const string Accepted = "accepted";
    public const string Allowed = "allowed";
}

/// <summary>The outcome of <see cref="McpClients.Accept"/>: the tool's Corvid name when registered, or why not.</summary>
public sealed record McpAcceptResult(bool Accepted, string Message, string? Name = null);

/// <summary>
/// The configured MCP servers (plan §7, §12). <see cref="Start"/> connects to each in the background and registers its tools
/// in the <see cref="ToolRegistry"/> as they are listed; the turn loop reads the registry per turn, so a server that is slow
/// or never answers delays only its own tools and never the host. Tools are registered once per process under their
/// prefixed names: a server that restarts keeps the definitions first listed (a later listing that changed them is not
/// taken), and a call that names a tool the server no longer has fails with the server's error.
/// <para>
/// C-022: each listed definition is checked against its pin before it is registered. A server's first listing pins every
/// tool (trust on first use); after that a new tool or a changed definition is held until the user accepts it, and a tool
/// whose text matches a poisoning pattern is held even on the first listing until the user allows it.
/// </para>
/// </summary>
public sealed class McpClients : IAsyncDisposable
{
    private readonly ToolRegistry _registry;
    private readonly ImmutableArray<McpServerConnection> _servers;
    private readonly IMcpPinStore _pins;
    private readonly Action<McpDefinitionNotice>? _notices;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    private Task _started = Task.CompletedTask;
    private bool _running;

    public McpClients(IEnumerable<McpServerDefinition> servers, IMcpTransports transports, ToolRegistry registry, TimeProvider? time = null)
        : this(servers, transports, registry, new InMemoryMcpPinStore(), notices: null, time)
    {
    }

    /// <param name="pins">Where definitions are pinned (Corvid state; <see cref="JsonFileMcpPinStore"/> in the host).</param>
    /// <param name="notices">Told of every held, accepted or allowed definition; called on the registering thread, and never allowed to fail it.</param>
    public McpClients(IEnumerable<McpServerDefinition> servers, IMcpTransports transports, ToolRegistry registry, IMcpPinStore pins,
        Action<McpDefinitionNotice>? notices = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentNullException.ThrowIfNull(transports);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(pins);
        _registry = registry;
        _pins = pins;
        _notices = notices;
        _time = time ?? TimeProvider.System;
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
        var held = new List<McpHeldTool>();
        var allowed = definition.Tools.IsEmpty ? null : definition.Tools.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, McpToolPin> pins;
        try
        {
            pins = _pins.Pins(definition.Name);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Without the pins nothing can be checked, so nothing is offered: a broken store must not mean trust on first use.
            server.SetTools([], [$"no tools offered: the pinned definitions cannot be read ({exception.Message})"]);
            return;
        }
        var firstUse = pins.Count == 0;
        var pinNow = new Dictionary<string, McpToolPin>(StringComparer.Ordinal);
        var now = _time.GetUtcNow();
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

            var listed = McpToolDefinition.From(tool);
            var pin = pins.GetValueOrDefault(tool.Name);
            var flag = McpDescriptionScan.Scan(tool);
            string? hold = null;
            if (flag is not null)
            {
                if (pin?.AllowedFlagged == listed.Hash && pin.Hash == listed.Hash)
                    descriptor = McpToolMapping.Describe(definition, tool, untrusted: true).Descriptor!;
                else
                    hold = McpHoldReasons.Flagged;
            }
            else if (pin is null)
            {
                if (firstUse)
                    pinNow[tool.Name] = new McpToolPin(listed.Hash, listed.Canonical, now);
                else
                    hold = McpHoldReasons.New;
            }
            else if (pin.Hash != listed.Hash)
            {
                hold = McpHoldReasons.Changed;
            }
            if (hold is not null)
            {
                held.Add(new McpHeldTool(definition.Name, tool.Name, hold, listed, pin, flag) { Listed = tool });
                Notify(new McpDefinitionNotice(definition.Name, tool.Name, hold, listed.Hash, pin?.Hash, flag));
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
                pinNow.Remove(tool.Name);
            }
        }
        if (allowed is not null)
            issues.AddRange(allowed.Where(name => !seen.Contains(name)).Order(StringComparer.Ordinal).Select(name => $"'{name}': not offered by the server"));
        if (pinNow.Count > 0)
        {
            try
            {
                _pins.Set(definition.Name, pinNow);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // The tools were listed for the first time and stay offered in this process; the next start pins them again.
                issues.Add($"the tools' definitions could not be pinned ({exception.Message})");
            }
        }
        server.SetTools([.. registered], [.. issues], [.. held]);
    }

    /// <summary>
    /// Pins the definition this process holds for <paramref name="tool"/> (the one <c>/mcp diff</c> shows) and registers it,
    /// so it is offered from the next turn. A flagged tool is accepted only with <paramref name="allowFlagged"/> (<c>/mcp
    /// allow</c>), which records that this exact definition was allowed and registers it as an untrusted server's tool;
    /// <paramref name="allowFlagged"/> is refused for a tool that is not flagged.
    /// </summary>
    public McpAcceptResult Accept(string server, string tool, bool allowFlagged = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        ArgumentException.ThrowIfNullOrEmpty(tool);
        var connection = _servers.FirstOrDefault(candidate => candidate.Definition.Name == server);
        if (connection is null)
            return new McpAcceptResult(false, $"There is no MCP server named '{server}'.");
        if (connection.Held(tool) is not { } held)
            return new McpAcceptResult(false, $"{server} has no held tool named '{tool}'.");
        var flagged = held.Reason == McpHoldReasons.Flagged;
        if (flagged && !allowFlagged)
            return new McpAcceptResult(false,
                $"{server}/{tool} is flagged (\"{held.Flag}\"), so accept does not take it; read /mcp diff {server} {tool}, then /mcp allow {server} {tool} to offer it as an untrusted tool.");
        if (!flagged && allowFlagged)
            return new McpAcceptResult(false, $"{server}/{tool} is not flagged; /mcp accept {server} {tool} pins it.");

        var (descriptor, problem) = McpToolMapping.Describe(connection.Definition, held.Listed, untrusted: flagged);
        if (descriptor is null)
            return new McpAcceptResult(false, problem!);
        var pin = new McpToolPin(held.Definition.Hash, held.Definition.Canonical, _time.GetUtcNow(), flagged ? held.Definition.Hash : null);
        try
        {
            _pins.Set(server, new Dictionary<string, McpToolPin>(StringComparer.Ordinal) { [tool] = pin });
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new McpAcceptResult(false, $"The pin could not be written: {exception.Message}");
        }
        try
        {
            _registry.Add(new McpTool(connection, tool, descriptor));
        }
        catch (InvalidOperationException)
        {
            return new McpAcceptResult(false, $"Pinned, but the name {descriptor.Name} is already taken, so {server}/{tool} is not offered.");
        }
        connection.Release(held, descriptor.Name);
        Notify(new McpDefinitionNotice(server, tool, flagged ? McpPinKinds.Allowed : McpPinKinds.Accepted, held.Definition.Hash, held.Pin?.Hash, held.Flag));
        return new McpAcceptResult(true, flagged
            ? $"Allowed {server}/{tool} as {descriptor.Name}: it is treated as an untrusted server's tool (asks before every call, taints the session); a later change flags it again."
            : $"Accepted {server}/{tool} as {descriptor.Name}; it is offered from the next turn.", descriptor.Name);
    }

    private void Notify(McpDefinitionNotice notice)
    {
        if (_notices is null)
            return;
        try
        {
            _notices(notice);
        }
        catch (Exception)
        {
            // The record is the host's; failing to write it never changes what is offered or held.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        foreach (var server in _servers)
            await server.DisposeAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }
}
