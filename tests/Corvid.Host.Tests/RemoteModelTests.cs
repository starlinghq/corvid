using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Corvid.Models.Privacy;
using Corvid.Vault.Contracts;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FakeVault = Corvid.Models.Remote.Tests.FakeVault;

namespace Corvid.Host.Tests;

/// <summary>C-015: remote models from configuration, Vault as the only key path, and the egress gate in the composition.</summary>
public sealed class RemoteModelTests
{
    private const string Reply = """
        {"id":"msg_1","type":"message","role":"assistant","model":"claude-opus-5-5",
         "content":[{"type":"text","text":"Hello from Claude"}],"stop_reason":"end_turn","stop_sequence":null,
         "usage":{"input_tokens":12,"output_tokens":4}}
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Dictionary<string, string?> Claude(string socket, string slotKey = "Corvid:Models:Slots:generator:Escalation:1") => new()
    {
        ["Corvid:Models:Remote:claude:Provider"] = "anthropic",
        ["Corvid:Models:Remote:claude:Model"] = "claude-opus-5-5",
        ["Corvid:Models:Remote:claude:Secret"] = "anthropic.key",
        ["Corvid:Models:Remote:claude:ContextWindow"] = "1000000",
        ["Corvid:Models:Remote:claude:AcceptedContext"] = "200000",
        ["Corvid:Vault:SocketPath"] = socket,
        [slotKey] = "claude",
    };

    private static TestHost Host(IReadOnlyDictionary<string, string?> settings) =>
        TestHost.Create(settings: settings, services: services =>
            services.AddSingleton<IModelProvider>(provider => new RemoteModelProvider(provider.GetRequiredService<IConfiguration>(), network: null)));

    [Fact]
    public void Entries_that_would_leak_or_cannot_work_are_refused_with_reasons()
    {
        var provider = new RemoteModelProvider(new Dictionary<string, RemoteModelSettings>
        {
            ["keyless-cloud"] = new() { Provider = "openai", Model = "gpt-5" },
            ["public-server"] = new() { Provider = "openai-compatible", Model = "m", Endpoint = "http://203.0.113.7:8080/v1" },
            ["plain-key"] = new() { Provider = "openai-compatible", Model = "m", Endpoint = "http://127.0.0.1:8080/v1", Secret = "vllm.key" },
            ["bad"] = new() { Provider = "carrier-pigeon", Model = "m" },
            ["private"] = new() { Provider = "anthropic", Model = "claude-opus-5-5", Secret = "anthropic.key", Privacy = PrivacyClass.LocalServer },
            ["ok-local"] = new() { Provider = "openai-compatible", Model = "qwen3.5-27b", Endpoint = "http://localhost:8080/v1" },
            ["ok-cloud"] = new() { Provider = "gemini", Model = "gemini-3-pro", Secret = "gemini.key" },
        }, vaultSocket: "/nonexistent/vault.sock");

        Assert.Equal(["ok-cloud", "ok-local"], provider.Models.Select(model => model.Name).Order(StringComparer.Ordinal));
        var problems = string.Join("\n", provider.Problems);
        Assert.Contains("keyless-cloud:Secret must name the Vault secret", problems, StringComparison.Ordinal);
        Assert.Contains("public-server:Endpoint 203.0.113.7 is not a loopback or private address", problems, StringComparison.Ordinal);
        Assert.Contains("plain-key:Endpoint must use https when a Secret is set", problems, StringComparison.Ordinal);
        Assert.Contains("bad:Provider must be one of", problems, StringComparison.Ordinal);
        Assert.Contains("private:Privacy can be set only for openai-compatible", problems, StringComparison.Ordinal);

        var cloud = provider.Models.Single(model => model.Name == "ok-cloud").Profile;
        Assert.Equal(("ok-cloud@gemini", PrivacyClass.Cloud, PromptCachingStyle.Automatic, "generativelanguage.googleapis.com"),
            (cloud.Id, cloud.Privacy, cloud.Caching, cloud.Placement));
        Assert.Equal(PrivacyClass.LocalServer, provider.Models.Single(model => model.Name == "ok-local").Profile.Privacy);
    }

    [Theory]
    [InlineData("localhost", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("[::1]", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.0.2", true)]
    [InlineData("gpu-box", true)]
    [InlineData("gpu-box.lan", true)]
    [InlineData("203.0.113.7", false)]
    [InlineData("172.32.0.1", false)]
    [InlineData("api.example.com", false)]
    public void Local_server_endpoints_must_be_on_the_users_own_network(string host, bool local) =>
        Assert.Equal(local, RemoteModelProvider.IsLocalHost(host));

    [Fact]
    public async Task Without_vault_a_cloud_escalation_target_is_left_out_and_the_rest_runs()
    {
        await using var host = Host(Claude("{root}/no-vault.sock"));
        await using var runtime = await host.StartAsync();

        Assert.Equal(["fake-gen-large@cpu"], runtime.Registry.Get(ModelSlots.Generator).Escalation.Select(binding => binding.Profile.Id));
        var unavailable = Assert.Single(runtime.UnavailableModels);
        Assert.Equal(ModelReadiness.Unavailable, unavailable.Readiness);
        Assert.Contains("Vault, which is not running", unavailable.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_vault_a_cloud_default_stops_the_start()
    {
        await using var host = Host(Claude("{root}/no-vault.sock", "Corvid:Models:Slots:summarizer:Model"));
        await host.Host.StartAsync(Ct);

        var exception = await Assert.ThrowsAsync<ModelsUnavailableException>(() =>
            host.Services.GetRequiredService<CorvidRuntimeFactory>().StartAsync(Ct));

        Assert.Equal("claude", Assert.Single(exception.Missing).Model.Name);
    }

    [Fact]
    public async Task A_secret_missing_from_vault_or_bound_to_another_host_is_unavailable()
    {
        await using var vault = new FakeVault(_ => FakeVault.Json(Reply));
        var provider = new RemoteModelProvider(new Dictionary<string, RemoteModelSettings>
        {
            ["claude"] = new() { Provider = "anthropic", Model = "claude-opus-5-5", Secret = "anthropic.key" },
            ["local"] = new() { Provider = "openai-compatible", Model = "m", Endpoint = "http://127.0.0.1:8080/v1" },
        }, vault.SocketPath);

        Assert.Contains("has no secret 'anthropic.key'", provider.Check("claude").Detail, StringComparison.Ordinal);
        vault.Catalogue.Add(new CatalogEntry("anthropic.key", "Anthropic", ["api.openai.com"], []));
        Assert.Contains("not bound to api.anthropic.com", provider.Check("claude").Detail, StringComparison.Ordinal);
        vault.Catalogue[0] = new CatalogEntry("anthropic.key", "Anthropic", ["api.anthropic.com"], []);
        Assert.True(provider.Check("claude").IsReady);
        Assert.True(provider.Check("local").IsReady);
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DownloadAsync("claude", cancellationToken: Ct));
    }

    [Fact]
    public async Task A_cloud_model_is_composed_behind_the_egress_gate_and_answers_through_vault()
    {
        await using var vault = new FakeVault(_ => FakeVault.Json(Reply));
        vault.Catalogue.Add(new CatalogEntry("anthropic.key", "Anthropic", ["api.anthropic.com"], []));
        await using var host = Host(Claude(vault.SocketPath));
        await using var runtime = await host.StartAsync();

        var cloud = runtime.Registry.Get(ModelSlots.Generator).Escalation.Single(binding => binding.Profile.Privacy == PrivacyClass.Cloud);
        Assert.NotNull(((IChatClient)cloud.Service).GetService(typeof(PrivacyEgressChatClient)));
        // Local bindings are not gated.
        Assert.Null(((IChatClient)runtime.Registry.Get(ModelSlots.Generator).Default.Service).GetService(typeof(PrivacyEgressChatClient)));
        Assert.True(runtime.Egress.HasExtractor);

        var session = runtime.OpenSession();
        var switched = await session.Actor.SwitchModelAsync(ModelSlots.Generator, cloud, pin: true, Ct);
        Assert.Equal(SwitchStatus.Switched, switched.Status);
        var delivery = await session.SendAsync("hello there", Ct);

        Assert.Null(delivery.Error);
        Assert.Equal((TurnStatus.Answered, "Hello from Claude"), (delivery.Result!.Status, delivery.Result.Text));
        var request = vault.Forwarded.Last();
        Assert.Equal(vault.Minted.Last(), FakeVault.Header(request, "x-api-key"));
        Assert.DoesNotContain("corvid.", FakeVault.Body(request).ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Egress_policy_comes_from_the_model_over_the_global_privacy_settings()
    {
        var settings = Claude("{root}/no-vault.sock");
        settings["Corvid:Privacy:ExtraLabels:0"] = "medical condition";
        settings["Corvid:Models:Remote:claude:Egress:Action"] = "Refuse";
        settings["Corvid:Models:Remote:local:Provider"] = "openai-compatible";
        settings["Corvid:Models:Remote:local:Model"] = "m";
        settings["Corvid:Models:Remote:local:Endpoint"] = "http://127.0.0.1:8080/v1";
        await using var host = Host(settings);
        await host.Host.StartAsync(Ct);
        var catalogue = host.Services.GetRequiredService<ModelCatalogue>();
        using var devices = new DeviceSchedulers();
        using var composer = new ModelComposer(catalogue, host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CorvidOptions>>().Value,
            devices, new Telemetry.ModelMetrics(), new Telemetry.TurnFeed());

        var claude = composer.PolicyFor(catalogue.Find("claude").Profile)!;
        Assert.Equal(EgressAction.Refuse, claude.Action);
        Assert.Contains("medical condition", claude.AllLabels);
        Assert.Contains("person", claude.AllLabels);
        Assert.Null(composer.PolicyFor(catalogue.Find("local").Profile));
        Assert.Null(composer.PolicyFor(catalogue.Find("fake-gen").Profile));
    }
}
