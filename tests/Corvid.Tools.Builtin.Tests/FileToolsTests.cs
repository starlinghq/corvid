using System.Text.Json;
using static Corvid.Tools.Builtin.Tests.Fixtures;

namespace Corvid.Tools.Builtin.Tests;

public sealed class FileToolsTests : IDisposable
{
    private readonly TempTree _tree = new();
    private readonly string _workspace;
    private readonly FileTools _tools;

    public FileToolsTests()
    {
        _workspace = _tree.Path("work");
        Directory.CreateDirectory(_workspace);
        _tools = new FileTools(new Workspace(_workspace, [_tree.Path("work", "vault-data")]),
            FileToolOptions.Default with { MaxReadChars = 200, MaxListEntries = 6, MaxSearchResults = 3 });
    }

    public void Dispose() => _tree.Dispose();

    [Fact]
    public void Risk_metadata_matches_what_each_tool_does()
    {
        var tools = _tools.CreateTools().ToDictionary(tool => tool.Name, tool => tool.Descriptor.Risk);

        Assert.True(tools["read_file"].ReadOnly);
        Assert.True(tools["list_files"].ReadOnly);
        Assert.True(tools["search_files"].ReadOnly);
        Assert.Equal(new ToolRisk(SideEffects.Filesystem, Network: false, Destructive: true), tools["write_file"]);
        Assert.Equal(new ToolRisk(SideEffects.Filesystem, Network: false, Destructive: false), tools["patch_file"]);
        // C-007b finds a file tool's target by the parameter's name.
        Assert.All(_tools.CreateTools().Where(tool => tool.Name != "search_files"),
            tool => Assert.Contains(tool.Descriptor.Parameters, parameter => parameter.Name == "path"));
    }

    [Fact]
    public async Task Reading_numbers_lines_and_continues_where_it_stopped()
    {
        _tree.Write("work/notes.txt", string.Join('\n', Enumerable.Range(1, 30).Select(i => $"line {i} " + new string('x', 20))) + "\n");

        var first = await Invoke(_tools, "read_file", """{"path":"notes.txt"}""");
        Assert.Equal(30, first.GetProperty("total_lines").GetInt32());
        Assert.StartsWith("1\tline 1 ", first.GetProperty("content").GetString(), StringComparison.Ordinal);
        var next = first.GetProperty("next_start_line").GetInt32();
        Assert.True(next > 1 && next < 30);

        var rest = await Invoke(_tools, "read_file", $$"""{"path":"notes.txt","start_line":{{next}},"max_lines":3}""");
        Assert.StartsWith($"{next}\tline {next} ", rest.GetProperty("content").GetString(), StringComparison.Ordinal);
        Assert.Equal(next + 2, rest.GetProperty("end_line").GetInt32());
    }

