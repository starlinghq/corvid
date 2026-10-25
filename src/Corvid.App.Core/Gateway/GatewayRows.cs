using System.Globalization;
using System.Windows.Input;
using Corvid.App.Core.Jobs;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Gateway;

/// <summary>
/// A pairing request's card (F16): the channel, the sender's handle, the code to match against the one the bot sent, and
/// the answers. The handle comes from the channel and is what the person approves in one click, so beside the buttons it is
/// revealed (⟨U+202E⟩ shows), and a handle holding a hidden character can only be ignored. Pairing as owner asks first: an
/// owner answers approvals from the chat.
/// </summary>
public sealed class PairingRequestViewModel : ObservableObject
{
    private bool _confirmingOwner;

    internal PairingRequestViewModel(PairingRequest request, DateTimeOffset now, TimeZoneInfo zone, ICommand guest,
        Func<CancellationToken, Task> pairOwner, ICommand ignore, Action<AsyncRelayCommand> report)
    {
        Request = request;
        Channel = GatewayText.Channel(request.Adapter);
        Handle = SafeText.Reveal(request.Handle);
        Code = SafeText.Clean(request.Code);
        HasHidden = SafeText.HasHidden(request.Handle);
        Title = $"Pairing · {Channel} · {Handle}";
        Note = $"{JobsText.When(request.At, now, zone)} · {SafeText.Clean(request.Chat)} · approved on this machine only, never from a chat";
        Warning = HasHidden ? "The handle holds hidden characters, shown as ⟨U+…⟩: it may not be who it reads as. It can only be ignored." : "";
        GuestCommand = guest;
        // Pairing as owner is the question's answer only: it does nothing until the question is open.
        OwnerCommand = new AsyncRelayCommand(pairOwner, () => !HasHidden && IsConfirmingOwner);
        report(OwnerCommand);
        IgnoreCommand = ignore;
        AskOwnerCommand = new RelayCommand(() => IsConfirmingOwner = true, () => !HasHidden);
        CancelOwnerCommand = new RelayCommand(() => IsConfirmingOwner = false);
    }

    public PairingRequest Request { get; }

    public string Channel { get; }

    /// <summary>The handle revealed: what pairing would pair.</summary>
    public string Handle { get; }

    public string Code { get; }

    public string Title { get; }

    public string Note { get; }

    public bool HasHidden { get; }

    public string Warning { get; }

    public bool CanPair => !HasHidden;

    public ICommand GuestCommand { get; }

    public AsyncRelayCommand OwnerCommand { get; }

    public ICommand IgnoreCommand { get; }

    public RelayCommand AskOwnerCommand { get; }

    public RelayCommand CancelOwnerCommand { get; }

    /// <summary>The owner question is open. It never opens for a handle that hides a character.</summary>
    public bool IsConfirmingOwner
    {
        get => _confirmingOwner;
        set
        {
            if (SetProperty(ref _confirmingOwner, value && !HasHidden))
                OwnerCommand.NotifyCanExecuteChanged();
        }
    }

    public IReadOnlyList<TextRun> OwnerQuestion => Spans.Parse(
        $"Pair **{Spans.Escape(Handle)}** as an owner? An owner answers approvals and runs commands from {Spans.Escape(Channel)}, as you do here.");

    public string GuestName => $"Pair {Handle} as a guest";

    public string OwnerName => $"Pair {Handle} as an owner";

    /// <summary>The button that opens the owner question, named apart from the question's answer.</summary>
    public string AskOwnerName => $"Ask before pairing {Handle} as an owner";

    public string IgnoreName => $"Ignore {Handle}'s code";

    public string AutomationName => $"{Title}, code {Code}. {Note}";

    internal void CloseQuestion() => IsConfirmingOwner = false;
}

/// <summary>
/// A tool a guest can be given, ticked when it is on the allowlist being edited. Ticking it grants the tool in one click,
/// and tool names come from MCP servers too, so the name is revealed (⟨U+202E⟩ shows) and a name holding a hidden
/// character cannot be ticked: it may not be the tool it reads as. One already on the allowlist starts ticked and can only
/// be unticked.
/// </summary>
public sealed class GuestToolChoice(string name, bool isChecked) : ObservableObject
{
    private bool _isChecked = isChecked;

    /// <summary>The name revealed: what ticking it would grant.</summary>
    public string Name { get; } = SafeText.Reveal(name);

    internal string Raw { get; } = name;

    public bool HasHidden { get; } = SafeText.HasHidden(name);

