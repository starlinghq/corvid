using System.Diagnostics;
using System.Net;
using Corvid.Vault.Client;
using Corvid.Vault.Contracts;

namespace Corvid.Vault.Tests;

/// <summary>A real socket, a real HTTP server, and Corvid's side using only <see cref="VaultHandler"/>.</summary>
public sealed class EndToEndTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestVault _vault = new();
    private readonly EchoServer _echo = new();

    public void Dispose()
    {
        _echo.Dispose();
        _vault.Dispose();
    }

    private async Task<HttpResponseMessage> GetWithSecretAsync(VaultClient client, string path = "/echo", string? placeholder = null)
    {
        placeholder ??= await client.MintPlaceholderAsync(new SecretRef(TestVault.SecretName), Ct);
        using var http = new HttpClient(new VaultHandler(client));
        using var request = new HttpRequestMessage(HttpMethod.Get, _echo.BaseUrl + path);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + placeholder);
        return await http.SendAsync(request, Ct);
    }

    [Fact]
    public async Task The_upstream_gets_the_value_and_Corvid_gets_it_scrubbed()
    {
        VaultClient client = _vault.Client();

        using HttpResponseMessage response = await GetWithSecretAsync(client);
        string body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Bearer " + _vault.SecretValue, Assert.Single(_echo.ReceivedAuthorization));
        Assert.Equal("you sent: Bearer [secret:test.token]", body);
        Assert.Equal("Bearer [secret:test.token]", Assert.Single(response.Headers.GetValues("X-Echo")));
        Assert.DoesNotContain(_vault.SecretValue, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Compressed_echoes_are_decompressed_and_scrubbed()
    {
        using HttpResponseMessage response = await GetWithSecretAsync(_vault.Client(), "/gzip");

        Assert.Equal("you sent: Bearer [secret:test.token]", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Redirects_are_returned_not_followed_and_their_location_is_scrubbed()
    {
        using HttpResponseMessage response = await GetWithSecretAsync(_vault.Client(), "/redirect");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Single(_echo.ReceivedAuthorization);
        string location = Assert.Single(response.Headers.NonValidated["Location"]);
        Assert.Contains("[secret:test.token]", location, StringComparison.Ordinal);
        Assert.DoesNotContain(_vault.SecretValue, location, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_the_secret_is_not_bound_to_is_refused_before_anything_is_sent()
    {
        _vault.Policy.Secrets[TestVault.SecretName].Hosts = ["api.github.com"];
        _vault.WritePolicy();

        var e = await Assert.ThrowsAsync<VaultException>(() => GetWithSecretAsync(_vault.Client()));

        Assert.Equal(VaultErrorCodes.HostNotBound, e.Code);
        Assert.Empty(_echo.ReceivedAuthorization);
    }

    [Fact]
    public async Task A_placeholder_from_another_session_is_refused()
    {
        string placeholder = await _vault.Client("session-A").MintPlaceholderAsync(new SecretRef(TestVault.SecretName), Ct);

        var e = await Assert.ThrowsAsync<VaultException>(() => GetWithSecretAsync(_vault.Client("session-B"), placeholder: placeholder));

        Assert.Equal(VaultErrorCodes.BadPlaceholder, e.Code);
        Assert.Empty(_echo.ReceivedAuthorization);
    }

    [Fact]
    public async Task Placeholders_in_the_body_or_url_or_repeated_are_refused()
    {
        VaultClient client = _vault.Client();
        string p = await client.MintPlaceholderAsync(new SecretRef(TestVault.SecretName), Ct);
        using var http = new HttpClient(new VaultHandler(client));

        using var inBody = new HttpRequestMessage(HttpMethod.Post, _echo.BaseUrl + "/x")
        {
            Content = new StringContent("token=" + p),
        };
        inBody.Headers.TryAddWithoutValidation("X-Other", "vsec_test.token_" + new string('A', 24));
        using var inUrl = new HttpRequestMessage(HttpMethod.Get, _echo.BaseUrl + "/x?token=" + p);
        using var repeated = new HttpRequestMessage(HttpMethod.Get, _echo.BaseUrl + "/x");
        repeated.Headers.TryAddWithoutValidation("Authorization", "Bearer " + p);
        repeated.Headers.TryAddWithoutValidation("X-Copy", p);

        Assert.Equal(VaultErrorCodes.PlaceholderOutsideHeaders, (await Assert.ThrowsAsync<VaultException>(() => http.SendAsync(inBody, Ct))).Code);
        Assert.Equal(VaultErrorCodes.PlaceholderOutsideHeaders, (await Assert.ThrowsAsync<VaultException>(() => http.SendAsync(inUrl, Ct))).Code);
        Assert.Equal(VaultErrorCodes.PlaceholderRepeated, (await Assert.ThrowsAsync<VaultException>(() => http.SendAsync(repeated, Ct))).Code);
        Assert.Empty(_echo.ReceivedAuthorization);
    }

    [Fact]
    public async Task Writes_need_Corvids_approval()
    {
        VaultClient client = _vault.Client();
        string p = await client.MintPlaceholderAsync(new SecretRef(TestVault.SecretName), Ct);
        using var http = new HttpClient(new VaultHandler(client));

        HttpRequestMessage Post(bool approved)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, _echo.BaseUrl + "/x") { Content = new StringContent("{}") };
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + p);
            request.Options.Set(VaultHandler.Approved, approved);
            return request;
        }

        using var unapproved = Post(approved: false);
        using var approved = Post(approved: true);
        Assert.Equal(VaultErrorCodes.ApprovalRequired, (await Assert.ThrowsAsync<VaultException>(() => http.SendAsync(unapproved, Ct))).Code);
        using HttpResponseMessage response = await http.SendAsync(approved, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Requests_without_placeholders_never_go_to_Vault()
    {
        using var http = new HttpClient(new VaultHandler(_vault.Client(), new SocketsHttpHandler()));

        using HttpResponseMessage response = await http.GetAsync(_echo.BaseUrl + "/plain", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(Assert.Single(_echo.ReceivedAuthorization));
        Assert.False(_vault.Core.Audit.Counts.ContainsKey("forward.allowed"));
    }

    [Fact]
    public async Task The_catalogue_lists_names_and_bindings_never_values()
    {
        CatalogEntry entry = Assert.Single(await _vault.Client().GetCatalogAsync(Ct));

        Assert.Equal(TestVault.SecretName, entry.Name);
        Assert.Equal("Test API token", entry.Description);
        Assert.Equal(["127.0.0.1"], entry.Hosts);
    }

    [Fact]
    public async Task Scrub_service_removes_values_from_arbitrary_tool_output()
    {
        string text = await _vault.Client().ScrubAsync($"cat .env\nAPI_TOKEN={_vault.SecretValue}\n", Ct);

        Assert.Equal("cat .env\nAPI_TOKEN=[secret:test.token]\n", text);
    }

    [Fact]
    public async Task Other_uids_are_rejected_and_audited()
    {
        _vault.Policy.AllowedUids = [Native.CurrentUid() + 1];
        _vault.WritePolicy();

        await Assert.ThrowsAnyAsync<Exception>(() => _vault.Client().GetCatalogAsync(Ct));

        Assert.Equal(1, _vault.Core.Audit.Counts.GetValueOrDefault("connect.refused"));
    }

    [Fact]
    public async Task The_audit_log_records_uses_and_refusals_without_values()
    {
        using (await GetWithSecretAsync(_vault.Client()))
        {
        }

        _vault.Policy.Secrets[TestVault.SecretName].Hosts = ["api.github.com"];
        _vault.WritePolicy();
        await Assert.ThrowsAsync<VaultException>(() => GetWithSecretAsync(_vault.Client()));

        string log = await File.ReadAllTextAsync(_vault.Options.AuditPath, Ct);
        Assert.Equal(1, _vault.Core.Audit.Counts["forward.allowed"]);
        Assert.Equal(1, _vault.Core.Audit.Counts["forward.refused"]);
        Assert.DoesNotContain(_vault.SecretValue, log, StringComparison.Ordinal);
        Assert.DoesNotContain(_vault.SecretValue[..12], log, StringComparison.Ordinal);
    }

    /// <summary>
    /// S6 (vault.md §13): a separate process uses the secret through Vault. Its memory, read through /proc, holds the
    /// value in no encoding, while a canary it was given is found (so the scan demonstrably works).
    /// </summary>
    [Fact]
    public async Task S6_the_value_never_enters_the_client_process()
    {
        Assert.SkipUnless(ProcessMemory.CanRun, "Needs Linux /proc.");
        string ptraceScope = File.Exists("/proc/sys/kernel/yama/ptrace_scope") ? File.ReadAllText("/proc/sys/kernel/yama/ptrace_scope").Trim() : "0";
        Assert.SkipWhen(ptraceScope is not ("0" or "1"), $"Yama ptrace_scope is {ptraceScope}; reading a child's memory needs 0 or 1.");

        string clientDll = Path.Combine(
            AppContext.BaseDirectory.Replace(
                $"{Path.DirectorySeparatorChar}Corvid.Vault.Tests{Path.DirectorySeparatorChar}",
                $"{Path.DirectorySeparatorChar}Corvid.Vault.TestClient{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal),
            "Corvid.Vault.TestClient.dll");
        Assert.True(File.Exists(clientDll), $"Test client not built at {clientDll}.");
        string canary = "CANARY-" + Guid.NewGuid().ToString("N");

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in (string[])[clientDll, _vault.Options.SocketPath, "s6-session", TestVault.SecretName, _echo.BaseUrl + "/echo", canary])
        {
            start.ArgumentList.Add(a);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        ITestOutputHelper? log = TestContext.Current.TestOutputHelper;
        using Process child = Process.Start(start)!;
        log?.WriteLine($"started client pid {child.Id}");
        var lines = new List<string>();
        string? line;
        while ((line = await child.StandardOutput.ReadLineAsync(timeout.Token)) is not null and not "READY")
        {
            log?.WriteLine("client: " + line);
            lines.Add(line);
        }

        if (line != "READY")
        {
            // Only read stderr once the client has exited; reading it while the client waits for us would deadlock.
            Assert.Fail("Client exited early: " + await child.StandardError.ReadToEndAsync(timeout.Token) + string.Join("\n", lines));
        }

        Dictionary<string, byte[]> patterns = ProcessMemory.Encodings(_vault.SecretValue);
        foreach (var (label, bytes) in ProcessMemory.Encodings(canary))
        {
            patterns["canary-" + label] = bytes;
        }

        log?.WriteLine("client ready; scanning");
        HashSet<string> found = ProcessMemory.Find(child.Id, patterns, out long scanned);
        log?.WriteLine($"scanned {scanned} bytes; releasing client");
        child.StandardInput.Close();
        await child.WaitForExitAsync(timeout.Token);
        log?.WriteLine("client exited");

        // The upstream got the real value; the client saw only scrubbed text.
        Assert.Equal("Bearer " + _vault.SecretValue, Assert.Single(_echo.ReceivedAuthorization));
        Assert.Contains("BODY:you sent: Bearer [secret:test.token]", lines);
        Assert.Contains("ECHO:Bearer [secret:test.token]", lines);

        // Positive control: the scan finds what the child does hold.
        Assert.Contains("canary-utf16", found);
        Assert.True(scanned > 10_000_000, $"Scanned only {scanned} bytes.");

        // The property: no encoding of the value anywhere in the client's memory.
        Assert.DoesNotContain(found, f => !f.StartsWith("canary-", StringComparison.Ordinal));
    }
}
