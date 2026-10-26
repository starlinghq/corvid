using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Media;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Kernel.Events;
using Corvid.Kernel.Sessions;
using Corvid.Kernel.Turns;
using Corvid.Models.Images;
using Corvid.Tools.Builtin;

namespace Corvid.Acp;

/// <param name="StartRuntime">Starts the agent; called once, in the background, so <c>initialize</c> answers at once.</param>
/// <param name="Log">Standard error: the only place besides protocol messages anything may be written.</param>
public sealed record AcpServerOptions(Func<CancellationToken, Task<CorvidRuntime>> StartRuntime, SlashCommandRegistry Commands,
    Stream Input, Stream Output, TextWriter Log);

/// <summary>
/// <c>corvid acp</c> (plan §12, R11): Corvid as an Agent Client Protocol agent over stdio, for Zed, JetBrains and VS Code.
/// Sessions are Corvid sessions in the event log: new, load (replaying the conversation), resume, fork at a log position,
/// list and close. A prompt is one turn, streamed as <c>session/update</c> notifications with tool calls and file diffs;
/// approvals become <c>session/request_permission</c>; <c>session/cancel</c> cancels the turn.
/// </summary>
public static class AcpServer
{
    /// <summary>The ACP version spoken (schema <c>ProtocolVersion</c>).</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Serves one client until it closes its end; returns 0, or 1 when the agent could not start.</summary>
    public static async Task<int> RunAsync(AcpServerOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var connection = new JsonRpcConnection(options.Input, options.Output);
        await using (connection.ConfigureAwait(false))
        {
            var context = new AcpConnectionContext(connection, options.Log);
            var agent = new AcpAgent(context, options.Commands, options.StartRuntime, cancellationToken);
            try
            {
                await connection.RunAsync(agent, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException exception)
            {
                context.Log($"corvid acp: {exception.Message}");
            }
            finally
            {
                await agent.DisposeAsync().ConfigureAwait(false);
            }
        }
        return 0;
    }
}

/// <summary>What the agent's parts share: the connection, the runtime once it is up, and standard error.</summary>
internal sealed class AcpConnectionContext(JsonRpcConnection connection, TextWriter log)
{
    public JsonRpcConnection Connection { get; } = connection;

    public CorvidRuntime Runtime { get; set; } = null!;

    public ToolPresentation? Tools { get; set; }

    public void Update(string sessionId, SessionUpdate update) =>
        Connection.Notify(AcpMethods.SessionUpdate, new SessionNotification(sessionId, update), AcpJson.Default.SessionNotification);

    public void Log(string text)
    {
        lock (log)
        {
            log.WriteLine(text);
            log.Flush();
        }
    }
}

/// <summary>A session this connection has opened: its prompter, and the turn running in it.</summary>
internal sealed class AcpSessionState(CorvidSession session)
{
    private CancellationTokenSource _turn = new();
    private int _running;

    public CorvidSession Session { get; } = session;

    public IDisposable? Prompter { get; set; }

    public TurnReporter? Reporter { get; set; }

    /// <summary>Cancelled by <c>session/cancel</c>: open permission requests end with it.</summary>
    public CancellationToken TurnCancelled => Volatile.Read(ref _turn).Token;

    public bool TryBegin()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            return false;
        var previous = Interlocked.Exchange(ref _turn, new CancellationTokenSource());
        previous.Dispose();
        return true;
    }

    public void End()
    {
        Reporter = null;
        Volatile.Write(ref _running, 0);
    }

