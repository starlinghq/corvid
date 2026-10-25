using Corvid.App.Core;
using Corvid.App.Core.Board;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Gateway;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class GatewayTests
{
    private sealed record Rig(GatewayViewModel Gateway, DemoGatewayService Service, DemoBoardService Board, ShellViewModel Shell, StatusBarViewModel Bar);

    private static Rig Build()
    {
        var time = ManualTimeProvider.Demo();
        var board = new DemoBoardService(time);
        var service = new DemoGatewayService(time, board);
        var bar = new StatusBarViewModel { Gateway = "before" };
        var shell = new ShellViewModel(bar);
        shell.Navigate(Area.Gateway);
        return new Rig(new GatewayViewModel(service, shell, bar, time), service, board, shell, bar);
    }

    private static string Text(IEnumerable<TextRun> runs) => string.Concat(runs.Select(run => run.Text));

    [Fact]
    public void ItShowsTheBoardsPairingRequestTheUsersAndTheChannels()
    {
        var rig = Build();
        var gateway = rig.Gateway;

        var request = Assert.Single(gateway.Requests);
        Assert.Equal("Pairing · Telegram · @dana", request.Title);
        Assert.Equal("ABCD-EFGH", request.Code);
        Assert.Equal("today 08:40 · direct message · approved on this machine only, never from a chat", request.Note);
        Assert.True(request.CanPair);
        Assert.True(request.GuestCommand.CanExecute(null));

        Assert.Equal(["@kestrel", "river#2210"], gateway.Users.Select(user => user.Handle));
        Assert.Equal("every tool, and approvals", gateway.Users[0].ToolsText);
        Assert.Equal("memory_search", gateway.Users[1].ToolsText);
        Assert.Equal(["Telegram", "Discord", "Slack"], gateway.Adapters.Select(adapter => adapter.Name));
        Assert.Equal(["up", "up", "off"], gateway.Adapters.Select(adapter => adapter.StateText));
        Assert.Contains("slack.bot-token", gateway.Adapters[2].Detail, StringComparison.Ordinal);
        Assert.Equal("cli-a410's release-notes summary → Slack #release", Assert.Single(gateway.Deliveries).What);
        Assert.Equal("in this app", gateway.HostTag);
        Assert.Equal("hosted in this app · telegram up · discord up · slack off", Text(gateway.Subtitle));
        Assert.Equal("telegram up · discord up · slack off", rig.Bar.Gateway);

        rig.Shell.Navigate(Area.Board);
        Assert.Equal("before", rig.Bar.Gateway);
    }

    /// <summary>A service with one snapshot, for where the gateway runs (G-14).</summary>
    private sealed class FixedGateway(GatewayHost host) : IGatewayService
    {
        public event EventHandler? Changed { add { } remove { } }

        public Task<GatewaySnapshot> LoadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new GatewaySnapshot(host, [new GatewayAdapter("telegram", AdapterState.Unknown)], [], [], [], [], []));

        public Task<PairedUser> PairAsync(string requestId, PairingRole role, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task IgnoreAsync(string requestId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task RevokeAsync(string userId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<PairedUser> SetAllowlistAsync(string userId, IReadOnlyList<string> tools, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static GatewayViewModel Showing(GatewayHost host)
    {
        var time = ManualTimeProvider.Demo();
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        shell.Navigate(Area.Gateway);
        return new GatewayViewModel(new FixedGateway(host), shell, bar, time);
    }

    [Fact]
    public void ThePageSaysWhichProcessHostsTheGatewayOrWhyNoneDoes()
    {
        using var elsewhere = Showing(new GatewayHost(false, "corvid gateway (pid 4127)", ManualTimeProvider.Demo().GetUtcNow().AddMinutes(-5)));
        Assert.Equal("elsewhere", elsewhere.HostTag);
        Assert.StartsWith("corvid gateway (pid 4127) hosts the gateway since ", Text(elsewhere.HostText), StringComparison.Ordinal);
        Assert.StartsWith("hosted by corvid gateway (pid 4127) · ", Text(elsewhere.Subtitle), StringComparison.Ordinal);

        // G-14: nobody hosts it, and the page says why rather than naming a holder.
        using var nowhere = Showing(new GatewayHost(false, "", null) { Reason = "No gateway adapter is enabled." });
        Assert.Equal("off", nowhere.HostTag);
        Assert.StartsWith("No process hosts the gateway: No gateway adapter is enabled.", Text(nowhere.HostText), StringComparison.Ordinal);
        Assert.StartsWith("not running · ", Text(nowhere.Subtitle), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PairingAsAGuestGivesNoToolsAndAnswersTheBoardsItem()
    {
        var rig = Build();

        await ((AsyncRelayCommand)rig.Gateway.Requests[0].GuestCommand).ExecuteAsync();

        Assert.Empty(rig.Gateway.Requests);
        var dana = Assert.Single(rig.Gateway.Users, user => user.Handle == "@dana");
        Assert.Equal("guest", dana.RoleText);
        Assert.Equal("none: it can only talk", dana.ToolsText);
        Assert.True(dana.ToolsEmpty);
        Assert.Equal("Paired @dana as a guest, with no tools: they can talk to Corvid. Widen… gives them tools.", Text(rig.Gateway.Notice));
        Assert.DoesNotContain(await rig.Board.ListAttentionAsync(TestContext.Current.CancellationToken), item => item.Id == DemoBoardService.PairingItemId);
    }

    [Fact]
    public async Task TheTrayPairsNoOneButItsIgnoreIsFollowedHere()
    {
        var rig = Build();

        await rig.Board.ResolveAsync(DemoBoardService.PairingItemId, "guest", TestContext.Current.CancellationToken);
        Assert.Single(rig.Gateway.Requests);
        Assert.DoesNotContain(rig.Gateway.Users, user => user.Handle == "@dana");

        await rig.Board.ResolveAsync(DemoBoardService.PairingItemId, "ignore", TestContext.Current.CancellationToken);
        Assert.Empty(rig.Gateway.Requests);
        Assert.DoesNotContain(rig.Gateway.Users, user => user.Handle == "@dana");
    }

    [Fact]
    public void TheTraysOwnerLinkArrivesWithTheQuestionOpenEvenBeforeTheAreaExists()
    {
        var time = ManualTimeProvider.Demo();
        var board = new DemoBoardService(time);
        var service = new DemoGatewayService(time, board);
        var bar = new StatusBarViewModel();
        var shell = new ShellViewModel(bar);
        shell.Navigate(Area.Board);

        shell.Follow(new AreaLink(Area.Gateway, null) { Name = GatewayLinks.Owner(DemoGatewayService.DanaRequestId) });
        var gateway = new GatewayViewModel(service, shell, bar, time);

        var card = Assert.Single(gateway.Requests);
        Assert.True(card.IsConfirmingOwner);
        Assert.True(card.OwnerCommand.CanExecute(null));
        Assert.Same(card, gateway.TakePendingLanding());
        Assert.Null(gateway.PendingLanding);
    }

    [Fact]
    public void TheTraysGuestLinkLandsOnTheCardWithoutTheQuestion()
    {
        var rig = Build();
        rig.Shell.Navigate(Area.Board);

        rig.Shell.Follow(new AreaLink(Area.Gateway, null) { Name = GatewayLinks.Card(DemoGatewayService.DanaRequestId) });

        var card = Assert.Single(rig.Gateway.Requests);
        Assert.False(card.IsConfirmingOwner);
        Assert.Same(card, rig.Gateway.PendingLanding);

        rig.Shell.Navigate(Area.Board);
        rig.Shell.Follow(new AreaLink(Area.Gateway, null) { Name = GatewayLinks.Owner(DemoGatewayService.DanaRequestId) });
        Assert.True(card.IsConfirmingOwner);
    }

    [Fact]
    public void PairingAsOwnerDoesNothingUntilTheQuestionIsOpen()
    {
        var rig = Build();
        var card = rig.Gateway.Requests[0];

        Assert.False(card.OwnerCommand.CanExecute(null));
        Assert.NotEqual(card.AskOwnerName, card.OwnerName);
        card.AskOwnerCommand.Execute(null);
        Assert.True(card.OwnerCommand.CanExecute(null));
        card.CancelOwnerCommand.Execute(null);
        Assert.False(card.OwnerCommand.CanExecute(null));
    }

    [Fact]
    public async Task AGuestWhoseHandleHidesACharacterCannotBeWidened()
    {
        var time = ManualTimeProvider.Demo();
        var saved = new List<IReadOnlyList<string>>();
        var user = new PairedUser("u-9", "discord", "wr\u202Een#1", PairingRole.Guest, time.GetUtcNow());
        var row = new PairedUserViewModel(user, time.GetUtcNow(), TimeZoneInfo.Utc, () => ["memory_search"],
            (_, tools) => { saved.Add(tools); return Task.CompletedTask; }, _ => Task.CompletedTask, _ => { });

        Assert.True(row.HandleHasHidden);
        Assert.Equal("wr⟨U+202E⟩en#1", row.RevealedHandle);
        Assert.Equal("Tools wr⟨U+202E⟩en#1 may use. Each still follows the approval mode.", row.EditHeading);
        Assert.False(row.EditCommand.CanExecute(null));
        Assert.False(row.SaveCommand.CanExecute(null));
        await row.SaveCommand.ExecuteAsync();
        Assert.Empty(saved);
    }

    [Fact]
    public async Task TheTrayCannotPairAnOwnerItOnlyLeadsToGatewaysQuestion()
    {
        var rig = Build();
        var owner = (await rig.Board.ListAttentionAsync(TestContext.Current.CancellationToken))
            .Single(item => item.Id == DemoBoardService.PairingItemId).Actions.Single(action => action.Id == "owner");
        Assert.Equal(AttentionActionKind.Navigate, owner.Kind);
        Assert.Equal(Area.Gateway, owner.Area);
        Assert.Equal(GatewayLinks.Owner(DemoGatewayService.DanaRequestId), owner.Target);

        await rig.Board.ResolveAsync(DemoBoardService.PairingItemId, "owner", TestContext.Current.CancellationToken);
        Assert.Single(rig.Gateway.Requests);
        Assert.DoesNotContain(rig.Gateway.Users, user => user.Handle == "@dana");

        // Answered on Gateway after its question, the tray's item goes too.
        var card = rig.Gateway.Requests[0];
        card.AskOwnerCommand.Execute(null);
        await card.OwnerCommand.ExecuteAsync();
        Assert.Equal("owner", rig.Gateway.Users.Single(user => user.Handle == "@dana").RoleText);
        Assert.DoesNotContain(await rig.Board.ListAttentionAsync(TestContext.Current.CancellationToken), item => item.Id == DemoBoardService.PairingItemId);
    }

    [Fact]
    public async Task IgnoreDropsTheCode()
    {
        var rig = Build();

        await ((AsyncRelayCommand)rig.Gateway.Requests[0].IgnoreCommand).ExecuteAsync();

        Assert.Empty(rig.Gateway.Requests);
        Assert.DoesNotContain(rig.Gateway.Users, user => user.Handle == "@dana");
        Assert.True(rig.Gateway.HasUsers);
        Assert.Equal("Ignored the code from @dana: they stay unpaired.", Text(rig.Gateway.Notice));
        Assert.Contains(("pairing:telegram:ABCD-EFGH", "ignore"), rig.Board.Resolved);
    }

    [Fact]
    public void PairingAsOwnerAsksFirst()
    {
        var rig = Build();
        var request = rig.Gateway.Requests[0];

        request.AskOwnerCommand.Execute(null);

        Assert.True(request.IsConfirmingOwner);
        Assert.Equal("Pair @dana as an owner? An owner answers approvals and runs commands from Telegram, as you do here.", Text(request.OwnerQuestion));
        Assert.True(rig.Gateway.Close());
        Assert.False(request.IsConfirmingOwner);
    }

    [Fact]
    public async Task AHandleHoldingHiddenCharactersIsRevealedAndCanOnlyBeIgnored()
    {
        var rig = Build();
        var sent = rig.Service.Request("telegram", "@kes\u202Etrel\U000E0041", "WXYZ-1234");

        var card = Assert.Single(rig.Gateway.Requests, card => card.Code == "WXYZ-1234");
        Assert.Equal("@kes⟨U+202E⟩trel⟨U+E0041⟩", card.Handle);
        Assert.False(card.CanPair);
        Assert.Contains("hidden characters", card.Warning, StringComparison.Ordinal);
        Assert.False(card.GuestCommand.CanExecute(null));
        Assert.False(card.OwnerCommand.CanExecute(null));
        Assert.False(card.AskOwnerCommand.CanExecute(null));
        Assert.True(card.IgnoreCommand.CanExecute(null));
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Service.PairAsync(sent.Id, PairingRole.Guest, TestContext.Current.CancellationToken));

        await ((AsyncRelayCommand)card.IgnoreCommand).ExecuteAsync();
        Assert.Equal("Ignored the code from @kestrel: they stay unpaired.", Text(rig.Gateway.Notice));
    }

    [Fact]
    public async Task WidenGivesAGuestTheToolsTicked()
    {
        var rig = Build();
        var river = rig.Gateway.Users.Single(user => user.Handle == "river#2210");

        river.EditCommand.Execute(null);
        Assert.True(river.IsEditing);
        Assert.Equal(["memory_search", "session_list", "web_fetch", "read_file", "notes__read"], river.Choices.Select(choice => choice.Name));
        Assert.True(river.Choices[0].IsChecked);
        river.Choices[2].IsChecked = true;
        await river.SaveCommand.ExecuteAsync();

        var updated = rig.Gateway.Users.Single(user => user.Handle == "river#2210");
        Assert.Equal("memory_search, web_fetch", updated.ToolsText);
        Assert.False(updated.IsEditing);
        Assert.Equal("river#2210 can now use memory_search, web_fetch.", Text(rig.Gateway.Notice));
        Assert.False(rig.Gateway.Users[0].EditCommand.CanExecute(null));
    }

    [Fact]
    public async Task AToolNameHoldingHiddenCharactersIsRevealedAndCannotBeGranted()
    {
        var rig = Build();
        rig.Service.OfferGuestTool("web_fetch\u202E\U000E0041");
        var river = rig.Gateway.Users.Single(user => user.Handle == "river#2210");

        river.EditCommand.Execute(null);
        var hidden = river.Choices.Single(choice => choice.HasHidden);
        Assert.Equal("web_fetch⟨U+202E⟩⟨U+E0041⟩", hidden.Name);
        Assert.False(hidden.CanToggle);
        Assert.True(river.HasHiddenChoice);
        Assert.Contains("hidden characters", river.HiddenWarning, StringComparison.Ordinal);

        hidden.IsChecked = true;
        Assert.False(hidden.IsChecked);
        Assert.True(river.SaveCommand.CanExecute(null));
        await river.SaveCommand.ExecuteAsync();
        Assert.Equal("memory_search", rig.Gateway.Users.Single(user => user.Handle == "river#2210").ToolsText);

        // The service refuses it too, whoever asks.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            rig.Service.SetAllowlistAsync("u-2", ["memory_search", "web_fetch\u202E\U000E0041"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AHiddenToolAlreadyOnTheAllowlistMustBeUntickedBeforeSaving()
    {
        // Stored by another client, or before the check existed: the editor shows it ticked, and will not save it ticked.
        const string Hidden = "notes\u202E__read\U000E0041";
        var time = ManualTimeProvider.Demo();
        var saved = new List<IReadOnlyList<string>>();
        var user = new PairedUser("u-9", "discord", "wren#1", PairingRole.Guest, time.GetUtcNow()) { Allowlist = ["memory_search", Hidden] };
        var row = new PairedUserViewModel(user, time.GetUtcNow(), TimeZoneInfo.Utc, () => ["memory_search", Hidden],
            (_, tools) => { saved.Add(tools); return Task.CompletedTask; }, _ => Task.CompletedTask, _ => { });

        row.EditCommand.Execute(null);
        var hidden = row.Choices.Single(choice => choice.HasHidden);
        Assert.True(hidden.IsChecked);
        Assert.True(hidden.CanToggle);
        Assert.False(row.SaveCommand.CanExecute(null));
        await row.SaveCommand.ExecuteAsync();
        Assert.Empty(saved);

        hidden.IsChecked = false;
        Assert.False(hidden.CanToggle);
        Assert.True(row.SaveCommand.CanExecute(null));
        hidden.IsChecked = true;
        Assert.False(hidden.IsChecked);
        await row.SaveCommand.ExecuteAsync();
        Assert.Equal(["memory_search"], Assert.Single(saved));
    }

    [Fact]
    public async Task RevokingTheOnlyOwnerIsRefusedInRed()
    {
        var rig = Build();
        var owner = rig.Gateway.Users[0];

        owner.AskRevokeCommand.Execute(null);
        Assert.True(owner.IsConfirmingRevoke);
        await Assert.ThrowsAsync<InvalidOperationException>(() => owner.RevokeCommand.ExecuteAsync());

        Assert.Equal(Tone.Red, rig.Gateway.NoticeTone);
        Assert.Equal("That did not work: That is the only owner; pair another owner before revoking this one.", Text(rig.Gateway.Notice));
        Assert.Equal(2, rig.Gateway.Users.Count);

        await rig.Gateway.Users[1].RevokeCommand.ExecuteAsync();
        Assert.Single(rig.Gateway.Users);
        Assert.Equal("Revoked river#2210: their next message gets a new code.", Text(rig.Gateway.Notice));
    }

    [Fact]
    public void TheCompositionBuildsGatewayOnTheDemoAndDisposesIt()
    {
        Assert.True(AppOptions.TryParse(["--page", "gateway"], out var options, out var error), error);
        using var composition = new AppComposition(options, new InlineDispatcher(), ManualTimeProvider.DemoLocal());

        Assert.Single(composition.Gateway.Requests);
        Assert.Equal("telegram up · discord up · slack off", composition.StatusBar.Gateway);
    }

    [Fact]
    public async Task TheBellFollowsAPairingAnsweredHereBeforeTheBoardExists()
    {
        Assert.True(AppOptions.TryParse(["--page", "gateway"], out var options, out var error), error);
        using var composition = new AppComposition(options, new InlineDispatcher(), ManualTimeProvider.DemoLocal());
        var gateway = composition.Gateway;
        var areaItems = (await composition.BoardService.ListAttentionAsync(TestContext.Current.CancellationToken)).Count;
        var before = composition.Shell.AttentionCount;

        await ((AsyncRelayCommand)gateway.Requests[0].GuestCommand).ExecuteAsync();

        Assert.Equal(areaItems - 1, (await composition.BoardService.ListAttentionAsync(TestContext.Current.CancellationToken)).Count);
        Assert.Equal(before - 1, composition.Shell.AttentionCount);
    }
}
