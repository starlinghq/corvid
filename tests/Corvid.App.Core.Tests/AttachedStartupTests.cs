using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Remote;

namespace Corvid.App.Core.Tests;

/// <summary>
/// How a window attaches (G-1): the command line, a dispatcher that holds the connection's posts until the window exists,
/// and a window with no token for an address opening with every area saying why rather than connecting.
/// </summary>
public sealed class AttachedStartupTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("--attach unix:/run/user/1000/corvid/attach.sock", true)]
    [InlineData("--attach ws://127.0.0.1:8737 --pair ABCDE-FGHJK", true)]
    [InlineData("--attach=127.0.0.1:8737 --page board", true)]
    [InlineData("--attach unix:relative.sock", false)]
    [InlineData("--attach http://127.0.0.1:8737", false)]
    [InlineData("--attach ws://127.0.0.1:8737 --demo", false)]
    [InlineData("--engine --attach ws://127.0.0.1:8737", false)]
    [InlineData("--attach ws://127.0.0.1:8737 --scene approval", false)]
    [InlineData("--pair ABCDE-FGHJK", false)]
    public void AttachIsItsOwnEngineOnTheCommandLine(string line, bool ok)
    {
        Assert.Equal(ok, AppOptions.TryParse(line.Split(' '), out var options, out var error));
        if (ok)
        {
            Assert.NotNull(options.Attach);
            Assert.False(options.Demo);
        }
        else
        {
            Assert.NotEmpty(error);
        }
    }

    [Fact]
    public void PostsBeforeTheWindowRunOnItInOrderBeforeLaterOnes()
    {
        var late = new LateDispatcher();
        var ran = new List<int>();
        late.Post(() => ran.Add(1));
        late.Post(() => ran.Add(2));
        Assert.Empty(ran);
        Assert.False(late.HasThreadAccess);

        late.Bind(new InlineDispatcher());
        late.Post(() => ran.Add(3));

        Assert.Equal([1, 2, 3], ran);
        Assert.True(late.HasThreadAccess);
        Assert.Throws<InvalidOperationException>(() => late.Bind(new InlineDispatcher()));
    }

    private sealed class NoTokens : IDeviceTokenStore
    {
        public string? Token { get; init; }

        public string? Get(string address) => Token;

        public void Set(string address, string token) => throw new InvalidOperationException("nothing was paired");
    }

    [Fact]
    public async Task WithNoTokenForTheAddressTheWindowOpensSayingHowToPair()
    {
        var attached = await AttachedStartup.ConnectAsync("ws://192.0.2.10:8737", null, "test", new LateDispatcher(), new NoTokens(),
            ownerTokenFile: null, Ct);

        Assert.Null(attached.Connection);
        var unavailable = Assert.IsType<UnavailableAppServices>(attached.Services);
        Assert.Contains("pair", unavailable.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAddressNobodyServesOpensTheWindowWithTheReason()
    {
        var socket = Path.Combine(Path.GetTempPath(), $"corvid-none-{Guid.NewGuid():N}.sock");
        var attached = await AttachedStartup.ConnectAsync("unix:" + socket, null, "test", new LateDispatcher(), new NoTokens { Token = "a-token" },
            ownerTokenFile: null, Ct);
        Assert.Null(attached.Connection);
        Assert.IsType<UnavailableAppServices>(attached.Services);
    }
}
