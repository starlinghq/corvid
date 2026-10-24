using System.Collections.Concurrent;
using System.Globalization;
using Corvid.App.Core;
using Corvid.App.Core.Board;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Gateway;
using Corvid.App.Core.Jobs;
using Corvid.App.Core.Mcp;
using Corvid.App.Core.Memory;
using Corvid.App.Core.Mvvm;
using Corvid.App.Engine.Ops;

namespace Corvid.App.Engine.Board;

/// <summary>
/// The Board's attention items and status bar over the window's own area services (A3 integration): held MCP tools from
/// <see cref="IMcpService"/>, pairing codes from <see cref="IGatewayService"/>, staged memory from
/// <see cref="IMemoryService"/>, and job runs that finished in the last day from <see cref="IJobsService"/>. It reads the
/// same services the MCP, Gateway, Memory and Jobs pages use, so an answer given on a page (a tool accepted, a code
/// paired) leaves the tray at the page's next change, and an answer given here (a code ignored) reaches the page.
/// <para>
/// A session's approvals and questions are not listed here: the Board reads them from the session summaries
/// (<see cref="Core.Sessions.SessionSummary.PendingApprovals"/>, which the engine's session service keeps live), and
/// adds them before these. Questions come with K-6.
/// </para>
/// <para>
/// <see cref="AttentionChanged"/> follows the areas' <c>Changed</c> events and this service's own answers, posted to the UI
/// thread and coalesced. Memory has no change event, so staged memory is read again whenever the tray reloads. A pairing
/// code another process's gateway gave is seen at the next reload. An area that is off (its service refuses with
/// <see cref="ServiceUnavailableException"/>) lists nothing; one that fails otherwise lists one item saying so, which opens
/// the area. When the runtime itself cannot start, the whole list refuses with its reason.
/// </para>
/// </summary>
public sealed class EngineBoardService : IBoardService, IDisposable
{
    /// <summary>How long a finished run stays in the tray unless it is dismissed.</summary>
    public static readonly TimeSpan FinishedRunsShown = TimeSpan.FromDays(1);

    private readonly AppEngine _engine;
    private readonly IMcpService _mcp;
    private readonly IGatewayService _gateway;
    private readonly IJobsService _jobs;
    private readonly IMemoryService _memory;
    private readonly TimeProvider _time;
    private readonly ChangeSignal _changed;
    private readonly ConcurrentDictionary<string, byte> _dismissed = new(StringComparer.Ordinal);
    private int _disposed;

    public EngineBoardService(AppEngine engine, IMcpService mcp, IGatewayService gateway, IJobsService jobs, IMemoryService memory,
        IDispatcher dispatcher, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(mcp);
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(memory);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        _engine = engine;
        _mcp = mcp;
        _gateway = gateway;
        _jobs = jobs;
        _memory = memory;
        _time = time;
        _changed = new ChangeSignal(dispatcher, () => AttentionChanged?.Invoke(this, EventArgs.Empty));
        _mcp.Changed += OnAreaChanged;
        _gateway.Changed += OnAreaChanged;
        _jobs.Changed += OnAreaChanged;
    }

    public event EventHandler? AttentionChanged;

