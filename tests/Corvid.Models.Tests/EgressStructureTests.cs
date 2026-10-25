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
        // The person's words: the values go, the structure words and nouns stay, and "Mark" goes but "Marketing" stays.
        Assert.Equal("Check the deploy status and email the details to <EMAIL_1>. Ask <PERSON_2> from Marketing, and keep the API key out of it.", user);
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
            ("phone number", "555-0142", 0.9f));
        List<ChatMessage> messages = [new(ChatRole.User, "What is the nightly status? Tell ann@example.org or call 555-0142.")];
        var prepared = await Egress(extractor).PrepareAsync(Server, messages, new ModelRequestContext("s1", Tainted: false), Ct);

        Assert.Equal("What is the nightly status? Tell <EMAIL_1> or call <PHONE_NUMBER_1>.", Text(prepared.Messages[0]));
    }

    [Fact]
    public void The_default_threshold_is_raised()
    {
        Assert.True(EgressPolicy.Default.Threshold >= 0.6f);
    }

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
