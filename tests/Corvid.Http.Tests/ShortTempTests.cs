using System.Net.Sockets;
using Corvid.Tests;

namespace Corvid.Http.Tests;

/// <summary>
/// The shared short temporary directory: a stand-in whose constructor fails after creating it (a socket bind failing, say)
/// leaves nothing behind.
/// </summary>
public sealed class ShortTempTests
{
    [Fact]
    public void A_failed_set_up_removes_the_directory_and_rethrows_its_exception()
    {
        var root = ShortTemp.Create("cvt-");
        File.WriteAllText(Path.Combine(root, "made-by-set-up"), "x");

        var error = Assert.Throws<InvalidOperationException>(() => ShortTemp.SetUp<int>(root, () => throw new InvalidOperationException("bind failed")));

        Assert.Equal("bind failed", error.Message);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_socket_that_cannot_bind_removes_the_directory()
    {
        var root = ShortTemp.Create("cvt-");

        // The socket's directory does not exist, so the bind fails after the stand-in's directory was made.
        Assert.ThrowsAny<SocketException>(() => ShortTemp.Listen(root, Path.Combine(root, "missing", "v.sock")));

        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_set_up_that_succeeds_keeps_the_directory_for_the_caller_to_remove()
    {
        var root = ShortTemp.Create("cvt-");
        try
        {
            using var listener = ShortTemp.Listen(root, Path.Combine(root, "v.sock"));

            Assert.True(File.Exists(Path.Combine(root, "v.sock")));
            Assert.Equal(42, ShortTemp.SetUp(root, () => 42));
            Assert.True(Directory.Exists(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
