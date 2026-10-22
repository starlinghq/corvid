using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Extraction;
using Corvid.Models.Privacy;
using Corvid.Secrets;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Tests;

/// <summary>Finds fixed values by substring, as GLiNER2 would label them; refuses input over a length, as GLiNER2 does.</summary>
internal sealed class FakePiiExtractor(params (string Label, string Value)[] known) : IExtractor
{
    public int MaxChars { get; init; } = int.MaxValue;

    public Exception? Failure { get; init; }

    public List<string> Requests { get; } = [];

    public Task<ExtractionResult> ExtractAsync(ExtractionRequest request, CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
            throw Failure;
        if (request.Text.Length > MaxChars)
            throw new ArgumentException("input exceeds the model's limit");
        Requests.Add(request.Text);
        var spans = ImmutableArray.CreateBuilder<ExtractedSpan>();
        foreach (var (label, value) in known.Where(item => request.Labels.Contains(item.Label)))
            for (var at = request.Text.IndexOf(value, StringComparison.Ordinal); at >= 0; at = request.Text.IndexOf(value, at + 1, StringComparison.Ordinal))
                spans.Add(new ExtractedSpan(label, value, at, at + value.Length, 0.9f));
        return Task.FromResult(new ExtractionResult(spans.ToImmutable()));
    }
}

/// <summary>Records what reached the provider and answers with a scripted response.</summary>
internal sealed class ScriptedChatClient(Func<IReadOnlyList<ChatMessage>, ChatResponse> answer) : IChatClient
{
    public List<IReadOnlyList<ChatMessage>> Calls { get; } = [];

    public List<ChatOptions?> Options { get; } = [];

