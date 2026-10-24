using Corvid.App.Core.Board;

namespace Corvid.App.Core.Mcp;

/// <summary>
/// The MCP area's demo, consistent with the rest of the demo at 2026-10-24 14:05: four servers, one of which failed to
/// start, and the one held tool the Board's tray and the rail's badge show, <c>notes__search</c>, whose description and
/// schema changed since its pin of 2026-10-19 (held at 08:12). <c>web_tools__fetch_page</c> was flagged earlier and allowed,
/// so it asks before every call. Accepting, keeping blocked, forgetting, trusting and adding change this state as the
/// engine would; nothing is written, nothing is started and nothing is fetched.
/// <para>
/// The held tool is also the Board's attention item (<see cref="DemoBoardService.McpItemId"/>): accepting it here answers
/// that item. The tray only leads here (its Review… lands on the card); it never pins, since a pin is accepted where its
/// diff and hash are shown.
/// </para>
/// </summary>
public sealed class DemoMcpService : IMcpService, IDisposable
{
    public const string NotesServer = "notes";
    public const string HeldTool = "notes__search";

    private readonly TimeProvider _time;
    private readonly IBoardService? _board;
    private readonly List<McpServerRecord> _servers = [];
    private readonly List<McpHeldTool> _held = [];

    public DemoMcpService(TimeProvider time, IBoardService? board = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _board = board;
        Seed();
    }

    public event EventHandler? Changed;

