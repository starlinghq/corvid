using System.Net;
using System.Text;
using Corvid.Vault.Client;
using static Corvid.Tools.Builtin.Tests.Fixtures;

namespace Corvid.Tools.Builtin.Tests;

/// <summary>Serves canned responses by URL and records every request; it never follows redirects itself.</summary>
internal sealed class CannedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Page(string body, string mediaType = "text/html", HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    public static HttpResponseMessage Redirect(string location) =>
        new(HttpStatusCode.Found) { Headers = { Location = new Uri(location, UriKind.RelativeOrAbsolute) } };
}

public sealed class WebFetchTests
{
    private const string Html = """
        <!doctype html><html><head><title>Release &amp; notes</title><style>body { color: red }</style>
        <script>window.secret = "do not show";</script></head>
        <body><h1>Corvid 0.1</h1><p>Built-in tools &lt;finally&gt;.</p><!-- hidden comment -->
        <ul><li>files</li><li>shell</li></ul></body></html>
        """;

    [Fact]
    public void The_tool_is_a_read_only_network_tool()
    {
        using var web = new WebFetchTool(new CannedHandler(_ => CannedHandler.Page("")));
        var tool = Tool(web, "web_fetch");

        Assert.Equal(new ToolRisk(SideEffects.None, Network: true, Destructive: false), tool.Descriptor.Risk);
        Assert.Equal(["url"], tool.Descriptor.Parameters.Select(parameter => parameter.Name));
    }

    [Fact]
    public async Task Html_is_reduced_to_its_visible_text_and_marked_tainted()
    {
        var handler = new CannedHandler(_ => CannedHandler.Page(Html));
        using var web = new WebFetchTool(handler);

        var result = await Invoke(web, "web_fetch", """{"url":"https://example.org/releases"}""", new ToolCallContext(Session, "c-1"));

        Assert.True(result.GetProperty("tainted").GetBoolean());
        Assert.Equal(200, result.GetProperty("status").GetInt32());
        Assert.Equal("Release & notes", result.GetProperty("title").GetString());
        Assert.Equal("Corvid 0.1\nBuilt-in tools <finally>.\n- files\n- shell", result.GetProperty("content").GetString());
        Assert.False(result.GetProperty("truncated").GetBoolean());
        // The request tells Vault's handler, when it fronts this tool, that the turn was clean.
        var request = Assert.Single(handler.Requests);
        Assert.True(request.Options.TryGetValue(VaultHandler.Tainted, out var tainted));
        Assert.False(tainted);
    }

    [Fact]
    public async Task A_fetch_reports_its_progress_as_lines_and_never_the_page()
    {
        var handler = new CannedHandler(request => request.RequestUri!.AbsolutePath == "/old"
            ? CannedHandler.Redirect("/releases")
            : CannedHandler.Page(Html));
        using var web = new WebFetchTool(handler);
        var lines = new List<string>();

        await Invoke(web, "web_fetch", """{"url":"https://example.org/old"}""", new ToolCallContext(Session, "c-1") { Output = lines.Add });

        // K-2: what a running card's tail shows; the page itself is only in the result the model reads.
        Assert.Equal(["GET https://example.org/old\n", "GET https://example.org/releases\n", "200 text/html\n", "read\n"], lines);
        Assert.DoesNotContain(lines, line => line.Contains("Corvid 0.1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Redirects_are_followed_here_only_to_http_and_only_so_far()
    {
        var handler = new CannedHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/old" => CannedHandler.Redirect("/new"),
            "/new" => CannedHandler.Redirect("https://cdn.example.net/final"),
            "/final" => CannedHandler.Page("{\"ok\":true}", "application/json"),
            "/file" => CannedHandler.Redirect("file:///etc/passwd"),
            "/ftp" => CannedHandler.Redirect("ftp://example.org/x"),
            "/local" => CannedHandler.Redirect("http://127.0.0.1:8080/admin"),
            _ => CannedHandler.Redirect(request.RequestUri.AbsolutePath + "x"),
        });
        using var web = new WebFetchTool(handler, WebFetchOptions.Default with { MaxRedirects = 3 });

        var followed = await Invoke(web, "web_fetch", """{"url":"http://example.org/old"}""");
        Assert.Equal("https://cdn.example.net/final", followed.GetProperty("final_url").GetString());
        Assert.Equal("{\"ok\":true}", followed.GetProperty("content").GetString());

        foreach (var path in (string[])["/file", "/ftp", "/local", "/loop"])
        {
            await Assert.ThrowsAsync<HttpRequestException>(() => Invoke(web, "web_fetch", $$"""{"url":"http://example.org{{path}}"}"""));
        }

        Assert.DoesNotContain(handler.Requests, request => request.RequestUri!.Scheme != "http" && request.RequestUri.Scheme != "https");
        Assert.DoesNotContain(handler.Requests, request => request.RequestUri!.Host == "127.0.0.1");
    }

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.org/a")]
    [InlineData("example.org/page")]
    [InlineData("http://localhost:5000/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://10.0.0.8/")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://admin.localhost/")]
    [InlineData("https://user:password@example.org/")]
    public async Task Urls_that_are_not_public_http_are_refused_before_any_request(string url)
    {
        var handler = new CannedHandler(_ => CannedHandler.Page("should not be fetched"));
        using var web = new WebFetchTool(handler);

        await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(web, "web_fetch", $$"""{"url":"{{url}}"}"""));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Bodies_are_capped_and_binary_content_is_not_shown()
    {
        var handler = new CannedHandler(request => request.RequestUri!.AbsolutePath == "/big"
            ? CannedHandler.Page(new string('a', 10_000), "text/plain")
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x89, 0x50, 0x4e, 0x47, 0, 0]) { Headers = { ContentType = new("image/png") } } });
        using var web = new WebFetchTool(handler, WebFetchOptions.Default with { MaxBytes = 4_096, MaxResultChars = 1_000 });

