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

    internal PairingRequestViewModel(PairingRequest request, DateTimeOffset now, TimeZoneInfo zone, ICommand guest, ICommand owner, ICommand ignore)
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
        OwnerCommand = owner;
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

    public ICommand OwnerCommand { get; }

    public ICommand IgnoreCommand { get; }

    public RelayCommand AskOwnerCommand { get; }

    public RelayCommand CancelOwnerCommand { get; }

    public bool IsConfirmingOwner { get => _confirmingOwner; set => SetProperty(ref _confirmingOwner, value); }

    public IReadOnlyList<TextRun> OwnerQuestion => Spans.Parse(
        $"Pair **{Spans.Escape(Handle)}** as an owner? An owner answers approvals and runs commands from {Spans.Escape(Channel)}, as you do here.");

    public string GuestName => $"Pair {Handle} as a guest";

    public string OwnerName => $"Pair {Handle} as an owner";

    public string IgnoreName => $"Ignore {Handle}'s code";

    public string AutomationName => $"{Title}, code {Code}. {Note}";

    internal void CloseQuestion() => IsConfirmingOwner = false;
}

/// <summary>A tool a guest can be given, ticked when it is on the allowlist being edited.</summary>
public sealed class GuestToolChoice(string name, bool isChecked) : ObservableObject
{
    private bool _isChecked = isChecked;

    public string Name { get; } = SafeText.Clean(name);

    internal string Raw { get; } = name;

    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }
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

    internal PairedUserViewModel(PairedUser user, DateTimeOffset now, TimeZoneInfo zone, IReadOnlyList<string> guestTools,
        Func<PairedUserViewModel, IReadOnlyList<string>, Task> save, Func<PairedUserViewModel, Task> revoke, Action<AsyncRelayCommand> report)
    {
        User = user;
        Handle = SafeText.Clean(user.Handle);
        Channel = GatewayText.Channel(user.Adapter);
        Since = JobsText.When(user.Since, now, zone);
        _guestTools = guestTools;
        EditCommand = new RelayCommand(() =>
        {
            Choices = [.. _guestTools.Select(tool => new GuestToolChoice(tool, User.Allowlist.Contains(tool)))];
            IsEditing = true;
        }, () => IsGuest);
        SaveCommand = new AsyncRelayCommand(async () =>
        {
            await save(this, [.. Choices.Where(choice => choice.IsChecked).Select(choice => choice.Raw)]).ConfigureAwait(true);
            IsEditing = false;
        });
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

    private readonly IReadOnlyList<string> _guestTools;

    public PairedUser User { get; }

    public string Handle { get; }

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
