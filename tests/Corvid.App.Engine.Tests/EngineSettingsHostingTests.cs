using Corvid.App.Core.Shell;
using Corvid.App.Engine.Settings;

namespace Corvid.App.Engine.Tests;

/// <summary>A4 verification: saving a setting applies the hosting settings only when the save changed one of them.</summary>
public sealed class EngineSettingsHostingTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SavingAnotherSettingLeavesHostingThisRunTurnedOnAsItIs()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var service = new EngineSettingsService(fixture.Engine);
        // Hosting turned on for this run without the file saying so (an environment override, or Start on a page whose
        // remembered value is not written yet).
        await fixture.Engine.SetHostingAsync(HostedPart.Gateway, true);
        Assert.Equal("false", (await service.LoadAsync(Token)).Values["App:HostGateway"]);

        var write = await service.SaveAsync(new Dictionary<string, string> { ["Approvals:TimeoutSeconds"] = "45" }, Token);

        Assert.True(write.Written, write.Message);
        Assert.DoesNotContain("host the gateway", write.Message, StringComparison.Ordinal);
        Assert.True(fixture.Engine.HostsGateway);
    }
}
