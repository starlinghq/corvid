using System.Collections.Immutable;
using Corvid.Tools;

namespace Corvid.Mcp;

/// <summary>What happened to a tool definition (C-022): the host records it as an event and a span.</summary>
/// <param name="Kind">One of <see cref="McpPinKinds"/>.</param>
/// <param name="Hash">The listed definition's hash.</param>
/// <param name="PinnedHash">The pin it differs from, for a changed tool.</param>
/// <param name="Detail">The matched marker for a flagged tool; the withholding decision's id for an <c>injection</c> hold (C-033).</param>
public sealed record McpDefinitionNotice(string Server, string Tool, string Kind, string Hash, string? PinnedHash = null, string? Detail = null);

public static class McpPinKinds
{
    public const string New = McpHoldReasons.New;
    public const string Changed = McpHoldReasons.Changed;
    public const string Flagged = McpHoldReasons.Flagged;
    public const string Injection = McpHoldReasons.Injection;
    public const string Accepted = "accepted";
    public const string Allowed = "allowed";
}

/// <summary>The outcome of <see cref="McpClients.Accept"/>: the tool's Corvid name and the pinned hash when registered, or why not.</summary>
public sealed record McpAcceptResult(bool Accepted, string Message, string? Name = null, string? Hash = null);

/// <summary>
/// The configured MCP servers (plan §7, §12). <see cref="Start"/> connects to each in the background and registers its tools
/// in the <see cref="ToolRegistry"/> as they are listed; the turn loop reads the registry per turn, so a server that is slow
/// or never answers delays only its own tools and never the host. Tools are registered once per process under their
/// prefixed names: a server that restarts keeps the definitions first listed (a later listing that changed them is not
/// taken), and a call that names a tool the server no longer has fails with the server's error.
/// <para>
/// C-022: each listed definition is checked against its pin before it is registered. A server's first listing pins every
/// tool (trust on first use) and records the server even if it pins nothing; after that a new tool or a changed definition is held until the user accepts it, and a tool
/// whose text matches a poisoning pattern is held even on the first listing until the user allows it.
/// </para>
/// <para>
/// C-033: as an <see cref="IToolDescriptionReview"/>, a registered tool whose description the fabric's <c>injection</c>
/// question judged confidently is removed from the registry and held as <c>injection</c>, persistently, until the user
/// allows it. C-037: the turn loop no longer reports verdicts here (the generic question is not fitted for descriptions); a
/// hold C-033 already persisted is still honoured at listing, and the seam stays for a dedicated, fitted question. Every
/// withholding is therefore decided here, at listing, before a turn reads the registry and picks a tool.
/// </para>
/// </summary>
public sealed class McpClients : IToolDescriptionReview, IAsyncDisposable
{
    /// <summary>The prefix of an MCP tool's <see cref="ToolDescriptor.Origin"/>.</summary>
    public const string OriginPrefix = "mcp:";

    private readonly ToolRegistry _registry;
    private readonly ImmutableArray<McpServerConnection> _servers;
    private readonly IMcpPinStore _pins;
    private readonly Action<McpDefinitionNotice>? _notices;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _gate = new();
    // Accept and Withhold change the registry, a connection's tools and the pins together; one at a time.
    private readonly Lock _review = new();
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

