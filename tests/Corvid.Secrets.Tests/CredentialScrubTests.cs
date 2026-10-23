using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Corvid.Vault.Contracts;

namespace Corvid.Secrets.Tests;

public sealed class CredentialScrubTests
{
    // Fictitious, in GitHub's classic shape.
    private const string Token = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8";

    private static string Hash8(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8];

    private static string Scrub(string text) => CredentialScrub.Scrub(text, CredentialDetector.Egress).Text;

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string ScrubJson(string json) => CredentialScrub.ScrubJson(Json(json), CredentialDetector.Egress).Element.GetRawText();

    [Fact]
    public void A_shape_keeps_its_public_prefix_and_gets_its_kind_and_the_hash_prefix_of_the_whole_value()
    {
        var scrubbed = CredentialScrub.Scrub($"export GH_TOKEN={Token}", CredentialDetector.Egress);

        Assert.Equal($"export GH_TOKEN=ghp_[REDACTED:github-token:{Hash8(Token)}]", scrubbed.Text);
        Assert.Equal(1, scrubbed.Count);
    }

    [Theory]
    [InlineData("Run it with GH_TOKEN={0}?")]
    [InlineData("{{\"content\":\"GH_TOKEN={0}\\n\"}}")]
    [InlineData("Authorization: Bearer {0}")]
    public void A_shape_the_labelled_rule_also_covers_is_named_by_its_shape(string format)
    {
        var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, format, Token);

        var scrubbed = text.StartsWith('{') ? ScrubJson(text) : Scrub(text);

        Assert.Contains($"ghp_[REDACTED:github-token:{Hash8(Token)}]", scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("password=hunter2-Staging", "password=[REDACTED:labelled-value]")]
    [InlineData("redis://default:hunter2@cache:6379", "redis://default:[REDACTED:url-password]@cache:6379")]
    [InlineData("The staging database password is hunter2-Staging!", "The staging database password is [REDACTED:prose-value]")]
    [InlineData("curl -H 'Authorization: Bearer abc.def.ghi123' https://x", "curl -H 'Authorization: Bearer [REDACTED:authorization]' https://x")]
    public void A_value_a_person_chose_gets_its_kind_and_no_hash(string text, string expected) =>
        Assert.Equal(expected, Scrub(text));

    [Fact]
    public void A_pem_block_is_one_marker()
    {
        const string pem = "-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAABG5vbmUAAAAEbm9uZQAAAAAAAAABAAAAMwAAAAtzc2gtZW\n-----END OPENSSH PRIVATE KEY-----";

        Assert.Equal($"key:\n[REDACTED:private-key:{Hash8(pem)}]\n", Scrub($"key:\n{pem}\n"));
    }

    [Theory]
    [InlineData("export GH_TOKEN=" + Token)]
    [InlineData("{\"token\": \"" + Token + "\", \"password\": \"two words\"}")]
    [InlineData("DB_PASSWORD=Ab1x,Z9q-pw77 and the root password is Opal#Canyon19.")]
    [InlineData("postgres://app:S3cretPass@db.internal:5432/prod")]
    [InlineData("curl -u admin:Tr0ub4dor&3 https://x")]
    [InlineData("Authorization: Bearer " + Token)]
    public void The_scrub_is_idempotent_and_its_output_holds_no_credential(string text)
    {
        var once = Scrub(text);

        Assert.NotEqual(text, once);
        Assert.Equal(once, Scrub(once));
        Assert.Empty(CredentialDetector.Find(once, CredentialDetector.Egress));
        Assert.Equal(once, CredentialDetector.Redact(once, CredentialDetector.Lessons));
    }

    [Theory]
    [InlineData("[REDACTED:labelled-value]")]
    [InlineData("[REDACTED:unscannable]")]
    [InlineData("ghp_[REDACTED:github-token:3fa91c0e]")]
    [InlineData("AKIA[REDACTED:aws-access-key:0123abcd]")]
    [InlineData("[REDACTED]")]
    public void A_marker_is_exempt(string marker) => Assert.True(CredentialDetector.IsExempt(marker));

    [Fact]
    public void Every_kind_name_makes_an_exempt_marker()
    {
        foreach (var kind in Enum.GetValues<CredentialKind>())
        {
            Assert.True(CredentialDetector.IsExempt($"[REDACTED:{CredentialScrub.KindName(kind)}]"), kind.ToString());
            Assert.True(CredentialDetector.IsExempt($"[REDACTED:{CredentialScrub.KindName(kind)}:0123abcd]"), kind.ToString());
        }
    }

