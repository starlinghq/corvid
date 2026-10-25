using System.Collections.ObjectModel;
using System.ComponentModel;
using Corvid.App.Core.Jobs;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Gateway;

/// <summary>
/// The Gateway area (docs/plan/ui.md §4.10, F16): pairing requests first, each approved here as guest or owner, never from
/// a chat; the paired users with their guest tool allowlists (empty by default) and revoke; deliveries waiting; the
/// channels with their state and since when; where the gateway runs; and per-chat persona and slot overrides. Handles and
/// chat names come from the channels and are data: cleaned in lists, revealed where a person pairs. While the area shows,
/// it writes the status bar's gateway item.
/// </summary>
public sealed class GatewayViewModel : AreaViewModel, IDisposable
{
    /// <summary>At most this many requests and users are drawn; a busy bot's queue is not the page's to grow without end.</summary>
    public const int MaxRows = 50;

    private readonly IGatewayService _service;
    private readonly ShellViewModel _shell;
    private readonly StatusBarLease _lease;
    private readonly TimeProvider _time;
    private GatewaySnapshot? _snapshot;
    private bool _active;
    private bool _disposed;
    private int _refreshSequence;

    /// <summary>A link named a request before the snapshot listed it: landed on when it arrives.</summary>
    private string? _linked;

    private IReadOnlyList<TextRun> _subtitle = [];
    private IReadOnlyList<TextRun> _notice = [];
    private Tone _noticeTone = Tone.Green;
    private IReadOnlyList<AdapterRow> _adapters = [];
    private IReadOnlyList<GatewayLine> _deliveries = [];
    private IReadOnlyList<GatewayLine> _overrides = [];
    private IReadOnlyList<TextRun> _hostText = [];
    private string _hostTag = "";
    private string _guestToolsNote = "";

    public GatewayViewModel(IGatewayService service, ShellViewModel shell, StatusBarViewModel statusBar, TimeProvider time)
        : base(Area.Gateway, "The Telegram, Discord and Slack adapters, pairing requests, paired users and deliveries.")
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(statusBar);
        ArgumentNullException.ThrowIfNull(time);
        _service = service;
        _shell = shell;
        _time = time;
        _lease = new StatusBarLease(statusBar);
        DismissNoticeCommand = new RelayCommand(() => Say(true, ""));