    public Task<McpSnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<McpHeldTool> held = [.. _held];
        IReadOnlyList<McpServerRecord> servers = [.. _servers];
        return Task.FromResult(new McpSnapshot(servers, held,
            new McpServeStatus(false, ["memory_search", "memory_add (asks)", "session_list", "session_read"],
                "Another MCP client (an editor, another agent) starts `corvid mcp serve` over stdio; approvals still apply to what it asks for."),
            "~/.local/share/corvid/mcp-pins.json"));
    }

    public Task AcceptAsync(string server, string tool, string hash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var held = Held(server, tool);
        if (held.Hash != hash)
            throw new InvalidOperationException($"{tool}'s definition changed since it was shown (now {held.Hash}); review it again.");
        if (held.NeedsAllow)
            throw new InvalidOperationException($"{tool} was {(held.Reason == McpHoldReason.Flagged ? "flagged" : "withheld")}: only allow offers it, as a tool that asks every time.");
        if (McpText.HasHidden(held))
            throw new InvalidOperationException($"{SafeText.Reveal(tool)}'s name, server or definition holds hidden characters; it cannot be pinned.");
        Take(held, McpToolState.Pinned);
        if (_board is DemoBoardService board && server == NotesServer && tool == HeldTool)
            board.AnsweredInArea(DemoBoardService.McpItemId, "accept");
        return Task.CompletedTask;
    }

    public Task AllowAsync(string server, string tool, string hash, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var held = Held(server, tool);
        if (held.Hash != hash)
            throw new InvalidOperationException($"{tool}'s definition changed since it was shown (now {held.Hash}); review it again.");
        if (!held.NeedsAllow)
            throw new InvalidOperationException($"{tool} is not flagged; accept pins it.");
        if (McpText.HasHidden(held))
            throw new InvalidOperationException($"{SafeText.Reveal(tool)}'s name, server or definition holds hidden characters; it cannot be allowed.");
        Take(held, McpToolState.Allowed);
        return Task.CompletedTask;
    }

    public Task KeepBlockedAsync(string server, string tool, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var held = Held(server, tool);
        _held[_held.IndexOf(held)] = held with { KeptBlocked = true };
        UpdateTool(server, tool, record => record with { KeptBlocked = true });
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<int> ForgetAsync(string server, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = _servers.FindIndex(candidate => candidate.Name == server);
        if (index < 0)
            throw new InvalidOperationException($"There is no MCP server named {server}.");
        var record = _servers[index];
        if (!record.HasPins)
            throw new InvalidOperationException($"{server} has no pinned definitions; nothing was forgotten.");
        var count = record.Tools.Count(tool => tool.Hash.Length > 0);
        // Tools this process registered or holds stay as they are until it restarts; only the pins go.
        _servers[index] = record with
        {
            HasPins = false,
            Tools = [.. record.Tools.Select(tool => tool.State == McpToolState.Held ? tool : tool with { State = McpToolState.Unpinned, Hash = "" })],
        };
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(count);
    }

    public Task SetTrustAsync(string server, McpTrust trust, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var index = _servers.FindIndex(candidate => candidate.Name == server);
        if (index < 0)
            throw new InvalidOperationException($"There is no MCP server named {server}.");
        if (trust == McpTrust.Trusted && SafeText.HasHidden(_servers[index].Name + _servers[index].Target))
            throw new InvalidOperationException($"{server}'s configuration holds hidden characters; it cannot be trusted.");
        _servers[index] = _servers[index] with { Trust = trust };
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<McpServerRecord> AddServerAsync(McpServerDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        cancellationToken.ThrowIfCancellationRequested();
        var name = draft.Name.Trim();
        var target = draft.Target.Trim();
        if (SafeText.HasHidden(draft.Name) || SafeText.HasHidden(draft.Target))
            throw new InvalidOperationException("The server holds hidden characters; nothing was added.");
        if (name.Length == 0 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new InvalidOperationException("A server's name is letters, digits, - and _ (its tools are named <server>__<tool>).");
        if (_servers.Any(server => string.Equals(server.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"There is already a server named {name}.");
        if (target.Length == 0)
            throw new InvalidOperationException(draft.Transport == McpTransport.Stdio ? "Give the command that starts it." : "Give its address.");
        if (draft.Transport == McpTransport.Http
            && !(Uri.TryCreate(target, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http"))
            throw new InvalidOperationException("An HTTP server's address starts with https:// (or http:// on this machine).");
        var record = new McpServerRecord(name, draft.Transport, draft.Trust, McpServerState.NotStarted)
        {
            Target = target,
            Detail = "starts with the next session; its tools are pinned as it first lists them",
        };
        _servers.Add(record);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(record);
    }

    /// <summary>
    /// What the engine does when a server lists a definition that differs from its pin: holds it. The demo's scenes and the
    /// tests call it to hold another tool (a flagged one, one with hidden characters).
    /// </summary>
    public void Hold(McpHeldTool held)
    {
        ArgumentNullException.ThrowIfNull(held);
        _held.RemoveAll(candidate => candidate.Server == held.Server && candidate.Tool == held.Tool);
        _held.Add(held);
        if (_servers.FindIndex(server => server.Name == held.Server) is var index and >= 0)
        {
            var server = _servers[index];
            var tool = new McpToolRecord(held.Server, held.Tool, McpToolState.Held) { Hash = held.PinnedHash, HeldReason = held.Reason };
            _servers[index] = server with
            {
                Tools = server.Tools.Any(candidate => candidate.Name == held.Tool)
                    ? [.. server.Tools.Select(candidate => candidate.Name == held.Tool ? tool : candidate)]
                    : [.. server.Tools, tool],
            };
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Nothing to let go of since the tray stopped pinning; kept so the composition disposes every area's service alike.</summary>
    public void Dispose()
    {
    }

    private McpHeldTool Held(string server, string tool) =>
        _held.Find(held => held.Server == server && held.Tool == tool)
        ?? throw new InvalidOperationException($"{server} has no held tool named {tool}.");

    private void Take(McpHeldTool held, McpToolState state)
    {
        _held.Remove(held);
        UpdateTool(held.Server, held.Tool, tool => tool with { State = state, Hash = held.Hash, HeldReason = null, KeptBlocked = false });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateTool(string server, string tool, Func<McpToolRecord, McpToolRecord> change)
    {
        var index = _servers.FindIndex(candidate => candidate.Name == server);
        if (index < 0)
            return;
        var record = _servers[index];
        _servers[index] = record with { HasPins = true, Tools = [.. record.Tools.Select(candidate => candidate.Name == tool ? change(candidate) : candidate)] };
    }

    private DateTimeOffset At(int days, int hour, int minute)
    {
        var zone = _time.LocalTimeZone;
        var day = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone).Date.AddDays(days);
        var wall = new DateTime(day.Year, day.Month, day.Day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    private void Seed()
    {
        var started = At(0, 8, 2);
        _servers.Add(new McpServerRecord(NotesServer, McpTransport.Stdio, McpTrust.Trusted, McpServerState.Running)
        {
            Target = "notes-mcp --root ./notes",
            Since = started,
            HasPins = true,
            Tools =
            [
                new McpToolRecord(NotesServer, HeldTool, McpToolState.Held) { Hash = "3e9a41c0", HeldReason = McpHoldReason.Changed },
                new McpToolRecord(NotesServer, "notes__read", McpToolState.Pinned) { Hash = "a17f02d9" },
                new McpToolRecord(NotesServer, "notes__append", McpToolState.Pinned) { Hash = "5c0e88b1" },
            ],
        });
        _servers.Add(new McpServerRecord("github", McpTransport.Http, McpTrust.Untrusted, McpServerState.Running)
        {
            Target = "https://mcp.example.com/github",
            Since = started,
            HasPins = true,
            Tools =
            [
                new McpToolRecord("github", "github__list_issues", McpToolState.Pinned) { Hash = "0b6d3e72" },
                new McpToolRecord("github", "github__get_pull", McpToolState.Pinned) { Hash = "e41a9c05" },
                new McpToolRecord("github", "github__create_issue", McpToolState.Pinned) { Hash = "9f2b7d18" },
                new McpToolRecord("github", "github__comment", McpToolState.Pinned) { Hash = "6a83c4fe" },
            ],
        });
        _servers.Add(new McpServerRecord("web-tools", McpTransport.Stdio, McpTrust.Untrusted, McpServerState.Running)
        {
            Target = "web-tools-mcp --no-cache",
            Since = started,
            HasPins = true,
            Tools =
            [
                new McpToolRecord("web-tools", "web_tools__fetch_page", McpToolState.Allowed) { Hash = "d2c5f019" },
                new McpToolRecord("web-tools", "web_tools__extract_links", McpToolState.Pinned) { Hash = "47e0ab3c" },
            ],
        });
        _servers.Add(new McpServerRecord("calendar", McpTransport.Stdio, McpTrust.Untrusted, McpServerState.Failed)
        {
            Target = "calendar-mcp",
            Since = started,
            Detail = "exited with code 1 at start: CALENDAR_TOKEN is not set",
        });

        _held.Add(new McpHeldTool(NotesServer, HeldTool, McpHoldReason.Changed,
            new McpDefinition(
                "Search the notes by keyword. Returns matching note titles with their paths and the first 200 characters of each note.",
                "Search notes",
                """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Words to look for" },
                    "limit": { "type": "integer", "default": 10 },
                    "include_body": { "type": "boolean", "default": true }
                  },
                  "required": ["query"]
                }
                """,
                """{ "readOnlyHint": true }"""),
            "7b20d5f9", At(0, 8, 12))
        {
            Pinned = new McpDefinition(
                "Search the notes by keyword. Returns matching note titles with their paths.",
                "Search notes",
                """
                {
                  "type": "object",
                  "properties": {
                    "query": { "type": "string", "description": "Words to look for" },
                    "limit": { "type": "integer", "default": 10 }
                  },
                  "required": ["query"]
                }
                """,
                """{ "readOnlyHint": true }"""),
            PinnedHash = "3e9a41c0",
            PinnedAt = At(-5, 10, 31),
        });
    }
}
