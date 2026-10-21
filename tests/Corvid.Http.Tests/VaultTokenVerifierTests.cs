using System.Text;

namespace Corvid.Http.Tests;

/// <summary>The bearer token checked through Vault's scrub service against a stand-in Vault holding one secret.</summary>
public sealed class VaultTokenVerifierTests
{
    private const string Name = "serve.token";
    private const string Value = "k7Qp2vX9mR4tW8yB3nL6cF1hJ5sD0gZa";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheSecretItselfIsAccepted()
    {
        await using var vault = new ScrubbingVault(Name, Value);
        var verifier = new VaultTokenVerifier(Name, vault.SocketPath);

        Assert.True(await verifier.SecretExistsAsync(Token));
        Assert.True(await verifier.VerifyAsync(Value, Token));
    }

    [Theory]
    [InlineData("k7Qp2vX9mR4tW8yB3nL6cF1hJ5sD0gZb")]
    // The secret with more around it, or twice: the whole token must be the one marker.
    [InlineData("k7Qp2vX9mR4tW8yB3nL6cF1hJ5sD0gZa-and-more")]
    [InlineData("k7Qp2vX9mR4tW8yB3nL6cF1hJ5sD0gZak7Qp2vX9mR4tW8yB3nL6cF1hJ5sD0gZa")]
    // The marker text itself would come back unchanged; it is refused before Vault is asked.
    [InlineData("[secret:serve.token]")]
    [InlineData("[secret:serve.token]xxxxxxxxxxxxxxxx")]
    // Shorter than the minimum: the scrubber also matches a secret's first 12 bytes.
    [InlineData("k7Qp2vX9mR4t")]
    public async Task AnythingElseIsRefused(string token)
    {
        await using var vault = new ScrubbingVault(Name, Value);
        var verifier = new VaultTokenVerifier(Name, vault.SocketPath);

        Assert.False(await verifier.VerifyAsync(token, Token));
    }

    [Fact]
    public async Task AnotherSecretsMarkerIsRefused()
    {
        // Vault holds the presented value, but under another name than the one configured.
        await using var vault = new ScrubbingVault("other.secret", Value);
        var verifier = new VaultTokenVerifier(Name, vault.SocketPath);

        Assert.False(await verifier.SecretExistsAsync(Token));
        Assert.False(await verifier.VerifyAsync(Value, Token));
    }

    [Fact]
    public async Task AnEncodingOfTheSecretIsAcceptedAsTheSecret()
    {
        // Vault's scrubber matches encodings too; knowing the hex form is knowing the secret.
        await using var vault = new ScrubbingVault(Name, Value);
        var verifier = new VaultTokenVerifier(Name, vault.SocketPath);

        Assert.True(await verifier.VerifyAsync(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(Value)), Token));
    }

    [Fact]
    public async Task WithoutVaultTheTokenCannotBeTold()
    {
        var root = Path.Combine(Path.GetTempPath(), "cvn-" + Guid.NewGuid().ToString("N")[..10]);
        var verifier = new VaultTokenVerifier(Name, Path.Combine(root, "v.sock"));

        Assert.Null(await verifier.VerifyAsync(Value, Token));
    }

    [Fact]
    public async Task AnAcceptedTokenIsRememberedForAFewMinutes()
    {
        await using var vault = new ScrubbingVault(Name, Value);
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-20T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var verifier = new VaultTokenVerifier(Name, vault.SocketPath, time);

        Assert.True(await verifier.VerifyAsync(Value, Token));
        Assert.True(await verifier.VerifyAsync(Value, Token));
        Assert.Equal(1, vault.Scrubs);

        time.Advance(TimeSpan.FromMinutes(6));
        Assert.True(await verifier.VerifyAsync(Value, Token));
        Assert.Equal(2, vault.Scrubs);
    }

    [Fact]
    public async Task AFloodOfWrongTokensStopsReachingVault()
    {
        await using var vault = new ScrubbingVault(Name, Value);
        var time = new ManualTime(DateTimeOffset.Parse("2026-10-20T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var verifier = new VaultTokenVerifier(Name, vault.SocketPath, time);

        for (var i = 0; i < 40; i++)
            Assert.False(await verifier.VerifyAsync($"wrong-token-{i:D4}-xxxxxxxxxxxxxxxx", Token));
        Assert.Equal(20, vault.Scrubs);
        // Over the limit even the right token is refused until the minute has passed, so Vault's scrub budget is never spent.
        Assert.False(await verifier.VerifyAsync(Value, Token));

        time.Advance(TimeSpan.FromSeconds(61));
        Assert.True(await verifier.VerifyAsync(Value, Token));
    }

    private sealed class ManualTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
