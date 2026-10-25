using System.Text.Json;
using System.Text.Json.Nodes;
using Corvid.Abstractions.Events;
using Corvid.Host.Tests;

namespace Corvid.Acp.Tests;

public sealed class AcpTests
{
    private static readonly Dictionary<string, string?> BuiltinOn = new() { ["Corvid:Tools:Builtin"] = "true" };

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

    private static object Prompt(string sessionId, string text) => new { sessionId, prompt = new object[] { new { type = "text", text } } };

    private static object Initialize() => new { protocolVersion = 1, clientCapabilities = new { fs = new { readTextFile = false } }, clientInfo = new { name = "test", version = "1" } };

    [Fact]
    public async Task InitializeDescribesTheAgent()
    {
        await using var client = AcpClient.Start(TestHost.Create());
        var response = await client.RequestAsync("initialize", Initialize());

        var result = response["result"]!;
        Assert.Equal(1, result["protocolVersion"]!.GetValue<int>());
        var capabilities = result["agentCapabilities"]!;
        Assert.True(capabilities["loadSession"]!.GetValue<bool>());
        Assert.False(capabilities["promptCapabilities"]!["image"]!.GetValue<bool>());
        Assert.False(capabilities["promptCapabilities"]!["embeddedContext"]!.GetValue<bool>());
        foreach (var name in new[] { "list", "resume", "fork", "close" })
            Assert.NotNull(capabilities["sessionCapabilities"]![name]);
        Assert.Equal("corvid", result["agentInfo"]!["name"]!.GetValue<string>());
        Assert.Empty(result["authMethods"]!.AsArray());
    }

