using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Corvid.Secrets;
using Corvid.Vault.Contracts;

namespace Corvid.Secrets.Tests;

public sealed class CredentialDetectorTests
{
    // Public example values (AWS's documented key id, jwt.io's sample token) or fictitious ones in the provider's shape.
    private const string AwsKeyId = "AKIAIOSFODNN7EXAMPLE";
    private const string JwtSample =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIiwibmFtZSI6IkpvaG4gRG9lIiwiaWF0IjoxNTE2MjM5MDIyfQ.SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c";

    private static readonly string PemBlock =
        "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW\nQyNTUxOQAAACDfakefakefakefakefakefakefakefakefakefakefakeAAAA\n-----END OPENSSH PRIVATE KEY-----";

    private static IEnumerable<string> Values(string text, CredentialRules rules = CredentialDetector.Egress) =>
        CredentialDetector.Find(text, rules).Select(match => match.ValueIn(text));

    [Theory]
    [InlineData(CredentialKind.AwsAccessKey, "key AKIAIOSFODNN7EXAMPLE here", AwsKeyId, 4)]
    [InlineData(CredentialKind.AwsAccessKey, "ASIAY34FZKBOKMUTVV7A is temporary", "ASIAY34FZKBOKMUTVV7A", 4)]
    [InlineData(CredentialKind.GitHubToken, "Use ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8 to clone", "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8", 4)]
    [InlineData(CredentialKind.GitHubToken, "ghs_abcdefghijklmnopqrstuvwxyz0123456789", "ghs_abcdefghijklmnopqrstuvwxyz0123456789", 4)]
    [InlineData(CredentialKind.GitHubToken, "github_pat_11ABCDEFG0123456789_abcdefghij", "github_pat_11ABCDEFG0123456789_abcdefghij", 11)]
    [InlineData(CredentialKind.SlackToken, "xoxb-123456789012-1234567890123-AbCdEfGhIjKlMnOpQrStUvWx.", "xoxb-123456789012-1234567890123-AbCdEfGhIjKlMnOpQrStUvWx", 5)]
    [InlineData(CredentialKind.SlackToken, "xoxp-1234567890-abcdef", "xoxp-1234567890-abcdef", 5)]
    [InlineData(CredentialKind.SkKey, "sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "sk-ant-api03-abcdefghijklmnopqrstuvwxyz", 3)]
    [InlineData(CredentialKind.SkKey, "key sk-proj-Ab3dEf6hIj9kLm2nOp5qRs8t, then", "sk-proj-Ab3dEf6hIj9kLm2nOp5qRs8t", 3)]
    [InlineData(CredentialKind.SkKey, "Use the API key sk-live-4f9a2b7c8d1e3f5a6b7c for it.", "sk-live-4f9a2b7c8d1e3f5a6b7c", 3)]
    [InlineData(CredentialKind.StripeKey, "stripe.api_key = 'sk_live_4eC39HqLyjWDarjtT1zdp7dc'", "sk_live_4eC39HqLyjWDarjtT1zdp7dc", 8)]
    [InlineData(CredentialKind.StripeKey, "rk_live_4eC39HqLyjWDarjtT1zdp7dc only reads", "rk_live_4eC39HqLyjWDarjtT1zdp7dc", 8)]
    [InlineData(CredentialKind.GitLabToken, "push with glpat-xYz12AbC34dEf56GhI78j", "glpat-xYz12AbC34dEf56GhI78j", 6)]
    [InlineData(CredentialKind.GoogleApiKey, "&key=AIzaSyA1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q", "AIzaSyA1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q", 4)]
    [InlineData(CredentialKind.HuggingFaceToken, "--token hf_AbCdEfGhIjKlMnOpQrStUvWxYzAbCdEfGh", "hf_AbCdEfGhIjKlMnOpQrStUvWxYzAbCdEfGh", 3)]
    [InlineData(CredentialKind.Jwt, "cookie " + JwtSample, JwtSample, 3)]
    public void Provider_shapes_are_found_with_their_public_prefix(CredentialKind kind, string text, string value, int prefix)
    {
        var match = Assert.Single(CredentialDetector.Find(text, CredentialDetector.Egress));
        Assert.Equal(kind, match.Kind);
        Assert.Equal(value, match.ValueIn(text));
        Assert.Equal(prefix, match.PrefixLength);
        Assert.Equal(CredentialStrength.Shape, match.Strength);
    }

    [Theory]
    [InlineData("curl -H 'Authorization: Bearer abc.def.ghi123' https://x", "abc.def.ghi123")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZDEyMw==", "dXNlcjpwYXNzd29yZDEyMw==")]
    [InlineData("""{"Authorization": "token 0123456789abcdef"}""", "0123456789abcdef")]
    [InlineData("bearer eyJhbGciOiJI.payload1.sig", "eyJhbGciOiJI.payload1.sig")]
    public void Authorization_values_are_found_without_their_scheme(string text, string value) =>
        Assert.Equal([value], Values(text));

    [Theory]
    [InlineData("export GITHUB_TOKEN=abc123def", "abc123def")]
    [InlineData("""{"api_key": "abc123", "n": 1}""", "abc123")]
    [InlineData("password: 'two words'", "two words")]
    [InlineData("export SMTP_PASSWORD=\"mail relay 2024\"", "mail relay 2024")]
    [InlineData("aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY")]
    [InlineData("SECRET_KEY=django-insecure-0abc", "django-insecure-0abc")]
    [InlineData("--set auth.apiKey=Qm9vVGhpc0lzRmFrZQ", "Qm9vVGhpc0lzRmFrZQ")]
    [InlineData("passwd = tiger", "tiger")]
    public void Assignments_to_a_secret_label_are_found_as_the_value_alone(string text, string value) =>
        Assert.Equal([value], Values(text));

    [Theory]
    [InlineData("postgres://app:S3cretPass@db.internal:5432/prod.", "S3cretPass")]
    [InlineData("redis://default:hunter2@cache:6379", "hunter2")]
    public void A_url_userinfo_password_is_found(string text, string value) =>
        Assert.Equal([value], Values(text));

    [Theory]
    [InlineData("The staging database password is hunter2-Staging!", "hunter2-Staging!")]
    [InlineData("Log in as admin with password Tr0ub4dor&3 and change it afterwards.", "Tr0ub4dor&3")]
    [InlineData("The wifi password at the cabin is pinecone-lake-2024.", "pinecone-lake-2024")]
    [InlineData("The SSH private key passphrase is correct-horse-battery-staple.", "correct-horse-battery-staple")]
    [InlineData("Our AWS secret access key is wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY.", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY")]
    [InlineData("the root password was changed to Opal#Canyon19 this morning", "Opal#Canyon19")]
    [InlineData("Use the password 'brisk Falcon 88' for the drive.", "brisk Falcon 88")]
    [InlineData("The client secret is QmFzZTY0SXNOb3RFbmNyeXB0aW9u", "QmFzZTY0SXNOb3RFbmNyeXB0aW9u")]
    public void Prose_values_after_a_secret_label_are_found_when_they_look_like_secrets(string text, string value) =>
        Assert.Equal([value], Values(text));

    // Spike S3's nine credential texts (evals/spikes/S3-gliner2-targets/fixtures/pii.jsonl), where GLiNER2 alone covered five.
    [Theory]
    [InlineData("The staging database password is hunter2-Staging!", "hunter2-Staging!")]
    [InlineData("Use the API key sk-live-4f9a2b7c8d1e3f5a6b7c for the payment service.", "sk-live-4f9a2b7c8d1e3f5a6b7c")]
    [InlineData("Log in as admin with password Tr0ub4dor&3 and change it afterwards.", "Tr0ub4dor&3")]
    [InlineData("Set GITHUB_TOKEN=ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8 in the CI settings.", "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8")]
    [InlineData("Our AWS secret access key is wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY.", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY")]
    [InlineData("The wifi password at the cabin is pinecone-lake-2024.", "pinecone-lake-2024")]
    [InlineData("The SSH private key passphrase is correct-horse-battery-staple.", "correct-horse-battery-staple")]
    [InlineData("The Slack bot token is xoxb-123456789012-1234567890123-AbCdEfGhIjKlMnOpQrStUvWx.", "xoxb-123456789012-1234567890123-AbCdEfGhIjKlMnOpQrStUvWx")]
    [InlineData("The postgres connection string is postgres://app:S3cretPass@db.internal:5432/prod.", "S3cretPass")]
    public void Spike_S3_credentials_are_all_found(string text, string value) =>
        Assert.Equal([value], Values(text));

    [Theory]
    // Digests, ids and versions: the encoded rule is opt-in, so egress and traces leave them.
    [InlineData("Reset to 9fceb02d0ae598e95dc970b74767f19372d61af8 and rebuild.")]
    [InlineData("Session 5f0c6a8e-1b2c-4d3e-8f9a-0b1c2d3e4f5a replayed.")]
    [InlineData("docker pull nginx@sha256:0d17b565c37bcbd895e9d92315a05c1c3c9a29f762b011a10c54a66cd53c9b31")]
    // Variable references, templates, placeholders and masks.
    [InlineData("Pass the token=$GITHUB_TOKEN env to the script.")]
    [InlineData("Pass token=${GITHUB_TOKEN} or password=%DB_PASSWORD% from the environment.")]
    [InlineData("password: ${{ secrets.DB_PASSWORD }}")]
    [InlineData("curl -H \"Authorization: Bearer $API_TOKEN\" https://api.example.com")]
    [InlineData("api_key: <YOUR_API_KEY>")]
    [InlineData("The model replied with <CREDENTIAL_1>, password=<CREDENTIAL_2>.")]
    [InlineData("password: ******** and secret: xxxxxxxx")]
    [InlineData("password=[REDACTED] token: null")]
    [InlineData("""{"api_key": "", "enabled": false, "token": true}""")]
    // Token counts and prose about credentials.
    [InlineData("input_tokens: 512, max_tokens=4096")]
    [InlineData("token: 4096")]
    [InlineData("The password is stored in Vault, not in the repo.")]
    [InlineData("the password is stored in Vault")]
    [InlineData("The password manager is 1Password on every laptop.")]
    [InlineData("Your password must be at least 12 characters long.")]
    [InlineData("The password reset link is https://example.com/reset?id=42 today.")]
    [InlineData("The secret is to cache the embeddings between runs.")]
    [InlineData("GitHub personal access tokens start with ghp_ and fine-grained ones with github_pat_.")]
    [InlineData("Bearer authentication is required for every endpoint.")]
    [InlineData("Authorization: Bearer")]
    [InlineData("Install scikit-learn and run the sk-learn-pipeline-examples notebook.")]
    [InlineData("The BEGIN PRIVATE KEY header marks a PKCS#8 file: -----BEGIN PRIVATE KEY-----")]
    [InlineData("Connect to postgres://localhost:5432/app as the app user.")]
    public void Hard_negatives_are_not_credentials_under_the_egress_rules(string text) =>
        Assert.Empty(Values(text));

    [Fact]
    public void Vault_placeholders_are_never_credentials()
    {
        var placeholder = Placeholders.Format("github-token", "Ab3dEf6hIj9kLm2nOp5qRs8t");
        foreach (var text in new[] { $"GITHUB_TOKEN={placeholder}", $"the password is {placeholder}", $"\"api_key\": \"{placeholder}\"" })
            Assert.Empty(Values(text, CredentialDetector.Lessons));
    }

    [Fact]
    public void A_pem_block_is_one_match_from_header_to_end_line_and_a_cut_block_runs_to_the_end()
    {
        var text = "key:\n" + PemBlock + "\nafter";
        var match = Assert.Single(CredentialDetector.Find(text, CredentialDetector.Egress));
        Assert.Equal(CredentialKind.PrivateKey, match.Kind);
        Assert.Equal(PemBlock, match.ValueIn(text));

        var cut = "out: " + PemBlock[..120];
        Assert.Equal(PemBlock[..120], Assert.Single(Values(cut)));

        // Two blocks are two matches: the lazy body stops at the first END line.
        var two = PemBlock + "\n\n" + PemBlock;
        Assert.Equal([PemBlock, PemBlock], Values(two));

        // JSON-escaped line breaks, as a tool result serialized to a string holds them.
        var escaped = "{\"private_key\": \"" + PemBlock.Replace("\n", "\\n", StringComparison.Ordinal) + "\\n\"}";
        Assert.Contains(PemBlock.Replace("\n", "\\n", StringComparison.Ordinal), Assert.Single(Values(escaped)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_quoted_value_is_the_text_between_its_quotes_whichever_alternative_matched()
    {
        Assert.Equal(["a b c"], Values("password = \"a b c\""));
        Assert.Equal(["a b c"], Values("password = 'a b c'"));
        Assert.Equal(["abc123"], Values("password=abc123, user=me"));
        var text = """{"password": "p1", "token": "t2t2t2t2", "secret": 's3s3'}""";
        Assert.Equal(["p1", "t2t2t2t2", "s3s3"], Values(text));
    }

    [Fact]
    public void Overlapping_rules_merge_into_one_span_and_the_shape_keeps_its_prefix()
    {
        var text = "GITHUB_TOKEN=ghp_abcdefghijklmnopqrstuvwxyz0123456789";
        var match = Assert.Single(CredentialDetector.Find(text, CredentialDetector.Egress));
        Assert.Equal(CredentialKind.GitHubToken, match.Kind);
        Assert.Equal("GITHUB_TOKEN=ghp_[REDACTED]", CredentialDetector.Redact(text, CredentialDetector.Traces));

        // A labelled value that starts earlier and a token inside it: one span over both.
        var quoted = "token: \"Bearer ghp_abcdefghijklmnopqrstuvwxyz0123456789 extra\"";
        Assert.Equal(["Bearer ghp_abcdefghijklmnopqrstuvwxyz0123456789 extra"], Values(quoted));
    }

    [Theory]
    [InlineData("export GITHUB_TOKEN=abc123def", "export GITHUB_TOKEN=[REDACTED]")]
    [InlineData("key AKIAABCDEFGHIJKLMNOP here", "key AKIA[REDACTED] here")]
    [InlineData("sk-ant-api03-abcdefghijklmnopqrstuvwxyz", "sk-[REDACTED]")]
    [InlineData("postgres://app:S3cretPass@db/prod", "postgres://app:[REDACTED]@db/prod")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEAabcdefgh\n-----END RSA PRIVATE KEY-----\nafter", "[REDACTED]\nafter")]
    [InlineData("nothing here", "nothing here")]
    public void Redact_keeps_prefixes_labels_and_quotes(string text, string expected) =>
        Assert.Equal(expected, CredentialDetector.Redact(text, CredentialDetector.Traces));

    [Fact]
    public void Encoded_blobs_and_a_bare_key_header_are_found_only_under_the_lesson_rules()
    {
        const string hex = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
        const string base64 = "QWxhZGRpbjpPcGVuU2VzYW1lMTIzNDU2Nzg5MEFCQ0RFRkdI";
        const string header = "-----BEGIN RSA PRIVATE KEY-----";
        foreach (var value in new[] { hex, base64, header })
        {
            var text = $"Use {value} for signing.";
            Assert.Empty(Values(text, CredentialDetector.Egress));
            var match = Assert.Single(CredentialDetector.Find(text, CredentialDetector.Lessons));
            Assert.Equal(value, match.ValueIn(text));
            Assert.Equal(value == header ? CredentialStrength.Shape : CredentialStrength.Encoded, match.Strength);
        }

        // A long identifier with a digit switches class like words, not like random text.
        Assert.Empty(Values("Read DependencyInjectionAbstractions1VersionPropertyName from props.", CredentialDetector.Lessons));
        // Paths are not blobs.
        Assert.Empty(Values("Edit src/Corvid.Kernel/Turns/TurnRunnerAndFriendsAndMoreFiles/X.cs", CredentialDetector.Lessons));
    }

    [Fact]
    public void A_timeout_fails_closed_in_redact_and_contains()
    {
        static ImmutableArray<CredentialMatch> Slow(string text, CredentialRules rules) =>
            throw new RegexMatchTimeoutException(text, "pattern", TimeSpan.FromSeconds(1));

        Assert.Equal(CredentialDetector.Redacted, CredentialDetector.Redact("harmless text", CredentialDetector.Traces, Slow));
        Assert.True(CredentialDetector.Contains("harmless text", CredentialDetector.Egress, Slow));
        Assert.False(CredentialDetector.Contains("harmless text", CredentialDetector.Egress));
    }

    [Fact]
    public void Matches_are_ordered_and_never_overlap_in_a_long_mixed_text()
    {
        var text = string.Join("\n", Enumerable.Range(0, 50).Select(i =>
            $"line {i}: GITHUB_TOKEN=ghp_{i:D4}abcdefghijklmnopqrstuvwxyz012345 password is Opal#Canyon{i:D2} {AwsKeyId} " + PemBlock));
        var matches = CredentialDetector.Find(text, CredentialDetector.Lessons);
        Assert.Equal(200, matches.Length);
        for (var i = 1; i < matches.Length; i++)
            Assert.True(matches[i].Start >= matches[i - 1].End);
    }

    [Theory]
    [InlineData("hunter2-Staging!", true)]
    [InlineData("correct-horse-battery-staple", true)]
    [InlineData("aBcDeFgHiJkLmNoPqR", true)]
    [InlineData("stored", false)]
    [InlineData("well-known", false)]
    [InlineData("PasswordManager", false)]
    public void The_secret_like_test(string value, bool expected) =>
        Assert.Equal(expected, CredentialDetector.IsSecretLike(value));

    // Review of C-032, finding 1: a quote inside a value, escaped in JSON or bare in plain text, does not end it.
    [Theory]
    [InlineData("""{"cmd": "password = Xk9\"vQ2!mL7pZ"}""", "Xk9\\\"vQ2!mL7pZ")]
    [InlineData("""{"password": "Xk9\"vQ2!mL7pZ"}""", "Xk9\\\"vQ2!mL7pZ")]
    [InlineData("password = Xk9\"vQ2!mL7pZ", "Xk9\"vQ2!mL7pZ")]
    [InlineData("password = \"Xk9\\\"vQ2!mL7pZ\"", "Xk9\\\"vQ2!mL7pZ")]
    [InlineData("The password is \"Xk9\\\"vQ2!mL7pZ\" now.", "Xk9\\\"vQ2!mL7pZ")]
    [InlineData("The password is Xk9\"vQ2!mL7pZ.", "Xk9\"vQ2!mL7pZ")]
    // A quote that closes the text around a bare assignment is not part of its value, nor the backslash escaping it in JSON.
    [InlineData("say \"TOKEN=abc123def\" to it", "abc123def")]
    [InlineData("""{"cmd": "export TOKEN=abc123def"}""", "abc123def")]
    [InlineData("""{"msg": "set \"TOKEN=abc123def\" now"}""", "abc123def")]
    public void A_quote_inside_a_value_does_not_end_it(string text, string value) =>
        Assert.Equal([value], Values(text));

    // Finding 2: only a whole value is exempt.
    [Fact]
    public void A_secret_that_only_contains_a_placeholder_or_redacted_marker_is_still_found()
    {
        var placeholder = Placeholders.Format("db", "Ab3dEf6hIj9kLm2nOp5qRs8t");
        Assert.Equal(["hunter2" + placeholder], Values($"password=hunter2{placeholder}"));
        Assert.Equal(["Xk9[REDACTED]vQ2"], Values("password: 'Xk9[REDACTED]vQ2'"));
        Assert.Equal(["Xk9[REDACTED]"], Values("password: 'Xk9[REDACTED]'"));
        Assert.Empty(Values($"password={placeholder}"));
        Assert.Empty(Values("password: '[REDACTED]'"));
    }

    // Finding 5: a $, % or angle bracket alone does not make a value a reference.
    [Theory]
    [InlineData("password=$ecretP4ss!word", "$ecretP4ss!word")]
    [InlineData("password: '%Xk9vQ2%mL7%'", "%Xk9vQ2%mL7%")]
    [InlineData("password: '<Xk9\"vQ2!>'", "<Xk9\"vQ2!>")]
    [InlineData("password: '${DB_PASS:-hunter2}'", "${DB_PASS:-hunter2}")]
    [InlineData("password: '{{ x }}hunter2'", "{{ x }}hunter2")]
    public void A_value_only_starting_like_a_reference_is_found(string text, string value) =>
        Assert.Equal([value], Values(text));

    [Theory]
    [InlineData("password: '$(cat /run/secrets/db)'")]
    [InlineData("password: '<your password here>'")]
    [InlineData("password: ‹EMAIL_1›")]
    [InlineData("token: ${{ secrets.GITHUB_TOKEN }}.")]
    public void A_whole_reference_or_placeholder_is_not_a_credential(string text) =>
        Assert.Empty(Values(text));

    // Finding 7: the shapes and labels the spec names.
    [Theory]
    [InlineData("xoxe-1-My0xLTEyMzQ1Njc4OTAtYWJjZGVm", "xoxe-1-My0xLTEyMzQ1Njc4OTAtYWJjZGVm")]
    [InlineData("SLACK_APP_TOKEN=xapp-1-A0123456789-0123456789012-abcdef0123", "xapp-1-A0123456789-0123456789012-abcdef0123")]
    [InlineData("pass=Xk9vQ2mL7pZ", "Xk9vQ2mL7pZ")]
    [InlineData("export DB_PASS=Xk9vQ2mL7pZ", "Xk9vQ2mL7pZ")]
    [InlineData("Server=db;Uid=sa;Pwd=Xk9vQ2mL7pZ;", "Xk9vQ2mL7pZ")]
    [InlineData("curl -u admin:Xk9vQ2mL7pZ https://api.example.com", "Xk9vQ2mL7pZ")]
    [InlineData("curl --user 'admin:Xk9 vQ2' https://api.example.com", "Xk9 vQ2")]
    public void Slack_app_and_refresh_tokens_short_password_labels_and_curl_users_are_found(string text, string value) =>
        Assert.Equal([value], Values(text));

    [Theory]
    [InlineData("The compass: north by northwest.")]
    [InlineData("PWD=/home/mark/projects")]
    [InlineData("git push -u origin main")]
    [InlineData("docker run -u 1000:1000 image")]
    [InlineData("xoxo-hugs-and-kisses")]
    [InlineData("password: |")]
    public void Lookalikes_of_the_new_labels_are_not_credentials(string text) =>
        Assert.Empty(Values(text));

    // Finding 8: redacting a redacted text changes nothing.
    [Theory]
    [InlineData("token: ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8.")]
    [InlineData("GITHUB_TOKEN=ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("export GITHUB_TOKEN=abc123def")]
    [InlineData("The staging database password is hunter2-Staging!")]
    [InlineData("postgres://app:S3cretPass@db/prod")]
    [InlineData("Authorization: Bearer abc.def.ghi123")]
    [InlineData("""{"api_key": "abc123", "password": "Xk9\"vQ2!mL7pZ"}""")]
    [InlineData("key AKIAIOSFODNN7EXAMPLE and sk-ant-api03-abcdefghijklmnopqrstuvwxyz, xoxb-1234567890-abcdef")]
    [InlineData("curl -u admin:Xk9vQ2mL7pZ https://x")]
    public void Redact_is_idempotent(string text)
    {
        var once = CredentialDetector.Redact(text, CredentialDetector.Traces);
        Assert.NotEqual(text, once);
        Assert.Equal(once, CredentialDetector.Redact(once, CredentialDetector.Traces));
        Assert.Empty(Values(once, CredentialDetector.Lessons));
    }
}
