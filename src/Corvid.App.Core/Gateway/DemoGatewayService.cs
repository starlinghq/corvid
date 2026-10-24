using Corvid.App.Core.Board;

namespace Corvid.App.Core.Gateway;

/// <summary>
/// The Gateway area's demo, consistent with the rest of the demo at 2026-10-24 14:05: the gateway is hosted in this app,
/// Telegram and Discord are up and Slack is off (the status bar's "telegram up · discord up · slack off"), and the one
/// pairing request is the Board's: <c>@dana</c> on Telegram at 08:40 with code ABCD-EFGH. Pairing, ignoring, revoking and
/// widening an allowlist change this state as the gateway would; nothing is sent and nothing is written.
/// <para>
/// The request is also the Board's attention item (<see cref="DemoBoardService.PairingItemId"/>): answering it here answers
/// that item. From the tray, pairing only leads here (to the card, or to its owner question); the tray's Ignore is
/// followed here.
/// </para>
/// </summary>
public sealed class DemoGatewayService : IGatewayService, IDisposable
{
    public const string DanaRequestId = "pr-telegram-abcd";

    private readonly List<string> _guestTools = ["memory_search", "session_list", "web_fetch", "read_file", "notes__read"];

    private readonly TimeProvider _time;
    private readonly IBoardService? _board;
    private readonly List<GatewayAdapter> _adapters = [];
    private readonly List<PairingRequest> _requests = [];
    private readonly List<PairedUser> _users = [];
    private readonly List<PendingDelivery> _deliveries = [];
    private readonly List<ChatOverride> _overrides = [];
    private readonly GatewayHost _host;
    private int _next = 10;
    private bool _disposed;

    public DemoGatewayService(TimeProvider time, IBoardService? board = null)
    {
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _board = board;
        _host = new GatewayHost(true, "this app", At(0, 8, 2));
        Seed();
        if (_board is not null)
        {
            _board.AttentionChanged += OnBoardChanged;
            FollowBoard();
        }
    }

    public event EventHandler? Changed;

