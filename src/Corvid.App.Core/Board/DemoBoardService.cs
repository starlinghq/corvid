using Corvid.App.Core.Gateway;
using Corvid.App.Core.Mcp;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Board;

/// <summary>
/// The Board mockup's moment (Board.dc.html) beyond the sessions: four items the other areas hold, a held MCP tool, a
/// Telegram pairing code, two facts nightly-tidy staged and that job's finished run, and the engine's state for the status
/// bar. Consistent with the session demo (<see cref="DemoSessionService"/>): the run is its <c>job:nightly-tidy</c>
/// session, finished at 09:00:41 with three tool calls, and the clock is the frozen 14:05.
/// </summary>
public sealed class DemoBoardService : IBoardService
{
    public const string McpItemId = "mcp:notes__search";
    public const string PairingItemId = "pairing:telegram:ABCD-EFGH";
    public const string MemoryItemId = "memory:nightly-tidy:0900";
    public const string JobItemId = "job:nightly-tidy:0900";

    private readonly List<AttentionItem> _items;
    private readonly List<(string Item, string Action)> _resolved = [];

    public DemoBoardService(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var zone = time.LocalTimeZone;
        var day = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone);
        DateTimeOffset At(int hour, int minute, int second = 0)
        {
            var wall = new DateTime(day.Year, day.Month, day.Day, hour, minute, second, DateTimeKind.Unspecified);
            return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
        }

        _items =
        [
            new AttentionItem(McpItemId, AttentionKind.McpTool, "MCP tool held · notes__search changed",
                "its description differs from the pin of 2026-10-19 · not offered until you accept")
            {
                At = At(8, 12),
                Actions =
                [
                    // Pinning is done on MCP's card, which shows the diff and the hash accept pins; never from a bare button.
                    new AttentionAction("review", "Review…", AttentionActionKind.Navigate)
                    {
                        Area = Core.Area.Mcp, Target = DemoMcpService.HeldTool,
                        Help = "Open MCP at the held definition: its diff against the pin, the hash accept pins, and the answers",
                    },
                ],
            },
            new AttentionItem(PairingItemId, AttentionKind.Pairing, "Pairing · Telegram · @dana · code ABCD-EFGH",
                "approved on this machine only, never from a chat")
            {
                At = At(8, 40),
                Actions =
                [
                    // Pairing is done on Gateway's card, which shows the handle with any hidden character revealed. An owner
                    // answers approvals from the chat, so the tray opens that question there, never pairing from a bare button.
                    new AttentionAction("guest", "Pair as guest…", AttentionActionKind.Navigate)
                    {
                        Area = Core.Area.Gateway, Target = GatewayLinks.Card(DemoGatewayService.DanaRequestId),
                        Help = "Open Gateway at @dana's request, to pair them as a guest with an empty tool allowlist",
                    },
                    new AttentionAction("owner", "Pair as owner…", AttentionActionKind.Navigate)
                    {
                        Area = Core.Area.Gateway, Target = GatewayLinks.Owner(DemoGatewayService.DanaRequestId), Style = AttentionActionStyle.Ghost,
                        Help = "Open Gateway, which asks before pairing @dana as an owner who can answer approvals",
                    },
                    new AttentionAction("ignore", "Ignore", AttentionActionKind.Resolve)
                    {
                        Style = AttentionActionStyle.Danger, Help = "Drop the code; @dana stays unpaired",
                    },
                ],
            },
            new AttentionItem(MemoryItemId, AttentionKind.Memory, "Memory · 2 facts staged",
                "from nightly-tidy 09:00 · staged because memory_op scored 0.62, under its floor")
            {
                At = At(9, 0, 38),
                Actions =
                [
                    new AttentionAction("review", "Review", AttentionActionKind.Navigate)
                    {
                        Area = Core.Area.Memory, IsLink = true, Help = "Open Memory's review of the staged facts",
                    },
                ],
            },
            new AttentionItem(JobItemId, AttentionKind.Job, "Job finished · nightly-tidy",
                "09:00:41 · 3 tool calls · result delivered to the Telegram home chat")
            {
                At = At(9, 0, 41),
                Actions =
                [
                    new AttentionAction("open-run", "Open run", AttentionActionKind.OpenSession)
                    {
                        SessionId = DemoSessionService.JobSessionId, Help = "Open the run as it ran, read-only",
                    },
                ],
            },
        ];
    }

    public event EventHandler? AttentionChanged;

    /// <summary>The answers given so far, in order: which item, with which action.</summary>
    public IReadOnlyList<(string Item, string Action)> Resolved => _resolved;

    public Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<AttentionItem> items = [.. _items.OrderBy(item => item.At)];
        return Task.FromResult(items);
    }

    public Task ResolveAsync(string itemId, string actionId, CancellationToken cancellationToken = default)
    {
        var item = _items.FirstOrDefault(candidate => candidate.Id == itemId);
        if (item is null || !item.Actions.Any(action => action.Id == actionId && action.Kind == AttentionActionKind.Resolve))
            return Task.CompletedTask;
        Answer(item, actionId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// The item was answered on its own area's page with an answer the tray only leads to (accepting a held tool on MCP,
    /// pairing on Gateway): it goes, as if the tray had answered it.
    /// </summary>
    public void AnsweredInArea(string itemId, string answer)
    {
        if (_items.FirstOrDefault(candidate => candidate.Id == itemId) is { } item)
            Answer(item, answer);
    }

    private void Answer(AttentionItem item, string actionId)
    {
        var itemId = item.Id;
        _items.Remove(item);
        _resolved.Add((itemId, actionId));
        AttentionChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<BoardStatus> GetStatusAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new BoardStatus(
            Engine: "in-process",
            Decider: "Kev-0.8B, calibrated",
            Scheduler: "here · next: nightly-tidy, tomorrow 09:00",
            Gateway: "telegram up · discord up · slack off"));
}