    public List<ModelRequestContext?> Ambient { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        Calls.Add(list);
        Options.Add(options);
        Ambient.Add(ModelRequestContext.Current);
        return Task.FromResult(answer(list));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

public sealed class PrivacyEgressTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly ModelProfile Cloud = Profiles.Cloud("claude");

    private static readonly FakePiiExtractor Pii = new(("person", "Alice Smith"), ("email", "alice@example.com"),
        ("account number", "12345678"));

    private static PrivacyEgress Egress(IExtractor? extractor, EgressPolicy? policy = null) =>
        new(extractor, new PrivacyEgressOptions { PolicyFor = profile => profile.Privacy == PrivacyClass.Cloud ? policy ?? EgressPolicy.Default : null });

    private static ChatOptions Session(string session, bool tainted = false) =>
        new ModelRequestContext(session, tainted).Apply(new ChatOptions { MaxOutputTokens = 64 });

    private static List<ChatMessage> Conversation() =>
    [
        new(ChatRole.System, "You are Corvid."),
        new(ChatRole.User, "Email Alice Smith at alice@example.com about account 12345678."),
        new(ChatRole.Assistant, [new FunctionCallContent("c1", "lookup", new Dictionary<string, object?>
        {
            ["name"] = "Alice Smith",
            ["filter"] = JsonDocument.Parse("""{"account": 12345678, "tags": ["alice@example.com", "x"]}""").RootElement.Clone(),
        })]),
        new(ChatRole.Tool, [new FunctionResultContent("c1", JsonDocument.Parse("""{"owner":"Alice Smith","ok":true}""").RootElement.Clone())]),
    ];

    private static string Sent(IEnumerable<ChatMessage> messages) =>
        string.Join("\n", messages.SelectMany(message => message.Contents).Select(content => content switch
        {
            TextContent text => text.Text,
            FunctionCallContent call => JsonSerializer.Serialize(call.Arguments),
            FunctionResultContent result => JsonSerializer.Serialize(result.Result),
            _ => "",
        }));

    [Fact]
    public async Task Redaction_replaces_pii_everywhere_and_the_response_maps_back_in_text_and_tool_arguments()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("Writing to <PERSON_1> now."),
            new FunctionCallContent("c2", "send_email", new Dictionary<string, object?> { ["to"] = "<EMAIL_1>", ["note"] = "re <ACCOUNT_NUMBER_1>" }),
        ])));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(Pii));

        var response = await client.GetResponseAsync(Conversation(), Session("s1"), Ct);

        var sent = Sent(provider.Calls[0]);
        Assert.DoesNotContain("Alice Smith", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("12345678", sent, StringComparison.Ordinal);
        Assert.Contains("Email <PERSON_1> at <EMAIL_1> about account <ACCOUNT_NUMBER_1>.", sent, StringComparison.Ordinal);
        // A numeric account number becomes its placeholder string; other JSON keeps its shape.
        Assert.Contains("\"account\":\"\\u003CACCOUNT_NUMBER_1\\u003E\"", sent, StringComparison.Ordinal);
        Assert.Contains("\"ok\":true", sent, StringComparison.Ordinal);

        Assert.Equal("Writing to Alice Smith now.", response.Text);
        var call = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Single();
        Assert.Equal("alice@example.com", call.Arguments!["to"]);
        Assert.Equal("re 12345678", call.Arguments["note"]);
    }

    [Fact]
    public async Task Placeholders_are_stable_within_a_session_and_independent_across_sessions()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var egress = Egress(new FakePiiExtractor(("person", "Alice Smith"), ("person", "Bob Jones")));
        var client = new PrivacyEgressChatClient(provider, Cloud, egress);

        await client.GetResponseAsync([new(ChatRole.User, "Alice Smith called.")], Session("s1"), Ct);
        await client.GetResponseAsync([new(ChatRole.User, "Alice Smith called."), new(ChatRole.User, "Then Bob Jones.")], Session("s1"), Ct);
        await client.GetResponseAsync([new(ChatRole.User, "Bob Jones first.")], Session("s2"), Ct);

        Assert.Equal("<PERSON_1> called.", Sent(provider.Calls[0]));
        // The earlier message renders identically, so a cloud prompt cache keeps its prefix.
        Assert.Equal("<PERSON_1> called.\nThen <PERSON_2>.", Sent(provider.Calls[1]));
        Assert.Equal("<PERSON_1> first.", Sent(provider.Calls[2]));
    }

    [Fact]
    public async Task Corvid_request_keys_are_stripped_before_the_provider_and_the_context_is_ambient()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(Pii));
        var options = Session("s1", tainted: true);
        options.AdditionalProperties!["vendor_flag"] = 1;

        await client.GetResponseAsync([new(ChatRole.User, "hello")], options, Ct);

        var passed = provider.Options.Single()!;
        Assert.Equal(64, passed.MaxOutputTokens);
        Assert.Equal(["vendor_flag"], passed.AdditionalProperties!.Keys);
        Assert.Equal(new ModelRequestContext("s1", true), provider.Ambient.Single());
        // The caller's options are untouched.
        Assert.True(options.AdditionalProperties.ContainsKey(ModelRequestKeys.Tainted));
    }

    [Fact]
    public async Task Refuse_policy_keeps_pii_local_and_sends_nothing()
    {
        var provider = new ScriptedChatClient(_ => throw new InvalidOperationException("must not be called"));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(Pii, EgressPolicy.Default with { Action = EgressAction.Refuse }));

        var refused = await Assert.ThrowsAsync<PrivacyEgressRefusedException>(() =>
            client.GetResponseAsync(Conversation(), Session("s1"), Ct));

        Assert.Contains("account number, email, person", refused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("Alice", refused.Message, StringComparison.Ordinal);
        Assert.Empty(provider.Calls);

        // Without PII the same policy sends.
        var allowed = new PrivacyEgressChatClient(new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))),
            Cloud, Egress(Pii, EgressPolicy.Default with { Action = EgressAction.Refuse }));
        Assert.Equal("ok", (await allowed.GetResponseAsync([new(ChatRole.User, "what is 2+2?")], Session("s1"), Ct)).Text);
    }

    [Theory]
    [InlineData(EgressAction.Send)]
    [InlineData(EgressAction.Redact)]
    [InlineData(EgressAction.Refuse)]
    public async Task Without_an_extractor_or_when_it_fails_cloud_requests_are_refused_never_sent_unscanned(EgressAction action)
    {
        var policy = EgressPolicy.Default with { Action = action };
        foreach (var extractor in new IExtractor?[] { null, new FakePiiExtractor { Failure = new InvalidOperationException("onnx crashed") } })
        {
            var provider = new ScriptedChatClient(_ => throw new InvalidOperationException("must not be called"));
            var client = new PrivacyEgressChatClient(provider, Cloud, Egress(extractor, policy));

            var refused = await Assert.ThrowsAsync<PrivacyEgressRefusedException>(() =>
                client.GetResponseAsync([new(ChatRole.User, "hello")], Session("s1"), Ct));
            Assert.Contains(extractor is null ? "no PII extractor" : "extractor failed", refused.Reason, StringComparison.Ordinal);

            await Assert.ThrowsAsync<PrivacyEgressRefusedException>(async () =>
            {
                await foreach (var _ in client.GetStreamingResponseAsync([new(ChatRole.User, "hello")], Session("s1"), Ct))
                {
                }
            });
            Assert.Empty(provider.Calls);
        }
    }

    [Fact]
    public async Task Send_policy_sends_as_is_unless_the_request_is_tainted()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(Pii, EgressPolicy.Default with { Action = EgressAction.Send }));

        await client.GetResponseAsync([new(ChatRole.User, "Alice Smith")], Session("s1"), Ct);
        await client.GetResponseAsync([new(ChatRole.User, "Alice Smith")], Session("s1", tainted: true), Ct);

        Assert.Equal("Alice Smith", Sent(provider.Calls[0]));
        // Outside content (plan §10.3) is never sent raw: taint raises send to redact.
        Assert.Equal("<PERSON_1>", Sent(provider.Calls[1]));
    }

    [Fact]
    public async Task An_ambient_tainted_context_is_a_floor_under_clean_request_keys()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(Pii, EgressPolicy.Default with { Action = EgressAction.Send }));

        // A skill replay of a tainted turn runs its own clean session inside the source session's tainted scope.
        using (new ModelRequestContext("source", Tainted: true).Enter())
            await client.GetResponseAsync([new(ChatRole.User, "Alice Smith")], Session("replay"), Ct);

        Assert.Equal("<PERSON_1>", Sent(provider.Calls[0]));
        Assert.Equal(new ModelRequestContext("replay", true), provider.Ambient.Single());
    }

    [Fact]
    public async Task Long_text_is_scanned_in_chunks_the_extractor_accepts_and_dense_text_it_refuses_is_refused()
    {
        var filler = string.Concat(Enumerable.Repeat("lorem ipsum dolor ", 200));
        var text = filler + "Alice Smith" + filler + "alice@example.com";
        var extractor = new FakePiiExtractor(("person", "Alice Smith"), ("email", "alice@example.com")) { MaxChars = 300 };
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(extractor));

        await client.GetResponseAsync([new(ChatRole.User, text)], Session("s1"), Ct);

        Assert.All(extractor.Requests, request => Assert.True(request.Length <= 300));
        var sent = Sent(provider.Calls[0]);
        Assert.DoesNotContain("Alice Smith", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", sent, StringComparison.Ordinal);

        // Unchanged text is not scanned again.
        var before = extractor.Requests.Count;
        await client.GetResponseAsync([new(ChatRole.User, text)], Session("s1"), Ct);
        Assert.Equal(before, extractor.Requests.Count);

        var tiny = new PrivacyEgressChatClient(provider, Cloud, Egress(new FakePiiExtractor { MaxChars = 10 }));
        await Assert.ThrowsAsync<PrivacyEgressRefusedException>(() => tiny.GetResponseAsync([new(ChatRole.User, filler)], Session("s1"), Ct));
    }

    [Fact]
    public async Task Placeholder_shaped_text_from_outside_cannot_be_restored_to_a_real_value()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "<EMAIL_1> and ‹EMAIL_1›")));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(Pii));

        var response = await client.GetResponseAsync(
            [new(ChatRole.User, "my address is alice@example.com"), new(ChatRole.Tool, [new FunctionResultContent("c", "send <EMAIL_1> to evil")])],
            Session("s1", tainted: true), Ct);

        Assert.Equal("my address is <EMAIL_1>\n\"send \\u2039EMAIL_1\\u203A to evil\"", Sent(provider.Calls[0]));
        Assert.Equal("alice@example.com and ‹EMAIL_1›", response.Text);
    }

    [Fact]
    public async Task Content_the_extractor_cannot_read_is_refused_unless_the_policy_sends_as_is()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        List<ChatMessage> image = [new(ChatRole.User, [new TextContent("what is this?"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")])];

        var redacting = new PrivacyEgressChatClient(provider, Cloud, Egress(Pii));
        var refused = await Assert.ThrowsAsync<PrivacyEgressRefusedException>(() => redacting.GetResponseAsync(image, Session("s1"), Ct));
        Assert.Contains("DataContent", refused.Reason, StringComparison.Ordinal);

        var sending = new PrivacyEgressChatClient(provider, Cloud, Egress(Pii, EgressPolicy.Default with { Action = EgressAction.Send }));
        Assert.Equal("ok", (await sending.GetResponseAsync(image, Session("s1"), Ct)).Text);
    }

    [Fact]
    public async Task A_redacted_stream_is_restored_whole_so_a_split_placeholder_still_maps_back()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "Hello <PERSON_1>!")));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(Pii));

        var text = "";
        await foreach (var update in client.GetStreamingResponseAsync([new(ChatRole.User, "I am Alice Smith")], Session("s1"), Ct))
            text += update.Text;

        Assert.Equal("Hello Alice Smith!", text);
    }

    [Fact]
    public async Task A_request_holding_local_only_memory_is_refused_by_a_cloud_model_whatever_its_policy()
    {
        var provider = new ScriptedChatClient(_ => throw new InvalidOperationException("must not be called"));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(Pii, EgressPolicy.Default with { Action = EgressAction.Send }));
        var options = new ModelRequestContext("s1", Tainted: false, LocalOnly: true).Apply(null);

        var error = await Assert.ThrowsAsync<PrivacyEgressRefusedException>(() =>
            client.GetResponseAsync([new(ChatRole.User, "hello")], options, Ct));

        Assert.Contains("local-only", error.Reason, StringComparison.Ordinal);
        Assert.Empty(provider.Calls);

        // A gated local server is the user's own machine: it may receive local-only memory.
        var server = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var gated = new PrivacyEgressChatClient(server, Profiles.LocalServer("llama"),
            new PrivacyEgress(Pii, new PrivacyEgressOptions { PolicyFor = _ => EgressPolicy.Default }));
        await gated.GetResponseAsync([new(ChatRole.User, "hello")], options, Ct);
        Assert.Single(server.Calls);
    }

    [Fact]
    public async Task Instructions_in_the_options_are_scanned_and_redacted_like_the_messages()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(Pii));
        var options = Session("s1");
        options.Instructions = "The user is Alice Smith.";

        await client.GetResponseAsync([new(ChatRole.User, "hi")], options, Ct);

        Assert.Null(provider.Options.Single()!.Instructions);
        Assert.Equal("The user is <PERSON_1>.\nhi", Sent(provider.Calls[0]));
        Assert.Equal("The user is Alice Smith.", options.Instructions);
    }

    [Fact]
    public async Task Json_keys_are_redacted_and_results_the_scan_cannot_read_as_sent_are_refused()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(Pii));

        await client.GetResponseAsync([new(ChatRole.Tool, [new FunctionResultContent("c1",
            JsonDocument.Parse("""{"alice@example.com": {"owner": "Alice Smith"}}""").RootElement.Clone())])], Session("s1"), Ct);
        var sent = Sent(provider.Calls[0]);
        Assert.DoesNotContain("alice@example.com", sent, StringComparison.Ordinal);
        Assert.Contains("\\u003CEMAIL_1\\u003E", sent, StringComparison.Ordinal);

        // A tool's own result object is serialized by the adapter, not as the ToString() a scan would read.
        var error = await Assert.ThrowsAsync<PrivacyEgressRefusedException>(() => client.GetResponseAsync(
            [new(ChatRole.Tool, [new FunctionResultContent("c2", new { Owner = "Alice Smith" })])], Session("s1"), Ct));
        Assert.Contains("FunctionResultContent", error.Reason, StringComparison.Ordinal);
        Assert.Single(provider.Calls);
    }

    [Fact]
    public async Task Local_profiles_are_not_gated()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var client = new PrivacyEgressChatClient(provider, Profiles.Local("qwen"), Egress(null));

        await client.GetResponseAsync([new(ChatRole.User, "Alice Smith")], Session("s1"), Ct);

        Assert.Equal("Alice Smith", Sent(provider.Calls[0]));
    }

    // Fictitious, in GitHub's classic token shape.
    private const string GitHubToken = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8";

    private static readonly string PemBlock = "-----BEGIN RSA PRIVATE KEY-----\n" +
        string.Join("\n", Enumerable.Range(0, 6).Select(i => $"MIIEowIBAAKCAQEA{i}bcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUV")) +
        "\n-----END RSA PRIVATE KEY-----";

    [Fact]
    public async Task A_credential_value_is_redacted_under_a_send_policy_at_any_threshold_and_restored_in_the_response()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant,
        [
            new TextContent("Cloning with <CREDENTIAL_1>."),
            new FunctionCallContent("c2", "shell", new Dictionary<string, object?> { ["env"] = "GH_TOKEN=<CREDENTIAL_1>" }),
        ])));
        // The extractor finds nothing, and its threshold would drop anything it found: the detector does not depend on it.
        var egress = Egress(new FakePiiExtractor(), EgressPolicy.Default with { Action = EgressAction.Send, Threshold = 0.99f });
        var client = new PrivacyEgressChatClient(provider, Cloud, egress);

        var response = await client.GetResponseAsync([new(ChatRole.User, $"Set GITHUB_TOKEN={GitHubToken} in CI.")], Session("s1"), Ct);

        Assert.Equal("Set GITHUB_TOKEN=<CREDENTIAL_1> in CI.", Sent(provider.Calls[0]));
        Assert.Equal($"Cloning with {GitHubToken}.", response.Text);
        var call = response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Single();
        Assert.Equal($"GH_TOKEN={GitHubToken}", call.Arguments!["env"]);

        var decision = (await egress.PrepareAsync(Cloud, [new(ChatRole.User, $"token {GitHubToken}")], new ModelRequestContext("s2", false), Ct)).Decision;
        Assert.Equal(EgressOutcome.Redacted, decision.Outcome);
        Assert.Equal(EgressAction.Redact, decision.Action);
        Assert.Contains("1 credential value", decision.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_switch_time_assessment_applies_the_credential_floor()
    {
        string[] texts = [$"token {GitHubToken}"];

        var sending = await Egress(new FakePiiExtractor(), EgressPolicy.Default with { Action = EgressAction.Send })
            .AssessAsync(Cloud, texts, tainted: false, Ct);
        Assert.Equal(EgressOutcome.Redacted, sending.Outcome);
        Assert.Equal(EgressAction.Redact, sending.Action);

        var refusing = await Egress(new FakePiiExtractor(), EgressPolicy.Default with { Action = EgressAction.Refuse })
            .AssessAsync(Cloud, texts, tainted: false, Ct);
        Assert.Equal(EgressOutcome.Refused, refusing.Outcome);
        Assert.Contains("1 credential value", refusing.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(GitHubToken, refusing.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_credential_value_under_a_refuse_policy_is_refused_and_the_reason_never_names_it()
    {
        var provider = new ScriptedChatClient(_ => throw new InvalidOperationException("must not be called"));
        var client = new PrivacyEgressChatClient(provider, Cloud,
            Egress(new FakePiiExtractor(), EgressPolicy.Default with { Action = EgressAction.Refuse, Labels = ["person"] }));

        var refused = await Assert.ThrowsAsync<PrivacyEgressRefusedException>(() =>
            client.GetResponseAsync([new(ChatRole.User, "The staging database password is hunter2-Staging!")], Session("s1"), Ct));

        Assert.Contains("credential (1 credential value)", refused.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", refused.Message, StringComparison.Ordinal);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task A_gliner_credential_span_is_a_noun_and_does_not_raise_a_send_policy()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var client = new PrivacyEgressChatClient(provider, Cloud,
            Egress(new FakePiiExtractor(("credential", "API key")), EgressPolicy.Default with { Action = EgressAction.Send }));

        await client.GetResponseAsync([new(ChatRole.User, "Where do I put the API key?")], Session("s1"), Ct);

        Assert.Equal("Where do I put the API key?", Sent(provider.Calls[0]));
    }

    [Fact]
    public async Task A_pem_block_across_a_chunk_boundary_is_redacted_whole()
    {
        var filler = string.Concat(Enumerable.Repeat("lorem ipsum dolor ", 12));
        var text = filler + PemBlock + " " + filler;
        var extractor = new FakePiiExtractor { MaxChars = 300 };
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var egress = new PrivacyEgress(extractor, new PrivacyEgressOptions { ChunkChars = 250, ChunkOverlap = 40 });
        var client = new PrivacyEgressChatClient(provider, Cloud, egress);

        await client.GetResponseAsync([new(ChatRole.User, text)], Session("s1"), Ct);

        // No chunk the extractor saw held the whole block.
        Assert.DoesNotContain(extractor.Requests, request => request.Contains(PemBlock, StringComparison.Ordinal));
        Assert.Equal(filler + "<CREDENTIAL_1> " + filler, Sent(provider.Calls[0]));
    }

    [Fact]
    public async Task A_labelled_value_in_a_json_tool_result_is_found_with_its_key_and_redacted()
    {
        var provider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(new FakePiiExtractor(), EgressPolicy.Default with { Action = EgressAction.Send }));

        await client.GetResponseAsync([new(ChatRole.Tool, [new FunctionResultContent("c1",
            JsonDocument.Parse("""{"user": "svc", "password": "c0rrect-Pony-77", "note": "xé"}""").RootElement.Clone())])], Session("s1"), Ct);

        var sent = Sent(provider.Calls[0]);
        Assert.DoesNotContain("c0rrect-Pony-77", sent, StringComparison.Ordinal);
        Assert.Contains("\\u003CCREDENTIAL_1\\u003E", sent, StringComparison.Ordinal);
        Assert.Contains("\"user\":\"svc\"", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_an_extractor_a_request_with_a_credential_is_still_refused()
    {
        var provider = new ScriptedChatClient(_ => throw new InvalidOperationException("must not be called"));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(null, EgressPolicy.Default with { Action = EgressAction.Send }));

        var refused = await Assert.ThrowsAsync<PrivacyEgressRefusedException>(() =>
            client.GetResponseAsync([new(ChatRole.User, $"token {GitHubToken}")], Session("s1"), Ct));

        Assert.Contains("no PII extractor", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task A_credential_detector_timeout_refuses_the_request()
    {
        var provider = new ScriptedChatClient(_ => throw new InvalidOperationException("must not be called"));
        var egress = new PrivacyEgress(new FakePiiExtractor(), new PrivacyEgressOptions
        {
            PolicyFor = _ => EgressPolicy.Default with { Action = EgressAction.Send },
            FindCredentials = text => throw new RegexMatchTimeoutException(text, "pattern", TimeSpan.FromSeconds(1)),
        });
        var client = new PrivacyEgressChatClient(provider, Cloud, egress);

        var refused = await Assert.ThrowsAsync<PrivacyEgressRefusedException>(() =>
            client.GetResponseAsync([new(ChatRole.User, "hello")], Session("s1"), Ct));

        Assert.Contains("credential detector timed out", refused.Reason, StringComparison.Ordinal);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task A_credential_with_content_the_extractor_cannot_read_is_refused_even_under_send()
    {
        var provider = new ScriptedChatClient(_ => throw new InvalidOperationException("must not be called"));
        var client = new PrivacyEgressChatClient(provider, Cloud, Egress(new FakePiiExtractor(), EgressPolicy.Default with { Action = EgressAction.Send }));
        List<ChatMessage> image = [new(ChatRole.User, [new TextContent($"token {GitHubToken}"), new DataContent(new byte[] { 1, 2, 3 }, "image/png")])];

        var refused = await Assert.ThrowsAsync<PrivacyEgressRefusedException>(() => client.GetResponseAsync(image, Session("s1"), Ct));

        Assert.Contains("1 credential value and DataContent", refused.Reason, StringComparison.Ordinal);
    }

    private static IEnumerable<string> Arguments(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().SelectMany(call => call.Arguments!.Values).Select(value => value?.ToString() ?? "");

    // Review of C-032, finding 1: a JSON-escaped quote inside a tool-call argument no longer cuts the value short.
    [Theory]
    [InlineData("command", "mysql -u root password = Xk9\"vQ2!mL7pZ", "mysql -u root password = <CREDENTIAL_1>")]
    [InlineData("password", "Xk9\"vQ2!mL7pZ", "<CREDENTIAL_1>")]
    [InlineData("command", "say \"TOKEN=Xk9vQ2mL7pZ\" twice", "say \"TOKEN=<CREDENTIAL_1>\" twice")]
    public async Task A_quote_inside_a_tool_call_argument_is_redacted_with_the_rest_of_the_value(string key, string value, string expected)
    {
        var egress = Egress(new FakePiiExtractor(), EgressPolicy.Default with { Action = EgressAction.Send });
        List<ChatMessage> messages = [new(ChatRole.Assistant, [new FunctionCallContent("c1", "shell", new Dictionary<string, object?> { [key] = value })])];

        var prepared = await egress.PrepareAsync(Cloud, messages, new ModelRequestContext("s1", false), Ct);

        Assert.Equal(EgressOutcome.Redacted, prepared.Decision.Outcome);
        Assert.Equal(expected, Assert.Single(Arguments(prepared.Messages)));
    }

    [Fact]
    public async Task A_credential_found_in_json_that_cannot_be_decoded_refuses_the_request()
    {
        // A finder that cuts a value at an escape, as the detector did before finding 1: the JSON form decodes to nothing valid.
        var egress = new PrivacyEgress(new FakePiiExtractor(), new PrivacyEgressOptions
        {
            PolicyFor = _ => EgressPolicy.Default with { Action = EgressAction.Send },
            FindCredentials = text => text.IndexOf("\\SECRET", StringComparison.Ordinal) is var at and >= 0
                ? [new CredentialMatch(CredentialKind.LabelledValue, at, "\\SECRETVALUE".Length, 0, CredentialStrength.Labelled)]
                : [],
        });
        List<ChatMessage> messages = [new(ChatRole.Assistant, [new FunctionCallContent("c1", "shell", new Dictionary<string, object?> { ["p"] = "x\\SECRETVALUE" })])];

        var prepared = await egress.PrepareAsync(Cloud, messages, new ModelRequestContext("s1", false), Ct);

        Assert.Equal(EgressOutcome.Refused, prepared.Decision.Outcome);
        Assert.Contains("could not be decoded", prepared.Decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_credential_the_detector_finds_again_after_redaction_refuses_the_request()
    {
        // The first scan finds only the head of a value; the redacted text shows the rest. The gate refuses rather than send it.
        var egress = new PrivacyEgress(new FakePiiExtractor(), new PrivacyEgressOptions
        {
            PolicyFor = _ => EgressPolicy.Default with { Action = EgressAction.Send },
            FindCredentials = text =>
                text.Contains("<CREDENTIAL_1>", StringComparison.Ordinal) && text.IndexOf("TAILSECRET", StringComparison.Ordinal) is var tail and >= 0
                    ? [new CredentialMatch(CredentialKind.LabelledValue, tail, "TAILSECRET".Length, 0, CredentialStrength.Labelled)]
                    : text.IndexOf("HEADSECRET", StringComparison.Ordinal) is var head and >= 0 ? [new CredentialMatch(CredentialKind.LabelledValue, head, "HEADSECRET".Length, 0, CredentialStrength.Labelled)] : [],
        });

        var prepared = await egress.PrepareAsync(Cloud, [new(ChatRole.User, "HEADSECRET TAILSECRET")], new ModelRequestContext("s1", false), Ct);

        Assert.Equal(EgressOutcome.Refused, prepared.Decision.Outcome);
        Assert.Contains("still in the request after redaction", prepared.Decision.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", prepared.Decision.Reason, StringComparison.Ordinal);
    }

    // Finding 3: a PII span that starts before a credential and overlaps it no longer leaves the credential's tail.
    [Fact]
    public async Task A_pii_span_overlapping_a_credential_is_redacted_as_one_value_and_restored_exactly()
    {
        const string text = "Clone https://alice:Tr0ub4dor&3xyz@git.example.com/repo first.";
        var egress = Egress(new FakePiiExtractor(("person", "alice:Tr0ub4dor")), EgressPolicy.Default with { Action = EgressAction.Send });

        var prepared = await egress.PrepareAsync(Cloud, [new(ChatRole.User, text)], new ModelRequestContext("s1", false), Ct);

        var sent = Sent(prepared.Messages);
        Assert.Equal(EgressOutcome.Redacted, prepared.Decision.Outcome);
        Assert.DoesNotContain("Tr0ub4dor", sent, StringComparison.Ordinal);
        Assert.DoesNotContain("3xyz", sent, StringComparison.Ordinal);
        Assert.Matches(@"^Clone https://<CREDENTIAL_\d+>@git\.example\.com/repo first\.$", sent);
        Assert.Equal(text, prepared.Map!.Restore(sent));
    }

    [Fact]
    public void Overlapping_values_are_replaced_by_their_union_and_a_contained_one_by_the_outer()
    {
        var map = new RedactionMap();
        map.PlaceholderFor("person", "alice:Tr0ub4dor");
        map.PlaceholderFor("credential", "Tr0ub4dor&3xyz");
        map.PlaceholderFor("person", "Bob Lee");
        map.PlaceholderFor("person", "Bob");

        var redacted = map.Redact("u=alice:Tr0ub4dor&3xyz@h, Bob Lee and Bob");

        Assert.Equal("u=<CREDENTIAL_2>@h, <PERSON_2> and <PERSON_3>", redacted);
        Assert.True(map.TryResolve("<CREDENTIAL_2>", out var union));
        Assert.Equal("alice:Tr0ub4dor&3xyz", union);
        Assert.Equal("u=alice:Tr0ub4dor&3xyz@h, Bob Lee and Bob", map.Restore(redacted));
    }

    [Fact]
    public void Placeholder_tags_and_redaction_prefer_the_longest_value()
    {
        Assert.Equal("ACCOUNT_NUMBER", RedactionMap.Tag("account number"));
        Assert.Equal("PHONE_NUMBER", RedactionMap.Tag(" phone-number "));
        Assert.Equal("PII_42", RedactionMap.Tag("42"));

        var map = new RedactionMap();
        map.PlaceholderFor("person", "Alice");
        map.PlaceholderFor("person", "Alice Smith");
        Assert.Equal("<PERSON_2> met <PERSON_1>.", map.Redact("Alice Smith met Alice."));
        Assert.Equal("Alice Smith met Alice. <PERSON_9>", map.Restore("<PERSON_2> met <PERSON_1>. <PERSON_9>"));
    }

    [Fact]
    public void Chunks_overlap_and_cover_the_text()
    {
        var egress = new PrivacyEgress(null, new PrivacyEgressOptions { ChunkChars = 100, ChunkOverlap = 20, MinChunkChars = 16 });
        var text = string.Concat(Enumerable.Range(0, 60).Select(i => $"w{i} "));

        var chunks = egress.Chunks(text).ToList();

        Assert.True(chunks.Count > 2);
        Assert.All(chunks, chunk => Assert.True(chunk.Length <= 100));
        Assert.StartsWith(chunks[0], text, StringComparison.Ordinal);
        Assert.EndsWith(chunks[^1], text, StringComparison.Ordinal);
        for (var i = 1; i < chunks.Count; i++)
            Assert.Contains(chunks[i][..10], chunks[i - 1], StringComparison.Ordinal);
    }
}

public sealed class PrivacyRetreatTests : IDisposable
{
    private const string Session = "s";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TempLog _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_refused_cloud_request_moves_the_slot_to_a_local_model_and_logs_why()
    {
        var local = Profiles.Chat(Profiles.Local("qwen3.5-0.8b"));
        var egress = new PrivacyEgress(new FakePiiExtractor(("person", "Alice Smith")),
            new PrivacyEgressOptions { PolicyFor = profile => profile.Privacy == PrivacyClass.Cloud ? EgressPolicy.Default with { Action = EgressAction.Refuse } : null });
        var cloudProvider = new ScriptedChatClient(_ => new ChatResponse(new ChatMessage(ChatRole.Assistant, "cloud")));
        var cloudProfile = Profiles.Cloud("claude");
        var cloud = new ModelBinding(cloudProfile, new PrivacyEgressChatClient(cloudProvider, cloudProfile, egress));
        var slots = new SessionModelSlots(new ModelSlotRegistry().Bind(ModelSlots.Generator, local, escalation: [cloud]), _db.Log, Session,
            new ModelSwitchOptions { Privacy = PrivacyGates.AllowAll });
        await slots.SwitchAsync(ModelSlots.Generator, cloud, SwitchReason.User, pin: true, cancellationToken: Ct);
        var client = slots.GetChatClient(ModelSlots.Generator);

        var answer = await client.GetResponseAsync([new(ChatRole.User, "what is 2+2")], cancellationToken: Ct);
        Assert.Equal("cloud", answer.Text);

        answer = await client.GetResponseAsync([new(ChatRole.User, "remind Alice Smith")], cancellationToken: Ct);

        Assert.Equal("qwen3.5-0.8b", answer.Text);
        Assert.Single(cloudProvider.Calls);
        Assert.Same(local, slots.Current(ModelSlots.Generator));
        var change = _db.Log.Read(Session, cancellationToken: Ct).Select(item => item.Payload).OfType<ModelSwitched>().Last();
        Assert.Equal(("privacy", "qwen3.5-0.8b"), (change.Reason, ModelProfile.FromSnapshot(change.NewProfile).Id));
    }

    [Fact]
    public async Task With_no_local_model_the_refusal_reaches_the_caller()
    {
        var egress = new PrivacyEgress(null);
        var cloudProfile = Profiles.Cloud("claude");
        var cloud = new ModelBinding(cloudProfile, new PrivacyEgressChatClient(
            new ScriptedChatClient(_ => throw new InvalidOperationException("must not be called")), cloudProfile, egress));
        var slots = new SessionModelSlots(new ModelSlotRegistry().Bind(ModelSlots.Generator, cloud), _db.Log, Session);

        await Assert.ThrowsAsync<PrivacyEgressRefusedException>(() =>
            slots.GetChatClient(ModelSlots.Generator).GetResponseAsync([new(ChatRole.User, "hi")], cancellationToken: Ct));
        Assert.Empty(_db.Log.Read(Session, cancellationToken: Ct));
    }

    [Fact]
    public async Task A_failing_local_server_and_a_refusing_cloud_fallback_end_the_request_instead_of_trading_it_back_and_forth()
    {
        var server = Profiles.Chat(Profiles.LocalServer("llama"));
        server.Client().FailWith([.. Enumerable.Range(0, 5).Select(_ => (Exception)Profiles.Status(System.Net.HttpStatusCode.ServiceUnavailable))]);
        var cloudProfile = Profiles.Cloud("claude");
        var cloudProvider = new ScriptedChatClient(_ => throw new InvalidOperationException("must not be called"));
        var cloud = new ModelBinding(cloudProfile, new PrivacyEgressChatClient(cloudProvider, cloudProfile, new PrivacyEgress(null)));
        var slots = new SessionModelSlots(new ModelSlotRegistry().Bind(ModelSlots.Generator, server, fallbacks: [cloud]), _db.Log, Session,
            new ModelSwitchOptions { Privacy = PrivacyGates.AllowAll });

        await Assert.ThrowsAsync<PrivacyEgressRefusedException>(() =>
            slots.GetChatClient(ModelSlots.Generator).GetResponseAsync([new(ChatRole.User, "hi")], cancellationToken: Ct));

        // The retreat does not go back to the server that just failed, so it is called once, and the cloud model never.
        Assert.Single(server.Client().Calls);
        Assert.Empty(cloudProvider.Calls);
    }
}
