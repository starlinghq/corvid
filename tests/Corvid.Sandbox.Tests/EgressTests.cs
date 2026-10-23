using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Corvid.Sandbox.Launcher;
using Corvid.Vault.Client;
using Corvid.Vault.Contracts;
using Corvid.Vault.Tests;

namespace Corvid.Sandbox.Tests;

/// <summary>
/// V-003b: sandboxed commands reach HTTPS through Corvid Vault's per-sandbox proxy and nothing else. Real bubblewrap, the
/// real launcher and its relay, and a real Vault in process whose upstream handler (production, changed only to dial it)
/// reaches a loopback TLS server for every host. The secret is bound to <c>bound.test</c>; the commands are curl and
/// Python's standard library, as a script would run them.
/// </summary>
[Trait("Category", "Confinement")]
public sealed class EgressTests : IDisposable
{
    private const string Bound = "bound.test";
    private const string Other = "other.test";
    private const string WriteOut = "proxied=%{http_code}\\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SandboxLayout _layout = new();
    private readonly TlsEchoServer _echo = new();
    private readonly TestVault _vault;

    public EgressTests()
    {
        _vault = new TestVault(p => p.Secrets[TestVault.SecretName].Hosts = [Bound], _echo.UpstreamHandler());
    }

    public void Dispose()
    {
        _vault.Dispose();
        _echo.Dispose();
        _layout.Dispose();
    }

    private VaultClient Client => _vault.Client("shell-session");

    private Task<SandboxOpenResponse> OpenAsync(bool network = false) => Client.OpenSandboxAsync("shell", tainted: false, network, 5, Ct);

    private SandboxPolicy Proxied(SandboxOpenResponse open) =>
        _layout.Policy with { Proxy = new SandboxProxy(open.ProxySocketPath, open.CaCertificatePem) };

    private Task<SandboxResult> RunAsync(SandboxOpenResponse open, string script, string? stdin = null) =>
        _layout.Sandbox().RunAsync(Proxied(open), SandboxCommand.Shell(script) with { StandardInput = stdin }, Ct);