        var big = await Invoke(web, "web_fetch", """{"url":"https://example.org/big"}""");
        Assert.True(big.GetProperty("truncated").GetBoolean());
        Assert.Equal(1_000, big.GetProperty("content").GetString()!.Length);
        var image = await Invoke(web, "web_fetch", """{"url":"https://example.org/logo.png"}""");
        Assert.Equal("[image/png content, 6 bytes, not shown]", image.GetProperty("content").GetString());
        Assert.True(image.GetProperty("tainted").GetBoolean());
    }

    [Fact]
    public async Task Error_statuses_are_results_not_failures()
    {
        using var web = new WebFetchTool(new CannedHandler(_ => CannedHandler.Page("not here", "text/plain", HttpStatusCode.NotFound)));

        var result = await Invoke(web, "web_fetch", """{"url":"https://example.org/missing"}""");

        Assert.Equal(404, result.GetProperty("status").GetInt32());
        Assert.Equal("not here", result.GetProperty("content").GetString());
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::127.0.0.1", false)]
    [InlineData("::", false)]
    [InlineData("64:ff9b::10.0.0.1", false)]
    [InlineData("64:ff9b::8.8.8.8", true)]
    [InlineData("64:ff9b:1::c0a8:101", false)]
    [InlineData("2002:a9fe:a9fe::1", false)]
    [InlineData("2002:808:808::1", true)]
    public void Only_public_addresses_are_reached(string address, bool isPublic) =>
        Assert.Equal(isPublic, WebFetchTool.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public async Task The_default_handler_does_not_follow_redirects_itself()
    {
        // A local server, so this needs no network; private addresses are allowed for the test only.
        using var listener = new HttpListener();
        var port = Random.Shared.Next(20_000, 60_000);
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var served = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var context = await listener.GetContextAsync();
                if (context.Request.Url!.AbsolutePath == "/start")
                {
                    context.Response.StatusCode = 302;
                    context.Response.RedirectLocation = "/end";
                }
                else
                {
                    var body = Encoding.UTF8.GetBytes("arrived");
                    context.Response.ContentType = "text/plain";
                    await context.Response.OutputStream.WriteAsync(body);
                }

                context.Response.Close();
            }
        }, Ct);
        using var web = new WebFetchTool(options: WebFetchOptions.Default with { AllowPrivateNetworks = true });

        var result = await Invoke(web, "web_fetch", $$"""{"url":"http://127.0.0.1:{{port}}/start"}""");

        await served.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal($"http://127.0.0.1:{port}/end", result.GetProperty("final_url").GetString());
        Assert.Equal("arrived", result.GetProperty("content").GetString());
    }
}
