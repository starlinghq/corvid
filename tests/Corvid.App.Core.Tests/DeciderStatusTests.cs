using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;

namespace Corvid.App.Core.Tests;

/// <summary>The status bar's decider words when the decider slot is unbound (A3 end-to-end pass, on the engine).</summary>
public sealed class DeciderStatusTests
{
    [Fact]
    public void AnUnboundDeciderSaysNoneIsConfiguredRatherThanNamingAnEmptyModel()
    {
        var demo = new DemoModelService(new InlineDispatcher(), ManualTimeProvider.Demo(), DemoModelScene.Main).Snapshot;
        var unbound = demo with
        {
            Decider = new DeciderStatus("", "no decider", DeciderTier.Degraded, "none", "none", "the degraded tier", "full checkpoints"),
        };

        Assert.Equal("degraded tier (no decider configured)", ModelsViewModel.StatusDecider(unbound));
        Assert.Equal("degraded until kev-0.8b is downloaded",
            ModelsViewModel.StatusDecider(demo with { Decider = demo.Decider with { Tier = DeciderTier.Degraded } }));
    }
}
