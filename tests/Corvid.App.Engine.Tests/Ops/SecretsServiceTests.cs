using System.Text.Json.Nodes;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Secrets;
using Corvid.App.Engine.Secrets;
using Corvid.Host.Configuration;
using Corvid.Models.Remote.Tests;
using Corvid.Vault.Contracts;

namespace Corvid.App.Engine.Tests.Ops;

/// <summary>
/// The Secrets page over a stand-in Vault on its own socket (never the user's Vault) and the configured features: names,
/// hosts and toolsets, never a value; bindings written to corvid.json.
/// </summary>
public sealed class SecretsServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> Settings(string socket) => new()
    {
        ["Corvid:Vault:SocketPath"] = socket,
        ["Corvid:Gateway:Adapters:telegram:Enabled"] = "true",
        ["Corvid:Gateway:Adapters:telegram:Secret"] = "telegram.bot-token",
    };

    [Fact]
    public async Task WithoutVaultEveryFeatureWaitsAndNothingIsListed()
    {
        await using var fixture = await OpsFixture.StartAsync(Settings("{root}/absent.sock"));
        using var service = new EngineSecretsService(fixture.Engine, new InlineDispatcher(), fixture.Time);

        var snapshot = await service.LoadAsync(Token);

        Assert.Equal(VaultState.Absent, snapshot.Vault.State);
        Assert.Equal(Path.Combine(fixture.Host.Root, "absent.sock"), snapshot.Vault.Socket);
        Assert.Equal(EngineSecretsService.NoCounts, snapshot.Vault.AuditSummary);
        Assert.Empty(snapshot.Secrets);
        var telegram = Assert.Single(snapshot.Consumers);
        Assert.Equal(("gateway:telegram", ConsumerKind.Gateway, "telegram.bot-token", "api.telegram.org"),
            (telegram.Id, telegram.Kind, telegram.Secret, telegram.Host));
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => service.BindAsync("gateway:telegram", "telegram.bot-token", Token));
        Assert.StartsWith("Vault does not answer", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCatalogueGivesNamesHostsAndToolsetsAndThePolicyIsSaidToBeUnreported()
    {
        await using var vault = new FakeVault(_ => FakeVault.Json("{}"));
        vault.Catalogue.Add(new CatalogEntry("telegram.bot-token", "Telegram bot for the gateway", ["api.telegram.org"], ["gateway"]));
        vault.Catalogue.Add(new CatalogEntry("github.token", "GitHub API", ["api.github.com"], []));
        vault.AuditCounts = new() { ["forward.allowed"] = 41, ["sandbox_forward.allowed"] = 1, ["placeholder.allowed"] = 9, ["forward.refused"] = 2, ["scrub.error"] = 1 };
        await using var fixture = await OpsFixture.StartAsync(Settings(vault.SocketPath));
        using var service = new EngineSecretsService(fixture.Engine, new InlineDispatcher(), fixture.Time);

        var snapshot = await service.LoadAsync(Token);

        Assert.Equal(VaultState.Running, snapshot.Vault.State);
        Assert.Equal(vault.SocketPath, snapshot.Vault.Socket);
        Assert.Equal(EngineSecretsService.NotReportedVersion, snapshot.Vault.Version);
        Assert.Equal("since Vault's audit log began: 42 requests sent with a secret · 9 placeholders minted · 2 refused · 1 failed. Vault does not count by day yet (V-n).",
            snapshot.Vault.AuditSummary);
        Assert.Equal(["github.token", "telegram.bot-token"], snapshot.Secrets.Select(secret => secret.Name));
        var telegram = snapshot.Secrets[1];
        Assert.Equal(("Telegram bot for the gateway", "api.telegram.org", "gateway"), (telegram.Description, telegram.Hosts.Single(), telegram.Toolsets.Single()));
        Assert.False(telegram.PolicyReported);
        Assert.Empty(telegram.Methods);
        Assert.Null(telegram.Role);

        // A Vault without the audit call still lists its secrets, and says so.
        vault.AuditCounts = null;
        Assert.StartsWith("Vault gave no audit summary", (await service.LoadAsync(Token)).Vault.AuditSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingASecretNeverPassesThroughTheAppItGivesTheCommand()
    {
        await using var fixture = await OpsFixture.StartAsync(Settings("{root}/absent.sock"));
        using var service = new EngineSecretsService(fixture.Engine, new InlineDispatcher(), fixture.Time);

        var launch = await service.StartSetAsync("gemini.key", Token);

        Assert.Equal(new SetterLaunch(false, "corvid-vault set gemini.key", EngineSecretsService.SetNote), launch);
        await Assert.ThrowsAsync<ArgumentException>(() => service.StartSetAsync("Gemini Key", Token));
    }

    [Fact]
    public async Task BindingChecksTheNameTheHostAndThenWritesCorvidJson()
    {
        await using var vault = new FakeVault(_ => FakeVault.Json("{}"));
        vault.Catalogue.Add(new CatalogEntry("tg.other", "another bot", ["api.telegram.org"], []));
        vault.Catalogue.Add(new CatalogEntry("github.token", "GitHub API", ["api.github.com"], []));
        await using var fixture = await OpsFixture.StartAsync(Settings(vault.SocketPath));
        using var service = new EngineSecretsService(fixture.Engine, new InlineDispatcher(), fixture.Time);
        var changes = 0;
        service.Changed += (_, _) => changes++;

        async Task<Exception> Refused(string consumer, string secret) => await Assert.ThrowsAnyAsync<Exception>(() => service.BindAsync(consumer, secret, Token));
        Assert.IsType<KeyNotFoundException>(await Refused("gateway:nothing", "tg.other"));
        Assert.Equal("Vault has no secret named tg.missing.", (await Refused("gateway:telegram", "tg.missing")).Message);
        Assert.Equal("github.token is not bound to api.telegram.org; Vault would refuse to send it there.", (await Refused("gateway:telegram", "github.token")).Message);
        Assert.Equal("That name holds hidden characters; it is not bound.", (await Refused("gateway:telegram", "tg.\u200Bother")).Message);
        Assert.Equal(0, changes);
        Assert.Equal("", fixture.ConfigText);

        // The test host sets the adapter's secret for this run, as --set would: the file is written, and the page is told
        // that the run's value still wins.
        var shadowed = await Refused("gateway:telegram", "tg.other");
        Assert.Contains("Corvid:Gateway:Adapters:telegram:Secret is set for this run", shadowed.Message, StringComparison.Ordinal);
        Assert.Equal("tg.other", (string?)JsonNode.Parse(fixture.ConfigText)!["Corvid"]!["Gateway"]!["Adapters"]!["telegram"]!["Secret"]);
    }

    [Fact]
    public void TheFeaturesAreThoseCorvidJsonNamesASecretForWithTheHostTheySendItTo()
    {
        var options = new CorvidOptions();
        options.Models.Remote["claude"] = new RemoteModelSettings { Provider = "anthropic", Model = "claude-x", Secret = "anthropic.key" };
        options.Models.Remote["gemini"] = new RemoteModelSettings { Provider = "gemini", Model = "g", Secret = "gemini.key", Slots = ["generator"] };
        options.Models.Remote["box"] = new RemoteModelSettings { Provider = "openai-compatible", Endpoint = "https://llm.example.org/v1", Secret = "box.key" };
        options.Models.Remote["local"] = new RemoteModelSettings { Provider = "openai-compatible", Endpoint = "http://127.0.0.1:8080/v1" };
        options.Gateway.Adapters["slack"] = new GatewayAdapterSettings { Enabled = true, Secret = "slack.bot-token", AppSecret = "slack.app-token" };
        options.Gateway.Adapters["discord"] = new GatewayAdapterSettings { Enabled = false, Secret = "discord.bot-token" };
        options.Mcp.Servers["notes"] = new McpServerSettings { Url = "https://notes.example.org/mcp", Secret = "notes.token" };

        var consumers = EngineSecretsService.Consumers(options);

        Assert.Equal(
            [
                ("model:box", "box.key", "llm.example.org"), ("model:claude", "anthropic.key", "api.anthropic.com"),
                ("model:gemini", "gemini.key", "generativelanguage.googleapis.com"), ("gateway:slack", "slack.bot-token", "slack.com"),
                ("gateway:slack:app", "slack.app-token", "slack.com"), ("mcp:notes", "notes.token", "notes.example.org"),
            ],
            consumers.Select(consumer => (consumer.Id, consumer.Secret, consumer.Host)));
        Assert.Equal("generator", consumers.Single(consumer => consumer.Id == "model:gemini").Detail);
        Assert.Equal(
            ["Corvid:Models:Remote:box:Secret", "Corvid:Models:Remote:claude:Secret", "Corvid:Models:Remote:gemini:Secret", "Corvid:Gateway:Adapters:slack:Secret",
             "Corvid:Gateway:Adapters:slack:AppSecret", "Corvid:Mcp:Servers:notes:Secret"],
            consumers.Select(EngineSecretsService.Key));
    }

    [Fact]
    public async Task TheCompositionMakesTheEnginesSecretsWhichWorkWhileTheRuntimeIsDown()
    {
        await using var host = EngineHosts.Create(new Corvid.Host.Tests.FakeModels(generatorReady: false), Settings("{root}/absent.sock"));
        await using var engine = await AppEngine.StartAsync(host.Host, ownsHost: false, cancellationToken: Token);
        using var composition = new AppComposition(new AppOptions { Demo = false }, new InlineDispatcher(), Corvid.App.Core.Tests.ManualTimeProvider.Demo(),
            new EngineAppServices(engine));

        var service = Assert.IsType<EngineSecretsService>(composition.SecretsService);
        Assert.Equal(VaultState.Absent, (await service.LoadAsync(Token)).Vault.State);
    }
}
