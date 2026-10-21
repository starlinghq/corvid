using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Host.Telemetry;
using Corvid.Kernel.Sessions;
using Corvid.Kernel.Turns;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Corvid.Http;

/// <param name="Verifier">Checks bearer tokens; required when <see cref="ServeSettings.TokenSecret"/> is set.</param>
public sealed record OpenAIEndpointOptions(ServeSettings Settings, IBearerTokenVerifier? Verifier = null);

/// <summary>
/// <c>corvid serve</c> (plan §12): an OpenAI-compatible endpoint, so Open WebUI and other front ends talk to Corvid.
/// <c>/v1/chat/completions</c> (streamed as server-sent events or not) runs one Corvid turn per request in a session that
/// stands for the conversation; <c>/v1/models</c> lists <c>corvid</c>.
/// </summary>
/// <remarks>
/// Who may call (never tools for an unauthenticated remote caller): with a token configured every request must present it;
/// without one, only loopback callers are served and the address must be loopback (the validator refuses anything else).
/// A loopback endpoint also refuses a <c>Host</c> header that is not a loopback name, so a web page cannot reach it through
/// DNS rebinding, and accepts only <c>application/json</c> bodies, so a page cannot post a form or plain text to it without
/// a CORS preflight, which is never answered. Nobody can answer an approval over HTTP: served sessions are unattended, and a
/// call that needed one fails the request (<see cref="ServeApprovals.Fail"/>) or is denied while the model goes on.
/// </remarks>
public static class OpenAIEndpoint
{
    public const string ModelId = "corvid";
    public const string SessionHeader = "X-Corvid-Session";
    internal const string SessionPrefix = "http-";

    /// <summary>A web application serving the endpoint on <see cref="ServeSettings.Url"/>; start it with <c>StartAsync</c>.</summary>
    public static WebApplication Build(CorvidRuntime runtime, OpenAIEndpointOptions options)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);
        var settings = options.Settings;
        if (!string.IsNullOrWhiteSpace(settings.TokenSecret) && options.Verifier is null)
            throw new ArgumentException("A token secret is configured but no verifier was given.", nameof(options));
        var url = new Uri(settings.Url);
        var loopback = ServeSettings.IsLoopback(url);
        var address = url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback
            : IPAddress.TryParse(url.Host.Trim('[', ']'), out var parsed) ? parsed
            : throw new ArgumentException($"Corvid:Serve:Url must name an IP address or localhost, not '{url.Host}'.", nameof(options));

        // Slim: no configuration files, no ASPNETCORE_URLS taking over the address, no logging to stdout.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.Limits.MaxRequestBodySize = settings.MaxRequestBytes;
            kestrel.AddServerHeader = false;
            kestrel.Listen(address, url.Port, listen => listen.Protocols = HttpProtocols.Http1AndHttp2);
        });
        builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.TypeInfoResolverChain.Insert(0, OpenAIJson.Default));

        var app = builder.Build();
        var handler = new ChatHandler(runtime, settings);
        app.Use(async (context, next) =>
        {
            if (await Refusal(context, settings, options.Verifier, loopback).ConfigureAwait(false) is { } refusal)
            {
                await WriteError(context, refusal.Status, refusal.Message, refusal.Type).ConfigureAwait(false);
                return;
            }
            await next(context).ConfigureAwait(false);
        });
        app.MapGet("/v1/models", () => Results.Json(new ModelList("list", [new ModelEntry(ModelId, "model", 0, "corvid")]),
            OpenAIJson.Default.ModelList));
        app.MapPost("/v1/chat/completions", handler.HandleAsync);
        return app;
    }

    /// <summary>The address the endpoint listens on once started (the port is chosen when the URL gives 0).</summary>
    public static Uri ListeningOn(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses;
        return new Uri(addresses?.FirstOrDefault() ?? throw new InvalidOperationException("The endpoint is not listening."));
    }

    internal sealed record RefusalReason(int Status, string Message, string Type);

    internal static async Task<RefusalReason?> Refusal(HttpContext context, ServeSettings settings, IBearerTokenVerifier? verifier, bool loopback)
    {
        var request = context.Request;
        if (loopback && !IsLoopbackHost(request.Host.Host))
            return new RefusalReason(StatusCodes.Status421MisdirectedRequest,
                "This endpoint serves loopback host names only (localhost, 127.0.0.1, [::1]).", "invalid_request_error");
        if (verifier is not null)
        {
            var header = request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || header.Length <= 7)
                return new RefusalReason(StatusCodes.Status401Unauthorized, "Present the configured token as Authorization: Bearer.", "authentication_error");
            switch (await verifier.VerifyAsync(header[7..].Trim(), context.RequestAborted).ConfigureAwait(false))
            {
                case null:
                    return new RefusalReason(StatusCodes.Status503ServiceUnavailable,
                        "The token cannot be checked because Corvid Vault is not answering.", "service_unavailable");
                case false:
                    return new RefusalReason(StatusCodes.Status401Unauthorized, "The token is not valid.", "authentication_error");
            }
        }
        else if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
            return new RefusalReason(StatusCodes.Status403Forbidden, "Without a token only loopback callers are served.", "permission_error");
        if (HttpMethods.IsPost(request.Method)
            && !(request.ContentType is { } type && type.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase)))
            return new RefusalReason(StatusCodes.Status415UnsupportedMediaType, "Send the request as application/json.", "invalid_request_error");
        return null;
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);

    internal static Task WriteError(HttpContext context, int status, string message, string type, string? code = null)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(new ErrorEnvelope(new ErrorBody(message, type) { Code = code }), OpenAIJson.Default.ErrorEnvelope);
    }
}