    [Fact]
    public async Task Binary_files_are_not_read()
    {
        File.WriteAllBytes(_tree.Path("work", "image.bin"), [1, 2, 0, 4]);

        var error = await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(_tools, "read_file", """{"path":"image.bin"}"""));
        Assert.Contains("binary", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("~/.ssh/id_ed25519")]
    [InlineData("sub/../../outside.txt")]
    [InlineData(".env")]
    [InlineData("config/.env.production")]
    [InlineData("keys/server.pem")]
    [InlineData("vault-data/store.bin")]
    public async Task Paths_outside_the_workspace_protected_or_secret_are_refused(string path)
    {
        _tree.Write("outside.txt", "outside");
        _tree.Write("work/.env", "SECRET=1");
        _tree.Write("work/config/.env.production", "SECRET=2");
        _tree.Write("work/keys/server.pem", "-----BEGIN PRIVATE KEY-----");
        _tree.Write("work/vault-data/store.bin", "sealed");
        var arguments = JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = path });

        await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(_tools, "read_file", arguments));
        var write = JsonSerializer.Serialize(new Dictionary<string, string> { ["path"] = path, ["content"] = "x" });
        await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(_tools, "write_file", write));
        Assert.Equal("outside", File.ReadAllText(_tree.Path("outside.txt")));
    }

    [Fact]
    public async Task Symbolic_links_cannot_lead_out_of_the_workspace()
    {
        _tree.Write("secret/data.txt", "outside the workspace");
        Directory.CreateSymbolicLink(_tree.Path("work", "escape"), _tree.Path("secret"));
        File.CreateSymbolicLink(_tree.Path("work", "file-link"), _tree.Path("secret", "data.txt"));
        // A link that stays inside is fine.
        _tree.Write("work/real/inside.txt", "inside");
        Directory.CreateSymbolicLink(_tree.Path("work", "alias"), _tree.Path("work", "real"));

        await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(_tools, "read_file", """{"path":"escape/data.txt"}"""));
        await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(_tools, "read_file", """{"path":"file-link"}"""));
        await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(_tools, "write_file", """{"path":"escape/new.txt","content":"x"}"""));
        Assert.False(File.Exists(_tree.Path("secret", "new.txt")));
        Assert.Equal("1\tinside\n", (await Invoke(_tools, "read_file", """{"path":"alias/inside.txt"}""")).GetProperty("content").GetString());
        // Listings show links but never walk through them.
        var listing = (await Invoke(_tools, "list_files", """{"recursive":true}""")).GetProperty("entries").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain(listing, entry => entry!.Contains("data.txt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Listing_hides_secrets_and_skipped_directories_and_stops_at_the_limit()
    {
        _tree.Write("work/README.md", "hello");
        _tree.Write("work/.env", "SECRET=1");
        _tree.Write("work/.git/config", "[core]");
        _tree.Write("work/src/app.cs", "class App {}");

        var flat = await Invoke(_tools, "list_files", "{}");
        Assert.Equal(["README.md (5 B)", "src/"], flat.GetProperty("entries").EnumerateArray().Select(e => e.GetString()));
        var deep = await Invoke(_tools, "list_files", """{"path":".","recursive":true}""");
        Assert.Equal(["README.md (5 B)", "src/", "src/app.cs (12 B)"], deep.GetProperty("entries").EnumerateArray().Select(e => e.GetString()));
        Assert.False(deep.GetProperty("truncated").GetBoolean());

        for (var i = 0; i < 10; i++)
            _tree.Write($"work/many/{i}.txt", "x");
        Assert.True((await Invoke(_tools, "list_files", """{"path":"many"}""")).GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task Search_finds_text_and_regular_expressions_but_not_secrets()
    {
        _tree.Write("work/a.txt", "Dentist on Friday\nnothing\n");
        _tree.Write("work/sub/b.md", "call the DENTIST\n");
        _tree.Write("work/.env", "DENTIST_TOKEN=secret");
        File.WriteAllBytes(_tree.Path("work", "blob.bin"), [0, (byte)'d', (byte)'e', (byte)'n']);

        var plain = await Invoke(_tools, "search_files", """{"pattern":"dentist"}""");
        Assert.Equal(["a.txt:1: Dentist on Friday", "sub/b.md:1: call the DENTIST"], plain.GetProperty("matches").EnumerateArray().Select(e => e.GetString()));
        var exact = await Invoke(_tools, "search_files", """{"pattern":"dentist","ignore_case":false}""");
        Assert.Empty(exact.GetProperty("matches").EnumerateArray());
        var regex = await Invoke(_tools, "search_files", """{"pattern":"^call .*ST$","regex":true,"path":"sub"}""");
        Assert.Single(regex.GetProperty("matches").EnumerateArray());
        await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(_tools, "search_files", """{"pattern":"(","regex":true}"""));
    }

    [Fact]
    public async Task Writing_creates_directories_and_replaces_atomically()
    {
        var created = await Invoke(_tools, "write_file", """{"path":"docs/plan.md","content":"# Plan\n"}""");
        Assert.True(created.GetProperty("created").GetBoolean());
        Assert.Equal("# Plan\n", File.ReadAllText(_tree.Path("work", "docs", "plan.md")));

        var replaced = await Invoke(_tools, "write_file", """{"path":"docs/plan.md","content":"# Plan v2\n"}""");
        Assert.False(replaced.GetProperty("created").GetBoolean());
        Assert.Equal("# Plan v2\n", File.ReadAllText(_tree.Path("work", "docs", "plan.md")));
        Assert.Equal(["plan.md"], Directory.GetFiles(_tree.Path("work", "docs")).Select(Path.GetFileName));
        await Assert.ThrowsAsync<ToolArgumentException>(() => Invoke(_tools, "write_file", """{"path":"docs","content":"x"}"""));
    }

    [Fact]
    public async Task Patching_replaces_exactly_one_occurrence_unless_told_otherwise()
    {
        _tree.Write("work/config.json", "{\r\n  \"port\": 8080,\r\n  \"debug\": true,\r\n  \"debug2\": true\r\n}");

        // The model's LF text matches a CRLF file, and the file keeps its line endings.
        await Invoke(_tools, "patch_file", """{"path":"config.json","old_text":"\"port\": 8080,\n","new_text":"\"port\": 9090,\n"}""");
        Assert.Contains("\"port\": 9090,\r\n", File.ReadAllText(_tree.Path("work", "config.json")), StringComparison.Ordinal);

        var ambiguous = await Assert.ThrowsAsync<ToolArgumentException>(() =>
            Invoke(_tools, "patch_file", """{"path":"config.json","old_text":"true","new_text":"false"}"""));
        Assert.Contains("2 times", ambiguous.Message, StringComparison.Ordinal);
        var all = await Invoke(_tools, "patch_file", """{"path":"config.json","old_text":"true","new_text":"false","replace_all":true}""");
        Assert.Equal(2, all.GetProperty("replacements").GetInt32());
        await Assert.ThrowsAsync<ToolArgumentException>(() =>
            Invoke(_tools, "patch_file", """{"path":"config.json","old_text":"missing","new_text":"x"}"""));
        await Assert.ThrowsAsync<ToolArgumentException>(() =>
            Invoke(_tools, "patch_file", """{"path":"nope.json","old_text":"a","new_text":"b"}"""));
    }

    [Fact]
    public void The_workspace_must_exist_and_be_absolute()
    {
        Assert.Throws<ArgumentException>(() => new Workspace("relative/dir"));
        Assert.Throws<DirectoryNotFoundException>(() => new Workspace(_tree.Path("missing")));
    }
}