    private static void Ran(SandboxResult result) =>
        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}\nstdout: {result.StandardOutput}\nstderr: {result.StandardError}");

    // (1)
    [Fact]
    public async Task Curl_reaches_the_bound_host_through_vault_with_the_placeholder_substituted_and_scrubbed()
    {
        Host.RequireBubblewrap();
        SandboxOpenResponse open = await OpenAsync();
        string placeholder = await Client.MintPlaceholderAsync(new SecretRef(TestVault.SecretName), Ct);

        SandboxResult result = await RunAsync(open, $"curl -sS --fail-with-body -H 'Authorization: Bearer {placeholder}' https://{Bound}/echo");

        Ran(result);
        Assert.Equal("Bearer " + _vault.SecretValue, Assert.Single(_echo.ReceivedAuthorization));
        Assert.Equal("you sent: Bearer [secret:test.token]", result.StandardOutput);
        Assert.DoesNotContain(_vault.SecretValue, result.StandardOutput + result.StandardError, StringComparison.Ordinal);
        Assert.Equal(1, _vault.Core.Audit.Counts.GetValueOrDefault("sandbox_forward.allowed"));
        await Client.CloseSandboxAsync(open.SandboxId, Ct);
    }

    // (2)
    [Fact]
    public async Task Python_urllib_uses_the_proxy_and_the_sandbox_ca_from_the_environment()
    {
        Host.RequireBubblewrap();
        Assert.SkipUnless(File.Exists("/usr/bin/python3"), "/usr/bin/python3 is not installed.");
        SandboxOpenResponse open = await OpenAsync();
        string placeholder = await Client.MintPlaceholderAsync(new SecretRef(TestVault.SecretName), Ct);
        File.WriteAllText(Path.Combine(_layout.Workspace, "fetch.py"), $$"""
            import urllib.request
            request = urllib.request.Request("https://{{Bound}}/py", headers={"Authorization": "Bearer {{placeholder}}"})
            with urllib.request.urlopen(request, timeout=30) as response:
                print(response.status, response.read().decode())
            """);

        SandboxResult result = await RunAsync(open, "/usr/bin/python3 -I fetch.py");

        Ran(result);
        Assert.Equal("200 you sent: Bearer [secret:test.token]\n", result.StandardOutput);
        Assert.Equal("Bearer " + _vault.SecretValue, Assert.Single(_echo.ReceivedAuthorization));
    }

    // (3)
    [Fact]
    public async Task Pass_through_needs_network_and_is_scrubbed()
    {
        Host.RequireBubblewrap();
        _echo.Extra = " and " + _vault.SecretValue; // an upstream that echoes a stored value back
        SandboxOpenResponse online = await OpenAsync(network: true);
        SandboxOpenResponse offline = await OpenAsync(network: false);
        const string Fetch = $"curl -sS https://{Other}/public; echo \" exit=$?\"";

        SandboxResult passed = await RunAsync(online, Fetch);
        SandboxResult refused = await RunAsync(offline, Fetch);

        Assert.Equal("you sent:  and [secret:test.token] exit=0\n", passed.StandardOutput);
        Assert.DoesNotContain(_vault.SecretValue, passed.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("403", refused.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("exit=0", refused.StandardOutput, StringComparison.Ordinal);
        Assert.Single(_echo.Received);
        Assert.Contains("\"code\":\"network_not_granted\"", await File.ReadAllTextAsync(_vault.Options.AuditPath, Ct), StringComparison.Ordinal);
    }

    // (4)
    [Fact]
    public async Task There_is_no_route_out_except_the_proxy()
    {
        Host.RequireBubblewrap();
        SandboxOpenResponse open = await OpenAsync(network: true);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string vaultRun = Path.GetDirectoryName(_vault.Options.SocketPath)!;
        string script = $"""
            curl -sS --max-time 10 --noproxy '*' https://1.1.1.1/ > /dev/null; echo "direct-curl=$?"
            (exec 3<>/dev/tcp/1.1.1.1/443) 2>&1; echo "direct-tcp=$?"
            (exec 3<>/dev/tcp/127.0.0.1/{port}) 2> /dev/null; echo "host-loopback=$?"
            echo "interfaces=$(grep -c : /proc/net/dev)"
            echo "run=$(ls -A /run/corvid-sandbox | tr '\n' ' ')"
            echo "vault-sockets=$(ls '{vaultRun}' '{vaultRun}/sandbox' 2> /dev/null | grep -c sock)"
            curl -sS -o /dev/null -w '{WriteOut}' https://{Other}/
            """;

        SandboxResult result = await RunAsync(open, script);

        string output = result.StandardOutput;
        Assert.DoesNotContain("direct-curl=0\n", output, StringComparison.Ordinal);
        Assert.Contains("direct-curl=", output, StringComparison.Ordinal);
        Assert.Contains("Network is unreachable", output, StringComparison.Ordinal);
        Assert.Contains("direct-tcp=1\n", output, StringComparison.Ordinal);
        Assert.Contains("host-loopback=1\n", output, StringComparison.Ordinal);
        Assert.False(listener.Pending());
        // Loopback only, from /proc/net/dev: /sys/class/net shows the host's sysfs, which bubblewrap binds read-only.
        Assert.Contains("interfaces=1\n", output, StringComparison.Ordinal);
        Assert.Contains("run=ca.pem proxy.sock \n", output, StringComparison.Ordinal);
        Assert.Contains("vault-sockets=0\n", output, StringComparison.Ordinal); // Vault's own socket and the sandbox directory
        Assert.EndsWith("proxied=200\n", output, StringComparison.Ordinal); // the control: the proxy works in the same run
    }

    // (5)
    [Fact]
    public async Task The_trust_bundle_is_the_sandbox_ca_alone_and_the_host_store_is_untouched()
    {
        Host.RequireBubblewrap();
        SandboxOpenResponse open = await OpenAsync();
        string before = HostTrustStore();

        SandboxResult result = await RunAsync(open, "sha256sum < \"$SSL_CERT_FILE\" | cut -d' ' -f1; ls /run/corvid-sandbox/*.pem; env | sort");

        Ran(result);
        string[] lines = result.StandardOutput.Split('\n');
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(open.CaCertificatePem))), lines[0]);
        Assert.Equal("/run/corvid-sandbox/ca.pem", lines[1]); // the bundle is that one file, nothing from the host
        foreach (var (name, value) in SandboxPlan.ProxyEnvironment)
        {
            Assert.Contains($"{name}={value}", lines);
        }

        Assert.Equal(before, HostTrustStore());
    }

    // (6)
    [Fact]
    public async Task The_relay_is_confined_and_exit_status_and_input_pass_through()
    {
        Host.RequireBubblewrap();
        SandboxOpenResponse open = await OpenAsync();
        string outside = Path.Combine(_layout.Outside, "relay-child-wrote");
        string script = $"""
            grep -E '^(NoNewPrivs|Seccomp):' /proc/$PPID/status
            tr '\0' '\n' < /proc/$PPID/cmdline | grep -cx -- --relay-run
            echo x > '{outside}' 2> /dev/null || echo landlock-refused
            cat
            exit 7
            """;

        SandboxResult result = await RunAsync(open, script, stdin: "from stdin");

        Assert.Equal(7, result.ExitCode);
        Assert.Matches(@"NoNewPrivs:\s+1\n", result.StandardOutput);
        Assert.Matches(@"Seccomp:\s+2\n", result.StandardOutput);
        Assert.Contains("1\nlandlock-refused\nfrom stdin", result.StandardOutput, StringComparison.Ordinal);
        Assert.False(File.Exists(outside));
        Assert.Contains(result.Layers, l => l.StartsWith("landlock-v", StringComparison.Ordinal));

        SandboxResult killed = await RunAsync(open, "kill -TERM $$");
        Assert.Equal(128 + 15, killed.ExitCode);
    }

    [Fact]
    public async Task The_launcher_refuses_unsafe_proxy_arguments()
    {
        Host.RequireBubblewrap();
        SandboxOpenResponse open = await OpenAsync();
        string ca = Path.Combine(_layout.Outside, "ca.pem");
        File.WriteAllText(ca, open.CaCertificatePem);
        string link = Path.Combine(_layout.Outside, "ca-link.pem");
        File.CreateSymbolicLink(link, ca);
        string withKey = Path.Combine(_layout.Outside, "with-key.pem");
        using (var key = ECDsa.Create())
        {
            File.WriteAllText(withKey, open.CaCertificatePem + key.ExportPkcs8PrivateKeyPem());
        }

        string inWorkspace = Path.Combine(_layout.Workspace, "ca.pem");
        File.WriteAllText(inWorkspace, open.CaCertificatePem);
        string marker = Path.Combine(_layout.Workspace, "ran");
        string[] proxy = ["--proxy-socket", open.ProxySocketPath, "--proxy-ca", ca];

        (string[] Arguments, string Expected)[] cases =
        [
            ([.. proxy, "--network"], "cannot be combined with --network"),
            (["--proxy-socket", open.ProxySocketPath], "go together"),
            (["--proxy-socket", open.ProxySocketPath, "--proxy-ca", link], "is a symlink"),
            (["--proxy-socket", open.ProxySocketPath, "--proxy-ca", withKey], "private key"),
            (["--proxy-socket", open.ProxySocketPath, "--proxy-ca", inWorkspace], "inside the workspace"),
            ([.. proxy, "--env", "HTTPS_PROXY"], "collides with a variable the proxy sets"),
            ([.. proxy, "--env", "ssl_cert_file"], "collides with a variable the proxy sets"),
        ];
        foreach (var (arguments, expected) in cases)
        {
            using Process launcher = Process.Start(_layout.Launcher(["--workspace", _layout.Workspace, .. arguments, "--", "/usr/bin/touch", marker]))!;
            Task<string> stdout = launcher.StandardOutput.ReadToEndAsync(Ct);
            string stderr = await launcher.StandardError.ReadToEndAsync(Ct);
            await launcher.WaitForExitAsync(Ct);
            await stdout;
            Assert.True(launcher.ExitCode == 125, $"{string.Join(' ', arguments)}: exit {launcher.ExitCode}, {stderr}");
            Assert.Contains(expected, stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(marker));
        }
    }

    /// <summary>Every path under /etc/ssl with its size and modification time, symlinks not followed.</summary>
    private static string HostTrustStore()
    {
        if (!Directory.Exists("/etc/ssl"))
        {
            return "";
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
        return string.Join('\n', new DirectoryInfo("/etc/ssl").EnumerateFileSystemInfos("*", options)
            .Select(i => $"{i.FullName} {(i is FileInfo f && i.LinkTarget is null ? f.Length : 0)} {i.LastWriteTimeUtc.Ticks} {i.LinkTarget}")
            .Order(StringComparer.Ordinal));
    }
}
