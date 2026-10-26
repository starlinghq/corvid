using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Corvid.Tools.CodeMode;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Corvid.Tools.Tests;

/// <summary>
/// A7: code mode's generated clients (C#, Python, JavaScript), each run against a host speaking the frozen wire format: a
/// real <see cref="CodeModeSession"/> on a Unix socket with a fake invoker standing in for the turn. Each script checks a
/// wrong token is refused, calls a tool with required and optional arguments, reads the taint of an outside result, gets a
/// refused call and an unknown tool as exceptions, calls a tool whose names are keywords, and reads the limits.
/// </summary>
public sealed class CodeModeClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    /// <summary>Text an MCP server could write to break out of a comment, a docstring or a string, or hide code with bidi controls.</summary>
    internal static readonly string Hostile = "Weather.\"\"\" '''\n__import__('os').system('touch PWNED')\n*/ ${process.exit(3)} `x` \\\" "
        + (char)0x202E + (char)0x2066 + " }\r\n#endregion" + (char)0x2028 + "console.log(1)";

    internal static IReadOnlyList<CodeModeTool> Tools() =>
    [
        new("get_weather", Hostile, Json("""
            {"type":"object","properties":{
              "city":{"type":"string","description":"The city.\n\"\"\" */"},
              "days":{"type":"integer"},
              "units":{"type":["string","null"],"enum":["metric","imperial"]},
              "tags":{"type":"array","items":{"type":"string"}}
            },"required":["city"]}
            """)),
        new("fetch_page", "Fetches a page.", Json("""{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}""")),
        new("fail_tool", "Always refused.", Json("""{"type":"object","properties":{"reason":{"type":"string"}},"required":["reason"]}""")),
        new("class", "A tool whose names are keywords.", Json("""
            {"type":"object","properties":{"from":{"type":"string"},"in":{"type":"integer"},"options":{"type":"object"},"call":{"type":"boolean"},
              "cancellationToken":{"type":"number"}},"required":["from"]}
            """)),
    ];

    /// <summary>The host side: a Unix socket in a 0700 temporary directory, served by one <see cref="CodeModeSession"/>.</summary>
    internal sealed class FakeHost : IAsyncDisposable
    {
        private readonly Socket _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _accepting;
        private readonly ConcurrentBag<Task> _connections = [];

        public FakeHost(IReadOnlyList<CodeModeTool> tools)
        {
            Directory = System.IO.Directory.CreateTempSubdirectory("corvid-cm-");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(Directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            SocketPath = Path.Combine(Directory.FullName, "tools.sock");
            TokenFile = Path.Combine(Directory.FullName, "tools.token");
            WrongTokenFile = Path.Combine(Directory.FullName, "wrong.token");
            var token = CodeModeProtocol.NewToken();
            File.WriteAllText(TokenFile, token);
            File.WriteAllText(WrongTokenFile, CodeModeProtocol.NewToken());
            Session = new CodeModeSession(token, tools, InvokeAsync);
            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _listener.Listen();
            _accepting = AcceptAsync();
        }

        public DirectoryInfo Directory { get; }

        public string SocketPath { get; }

        public string TokenFile { get; }

        public string WrongTokenFile { get; }

        public CodeModeSession Session { get; }

        public ConcurrentQueue<(string Tool, string Arguments)> Calls { get; } = new();

        private Task<NestedToolResult> InvokeAsync(string tool, JsonElement arguments, CancellationToken cancellationToken)
        {
            Calls.Enqueue((tool, arguments.GetRawText()));
            var echo = Json($$"""{"tool":"{{tool}}","args":{{arguments.GetRawText()}}}""");
            return Task.FromResult(tool switch
            {
                "fail_tool" => new NestedToolResult("p.9", Json("""{"error":"not run","details":["denied by the user"]}"""), IsError: true, Tainted: false),
                "fetch_page" => new NestedToolResult("p.2", echo, IsError: false, Tainted: true),
                _ => new NestedToolResult("p.1", echo, IsError: false, Tainted: false),
            });
        }

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var socket = await _listener.AcceptAsync(_stop.Token);
                    _connections.Add(Task.Run(async () =>
                    {
                        await using var stream = new NetworkStream(socket, ownsSocket: true);
                        await Session.ServeAsync(stream, _stop.Token);
                    }));
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        /// <param name="input">The script on standard input, as code mode's runner gives it; null for none.</param>
        public async Task<(int ExitCode, string Output, string Error)> RunAsync(string file, string arguments, string workingDirectory, TimeSpan timeout,
            string? input = null)
        {
            var start = new ProcessStartInfo(file, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = input is not null,
                UseShellExecute = false,
            };
            start.Environment["CORVID_TOOLS_SOCKET"] = SocketPath;
            start.Environment["CORVID_TOOLS_TOKEN_FILE"] = TokenFile;
            start.Environment["WRONG_TOKEN_FILE"] = WrongTokenFile;
            start.Environment["DOTNET_NOLOGO"] = "1";
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            using var process = Process.Start(start)!;
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), Ct);
                process.StandardInput.Close();
            }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            limit.CancelAfter(timeout);
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var error = process.StandardError.ReadToEndAsync(limit.Token);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            return (process.ExitCode, await output, await error);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Dispose();
            await _accepting;
            foreach (var connection in _connections)
                try
                {
                    await connection;
                }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException)
                {
                }
            _stop.Dispose();
            Directory.Delete(recursive: true);
        }
    }

    /// <summary>The program on <see cref="PATH"/>, or null.</summary>
    internal static string? Find(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, program)).FirstOrDefault(File.Exists);

    /// <summary>What every script prints: one JSON object, checked the same way for each language.</summary>
    private static void AssertReport(FakeHost host, (int ExitCode, string Output, string Error) run)
    {
        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}\n{run.Output}\n{run.Error}");
        Assert.Empty(System.IO.Directory.EnumerateFiles(host.Directory.FullName, "PWNED", SearchOption.AllDirectories));
        var report = Json(run.Output.Trim().Split('\n')[^1]);

        Assert.Equal("unauthorized", report.GetProperty("wrong_token").GetString());
        Assert.Equal("""{"tool":"get_weather","args":{"city":"Paris","days":2,"tags":["a","b"]}}""", report.GetProperty("weather").GetRawText());
        Assert.False(report.GetProperty("weather_tainted").GetBoolean());
        Assert.Equal("https://example.org/", report.GetProperty("page").GetProperty("args").GetProperty("url").GetString());
        Assert.True(report.GetProperty("page_tainted").GetBoolean());
        var fail = report.GetProperty("fail");
        Assert.Equal("tool_error", fail.GetProperty("code").GetString());
        Assert.Equal("not run: denied by the user", fail.GetProperty("message").GetString());
        Assert.Equal("not run", fail.GetProperty("detail").GetProperty("error").GetString());
        Assert.False(fail.GetProperty("tainted").GetBoolean());
        Assert.Equal("unknown_tool", report.GetProperty("unknown").GetString());
        Assert.Equal("""{"tool":"class","args":{"from":"x","in":1,"call":true,"cancellationToken":0.5}}""", report.GetProperty("keywords").GetRawText());
        Assert.Equal(50, report.GetProperty("limits").GetProperty("calls").GetInt32());

        // The wrong token ran nothing; the unknown tool never reached the turn.
        Assert.Equal(["get_weather", "fetch_page", "fail_tool", "class"], host.Calls.Select(call => call.Tool));
        Assert.Equal(4, host.Session.Calls);
    }

    private const string PythonScript = """
        import json, os
        import corvid_tools as t

        out = {}
        right = os.environ["CORVID_TOOLS_TOKEN_FILE"]
        os.environ["CORVID_TOOLS_TOKEN_FILE"] = os.environ["WRONG_TOKEN_FILE"]
        try:
            t.get_weather("Nowhere")
        except t.CorvidToolError as e:
            out["wrong_token"] = e.code
        os.environ["CORVID_TOOLS_TOKEN_FILE"] = right
        out["weather"] = t.get_weather("Paris", days=2, tags=["a", "b"])
        out["weather_tainted"] = t.last_call_tainted()
        out["page"] = t.fetch_page("https://example.org/")
        out["page_tainted"] = t.last_call_tainted()
        try:
            t.fail_tool("why")
        except t.CorvidToolError as e:
            out["fail"] = {"code": e.code, "message": e.message, "detail": e.detail, "tainted": e.tainted}
        try:
            t.call("not_offered", {})
        except t.CorvidToolError as e:
            out["unknown"] = e.code
        out["keywords"] = t.class_("x", in_=1, call_=True, cancellationToken=0.5)
        out["limits"] = t.limits()
        assert "Weather." in t.get_weather.__doc__
        print(json.dumps(out, separators=(",", ":")))
        """;

    private const string JavaScriptBody = """
        const out = {};
        const right = process.env.CORVID_TOOLS_TOKEN_FILE;
        process.env.CORVID_TOOLS_TOKEN_FILE = process.env.WRONG_TOKEN_FILE;
        try { await t.get_weather("Nowhere"); } catch (e) { out.wrong_token = e.code; }
        process.env.CORVID_TOOLS_TOKEN_FILE = right;
        out.weather = await t.get_weather("Paris", { days: 2, tags: ["a", "b"] });
        out.weather_tainted = t.lastCallTainted();
        out.page = await t.fetch_page("https://example.org/");
        out.page_tainted = t.lastCallTainted();
        try { await t.fail_tool("why"); } catch (e) {
          if (!(e instanceof t.CorvidToolError)) throw e;
          out.fail = { code: e.code, message: e.message.slice(e.code.length + 2), detail: e.detail, tainted: e.tainted };
        }
        try { await t.call("not_offered", {}); } catch (e) { out.unknown = e.code; }
        out.keywords = await t.tools["class"]("x", { in: 1, call: true, cancellationToken: 0.5 });
        out.limits = t.limits();
        if (!t.get_weather.description.includes("Weather.")) throw new Error("no description");
        console.log(JSON.stringify(out));
        """;

    [Fact]
    public async Task The_python_client_inline_runs_from_standard_input_as_code_mode_runs_it()
    {
        Assert.SkipWhen(Find("python3") is null, "python3 is not installed");
        await using var host = new FakeHost(Tools());
        var work = host.Directory.CreateSubdirectory("work").FullName;
        var (prefix, suffix) = CodeModeClients.Inline(CodeModeLanguage.Python, Tools());

        // As the code tool runs a script: one file on standard input, nothing beside it; corvid_tools imports from memory.
        var run = await host.RunAsync("python3", "-E -s -B -", work, TimeSpan.FromSeconds(60),
            prefix + "assert corvid.get_weather is get_weather\n" + PythonScript + suffix);

        AssertReport(host, run);
        Assert.Empty(System.IO.Directory.EnumerateFileSystemEntries(work));
    }

    [Fact]
    public async Task The_javascript_client_inline_keeps_its_names_out_of_the_scripts_way()
    {
        Assert.SkipWhen(Find("node") is null, "node is not installed");
        await using var host = new FakeHost(Tools());
        var work = host.Directory.CreateSubdirectory("work").FullName;
        var (prefix, suffix) = CodeModeClients.Inline(CodeModeLanguage.JavaScript, Tools());

        // The script declares names the client uses inside; only corvid is the client's at the top level.
        var run = await host.RunAsync("node", "--input-type=module -", work, TimeSpan.FromSeconds(60),
            prefix + "const call = 1; const tools = 2; function limits() {} class CorvidToolError {}\nconst t = corvid;\n" + JavaScriptBody + suffix);

        AssertReport(host, run);
    }

    [Fact]
    public async Task The_python_client_calls_tools_over_the_socket_and_raises_errors()
    {
        Assert.SkipWhen(Find("python3") is null, "python3 is not installed");
        await using var host = new FakeHost(Tools());
        var work = host.Directory.CreateSubdirectory("work").FullName;
        var client = CodeModeClients.Generate(CodeModeLanguage.Python, Tools());
        await File.WriteAllTextAsync(Path.Combine(work, client.FileName!), client.Source, Ct);
        await File.WriteAllTextAsync(Path.Combine(work, "script.py"), PythonScript, Ct);

        // -E -s: no PYTHON* variables and no user site; the script's directory stays on the path for the module.
        var run = await host.RunAsync("python3", "-E -s script.py", work, TimeSpan.FromSeconds(60));

        AssertReport(host, run);
    }

    [Fact]
    public async Task The_javascript_client_calls_tools_over_the_socket_and_rejects_with_errors()
    {
        Assert.SkipWhen(Find("node") is null, "node is not installed");
        await using var host = new FakeHost(Tools());
        var work = host.Directory.CreateSubdirectory("work").FullName;
        var client = CodeModeClients.Generate(CodeModeLanguage.JavaScript, Tools());
        await File.WriteAllTextAsync(Path.Combine(work, client.FileName!), client.Source, Ct);
        await File.WriteAllTextAsync(Path.Combine(work, "script.mjs"), "import * as t from \"./corvid_tools.mjs\";\n" + JavaScriptBody, Ct);

        // The open socket must not keep the script alive: it exits on its own.
        var run = await host.RunAsync("node", "script.mjs", work, TimeSpan.FromSeconds(60));

        AssertReport(host, run);
    }

    /// <summary>The SDK the tests run under: the runtime's directory is <c>{root}/shared/Microsoft.NETCore.App/{version}</c>.</summary>
    internal static string? Dotnet()
    {
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var root = runtime is null ? null : Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        var dotnet = root is null ? null : Path.Combine(root, "dotnet");
        return dotnet is not null && File.Exists(dotnet) && System.IO.Directory.Exists(Path.Combine(root!, "sdk")) ? dotnet : null;
    }

    private const string CSharpScript = """
        var output = new System.Text.Json.Nodes.JsonObject();
        var right = System.Environment.GetEnvironmentVariable("CORVID_TOOLS_TOKEN_FILE");
        System.Environment.SetEnvironmentVariable("CORVID_TOOLS_TOKEN_FILE", System.Environment.GetEnvironmentVariable("WRONG_TOKEN_FILE"));
        try { await CorvidTools.GetWeatherAsync("Nowhere"); } catch (CorvidToolException e) { output["wrong_token"] = e.Code; }
        System.Environment.SetEnvironmentVariable("CORVID_TOOLS_TOKEN_FILE", right);
        output["weather"] = System.Text.Json.Nodes.JsonNode.Parse((await CorvidTools.GetWeatherAsync("Paris", days: 2, tags: ["a", "b"])).GetRawText());
        output["weather_tainted"] = CorvidTools.LastCallTainted;
        output["page"] = System.Text.Json.Nodes.JsonNode.Parse((await CorvidTools.FetchPageAsync("https://example.org/")).GetRawText());
        output["page_tainted"] = CorvidTools.LastCallTainted;
        try { await CorvidTools.FailToolAsync("why"); }
        catch (CorvidToolException e)
        {
            output["fail"] = new System.Text.Json.Nodes.JsonObject
            {
                ["code"] = e.Code,
                ["message"] = e.Message[(e.Code.Length + 2)..],
                ["detail"] = System.Text.Json.Nodes.JsonNode.Parse(e.Detail!.Value.GetRawText()),
                ["tainted"] = e.Tainted,
            };
        }
        try { await CorvidTools.CallAsync("not_offered"); } catch (CorvidToolException e) { output["unknown"] = e.Code; }
        output["keywords"] = System.Text.Json.Nodes.JsonNode.Parse((await CorvidTools.ClassAsync("x", @in: 1, call: true, cancellationToken_: 0.5)).GetRawText());
        output["limits"] = System.Text.Json.Nodes.JsonNode.Parse(CorvidTools.Limits!.Value.GetRawText());
        if (!CorvidTools.Descriptions["get_weather"].Contains("Weather.")) throw new System.Exception("no description");
        System.Console.WriteLine(output.ToJsonString());
        """;

    [Fact]
    public async Task The_csharp_client_runs_as_a_file_based_app_against_the_socket()
    {
        var dotnet = Dotnet();
        Assert.SkipWhen(dotnet is null, "no .NET SDK beside the runtime");
        await using var host = new FakeHost(Tools());
        var work = host.Directory.CreateSubdirectory("work").FullName;
        await File.WriteAllTextAsync(Path.Combine(work, "script.cs"), CodeModeClients.WithCSharpClient(CSharpScript, Tools()), Ct);

        var run = await host.RunAsync(dotnet!, "run script.cs", work, TimeSpan.FromMinutes(4));

        AssertReport(host, run);
    }

    /// <summary>The arguments each of the three calls below sends: by position, with the optional ones by name, all by name.</summary>
    private static readonly string[] ByPositionOrName =
        ["""{"city":"Rome","days":3}""", """{"city":"Oslo","days":4,"units":"metric"}""", """{"city":"Bern"}"""];

    private static void AssertArguments(FakeHost host, (int ExitCode, string Output, string Error) run)
    {
        Assert.True(run.ExitCode == 0, $"exit {run.ExitCode}\n{run.Output}\n{run.Error}");
        var sent = host.Calls.Select(call => System.Text.Json.Nodes.JsonNode.Parse(call.Arguments)).ToList();
        Assert.Equal(ByPositionOrName.Length, sent.Count);
        for (var i = 0; i < sent.Count; i++)
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(ByPositionOrName[i]), sent[i]), sent[i]!.ToJsonString());
    }

    [Fact]
    public async Task Python_optional_arguments_go_by_position_or_by_name()
    {
        Assert.SkipWhen(Find("python3") is null, "python3 is not installed");
        await using var host = new FakeHost(Tools());
        var (prefix, suffix) = CodeModeClients.Inline(CodeModeLanguage.Python, Tools());

        var run = await host.RunAsync("python3", "-E -s -B -", host.Directory.FullName, TimeSpan.FromSeconds(60), prefix + """
            corvid.get_weather("Rome", 3)
            corvid.get_weather("Oslo", days=4, units="metric")
            corvid.get_weather(city="Bern")
            """ + suffix);

        AssertArguments(host, run);
    }

    [Fact]
    public async Task JavaScript_optional_arguments_go_by_position_or_by_name()
    {
        Assert.SkipWhen(Find("node") is null, "node is not installed");
        await using var host = new FakeHost(Tools());
        var (prefix, suffix) = CodeModeClients.Inline(CodeModeLanguage.JavaScript, Tools());

        var run = await host.RunAsync("node", "--input-type=module -", host.Directory.FullName, TimeSpan.FromSeconds(60), prefix + """
            await corvid.get_weather("Rome", 3);
            await corvid.get_weather("Oslo", { days: 4, units: "metric" });
            await corvid.get_weather({ city: "Bern" });
            """ + suffix);

        AssertArguments(host, run);
    }

    [Fact]
    public void The_csharp_client_compiles_cleanly_with_hostile_descriptions_and_keyword_names()
    {
        var source = CodeModeClients.WithCSharpClient(CSharpScript, Tools());
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create("script", [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), cancellationToken: Ct)],
            references, new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));

        using var assembly = new MemoryStream();
        var emitted = compilation.Emit(assembly, cancellationToken: Ct);

        Assert.True(emitted.Success, string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        Assert.Empty(emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning));
    }

    [Theory]
    [InlineData(CodeModeLanguage.CSharp)]
    [InlineData(CodeModeLanguage.Python)]
    [InlineData(CodeModeLanguage.JavaScript)]
    public void A_client_is_ascii_and_never_carries_descriptor_text_raw(CodeModeLanguage language)
    {
        var source = CodeModeClients.Generate(language, Tools()).Source;

        Assert.All(source, c => Assert.True(c is '\n' or (>= ' ' and <= '~'), $"U+{(int)c:X4} in the {language} client"));
        // The description's line breaks and quotes stay escapes inside one string literal.
        Assert.DoesNotContain("\n__import__", source, StringComparison.Ordinal);
        Assert.DoesNotContain("'os'", source, StringComparison.Ordinal);
        Assert.Contains("\\n__import__(\\u0027os\\u0027).system(\\u0027touch PWNED\\u0027)\\n", source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_descriptor_round_trips_and_a_wrong_one_is_refused()
    {
        var tools = Tools();

        var read = CodeModeClients.FromJson(CodeModeCatalog.ToJson(tools));

        Assert.Equal(tools.Select(tool => (tool.Name, tool.Description, JsonSerializer.Serialize(tool.Parameters))),
            read.Select(tool => (tool.Name, tool.Description, JsonSerializer.Serialize(tool.Parameters))));
        Assert.Throws<JsonException>(() => CodeModeClients.FromJson("""{"version":2,"tools":[]}"""u8));
        Assert.Throws<JsonException>(() => CodeModeClients.FromJson("""{"version":1,"tools":[{"name":"a-b","parameters":{}}]}"""u8));
        Assert.True(CodeModeClients.TryParseLanguage("Python3", out var python) && python == CodeModeLanguage.Python);
        Assert.True(CodeModeClients.TryParseLanguage("c#", out var csharp) && csharp == CodeModeLanguage.CSharp);
        Assert.False(CodeModeClients.TryParseLanguage("ruby", out _));
    }

    [Fact]
    public void Tool_names_that_shadow_the_clients_own_names_are_renamed()
    {
        var empty = Json("""{"type":"object","properties":{}}""");
        CodeModeTool[] tools = [new("call", "", empty), new("_corvid_client", "", empty), new("__corvidNet", "", empty), new("call_async", "", empty)];

        var python = CodeModeClients.Python(tools);
        var javascript = CodeModeClients.JavaScript(tools);
        var csharp = CodeModeClients.CSharp(tools);

        Assert.Contains("def call_(", python, StringComparison.Ordinal);
        Assert.Contains("def tool_corvid_client(", python, StringComparison.Ordinal);
        Assert.Contains("export async function call_(", javascript, StringComparison.Ordinal);
        Assert.Contains("export async function tool__corvidNet(", javascript, StringComparison.Ordinal);
        Assert.Contains(" CallAsync_(", csharp, StringComparison.Ordinal);
        Assert.Contains(" CallAsyncAsync(", csharp, StringComparison.Ordinal);
    }
}
