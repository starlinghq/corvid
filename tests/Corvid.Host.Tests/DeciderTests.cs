using Corvid.Abstractions.Events;
using Corvid.Host.Commands;
using Corvid.Host.Diagnostics;
using Corvid.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>The decider slot (C-004b): Kev when its model is in the cache, the degraded tier with a notice when not.</summary>
public sealed class DeciderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> Kev(string? second = null)
    {
        var settings = new Dictionary<string, string?> { ["Corvid:Models:Slots:decider:Model"] = "fake-kev" };
        if (second is not null)
            settings["Corvid:Models:Slots:decider:Escalation:0"] = second;
        return settings;
    }

    [Fact]
    public async Task KevAnswersTheFabricWhenItsModelIsReady()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("Hello.")));
        await using var host = TestHost.Create(models, Kev());
        await using var runtime = await host.StartAsync();
        bool? onDevice = null;
        Assert.True(runtime.Devices.TryGet("cpu", out var cpu));
        models.Kev.Answer = (_, question) =>
        {
            onDevice = cpu.IsCurrentThread;
            return [.. question.Options.Select(_ => 1.0 / question.Options.Length)];
        };
        var session = runtime.OpenSession();

        await session.SendAsync("hello", Ct);

        Assert.Equal("fake-kev", runtime.Fabric.CalibratedDecider);
        Assert.Null(runtime.Fabric.EscalationDecider);
        Assert.Empty(runtime.DeciderNotices);
        Assert.Equal("fake-kev@cpu", runtime.Registry.Get(ModelSlots.Decider).Default.Profile.Id);
        Assert.Contains("fake-kev", runtime.DeciderState, StringComparison.Ordinal);
        // One request per checkpoint, run on the CPU device (fabric priority), never on the session actor.
        Assert.NotEmpty(models.Kev.Requests);
        Assert.True(onDevice);
        var decisions = runtime.Log.Read(session.Id, cancellationToken: Ct).Select(item => item.Payload).OfType<Decision>().ToArray();
        Assert.NotEmpty(decisions);
        Assert.All(decisions, decision => Assert.Equal("fake-kev", decision.Decider));
        Assert.All(decisions.Where(decision => decision.Probabilities.Count > 0), decision => Assert.True(decision.Calibrated));
        Assert.True(runtime.Metrics.TryGet(ModelSlots.Decider, "fake-kev@cpu", out var latency) && latency.Calls > 0);
    }

    [Fact]
    public async Task AMissingKevLeavesTheDegradedTierAndSaysHowToFetchIt()
    {
        var models = new FakeModels(kevReady: false);
        await using var host = TestHost.Create(models, Kev("fake-kev-large"));
        await using var runtime = await host.StartAsync();

        Assert.Null(runtime.Fabric.CalibratedDecider);
        Assert.False(runtime.Registry.TryGet(ModelSlots.Decider, out _));
        Assert.DoesNotContain("fake-kev", models.Provider.Loaded);
        var notice = Assert.Single(runtime.DeciderNotices);
        Assert.Contains("degraded tier", notice, StringComparison.Ordinal);
        Assert.Contains("corvid model download fake-kev", notice, StringComparison.Ordinal);
        Assert.StartsWith("degraded tier", runtime.DeciderState, StringComparison.Ordinal);

        var delivery = await runtime.OpenSession().SendAsync("hello", Ct);
        Assert.Null(delivery.Error);
    }

    [Fact]
    public async Task TheSecondTierIsBoundOnlyWhenConfiguredAndReady()
    {
        await using (var host = TestHost.Create(new FakeModels(), Kev("fake-kev-large")))
        await using (var runtime = await host.StartAsync())
        {
            Assert.Equal("fake-kev-large", runtime.Fabric.EscalationDecider);
            Assert.Equal(["fake-kev-large@cpu"], runtime.Registry.Get(ModelSlots.Decider).Escalation.Select(binding => binding.Profile.Id));
            Assert.Contains("fake-kev-large for gates below their floor", runtime.DeciderState, StringComparison.Ordinal);
        }

        await using (var host = TestHost.Create(new FakeModels(kevLargeReady: false), Kev("fake-kev-large")))
        await using (var runtime = await host.StartAsync())
        {
            Assert.Equal("fake-kev", runtime.Fabric.CalibratedDecider);
            Assert.Null(runtime.Fabric.EscalationDecider);
            Assert.Contains("corvid model download fake-kev-large", Assert.Single(runtime.DeciderNotices), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AKevThatFailsToLoadForAnyReasonLeavesTheDegradedTier()
    {
        // ONNX Runtime throws its own exception type, for example when Kev-4B does not fit in memory.
        var models = new FakeModels();
        models.Provider.Add("fake-kev-broken", [ModelSlots.Decider], () => throw new BrokenModelException("bad allocation"),
            tools: ToolCallSupport.None);
        await using var host = TestHost.Create(models, new Dictionary<string, string?> { ["Corvid:Models:Slots:decider:Model"] = "fake-kev-broken" });
        await using var runtime = await host.StartAsync();

        Assert.Null(runtime.Fabric.CalibratedDecider);
        Assert.Contains("did not load (bad allocation)", Assert.Single(runtime.DeciderNotices), StringComparison.Ordinal);
        Assert.Null((await runtime.OpenSession().SendAsync("hello", Ct)).Error);
    }

    private sealed class BrokenModelException(string message) : Exception(message);

    [Fact]
    public async Task TheDeciderIsSharedSoModelRefusesToSwitchItPerSessionAndStatusShowsBothTiers()
    {
        await using var host = TestHost.Create(new FakeModels(), Kev("fake-kev-large"));
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        ValueTask<CommandOutput> Run(string line) => commands.ExecuteAsync(line, new SlashCommandContext(runtime, session, commands), Ct);

        var refused = await Run("/model decider fake-kev-large");
        Assert.Equal(OutputTone.Error, refused.Tone);
        Assert.Contains("corvid model switch decider", refused.Lines[0], StringComparison.Ordinal);
        Assert.Equal(OutputTone.Error, (await Run("/model fake-kev-large")).Tone);
        Assert.Equal("fake-kev@cpu", session.Slots.Current(ModelSlots.Decider).Profile.Id);

        var status = await Run("/status");
        Assert.Contains(status.Table!.Rows, row => row[0] == ModelSlots.Decider && row[1] == "fake-kev@cpu");
        Assert.Contains(status.Table.Rows, row => row[0] == ModelSlots.Decider && row[1] == "fake-kev-large@cpu (second tier)");
        Assert.Contains(status.Table.Rows, row => row[0] == ModelSlots.Decider && row[1] == "degraded (fallback)");
        Assert.Contains(status.Lines, line => line.StartsWith("decider: fake-kev", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DoctorWarnsRatherThanFailsWithoutKev()
    {
        await using (var host = TestHost.Create(new FakeModels(kevReady: false), Kev()))
        {
            var checks = await host.Services.GetRequiredService<Doctor>().RunAsync(cancellationToken: Ct);
            var decider = Assert.Single(checks, check => check.Name == ModelSlots.Decider);
            Assert.Equal(CheckStatus.Warning, decider.Status);
            Assert.Contains("degraded tier", decider.Detail, StringComparison.Ordinal);
            Assert.Equal("corvid model download fake-kev", decider.Fix);
            Assert.Equal(CheckStatus.Warning, Assert.Single(checks, check => check.Name == "fake-kev").Status);
            Assert.DoesNotContain(checks, check => check.Status == CheckStatus.Failed && check.Area == "models");
        }

        await using (var host = TestHost.Create(new FakeModels(), Kev("fake-kev-large")))
        {
            var checks = await host.Services.GetRequiredService<Doctor>().RunAsync(cancellationToken: Ct);
            var decider = Assert.Single(checks, check => check.Name == ModelSlots.Decider);
            Assert.Equal(CheckStatus.Ok, decider.Status);
            Assert.Contains("with fake-kev-large for gates below their floor", decider.Detail, StringComparison.Ordinal);
        }
    }
}