        _service.Changed += OnServiceChanged;
        _shell.PropertyChanged += OnShellChanged;
        // A link followed before this area was first opened (the Board's "Pair as owner…") is read here: the page is made after it.
        if (_shell.CurrentArea == Area.Gateway && _shell.Link is { Area: Area.Gateway, Name: { } linked })
            _linked = linked;
        _ = RefreshAsync();
        UpdateActive();
    }

    public IReadOnlyList<TextRun> Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }

    public ObservableCollection<PairingRequestViewModel> Requests { get; } = [];

    public bool HasRequests => Requests.Count > 0;

    public string NoRequestsText => "No pairing requests. Someone who messages a bot gets a code, and it waits here for you.";

    public ObservableCollection<PairedUserViewModel> Users { get; } = [];

    public bool HasUsers => Users.Count > 0;

    /// <summary>"Guests get only the tools on their allowlist: memory_search, session_list, …".</summary>
    public string GuestToolsNote { get => _guestToolsNote; private set => SetProperty(ref _guestToolsNote, value); }

    public IReadOnlyList<AdapterRow> Adapters { get => _adapters; private set => SetProperty(ref _adapters, value); }

    public IReadOnlyList<GatewayLine> Deliveries { get => _deliveries; private set { if (SetProperty(ref _deliveries, value)) OnPropertyChanged(nameof(HasDeliveries)); } }

    public bool HasDeliveries => _deliveries.Count > 0;

    public IReadOnlyList<GatewayLine> Overrides { get => _overrides; private set => SetProperty(ref _overrides, value); }

    public string HostTag { get => _hostTag; private set => SetProperty(ref _hostTag, value); }

    public IReadOnlyList<TextRun> HostText { get => _hostText; private set => SetProperty(ref _hostText, value); }

    // ---- Notice ----

    public IReadOnlyList<TextRun> Notice { get => _notice; private set { if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); } }

    public bool HasNotice => _notice.Count > 0;

    public Tone NoticeTone { get => _noticeTone; private set => SetProperty(ref _noticeTone, value); }

    public RelayCommand DismissNoticeCommand { get; }

    /// <summary>
    /// The request card a link landed on, for the page to bring into view (and focus its owner question's answer when the
    /// link opened it). Set before the page exists when the link opened the area; <see cref="TakePendingLanding"/> hands it
    /// over once.
    /// </summary>
    public PairingRequestViewModel? PendingLanding { get; private set; }

    public PairingRequestViewModel? TakePendingLanding()
    {
        var card = PendingLanding;
        PendingLanding = null;
        return card;
    }

    /// <summary>Esc: closes an open question or allowlist editor. False when there was nothing to close.</summary>
    public bool Close()
    {
        var closed = false;
        foreach (var request in Requests)
        {
            closed |= request.IsConfirmingOwner;
            request.CloseQuestion();
        }

        foreach (var user in Users)
            closed |= user.CloseQuestions();
        return closed;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _service.Changed -= OnServiceChanged;
        _shell.PropertyChanged -= OnShellChanged;
        _lease.Release();
    }

    // ---- Loading ----

    private void OnServiceChanged(object? sender, EventArgs e) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        // A later change's snapshot wins over an earlier one that finished after it.
        var sequence = ++_refreshSequence;
        GatewaySnapshot snapshot;
        try
        {
            snapshot = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (sequence == _refreshSequence && !_disposed)
                Say(false, $"The gateway's state could not be loaded: {Spans.Escape(exception.Message)}");
            return;
        }

        if (_disposed || sequence != _refreshSequence)
            return;
        _snapshot = snapshot;
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;

        // Requests and users keep their view models (an open question or editor survives an unrelated change), rebuilt once.
        Sync(Requests, snapshot.Requests.OrderBy(request => request.At).Take(MaxRows).ToList(),
            (card, request) => card.Request == request, request => RequestCard(request, now, zone));
        Sync(Users, snapshot.Users.OrderBy(user => user.Role == PairingRole.Owner ? 0 : 1).ThenBy(user => user.Since).Take(MaxRows).ToList(),
            (row, user) => row.User == user, user => new PairedUserViewModel(user, now, zone, () => _snapshot?.GuestTools ?? [], SaveAllowlistAsync, RevokeAsync, ReportFailures));
        OnPropertyChanged(nameof(HasRequests));
        OnPropertyChanged(nameof(HasUsers));

        Adapters = [.. snapshot.Adapters.Select(adapter => new AdapterRow(
            GatewayText.Channel(adapter.Name), GatewayText.State(adapter.State), GatewayText.StateTone(adapter.State),
            adapter.Since is { } since ? JobsText.When(since, now, zone) : "",
            SafeText.Clean(adapter.Account), SafeText.Clean(adapter.Detail)))];
        Deliveries = [.. snapshot.Deliveries.Take(MaxRows).Select(delivery => new GatewayLine(
            $"{SafeText.Clean(delivery.What)} → {SafeText.Clean(delivery.To)}",
            $"waiting since {JobsText.When(delivery.Since, now, zone)} · {SafeText.Clean(delivery.Why)}"))];
        Overrides = [.. snapshot.Overrides.Select(chat => new GatewayLine(
            $"{GatewayText.Channel(chat.Adapter)} · {SafeText.Clean(chat.Chat)}",
            $"persona {SafeText.Clean(chat.Persona)} · {SafeText.Clean(chat.Slot)}"))];
        GuestToolsNote = "A guest can use only the tools on its allowlist, chosen from: " + string.Join(", ", snapshot.GuestTools.Select(SafeText.Clean)) + ".";
        var hostSince = snapshot.Host.Since is { } at ? " since " + JobsText.When(at, now, zone) : "";
        (HostTag, var host) = snapshot.Host switch
        {
            { HostedHere: true } => ("in this app", $"This app hosts the gateway{hostSince}: the adapters run here while it is open, and their messages reach the sessions it runs."),
            { Nowhere: true } => ("off", $"No process hosts the gateway: {Spans.Escape(SafeText.Clean(snapshot.Host.Reason))} Pairing requests already waiting can still be answered here."),
            _ => ("elsewhere", $"**{Spans.Escape(SafeText.Clean(snapshot.Host.Holder))}** hosts the gateway{hostSince}; this app shows its state and approves its pairings."),
        };
        HostText = Spans.Parse(host);
        Subtitle = Spans.Parse(SubtitleText(snapshot));
        if (_active)
            _lease.Gateway = GatewayText.Summary(snapshot.Adapters);
        if (_linked is { } linked)
        {
            _linked = null;
            Land(linked);
        }
    }

    /// <summary>
    /// Inserts, moves and removes only what changed, so a row that stays keeps its element (and an open editor its focus).
    /// A move is a removal and an insertion, as the Board's sync does it, for Uno's theme.
    /// </summary>
    private static void Sync<TRow, TRecord>(ObservableCollection<TRow> rows, List<TRecord> records, Func<TRow, TRecord, bool> same, Func<TRecord, TRow> make)
        where TRow : class
    {
        var wanted = records.Select(record => rows.FirstOrDefault(row => same(row, record)) is { } kept ? kept : make(record)).ToList();
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = rows.IndexOf(wanted[i]);
            if (at == i)
                continue;
            if (at >= 0)
                rows.RemoveAt(at);
            rows.Insert(i, wanted[i]);
        }

        while (rows.Count > wanted.Count)
            rows.RemoveAt(rows.Count - 1);
    }

    /// <summary>
    /// A link into Gateway naming a pairing request (the Board's tray): its card comes into view, with the owner question
    /// open when the link asked for it. A handle that hides a character never gets the question.
    /// </summary>
    private void Land(string name)
    {
        var (id, owner) = GatewayLinks.Read(name);
        if (Requests.FirstOrDefault(card => card.Request.Id == id) is not { } card)
        {
            Say(false, "That pairing request was already answered.");
            return;
        }

        if (owner)
            card.IsConfirmingOwner = card.CanPair;
        PendingLanding = card;
        OnPropertyChanged(nameof(PendingLanding));
    }

    private PairingRequestViewModel RequestCard(PairingRequest request, DateTimeOffset now, TimeZoneInfo zone)
    {
        var guest = new AsyncRelayCommand(token => PairAsync(request, PairingRole.Guest, token), () => !SafeText.HasHidden(request.Handle));
        var ignore = new AsyncRelayCommand(token => IgnoreAsync(request, token));
        foreach (var command in new[] { guest, ignore })
            ReportFailures(command);
        return new PairingRequestViewModel(request, now, zone, guest, token => PairAsync(request, PairingRole.Owner, token), ignore, ReportFailures);
    }

    private static string SubtitleText(GatewaySnapshot snapshot)
    {
        var where = snapshot.Host switch
        {
            { HostedHere: true } => "hosted in this app",
            { Nowhere: true } => "not running",
            _ => $"hosted by {Spans.Escape(SafeText.Clean(snapshot.Host.Holder))}",
        };
        return $"{where} · {Spans.Escape(GatewayText.Summary(snapshot.Adapters))}";
    }

    // ---- Actions ----

    private async Task PairAsync(PairingRequest request, PairingRole role, CancellationToken cancellationToken)
    {
        var user = await _service.PairAsync(request.Id, role, cancellationToken).ConfigureAwait(true);
        var handle = Spans.Escape(SafeText.Clean(user.Handle));
        Say(true, role == PairingRole.Guest
            ? $"Paired **{handle}** as a guest, with no tools: they can talk to Corvid. Widen… gives them tools."
            : $"Paired **{handle}** as an owner: they can answer approvals from {Spans.Escape(GatewayText.Channel(user.Adapter))}.");
    }

    private async Task IgnoreAsync(PairingRequest request, CancellationToken cancellationToken)
    {
        await _service.IgnoreAsync(request.Id, cancellationToken).ConfigureAwait(true);
        Say(true, $"Ignored the code from **{Spans.Escape(SafeText.Clean(request.Handle))}**: they stay unpaired.");
    }

    private async Task SaveAllowlistAsync(PairedUserViewModel row, IReadOnlyList<string> tools)
    {
        var user = await _service.SetAllowlistAsync(row.User.Id, tools, CancellationToken.None).ConfigureAwait(true);
        Say(true, user.Allowlist.Count == 0
            ? $"**{Spans.Escape(row.Handle)}** has no tools: they can only talk."
            : $"**{Spans.Escape(row.Handle)}** can now use {Spans.Escape(string.Join(", ", user.Allowlist.Select(SafeText.Clean)))}.");
    }

    private async Task RevokeAsync(PairedUserViewModel row)
    {
        await _service.RevokeAsync(row.User.Id, CancellationToken.None).ConfigureAwait(true);
        Say(true, $"Revoked **{Spans.Escape(row.Handle)}**: their next message gets a new code.");
    }

    // ---- Shell, status bar, notices ----

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentArea))
        {
            UpdateActive();
        }
        else if (e.PropertyName == nameof(ShellViewModel.Link) && _shell.Link is { Area: Area.Gateway, Name: { } name })
        {
            if (_snapshot is null)
                _linked = name;
            else
                Land(name);
        }
    }

    private void UpdateActive()
    {
        var active = _shell.CurrentArea == Area.Gateway;
        if (active == _active)
            return;
        _active = active;
        if (active)
        {
            _lease.Take();
            // The Board's own items (the engine line, how many sessions run) are not this page's to show.
            _lease.Engine = "";
            _lease.Trailing = "";
            if (_snapshot is not null)
                _lease.Gateway = GatewayText.Summary(_snapshot.Adapters);
            return;
        }

        Close();
        _lease.Release();
    }

    private void ReportFailures(AsyncRelayCommand command) => command.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(AsyncRelayCommand.Error) && command.Error is { } error)
            Say(false, $"That did not work: {Spans.Escape(error.Message)}");
    };

    private void Say(bool ok, string markup)
    {
        NoticeTone = ok ? Tone.Green : Tone.Red;
        Notice = Spans.Parse(markup);
    }
}
