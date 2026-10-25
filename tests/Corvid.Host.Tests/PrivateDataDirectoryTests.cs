using Corvid.Gateway;
using Corvid.Host.Composition;
using Corvid.Kernel.Events;
using Corvid.Kernel.Jobs;
using Corvid.Memory;

namespace Corvid.Host.Tests;

/// <summary>
/// A4 verification: on a fresh home whichever store opens first makes the data directory, and makes it owner only (0700),
/// not with the umask: the lock, the pairing store, the job store, the event log and memory alike.
/// </summary>
public sealed class PrivateDataDirectoryTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-private-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    public static TheoryData<string> Stores => ["lock", "drivers-lock", "pairing", "jobs", "events", "memory"];

    [Theory]
    [MemberData(nameof(Stores))]
    public void The_first_store_to_open_makes_the_data_directory_owner_only(string store)
    {
        if (OperatingSystem.IsWindows())
            return;
        var data = Path.Combine(_root, "share", "corvid");
        switch (store)
        {
            case "lock":
                ProcessLock.TryAcquire(Path.Combine(data, "gateway.lock"))!.Dispose();
                break;
            case "drivers-lock":
                PrivateDirectoryFor(data);
                ProcessLock.TryAcquire(Path.Combine(data, "drivers", "cli-1.lock"))!.Dispose();
                Assert.Equal(OwnerOnly, File.GetUnixFileMode(Path.Combine(data, "drivers")));
                break;
            case "pairing":
                _ = new PairingStore(Path.Combine(data, "gateway.db"));
                break;
            case "jobs":
                _ = new JobStore(Path.Combine(data, "jobs.db"));
                break;
            case "events":
                _ = new SqliteEventLog(Path.Combine(data, "events.db"));
                break;
            case "memory":
                _ = new MemoryDatabase(Path.Combine(data, "memory.db"));
                break;
        }

        Assert.Equal(OwnerOnly, File.GetUnixFileMode(data));
    }

    // The data directory as the runtime makes it at start (CorvidPaths), before drivers/ inside it.
    private static void PrivateDirectoryFor(string data) => Corvid.Host.Configuration.CorvidPaths.CreatePrivateDirectory(data);
}