    [Fact]
    public async Task APromptStreamsItsAnswerAsRecorded()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("Hello from the fake generator.")));
        await using var client = AcpClient.Start(TestHost.Create(models));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();

        var response = await client.RequestAsync("session/prompt", Prompt(session, "hello"));

        Assert.Equal("end_turn", response["result"]!["stopReason"]!.GetValue<string>());
        var text = string.Concat(client.UpdatesFor(session).Where(update => update["sessionUpdate"]!.GetValue<string>() == "agent_message_chunk")
            .Select(update => update["content"]!["text"]!.GetValue<string>()));
        Assert.Equal("Hello from the fake generator.", text);
        // Streamed word by word, and never sent again whole.
        Assert.True(client.UpdatesFor(session).Count(update => update["sessionUpdate"]!.GetValue<string>() == "agent_message_chunk") > 1);
        Exchanges.Verify("prompt-streaming", client);
    }

    [Fact]
    public async Task SessionNewOffersTheSlashCommandsAndRunsThem()
    {
        await using var client = AcpClient.Start(TestHost.Create());
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();
        await WaitForAsync(() => client.UpdatesFor(session).Any(update => update["sessionUpdate"]!.GetValue<string>() == "available_commands_update"));
        var commands = client.UpdatesFor(session).First(update => update["sessionUpdate"]!.GetValue<string>() == "available_commands_update")["availableCommands"]!
            .AsArray().Select(command => command!["name"]!.GetValue<string>()).ToList();
        Assert.Contains("status", commands);
        Assert.Contains("undo", commands);
        Assert.DoesNotContain("exit", commands);

        var response = await client.RequestAsync("session/prompt", Prompt(session, "/goal ship the release"));
        Assert.Equal("end_turn", response["result"]!["stopReason"]!.GetValue<string>());
        var runtime = await client.Runtime;
        Assert.Equal("ship the release", Corvid.Kernel.Events.SessionProjection.Load(runtime.Log, session, cancellationToken: TestContext.Current.CancellationToken).Goal);
    }

    [Fact]
    public async Task AFileEditAsksPermissionWithItsDiffAndReportsTheChange()
    {
        var models = WithFileEmbedder(new FakeModels(new FakeGenerator(Reply.Call("write_file", """{"path":"notes.txt","content":"hello\n"}"""), Reply.Say("Wrote it."))));
        await using var client = AcpClient.Start(TestHost.Create(models, FileTools));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();
        var file = Path.Combine(client.Workspace, "notes.txt");

        var response = await client.RequestAsync("session/prompt", Prompt(session, "write hello to the file notes.txt"));

        Assert.Equal("end_turn", response["result"]!["stopReason"]!.GetValue<string>());
        Assert.Equal("hello\n", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
        var request = Assert.Single(client.PermissionRequests)["params"]!;
        Assert.Equal(session, request["sessionId"]!.GetValue<string>());
        var options = request["options"]!.AsArray().Select(option => (option!["optionId"]!.GetValue<string>(), option["kind"]!.GetValue<string>())).ToList();
        Assert.Contains(("allow-once", "allow_once"), options);
        Assert.Contains(("deny", "reject_once"), options);
        var call = request["toolCall"]!;
        var callId = call["toolCallId"]!.GetValue<string>();
        var proposed = call["content"]!.AsArray().Single(item => item!["type"]!.GetValue<string>() == "diff")!;
        Assert.Equal(file, proposed["path"]!.GetValue<string>());
        Assert.Null(proposed["oldText"]);
        Assert.Equal("hello\n", proposed["newText"]!.GetValue<string>());
        Assert.Equal("notes.txt", call["rawInput"]!["path"]!.GetValue<string>());

        var updates = client.UpdatesFor(session);
        // The call is announced before the permission request names it, and finished after it, with the ledger's diff.
        var started = updates.First(update => update["sessionUpdate"]!.GetValue<string>() == "tool_call");
        Assert.Equal(callId, started["toolCallId"]!.GetValue<string>());
        Assert.Equal("edit", started["kind"]!.GetValue<string>());
        Assert.Equal("pending", started["status"]!.GetValue<string>());
        var finished = updates.Last(update => update["sessionUpdate"]!.GetValue<string>() == "tool_call_update");
        Assert.Equal("completed", finished["status"]!.GetValue<string>());
        var diff = finished["content"]!.AsArray().Single()!;
        Assert.Equal("diff", diff["type"]!.GetValue<string>());
        Assert.Equal(file, diff["path"]!.GetValue<string>());
        Assert.Null(diff["oldText"]);
        Assert.Equal("hello\n", diff["newText"]!.GetValue<string>());
        Assert.Equal(file, finished["locations"]![0]!["path"]!.GetValue<string>());
        Exchanges.Verify("write-with-permission", client);
    }

    // Review of #59 (C-040), finding 6: the log holds the call's arguments credential-scrubbed; the permission request shows
    // what is written, from the prompt's in-memory action.
    [Fact]
    public async Task APermissionRequestShowsTheArgumentsThatRunNotTheScrubbedLog()
    {
        var models = WithFileEmbedder(new FakeModels(new FakeGenerator(
            Reply.Call("write_file", """{"path":"config.txt","content":"DB_PASSWORD=hunter2x9\n"}"""), Reply.Say("Wrote it."))));
        await using var client = AcpClient.Start(TestHost.Create(models, FileTools));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();

        await client.RequestAsync("session/prompt", Prompt(session, "write the config file config.txt"));

        var call = Assert.Single(client.PermissionRequests)["params"]!["toolCall"]!;
        Assert.Equal("DB_PASSWORD=hunter2x9\n", call["rawInput"]!["content"]!.GetValue<string>());
        var proposed = call["content"]!.AsArray().Single(item => item!["type"]!.GetValue<string>() == "diff")!;
        Assert.Equal("DB_PASSWORD=hunter2x9\n", proposed["newText"]!.GetValue<string>());
        var runtime = await client.Runtime;
        var logged = runtime.Log.Read(session, 0, long.MaxValue, 4096, TestContext.Current.CancellationToken)
            .Select(stored => stored.Payload).OfType<Corvid.Abstractions.Events.ToolCallIssued>().Single();
        Assert.Equal("DB_PASSWORD=[REDACTED:labelled-value]\n", logged.Arguments.GetProperty("content").GetString());
    }

    [Fact]
    public async Task AnEditOfAnExistingFileShowsBothSides()
    {
        var models = new FakeModels(new FakeGenerator(
            Reply.Call("patch_file", """{"path":"notes.txt","old_text":"milk","new_text":"bread"}"""), Reply.Say("Changed.")));
        await using var client = AcpClient.Start(TestHost.Create(models, BuiltinOn));
        var file = Path.Combine(client.Workspace, "notes.txt");
        await File.WriteAllTextAsync(file, "buy milk\n", TestContext.Current.CancellationToken);
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();

        await client.RequestAsync("session/prompt", Prompt(session, "patch the file notes.txt: bread, not milk"));

        var proposed = Assert.Single(client.PermissionRequests)["params"]!["toolCall"]!["content"]!.AsArray()
            .Single(item => item!["type"]!.GetValue<string>() == "diff")!;
        Assert.Equal("buy milk\n", proposed["oldText"]!.GetValue<string>());
        Assert.Equal("buy bread\n", proposed["newText"]!.GetValue<string>());
        var diff = client.UpdatesFor(session).Last(update => update["sessionUpdate"]!.GetValue<string>() == "tool_call_update")["content"]![0]!;
        Assert.Equal("buy milk\n", diff["oldText"]!.GetValue<string>());
        Assert.Equal("buy bread\n", diff["newText"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("deny")]
    [InlineData("cancelled")]
    [InlineData("not-an-option")]
    public async Task AnythingButAnAllowOptionDenies(string answer)
    {
        var models = WithFileEmbedder(new FakeModels(new FakeGenerator(Reply.Call("write_file", """{"path":"notes.txt","content":"hello\n"}"""), Reply.Say("I did not write it."))));
        await using var client = AcpClient.Start(TestHost.Create(models, FileTools));
        client.Permission = _ => Task.FromResult(answer == "cancelled" ? AcpClient.Cancelled() : AcpClient.Selected(answer));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();

        var response = await client.RequestAsync("session/prompt", Prompt(session, "write hello to the file notes.txt"));

        Assert.Equal("end_turn", response["result"]!["stopReason"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(client.Workspace, "notes.txt")));
        var finished = client.UpdatesFor(session).Last(update => update["sessionUpdate"]!.GetValue<string>() == "tool_call_update");
        Assert.Equal("failed", finished["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task SessionCancelEndsTheTurnAndItsPermissionRequest()
    {
        var generator = new FakeGenerator(Reply.Call("write_file", """{"path":"notes.txt","content":"hello\n"}"""), Reply.Say("Wrote it."));
        var models = WithFileEmbedder(new FakeModels(generator));
        await using var client = AcpClient.Start(TestHost.Create(models, FileTools));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Permission = async _ =>
        {
            asked.TrySetResult();
            // The editor holds the prompt open until the user cancels the turn; it then answers cancelled, as ACP requires.
            await release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return AcpClient.Cancelled();
        };

        var prompt = await client.BeginAsync("session/prompt", Prompt(session, "write hello to the file notes.txt"));
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        // A second prompt while one runs is refused.
        var second = await client.RequestAsync("session/prompt", Prompt(session, "and another"));
        Assert.Equal(-32600, second["error"]!["code"]!.GetValue<int>());
        await client.NotifyAsync("session/cancel", new { sessionId = session });
        release.SetResult();

        var response = await client.ResponseAsync(prompt);
        Assert.Equal("cancelled", response["result"]!["stopReason"]!.GetValue<string>());
        Assert.False(File.Exists(Path.Combine(client.Workspace, "notes.txt")));
        // The kernel saw the cancel before the permission request ended: no model call after it.
        Assert.Single(generator.Calls);
    }

    [Fact]
    public async Task ACancelledPromptAnswersCancelledEvenWhenItsTurnFinishesFirst()
    {
        // The kernel has finished the turn (answered, past its last cancellation check) when session/cancel arrives, and
        // the prompt has not answered yet. ACP: after session/cancel the agent MUST respond with the cancelled stop reason.
        var models = new FakeModels(new FakeGenerator(Reply.Say("Finished anyway.")));
        await using var client = AcpClient.Start(TestHost.Create(models));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();
        // The turn's invoke_agent span stops once its result is final: holding the listener there holds the finished turn.
        var finished = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var listener = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name == Corvid.Kernel.KernelTelemetry.SourceName,
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                System.Diagnostics.ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName != Corvid.Kernel.KernelTelemetry.InvokeAgentSpan
                    || activity.GetTagItem(Corvid.Kernel.KernelTelemetry.ConversationId) as string != session)
                    return;
                finished.TrySetResult(activity.GetTagItem("corvid.turn.status") as string);
                release.Wait(TimeSpan.FromSeconds(30));
            },
        };
        System.Diagnostics.ActivitySource.AddActivityListener(listener);

        var prompt = await client.BeginAsync("session/prompt", Prompt(session, "say something"));
        var status = await finished.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
        await client.NotifyAsync("session/cancel", new { sessionId = session });
        // Notifications are handled in order on the read loop: an answer to a later request means the cancel was seen.
        var second = await client.RequestAsync("session/prompt", Prompt(session, "and another"));
        Assert.Equal(-32600, second["error"]!["code"]!.GetValue<int>());
        release.Set();

        var response = await client.ResponseAsync(prompt);
        Assert.Equal("answered", status);
        Assert.Equal("cancelled", response["result"]!["stopReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task ACancelBetweenPromptsDoesNotCancelTheNextOne()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("First."), Reply.Say("Second.")));
        await using var client = AcpClient.Start(TestHost.Create(models));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();
        await client.RequestAsync("session/prompt", Prompt(session, "one"));
        await client.NotifyAsync("session/cancel", new { sessionId = session });

        var response = await client.RequestAsync("session/prompt", Prompt(session, "two"));

        Assert.Equal("end_turn", response["result"]!["stopReason"]!.GetValue<string>());
    }

    [Fact]
    public async Task LoadReplaysTheConversationAndResumeDoesNot()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("list_notes"), Reply.Say("You have two notes.")));
        await using var client = AcpClient.Start(TestHost.Create(models));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();
        await client.RequestAsync("session/prompt", Prompt(session, "list my notes"));
        var before = client.UpdatesFor(session).Count;

        var loaded = await client.RequestAsync("session/load", new { sessionId = session, cwd = client.Workspace, mcpServers = Array.Empty<object>() });

        Assert.NotNull(loaded["result"]);
        var replay = client.UpdatesFor(session).Skip(before).Where(update => update["sessionUpdate"]!.GetValue<string>() != "available_commands_update").ToList();
        Assert.Equal(["user_message_chunk", "tool_call", "tool_call_update", "agent_message_chunk"],
            replay.Select(update => update["sessionUpdate"]!.GetValue<string>()));
        Assert.Equal("list my notes", replay[0]["content"]!["text"]!.GetValue<string>());
        Assert.Equal("list_notes", replay[1]["title"]!.GetValue<string>());
        Assert.Equal("completed", replay[2]["status"]!.GetValue<string>());
        Assert.Equal("You have two notes.", replay[3]["content"]!["text"]!.GetValue<string>());

        var count = client.UpdatesFor(session).Count;
        var resumed = await client.RequestAsync("session/resume", new { sessionId = session, cwd = client.Workspace });
        Assert.NotNull(resumed["result"]);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(client.UpdatesFor(session).Skip(count), update => update["sessionUpdate"]!.GetValue<string>() is "user_message_chunk" or "agent_message_chunk");

        var missing = await client.RequestAsync("session/load", new { sessionId = "acp-nope", cwd = client.Workspace, mcpServers = Array.Empty<object>() });
        Assert.Equal(-32002, missing["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task ForkCopiesTheSessionAtALogPosition()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("First answer."), Reply.Say("Answer in the fork.")));
        await using var client = AcpClient.Start(TestHost.Create(models));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();
        await client.RequestAsync("session/prompt", Prompt(session, "first question"));
        var runtime = await client.Runtime;
        var head = runtime.Log.GetHead(session, TestContext.Current.CancellationToken);

        var forked = await client.RequestAsync("session/fork", new { sessionId = session, cwd = client.Workspace });
        var fork = forked["result"]!["sessionId"]!.GetValue<string>();
        Assert.NotEqual(session, fork);
        Assert.Equal(head, runtime.Log.GetHead(fork, TestContext.Current.CancellationToken));
        await client.RequestAsync("session/prompt", Prompt(fork, "second question"));
        // The fork goes on alone; the source is unchanged.
        Assert.Equal(head, runtime.Log.GetHead(session, TestContext.Current.CancellationToken));
        var transcript = Corvid.Kernel.Events.SessionProjection.Load(runtime.Log, fork, cancellationToken: TestContext.Current.CancellationToken).Transcript.Select(entry => entry.Content).ToList();
        Assert.Equal(["first question", "second question"], transcript.OfType<UserMessage>().Select(message => message.Text));

        // At an earlier position: the first event only.
        var early = await client.RequestAsync("session/fork", new { sessionId = session, cwd = client.Workspace, _meta = new { corvid = new { throughSequence = 1 } } });
        Assert.Equal(1, runtime.Log.GetHead(early["result"]!["sessionId"]!.GetValue<string>(), TestContext.Current.CancellationToken));
        var outside = await client.RequestAsync("session/fork", new { sessionId = session, cwd = client.Workspace, _meta = new { corvid = new { throughSequence = head + 5 } } });
        Assert.Equal(-32602, outside["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task ListShowsTheUsersConversations()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Say("Hi.")));
        await using var client = AcpClient.Start(TestHost.Create(models));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();
        await client.RequestAsync("session/prompt", Prompt(session, "remember the milk"));

        var listed = await client.RequestAsync("session/list", new { });

        var entry = listed["result"]!["sessions"]!.AsArray().Single(item => item!["sessionId"]!.GetValue<string>() == session)!;
        Assert.Equal("remember the milk", entry["title"]!.GetValue<string>());
        Assert.Equal((await client.Runtime).Builtin.Environment.Workspace, entry["cwd"]!.GetValue<string>());
        Assert.NotNull(entry["updatedAt"]);
        var elsewhere = await client.RequestAsync("session/list", new { cwd = "/" });
        Assert.Empty(elsewhere["result"]!["sessions"]!.AsArray());
    }

    [Fact]
    public async Task ASessionForAnotherDirectoryIsRefused()
    {
        await using var client = AcpClient.Start(TestHost.Create());
        await client.RequestAsync("initialize", Initialize());
        var other = Directory.CreateTempSubdirectory("corvid-acp-other-");
        try
        {
            var response = await client.RequestAsync("session/new", new { cwd = other.FullName, mcpServers = Array.Empty<object>() }, TimeSpan.FromSeconds(120));
            Assert.Equal(-32602, response["error"]!["code"]!.GetValue<int>());
            Assert.Contains("workspace", response["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
            var relative = await client.RequestAsync("session/new", new { cwd = "work", mcpServers = Array.Empty<object>() });
            Assert.Equal(-32602, relative["error"]!["code"]!.GetValue<int>());
        }
        finally
        {
            other.Delete();
        }
    }

    [Fact]
    public async Task MalformedAndUnsupportedRequestsGetJsonRpcErrors()
    {
        await using var client = AcpClient.Start(TestHost.Create());
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();

        await client.SendRawAsync("{not json");
        var parse = await client.NextErrorWithoutIdAsync();
        Assert.Equal(-32700, parse["error"]!["code"]!.GetValue<int>());

        var unknown = await client.RequestAsync("session/set_mode", new { sessionId = session, modeId = "x" });
        Assert.Equal(-32601, unknown["error"]!["code"]!.GetValue<int>());
        var image = await client.RequestAsync("session/prompt", new { sessionId = session, prompt = new object[] { new { type = "image", data = "AA==", mimeType = "image/png" } } });
        Assert.Equal(-32602, image["error"]!["code"]!.GetValue<int>());
        var missing = await client.RequestAsync("session/prompt", new { sessionId = session });
        Assert.Equal(-32602, missing["error"]!["code"]!.GetValue<int>());
        var closed = await client.RequestAsync("session/prompt", Prompt("acp-unknown", "hi"));
        Assert.Equal(-32002, closed["error"]!["code"]!.GetValue<int>());
    }

    private sealed class ExtraTools(params Corvid.Tools.ToolFunction[] tools) : Corvid.Tools.IToolProvider
    {
        public IReadOnlyList<Corvid.Tools.ToolFunction> CreateTools() => tools;
    }

    // A5 (A4 leftover): a running call's output tail reaches the editor as the call's content while it runs.
    [Fact]
    public async Task ARunningCallsOutputTailIsAToolCallUpdateInProgress()
    {
        var weather = new TestTool(new Corvid.Tools.ToolDescriptor("check_weather", "Check the weather forecast.", """{"type":"object","properties":{}}""",
            new Corvid.Tools.ToolRisk(Corvid.Tools.SideEffects.None, Network: false, Destructive: false), []), arguments =>
        {
            Corvid.Tools.ToolCallContext.From(arguments)!.Output!("asking the forecast service\n");
            return "sunny";
        });
        var models = new FakeModels(new FakeGenerator(Reply.Call("check_weather"), Reply.Say("Sunny.")));
        await using var client = AcpClient.Start(TestHost.Create(models,
            services: services => Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<Corvid.Tools.IToolProvider>(services, new ExtraTools(weather))));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();

        await client.RequestAsync("session/prompt", Prompt(session, "what's the weather?"));

        var updates = client.UpdatesFor(session);
        var tail = Assert.Single(updates, update => update["sessionUpdate"]?.GetValue<string>() == "tool_call_update"
            && update["status"]?.GetValue<string>() == "in_progress");
        Assert.Equal("asking the forecast service", tail["content"]![0]!["content"]!["text"]!.GetValue<string>().TrimEnd());
        var done = updates.FindIndex(update => update["sessionUpdate"]?.GetValue<string>() == "tool_call_update"
            && update["status"]?.GetValue<string>() == "completed");
        Assert.True(updates.IndexOf(tail) < done);
    }

    // A5 (A4 leftover): a turn that ends on a clarifying question (K-6) tells the editor so, structured, before the prompt's
    // response; ACP has no way to answer inside the turn, so the person's next prompt answers it.
    [Fact]
    public async Task AQuestionTheTurnEndedOnIsACorvidQuestionNotification()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("ask_user", """{"question":"Which branch?","options":["main","release"]}""")));
        await using var client = AcpClient.Start(TestHost.Create(models, BuiltinOn));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();

        var response = await client.RequestAsync("session/prompt", Prompt(session, "tag the release"));

        Assert.Equal("end_turn", response["result"]!["stopReason"]!.GetValue<string>());
        List<JsonObject> questions;
        lock (client.Updates)
            questions = [.. client.Updates.Where(update => update["method"]?.GetValue<string>() == "_corvid/question")];
        var question = Assert.Single(questions)["params"]!;
        Assert.Equal(session, question["sessionId"]!.GetValue<string>());
        Assert.Equal("Which branch?", question["question"]!.GetValue<string>());
        Assert.Equal(["main", "release"], question["options"]!.AsArray().Select(option => option!.GetValue<string>()));
        Assert.StartsWith("q-", question["questionId"]!.GetValue<string>(), StringComparison.Ordinal);
        // The question's text is the turn's answer too, so a client that ignores the notification still shows it.
        Assert.Contains(client.UpdatesFor(session), update => update["sessionUpdate"]?.GetValue<string>() == "agent_message_chunk"
            && update["content"]!["text"]!.GetValue<string>().Contains("Which branch?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AResourceLinkReachesTheModelAsText()
    {
        var generator = new FakeGenerator(Reply.Say("Seen."));
        await using var client = AcpClient.Start(TestHost.Create(new FakeModels(generator)));
        await client.RequestAsync("initialize", Initialize());
        var session = await client.NewSessionAsync();

        await client.RequestAsync("session/prompt", new
        {
            sessionId = session,
            prompt = new object[] { new { type = "text", text = "look at" }, new { type = "resource_link", name = "main.cs", uri = "file:///w/main.cs" } },
        });

        var runtime = await client.Runtime;
        var message = Corvid.Kernel.Events.SessionProjection.Load(runtime.Log, session, cancellationToken: TestContext.Current.CancellationToken).Transcript.Select(entry => entry.Content).OfType<UserMessage>().Single();
        Assert.Equal("look at\n[main.cs](file:///w/main.cs)", message.Text);
    }

    [Fact]
    public async Task MissingModelsFailSessionsButNotInitialize()
    {
        await using var client = AcpClient.Start(TestHost.Create(new FakeModels(generatorReady: false)));
        var initialized = await client.RequestAsync("initialize", Initialize());
        Assert.NotNull(initialized["result"]);

        var response = await client.RequestAsync("session/new", new { cwd = client.Workspace, mcpServers = Array.Empty<object>() }, TimeSpan.FromSeconds(120));

        Assert.Equal(-32603, response["error"]!["code"]!.GetValue<int>());
        Assert.Contains("corvid model download fake-gen", response["error"]!["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains("could not start", client.StandardError, StringComparison.Ordinal);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.True(condition());
    }
}