    /// <summary>
    /// The kernel's turn first, then the open permission requests: a request answered as cancelled lets the turn go on, and
    /// it must find itself cancelled rather than make its next model call.
    /// </summary>
    public void Cancel()
    {
        Session.Cancel();
        try
        {
            Volatile.Read(ref _turn).Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

internal sealed partial class AcpAgent : IJsonRpcHandler, IAsyncDisposable
{
    private const string Surface = "acp";
    private const int ListPage = 50;

    private readonly AcpConnectionContext _context;
    private readonly SlashCommandRegistry _commands;
    private readonly Task<CorvidRuntime> _runtime;
    private readonly ConcurrentDictionary<string, AcpSessionState> _sessions = new(StringComparer.Ordinal);

    public AcpAgent(AcpConnectionContext context, SlashCommandRegistry commands, Func<CancellationToken, Task<CorvidRuntime>> start,
        CancellationToken cancellationToken)
    {
        _context = context;
        _commands = commands;
        _runtime = StartAsync(start, cancellationToken);
    }

    public async Task<JsonRpcResult> HandleRequestAsync(string method, JsonElement parameters, CancellationToken cancellationToken) =>
        method switch
        {
            AcpMethods.Initialize => Result(Initialize(Read(parameters, AcpJson.Default.InitializeRequest)), AcpJson.Default.InitializeResponse),
            AcpMethods.SessionNew => await NewAsync(Read(parameters, AcpJson.Default.SessionRequest)).ConfigureAwait(false),
            AcpMethods.SessionLoad => await LoadAsync(Read(parameters, AcpJson.Default.SessionRequest), replay: true).ConfigureAwait(false),
            AcpMethods.SessionResume => await LoadAsync(Read(parameters, AcpJson.Default.SessionRequest), replay: false).ConfigureAwait(false),
            AcpMethods.SessionFork => await ForkAsync(Read(parameters, AcpJson.Default.SessionRequest), cancellationToken).ConfigureAwait(false),
            AcpMethods.SessionList => Result(await ListAsync(Read(parameters, AcpJson.Default.ListSessionsRequest)).ConfigureAwait(false),
                AcpJson.Default.ListSessionsResponse),
            AcpMethods.SessionClose => Result(Close(Read(parameters, AcpJson.Default.SessionIdRequest)), AcpJson.Default.EmptyResponse),
            // A5: the generator and the decider for this session (AcpServer.Models.cs).
            AcpConfig.SetConfigOption => await SetConfigOptionAsync(Read(parameters, AcpJson.Default.SetConfigOptionRequest), cancellationToken).ConfigureAwait(false),
            AcpMethods.SessionPrompt => Result(await PromptAsync(Read(parameters, AcpJson.Default.PromptRequest), cancellationToken).ConfigureAwait(false),
                AcpJson.Default.PromptResponse),
            _ => throw new AcpException(AcpErrors.MethodNotFound, $"Corvid does not implement '{method}'."),
        };

    public void HandleNotification(string method, JsonElement parameters)
    {
        if (method == AcpMethods.SessionCancel && Read(parameters, AcpJson.Default.SessionIdRequest) is { } cancel
            && _sessions.TryGetValue(cancel.SessionId, out var state))
            state.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Unwatch();
        foreach (var state in _sessions.Values)
        {
            state.Cancel();
            state.Prompter?.Dispose();
        }
        CorvidRuntime runtime;
        try
        {
            runtime = await _runtime.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return;
        }
        await runtime.MaintenanceIdleAsync().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        await runtime.DisposeAsync().ConfigureAwait(false);
        runtime.ChildChanged -= OnChild;
    }

    private void OnChild(string parentId, SessionStreamUpdate update)
    {
        // L5 (A5 review): a child is a tool call of the prompt turn that runs it; a session/update about a call outside any
        // prompt turn (a child a /delegate command started, which runs on after the command's answer) is not sent.
        if (_sessions.TryGetValue(parentId, out var state) && state.Reporter is not null && TurnReporter.Child(update) is { } notification)
            _context.Update(parentId, notification);
    }

    private async Task<CorvidRuntime> StartAsync(Func<CancellationToken, Task<CorvidRuntime>> start, CancellationToken cancellationToken)
    {
        try
        {
            var runtime = await Task.Run(() => start(cancellationToken), cancellationToken).ConfigureAwait(false);
            _context.Runtime = runtime;
            _context.Tools = new ToolPresentation(runtime);
            // A5: a child session of an open session is shown as a call of its own, started and finished.
            runtime.ChildChanged += OnChild;
            foreach (var notice in runtime.DeciderNotices)
                _context.Log("corvid acp: " + notice);
            return runtime;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _context.Log("corvid acp: Corvid could not start: " + exception.Message);
            throw;
        }
    }

    private async Task<CorvidRuntime> RuntimeAsync()
    {
        try
        {
            return await _runtime.ConfigureAwait(false);
        }
        catch (ModelsUnavailableException exception)
        {
            throw new AcpException(AcpErrors.InternalError, "Corvid cannot start: " + string.Join(" ", exception.Missing.Select(item =>
                item.Detail + (item.Acquire is { } remedy ? $" Run `{remedy}`." : ""))));
        }
        catch (Exception exception) when (exception is not (AcpException or OutOfMemoryException))
        {
            throw new AcpException(AcpErrors.InternalError, "Corvid cannot start: " + exception.Message);
        }
    }

    private static InitializeResponse Initialize(InitializeRequest request) =>
        // Version negotiation: the agent answers with the version it speaks; a client that cannot use it disconnects.
        new(AcpServer.ProtocolVersion,
            new AgentCapabilities(
                LoadSession: true,
                // K-8 (A6): text, resource links and images (a turn refuses them, with why, when no model can read them); audio
                // needs a model that hears, and embedded resources a size policy.
                new PromptCapabilities(Image: true, Audio: false, EmbeddedContext: false),
                // Corvid connects only its own configured MCP servers (C-018); a client's are not (and said so on standard error).
                new McpCapabilities(Http: false, Sse: false),
                new SessionCapabilities { List = new Supported(), Resume = new Supported(), Fork = new Supported(), Close = new Supported() }),
            new Implementation("corvid", Version()) { Title = "Corvid" },
            []);

    private async Task<JsonRpcResult> NewAsync(SessionRequest request)
    {
        var runtime = await RuntimeAsync().ConfigureAwait(false);
        CheckWorkspace(runtime, request);
        NoteMcpServers(request);
        var state = Open(runtime, runtime.OpenSession(surface: Surface));
        return Result(new NewSessionResponse(state.Session.Id) { ConfigOptions = ConfigOptions(runtime, state.Session) }, AcpJson.Default.NewSessionResponse,
            () => SendCommands(state.Session.Id));
    }

    private async Task<JsonRpcResult> LoadAsync(SessionRequest request, bool replay)
    {
        var runtime = await RuntimeAsync().ConfigureAwait(false);
        CheckWorkspace(runtime, request);
        var id = Required(request.SessionId);
        if (runtime.Log.GetHead(id) == 0)
            throw new AcpException(AcpErrors.ResourceNotFound, $"There is no session '{id}' in Corvid's log.");
        AcpSessionState state;
        try
        {
            state = _sessions.TryGetValue(id, out var open) ? open : Open(runtime, runtime.OpenSession(id, Surface));
        }
        catch (SessionHeldException exception)
        {
            // G-12: another process drives it (a terminal, the app); the editor may not post into it.
            throw new AcpException(AcpErrors.InvalidRequest, exception.Message);
        }
        // session/load replays the whole conversation before answering; session/resume must not.
        if (replay)
            await ReplayAsync(runtime, id).ConfigureAwait(false);
        return Result(new ConfigOptionsResponse { ConfigOptions = ConfigOptions(runtime, state.Session) }, AcpJson.Default.ConfigOptionsResponse,
            () => SendCommands(state.Session.Id));
    }

    private async Task<JsonRpcResult> ForkAsync(SessionRequest request, CancellationToken cancellationToken)
    {
        var runtime = await RuntimeAsync().ConfigureAwait(false);
        CheckWorkspace(runtime, request);
        var source = Required(request.SessionId);
        if (runtime.Log.GetHead(source) == 0)
            throw new AcpException(AcpErrors.ResourceNotFound, $"There is no session '{source}' in Corvid's log.");
        long? through = null;
        if (request.Meta is { ValueKind: JsonValueKind.Object } meta && meta.TryGetProperty("corvid", out var corvid))
            through = Read(corvid, AcpJson.Default.CorvidMeta)?.ThroughSequence;
        CorvidSession fork;
        try
        {
            fork = await runtime.ForkSessionAsync(source, through, Surface, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (SessionForkException exception)
        {
            throw new AcpException(AcpErrors.InvalidParams, exception.Message);
        }
        var state = Open(runtime, fork);
        return Result(new NewSessionResponse(fork.Id) { ConfigOptions = ConfigOptions(runtime, fork) }, AcpJson.Default.NewSessionResponse,
            () => SendCommands(state.Session.Id));
    }

    private async Task<ListSessionsResponse> ListAsync(ListSessionsRequest request)
    {
        var runtime = await RuntimeAsync().ConfigureAwait(false);
        var workspace = runtime.Builtin.Environment.Workspace;
        if (request.Cwd is { Length: > 0 } cwd && !SameDirectory(cwd, workspace))
            return new ListSessionsResponse([]);
        var offset = request.Cursor is { } cursor ? int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value : throw new AcpException(AcpErrors.InvalidParams, "The cursor is not one this agent gave.") : 0;
        // Conversations the user had (in the editor or the terminal), not scheduled runs or HTTP clients' sessions.
        var sessions = runtime.Log.ListSessions()
            .Where(id => id.StartsWith(Surface + "-", StringComparison.Ordinal) || id.StartsWith("cli-", StringComparison.Ordinal))
            .Select(id => Describe(runtime, id, workspace))
            .OfType<(SessionInfo Info, DateTimeOffset Updated)>()
            .OrderByDescending(item => item.Updated)
            .Select(item => item.Info)
            .ToList();
        var page = sessions.Skip(offset).Take(ListPage).ToArray();
        return new ListSessionsResponse(page)
        {
            NextCursor = offset + page.Length < sessions.Count ? (offset + page.Length).ToString(CultureInfo.InvariantCulture) : null,
        };
    }

    private EmptyResponse Close(SessionIdRequest request)
    {
        if (_sessions.TryRemove(request.SessionId, out var state))
        {
            state.Cancel();
            state.Prompter?.Dispose();
        }
        return new EmptyResponse();
    }

    private async Task<PromptResponse> PromptAsync(PromptRequest request, CancellationToken cancellationToken)
    {
        var runtime = await RuntimeAsync().ConfigureAwait(false);
        if (!_sessions.TryGetValue(request.SessionId, out var state))
            throw new AcpException(AcpErrors.ResourceNotFound, $"Session '{request.SessionId}' is not open; create, load or resume it first.");
        var (text, images) = PromptContent(request.Prompt);
        if (!state.TryBegin())
            throw new AcpException(AcpErrors.InvalidRequest, "A prompt is already running in this session; cancel it or wait for its answer.");
        // Cancelled by session/cancel (or $/cancel_request) for this prompt only: TryBegin made a fresh one.
        var cancelled = state.TurnCancelled;
        try
        {
            if (images.IsEmpty && SlashCommandRegistry.IsCommand(text))
            {
                var output = await _commands.ExecuteAsync(text, new SlashCommandContext(runtime, state.Session, _commands), cancellationToken)
                    .ConfigureAwait(false);
                _context.Update(state.Session.Id, new AgentMessageChunk(new TextContent(output.ToPlainText())));
                return new PromptResponse(cancelled.IsCancellationRequested ? StopReasons.Cancelled : StopReasons.EndTurn);
            }

            var reporter = new TurnReporter(_context, state.Session, _context.Tools!, runtime.Log.GetHead(state.Session.Id));
            state.Reporter = reporter;
            Delivery delivery;
            using (runtime.Feed.Subscribe(state.Session.Id, reporter))
            // $/cancel_request on the prompt cancels the turn as session/cancel does; the answer still arrives.
            using (cancellationToken.Register(state.Cancel))
                delivery = images.IsEmpty
                    ? await state.Session.SendAsync(text, _context.Connection.Closed).ConfigureAwait(false)
                    : await state.Session.SendAsync(text, images, _context.Connection.Closed).ConfigureAwait(false);
            await reporter.DrainAsync().ConfigureAwait(false);
            if (delivery.Error is { } error)
                throw new AcpException(AcpErrors.InternalError, "The turn failed: " + error.Message);
            var result = delivery.Result!;
            // Streamed text is already in the editor; anything else (a fixed message, an answer not streamed) is sent now.
            if (result.Text is { } answer && !string.Equals(answer.Trim(), reporter.Streamed.Trim(), StringComparison.Ordinal))
                _context.Update(state.Session.Id, new AgentMessageChunk(new TextContent(answer)));
            // A5: the question the turn ended on (K-6), structured, after its text; the person's next prompt answers it.
            if (result.Status == TurnStatus.Clarifying && result.Question is { } question)
                _context.Connection.Notify(AcpMethods.CorvidQuestion,
                    new CorvidQuestionNotification(state.Session.Id, question.Id, question.Text, [.. question.Options.IsDefault ? [] : question.Options]),
                    AcpJson.Default.CorvidQuestionNotification);
            // ACP: once the client has sent session/cancel, the prompt MUST answer "cancelled", even when the turn reached
            // its end before the kernel saw the cancel.
            if (cancelled.IsCancellationRequested)
                return new PromptResponse(StopReasons.Cancelled);
            return new PromptResponse(result.Status switch
            {
                TurnStatus.Cancelled => StopReasons.Cancelled,
                TurnStatus.StepBudgetExhausted => StopReasons.MaxTurnRequests,
                _ => StopReasons.EndTurn,
            });
        }
        finally
        {
            state.End();
        }
    }

    private AcpSessionState Open(CorvidRuntime runtime, CorvidSession session)
    {
        var state = _sessions.GetOrAdd(session.Id, _ => new AcpSessionState(session));
        state.Prompter ??= runtime.AttachPrompter(session.Id, new PermissionPrompter(_context, state));
        return state;
    }

    private void SendCommands(string sessionId) =>
        _context.Update(sessionId, new AvailableCommandsUpdate([.. _commands.Commands
            // Leaving is the editor's business; forking is session/fork's (G-15), which opens the fork for the editor.
            .Where(command => command.Name is not ("exit" or "quit" or "fork"))
            // A5: /agents and /delegate run when typed; the editor's list keeps its recorded set (they are the chat's and the app's).
            .Where(command => command.Name is not ("agents" or "delegate"))
            .Select(command => new AvailableCommand(command.Name, command.Summary)
            {
                Input = command.Usage.Contains(' ', StringComparison.Ordinal) ? new AvailableCommandInput(command.Usage) : null,
            })]));

    /// <summary>The conversation as it stands (compactions expanded, undone turns left out), as the client would have seen it.</summary>
    private async Task ReplayAsync(CorvidRuntime runtime, string sessionId)
    {
        // The host's replay (K-10), the conversation alone: session/load has always sent results as text, without diffs.
        var history = await SessionReplay.ReadAsync(runtime, sessionId, _context.Tools ?? new ToolPresentation(runtime), ReplayOptions.Transcript)
            .ConfigureAwait(false);
        foreach (var update in history.Updates)
        {
            if (TurnReporter.Replayed(update) is { } notification)
                _context.Update(sessionId, notification);
            // K-8 (A6): a message's images follow its text, as the image blocks they came as; one no longer stored is left out.
            if (update is PromptSent { Images.IsEmpty: false } prompt)
                foreach (var image in prompt.Images)
                    if (runtime.Media.Read(image.Digest) is { } bytes)
                        _context.Update(sessionId, new UserMessageChunk(new ImageContent(Convert.ToBase64String(bytes), image.MediaType)));
        }
    }

    private static (SessionInfo Info, DateTimeOffset Updated)? Describe(CorvidRuntime runtime, string id, string workspace)
    {
        // G-6: the title every list shows (a rename, the generated title, else the first message), from the host.
        if (SessionCatalogue.Describe(runtime.Log, id) is not { } session)
            return null;
        return (new SessionInfo(id, workspace)
        {
            Title = session.Title,
            UpdatedAt = session.LastActivity.ToString("O", CultureInfo.InvariantCulture),
        }, session.LastActivity);
    }

    /// <summary>
    /// The runtime's workspace is fixed when it starts: approvals, the sandbox and checkpoints protect that directory. A session
    /// for another directory would run tools somewhere the user did not mean, so it is refused rather than redirected.
    /// </summary>
    private static void CheckWorkspace(CorvidRuntime runtime, SessionRequest request)
    {
        if (!Path.IsPathFullyQualified(request.Cwd))
            throw new AcpException(AcpErrors.InvalidParams, $"cwd must be an absolute path, not '{request.Cwd}'.");
        var workspace = runtime.Builtin.Environment.Workspace;
        if (!SameDirectory(request.Cwd, workspace))
            throw new AcpException(AcpErrors.InvalidParams, $"Corvid's workspace is {workspace}, not {request.Cwd}. Start `corvid acp` in " +
                $"{request.Cwd}, or with --set Workspace={request.Cwd}.");
    }

    // Corvid connects only the MCP servers in its own configuration (C-018), under its sandbox and trust settings; a client's
    // servers would be commands and tokens chosen outside that, so they are not used, and the editor's log says so.
    private void NoteMcpServers(SessionRequest request)
    {
        if (request.McpServers is { Length: > 0 } servers)
            _context.Log($"corvid acp: {servers.Length} MCP server(s) from the client are not connected; Corvid uses only the servers in Corvid:Mcp:Servers.");
    }

    private static bool SameDirectory(string path, string workspace)
    {
        try
        {
            return Directory.Exists(path) && new Workspace(path).Root == new Workspace(workspace).Root;
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The prompt as one message and its images (K-8, A6): text as written, resource links as Markdown links, image blocks as
    /// their decoded bytes (base64 <c>data</c>; a <c>uri</c> alone is refused, since Corvid never fetches an image). The turn
    /// reads each image's format from its bytes, strips its metadata and stores it, or refuses the prompt with why.
    /// </summary>
    internal static (string Text, ImmutableArray<ImageInput> Images) PromptContent(JsonElement[] blocks)
    {
        var text = new StringBuilder();
        var images = ImmutableArray.CreateBuilder<ImageInput>();
        foreach (var block in blocks)
        {
            var type = block.ValueKind == JsonValueKind.Object && block.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String
                ? kind.GetString() : null;
            if (type == "image")
            {
                images.Add(Image(block, images.Count));
                continue;
            }
            var piece = type switch
            {
                "text" when String(block, "text") is { } value => value,
                "resource_link" when String(block, "uri") is { } uri => $"[{String(block, "name") ?? uri}]({uri})",
                _ => throw new AcpException(AcpErrors.InvalidParams,
                    $"Corvid accepts text, resource links and images in a prompt, not '{type ?? "an unknown block"}'."),
            };
            if (text.Length > 0)
                text.Append('\n');
            text.Append(piece);
        }
        if (string.IsNullOrWhiteSpace(text.ToString()) && images.Count == 0)
            throw new AcpException(AcpErrors.InvalidParams, "The prompt is empty.");
        return (text.ToString(), images.ToImmutable());
    }

    private static ImageInput Image(JsonElement block, int index)
    {
        if (index >= ImageFiles.MaxPerMessage)
            throw new AcpException(AcpErrors.InvalidParams, $"A prompt may carry at most {ImageFiles.MaxPerMessage} images.");
        if (String(block, "data") is not { Length: > 0 } data)
            throw new AcpException(AcpErrors.InvalidParams, "An image block needs its base64 data: Corvid never fetches an image by its uri.");
        // Base64 is 4/3 the size: a block past the limit is refused before it is decoded.
        if (data.Length > (ImageFiles.MaxBytes / 3 + 1) * 4)
            throw new AcpException(AcpErrors.InvalidParams, $"An image may be at most {ImageFiles.Size(ImageFiles.MaxBytes)}.");
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(data);
        }
        catch (FormatException)
        {
            throw new AcpException(AcpErrors.InvalidParams, "An image block's data is not base64.");
        }
        var uri = String(block, "uri");
        return new ImageInput(bytes, uri is { Length: > 0 } ? Path.GetFileName(uri) : null);
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string Required(string? sessionId) =>
        !string.IsNullOrWhiteSpace(sessionId) ? sessionId : throw new AcpException(AcpErrors.InvalidParams, "sessionId is required.");

    private static T Read<T>(JsonElement parameters, JsonTypeInfo<T> type)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new AcpException(AcpErrors.InvalidParams, "params must be an object.");
        try
        {
            return parameters.Deserialize(type) ?? throw new AcpException(AcpErrors.InvalidParams, "params must be an object.");
        }
        catch (JsonException exception)
        {
            throw new AcpException(AcpErrors.InvalidParams, "Invalid params: " + exception.Message);
        }
    }

    private static JsonRpcResult Result<T>(T value, JsonTypeInfo<T> type, Action? after = null) =>
        new(JsonSerializer.SerializeToElement(value, type), after);

    private static string Version() =>
        typeof(AcpServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
}