    public Task<GatewaySnapshot> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GatewaySnapshot(_host, [.. _adapters], [.. _requests], [.. _users], [.. _deliveries], [.. _overrides], [.. _guestTools]));
    }

    public Task<PairedUser> PairAsync(string requestId, PairingRole role, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = Pair(Request(requestId), role);
        // The tray only leads here to pair, so a pairing answered here is told to the Board directly.
        if (_board is DemoBoardService demo && requestId == DanaRequestId)
            demo.AnsweredInArea(DemoBoardService.PairingItemId, role == PairingRole.Owner ? "owner" : "guest");
        return Task.FromResult(user);
    }

    public async Task IgnoreAsync(string requestId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _requests.Remove(Request(requestId));
        Changed?.Invoke(this, EventArgs.Empty);
        if (_board is not null && requestId == DanaRequestId)
            await _board.ResolveAsync(DemoBoardService.PairingItemId, "ignore", cancellationToken).ConfigureAwait(true);
    }

    public Task RevokeAsync(string userId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = _users.Find(candidate => candidate.Id == userId) ?? throw new InvalidOperationException($"There is no paired user {userId}.");
        if (user.Role == PairingRole.Owner && _users.Count(candidate => candidate.Role == PairingRole.Owner) == 1)
            throw new InvalidOperationException("That is the only owner; pair another owner before revoking this one.");
        _users.Remove(user);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task<PairedUser> SetAllowlistAsync(string userId, IReadOnlyList<string> tools, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tools);
        cancellationToken.ThrowIfCancellationRequested();
        var index = _users.FindIndex(candidate => candidate.Id == userId);
        if (index < 0)
            throw new InvalidOperationException($"There is no paired user {userId}.");
        if (_users[index].Role != PairingRole.Guest)
            throw new InvalidOperationException("An owner has every tool; the allowlist is for guests.");
        if (SafeText.HasHidden(_users[index].Handle))
            throw new InvalidOperationException($"{SafeText.Reveal(_users[index].Handle)} holds hidden characters; their tools cannot be widened.");
        if (tools.FirstOrDefault(SafeText.HasHidden) is { } hidden)
            throw new InvalidOperationException($"{SafeText.Reveal(hidden)} holds hidden characters; it cannot be given to a guest.");
        if (tools.FirstOrDefault(tool => !_guestTools.Contains(tool)) is { } unknown)
            throw new InvalidOperationException($"{SafeText.Reveal(unknown)} is not a tool guests can be given.");
        var user = _users[index] with { Allowlist = [.. _guestTools.Where(tools.Contains)] };
        _users[index] = user;
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.FromResult(user);
    }

    /// <summary>A message to a bot from someone unpaired: what the gateway does, issuing a code. Tests and scenes call it.</summary>
    public PairingRequest Request(string adapter, string handle, string code)
    {
        var request = new PairingRequest($"pr-{_next++}", adapter, handle, code, _time.GetUtcNow()) { Chat = "direct message" };
        _requests.Add(request);
        Changed?.Invoke(this, EventArgs.Empty);
        return request;
    }

    /// <summary>A tool that guests can be given appears (an MCP server's, say). Tests and scenes call it.</summary>
    public void OfferGuestTool(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        _guestTools.Add(name);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_board is not null)
            _board.AttentionChanged -= OnBoardChanged;
    }

    private PairingRequest Request(string requestId) =>
        _requests.Find(request => request.Id == requestId) ?? throw new InvalidOperationException("That pairing request was already answered.");

    /// <summary>
    /// Pairs the sender, whichever page answered: a handle that hides a character may not be who it reads as, so it is never
    /// paired, from here or from the Board's tray.
    /// </summary>
    private PairedUser Pair(PairingRequest request, PairingRole role)
    {
        if (SafeText.HasHidden(request.Handle))
            throw new InvalidOperationException("The handle holds hidden characters; it cannot be paired. Ignore the request.");
        _requests.Remove(request);
        var user = new PairedUser($"u-{_next++}", request.Adapter, request.Handle, role, _time.GetUtcNow());
        _users.Add(user);
        Changed?.Invoke(this, EventArgs.Empty);
        return user;
    }

    private void OnBoardChanged(object? sender, EventArgs e) => FollowBoard();

    /// <summary>
    /// The Board's tray ignored the request: drop it here too. Only an ignore is followed (the tray pairs no one), and only
    /// one the tray gave; the Board may have answered before this area was opened, so its whole record is read.
    /// </summary>
    private void FollowBoard()
    {
        if (_board is not DemoBoardService board || _requests.Find(request => request.Id == DanaRequestId) is not { } request)
            return;
        if (board.Resolved.Contains((DemoBoardService.PairingItemId, "ignore")))
        {
            _requests.Remove(request);
            Changed?.Invoke(this, EventArgs.Empty);
        }
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
        _adapters.Add(new GatewayAdapter("telegram", AdapterState.Up) { Since = At(0, 8, 2), Account = "@corvid_home_bot" });
        _adapters.Add(new GatewayAdapter("discord", AdapterState.Up) { Since = At(0, 8, 2), Account = "corvid-lab bot" });
        _adapters.Add(new GatewayAdapter("slack", AdapterState.Off)
        {
            Detail = "no token: Vault holds no slack.bot-token, so the adapter does not start",
        });
        _requests.Add(new PairingRequest(DanaRequestId, "telegram", "@dana", "ABCD-EFGH", At(0, 8, 40)) { Chat = "direct message" });
        _users.Add(new PairedUser("u-1", "telegram", "@kestrel", PairingRole.Owner, At(-22, 19, 4)));
        _users.Add(new PairedUser("u-2", "discord", "river#2210", PairingRole.Guest, At(-12, 11, 30)) { Allowlist = ["memory_search"] });
        _deliveries.Add(new PendingDelivery("d-7", "cli-a410's release-notes summary", "Slack #release", At(0, 13, 40),
            "slack is off; it goes when the adapter starts"));
        _overrides.Add(new ChatOverride("telegram", "home chat", "default", "generator: qwen3.5-0.8b, local only"));
        _overrides.Add(new ChatOverride("discord", "#corvid-lab", "terse", "—"));
    }
}
