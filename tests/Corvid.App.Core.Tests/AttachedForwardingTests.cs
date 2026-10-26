using System.Reflection;
using Corvid.App.Core.Remote;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Tests;

/// <summary>
/// The attached window's session seam implements every member of <see cref="ISessionService"/> itself (A6 integration):
/// a member left to its interface default (images, K-8; workspaces, G-4, were added as defaults) would answer for the
/// window ("comes with the engine") instead of asking the engine, as the attached window did before this check.
/// </summary>
public sealed class AttachedForwardingTests
{
    public static TheoryData<string> Seams => ["RemoteSessionService", "Borrowed"];

    private static Type Seam(string name) => name == "RemoteSessionService"
        ? typeof(RemoteSessionService)
        : typeof(RemoteAppServices).GetNestedType("Borrowed", BindingFlags.NonPublic)!;

    [Theory]
    [MemberData(nameof(Seams))]
    public void EveryMemberOfTheSeamIsImplementedNotLeftToItsDefault(string name)
    {
        var seam = Seam(name);
        Assert.NotNull(seam);
        var map = seam.GetInterfaceMap(typeof(ISessionService));

        var defaulted = map.TargetMethods.Where(method => method.DeclaringType == typeof(ISessionService)).Select(method => method.Name).ToList();

        Assert.Empty(defaulted);
    }
}
