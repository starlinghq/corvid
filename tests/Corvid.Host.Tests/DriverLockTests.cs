using Corvid.Host.Composition;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>
/// The one-process rule as a file (U-8, G-12): the lock with its holder's note, each session's driver lock, and the
/// scheduler's holder. Two runtimes over one data directory stand in for two processes: <c>flock</c> on two open files
/// conflicts within a process as across processes.
/// </summary>
public sealed class DriverLockTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string TempLock() => Path.Combine(Path.GetTempPath(), "corvid-lock-" + Guid.NewGuid().ToString("N"), "a.lock");

    [Fact]
    public void ALockIsHeldByOneHolderAndItsNoteSaysWho()
    {
        var path = TempLock();
        try
        {
            using (var held = ProcessLock.TryAcquire(path))
            {
                Assert.NotNull(held);
                Assert.Equal((ProcessLock.ProcessName, Environment.ProcessId), (held.Holder.Process, held.Holder.ProcessId));
                Assert.Null(ProcessLock.TryAcquire(path));
                var probe = ProcessLock.Probe(path);
                Assert.Equal(LockState.HeldElsewhere, probe.State);
                Assert.Equal(held.Holder, probe.Holder);
            }
            // Released: the note goes, the lock file stays (deleting it would let two processes hold two files).
            Assert.Equal(LockState.Free, ProcessLock.Probe(path).State);
            Assert.False(File.Exists(ProcessLock.NotePath(path)));
            Assert.True(File.Exists(path));
            using var again = ProcessLock.TryAcquire(path);
            Assert.NotNull(again);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void ANoteACrashedHolderLeftOverAFreeLockIsStale()
    {
        var path = TempLock();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "");
            File.WriteAllText(ProcessLock.NotePath(path), """{"process":"corvid chat","pid":1,"since":"2026-10-24T09:00:00+00:00"}""");

            Assert.Equal(LockProbe.Free, ProcessLock.Probe(path));
            using var taken = ProcessLock.TryAcquire(path);
            Assert.NotNull(taken);
            // The note is this holder's now.
            Assert.Equal(taken.Holder, ProcessLock.ReadNote(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task ASessionHasOneDriverAndAnotherProcessIsToldWhoDrivesIt()
    {
        await using var host = TestHost.Create();
        var first = await host.StartAsync();
        var factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();
        await using var second = await factory.StartAsync(Token);
        try
        {
            var session = first.OpenSession("cli-shared");

            Assert.Equal(LockState.HeldHere, first.Driver(session.Id).State);
            var seen = second.Driver(session.Id);
            Assert.Equal(LockState.HeldElsewhere, seen.State);
            Assert.Equal(Environment.ProcessId, seen.Holder!.ProcessId);
            var refused = Assert.Throws<SessionHeldException>(() => second.OpenSession(session.Id));
            Assert.Equal(session.Id, refused.SessionId);
            Assert.Equal(seen.Holder, refused.Holder);
            Assert.Contains("is driven by", refused.Message, StringComparison.Ordinal);
            Assert.False(second.TryGetSession(session.Id, out _));
            // Another session is not held by it, and a session nobody opened is free.
            Assert.Equal(LockState.Free, second.Driver("cli-other").State);
            second.OpenSession("cli-other");
            Assert.Throws<SessionHeldException>(() => first.OpenSession("cli-other"));
        }
        finally
        {
            // The driver exits: its locks go with it.
            await first.DisposeAsync();
        }

        Assert.Equal(LockState.Free, second.Driver("cli-shared").State);
        var taken = second.OpenSession("cli-shared");
        Assert.Equal(LockState.HeldHere, second.Driver(taken.Id).State);
    }

    [Fact]
    public async Task ReadingASessionAnotherProcessDrivesNeedsNoLock()
    {
        await using var host = TestHost.Create(new FakeModels(new FakeGenerator(Reply.Say("Hello."))));
        await using var first = await host.StartAsync();
        var factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();
        await using var second = await factory.StartAsync(Token);
        var session = first.OpenSession("cli-read");
        await session.SendAsync("hi", Token);

        // The second process reads it as it stands, without opening it.
        Assert.Contains(second.Log.Read("cli-read", cancellationToken: Token), item => item.Payload is Abstractions.Events.AssistantMessage);
        Assert.Equal(LockState.HeldElsewhere, second.Driver("cli-read").State);
    }

    [Fact]
    public async Task ARecapOrARenameOfASessionAnotherProcessDrivesIsRefusedAndWritesNothing()
    {
        var generator = new FakeGenerator(Reply.Say("Hello."), Reply.Say("You said hi."));
        await using var host = TestHost.Create(new FakeModels(generator));
        await using var first = await host.StartAsync();
        var factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();
        await using var second = await factory.StartAsync(Token);
        var session = first.OpenSession("cli-held");
        await session.SendAsync("hi", Token);
        var head = second.Log.GetHead("cli-held", Token);

        // G-12: only the driver writes to the log, a title or a recap included, and the generator is not asked for one.
        var renamed = Assert.Throws<SessionHeldException>(() => second.Rename("cli-held", "From the window"));
        Assert.Contains("is driven by", renamed.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<SessionHeldException>(() => second.RecapAsync("cli-held", cancellationToken: Token));
        Assert.Single(generator.Calls);
        Assert.Equal(head, second.Log.GetHead("cli-held", Token));

        // The driver itself may.
        Assert.Equal("You said hi.", (await first.RecapAsync("cli-held", cancellationToken: Token))?.Text);
        Assert.Equal("Mine now", first.Rename("cli-held", "Mine now").Title);
    }

    [Fact]
    public async Task ASessionAnotherProcessTakesWhileTheRecapIsWrittenIsNotWrittenTo()
    {
        CorvidRuntime? taker = null;
        var generator = new FakeGenerator(
            Reply.Say("Hello."),
            _ =>
            {
                // While the local generator writes the recap, the terminal takes the session.
                taker!.OpenSession("cli-race");
                return new ChatResponse(new ChatMessage(ChatRole.Assistant, "You said hi."));
            });
        await using var host = TestHost.Create(new FakeModels(generator));
        var factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();
        await using (var earlier = await host.StartAsync())
            await earlier.OpenSession("cli-race").SendAsync("hi", Token);
        await using var window = await factory.StartAsync(Token);
        await using var terminal = await factory.StartAsync(Token);
        taker = terminal;
        var head = window.Log.GetHead("cli-race", Token);
        Assert.Equal(LockState.Free, window.Driver("cli-race").State);

        await Assert.ThrowsAsync<SessionHeldException>(() => window.RecapAsync("cli-race", cancellationToken: Token));

        Assert.Equal(2, generator.Calls.Count);
        Assert.Equal(head, window.Log.GetHead("cli-race", Token));
    }

    [Theory]
    [InlineData("cli-20261024-101500-abc123", "cli-20261024-101500-abc123")]
    [InlineData("app-x.y_z", "app-x.y_z")]
    public void APlainSessionIdIsItsOwnFileName(string id, string expected) => Assert.Equal(expected, CorvidRuntime.DriverFileName(id));

    [Fact]
    public void AnIdWithOtherCharactersGetsAFileNameOfItsOwn()
    {
        var gateway = CorvidRuntime.DriverFileName("gateway:telegram:42");
        Assert.StartsWith("gateway_telegram_42~", gateway, StringComparison.Ordinal);
        Assert.NotEqual(gateway, CorvidRuntime.DriverFileName("gateway_telegram_42"));
        Assert.NotEqual(gateway, CorvidRuntime.DriverFileName("gateway:telegram/42"));
        Assert.DoesNotContain('/', CorvidRuntime.DriverFileName("../../etc/passwd"));
        Assert.NotEqual("..", CorvidRuntime.DriverFileName(".."));
    }

    [Fact]
    public async Task TheSchedulersHolderAndSinceAreKnownHereAndElsewhere()
    {
        await using var host = TestHost.Create(settings: new Dictionary<string, string?> { ["Corvid:Scheduler:Enabled"] = "true" });
        var before = DateTimeOffset.UtcNow;
        await using var first = await host.StartAsync();
        var factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();
        await using var second = await factory.StartAsync(Token);

        Assert.Equal("running in this process", first.SchedulerState);
        var here = first.SchedulerHolder;
        Assert.NotNull(here);
        Assert.InRange(here.Since, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal("run by another corvid process", second.SchedulerState);
        Assert.Equal(here, second.SchedulerHolder);
    }

    [Fact]
    public void ARefusalThatIsNoHolderIsNotHeld()
    {
        // L5: a lock file that cannot be opened for another reason (here a name too long for the file system) is an
        // error the caller reports, never "another process holds it".
        var path = Path.Combine(Path.GetDirectoryName(TempLock())!, new string('a', 300) + ".lock");
        try
        {
            Assert.ThrowsAny<IOException>(() => ProcessLock.TryAcquire(path));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task ADriverThatLeftNoNoteStillHoldsTheSession()
    {
        // L6: a holder that could not write its note (an older corvid, a note the disk refused) still holds the lock.
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        var path = CorvidRuntime.DriverLockPath(runtime.Options, "cli-quiet");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.False(File.Exists(ProcessLock.NotePath(path)));
            Assert.Equal(LockState.HeldElsewhere, runtime.Driver("cli-quiet").State);
            Assert.Throws<SessionHeldException>(() => runtime.OpenSession("cli-quiet"));
        }
        Assert.Equal(LockState.Free, runtime.Driver("cli-quiet").State);
    }

    [Fact]
    public async Task ADataDirectoryWithNoRoomForTheLockRefusesToOpenTheSession()
    {
        if (OperatingSystem.IsWindows())
            return;
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        var drivers = Path.GetDirectoryName(CorvidRuntime.DriverLockPath(runtime.Options, "cli-ro"))!;
        Directory.CreateDirectory(drivers);
        File.SetUnixFileMode(drivers, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            // Root writes anyway: nothing to test there.
            try
            {
                File.WriteAllText(Path.Combine(drivers, "probe"), "");
                return;
            }
            catch (UnauthorizedAccessException)
            {
            }

            // L5: not posted to unguarded, and not said to be someone else's.
            var refused = Assert.Throws<SessionLockUnavailableException>(() => runtime.OpenSession("cli-ro"));
            Assert.Contains("read-only", refused.Message, StringComparison.Ordinal);
            Assert.False(runtime.TryGetSession("cli-ro", out _));
        }
        finally
        {
            File.SetUnixFileMode(drivers, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task ARenameOrARecapsWriteHoldsTheDriverLockWhileItWrites()
    {
        // L7: the write itself holds the lock when nobody drives the session, so nobody opens it between check and append.
        await using var host = TestHost.Create();
        await using var window = await host.StartAsync();
        var factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();
        await using var terminal = await factory.StartAsync(Token);

        using (var recap = window.GuardWrite("cli-w", probes: 1))
        {
            recap.Check();
            // The check before generating only looks.
            Assert.Equal(LockState.Free, terminal.Driver("cli-w").State);
            recap.Check();
            Assert.Equal(LockState.HeldElsewhere, terminal.Driver("cli-w").State);
            Assert.Throws<SessionHeldException>(() => terminal.OpenSession("cli-w"));
        }
        Assert.Equal(LockState.Free, terminal.Driver("cli-w").State);
        Assert.Equal("Renamed", window.Rename("cli-w", "Renamed").Title);
        Assert.Equal(LockState.Free, terminal.Driver("cli-w").State);
        terminal.OpenSession("cli-w");
        Assert.Throws<SessionHeldException>(() => window.Rename("cli-w", "Again"));
    }

    [Fact]
    public async Task ADriverLetsGoOfAnIdleSessionAndAnotherProcessDrivesIt()
    {
        // M2: the app lets go of a session it opened for a restore, or stopped viewing, so `corvid chat --session` can drive it.
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        var generator = new FakeGenerator(_ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Done."));
        });
        await using var host = TestHost.Create(new FakeModels(generator));
        await using var app = await host.StartAsync();
        var factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();
        await using var terminal = await factory.StartAsync(Token);
        var session = app.OpenSession("cli-busy");
        Assert.False(await terminal.ReleaseDriverAsync("cli-busy"));

        var turn = await session.PostAsync("hi", Abstractions.Sessions.EnvelopeOrigin.User, Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30), Token));
        // A turn running: kept.
        Assert.False(await app.ReleaseDriverAsync("cli-busy"));
        Assert.Throws<SessionHeldException>(() => terminal.OpenSession("cli-busy"));
        release.Set();
        await turn.WaitAsync(Token);
        await session.Actor.IdleAsync(Token);

        Assert.True(await app.ReleaseDriverAsync("cli-busy"));

        Assert.False(app.TryGetSession("cli-busy", out _));
        Assert.Equal(LockState.Free, app.Driver("cli-busy").State);
        var theirs = terminal.OpenSession("cli-busy");
        Assert.Equal(LockState.HeldHere, terminal.Driver(theirs.Id).State);
        Assert.Throws<SessionHeldException>(() => app.OpenSession("cli-busy"));
    }

    [Fact]
    public void IdsThatDifferOnlyInCaseNeverShareALockFile()
    {
        var upper = CorvidRuntime.DriverFileName("Cli-A");
        Assert.NotEqual(upper.ToLowerInvariant(), CorvidRuntime.DriverFileName("cli-a").ToLowerInvariant());
        Assert.Equal(upper.ToLowerInvariant(), upper);
    }

    [Fact]
    public async Task AMessagePostedToASessionLetGoOfRetakesItsLockOrFailsWithWhoHoldsIt()
    {
        // M2: a handle kept across a release posts through the router, which never runs a turn without the driver lock.
        await using var host = TestHost.Create(new FakeModels(new FakeGenerator(Reply.Say("Back."))));
        await using var app = await host.StartAsync();
        var factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();
        await using var terminal = await factory.StartAsync(Token);
        var kept = app.OpenSession("cli-kept");
        Assert.True(await app.ReleaseDriverAsync("cli-kept"));
        terminal.OpenSession("cli-kept");

        var delivery = await kept.PostAsync("still there?", Abstractions.Sessions.EnvelopeOrigin.User, Token);

        var refused = await Assert.ThrowsAsync<SessionHeldException>(() => delivery.WaitAsync(Token));
        Assert.Equal("cli-kept", refused.SessionId);
        Assert.False(app.TryGetSession("cli-kept", out _));
    }

    [Fact]
    public async Task NobodyHoldsTheSchedulerWhenItIsOff()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();

        Assert.Null(runtime.SchedulerHolder);
    }
}
