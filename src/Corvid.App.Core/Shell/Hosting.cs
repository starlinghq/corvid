using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Shell;

/// <summary>What the app can host for the machine (U-8): the gateway and the scheduler.</summary>
public enum HostedPart
{
    Gateway,
    Scheduler,
}

/// <summary>
/// What the app hosts: each part asked for (<c>Corvid:App:HostGateway</c>, <c>Corvid:App:HostScheduler</c>, or Start
/// hosting) and whether it runs here now. A part asked for while another process holds its lock is taken over when that
/// process lets go.
/// </summary>
public sealed record HostingState(bool GatewayOn, bool GatewayHere, bool SchedulerOn, bool SchedulerHere)
{
    public static HostingState None { get; } = new(false, false, false, false);

    public bool On(HostedPart part) => part == HostedPart.Gateway ? GatewayOn : SchedulerOn;

    public bool Here(HostedPart part) => part == HostedPart.Gateway ? GatewayHere : SchedulerHere;

    /// <summary>The status bar's notice while this app hosts either ("gateway · scheduler"); empty when it hosts neither.</summary>
    public string Notice => (GatewayHere, SchedulerHere) switch
    {
        (true, true) => "gateway · scheduler",
        (true, false) => "gateway",
        (false, true) => "scheduler",
        _ => "",
    };
}

/// <summary>
/// The app's hosting of the gateway and the scheduler (U-8): off until the person turns it on, here (with one question
/// first) or in Settings. The engine implements it over its hosted gateway and the runtime's scheduler; the demo hosts
/// nothing.
/// </summary>
public interface IHostingService
{
    /// <summary>Why this window cannot host anything (the demo, no engine); null when it can.</summary>
    string? Unavailable { get; }

    HostingState State { get; }

    /// <summary>
    /// Turns hosting of <paramref name="part"/> on or off, for this run and in <c>corvid.json</c>: on takes the lock (or waits
    /// for the process that holds it), off stops it and releases the lock. Returns what happened, in a sentence.
    /// </summary>
    Task<string> SetAsync(HostedPart part, bool on, CancellationToken cancellationToken);

    /// <summary>What it hosts may have changed. Raised on the UI thread.</summary>
    event EventHandler? Changed;
}

/// <summary>A window that hosts nothing: the demo, or no engine.</summary>
public sealed class NoHostingService(string reason) : IHostingService
{
    public string? Unavailable { get; } = reason;

    public HostingState State => HostingState.None;

    public Task<string> SetAsync(HostedPart part, bool on, CancellationToken cancellationToken) =>
        Task.FromException<string>(new InvalidOperationException(Unavailable));

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}

/// <summary>
/// The Start hosting / Stop hosting control on the Gateway and Jobs pages: Start asks once, naming what hosting does, and
/// Stop stops at once. What happened goes to the page's notice.
/// </summary>
public sealed class HostingToggleViewModel : ObservableObject, IDisposable
{
    private readonly IHostingService _service;
    private readonly Action<bool, string> _say;
    private bool _asking;

    public HostingToggleViewModel(IHostingService service, HostedPart part, Action<bool, string> say)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(say);
        _service = service;
        _say = say;
        Part = part;
        AskCommand = new RelayCommand(() => IsAsking = true, () => CanHost && !IsOn);
        CancelCommand = new RelayCommand(() => IsAsking = false);
        StartCommand = new AsyncRelayCommand(token => SetAsync(true, token), () => CanHost && !IsOn);
        StopCommand = new AsyncRelayCommand(token => SetAsync(false, token), () => CanHost && IsOn);
        foreach (var command in new[] { StartCommand, StopCommand })
            command.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AsyncRelayCommand.Error) && command.Error is { } error)
                    _say(false, $"That did not work: {Spans.Escape(error.Message)}");
            };
        _service.Changed += OnChanged;
    }

    public HostedPart Part { get; }

    public bool CanHost => _service.Unavailable is null;

    /// <summary>Hosting is asked for (it may still wait for another process to let go).</summary>
    public bool IsOn => _service.State.On(Part);

    public bool ShowStart => CanHost && !IsOn && !IsAsking;

    public bool ShowStop => CanHost && IsOn;

    public bool IsAsking
    {
        get => _asking;
        private set
        {
            if (SetProperty(ref _asking, value && CanHost && !IsOn))
                OnPropertyChanged(nameof(ShowStart));
        }
    }

    public string StartLabel => Part == HostedPart.Gateway ? "Start hosting the gateway…" : "Start hosting the scheduler…";

    public string StopLabel => Part == HostedPart.Gateway ? "Stop hosting the gateway" : "Stop hosting the scheduler";

    /// <summary>The one question Start asks: what hosting does, before it does it.</summary>
    public string Question => Part == HostedPart.Gateway
        ? "Host the gateway in this app? It connects every enabled adapter (Telegram, Discord, Slack) with its token from the Vault, "
          + "and their chats' messages run turns here while the app is open. It is remembered as Corvid:App:HostGateway; Stop hosting turns it off."
        : "Host the scheduler in this app? It runs scheduled jobs when they are due, each with its tools (a job's prompt runs as a turn, "
          + "its script through the sandbox), while the app is open. It is remembered as Corvid:App:HostScheduler; Stop hosting turns it off.";

    public RelayCommand AskCommand { get; }

    public RelayCommand CancelCommand { get; }

    public AsyncRelayCommand StartCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    /// <summary>Esc: closes the question. False when it was not open.</summary>
    public bool Close()
    {
        var open = IsAsking;
        IsAsking = false;
        return open;
    }

    public void Dispose() => _service.Changed -= OnChanged;

    private async Task SetAsync(bool on, CancellationToken cancellationToken)
    {
        IsAsking = false;
        var said = await _service.SetAsync(Part, on, cancellationToken).ConfigureAwait(true);
        _say(true, Spans.Escape(said));
        OnChanged(this, EventArgs.Empty);
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (IsOn)
            IsAsking = false;
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(ShowStart));
        OnPropertyChanged(nameof(ShowStop));
        AskCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }
}