/// <summary>One chat completion: the conversation's session, one turn, the answer as JSON or as server-sent events.</summary>
internal sealed class ChatHandler(CorvidRuntime runtime, ServeSettings settings)
{
    private const int RememberedConversations = 4096;

    private readonly ConversationMap _conversations = new(RememberedConversations);
    private readonly SessionGates _turns = new();
    private readonly SessionSettings _sessionSettings = new(Interactive: false, Tools: settings.Tools);

    public async Task HandleAsync(HttpContext context)
    {
        ChatCompletionRequest? request;
        try
        {
            request = await context.Request.ReadFromJsonAsync(OpenAIJson.Default.ChatCompletionRequest, context.RequestAborted).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            await OpenAIEndpoint.WriteError(context, 400, "The body is not a chat completion request: " + exception.Message, "invalid_request_error").ConfigureAwait(false);
            return;
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException exception)
        {
            await OpenAIEndpoint.WriteError(context, exception.StatusCode, exception.Message, "invalid_request_error").ConfigureAwait(false);
            return;
        }
        if (Invalid(request) is { } invalid)
        {
            await OpenAIEndpoint.WriteError(context, invalid.Status, invalid.Message, "invalid_request_error",
                invalid.Code).ConfigureAwait(false);
            return;
        }
        Conversation conversation;
        try
        {
            conversation = Conversation.Read(request!.Messages!);
        }
        catch (FormatException exception)
        {
            await OpenAIEndpoint.WriteError(context, 400, exception.Message, "invalid_request_error", "invalid_messages").ConfigureAwait(false);
            return;
        }

        var (session, text) = Open(context, conversation);
        if (session is null)
        {
            await OpenAIEndpoint.WriteError(context, 404, $"There is no served session '{context.Request.Headers[OpenAIEndpoint.SessionHeader]}'.",
                "invalid_request_error", "session_not_found").ConfigureAwait(false);
            return;
        }
        context.Response.Headers[OpenAIEndpoint.SessionHeader] = session.Id;

        // One request at a time per session, so each request's feed carries only its own turn.
        using (await _turns.EnterAsync(session.Id, context.RequestAborted).ConfigureAwait(false))
        {
            if (request!.Stream == true)
                await StreamAsync(context, session, conversation, text).ConfigureAwait(false);
            else
                await AnswerAsync(context, session, conversation, text).ConfigureAwait(false);
        }
    }

