using System.Collections.ObjectModel;
using System.ComponentModel;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Mcp;

/// <summary>
/// The MCP area (docs/plan/ui.md §4.10, F15): the held tool definitions first, each with its diff against the pin and
/// accept, allow (a flagged one) or keep blocked; the servers with transport, trust and state; the selected server's
/// detail with trust and forget, and the tools it added with their pinned hashes; <c>corvid mcp serve</c>; and the
/// add-server form. Everything a server sends is data: names and descriptions are cleaned for display, and what a person
/// acts on in one click (a definition to pin, a server to trust, a server to add) is revealed and refused while it holds a
/// hidden character. The rail's MCP badge counts the held tools.
/// </summary>
public sealed class McpViewModel : AreaViewModel, IDisposable
{
    /// <summary>At most this many held cards are drawn at once; the rest wait in the tools table.</summary>
    public const int MaxHeldCards = 12;

    private readonly IMcpService _service;
    private readonly ShellViewModel _shell;
    private readonly StatusBarLease _lease;
    private readonly TimeProvider _time;
    private readonly Dictionary<string, McpServerRowViewModel> _rows = new(StringComparer.Ordinal);
    private McpSnapshot? _snapshot;
    private McpServerRowViewModel? _selected;
    private bool _active;
    private bool _disposed;

    private IReadOnlyList<TextRun> _subtitle = [];
    private IReadOnlyList<TextRun> _notice = [];
    private Tone _noticeTone = Tone.Green;
    private IReadOnlyList<ModelFact> _detail = [];
    private IReadOnlyList<McpToolRowViewModel> _tools = [];
    private McpConfirm _confirm;
    private string _heldMore = "";
    private bool _adding;
    private string _draftName = "";
    private string _draftTarget = "";
    private McpTransport _draftTransport = McpTransport.Stdio;
    private bool _draftTrusted;
    private string _draftWarning = "";
    private IReadOnlyList<TextRun> _serve = [];
    private IReadOnlyList<string> _serveExposes = [];

    public McpViewModel(IMcpService service, ShellViewModel shell, StatusBarViewModel statusBar, TimeProvider time)
        : base(Area.Mcp, "MCP servers and their tools: pins, held definitions with their diffs, accept, allow and forget.")
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(statusBar);
        ArgumentNullException.ThrowIfNull(time);
        _service = service;
        _shell = shell;
        _time = time;
        _lease = new StatusBarLease(statusBar);

        AskTrustCommand = new RelayCommand(() => Confirm = McpConfirm.Trust, () => _selected?.Record.Trust == McpTrust.Untrusted);
        TrustCommand = new AsyncRelayCommand(TrustAsync, () => _selected is { } row && !SafeText.HasHidden(row.Record.Name + row.Record.Target));
        UntrustCommand = new AsyncRelayCommand(UntrustAsync, () => _selected?.Record.Trust == McpTrust.Trusted);
        AskForgetCommand = new RelayCommand(() => Confirm = McpConfirm.Forget, () => _selected?.Record.HasPins == true);
        ForgetCommand = new AsyncRelayCommand(ForgetAsync, () => _selected?.Record.HasPins == true);
        CancelConfirmCommand = new RelayCommand(() => Confirm = McpConfirm.None);
        AddServerCommand = new RelayCommand(OpenForm);
        CancelAddCommand = new RelayCommand(() => IsAdding = false);
        SaveServerCommand = new AsyncRelayCommand(SaveServerAsync, CanSave);
        DismissNoticeCommand = new RelayCommand(() => Say(true, ""));
        Transports =
        [
            new Choice("stdio", "A command this machine runs; it talks over standard input and output", new RelayCommand(() => DraftTransport = McpTransport.Stdio)) { IsSelected = true },
            new Choice("http", "A server at an address; nothing is fetched until a session starts it", new RelayCommand(() => DraftTransport = McpTransport.Http)),
        ];
        foreach (var command in new[] { TrustCommand, UntrustCommand, ForgetCommand, SaveServerCommand })
            ReportFailures(command);