    public async Task<IReadOnlyList<AttentionItem>> ListAttentionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // A runtime that cannot start refuses the whole list with its reason, rather than one failure item per area.
        await _engine.RuntimeAsync().ConfigureAwait(false);
        var areas = await Task.WhenAll(
            ReadArea(Core.Area.Mcp, AttentionKind.McpTool, "MCP", () => McpAsync(cancellationToken)),
            ReadArea(Core.Area.Gateway, AttentionKind.Pairing, "Gateway", () => PairingAsync(cancellationToken)),
            ReadArea(Core.Area.Memory, AttentionKind.Memory, "Memory", () => MemoryAsync(cancellationToken)),
            ReadArea(Core.Area.Jobs, AttentionKind.Job, "Jobs", () => JobsAsync(cancellationToken))).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return [.. areas.SelectMany(items => items).OrderBy(item => item.At)];
    }

    public async Task ResolveAsync(string itemId, string actionId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemId);
        ArgumentNullException.ThrowIfNull(actionId);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // Only answers that grant nothing are taken here: pinning and pairing are done on the area's card.
        if (itemId.StartsWith(PairingPrefix, StringComparison.Ordinal) && actionId == "ignore")
        {
            // A refusal (the code was answered or expired meanwhile) throws, and the tray says so.
            await _gateway.IgnoreAsync(itemId[PairingPrefix.Length..], cancellationToken).ConfigureAwait(false);
            _changed.Raise();
        }
        else if (itemId.StartsWith(JobPrefix, StringComparison.Ordinal) && actionId == "dismiss")
        {
            if (_dismissed.TryAdd(itemId[JobPrefix.Length..], 0))
                _changed.Raise();
        }
    }

    public async Task<BoardStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var runtime = await _engine.RuntimeAsync().ConfigureAwait(false);
        var scheduler = await Read(() => _jobs.LoadAsync(cancellationToken), Scheduler).ConfigureAwait(false);
        var gateway = await Read(() => _gateway.LoadAsync(cancellationToken),
            snapshot => snapshot.Adapters.Count == 0 ? "no adapters" : GatewayText.Summary(snapshot.Adapters)).ConfigureAwait(false);
        return new BoardStatus("in-process", runtime.DeciderState, scheduler, gateway);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _mcp.Changed -= OnAreaChanged;
        _gateway.Changed -= OnAreaChanged;
        _jobs.Changed -= OnAreaChanged;
        _changed.Dispose();
    }

    // ---- The areas ----

    internal const string McpPrefix = "mcp:";
    internal const string PairingPrefix = "pairing:";
    internal const string MemoryItemId = "memory:staged";
    internal const string JobPrefix = "job:";

    private async Task<IEnumerable<AttentionItem>> McpAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _mcp.LoadAsync(cancellationToken).ConfigureAwait(false);
        // A definition the person already looked at and kept blocked waits on no one.
        return snapshot.Held.Where(held => !held.KeptBlocked).Select(held =>
            new AttentionItem($"{McpPrefix}{held.Server}:{held.Tool}:{held.Hash}", AttentionKind.McpTool,
                $"MCP tool held · {held.Tool} {Held(held.Reason)}", McpDetail(held))
            {
                At = held.HeldAt,
                Actions =
                [
                    // Pinning is done on MCP's card, which shows the definition and the hash accept pins; never from a bare button.
                    new AttentionAction("review", "Review…", AttentionActionKind.Navigate)
                    {
                        Area = Core.Area.Mcp, Target = held.Tool,
                        Help = "Open MCP at the held definition: what changed, the hash accept pins, and the answers",
                    },
                ],
            });
    }

    private static string Held(McpHoldReason reason) => reason switch
    {
        McpHoldReason.New => "is new",
        McpHoldReason.Changed => "changed",
        McpHoldReason.Flagged => "is flagged",
        _ => "reads as an injection",
    };

    private string McpDetail(McpHeldTool held)
    {
        var pinned = held.PinnedAt is { } at ? $"its definition differs from the pin of {Day(at)} · " : "";
        return held.NeedsAllow ? $"{pinned}not offered until you allow it as a tool that always asks"
            : $"{pinned}not offered until you accept";
    }

    private async Task<IEnumerable<AttentionItem>> PairingAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _gateway.LoadAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Requests.Select(request =>
        {
            var channel = GatewayText.Channel(request.Adapter);
            return new AttentionItem(PairingPrefix + request.Id, AttentionKind.Pairing,
                $"Pairing · {channel} · {request.Handle} · code {request.Code}", "approved on this machine only, never from a chat")
            {
                At = request.At,
                Actions =
                [
                    // Pairing is done on Gateway's card, which shows the handle with any hidden character revealed.
                    new AttentionAction("guest", "Pair as guest…", AttentionActionKind.Navigate)
                    {
                        Area = Core.Area.Gateway, Target = GatewayLinks.Card(request.Id),
                        Help = "Open Gateway at the request, to pair them as a guest with the adapter's guest tools",
                    },
                    new AttentionAction("owner", "Pair as owner…", AttentionActionKind.Navigate)
                    {
                        Area = Core.Area.Gateway, Target = GatewayLinks.Owner(request.Id), Style = AttentionActionStyle.Ghost,
                        Help = "Open Gateway, which asks before pairing them as an owner who can answer approvals",
                    },
                    new AttentionAction("ignore", "Ignore", AttentionActionKind.Resolve)
                    {
                        Style = AttentionActionStyle.Danger, Help = "Drop the code; they stay unpaired",
                    },
                ],
            };
        });
    }

    private async Task<IEnumerable<AttentionItem>> MemoryAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _memory.LoadAsync(cancellationToken).ConfigureAwait(false);
        var facts = snapshot.StagedFacts.Count;
        var blocks = snapshot.BlockUpdates.Count;
        var lessons = snapshot.PendingLessons.Count;
        if (facts + blocks + lessons == 0)
            return [];
        var parts = new List<string>();
        if (facts > 0)
            parts.Add(Plural(facts, "fact"));
        if (blocks > 0)
            parts.Add(Plural(blocks, "block update"));
        if (lessons > 0)
            parts.Add(Plural(lessons, "lesson"));
        var staged = snapshot.StagedFacts.Select(fact => fact.StagedAt).Concat(snapshot.BlockUpdates.Select(block => block.StagedAt))
            .Where(at => at != default).DefaultIfEmpty(_time.GetUtcNow()).Min();
        return
        [
            new AttentionItem(MemoryItemId, AttentionKind.Memory, $"Memory · {string.Join(", ", parts)} waiting",
                "staged for your review before they are written")
            {
                At = staged,
                Actions =
                [
                    new AttentionAction("review", "Review", AttentionActionKind.Navigate)
                    {
                        Area = Core.Area.Memory, IsLink = true, Help = "Open Memory's review of what is staged",
                    },
                ],
            },
        ];
    }

    private async Task<IEnumerable<AttentionItem>> JobsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _jobs.LoadAsync(cancellationToken).ConfigureAwait(false);
        var since = _time.GetUtcNow() - FinishedRunsShown;
        var items = new List<AttentionItem>();
        foreach (var job in snapshot.Jobs)
        {
            if (job.LastRun is not { Status: JobRunStatus.Ok or JobRunStatus.Failed, FinishedAt: { } finished } run
                || finished < since || _dismissed.ContainsKey(run.RunId))
                continue;
            var failed = run.Status == JobRunStatus.Failed;
            var detail = string.Join(" · ", new[] { Time(finished), run.Detail }.Where(part => part.Length > 0));
            AttentionAction open = run.SessionId.Length > 0
                ? new AttentionAction("open-run", "Open run", AttentionActionKind.OpenSession)
                {
                    SessionId = run.SessionId, Help = "Open the run's session as it ran",
                }
                : new AttentionAction("open-run", "Open in Jobs", AttentionActionKind.Navigate)
                {
                    Area = Core.Area.Jobs, Help = "Open Jobs: this run had no agent turn, so it has no session",
                };
            items.Add(new AttentionItem(JobPrefix + run.RunId, AttentionKind.Job,
                $"{(failed ? "Job failed" : "Job finished")} · {job.Name}", detail)
            {
                At = finished,
                Actions =
                [
                    open,
                    new AttentionAction("dismiss", "Dismiss", AttentionActionKind.Resolve)
                    {
                        Style = AttentionActionStyle.Ghost, Help = "Take it off the tray; the run stays in Jobs",
                    },
                ],
            });
        }
        return items;
    }

    private string Scheduler(JobsSnapshot snapshot) => snapshot.Scheduler.Host switch
    {
        SchedulerHost.Here => snapshot.Jobs.Where(job => job.State == JobState.Active && job.NextRun is not null)
            .MinBy(job => job.NextRun) is { } next ? $"here · next: {next.Name}, {NextTime(next.NextRun!.Value)}" : "here · nothing due",
        SchedulerHost.Elsewhere => snapshot.Scheduler.Holder.Length > 0 ? $"elsewhere: {snapshot.Scheduler.Holder}" : "elsewhere",
        _ => "off here",
    };

    // ---- Helpers ----

    /// <summary>
    /// One area's items. An area that is off lists nothing; any other failure is one item saying so, which opens the area
    /// (where the page shows the whole reason), so a failure is never silent and the other areas still list theirs.
    /// </summary>
    private static async Task<IEnumerable<AttentionItem>> ReadArea(Area area, AttentionKind kind, string name, Func<Task<IEnumerable<AttentionItem>>> read)
    {
        try
        {
            return [.. await read().ConfigureAwait(false)];
        }
        catch (ServiceUnavailableException)
        {
            return [];
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            return
            [
                new AttentionItem($"failed:{area}", kind, $"{name} could not be read", exception.Message)
                {
                    Actions =
                    [
                        new AttentionAction("open", $"Open {name}", AttentionActionKind.Navigate)
                        {
                            Area = area, IsLink = true, Help = $"Open {name}, which says what failed",
                        },
                    ],
                },
            ];
        }
    }

    /// <summary>A status bar item from an area's snapshot, or what kept it from loading.</summary>
    private static async Task<string> Read<T>(Func<Task<T>> load, Func<T, string> text)
    {
        try
        {
            return text(await load().ConfigureAwait(false));
        }
        catch (ServiceUnavailableException)
        {
            return "off";
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
        {
            return "unreadable";
        }
    }

    private void OnAreaChanged(object? sender, EventArgs e) => _changed.Raise();

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";

    private DateTimeOffset Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, _time.LocalTimeZone);

    private string Day(DateTimeOffset at) => Local(at).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private string Time(DateTimeOffset at)
    {
        var local = Local(at);
        return local.Date == Local(_time.GetUtcNow()).Date
            ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : local.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
    }

    private string NextTime(DateTimeOffset at)
    {
        var local = Local(at);
        var today = Local(_time.GetUtcNow()).Date;
        var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
        return local.Date == today ? $"today {clock}"
            : local.Date == today.AddDays(1) ? $"tomorrow {clock}"
            : local.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
    }
}