    private static (int Status, string Message, string? Code)? Invalid(ChatCompletionRequest? request)
    {
        if (request?.Messages is not { Length: > 0 })
            return (400, "messages must hold at least one message.", "invalid_messages");
        if (request.Model is { Length: > 0 } model && model != OpenAIEndpoint.ModelId)
            return (404, $"The model '{model}' does not exist; this endpoint serves '{OpenAIEndpoint.ModelId}'.", "model_not_found");
        // Corvid runs its own tools, under its own approvals; a client's function definitions would bypass both.
        if (Present(request.Tools) || Present(request.ToolChoice) || Present(request.Functions) || Present(request.FunctionCall))
            return (400, "Client-defined tools are not supported: Corvid runs its own tools under its own approvals.", "unsupported_parameter");
        if (request.N is > 1)
            return (400, "n must be 1.", "unsupported_parameter");
        return null;

        static bool Present(JsonElement? value) => value is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } element
            && !(element.ValueKind == JsonValueKind.Array && element.GetArrayLength() == 0)
            && !(element.ValueKind == JsonValueKind.String && element.GetString() == "none");
    }

    /// <summary>
    /// The session for the conversation: the one the client names (only a served session), or the one that answered this
    /// history before, or a new one started with the client's history. Returns the text to send.
    /// </summary>
    private (CorvidSession? Session, string Text) Open(HttpContext context, Conversation conversation)
    {
        if (context.Request.Headers.TryGetValue(OpenAIEndpoint.SessionHeader, out var named) && named.ToString() is { Length: > 0 } id)
        {
            // Only sessions this endpoint started: a terminal or editor session has a user who answers its approvals.
            if (!id.StartsWith(OpenAIEndpoint.SessionPrefix, StringComparison.Ordinal) || runtime.Log.GetHead(id) == 0)
                return (null, "");
            return (runtime.OpenSession(id, "http", _sessionSettings), conversation.Last);
        }
        if (conversation.History.Count > 0 && _conversations.TryGet(ConversationMap.Fingerprint(conversation.History), out var known))
            return (runtime.OpenSession(known, "http", _sessionSettings), conversation.Last);

        var session = runtime.OpenSession(surface: "http", settings: _sessionSettings);
        session.SeedHistory(conversation.History);
        // A client's system prompt is the user's instruction, carried as text, never as Corvid's own system prompt.
        var text = conversation.System is { Length: > 0 } system ? $"Instructions from the client:\n{system}\n\n{conversation.Last}" : conversation.Last;
        return (session, text);
    }

    private async Task AnswerAsync(HttpContext context, CorvidSession session, Conversation conversation, string text)
    {
        var watch = new TurnWatch(session, settings.Approvals, streamText: null);
        var outcome = await RunAsync(context, session, watch, text).ConfigureAwait(false);
        if (outcome.Error is { } error)
        {
            await OpenAIEndpoint.WriteError(context, error.Status, error.Message, error.Type, error.Code).ConfigureAwait(false);
            return;
        }
        Remember(session, conversation, outcome.Text);
        await context.Response.WriteAsJsonAsync(new ChatCompletion(CompletionId(), "chat.completion", Now(), OpenAIEndpoint.ModelId,
            [new Choice(0, new ChatMessage("assistant", outcome.Text), outcome.FinishReason)]), OpenAIJson.Default.ChatCompletion,
            contentType: null, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task StreamAsync(HttpContext context, CorvidSession session, Conversation conversation, string text)
    {
        var id = CompletionId();
        var created = Now();
        var response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        var events = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var watch = new TurnWatch(session, settings.Approvals, delta => events.Writer.TryWrite(delta));
        var turn = RunAsync(context, session, watch, text);
        _ = turn.ContinueWith(_ => events.Writer.TryComplete(), TaskScheduler.Default);

        await Send(new ChunkChoice(0, new Delta { Role = "assistant", Content = "" })).ConfigureAwait(false);
        await foreach (var delta in events.Reader.ReadAllAsync(context.RequestAborted).ConfigureAwait(false))
            await Send(new ChunkChoice(0, new Delta { Content = delta })).ConfigureAwait(false);
        var outcome = await turn.ConfigureAwait(false);
        if (outcome.Error is { } error)
        {
            // Headers are gone; the error travels as an event, as the API does mid-stream.
            await response.WriteAsync("data: " + JsonSerializer.Serialize(new ErrorEnvelope(new ErrorBody(error.Message, error.Type) { Code = error.Code }),
                OpenAIJson.Default.ErrorEnvelope) + "\n\n", context.RequestAborted).ConfigureAwait(false);
        }
        else
        {
            // Text the generator did not stream (a fixed message, an answer produced while tools were on offer) comes last.
            if (!string.Equals(outcome.Text.Trim(), watch.Streamed.Trim(), StringComparison.Ordinal))
                await Send(new ChunkChoice(0, new Delta { Content = (watch.StreamedAny ? "\n\n" : "") + outcome.Text })).ConfigureAwait(false);
            await Send(new ChunkChoice(0, new Delta()) { FinishReason = outcome.FinishReason }).ConfigureAwait(false);
            Remember(session, conversation, outcome.Text);
        }
        await response.WriteAsync("data: [DONE]\n\n", context.RequestAborted).ConfigureAwait(false);
        await response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);

        async Task Send(ChunkChoice choice)
        {
            var chunk = new ChatCompletionChunk(id, "chat.completion.chunk", created, OpenAIEndpoint.ModelId, [choice]);
            await response.WriteAsync("data: " + JsonSerializer.Serialize(chunk, OpenAIJson.Default.ChatCompletionChunk) + "\n\n", context.RequestAborted)
                .ConfigureAwait(false);
            await response.Body.FlushAsync(context.RequestAborted).ConfigureAwait(false);
        }
    }

    private sealed record Failure(int Status, string Message, string Type, string? Code);

    private sealed record Outcome(string Text, string FinishReason, Failure? Error);

    private async Task<Outcome> RunAsync(HttpContext context, CorvidSession session, TurnWatch watch, string text)
    {
        Delivery delivery;
        using (runtime.Feed.Subscribe(session.Id, watch))
        // A client that goes away cancels its turn; the session and its log stay.
        using (context.RequestAborted.Register(session.Cancel))
        {
            try
            {
                delivery = await session.SendAsync(text, CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return new Outcome("", "stop", new Failure(503, "Corvid is shutting down.", "server_error", null));
            }
        }
        if (watch.Denied is { } tool)
            return new Outcome("", "stop", new Failure(403,
                $"Corvid did not run '{tool}': it needs the user's approval (or is refused outright), and approvals cannot be given over " +
                "HTTP, so the turn stopped. " +
                $"Continue in a terminal (`corvid chat --session {session.Id}`) or an editor to approve it, or set Corvid:Serve:Approvals=Deny " +
                "to let the model answer without it.", "approval_required", "approval_required"));
        if (delivery.Error is { } error)
            return new Outcome("", "stop", new Failure(500, "The turn failed: " + error.Message, "server_error", null));
        var result = delivery.Result!;
        if (result.Status == TurnStatus.Cancelled)
            return new Outcome("", "stop", new Failure(499, "The turn was cancelled.", "cancelled", null));
        return new Outcome(result.Text ?? "", result.Status == TurnStatus.StepBudgetExhausted ? "length" : "stop", null);
    }

    // The client will send this answer back as the last assistant message; the history up to it names this session.
    private void Remember(CorvidSession session, Conversation conversation, string answer) =>
        _conversations.Set(ConversationMap.Fingerprint([.. conversation.History, (true, conversation.Last), (false, answer)]), session.Id);

    private static string CompletionId() => "chatcmpl-" + Guid.NewGuid().ToString("N");

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();
}

/// <summary>
/// What a request watches of its turn: streamed text, and (under <see cref="ServeApprovals.Fail"/>) the first call denied for
/// want of an approval, which stops the turn so nothing after it runs and the request fails saying why.
/// </summary>
internal sealed class TurnWatch(CorvidSession session, ServeApprovals approvals, Action<string>? streamText) : ITurnObserver
{
    private readonly Lock _gate = new();
    private readonly StringBuilder _generation = new();
    private bool _any;

    public string? Denied { get; private set; }

    public string Streamed
    {
        get
        {
            lock (_gate)
                return _generation.ToString();
        }
    }

    public void GenerationStarted()
    {
        lock (_gate)
        {
            // A new generation after streamed narration: keep the two apart in the client's text.
            if (_generation.Length > 0 && streamText is not null)
                streamText("\n\n");
            _generation.Clear();
        }
    }

    public void Text(string delta)
    {
        if (streamText is null)
            return;
        lock (_gate)
        {
            _any = true;
            _generation.Append(delta);
            streamText(delta);
        }
    }

    public void ToolFinished(string callId, string tool, string outcome, TimeSpan elapsed)
    {
        if (outcome != "denied" || approvals != ServeApprovals.Fail)
            return;
        lock (_gate)
            Denied ??= tool;
        session.Cancel();
    }

    public bool StreamedAny => _any;
}

/// <summary>The client's messages: its system text, the history before the last message, and the last (user) message.</summary>
internal sealed record Conversation(string? System, IReadOnlyList<(bool FromUser, string Text)> History, string Last)
{
    public static Conversation Read(JsonElement[] messages)
    {
        var system = new List<string>();
        var history = new List<(bool, string)>();
        for (var i = 0; i < messages.Length; i++)
        {
            var message = messages[i];
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("role", out var roleElement) || roleElement.ValueKind != JsonValueKind.String)
                throw new FormatException($"messages[{i}] needs a role.");
            var role = roleElement.GetString();
            var text = Content(message, i);
            switch (role)
            {
                case "system" or "developer":
                    if (text.Length > 0)
                        system.Add(text);
                    break;
                case "user":
                    history.Add((true, text));
                    break;
                case "assistant":
                    // An assistant turn that only called the client's tools has no text, and Corvid never offered those tools.
                    if (text.Length > 0)
                        history.Add((false, text));
                    break;
                case "tool" or "function":
                    throw new FormatException("Tool results are not accepted: Corvid runs its own tools.");
                default:
                    throw new FormatException($"messages[{i}] has an unknown role '{role}'.");
            }
        }
        if (history.Count == 0 || !history[^1].Item1)
            throw new FormatException("The last message must be from the user.");
        var last = history[^1].Item2;
        if (string.IsNullOrWhiteSpace(last))
            throw new FormatException("The last message is empty.");
        history.RemoveAt(history.Count - 1);
        return new Conversation(system.Count > 0 ? string.Join("\n\n", system) : null, history, last);
    }

    private static string Content(JsonElement message, int index)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind == JsonValueKind.Null)
            return "";
        if (content.ValueKind == JsonValueKind.String)
            return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array)
            throw new FormatException($"messages[{index}].content must be text or a list of text parts.");
        var text = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object || !part.TryGetProperty("type", out var type) || type.GetString() != "text"
                || !part.TryGetProperty("text", out var value) || value.ValueKind != JsonValueKind.String)
                throw new FormatException($"messages[{index}].content: only text parts are accepted.");
            if (text.Length > 0)
                text.Append('\n');
            text.Append(value.GetString());
        }
        return text.ToString();
    }
}

/// <summary>
/// Remembers which session answered a conversation, by a digest of its user and assistant messages, so a client that sends
/// the whole history each time keeps talking to one session. Bounded and in memory: after a restart a known conversation
/// starts a new session seeded with its history.
/// </summary>
internal sealed class ConversationMap(int capacity)
{
    private readonly ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();

    public bool TryGet(string fingerprint, out string sessionId) => _sessions.TryGetValue(fingerprint, out sessionId!);

    public void Set(string fingerprint, string sessionId)
    {
        if (_sessions.TryAdd(fingerprint, sessionId))
            _order.Enqueue(fingerprint);
        else
            _sessions[fingerprint] = sessionId;
        while (_sessions.Count > capacity && _order.TryDequeue(out var oldest))
            _sessions.TryRemove(oldest, out _);
    }

    public static string Fingerprint(IEnumerable<(bool FromUser, string Text)> messages)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (fromUser, text) in messages)
        {
            hash.AppendData(Encoding.UTF8.GetBytes((fromUser ? "user" : "assistant") + "\0" + text.Trim() + "\u0001"));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