        _service.Changed += OnServiceChanged;
        _shell.PropertyChanged += OnShellChanged;
        _ = RefreshAsync();
        UpdateActive();
    }

    public IReadOnlyList<TextRun> Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }

    // ---- Held definitions ----

    /// <summary>The held definitions to review, oldest first, at most <see cref="MaxHeldCards"/>.</summary>
    public ObservableCollection<McpHeldViewModel> Held { get; } = [];

    public bool HasHeld => Held.Count > 0;

    /// <summary>"and 3 more held tools: see their servers' tables", when the cards are capped.</summary>
    public string HeldMore { get => _heldMore; private set { if (SetProperty(ref _heldMore, value)) OnPropertyChanged(nameof(HasHeldMore)); } }

    public bool HasHeldMore => _heldMore.Length > 0;

    // ---- Servers ----

    public ObservableCollection<McpServerRowViewModel> Servers { get; } = [];

    public McpServerRowViewModel? Selected
    {
        get => _selected;
        private set
        {
            if (ReferenceEquals(_selected, value))
                return;
            if (_selected is not null)
                _selected.IsSelected = false;
            _selected = value;
            if (_selected is not null)
                _selected.IsSelected = true;
            Confirm = McpConfirm.None;
            OnPropertyChanged();
            ShowDetail();
        }
    }

    public bool HasSelection => _selected is not null;

    public string DetailTitle => _selected?.Name ?? "";

    public string DetailTrust => _selected?.TrustText ?? "";

    public Tone DetailTrustTone => _selected?.TrustTone ?? Tone.Grey;

    public IReadOnlyList<ModelFact> Detail { get => _detail; private set => SetProperty(ref _detail, value); }

    public IReadOnlyList<McpToolRowViewModel> Tools { get => _tools; private set { if (SetProperty(ref _tools, value)) OnPropertyChanged(nameof(HasTools)); } }

    public bool HasTools => _tools.Count > 0;

    public string ToolsTitle => _selected is null ? "Tools" : $"Tools · {_selected.Name}";

    public string NoToolsText => _selected?.Record.State == McpServerState.Failed
        ? "It added no tools: it did not start."
        : "It has added no tools yet.";

    public bool CanTrust => _selected?.Record.Trust == McpTrust.Untrusted;

    public bool CanUntrust => _selected?.Record.Trust == McpTrust.Trusted;

    public bool CanForget => _selected?.Record.HasPins == true;

    // ---- Trust and forget, confirmed in the panel ----

    public McpConfirm Confirm
    {
        get => _confirm;
        private set
        {
            if (SetProperty(ref _confirm, value))
            {
                OnPropertyChanged(nameof(IsConfirmingTrust));
                OnPropertyChanged(nameof(IsConfirmingForget));
                OnPropertyChanged(nameof(ConfirmText));
                OnPropertyChanged(nameof(ConfirmWarning));
                OnPropertyChanged(nameof(HasConfirmWarning));
                TrustCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsConfirmingTrust => _confirm == McpConfirm.Trust;

    public bool IsConfirmingForget => _confirm == McpConfirm.Forget;

    /// <summary>
    /// What the answer does, with the server's command or address revealed (a server to trust is acted on in one click):
    /// "Trust **notes**? … It runs `notes-mcp --root ./notes`."
    /// </summary>
    public IReadOnlyList<TextRun> ConfirmText
    {
        get
        {
            if (_selected is not { Record: var server })
                return [];
            var name = Spans.Escape(SafeText.Reveal(server.Name));
            var target = Spans.Escape(SafeText.Reveal(server.Target));
            return Spans.Parse(_confirm switch
            {
                McpConfirm.Trust => $"Trust **{name}**? Its tools' hints are honoured, and its descriptions and results no longer taint the session. "
                    + $"It {(server.Transport == McpTransport.Stdio ? "runs" : "is at")} `{target}`.",
                McpConfirm.Forget => $"Forget **{name}**'s pins? Its next start pins whatever it lists (trust on first use). "
                    + "Tools this process registered or holds stay as they are until it restarts.",
                _ => "",
            });
        }
    }

    /// <summary>A server holding a hidden character in its name or command cannot be trusted from here.</summary>
    public string ConfirmWarning => _confirm == McpConfirm.Trust && _selected is { Record: var server } && SafeText.HasHidden(server.Name + server.Target)
        ? "Its name or command holds hidden characters, shown as ⟨U+…⟩. Correct the configuration first."
        : "";

    public bool HasConfirmWarning => ConfirmWarning.Length > 0;

    public RelayCommand AskTrustCommand { get; }

    public AsyncRelayCommand TrustCommand { get; }

    public AsyncRelayCommand UntrustCommand { get; }

    public RelayCommand AskForgetCommand { get; }

    public AsyncRelayCommand ForgetCommand { get; }

    public RelayCommand CancelConfirmCommand { get; }

    // ---- corvid mcp serve ----

    public IReadOnlyList<TextRun> Serve { get => _serve; private set => SetProperty(ref _serve, value); }

    public IReadOnlyList<string> ServeExposes { get => _serveExposes; private set => SetProperty(ref _serveExposes, value); }

    public string ServeTag => _snapshot?.Serve.Running == true ? "serving" : "off";

    // ---- Add a server ----

    public bool IsAdding { get => _adding; private set => SetProperty(ref _adding, value); }

    public RelayCommand AddServerCommand { get; }

    public RelayCommand CancelAddCommand { get; }

    public AsyncRelayCommand SaveServerCommand { get; }

    public IReadOnlyList<Choice> Transports { get; }

    public string DraftName { get => _draftName; set { if (SetProperty(ref _draftName, value ?? "")) DraftChanged(); } }

    public string DraftTarget { get => _draftTarget; set { if (SetProperty(ref _draftTarget, value ?? "")) DraftChanged(); } }

    public McpTransport DraftTransport
    {
        get => _draftTransport;
        set
        {
            if (!SetProperty(ref _draftTransport, value))
                return;
            Transports[0].IsSelected = value == McpTransport.Stdio;
            Transports[1].IsSelected = value == McpTransport.Http;
            OnPropertyChanged(nameof(TargetLabel));
            OnPropertyChanged(nameof(TargetPlaceholder));
            DraftChanged();
        }
    }

    public string TargetLabel => _draftTransport == McpTransport.Stdio ? "command" : "address";

    public string TargetPlaceholder => _draftTransport == McpTransport.Stdio ? "notes-mcp --root ./notes" : "https://mcp.example.com/tools";

    /// <summary>Trusted from the start; untrusted (the default) asks before every call and taints.</summary>
    public bool DraftTrusted { get => _draftTrusted; set => SetProperty(ref _draftTrusted, value); }

    public string DraftWarning { get => _draftWarning; private set { if (SetProperty(ref _draftWarning, value)) OnPropertyChanged(nameof(HasDraftWarning)); } }

    public bool HasDraftWarning => _draftWarning.Length > 0;

    // ---- Notice ----

    public IReadOnlyList<TextRun> Notice { get => _notice; private set { if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice)); } }

    public bool HasNotice => _notice.Count > 0;

    public Tone NoticeTone { get => _noticeTone; private set => SetProperty(ref _noticeTone, value); }

    public RelayCommand DismissNoticeCommand { get; }

    // ---- Keys ----

    /// <summary>Up and Down on the page: the previous or next server.</summary>
    public void MoveSelection(int delta)
    {
        if (Servers.Count == 0)
            return;
        var index = _selected is null ? (delta > 0 ? -1 : Servers.Count) : Servers.IndexOf(_selected);
        Selected = Servers[Math.Clamp(index + delta, 0, Servers.Count - 1)];
    }

    /// <summary>Esc: closes a question, then the form. False when there was nothing to close.</summary>
    public bool Close()
    {
        if (_confirm != McpConfirm.None)
        {
            Confirm = McpConfirm.None;
            return true;
        }

        if (IsAdding)
        {
            IsAdding = false;
            return true;
        }

        return false;
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
        McpSnapshot snapshot;
        try
        {
            snapshot = await _service.LoadAsync(CancellationToken.None).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Say(false, $"The MCP servers could not be loaded: {Spans.Escape(exception.Message)}");
            return;
        }

        if (_disposed)
            return;
        _snapshot = snapshot;
        SyncServers(snapshot);
        SyncHeld(snapshot);
        Subtitle = Spans.Parse(SubtitleText(snapshot));
        Serve = Spans.Parse(snapshot.Serve.Note);
        ServeExposes = [.. snapshot.Serve.Exposes.Select(SafeText.Clean)];
        OnPropertyChanged(nameof(ServeTag));
        _shell.SetBadge(Area.Mcp, snapshot.Held.Count);
        if (_active)
            ApplyStatus();
        if (_selected is null || !_rows.ContainsKey(_selected.Id))
            Selected = Landing();
        else
            ShowDetail();
    }

    private void SyncServers(McpSnapshot snapshot)
    {
        var wanted = new List<McpServerRowViewModel>();
        foreach (var server in snapshot.Servers)
        {
            if (!_rows.TryGetValue(server.Name, out var row))
                _rows[server.Name] = row = new McpServerRowViewModel(server, select => Selected = select);
            else
                row.Update(server);
            wanted.Add(row);
        }

        foreach (var gone in _rows.Keys.Except(wanted.Select(row => row.Id)).ToList())
            _rows.Remove(gone);
        if (!wanted.SequenceEqual(Servers))
        {
            Servers.Clear();
            foreach (var row in wanted)
                Servers.Add(row);
        }
    }

    /// <summary>The held cards, rebuilt once per change: a card's diff is made once, when its definition arrives.</summary>
    private void SyncHeld(McpSnapshot snapshot)
    {
        var now = _time.GetUtcNow();
        var zone = _time.LocalTimeZone;
        var wanted = snapshot.Held.OrderBy(held => held.KeptBlocked).ThenBy(held => held.HeldAt).Take(MaxHeldCards).ToList();
        var cards = wanted.Select(held => Held.FirstOrDefault(card => card.Held == held) ?? Card(held, now, zone)).ToList();
        if (!cards.SequenceEqual(Held))
        {
            Held.Clear();
            foreach (var card in cards)
                Held.Add(card);
        }

        var more = snapshot.Held.Count - wanted.Count;
        HeldMore = more > 0 ? $"and {more} more held tool{(more == 1 ? "" : "s")}: review them from their servers' tools" : "";
        OnPropertyChanged(nameof(HasHeld));
    }

    private McpHeldViewModel Card(McpHeldTool held, DateTimeOffset now, TimeZoneInfo zone)
    {
        var accept = new AsyncRelayCommand(token => AcceptAsync(held, token), () => !held.NeedsAllow && !SafeText.HasHidden(held.Definition.All));
        var allow = new AsyncRelayCommand(token => AllowAsync(held, token), () => held.NeedsAllow && !SafeText.HasHidden(held.Definition.All));
        var keep = new AsyncRelayCommand(token => KeepAsync(held, token), () => !held.KeptBlocked);
        foreach (var command in new[] { accept, allow, keep })
            ReportFailures(command);
        return new McpHeldViewModel(held, now, zone, accept, allow, keep);
    }

    /// <summary>Where the page lands: the server of the first held tool (the Board's "Diff"), else the first server.</summary>
    private McpServerRowViewModel? Landing()
    {
        if (Held.FirstOrDefault() is { } card && _rows.TryGetValue(card.Held.Server, out var row))
            return row;
        return Servers.FirstOrDefault();
    }

    private void ShowDetail()
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(DetailTitle));
        OnPropertyChanged(nameof(DetailTrust));
        OnPropertyChanged(nameof(DetailTrustTone));
        OnPropertyChanged(nameof(CanTrust));
        OnPropertyChanged(nameof(CanUntrust));
        OnPropertyChanged(nameof(CanForget));
        OnPropertyChanged(nameof(ToolsTitle));
        OnPropertyChanged(nameof(NoToolsText));
        OnPropertyChanged(nameof(ConfirmText));
        OnPropertyChanged(nameof(ConfirmWarning));
        OnPropertyChanged(nameof(HasConfirmWarning));
        AskTrustCommand.NotifyCanExecuteChanged();
        AskForgetCommand.NotifyCanExecuteChanged();
        foreach (var command in new[] { TrustCommand, UntrustCommand, ForgetCommand })
            command.NotifyCanExecuteChanged();
        if (_selected is not { Record: var server })
        {
            Detail = [];
            Tools = [];
            return;
        }

        var facts = new List<ModelFact>
        {
            Fact("transport", McpText.Transport(server.Transport)),
            Fact(server.Transport == McpTransport.Stdio ? "runs" : "address", $"`{Spans.Escape(SafeText.Clean(server.Target))}`"),
            Fact("state", McpText.State(server.State) + (server.Since is { } since ? " since " + Jobs.JobsText.When(since, _time.GetUtcNow(), _time.LocalTimeZone) : "")),
        };
        if (server.Detail.Length > 0)
            facts.Add(Fact("detail", Spans.Escape(SafeText.Clean(server.Detail))));
        facts.Add(Fact("trust", server.Trust == McpTrust.Trusted
            ? "trusted: its hints are honoured; calls follow the approval mode"
            : "untrusted: each call asks, and its descriptions and results taint the session"));
        Detail = facts;
        Tools = [.. server.Tools.Select(tool => new McpToolRowViewModel(tool, Review))];
    }

    private static ModelFact Fact(string key, string markup) => new(key, Spans.Parse(markup));

    /// <summary>A held tool's Review: its card shows its diff again, first.</summary>
    private void Review(McpToolRecord tool)
    {
        if (Held.FirstOrDefault(card => card.Held.Server == tool.Server && card.Held.Tool == tool.Name) is { } card)
        {
            card.ShowDiff = true;
            ReviewRequested?.Invoke(this, card);
        }
    }

    /// <summary>A held card should come into view (the tools table's Review); the page scrolls to it.</summary>
    public event EventHandler<McpHeldViewModel>? ReviewRequested;

    private static string SubtitleText(McpSnapshot snapshot)
    {
        var held = snapshot.Held.Count;
        var servers = snapshot.Servers.Count;
        return $"{servers} server{(servers == 1 ? "" : "s")} · {(held == 0 ? "no tool held" : held == 1 ? "1 tool held" : $"{held} tools held")} · "
            + $"pins in `{Spans.Escape(SafeText.Clean(snapshot.PinsPath))}`";
    }

    // ---- Actions ----

    private async Task AcceptAsync(McpHeldTool held, CancellationToken cancellationToken)
    {
        await _service.AcceptAsync(held.Server, held.Tool, held.Hash, cancellationToken).ConfigureAwait(true);
        Say(true, $"Pinned **{Spans.Escape(SafeText.Clean(held.Tool))}** at `{Spans.Escape(SafeText.Clean(held.Hash))}`: it is offered from the next turn.");
    }

    private async Task AllowAsync(McpHeldTool held, CancellationToken cancellationToken)
    {
        await _service.AllowAsync(held.Server, held.Tool, held.Hash, cancellationToken).ConfigureAwait(true);
        Say(true, $"Allowed **{Spans.Escape(SafeText.Clean(held.Tool))}** as an untrusted tool: it asks before every call.");
    }

    private async Task KeepAsync(McpHeldTool held, CancellationToken cancellationToken)
    {
        await _service.KeepBlockedAsync(held.Server, held.Tool, cancellationToken).ConfigureAwait(true);
        Say(true, $"Kept **{Spans.Escape(SafeText.Clean(held.Tool))}** blocked: it stays held and is not offered. Accept it later from here.");
    }

    private async Task TrustAsync(CancellationToken cancellationToken)
    {
        if (_selected is not { } row)
            return;
        Confirm = McpConfirm.None;
        await _service.SetTrustAsync(row.Id, McpTrust.Trusted, cancellationToken).ConfigureAwait(true);
        Say(true, $"**{Spans.Escape(row.Name)}** is trusted: its hints are honoured from the next session.");
    }

    private async Task UntrustAsync(CancellationToken cancellationToken)
    {
        if (_selected is not { } row)
            return;
        await _service.SetTrustAsync(row.Id, McpTrust.Untrusted, cancellationToken).ConfigureAwait(true);
        Say(true, $"**{Spans.Escape(row.Name)}** is untrusted: each of its calls asks, and its results taint the session.");
    }

    private async Task ForgetAsync(CancellationToken cancellationToken)
    {
        if (_selected is not { } row)
            return;
        Confirm = McpConfirm.None;
        var count = await _service.ForgetAsync(row.Id, cancellationToken).ConfigureAwait(true);
        Say(true, $"Forgot **{Spans.Escape(row.Name)}**'s {count} pinned definition{(count == 1 ? "" : "s")}: its next start pins whatever it lists.");
    }

    private void OpenForm()
    {
        DraftName = "";
        DraftTarget = "";
        DraftTransport = McpTransport.Stdio;
        DraftTrusted = false;
        IsAdding = true;
    }

    private void DraftChanged()
    {
        var hidden = SafeText.HasHidden(_draftName) ? ("name", _draftName) : SafeText.HasHidden(_draftTarget) ? (TargetLabel, _draftTarget) : default;
        DraftWarning = hidden.Item1 is null ? ""
            : $"The {hidden.Item1} holds hidden characters, shown here: {SafeText.Reveal(hidden.Item2)}. Remove them to add the server.";
        SaveServerCommand.NotifyCanExecuteChanged();
    }

    private bool CanSave() => _draftName.Trim().Length > 0 && _draftTarget.Trim().Length > 0 && !HasDraftWarning;

    private async Task SaveServerAsync(CancellationToken cancellationToken)
    {
        var server = await _service.AddServerAsync(new McpServerDraft(_draftName, _draftTransport, _draftTarget,
            _draftTrusted ? McpTrust.Trusted : McpTrust.Untrusted), cancellationToken).ConfigureAwait(true);
        IsAdding = false;
        if (_rows.TryGetValue(server.Name, out var row))
            Selected = row;
        Say(true, $"Added **{Spans.Escape(SafeText.Clean(server.Name))}**: it starts with the next session, and its tools are pinned as it first lists them.");
    }

    // ---- Shell, status bar, notices ----

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.CurrentArea))
            UpdateActive();
    }

    private void UpdateActive()
    {
        var active = _shell.CurrentArea == Area.Mcp;
        if (active == _active)
            return;
        _active = active;
        if (active)
        {
            _lease.Take();
            ApplyStatus();
            // Arriving (from the rail or the Board's "Diff") lands on the held tool's server.
            if (Held.Count > 0 && _rows.TryGetValue(Held[0].Held.Server, out var row))
                Selected = row;
            return;
        }

        Confirm = McpConfirm.None;
        _lease.Release();
    }

    private void ApplyStatus()
    {
        if (_snapshot is not { } snapshot)
            return;
        var running = snapshot.Servers.Count(server => server.State == McpServerState.Running);
        _lease.AreaFields =
        [
            new StatusField("mcp", $"{running} of {snapshot.Servers.Count} running · {snapshot.Held.Count} held"),
        ];
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

/// <summary>The question the server panel is asking, if any.</summary>
public enum McpConfirm
{
    None,
    Trust,
    Forget,
}