    /// <summary>Where the definitions are pinned; <c>/mcp forget</c> removes a server's entry through it (C-033).</summary>
    public IMcpPinStore PinStore => _pins;

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
        var entries = new List<McpRegisteredTool>();
        var issues = new List<string>();
        var held = new List<McpHeldTool>();
        var allowed = definition.Tools.IsEmpty ? null : definition.Tools.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, McpToolPin> pins;
        bool firstUse;
        try
        {
            pins = _pins.Pins(definition.Name);
            // A server is trusted on first use once: its entry is written with the first listing even when nothing is pinned.
            firstUse = !_pins.Listed(definition.Name);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Without the pins nothing can be checked, so nothing is offered: a broken store must not mean trust on first use.
            server.SetTools([], [$"no tools offered: the pinned definitions cannot be read ({exception.Message})"]);
            return;
        }
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
            // Held tools count: each has a slot, so accepting them never takes a server over its limit.
            if (registered.Count + held.Count >= definition.MaxTools)
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
            var untrusted = false;
            if (flag is not null)
            {
                if (pin?.AllowedFlagged == listed.Hash && pin.Hash == listed.Hash)
                    untrusted = true;
                else
                    hold = McpHoldReasons.Flagged;
            }
            else if (pin?.Withheld == listed.Hash && pin.AllowedFlagged != listed.Hash)
            {
                // C-033: withheld in an earlier process (or by another one) and not allowed since: still held.
                hold = McpHoldReasons.Injection;
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
            else if (pin.AllowedFlagged == listed.Hash)
            {
                // A definition the user allowed after an injection verdict (C-033) stays an untrusted server's tool.
                untrusted = true;
            }
            if (hold is not null)
            {
                var decision = hold == McpHoldReasons.Injection ? pin!.WithheldDecision : null;
                held.Add(new McpHeldTool(definition.Name, tool.Name, hold, listed, pin, flag) { Listed = tool, Decision = decision });
                Notify(new McpDefinitionNotice(definition.Name, tool.Name, hold, listed.Hash, pin?.Hash, flag ?? decision));
                continue;
            }
            if (untrusted)
                descriptor = McpToolMapping.Describe(definition, tool, untrusted: true).Descriptor!;

            try
            {
                var instance = new McpTool(server, tool.Name, descriptor);
                _registry.Add(instance);
                registered.Add(descriptor.Name);
                entries.Add(new McpRegisteredTool(descriptor.Name, tool, listed, instance));
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
        if (firstUse || pinNow.Count > 0)
        {
            try
            {
                _pins.Set(definition.Name, pinNow);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // The tools were listed for the first time and stay offered in this process; the next start is a first use again.
                issues.Add($"the tools' definitions could not be pinned ({exception.Message})");
            }
        }
        server.SetTools([.. registered], [.. issues], [.. held], entries);
    }

    /// <summary>
    /// C-033: withholds the named tools after a confident <c>injection</c> verdict over their descriptions (C-037: no caller
    /// in the turn loop since; kept for a dedicated <c>description_injection</c> question, deferred). Each one this
    /// process registered from the origin's server is removed from the registry, its instance refuses further calls, and it
    /// is held as <c>injection</c> with the definition it was listed with; the pin records the withheld hash and the decision,
    /// so the hold survives a restart and another process sees it. A definition the user allowed (<c>/mcp allow</c>) is left
    /// offered: the verdict is only logged. Names that are not registered (already withheld, or another source's) are skipped.
    /// </summary>
    public void Withhold(ToolWithholding request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.Origin.StartsWith(OriginPrefix, StringComparison.Ordinal))
            return;
        var server = request.Origin[OriginPrefix.Length..];
        var connection = _servers.FirstOrDefault(candidate => candidate.Definition.Name == server);
        if (connection is null)
            return;
        lock (_review)
        {
            foreach (var name in request.Tools.Distinct(StringComparer.Ordinal))
            {
                if (connection.Registered(name) is not { } entry)
                    continue;
                var hash = entry.Definition.Hash;
                McpToolPin? pin;
                try
                {
                    pin = _pins.Pins(server).GetValueOrDefault(entry.Listed.Name);
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    // Without the pins an earlier allow cannot be seen; withholding is the safe side.
                    pin = null;
                }
                if (pin?.AllowedFlagged == hash)
                    continue;

                _registry.Remove(name);
                entry.Instance.Withdraw();
                var withheld = (pin ?? new McpToolPin(hash, entry.Definition.Canonical, _time.GetUtcNow())) with
                {
                    Withheld = hash,
                    WithheldDecision = request.DecisionId,
                };
                try
                {
                    _pins.Set(server, new Dictionary<string, McpToolPin>(StringComparer.Ordinal) { [entry.Listed.Name] = withheld });
                }
                catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
                {
                    // Held in this process; the next start checks the definition against its pin as before.
                }
                connection.Withdraw(entry, new McpHeldTool(server, entry.Listed.Name, McpHoldReasons.Injection, entry.Definition, withheld, null)
                {
                    Listed = entry.Listed,
                    Decision = request.DecisionId,
                });
                Notify(new McpDefinitionNotice(server, entry.Listed.Name, McpPinKinds.Injection, hash, pin?.Hash, request.DecisionId));
            }
        }
    }

    /// <summary>The shortest hash prefix <c>--hash</c> takes (C-033).</summary>
    public const int MinHashPrefix = 12;