    /// <summary>Whether the box takes a click: always, unless the name hides something and the box is not ticked.</summary>
    public bool CanToggle => !HasHidden || _isChecked;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            // A name that hides a character is never granted, whatever the binding sends.
            if (value && HasHidden)
            {
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _isChecked, value))
                OnPropertyChanged(nameof(CanToggle));
        }
    }
}

/// <summary>
/// A paired user: handle, channel, role and since, and for a guest the tool allowlist (empty by default) with Widen… to
/// edit it; Revoke… unpairs, after asking.
/// </summary>
public sealed class PairedUserViewModel : ObservableObject
{
    private bool _editing;
    private bool _confirmingRevoke;
    private IReadOnlyList<GuestToolChoice> _choices = [];

    internal PairedUserViewModel(PairedUser user, DateTimeOffset now, TimeZoneInfo zone, Func<IReadOnlyList<string>> guestTools,
        Func<PairedUserViewModel, IReadOnlyList<string>, Task> save, Func<PairedUserViewModel, Task> revoke, Action<AsyncRelayCommand> report)
    {
        User = user;
        Handle = SafeText.Clean(user.Handle);
        HandleHasHidden = SafeText.HasHidden(user.Handle);
        RevealedHandle = SafeText.Reveal(user.Handle);
        Channel = GatewayText.Channel(user.Adapter);
        Since = JobsText.When(user.Since, now, zone);
        _guestTools = guestTools;
        EditCommand = new RelayCommand(() =>
        {
            foreach (var old in Choices)
                old.PropertyChanged -= OnChoiceChanged;
            Choices = [.. _guestTools().Select(tool => new GuestToolChoice(tool, User.Allowlist.Contains(tool)))];
            foreach (var choice in Choices)
                choice.PropertyChanged += OnChoiceChanged;
            OnPropertyChanged(nameof(HasHiddenChoice));
            OnPropertyChanged(nameof(HiddenWarning));
            RefreshSave();
            IsEditing = true;
        }, () => IsGuest && !HandleHasHidden);
        SaveCommand = new AsyncRelayCommand(async () =>
        {
            if (HandleHasHidden)
                throw new InvalidOperationException("The guest's handle holds hidden characters; their tools cannot be widened. Revoke them instead.");
            if (Choices.Any(choice => choice.IsChecked && choice.HasHidden))
                throw new InvalidOperationException("A ticked tool's name holds hidden characters; untick it before saving.");
            await save(this, [.. Choices.Where(choice => choice.IsChecked).Select(choice => choice.Raw)]).ConfigureAwait(true);
            IsEditing = false;
        }, () => !HandleHasHidden && !Choices.Any(choice => choice.IsChecked && choice.HasHidden));
        CancelEditCommand = new RelayCommand(() => IsEditing = false);
        AskRevokeCommand = new RelayCommand(() => IsConfirmingRevoke = true);
        RevokeCommand = new AsyncRelayCommand(async () =>
        {
            IsConfirmingRevoke = false;
            await revoke(this).ConfigureAwait(true);
        });
        CancelRevokeCommand = new RelayCommand(() => IsConfirmingRevoke = false);
        report(SaveCommand);
        report(RevokeCommand);
    }

    /// <summary>The tools guests can be given now, read when the editor opens (an MCP server can add one meanwhile).</summary>
    private readonly Func<IReadOnlyList<string>> _guestTools;

    public PairedUser User { get; }

    public string Handle { get; }

    /// <summary>The handle with every hidden character shown (⟨U+202E⟩): the editor names whom it widens this way.</summary>
    public string RevealedHandle { get; }

    /// <summary>The handle hides a character: it may not be who it reads as, so its tools are never widened from here.</summary>
    public bool HandleHasHidden { get; }

    /// <summary>"Tools @river may use. …": the editor's heading, naming the guest as they would be granted.</summary>
    public string EditHeading => $"Tools {RevealedHandle} may use. Each still follows the approval mode.";

    public string Channel { get; }

    public string Since { get; }

    public bool IsGuest => User.Role == PairingRole.Guest;

    public string RoleText => IsGuest ? "guest" : "owner";

    public Tone RoleTone => IsGuest ? Tone.Grey : Tone.Violet;

    /// <summary>"memory_search", "none: it can only talk", or "every tool" for an owner.</summary>
    public string ToolsText => !IsGuest ? "every tool, and approvals"
        : User.Allowlist.Count == 0 ? "none: it can only talk"
        : string.Join(", ", User.Allowlist.Select(SafeText.Clean));

    public bool ToolsEmpty => IsGuest && User.Allowlist.Count == 0;

