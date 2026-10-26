using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Extraction;
using Corvid.Kernel.Turns;
using Corvid.Models.Privacy;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Tests;

/// <summary>
/// Labels fixed strings wherever they appear, each at its own confidence, the way GLiNER2 behaved under
/// <c>Privacy:LocalServer: Redact</c> in the A4 end-to-end pass: JSON keys ("status", "details"), the system prompt's words
/// ("Corvid") and nouns ("API key") came back as PII alongside the real values.
/// </summary>
internal sealed class EagerExtractor(params (string Label, string Value, float Confidence)[] known) : IExtractor
{
    public List<string> Requests { get; } = [];

    public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request.Text);
        var spans = ImmutableArray.CreateBuilder<ExtractedSpan>();
        foreach (var (label, value, confidence) in known.Where(item => request.Labels.Contains(item.Label)))
            for (var at = request.Text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = request.Text.IndexOf(value, at + 1, StringComparison.Ordinal))
                spans.Add(new ExtractedSpan(label, value, at, at + value.Length, confidence));
        return Task.FromResult(new ExtractionResult(spans.ToImmutable()));
    }
}

/// <summary>
/// A5 (A4 leftover): redaction reads the person's, the model's and the tools' free text, not the request's structure (JSON
/// keys) nor Corvid's own system prompt, and a word is replaced only where it stands as a word. Memory in the system message
/// is still redacted, and a credential value still never leaves.
/// </summary>
public sealed class EgressStructureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ModelProfile Server = Profiles.Cloud("lan-model") with { Privacy = PrivacyClass.LocalServer };

    private static readonly EagerExtractor Eager = new(
        ("person", "Mark Walls", 0.97f), ("person", "Mark", 0.9f), ("email", "mark@example.org", 0.98f),
        ("person", "status", 0.72f), ("address", "details", 0.5f), ("person", "Corvid", 0.81f), ("person", "assistant", 0.66f),
        ("credential", "API key", 0.88f), ("person", "owner", 0.7f));

    private static PrivacyEgress Egress(IExtractor extractor, bool trusted = true) => new(extractor, new PrivacyEgressOptions
    {
        PolicyFor = profile => profile.Privacy == PrivacyClass.Cloud ? null : EgressPolicy.Default,
        TrustedTexts = trusted ? [TurnOptions.DefaultSystemPrompt] : [],
    });

    // A turn as the kernel renders it: the configured prompt, then core memory, in one system message.
    private static List<ChatMessage> Turn() =>
    [
        new(ChatRole.System, TurnOptions.DefaultSystemPrompt + "\n\nCore memory:\n- The user is Mark Walls."),
        new(ChatRole.User, "Check the deploy status and email the details to mark@example.org. Ask Mark from Marketing, and keep the API key out of it."),
        new(ChatRole.Assistant, [new FunctionCallContent("c1", "shell", new Dictionary<string, object?> { ["command"] = "deploy status --json" })]),
        new(ChatRole.Tool, [new FunctionResultContent("c1",
            JsonDocument.Parse("""{"status":"ok","details":{"owner":"Mark Walls","exit_code":0,"Mark Walls":"admin"}}""").RootElement.Clone())]),
    ];

    private static string Text(ChatMessage message) => string.Concat(message.Contents.Select(content => content switch
    {
        TextContent text => text.Text,
        FunctionCallContent call => JsonSerializer.Serialize(call.Arguments),
        FunctionResultContent result => result.Result is JsonElement json ? json.GetRawText() : "",
        _ => "",
    }));

    [Fact]
    public async Task Structure_and_the_system_prompt_survive_while_the_persons_values_are_redacted()
    {
        var prepared = await Egress(Eager).PrepareAsync(Server, Turn(), new ModelRequestContext("s1", Tainted: false), Ct);

        Assert.Equal(EgressOutcome.Redacted, prepared.Decision.Outcome);
        var system = Text(prepared.Messages[0]);
        var user = Text(prepared.Messages[1]);
        var call = Text(prepared.Messages[2]);
        var result = Text(prepared.Messages[3]);

        // Corvid's own prompt is sent as written ("Corvid", "assistant" untouched); the memory after it is redacted.
        Assert.StartsWith(TurnOptions.DefaultSystemPrompt, system, StringComparison.Ordinal);
        Assert.EndsWith("- The user is <PERSON_1>.", system, StringComparison.Ordinal);
        // The person's words: the values go, the structure words and nouns stay. A5 review: a name of more than three letters
        // goes inside a longer word too ("Mark" in "Marketing"), since a name glued to other text still names the person.
        Assert.Equal("Check the deploy status and email the details to <EMAIL_1>. Ask <PERSON_2> from <PERSON_2>eting, and keep the API key out of it.", user);
        Assert.Contains("deploy status --json", call, StringComparison.Ordinal);
        // JSON keys that are identifiers are structure; a key that is a value (a name) is redacted like a value.
        Assert.Equal("""{"status":"ok","details":{"owner":"<PERSON_1>","exit_code":0,"<PERSON_1>":"admin"}}""",
            result.Replace("\\u003C", "<", StringComparison.Ordinal).Replace("\\u003E", ">", StringComparison.Ordinal));
        Assert.DoesNotContain(prepared.Decision.Labels.Keys, label => label == "address" || label == "credential");
    }

    [Fact]
    public async Task The_extractor_never_reads_the_trusted_prompt_or_identifier_keys()
    {
        var extractor = new EagerExtractor(("person", "Mark Walls", 0.97f));
        await Egress(extractor).PrepareAsync(Server, Turn(), new ModelRequestContext("s1", Tainted: false), Ct);

        Assert.DoesNotContain(extractor.Requests, text => text.Contains("You are Corvid", StringComparison.Ordinal));
        Assert.Contains(extractor.Requests, text => text.Contains("Core memory:", StringComparison.Ordinal));
        Assert.DoesNotContain(extractor.Requests, text => text is "status" or "details" or "exit_code" or "owner");
        // A key that is a value is still read.
        Assert.Contains(extractor.Requests, text => text == "Mark Walls");
    }

    [Fact]
    public async Task Trusted_text_is_trusted_only_in_a_system_message()
    {
        // The person pasting Corvid's own prompt with a name in it: their message is read whole.
        List<ChatMessage> messages =
        [
            new(ChatRole.System, TurnOptions.DefaultSystemPrompt),
            new(ChatRole.User, TurnOptions.DefaultSystemPrompt + " Corvid is what Mark Walls calls it."),
        ];
        var prepared = await Egress(Eager).PrepareAsync(Server, messages, new ModelRequestContext("s1", Tainted: false), Ct);

        Assert.Equal(TurnOptions.DefaultSystemPrompt, Text(prepared.Messages[0]));
        var user = Text(prepared.Messages[1]);
        Assert.DoesNotContain("Corvid", user, StringComparison.Ordinal);
        Assert.DoesNotContain("Mark Walls", user, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_trusted_texts_the_system_prompt_is_scanned_as_before()
    {
        var prepared = await Egress(Eager, trusted: false).PrepareAsync(Server, Turn(), new ModelRequestContext("s1", Tainted: false), Ct);

        Assert.DoesNotContain("Corvid", Text(prepared.Messages[0]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_credential_value_in_an_identifier_shaped_key_still_never_leaves()
    {
        // Lowercase letters only would pass as an identifier, but the credential detector reads the JSON with its keys.
        const string token = "ghp_16C7e42F292c6912E7710c838347Ae178B4a";
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "what is in the config?"),
            new(ChatRole.Tool, [new FunctionResultContent("c1", JsonDocument.Parse($$"""{"{{token}}":"value","token":"{{token}}"}""").RootElement.Clone())]),
        ];
        var prepared = await Egress(new EagerExtractor()).PrepareAsync(Server, messages, new ModelRequestContext("s1", Tainted: false), Ct);

        Assert.NotEqual(EgressOutcome.Sent, prepared.Decision.Outcome);
        Assert.All(prepared.Messages, message => Assert.DoesNotContain(token, Text(message), StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_span_without_its_labels_shape_is_not_a_value()
    {
        // The real GLiNER2 called "status" an email in plain text, at a confidence over the threshold.
        var extractor = new EagerExtractor(("email", "status", 0.93f), ("phone number", "nightly", 0.8f), ("email", "ann@example.org", 0.95f),
            ("phone number", "555-0142", 0.9f), ("email", "fake@openai-compatible", 0.9f), ("person", "You", 0.85f), ("person", "User", 0.8f),
            ("person", "Ann", 0.9f));
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "User: What is the nightly status? You know Ann: tell ann@example.org or call 555-0142.\ngenerator: fake@openai-compatible"),
        ];
        var prepared = await Egress(extractor).PrepareAsync(Server, messages, new ModelRequestContext("s1", Tainted: false), Ct);

        // The real GLiNER2 (A5 pass) also called "You" and "User" people, and the TurnState's "fake@openai-compatible" an email.
        Assert.Equal("User: What is the nightly status? You know <PERSON_1>: tell <EMAIL_1> or call <PHONE_NUMBER_1>.\ngenerator: fake@openai-compatible",
            Text(prepared.Messages[0]));
    }

    [Fact]
    public async Task The_default_threshold_is_0_4_and_a_name_found_at_0_5_is_redacted()
    {
        // A5 review: A5 had raised it to 0.6; the structure rules drop the false positives that raise was for.
        Assert.Equal(0.4f, EgressPolicy.Default.Threshold);
        var extractor = new EagerExtractor(("person", "Hannah Okafor", 0.5f), ("address", "details", 0.5f), ("person", "owner", 0.45f));
        List<ChatMessage> messages = [new(ChatRole.User, "Send the details to the owner, Hannah Okafor.")];

        var prepared = await Egress(extractor).PrepareAsync(Server, messages, new ModelRequestContext("s1", Tainted: false), Ct);

        Assert.Equal("Send the details to the owner, <PERSON_1>.", Text(prepared.Messages[0]));
    }

    [Fact]
    public async Task A_credential_span_of_letters_that_is_no_credential_noun_is_a_value()
    {
        // The credential detector is stubbed out, so this is the extractor's span alone.
        var extractor = new EagerExtractor(("credential", "wifi password", 0.9f), ("credential", "sunshinerainbow", 0.8f));
        var egress = new PrivacyEgress(extractor, new PrivacyEgressOptions
        {
            PolicyFor = _ => EgressPolicy.Default,
            FindCredentials = _ => [],
        });
        List<ChatMessage> messages = [new(ChatRole.User, "my wifi password is sunshinerainbow")];

        var prepared = await egress.PrepareAsync(Server, messages, new ModelRequestContext("s1", Tainted: false), Ct);

        Assert.Equal("my wifi password is <CREDENTIAL_1>", Text(prepared.Messages[0]));
    }

    [Theory]
    [InlineData("API key", true)]
    [InlineData("password", true)]
    [InlineData("wifi password", true)]
    [InlineData("SSH private key", true)]
    [InlineData("client-secret", true)]
    [InlineData("sunshinerainbow", false)]
    [InlineData("hunter", false)]
    [InlineData("hunter2", false)]
    [InlineData("correct horse battery staple", false)]
    public void Only_credential_nouns_are_structure_under_the_credential_label(string span, bool structure) =>
        Assert.Equal(structure, PrivacyEgress.Structural(PrivacyEgress.CredentialLabel, span, PrivacyEgress.StructureWords));

    [Fact]
    public async Task A_name_inside_a_handle_is_redacted_and_a_short_one_only_as_a_word()
    {
        var extractor = new EagerExtractor(("person", "Smith", 0.9f), ("person", "Ann", 0.9f));
        List<ChatMessage> messages = [new(ChatRole.User, "Ask Smith (handle SmithJ, or jSmith2) and Ann, who wrote the Annual report.")];

        var prepared = await Egress(extractor).PrepareAsync(Server, messages, new ModelRequestContext("s1", Tainted: false), Ct);

        var sent = Text(prepared.Messages[0]);
        Assert.DoesNotContain("Smith", sent, StringComparison.Ordinal);
        Assert.Equal("Ask <PERSON_1> (handle <PERSON_1>J, or j<PERSON_1>2) and <PERSON_2>, who wrote the Annual report.", sent);
    }

    [Fact]
    public async Task A_name_that_is_a_word_of_the_trusted_prompt_is_still_redacted()
    {
        // A5 review: the trusted prompt's words no longer count as structure where the person or a tool writes them.
        const string prompt = "You are Corvid. Answer with grace and keep it brief.";
        var extractor = new EagerExtractor(("person", "grace", 0.8f));
        var egress = new PrivacyEgress(extractor, new PrivacyEgressOptions { PolicyFor = _ => EgressPolicy.Default, TrustedTexts = [prompt] });
        List<ChatMessage> messages =
        [
            new(ChatRole.System, prompt),
            new(ChatRole.User, "Forward it to grace, she runs the build."),
            new(ChatRole.Tool, [new FunctionResultContent("c1", JsonDocument.Parse("""{"owner":"grace"}""").RootElement.Clone())]),
        ];

        var prepared = await egress.PrepareAsync(Server, messages, new ModelRequestContext("s1", Tainted: false), Ct);

        Assert.Equal(prompt, Text(prepared.Messages[0]));
        Assert.Equal("Forward it to <PERSON_1>, she runs the build.", Text(prepared.Messages[1]));
        Assert.DoesNotContain("grace", Text(prepared.Messages[2]), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_that_only_looks_like_structure_is_read_as_a_value()
    {
        // A5 fix round (D7): a result keyed by a lowercase username; the key is data, so it is scanned and redacted, and no
        // key of the request makes the same word in free text read as structure.
        var extractor = new EagerExtractor(("person", "jsmith", 0.9f), ("email", "j@example.org", 0.95f));
        List<ChatMessage> messages =
        [
            new(ChatRole.User, "who is jsmith?"),
            new(ChatRole.Tool, [new FunctionResultContent("c1", JsonDocument.Parse("""{"jsmith":{"email":"j@example.org","exit_code":0}}""").RootElement.Clone())]),
        ];
        var prepared = await Egress(extractor).PrepareAsync(Server, messages, new ModelRequestContext("s1", Tainted: false), Ct);

        Assert.Equal(EgressOutcome.Redacted, prepared.Decision.Outcome);
        Assert.Equal("who is <PERSON_1>?", Text(prepared.Messages[0]));
        Assert.Equal("""{"<PERSON_1>":{"email":"<EMAIL_1>","exit_code":0}}""",
            Text(prepared.Messages[1]).Replace("\\u003C", "<", StringComparison.Ordinal).Replace("\\u003E", ">", StringComparison.Ordinal));
        Assert.DoesNotContain(extractor.Requests, text => text is "email" or "exit_code");
    }

    [Theory]
    [InlineData("status", true)]
    [InlineData("exit_code", true)]
    [InlineData("toolCallId", true)]
    [InlineData("content-type", true)]
    [InlineData("jsmith", false)]
    [InlineData("john_smith", false)]
    [InlineData("Mark Walls", false)]
    public void Structure_keys_are_made_of_known_words(string key, bool structure) =>
        Assert.Equal(structure, PrivacyEgress.StructureKey(key));

    [Theory]
    [InlineData("status", true)]
    [InlineData("exit_code", true)]
    [InlineData("toolCallId", true)]
    [InlineData("content-type", true)]
    [InlineData("Mark Walls", false)]
    [InlineData("mark@example.org", false)]
    [InlineData("Alice", false)]
    [InlineData("line2", false)]
    [InlineData("sk-proj-abc123", false)]
    public void Identifier_keys_are_structure(string key, bool structure) =>
        Assert.Equal(structure, PrivacyEgress.IdentifierKey(key));
}

/// <summary>Which credential spans name a kind of credential (structure, left as it is) and which hold one (redacted).</summary>
public sealed class CredentialNounTests
{
    [Theory]
    [InlineData("API key", true)]
    [InlineData("wifi password", true)]
    [InlineData("client secret", true)]
    [InlineData("password", true)]
    // A5 fix verification: a word as likely the value as the noun is a value on its own ("my password is secret").
    [InlineData("secret", false)]
    [InlineData("admin", false)]
    [InlineData("pin", false)]
    [InlineData("sunshinerainbow", false)]
    public void ASpanIsANounOnlyWhenItNamesAKindOfCredential(string value, bool noun) =>
        Assert.Equal(noun, PrivacyEgress.CredentialNoun(value));
}
