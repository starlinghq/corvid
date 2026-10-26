using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Remote;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Tests;

/// <summary>
/// Attached mode's credentials (G-1): the owner token, pairing codes that redeem once before they expire and cannot be
/// guessed, device tokens kept as digests in a 0600 file and refused once revoked, and only the owner making codes.
/// </summary>
public sealed class AttachedAuthenticatorTests : IDisposable
{
    private const string Owner = "owner-token-0123456789abcdefghijklmnop";

    private readonly string _directory = Directory.CreateTempSubdirectory("corvid-attach-auth-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Devices => Path.Combine(_directory, "attach", "devices.json");

    private AttachedAuthenticator Create(ManualTimeProvider? time = null, Func<string, bool?>? owner = null) =>
        new((token, _) => ValueTask.FromResult(owner is null ? token == Owner : owner(token)), Devices, time);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task TheOwnerTokenIsTheOwnerAndAnythingElseIsRefused()
    {
        var authenticator = Create();
        Assert.Equal(new AttachedIdentity(AttachedRole.Owner, null), await authenticator.AuthenticateAsync(Owner, Ct));
        Assert.Null(await authenticator.AuthenticateAsync("not the token", Ct));
        Assert.Null(await authenticator.AuthenticateAsync("", Ct));
    }

    [Fact]
    public async Task ACodeRedeemsOnceForADeviceTokenKeptOnlyAsADigestInAPrivateFile()
    {
        var authenticator = Create();
        var code = authenticator.CreatePairingCode();
        Assert.Matches("^[A-Z2-9]{5}-[A-Z2-9]{5}$", code.Code);

        // Typed as the person reads it: lower case and without the dash still redeems.
        var paired = await authenticator.PairAsync(code.Code.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant(), "phone", Ct);
        Assert.NotNull(paired);
        Assert.Equal(new AttachedIdentity(AttachedRole.Device, "phone"), paired.Value.Identity);
        Assert.Null(await authenticator.PairAsync(code.Code, "laptop", Ct));

        Assert.Equal(new AttachedIdentity(AttachedRole.Device, "phone"), await authenticator.AuthenticateAsync(paired.Value.Token, Ct));
        var stored = await File.ReadAllTextAsync(Devices, Ct);
        Assert.DoesNotContain(paired.Value.Token, stored, StringComparison.Ordinal);
        Assert.Contains(AttachedSecrets.Digest(paired.Value.Token), stored, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Devices));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(Devices)!));
        }
    }

    [Fact]
    public async Task ACodeExpires()
    {
        var time = ManualTimeProvider.Demo();
        var authenticator = Create(time);
        var code = authenticator.CreatePairingCode();
        time.Advance(AttachedAuthenticator.CodeLifetime);
        Assert.Null(await authenticator.PairAsync(code.Code, "phone", Ct));
    }

    [Fact]
    public async Task TooManyWrongCodesInAMinuteRefuseEvenTheRightOneUntilTheMinuteIsOver()
    {
        var time = ManualTimeProvider.Demo();
        var authenticator = Create(time);
        var code = authenticator.CreatePairingCode();
        for (var i = 0; i < AttachedAuthenticator.FailuresPerMinute; i++)
            Assert.Null(await authenticator.PairAsync("WRONG-CODE" + i, "guesser", Ct));

        Assert.Null(await authenticator.PairAsync(code.Code, "phone", Ct));
        // Tokens too: a flood of guesses shuts the door for the minute.
        Assert.Null(await authenticator.AuthenticateAsync(Owner, Ct));

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.NotNull(await authenticator.PairAsync(code.Code, "phone", Ct));
        Assert.NotNull(await authenticator.AuthenticateAsync(Owner, Ct));
    }

    [Fact]
    public async Task GuessesFromTheNetworkNeverLockTheOwnerOutOfTheSocketOrAnotherPeer()
    {
        // A6 fix: once --listen reaches past loopback, a host on the LAN could keep the owner out with ten wrong tokens.
        var time = ManualTimeProvider.Demo();
        var authenticator = Create(time);
        for (var i = 0; i < AttachedAuthenticator.FailuresPerMinute; i++)
            Assert.Null(await authenticator.AuthenticateAsync("wrong" + i, "192.0.2.7", Ct));

        Assert.Null(await authenticator.AuthenticateAsync(Owner, "192.0.2.7", Ct));
        Assert.NotNull(await authenticator.AuthenticateAsync(Owner, AttachedAuthenticator.SocketPeer, Ct));
        Assert.NotNull(await authenticator.AuthenticateAsync(Owner, "192.0.2.8", Ct));

        // Many peers together shut the network for the minute, so a code cannot be guessed from many addresses; the socket stays open.
        for (var i = 0; i < AttachedAuthenticator.NetworkFailuresPerMinute; i++)
            Assert.Null(await authenticator.PairAsync("WRONG-CODE", "guesser", "198.51.100." + i, Ct));
        var code = authenticator.CreatePairingCode();
        Assert.Null(await authenticator.PairAsync(code.Code, "phone", "203.0.113.1", Ct));
        Assert.NotNull(await authenticator.AuthenticateAsync(Owner, AttachedAuthenticator.SocketPeer, Ct));
        Assert.NotNull(await authenticator.PairAsync(code.Code, "phone", AttachedAuthenticator.SocketPeer, Ct));
    }

    [Fact]
    public async Task AnOwnerCheckThatCannotBeToldRefusesWithoutCountingAsAGuess()
    {
        var authenticator = Create(owner: _ => null);
        for (var i = 0; i < AttachedAuthenticator.FailuresPerMinute + 2; i++)
            Assert.Null(await authenticator.AuthenticateAsync(Owner, Ct));
        // Vault not answering is no guess: pairing is not locked out by it.
        Assert.NotNull(await authenticator.PairAsync(authenticator.CreatePairingCode().Code, "phone", Ct));
    }

    [Fact]
    public async Task ARevokedDeviceIsRefusedAndTwoDevicesOfOneNameAreTold()
    {
        var authenticator = Create();
        var first = (await authenticator.PairAsync(authenticator.CreatePairingCode().Code, "phone", Ct))!.Value;
        var second = (await authenticator.PairAsync(authenticator.CreatePairingCode().Code, "phone", Ct))!.Value;
        Assert.Equal("phone (2)", second.Identity.Device);

        Assert.True(AttachedAuthenticator.Revoke(Devices, "phone"));
        Assert.False(AttachedAuthenticator.Revoke(Devices, "phone"));
        Assert.Null(await authenticator.AuthenticateAsync(first.Token, Ct));
        Assert.Equal("phone (2)", (await authenticator.AuthenticateAsync(second.Token, Ct))?.Device);
    }

    [Fact]
    public void AFileOthersCanReadIsRefusedNotTrusted()
    {
        if (OperatingSystem.IsWindows())
            return;
        var path = Path.Combine(_directory, "attach", "owner.token");
        var token = OwnerTokens.Ensure(path);
        Assert.Equal(token, OwnerTokens.Ensure(path));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Equal(43, token.Length);

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        Assert.Throws<UnauthorizedAccessException>(() => OwnerTokens.Find(path));
        Assert.Throws<UnauthorizedAccessException>(() => OwnerTokens.Ensure(path));
    }

    [Fact]
    public async Task OnlyTheOwnerMakesPairingCodesOverTheWire()
    {
        var authenticator = Create();
        var engine = new DemoSessionService(new InlineDispatcher(), ManualTimeProvider.Demo());
        var server = new AttachedServer(engine, authenticator);
        using var stop = new CancellationTokenSource();

        async Task<RemoteSessionService> Connect(string token)
        {
            var (client, end) = InMemoryMessageChannel.CreatePair();
            _ = server.ServeAsync(end, "test", stop.Token);
            return await RemoteSessionService.ConnectAsync(client, token, "test", new InlineDispatcher(), Ct);
        }

        await using var owner = await Connect(Owner);
        var code = await owner.CreatePairingCodeAsync(Ct);
        var (pairing, end) = InMemoryMessageChannel.CreatePair();
        _ = server.ServeAsync(end, "test", stop.Token);
        var deviceToken = await RemoteSessionService.PairAsync(pairing, code.Code, "phone", Ct);

        await using var device = await Connect(deviceToken);
        Assert.Equal(AttachedRole.Device, device.Capabilities.Role);
        // A paired device cannot hand out access of its own.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => device.CreatePairingCodeAsync(Ct));
        await stop.CancelAsync();
    }

    [Fact]
    public async Task RevokingADeviceClosesTheConnectionsItHasOpen()
    {
        // A6 fix: `corvid serve revoke` refused only the next connection; a live one kept the seam and its approvals.
        var time = ManualTimeProvider.Demo();
        var authenticator = Create(time);
        var engine = new DemoSessionService(new InlineDispatcher(), ManualTimeProvider.Demo());
        var server = new AttachedServer(engine, authenticator, time);
        using var stop = new CancellationTokenSource();
        var token = (await authenticator.PairAsync(authenticator.CreatePairingCode().Code, "phone", Ct))!.Value.Token;

        async Task<(RemoteSessionService Client, Task Served)> Connect(string with)
        {
            var (client, end) = InMemoryMessageChannel.CreatePair();
            var served = server.ServeAsync(end, "test", stop.Token);
            return (await RemoteSessionService.ConnectAsync(client, with, "test", new InlineDispatcher(), Ct), served);
        }

        var (asking, _) = await Connect(token);
        var (watching, served) = await Connect(token);
        var (owner, ownerServed) = await Connect(Owner);
        await using (asking)
        await using (watching)
        await using (owner)
        {
            Assert.NotEmpty(await asking.ListAsync(Ct));
            Assert.True(AttachedAuthenticator.Revoke(Devices, "phone"));

            // Its next request is refused, and a connection that sends none is closed at the next check.
            await Assert.ThrowsAnyAsync<Exception>(() => asking.ListAsync(Ct));
            Assert.False(served.IsCompleted);
            time.Advance(AttachedProtocol.RevocationCheck);
            await served.WaitAsync(TimeSpan.FromSeconds(10), Ct);
            // The owner is never revoked this way.
            Assert.False(ownerServed.IsCompleted);
            Assert.NotEmpty(await owner.ListAsync(Ct));
        }
        await stop.CancelAsync();
    }

    [Fact]
    public async Task AnOpenSessionSaysSoWhenTheConnectionEnds()
    {
        var engine = new DemoSessionService(new InlineDispatcher(), ManualTimeProvider.Demo());
        var server = new AttachedServer(engine, Create());
        using var stop = new CancellationTokenSource();
        var (channel, end) = InMemoryMessageChannel.CreatePair();
        var served = server.ServeAsync(end, "test", stop.Token);
        await using var client = await RemoteSessionService.ConnectAsync(channel, Owner, "test", new InlineDispatcher(), Ct);
        var notices = new TaskCompletionSource<Notice>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = await client.OpenAsync(DemoSessionService.MainSessionId, update =>
        {
            if (update is Notice { Kind: NoticeKind.Warning } notice)
                notices.TrySetResult(notice);
        }, Ct);

        await stop.CancelAsync();
        await served.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var said = await notices.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.StartsWith("Disconnected from the attached Corvid", said.Text, StringComparison.Ordinal);
    }
}
