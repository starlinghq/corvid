using System.Net;
using System.Text.Json.Nodes;
using Corvid.Abstractions.Events;
using Corvid.Host.Tests;
using Corvid.Kernel.Events;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.AI;

namespace Corvid.Http.Tests;

public sealed class OpenAIEndpointTests
{
    // The built-in tools with an embedder that ranks write_file first for "write ..." requests, so the shortlist offers it.
    private static readonly Dictionary<string, string?> FileTools = new()
    {
        ["Corvid:Tools:Builtin"] = "true",
        ["Corvid:Models:Slots:embedder.text:Model"] = "file-embed",
    };

    private static FakeModels WithFileEmbedder(FakeModels models)
    {
        models.Provider.Add("file-embed", [Corvid.Models.ModelSlots.TextEmbedder], () => new KeywordEmbedder("write", "save"), context: 512,
            tools: Corvid.Models.ToolCallSupport.None);
        return models;
    }

    private static FakeModels WritesAFile() =>
        WithFileEmbedder(new FakeModels(new FakeGenerator(Reply.Call("write_file", """{"path":"notes.txt","content":"hello\n"}"""),
            Reply.Say("I could not write it."))));

    [Fact]
    public async Task ModelsListsCorvid()
    {
        await using var served = await Served.StartAsync(TestHost.Create());

        var response = await served.Client.GetAsync("/v1/models", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Served.BodyAsync(response);
        Assert.Equal("list", body["object"]!.GetValue<string>());
        var model = Assert.Single(body["data"]!.AsArray())!;
        Assert.Equal("corvid", model["id"]!.GetValue<string>());
        Assert.Equal("model", model["object"]!.GetValue<string>());
        Assert.Equal("corvid", model["owned_by"]!.GetValue<string>());
    }

    [Fact]
    public async Task ACompletionRunsOneTurnInANewSession()
    {
        await using var served = await Served.StartAsync(TestHost.Create());

        var response = await served.PostAsync(Served.Chat(("user", "hello")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Served.BodyAsync(response);
        Assert.Equal("chat.completion", body["object"]!.GetValue<string>());
        Assert.StartsWith("chatcmpl-", body["id"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("corvid", body["model"]!.GetValue<string>());
        var choice = Assert.Single(body["choices"]!.AsArray())!;
        Assert.Equal("assistant", choice["message"]!["role"]!.GetValue<string>());
        Assert.Equal("Hello from the fake generator.", choice["message"]!["content"]!.GetValue<string>());
        Assert.Equal("stop", choice["finish_reason"]!.GetValue<string>());

        var session = Assert.Single(response.Headers.GetValues(OpenAIEndpoint.SessionHeader));
        Assert.StartsWith("http-", session, StringComparison.Ordinal);
        var transcript = SessionProjection.Load(served.Runtime.Log, session, cancellationToken: TestContext.Current.CancellationToken).Transcript;
        Assert.Equal(["hello", "Hello from the fake generator."], transcript.Select(entry => entry.Content switch
        {
            UserMessage user => user.Text,
            AssistantMessage assistant => assistant.Text,
            _ => null,
        }).OfType<string>());
    }

    [Fact]
    public async Task AStreamedCompletionSendsChunksThenDone()
    {
        await using var served = await Served.StartAsync(TestHost.Create());

        var response = await served.PostAsync(Served.ChatStream(("user", "hello")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        var events = await Served.EventsAsync(response);
        Assert.Equal("[DONE]", events[^1]);
        var chunks = events[..^1].Select(item => JsonNode.Parse(item)!.AsObject()).ToList();
        Assert.All(chunks, chunk => Assert.Equal("chat.completion.chunk", chunk["object"]!.GetValue<string>()));
        Assert.Single(chunks.Select(chunk => chunk["id"]!.GetValue<string>()).Distinct());
        // The first chunk carries the role, the last the finish reason, and finish_reason is present (null) before it.
        Assert.Equal("assistant", chunks[0]["choices"]![0]!["delta"]!["role"]!.GetValue<string>());
        Assert.Equal("stop", chunks[^1]["choices"]![0]!["finish_reason"]!.GetValue<string>());
        Assert.All(chunks[..^1], chunk => Assert.True(chunk["choices"]![0]!.AsObject().ContainsKey("finish_reason")));
        var text = string.Concat(chunks.Select(chunk => chunk["choices"]![0]!["delta"]!["content"]?.GetValue<string>()));
        Assert.Equal("Hello from the fake generator.", text);
        // Streamed word by word as the generator produced it, never sent again whole.
        Assert.True(chunks.Count(chunk => chunk["choices"]![0]!["delta"]!["content"]?.GetValue<string>() is { Length: > 0 }) > 1);
    }

    [Fact]
    public async Task AClientThatResendsTheHistoryStaysInOneSession()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("First answer."), Reply.Say("Second answer.")));
        await using var served = await Served.StartAsync(TestHost.Create(models));

        var first = await served.PostAsync(Served.Chat(("system", "Be brief."), ("user", "one")));
        var session = Assert.Single(first.Headers.GetValues(OpenAIEndpoint.SessionHeader));
        var second = await served.PostAsync(Served.Chat(("system", "Be brief."), ("user", "one"), ("assistant", "First answer."), ("user", "two")));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(session, Assert.Single(second.Headers.GetValues(OpenAIEndpoint.SessionHeader)));
        Assert.Equal("Second answer.", (await Served.BodyAsync(second))["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        var users = SessionProjection.Load(served.Runtime.Log, session, cancellationToken: TestContext.Current.CancellationToken).Transcript
            .Select(entry => entry.Content).OfType<UserMessage>().Select(user => user.Text).ToList();
        // The client's system text arrives with the first message as the user's instruction, not as Corvid's system prompt.
        Assert.Equal(2, users.Count);
        Assert.Contains("Be brief.", users[0], StringComparison.Ordinal);
        Assert.EndsWith("one", users[0], StringComparison.Ordinal);
        Assert.Equal("two", users[1]);
        Assert.DoesNotContain("Be brief.", models.Generator.Calls[0][0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSessionHeaderContinuesAServedSession()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("First answer."), Reply.Say("Second answer.")));
        await using var served = await Served.StartAsync(TestHost.Create(models));
        var first = await served.PostAsync(Served.Chat(("user", "one")));
        var session = Assert.Single(first.Headers.GetValues(OpenAIEndpoint.SessionHeader));

        // Only the new message is sent: the session already holds the conversation.
        var second = await served.PostAsync(Served.Chat(("user", "two")), session);

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(session, Assert.Single(second.Headers.GetValues(OpenAIEndpoint.SessionHeader)));
        var last = models.Generator.Calls[^1];
        Assert.Contains(last, message => message.Role == ChatRole.User && message.Text == "one");
        Assert.Contains(last, message => message.Role == ChatRole.Assistant && message.Text == "First answer.");
    }

    [Fact]
    public async Task AHistoryFromElsewhereSeedsANewSession()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("Third answer.")));
        await using var served = await Served.StartAsync(TestHost.Create(models));

        var response = await served.PostAsync(Served.Chat(("user", "one"), ("assistant", "an answer from another server"), ("user", "two")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(models.Generator.Calls);
        Assert.Contains(call, message => message.Role == ChatRole.User && message.Text == "one");
        Assert.Contains(call, message => message.Role == ChatRole.Assistant && message.Text == "an answer from another server");
        Assert.Equal("two", call.Last(message => message.Role == ChatRole.User).Text);
    }

    [Theory]
    // A terminal or editor session has a user who answers its approvals: HTTP may not drive it.
    [InlineData("cli-20261020-120000-abcdef")]
    [InlineData("acp-20261020-120000-abcdef")]
    [InlineData("http-20261020-120000-abcdef")]
    public async Task OnlyAnExistingServedSessionCanBeNamed(string session)
    {
        await using var served = await Served.StartAsync(TestHost.Create());
        var other = served.Runtime.OpenSession(session.Split('-')[0] == "http" ? null : session, session.Split('-')[0]);
        if (session.StartsWith("http-", StringComparison.Ordinal))
            Assert.NotEqual(session, other.Id);
        else
            await other.SendAsync("hello", TestContext.Current.CancellationToken);

        var response = await served.PostAsync(Served.Chat(("user", "take over")), session);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("session_not_found", (await Served.BodyAsync(response))["error"]!["code"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"model":"gpt-4o","messages":[{"role":"user","content":"hi"}]}""", 404, "model_not_found")]
    [InlineData("""{"messages":[]}""", 400, "invalid_messages")]
    [InlineData("""{"messages":[{"role":"user","content":"hi"}],"tools":[{"type":"function","function":{"name":"x"}}]}""", 400, "unsupported_parameter")]
    [InlineData("""{"messages":[{"role":"user","content":"hi"}],"functions":[{"name":"x"}]}""", 400, "unsupported_parameter")]
    [InlineData("""{"messages":[{"role":"user","content":"hi"}],"n":2}""", 400, "unsupported_parameter")]
    [InlineData("""{"messages":[{"role":"user","content":"hi"},{"role":"tool","content":"42","tool_call_id":"a"}]}""", 400, "invalid_messages")]
    [InlineData("""{"messages":[{"role":"user","content":"hi"},{"role":"assistant","content":"hello"}]}""", 400, "invalid_messages")]
    [InlineData("""{"messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"http://x"}}]}]}""", 400, "invalid_messages")]
    [InlineData("""{"messages":[{"role":"wizard","content":"hi"}]}""", 400, "invalid_messages")]
    [InlineData("""{"messages":""", 400, null)]
    public async Task InvalidRequestsGetOpenAIErrors(string body, int status, string? code)
    {
        await using var served = await Served.StartAsync(TestHost.Create());

        var response = await served.Client.PostAsync("/v1/chat/completions",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(status, (int)response.StatusCode);
        var error = (await Served.BodyAsync(response))["error"]!;
        Assert.Equal("invalid_request_error", error["type"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(error["message"]!.GetValue<string>()));
        if (code is not null)
            Assert.Equal(code, error["code"]!.GetValue<string>());
        // Nothing reached the model.
        Assert.Empty(served.Host.Models.Generator.Calls);
    }

    [Fact]
    public async Task TextPartsAndAnEmptyToolListAreAccepted()
    {
        await using var served = await Served.StartAsync(TestHost.Create());

        var response = await served.Client.PostAsync("/v1/chat/completions", new StringContent(
            """{"model":"corvid","tools":[],"tool_choice":"none","temperature":0.2,"messages":[{"role":"user","content":[{"type":"text","text":"hello"},{"type":"text","text":"there"}]}]}""",
            System.Text.Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("hello\nthere", served.Host.Models.Generator.Calls[0].Last(message => message.Role == ChatRole.User).Text);
    }

    [Fact]
    public async Task ABodyThatIsNotJsonIsRefusedBeforeItIsRead()
    {
        await using var served = await Served.StartAsync(TestHost.Create());

        // A web page can post a form or plain text without a CORS preflight; JSON it cannot.
        var response = await served.Client.PostAsync("/v1/chat/completions",
            new StringContent("""{"messages":[{"role":"user","content":"hi"}]}""", System.Text.Encoding.UTF8, "text/plain"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Empty(served.Host.Models.Generator.Calls);
    }

    [Theory]
    [InlineData("attacker.example")]
    [InlineData("corvid.local:8411")]
    public async Task ALoopbackEndpointRefusesOtherHostNames(string host)
    {
        await using var served = await Served.StartAsync(TestHost.Create());
        // DNS rebinding: a page on another name that resolves to 127.0.0.1 sends its own Host header.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        request.Headers.Host = host;

        var response = await served.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.MisdirectedRequest, response.StatusCode);
    }

    [Theory]
    [InlineData(true, null, HttpStatusCode.OK)]
    [InlineData(false, "a-token-that-is-long-enough-to-check", HttpStatusCode.Unauthorized)]
    [InlineData(null, "a-token-that-is-long-enough-to-check", HttpStatusCode.ServiceUnavailable)]
    public async Task WithATokenEveryRequestPresentsIt(bool? verdict, string? _, HttpStatusCode expected)
    {
        var verifier = new FixedVerifier(verdict);
        await using var served = await Served.StartAsync(TestHost.Create(), settings => settings.TokenSecret = "serve.token", verifier);

        var missing = await served.PostAsync(Served.Chat(("user", "hello")));
        var presented = await served.PostAsync(Served.Chat(("user", "hello")), token: "a-token-that-is-long-enough-to-check");

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal("authentication_error", (await Served.BodyAsync(missing))["error"]!["type"]!.GetValue<string>());
        Assert.Equal(expected, presented.StatusCode);
        Assert.Equal(["a-token-that-is-long-enough-to-check"], verifier.Seen);
        Assert.Equal(verdict == true ? 1 : 0, served.Host.Models.Generator.Calls.Count);
    }

    [Fact]
    public async Task WithoutATokenARemoteCallerIsRefused()
    {
        // Real loopback sockets cannot show a remote peer, so the check is run on a request as Kestrel would hand it over.
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Host = new HostString("127.0.0.1", 8411);
        context.Request.ContentType = "application/json";
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.20");

        var refusal = await OpenAIEndpoint.Refusal(context, new Corvid.Host.Configuration.ServeSettings(), verifier: null, loopback: true);
        Assert.Equal(StatusCodes.Status403Forbidden, refusal?.Status);

        context.Connection.RemoteIpAddress = IPAddress.IPv6Loopback;
        Assert.Null(await OpenAIEndpoint.Refusal(context, new Corvid.Host.Configuration.ServeSettings(), verifier: null, loopback: true));
        // With a token, the address does not matter; the token does.
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.20");
        context.Request.Host = new HostString("corvid.lan", 8411);
        Assert.Equal(StatusCodes.Status401Unauthorized,
            (await OpenAIEndpoint.Refusal(context, new Corvid.Host.Configuration.ServeSettings(), new FixedVerifier(true), loopback: false))?.Status);
        context.Request.Headers.Authorization = "Bearer a-token-that-is-long-enough-to-check";
        Assert.Null(await OpenAIEndpoint.Refusal(context, new Corvid.Host.Configuration.ServeSettings(), new FixedVerifier(true), loopback: false));
    }

    [Fact]
    public async Task ACallThatNeedsApprovalFailsTheRequestByDefault()
    {
        await using var served = await Served.StartAsync(TestHost.Create(WritesAFile(), FileTools));

        var response = await served.PostAsync(Served.Chat(("user", "write hello to the file notes.txt")));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = (await Served.BodyAsync(response))["error"]!;
        Assert.Equal("approval_required", error["code"]!.GetValue<string>());
        Assert.Contains("write_file", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("corvid chat --session http-", error["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(served.Workspace, "notes.txt")));
        // Nobody was asked: a served session is not interactive.
        var session = Assert.Single(response.Headers.GetValues(OpenAIEndpoint.SessionHeader));
        var events = served.Runtime.Log.Read(session, 0, limit: 1000, cancellationToken: TestContext.Current.CancellationToken).Select(item => item.Payload).ToList();
        Assert.DoesNotContain(events, payload => payload is ApprovalRequested);
        Assert.Contains(events, payload => payload is ToolResult { IsError: true });
    }

    [Fact]
    public async Task AStreamedRequestReportsTheApprovalErrorAsAnEvent()
    {
        await using var served = await Served.StartAsync(TestHost.Create(WritesAFile(), FileTools));

        var response = await served.PostAsync(Served.ChatStream(("user", "write hello to the file notes.txt")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var events = await Served.EventsAsync(response);
        Assert.Equal("[DONE]", events[^1]);
        var error = JsonNode.Parse(events[^2])!["error"]!;
        Assert.Equal("approval_required", error["code"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(served.Workspace, "notes.txt")));
    }

    [Fact]
    public async Task UnderDenyTheModelAnswersWithoutTheCall()
    {
        await using var served = await Served.StartAsync(TestHost.Create(WritesAFile(), FileTools),
            settings => settings.Approvals = Corvid.Host.Configuration.ServeApprovals.Deny);

        var response = await served.PostAsync(Served.Chat(("user", "write hello to the file notes.txt")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("I could not write it.", (await Served.BodyAsync(response))["choices"]![0]!["message"]!["content"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(served.Workspace, "notes.txt")));
    }

    [Fact]
    public async Task WithToolsOffTheModelIsOfferedNone()
    {
        var models = WithFileEmbedder(new FakeModels(new FakeGenerator(Reply.Say("Just talking."))));
        await using var served = await Served.StartAsync(TestHost.Create(models, FileTools), settings => settings.Tools = false);

        var response = await served.PostAsync(Served.Chat(("user", "write hello to the file notes.txt")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(models.Generator.Calls);
        Assert.Empty(Assert.Single(models.Generator.OfferedTools));
        Assert.False(File.Exists(Path.Combine(served.Workspace, "notes.txt")));
    }
}