    [Theory]
    [InlineData("[REDACTED:sk-abcdefghijklmnopqrst]", "sk-abcdefghijklmnopqrst")]
    [InlineData("[REDACTED:glpat-abcdefghijklmnopqrstuv]", "glpat-abcdefghijklmnopqrstuv")]
    public void A_key_dressed_as_a_marker_kind_is_still_found(string text, string key)
    {
        Assert.False(CredentialDetector.IsExempt(text));
        Assert.Contains(key, CredentialDetector.Find(text, CredentialDetector.Egress).Select(match => match.ValueIn(text)));
    }

    [Theory]
    [InlineData("[REDACTED:Labelled]")]
    [InlineData("ghp_[REDACTED:github-token:3FA91C0E]")]
    [InlineData("hunter2[REDACTED:labelled-value]")]
    public void Text_that_only_resembles_a_marker_is_not_exempt(string value) => Assert.False(CredentialDetector.IsExempt(value));

    [Fact]
    public void A_marker_has_no_angle_brackets_so_a_placeholder_restore_never_matches_it()
    {
        var scrubbed = Scrub($"token={Token} password=hunter2-x");

        Assert.DoesNotContain('<', scrubbed);
        Assert.DoesNotContain('>', scrubbed);
    }

    [Theory]
    [InlineData("Authorization: Bearer {0}")]
    [InlineData("{{\"token\":\"{0}\"}}")]
    [InlineData("export GITHUB_TOKEN={0}")]
    public void A_vault_placeholder_passes_unchanged(string format)
    {
        var text = string.Format(System.Globalization.CultureInfo.InvariantCulture, format,
            Placeholders.Format("github-token", "Ab3dEf6hIj9kLm2nOp5qRs8t"));

        Assert.Equal(text, Scrub(text));
        if (text.StartsWith('{'))
            Assert.Equal(text, ScrubJson(text));
    }

    [Fact]
    public void A_placeholder_inside_a_tool_result_passes_unchanged()
    {
        var placeholder = Placeholders.Format("db", "Ab3dEf6hIj9kLm2nOp5qRs8t");
        var json = $$"""{"stdout":"wrote DB_PASSWORD={{placeholder}} to .env","exit":0}""";

        var scrubbed = CredentialScrub.ScrubJson(Json(json), CredentialDetector.Egress);

        Assert.Equal(0, scrubbed.Count);
        Assert.Equal(json, scrubbed.Element.GetRawText());
    }

    [Fact]
    public void Commit_shas_and_digests_survive_since_only_the_egress_rules_run()
    {
        const string text = "HEAD is now at 3f786850e387550fdab836ed7e6dc881de23001b; sha256:e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

        Assert.Equal(text, Scrub(text));
    }

    [Fact]
    public void A_json_key_labels_its_value()
    {
        var scrubbed = ScrubJson("""{"user":"ana","password":"two words","n":1}""");

        Assert.Equal("""{"user":"ana","password":"[REDACTED:labelled-value]","n":1}""", scrubbed);
    }

    [Fact]
    public void A_json_escaped_value_is_found_decoded_and_replaced_in_its_leaf()
    {
        var scrubbed = ScrubJson("""{"api_key":"ab\"c\\d123"}""");

        Assert.Equal("""{"api_key":"[REDACTED:labelled-value]"}""", scrubbed);
    }

    [Fact]
    public void A_shape_in_a_nested_leaf_and_a_property_name_is_scrubbed_and_the_structure_kept()
    {
        var json = $$"""{"items":[{"note":"use {{Token}} here","ok":true}],"{{Token}}":null}""";

        var scrubbed = CredentialScrub.ScrubJson(Json(json), CredentialDetector.Egress);

        var marker = $"ghp_[REDACTED:github-token:{Hash8(Token)}]";
        Assert.Equal($$"""{"items":[{"note":"use {{marker}} here","ok":true}],"{{marker}}":null}""", scrubbed.Element.GetRawText());
        Assert.Equal(2, scrubbed.Count);
    }

