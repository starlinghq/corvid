namespace Corvid.Gateway.Tests;

public sealed class PairingStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-pairing-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero));

    private PairingStore Store(int maxPending = 10) => new(Path.Combine(_root, "gateway.db"), _time) { MaxPendingPerAdapter = maxPending };

    [Fact]
    public void ACodeIsBoundToItsSenderRemindedRarelyAndExpires()
    {
        var store = Store();

        var first = store.Request("telegram", "42", "mark", "42");
        var again = store.Request("telegram", "42", "mark", "42");
        _time.Now += store.RemindAfter;
        var later = store.Request("telegram", "42", "mark", "42");

        Assert.True(first.Notify);
        Assert.NotNull(PairingStore.Normalize(first.Request!.Code));
        Assert.Matches("^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$", PairingStore.Display(first.Request.Code));
        Assert.Equal((first.Request.Code, false), (again.Request!.Code, again.Notify));
        Assert.Equal((first.Request.Code, true), (later.Request!.Code, later.Notify));

        _time.Now += store.CodeLifetime;
        Assert.Empty(store.Pending());
        Assert.Null(store.Approve(first.Request.Code));
        var renewed = store.Request("telegram", "42", "mark", "42");
        Assert.NotEqual(first.Request.Code, renewed.Request!.Code);
    }

    [Fact]
    public void ApprovingMakesTheSenderAnOwnerOrGuestAndRevokingUndoesIt()
    {
        var store = Store();
        var adapter = new AdapterOptions("telegram");
        var owner = store.Request("telegram", "42", "mark", "42").Request!;
        var guest = store.Request("telegram", "43", "ada", "43").Request!;

        Assert.Equal(SenderRole.None, store.RoleOf(adapter, "42"));
        Assert.Equal("42", store.Approve(PairingStore.Display(owner.Code).ToLowerInvariant())!.UserId);
        Assert.Equal(SenderRole.Guest, store.Approve(guest.Code, SenderRole.Guest)!.Role);
        // A code is used once.
        Assert.Null(store.Approve(owner.Code));

        Assert.Equal(SenderRole.Owner, store.RoleOf(adapter, "42"));
        Assert.Equal(SenderRole.Guest, store.RoleOf(adapter, "43"));
        Assert.Equal(SenderRole.None, store.RoleOf(new AdapterOptions("discord"), "42"));
        Assert.Equal(["42", "43"], store.Users("telegram").Select(user => user.UserId));

        Assert.True(store.Revoke("telegram", "42"));
        Assert.Equal(SenderRole.None, store.RoleOf(adapter, "42"));
    }

    /// <summary>G-14: Ignore drops the code; the sender stays unpaired and asking again gets a new code.</summary>
    [Fact]
    public void DismissingACodePairsNobodyAndTheNextAskGetsANewCode()
    {
        var store = Store();
        var request = store.Request("telegram", "42", "mark", "42").Request!;

        Assert.True(store.Dismiss(PairingStore.Display(request.Code).ToLowerInvariant()));
        Assert.False(store.Dismiss(request.Code));
        Assert.False(store.Dismiss("not a code"));
        Assert.Empty(store.Pending());
        Assert.Null(store.Approve(request.Code));
        Assert.Equal(SenderRole.None, store.RoleOf(new AdapterOptions("telegram"), "42"));
        var again = store.Request("telegram", "42", "mark", "42");
        Assert.True(again.Notify);
        Assert.NotEqual(request.Code, again.Request!.Code);
    }

    [Fact]
    public void ConfigurationAllowlistsNeedNoPairing()
    {
        var store = Store();
        var adapter = new AdapterOptions("slack") { Owners = ["U1"], Guests = ["U2"] };

        Assert.Equal(SenderRole.Owner, store.RoleOf(adapter, "U1"));
        Assert.Equal(SenderRole.Guest, store.RoleOf(adapter, "U2"));
        Assert.Equal(SenderRole.None, store.RoleOf(adapter, "U3"));
    }

    [Fact]
    public void FewCodesWaitPerAdapterAndNonsenseIsNotACode()
    {
        var store = Store(maxPending: 2);
        store.Request("discord", "a", null, "c");
        store.Request("discord", "b", null, "c");

        Assert.Null(store.Request("discord", "c", null, "c").Request);
        Assert.NotNull(store.Request("telegram", "c", null, "c").Request);
        Assert.Null(store.Approve("not-a-code"));
        Assert.Null(PairingStore.Normalize("ABCD-EFG0"));
        Assert.Throws<ArgumentException>(() => store.Approve("ABCDEFGH", SenderRole.None));
    }

    [Fact]
    public void StateSurvivesReopening()
    {
        Store().SetState("cursor:discord:G1", "102");

        Assert.Equal("102", Store().GetState("cursor:discord:G1"));
        Assert.Null(Store().GetState("missing"));
    }

    [Fact]
    public void MessagesAreSplitAtLineBreaksThenSpacesAndNeverInsideASurrogatePair()
    {
        var lines = string.Join('\n', Enumerable.Repeat("0123456789", 5));
        Assert.Equal(["0123456789\n0123456789", "0123456789\n0123456789", "0123456789"], MessageChunks.Split(lines, 25));

        var emoji = new string('a', 19) + "😀" + "bbbb";
        var pieces = MessageChunks.Split(emoji, 20);
        Assert.Equal(emoji, string.Concat(pieces));
        Assert.All(pieces, piece => Assert.False(char.IsHighSurrogate(piece[^1])));
        Assert.Equal(["short"], MessageChunks.Split("short", 20));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
