using System.Text.Json;
using Corvid.Tools;
using ModelContextProtocol.Protocol;

namespace Corvid.Mcp.Tests;

/// <summary>C-022: tool definitions pinned on first listing, and new or changed ones held until the user accepts them.</summary>
public sealed class McpPinningTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-pins-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static McpServerDefinition Server(string name = "fake", McpTrust trust = McpTrust.Untrusted) => new()
    {
        Name = name,
        Command = "fake-server",
        Workspace = "/tmp/unused",
        Trust = trust,
        StartTimeout = TimeSpan.FromSeconds(10),
        CallTimeout = TimeSpan.FromSeconds(10),
    };

    /// <summary>One process start: a fresh registry and clients over the shared store, started and listed.</summary>
    private static async Task<(McpClients Clients, ToolRegistry Registry)> StartAsync(FakeMcpServer fake, IMcpPinStore store,
        List<McpDefinitionNotice>? notices = null, McpServerDefinition? server = null)
    {
        var registry = new ToolRegistry();
        var clients = new McpClients([server ?? Server()], fake, registry, store, notice =>
        {
            if (notices is not null)
                lock (notices)
                    notices.Add(notice);
        });
        clients.Start();
        await clients.Started.WaitAsync(TimeSpan.FromSeconds(30), Token);
        return (clients, registry);
    }

    private static JsonElement Json(string text)
    {
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    private static Tool Find(FakeMcpServer fake, string name) => fake.Tools.Single(tool => tool.Name == name);

    [Fact]
    public async Task TheFirstListingPinsAndRegistersEveryTool()
    {
        var store = new InMemoryMcpPinStore();
        await using var fake = new FakeMcpServer().Add("one").Add("two");
        var (clients, registry) = await StartAsync(fake, store);
        await using var _ = clients;

        Assert.Equal(["fake__one", "fake__two"], registry.List().Select(tool => tool.Name));
        var pins = store.Pins("fake");
        Assert.Equal(["one", "two"], pins.Keys.Order(StringComparer.Ordinal));
        Assert.All(pins.Values, pin => Assert.Equal(64, pin.Hash.Length));
        Assert.Equal(McpToolDefinition.From(Find(fake, "one")).Canonical, pins["one"].Definition);
        Assert.Empty(clients.Status()[0].Held);
    }

    [Fact]
    public async Task AChangedDescriptionSchemaOrAnnotationHoldsOnlyThatTool()
    {
        var store = new InMemoryMcpPinStore();
        await using var fake = new FakeMcpServer().Add("described").Add("schema").Add("annotated").Add("steady");
        await using (var _ = (await StartAsync(fake, store)).Clients)
        {
        }

        Find(fake, "described").Description = "A test tool, now different.";
        Find(fake, "schema").InputSchema = Json("""{"type":"object","properties":{"text":{"type":"string"},"extra":{"type":"string"}}}""");
        Find(fake, "annotated").Annotations = new ToolAnnotations { DestructiveHint = false };
        var notices = new List<McpDefinitionNotice>();
        var (clients, registry) = await StartAsync(fake, store, notices);
        await using var __ = clients;

        Assert.Equal(["fake__steady"], registry.List().Select(tool => tool.Name));
        var status = clients.Status()[0];
        Assert.Equal(["annotated", "described", "schema"], status.Held.Select(held => held.Tool).Order(StringComparer.Ordinal));
        Assert.All(status.Held, held => Assert.Equal(McpHoldReasons.Changed, held.Reason));
        Assert.Contains("'described': held, its definition changed since it was pinned (/mcp diff fake described; /mcp accept fake described)", status.Issues);
        Assert.Equal(3, notices.Count(notice => notice.Kind == McpPinKinds.Changed && notice.PinnedHash is not null && notice.PinnedHash != notice.Hash));
    }

    [Fact]
    public async Task AnEquivalentSchemaWithReorderedKeysIsNotHeld()
    {
        var store = new InMemoryMcpPinStore();
        await using var fake = new FakeMcpServer().Add("t", schema: """{"type":"object","properties":{"a":{"type":"string","description":"A."},"b":{"type":"number"}},"required":["a"]}""");
        await using (var _ = (await StartAsync(fake, store)).Clients)
        {
        }

        Find(fake, "t").InputSchema = Json("""{ "required": ["a"], "properties": { "b": {"type":"number"}, "a": {"description":"A.", "type":"string"} }, "type": "object" }""");
        var (clients, registry) = await StartAsync(fake, store);
        await using var __ = clients;

        Assert.Equal(["fake__t"], registry.List().Select(tool => tool.Name));
        Assert.Empty(clients.Status()[0].Held);
    }

    [Fact]
    public async Task ANewToolAfterPinningIsHeldAndARemovedOneDisappearsWithItsPinKept()
    {
        var store = new InMemoryMcpPinStore();
        await using var fake = new FakeMcpServer().Add("kept").Add("removed");
        await using (var _ = (await StartAsync(fake, store)).Clients)
        {
        }

        fake.Tools.RemoveAll(tool => tool.Name == "removed");
        fake.Add("added");
        var notices = new List<McpDefinitionNotice>();
        var (clients, registry) = await StartAsync(fake, store, notices);
        await using var __ = clients;

        Assert.Equal(["fake__kept"], registry.List().Select(tool => tool.Name));
        var held = Assert.Single(clients.Status()[0].Held);
        Assert.Equal(("added", McpHoldReasons.New, null), (held.Tool, held.Reason, held.Pin));
        Assert.Contains(clients.Status()[0].Issues, issue => issue.StartsWith("'added': held, new since", StringComparison.Ordinal));
        Assert.Equal(["kept", "removed"], store.Pins("fake").Keys.Order(StringComparer.Ordinal));
        Assert.Equal(McpPinKinds.New, Assert.Single(notices).Kind);
    }

    [Fact]
    public async Task AZeroWidthCharacterAddedToADescriptionIsAChange()
    {
        // The cleaned text the model would see is the same; the server's listing is not, so it is held.
        var store = new InMemoryMcpPinStore();
        await using var fake = new FakeMcpServer().Add("t", "Send a message.");
        await using (var _ = (await StartAsync(fake, store)).Clients)
        {
        }

        Find(fake, "t").Description = "Send a​ message.";
        var (clients, registry) = await StartAsync(fake, store);
        await using var __ = clients;

        Assert.Empty(registry.List());
        Assert.Equal(McpHoldReasons.Changed, Assert.Single(clients.Status()[0].Held).Reason);
    }

    [Fact]
    public async Task AcceptRegistersTheHeldDefinitionAndTheNextStartOffersItUnheld()
    {
        var store = new InMemoryMcpPinStore();
        await using var fake = new FakeMcpServer().Add("t", "Old text.");
        await using (var _ = (await StartAsync(fake, store)).Clients)
        {
        }

        Find(fake, "t").Description = "New text.";
        var notices = new List<McpDefinitionNotice>();
        var (clients, registry) = await StartAsync(fake, store, notices);
        await using (clients)
        {
            Assert.Empty(registry.List());
            var held = Assert.Single(clients.Status()[0].Held);
            Assert.Equal("Old text.", McpToolDefinition.Field(held.Pin!.Definition, "description"));
            Assert.Equal("New text.", McpToolDefinition.Field(held.Definition.Canonical, "description"));

            Assert.False(clients.Accept("fake", "t", allowFlagged: true).Accepted);
            Assert.False(clients.Accept("fake", "missing").Accepted);
            Assert.False(clients.Accept("nobody", "t").Accepted);
            var accepted = clients.Accept("fake", "t");
            Assert.True(accepted.Accepted, accepted.Message);
            Assert.Equal("fake__t", accepted.Name);
            Assert.Equal("New text.", Assert.Single(registry.List()).Description);
            var status = clients.Status()[0];
            Assert.Empty(status.Held);
            Assert.Equal(["fake__t"], status.Tools);
            Assert.DoesNotContain(status.Issues, issue => issue.Contains("held", StringComparison.Ordinal));
            Assert.Equal([McpPinKinds.Changed, McpPinKinds.Accepted], notices.Select(notice => notice.Kind));
            Assert.Equal(held.Definition.Hash, store.Pins("fake")["t"].Hash);
        }

        var (again, next) = await StartAsync(fake, store);
        await using var disposeAgain = again;
        Assert.Equal(["fake__t"], next.List().Select(tool => tool.Name));
        Assert.Empty(again.Status()[0].Held);
    }

    [Fact]
    public async Task AnUnreadablePinFileOffersNothing()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "mcp-pins.json");
        await File.WriteAllTextAsync(path, "{not json", Token);
        await using var fake = new FakeMcpServer().Add("t");
        var (clients, registry) = await StartAsync(fake, new JsonFileMcpPinStore(path));
        await using var _ = clients;

        Assert.Empty(registry.List());
        Assert.Contains(clients.Status()[0].Issues, issue => issue.Contains("cannot be read", StringComparison.Ordinal));
    }

    [Fact]
    public void TheJsonFileStoreRoundTripsAndWritesAtomically()
    {
        var path = Path.Combine(_root, "data", "mcp-pins.json");
        var store = new JsonFileMcpPinStore(path);
        Assert.Empty(store.Pins("fake"));
        var definition = McpToolDefinition.From(new Tool { Name = "t", Description = "D." });
        var at = new DateTimeOffset(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);
        store.Set("fake", new Dictionary<string, McpToolPin> { ["t"] = new(definition.Hash, definition.Canonical, at, definition.Hash) });

        Assert.False(File.Exists(path + ".tmp"));
        var text = File.ReadAllText(path);
        Assert.Contains("\"format\": \"corvid-mcp-pins\"", text, StringComparison.Ordinal);
        Assert.Contains("\"format_version\": 1", text, StringComparison.Ordinal);
        var pin = new JsonFileMcpPinStore(path).Pins("fake")["t"];
        Assert.Equal(new McpToolPin(definition.Hash, definition.Canonical, at, definition.Hash), pin);
    }

    [Theory]
    [InlineData("""{"format":"corvid-approval-rules","format_version":1,"servers":{}}""")]
    [InlineData("""{"format":"corvid-mcp-pins","format_version":2,"servers":{}}""")]
    [InlineData("""{"format":"corvid-mcp-pins","format_version":1,"servers":{},"extra":true}""")]
    [InlineData("""{"format":"corvid-mcp-pins","format_version":1,"servers":{"s":{"t":{"hash":"short","definition":"{}","pinned_at":"2026-10-21T00:00:00Z","allowed_flagged":null}}}}""")]
    public void ABadPinFileIsRefused(string json)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "bad.json");
        File.WriteAllText(path, json);
        var store = new JsonFileMcpPinStore(path);
        Assert.Throws<InvalidDataException>(() => store.Pins("s"));
        Assert.Throws<InvalidDataException>(() => store.Set("s", new Dictionary<string, McpToolPin>()));
    }

    [Fact]
    public void TwoWritersMergeByServerAndTool()
    {
        // corvid mcp list and a running chat each hold their own store over the same file.
        var path = Path.Combine(_root, "mcp-pins.json");
        var chat = new JsonFileMcpPinStore(path);
        var list = new JsonFileMcpPinStore(path);
        McpToolPin Pin(string name)
        {
            var definition = McpToolDefinition.From(new Tool { Name = name });
            return new McpToolPin(definition.Hash, definition.Canonical, DateTimeOffset.UnixEpoch);
        }

        chat.Set("notes", new Dictionary<string, McpToolPin> { ["add"] = Pin("add") });
        list.Set("github", new Dictionary<string, McpToolPin> { ["issue"] = Pin("issue") });
        list.Set("notes", new Dictionary<string, McpToolPin> { ["save"] = Pin("save") });
        chat.Set("notes", new Dictionary<string, McpToolPin> { ["add"] = Pin("add2") });

        var fresh = new JsonFileMcpPinStore(path);
        Assert.Equal(["add", "save"], fresh.Pins("notes").Keys.Order(StringComparer.Ordinal));
        Assert.Equal(Pin("add2").Hash, fresh.Pins("notes")["add"].Hash);
        Assert.Equal(["issue"], fresh.Pins("github").Keys);
    }

    [Fact]
    public void TheCanonicalDefinitionSortsKeysKeepsArraysAndNumbersAndHashesRawText()
    {
        var one = McpToolDefinition.From(new Tool
        {
            Name = "t",
            Description = "D.",
            InputSchema = Json("""{"type":"object","properties":{"n":{"type":"number","maximum":1.50}},"required":["n","m"]}"""),
            Annotations = new ToolAnnotations { ReadOnlyHint = true },
        });
        Assert.Equal(
            """{"annotations":{"readOnlyHint":true},"description":"D.","inputSchema":{"properties":{"n":{"maximum":1.50,"type":"number"}},"required":["n","m"],"type":"object"},"name":"t","title":null}""",
            one.Canonical);
        var reordered = McpToolDefinition.From(new Tool
        {
            Name = "t",
            Description = "D.",
            InputSchema = Json("""{"required":["m","n"],"type":"object","properties":{"n":{"type":"number","maximum":1.50}}}"""),
            Annotations = new ToolAnnotations { ReadOnlyHint = true },
        });
        Assert.NotEqual(one.Hash, reordered.Hash);
    }
}