    [Fact]
    public void A_short_labelled_value_only_replaces_a_leaf_that_is_it()
    {
        var scrubbed = ScrubJson("""{"cmd":"export PASSWORD=ab","pass":"ab","note":"about"}""");

        Assert.Equal("""{"cmd":"export PASSWORD=[REDACTED:labelled-value]","pass":"[REDACTED:labelled-value]","note":"about"}""", scrubbed);
    }

    [Fact]
    public void An_authorization_value_after_its_scheme_is_found_through_the_key()
    {
        var scrubbed = ScrubJson("""{"Authorization": "token 0123456789abcdef"}""");

        Assert.Equal("""{"Authorization":"token [REDACTED:authorization]"}""", scrubbed);
    }

    [Fact]
    public void A_labelled_number_becomes_a_marker_string()
    {
        Assert.Equal("""{"pin_password":"[REDACTED:labelled-value]","port":5432}""", ScrubJson("""{"pin_password": 73914482, "port": 5432}"""));
    }

    [Fact]
    public void An_object_under_a_secret_label_is_structure_and_its_own_keys_decide()
    {
        Assert.Equal("""{"token":{"kind":"oauth","expires_in":3600}}""", ScrubJson("""{"token":{"kind":"oauth","expires_in":3600}}"""));
        Assert.Equal("""{"data":{"password":"[REDACTED:labelled-value]"}}""", ScrubJson("""{"data":{"password":"pw"}}"""));
    }

    [Fact]
    public void Json_with_nothing_to_scrub_is_returned_as_given()
    {
        var element = Json("""{ "a" : [1, 2.50, "x"] }""");

        var scrubbed = CredentialScrub.ScrubJson(element, CredentialDetector.Egress);

        Assert.Equal(0, scrubbed.Count);
        Assert.Equal("""{ "a" : [1, 2.50, "x"] }""", scrubbed.Element.GetRawText());
    }

    [Fact]
    public void A_timeout_fails_closed_for_text_and_json()
    {
        static ImmutableArray<CredentialMatch> Slow(string text, CredentialRules rules) =>
            throw new RegexMatchTimeoutException(text, "pattern", TimeSpan.FromSeconds(1));

        Assert.Equal(new ScrubbedText(CredentialScrub.Unscannable, 1), CredentialScrub.Scrub("anything", CredentialDetector.Egress, Slow));
        var json = CredentialScrub.ScrubJson(Json("""{"a":"b"}"""), CredentialDetector.Egress, Slow);
        Assert.Equal("\"[REDACTED:unscannable]\"", json.Element.GetRawText());
    }

    [Fact]
    public void A_value_found_in_json_text_that_does_not_decode_fails_closed()
    {
        // A value cut inside an escape (a\u00) cannot be decoded, so the leaf it came from cannot be matched.
        static ImmutableArray<CredentialMatch> Cut(string text, CredentialRules rules)
        {
            var at = text.IndexOf("a\\u00", StringComparison.Ordinal);
            return at < 0 ? [] : [new CredentialMatch(CredentialKind.LabelledValue, at, 5, 0, CredentialStrength.Labelled)];
        }

        var scrubbed = CredentialScrub.ScrubJson(Json("""{"note":"a\u0041b"}"""), CredentialDetector.Egress, Cut);

        Assert.Equal("\"[REDACTED:unscannable]\"", scrubbed.Element.GetRawText());
    }

    [Theory]
    [InlineData("password", true)]
    [InlineData("GITHUB_TOKEN", true)]
    [InlineData("db_pass", true)]
    [InlineData("api-key", true)]
    [InlineData("compass", false)]
    [InlineData("max_tokens", false)]
    [InlineData("passenger", false)]
    [InlineData("nextPageToken", false)]
    [InlineData("page_token", false)]
    [InlineData("NextContinuationToken", false)]
    [InlineData("csrf_token", true)]
    [InlineData("refresh_token", true)]
    public void Secret_labels_are_recognized_whole(string name, bool expected) =>
        Assert.Equal(expected, CredentialDetector.IsSecretLabel(name));

