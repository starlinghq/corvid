using Corvid.Sandbox.Launcher;

namespace Corvid.Sandbox.Tests;

/// <summary>V-003b without bubblewrap: the launcher's proxy options, the plan's mounts and variables, and the library's checks.</summary>
public sealed class EgressUnitTests
{
    private const string Pem = "-----BEGIN CERTIFICATE-----\nMIIBfake+/=\n-----END CERTIFICATE-----\n";

    [Fact]
    public void The_proxy_options_parse_and_the_usage_documents_them()
    {
        LaunchRequest request = Arguments.ParseLaunch(["--workspace", "/w", "--proxy-socket", "/run/v/sandbox/a.sock", "--proxy-ca", "/s/ca.pem", "--", "curl"]);

        Assert.Equal("/run/v/sandbox/a.sock", request.ProxySocket);
        Assert.Equal("/s/ca.pem", request.ProxyCa);
        Assert.False(request.Network);
        Assert.Contains("--proxy-socket PATH", Arguments.Usage, StringComparison.Ordinal);
        Assert.Contains("--proxy-ca PATH", Arguments.Usage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--workspace /w --proxy-socket /s -- true")]
    [InlineData("--workspace /w --proxy-ca /c -- true")]
    [InlineData("--workspace /w --proxy-socket /s --proxy-ca /c --network -- true")]
    [InlineData("--workspace /w --network --proxy-ca /c --proxy-socket /s -- true")]
    [InlineData("--workspace /w --proxy-socket -- true")]
    public void Proxy_options_without_their_pair_or_with_network_are_refused(string args)
    {
        Assert.Throws<RefusedException>(() => Arguments.ParseLaunch(args.Split(' ')));
    }

    [Fact]
    public void Stage_2_and_the_relay_read_their_options()
    {
        Stage2Request stage2 = Arguments.ParseStage2(["--stage2", "--ready-fd", "7", "--relay", SandboxPlan.ProxySocketInside, "--", "sh"]);
        Assert.Equal(SandboxPlan.ProxySocketInside, stage2.Relay);
        Assert.Null(Arguments.ParseStage2(["--stage2", "--", "sh"]).Relay);

        RelayRequest relay = Arguments.ParseRelay(
            [Arguments.RelayFlag, "--listen-fd", "5", "--socket", "/run/corvid-sandbox/proxy.sock", "--env", "PATH=/bin", "--env", "A=b=c", "--", "sh", "-c", "x"]);
        Assert.Equal(5, relay.ListenFd);
        Assert.Equal("/run/corvid-sandbox/proxy.sock", relay.Socket);
        Assert.Equal(["PATH=/bin", "A=b=c"], relay.Environment);
        Assert.Equal(["sh", "-c", "x"], relay.Command);
        Assert.Throws<RefusedException>(() => Arguments.ParseRelay([Arguments.RelayFlag, "--listen-fd", "2", "--socket", "/s", "--", "sh"]));
        Assert.Throws<RefusedException>(() => Arguments.ParseRelay([Arguments.RelayFlag, "--socket", "/s", "--", "sh"]));
        Assert.Throws<RefusedException>(() => Arguments.ParseRelay([Arguments.RelayFlag, "--listen-fd", "5", "--", "sh"]));
    }

    [Fact]
    public void The_proxy_mounts_come_after_the_masks_and_run_and_stage_2_relays()
    {
        var plan = new SandboxPlan
        {
            Bubblewrap = "/usr/bin/bwrap",
            Workspace = "/home/u/project",
            WorkingDirectory = "/home/u/project",
            ReadOnlyPaths = [],
            MaskedDirectories = ["/run/user/1000", "/home/u/.ssh"],
            MaskedFiles = ["/home/u/project/.env"],
            Environment = ["PATH=/usr/bin", .. SandboxPlan.ProxyEnvironment.Select(p => p.Key + "=" + p.Value)],
            Network = false,
            BestEffortLayers = false,
            ProxySocket = "/run/user/1000/corvid-vault/sandbox/abc.sock",
            ProxyCa = "/tmp/corvid-sandbox-x/ca.pem",
            Stage2 = ["/opt/corvid-sandbox"],
            Stage2Environment = [],
            OutsideNamespaces = ["net=net:[1]"],
            Command = ["curl"],
        };

        List<string> a = [.. plan.BubblewrapArguments(readyFd: 9)];

        int Index(params string[] run) => Enumerable.Range(0, a.Count - run.Length + 1).First(i => a.Skip(i).Take(run.Length).SequenceEqual(run));
        int socket = Index("--ro-bind", "/run/user/1000/corvid-vault/sandbox/abc.sock", "/run/corvid-sandbox/proxy.sock");
        int ca = Index("--ro-bind", "/tmp/corvid-sandbox-x/ca.pem", "/run/corvid-sandbox/ca.pem");
        Assert.True(Index("--tmpfs", "/run") < socket);
        Assert.True(Index("--perms", "0000", "--tmpfs", "/run/user/1000") < socket, "a mask over the runtime directory must not cover the proxy");
        Assert.True(Index("--ro-bind", "/dev/null", "/home/u/project/.env") < socket);
        Assert.Equal(socket + 3, ca);
        Assert.DoesNotContain("--share-net", a);
        Assert.DoesNotContain(a, s => s.Contains("vault.sock", StringComparison.Ordinal));
        // Only the two files are mounted there (the third mention is stage 2's --relay argument): nothing else of Vault's.
        Assert.Equal(3, a.Count(s => s.StartsWith("/run/corvid-sandbox/", StringComparison.Ordinal)));
        Assert.Equal(1, a.Count(s => s.Contains("corvid-vault", StringComparison.Ordinal)));

        int separator = a.IndexOf("--");
        List<string> stage2 = a[(separator + 1)..];
        int relay = stage2.IndexOf("--relay");
        Assert.Equal(SandboxPlan.ProxySocketInside, stage2[relay + 1]);
        Assert.True(relay < stage2.IndexOf("--"));
        Assert.Contains("--not-in", stage2);
        Assert.DoesNotContain("--relay", (plan with { ProxySocket = null, ProxyCa = null }).BubblewrapArguments(9));
    }

    [Fact]
    public void The_proxy_environment_names_the_relay_and_the_sandbox_ca_alone()
    {
        var env = SandboxPlan.ProxyEnvironment.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        foreach (string name in (string[])["https_proxy", "HTTPS_PROXY", "http_proxy", "HTTP_PROXY"])
        {
            Assert.Equal("http://127.0.0.1:3128", env[name]);
        }

        Assert.Equal("localhost,127.0.0.1,::1", env["no_proxy"]);
        Assert.Equal("localhost,127.0.0.1,::1", env["NO_PROXY"]);
        foreach (string name in (string[])["SSL_CERT_FILE", "CURL_CA_BUNDLE", "REQUESTS_CA_BUNDLE", "NODE_EXTRA_CA_CERTS", "GIT_SSL_CAINFO", "PIP_CERT"])
        {
            Assert.Equal("/run/corvid-sandbox/ca.pem", env[name]);
        }

        Assert.Equal("/run/corvid-sandbox", env["SSL_CERT_DIR"]);
        Assert.Equal("1", env["NODE_USE_ENV_PROXY"]);
        Assert.Equal(3128, Relay.Port);
        // The library refuses the same names before anything leaves Corvid.
        Assert.Equal(SandboxPlan.ProxyEnvironment.Select(p => p.Key).Order(StringComparer.Ordinal),
            SandboxPolicy.ProxyEnvironmentNames.Order(StringComparer.Ordinal));
        // None of them is a secret-like name, so the launcher's own rule never refuses what it sets itself.
        Assert.DoesNotContain(env.Keys, SandboxPlan.IsSecretLike);
    }

    [Theory]
    [InlineData(Pem, true)]
    [InlineData(Pem + Pem, true)]
    [InlineData("  \n" + Pem, true)]
    [InlineData("", false)]
    [InlineData("hello", false)]
    [InlineData(Pem + "trailing", false)]
    [InlineData("-----BEGIN PUBLIC KEY-----\nMIIB\n-----END PUBLIC KEY-----\n", false)]
    public void The_ca_file_holds_certificates_only(string text, bool expected)
    {
        Assert.Equal(expected, SandboxPlan.IsCertificatesOnly(text));
    }

    [Fact]
    public void The_policy_maps_a_proxy_to_the_launcher_options()
    {
        SandboxPolicy policy = SandboxPolicy.ForWorkspace("/w") with { Proxy = new SandboxProxy("/run/v/sandbox/a.sock", Pem) };

        List<string> args = BubblewrapSandbox.Arguments(policy, new SandboxCommand("curl"), "/s/status.json", new SandboxOptions(), "/s/ca.pem");

        int at = args.IndexOf("--proxy-socket");
        Assert.Equal(["--proxy-socket", "/run/v/sandbox/a.sock", "--proxy-ca", "/s/ca.pem"], args[at..(at + 4)]);
        Assert.DoesNotContain("--network", args);
        LaunchRequest request = Arguments.ParseLaunch(args);
        Assert.Equal("/run/v/sandbox/a.sock", request.ProxySocket);
        Assert.Equal("/s/ca.pem", request.ProxyCa);
        Assert.Throws<ArgumentException>(() => BubblewrapSandbox.Arguments(policy, new SandboxCommand("curl"), null, new SandboxOptions()));
    }

    [Fact]
    public void Validate_checks_the_proxy()
    {
        SandboxPolicy good = SandboxPolicy.ForWorkspace("/w") with { Proxy = new SandboxProxy("/run/v/sandbox/a.sock", Pem) };
        good.Validate();

        Assert.Throws<ArgumentException>(() => (good with { Network = true }).Validate());
        Assert.Throws<ArgumentException>(() => (good with { Proxy = new SandboxProxy("relative.sock", Pem) }).Validate());
        Assert.Throws<ArgumentException>(() => (good with { Proxy = new SandboxProxy("/a.sock", "not a certificate") }).Validate());
        Assert.Throws<ArgumentException>(() => (good with { Proxy = new SandboxProxy("/a.sock", Pem + "-----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY-----\n") }).Validate());
        Assert.Throws<ArgumentException>(() => (good with { Environment = ["PATH", "HTTPS_PROXY"] }).Validate());
        Assert.Throws<ArgumentException>(() => (good with { Environment = ["PATH", "Ssl_Cert_File"] }).Validate());
        // Without a proxy the names are ordinary: a --network command may pass the user's own proxy settings.
        (SandboxPolicy.ForWorkspace("/w") with { Network = true, Environment = ["PATH", "HTTPS_PROXY", "NO_PROXY"] }).Validate();
    }

    [Fact]
    public void A_prepared_launch_refuses_the_proxy()
    {
        string launcher = Path.Combine(Path.GetTempPath(), "corvid-sandbox-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(launcher, "");
        try
        {
            var sandbox = new BubblewrapSandbox(new SandboxOptions { LauncherPath = launcher, Environment = new Dictionary<string, string>() });
            Assert.Throws<ArgumentException>(() => sandbox.Prepare(
                SandboxPolicy.ForWorkspace("/w") with { Proxy = new SandboxProxy("/run/v/sandbox/a.sock", Pem) }, new SandboxCommand("server")));
        }
        finally
        {
            File.Delete(launcher);
        }
    }
}