    /// <summary>
    /// Pins the definition this process holds for <paramref name="tool"/> (the one <c>/mcp diff</c> shows) and registers it,
    /// so it is offered from the next turn. A flagged tool, or one withheld after an <c>injection</c> verdict (C-033), is
    /// accepted only with <paramref name="allowFlagged"/> (<c>/mcp allow</c>), which records that this exact definition was
    /// allowed, clears the withhold and registers it as an untrusted server's tool; <paramref name="allowFlagged"/> is refused
    /// for any other hold. With <paramref name="expectedHash"/> (at least <see cref="MinHashPrefix"/> hex characters) the held
    /// definition's hash must start with it, so a review in another process accepts what it showed (C-033).
    /// </summary>
    public McpAcceptResult Accept(string server, string tool, bool allowFlagged = false, string? expectedHash = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(server);
        ArgumentException.ThrowIfNullOrEmpty(tool);
        var connection = _servers.FirstOrDefault(candidate => candidate.Definition.Name == server);
        if (connection is null)
            return new McpAcceptResult(false, $"There is no MCP server named '{server}'.");
        lock (_review)
            return Accept(connection, tool, allowFlagged, expectedHash);
    }

    private McpAcceptResult Accept(McpServerConnection connection, string tool, bool allowFlagged, string? expectedHash)
    {
        var server = connection.Definition.Name;
        if (connection.Held(tool) is not { } held)
            return new McpAcceptResult(false, $"{server} has no held tool named '{tool}'.");
        if (expectedHash is not null)
        {
            var prefix = expectedHash.Trim().ToLowerInvariant();
            if (prefix.Length < MinHashPrefix || prefix.Length > held.Definition.Hash.Length || !prefix.All(char.IsAsciiHexDigit))
                return new McpAcceptResult(false, $"--hash takes at least {MinHashPrefix} hex characters of the hash /mcp diff {server} {tool} shows.");
            if (!held.Definition.Hash.StartsWith(prefix, StringComparison.Ordinal))
                return new McpAcceptResult(false,
                    $"{server}/{tool} is held with hash {held.Definition.Hash[..MinHashPrefix]}, not {prefix}: the server listed another definition since it was reviewed; read /mcp diff {server} {tool} again.");
        }
        var review = McpHoldReasons.NeedsReview(held.Reason);
        if (review && !allowFlagged)
            return new McpAcceptResult(false, held.Reason == McpHoldReasons.Flagged
                ? $"{server}/{tool} is flagged (\"{held.Flag}\"), so accept does not take it; read /mcp diff {server} {tool}, then /mcp allow {server} {tool} to offer it as an untrusted tool."
                : $"{server}/{tool} was withheld after an injection verdict (decision {held.Decision}), so accept does not take it; read /mcp diff {server} {tool}, then /mcp allow {server} {tool} to offer it as an untrusted tool.");
        if (!review && allowFlagged)
            return new McpAcceptResult(false, $"{server}/{tool} is not flagged or withheld; /mcp accept {server} {tool} pins it.");
        var flagged = review;

        if (connection.Status.Tools.Length >= connection.Definition.MaxTools)
            return new McpAcceptResult(false, $"{server} already offers {connection.Definition.MaxTools} tools, its limit (MaxTools), so {server}/{tool} is not taken.");
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
        var instance = new McpTool(connection, tool, descriptor);
        try
        {
            _registry.Add(instance);
        }
        catch (InvalidOperationException)
        {
            return new McpAcceptResult(false, $"Pinned, but the name {descriptor.Name} is already taken, so {server}/{tool} is not offered.");
        }
        connection.Release(held, new McpRegisteredTool(descriptor.Name, held.Listed, held.Definition, instance));
        Notify(new McpDefinitionNotice(server, tool, flagged ? McpPinKinds.Allowed : McpPinKinds.Accepted, held.Definition.Hash, held.Pin?.Hash,
            held.Flag ?? held.Decision));
        return new McpAcceptResult(true, flagged
            ? $"Allowed {server}/{tool} as {descriptor.Name}: it is treated as an untrusted server's tool (asks before every call, taints the session); " +
              (held.Reason == McpHoldReasons.Injection ? "a later injection verdict over this definition is only logged, and a change holds it again." : "a later change flags it again.")
            : $"Accepted {server}/{tool} as {descriptor.Name}; it is offered from the next turn.", descriptor.Name, held.Definition.Hash);
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