    // Review of #59, finding 1: detection rules correlate on the command text, so a substitution in a labelled value stays.
    [Theory]
    [InlineData("export API_TOKEN=\"$(cat ~/.aws/credentials)-v2\"", "export API_TOKEN=\"$(cat ~/.aws/credentials)[REDACTED:labelled-value]\"")]
    [InlineData("export API_TOKEN=\"$(cat ~/.aws/credentials)\"", "export API_TOKEN=\"$(cat ~/.aws/credentials)\"")]
    [InlineData("export API_TOKEN=$(cat ~/.netrc)", "export API_TOKEN=$(cat ~/.netrc)")]
    [InlineData("TOKEN=\"pre`cat .env`post\"", "TOKEN=\"[REDACTED:labelled-value]`cat .env`[REDACTED:labelled-value]\"")]
    [InlineData("PASSWORD=\"${DB_PASSWORD}x9Kq\"", "PASSWORD=\"${DB_PASSWORD}[REDACTED:labelled-value]\"")]
    [InlineData("curl -u admin:\"$(cat ~/.netrc)\" https://x.example", "curl -u admin:\"$(cat ~/.netrc)\" https://x.example")]
    [InlineData("password: '${DB_PASS:-hunter2}'", "password: '[REDACTED:labelled-value]'")]
    public void A_substitution_in_a_labelled_value_is_kept_and_only_the_literal_text_replaced(string text, string expected)
    {
        Assert.Equal(expected, Scrub(text));
        Assert.Equal(expected, Scrub(expected));
    }

    [Fact]
    public void A_shape_inside_a_kept_substitution_is_still_replaced()
    {
        var scrubbed = Scrub($"TOKEN=\"$(echo {Token})\"");

        Assert.Equal($"TOKEN=\"$(echo ghp_[REDACTED:github-token:{Hash8(Token)}])\"", scrubbed);
    }

    [Fact]
    public void A_substitution_in_a_json_shell_command_is_kept()
    {
        var scrubbed = ScrubJson("""{"command":"export API_TOKEN=\"$(cat ~/.aws/credentials)-v2\""}""");

        Assert.Equal("""{"command":"export API_TOKEN=\"$(cat ~/.aws/credentials)[REDACTED:labelled-value]\""}""", scrubbed);
    }

    // Finding 3: a short raw value rewrote every equal leaf, and pagination cursors were lost.
    [Fact]
    public void A_short_labelled_value_does_not_rewrite_equal_leaves_under_other_keys()
    {
        Assert.Equal("""{"password":"[REDACTED:labelled-value]","user":"admin","note":"admin"}""",
            ScrubJson("""{"password":"admin","user":"admin","note":"admin"}"""));
    }

    [Theory]
    [InlineData("""{"items":[],"nextPageToken":"CgkIARIFCOgHEAE"}""")]
    [InlineData("""{"page_token":"eyJvZmZzZXQiOjUwfQ","next_token":"AAAABBBBCCCCDDDD1234"}""")]
    [InlineData("""{"NextContinuationToken":"1ueGcxLPRx1Tr/XYExHnhbYLgveDs2J/wm36Hy4vbOwM="}""")]
    public void A_pagination_cursor_survives(string json) => Assert.Equal(json, ScrubJson(json));

    [Fact]
    public void A_pagination_cursor_in_a_url_survives_and_a_token_beside_it_does_not()
    {
        Assert.Equal("https://api.example/v1/items?page_token=CgkIARIFCOgHEAE&api_key=[REDACTED:labelled-value]",
            Scrub("https://api.example/v1/items?page_token=CgkIARIFCOgHEAE&api_key=k7Qx9ZpL2mV4"));
    }

    // Finding 4: a value labelled by its parent key or by a sibling name.
    [Fact]
    public void A_value_key_under_a_secret_label_is_labelled()
    {
        Assert.Equal("""{"password":{"value":"[REDACTED:labelled-value]","rotated":"2026-01-01"}}""",
            ScrubJson("""{"password":{"value":"hunter2","rotated":"2026-01-01"}}"""));
    }

    [Fact]
    public void A_value_named_by_a_sibling_secret_name_is_labelled()
    {
        Assert.Equal("""{"env":[{"name":"DB_PASSWORD","value":"[REDACTED:labelled-value]"},{"name":"DB_HOST","value":"db.local"},{"name":"API_TOKEN","value":"${{ secrets.API_TOKEN }}"}]}""",
            ScrubJson("""{"env":[{"name":"DB_PASSWORD","value":"s3cr3t"},{"name":"DB_HOST","value":"db.local"},{"name":"API_TOKEN","value":"${{ secrets.API_TOKEN }}"}]}"""));
    }
}