    public RelayCommand EditCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public RelayCommand CancelEditCommand { get; }

    public bool IsEditing { get => _editing; private set => SetProperty(ref _editing, value); }

    public IReadOnlyList<GuestToolChoice> Choices { get => _choices; private set => SetProperty(ref _choices, value); }

    /// <summary>A tool offered in the editor has a name that hides characters: it cannot be ticked.</summary>
    public bool HasHiddenChoice => _choices.Any(choice => choice.HasHidden);

    public string HiddenWarning => HasHiddenChoice
        ? "A tool's name holds hidden characters, shown as ⟨U+…⟩: it may not be the tool it reads as, so it cannot be ticked. Untick it if it is on the allowlist."
        : "";

    private void OnChoiceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GuestToolChoice.IsChecked))
            RefreshSave();
    }

    private void RefreshSave() => SaveCommand.NotifyCanExecuteChanged();

    public RelayCommand AskRevokeCommand { get; }

    public AsyncRelayCommand RevokeCommand { get; }

    public RelayCommand CancelRevokeCommand { get; }

    public bool IsConfirmingRevoke { get => _confirmingRevoke; private set => SetProperty(ref _confirmingRevoke, value); }

    public IReadOnlyList<TextRun> RevokeQuestion => Spans.Parse(
        $"Revoke **{Spans.Escape(Handle)}**? Their next message gets a new code to approve here.");

    public string EditName => $"Widen {Handle}'s tools";

    public string RevokeName => $"Revoke {Handle}";

    public string AutomationName => $"{Handle} on {Channel}, {RoleText} since {Since}, tools: {ToolsText}";

    internal bool CloseQuestions()
    {
        var any = _editing || _confirmingRevoke;
        IsEditing = false;
        IsConfirmingRevoke = false;
        return any;
    }
}

/// <summary>A channel adapter's row: name, state, since, the bot's account and what to know.</summary>
public sealed record AdapterRow(string Name, string StateText, Tone StateTone, string Since, string Account, string Detail)
{
    public bool HasDetail => Detail.Length > 0;

    public bool HasAccount => Account.Length > 0;

    public string AutomationName => $"{Name}, {StateText}{(Since.Length > 0 ? " since " + Since : "")}. {Account} {Detail}";
}

/// <summary>A line of the deliveries or overrides lists: what, then the detail under it.</summary>
public sealed record GatewayLine(string What, string Detail)
{
    public string AutomationName => $"{What}. {Detail}";
}

/// <summary>
/// What a link into Gateway names (<see cref="Shell.AreaLink.Name"/>): a pairing request's card, or that card with the
/// owner question open. The Board's tray leads here with one; Gateway reads it back.
/// </summary>
public static class GatewayLinks
{
    private const string OwnerSuffix = "#owner";

    /// <summary>Lands on the request's card.</summary>
    public static string Card(string requestId) => requestId;

    /// <summary>Lands on the request's card with the owner question open.</summary>
    public static string Owner(string requestId) => requestId + OwnerSuffix;

    /// <summary>The request a link names, and whether it asks the owner question.</summary>
    public static (string RequestId, bool Owner) Read(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name.EndsWith(OwnerSuffix, StringComparison.Ordinal) ? (name[..^OwnerSuffix.Length], true) : (name, false);
    }
}

/// <summary>The Gateway page's wording.</summary>
public static class GatewayText
{
    /// <summary>"Telegram", "Discord", "Slack": the channel's name as a person writes it.</summary>
    public static string Channel(string adapter)
    {
        var clean = SafeText.Clean(adapter);
        return clean.Length == 0 ? "" : char.ToUpper(clean[0], CultureInfo.InvariantCulture) + clean[1..];
    }

    public static string State(AdapterState state) => state switch
    {
        AdapterState.Up => "up",
        AdapterState.Connecting => "connecting",
        AdapterState.Down => "down",
        AdapterState.Unknown => "not hosted here",
        _ => "off",
    };

    public static Tone StateTone(AdapterState state) => state switch
    {
        AdapterState.Up => Tone.Green,
        AdapterState.Connecting => Tone.Blue,
        AdapterState.Down => Tone.Red,
        _ => Tone.Grey,
    };

    /// <summary>"telegram up · discord up · slack off", the status bar's gateway item.</summary>
    public static string Summary(IEnumerable<GatewayAdapter> adapters) =>
        string.Join(" · ", adapters.Select(adapter => $"{SafeText.Clean(adapter.Name)} {State(adapter.State)}"));
}
