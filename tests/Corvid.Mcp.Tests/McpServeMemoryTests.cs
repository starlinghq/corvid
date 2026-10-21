using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Memory;
using Corvid.Tools.Builtin;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Corvid.Mcp.Tests;

/// <summary>
/// C-028: project memory over <c>corvid mcp serve</c>. Lessons only when approved, not local-only and of a project in scope;
/// a caller's project argument mapped onto known projects by string work alone; one answer for hidden and missing ids;
/// every call reported.
/// </summary>
public sealed class McpServeMemoryTests : IDisposable
{
    private static readonly string Widgets = ProjectIds.FromRemote("https://github.com/example/widgets.git");
    private static readonly string Gadgets = ProjectIds.FromRemote("git@github.com:example/gadgets.git");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-mcp-memory-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly MemoryDatabase _database;
    private readonly FactGraph _facts;
    private readonly EpisodicIndex _episodes;
    private readonly LessonStore _lessons;
    private readonly List<McpServeCall> _calls = [];

    public McpServeMemoryTests()
    {
        Directory.CreateDirectory(_root);
        _database = new MemoryDatabase(Path.Combine(_root, "memory.db"));
        _facts = new FactGraph(_database);
        _episodes = new EpisodicIndex(_database);
        _lessons = new LessonStore(_database);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Paths the tests name but never create: mapping is lexical, so none of them needs to exist.</summary>
    private string Home => Path.Combine(_root, "home", "sam");

    private string Workspace => Path.Combine(Home, "src", "widgets");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    internal static Lesson Approve(LessonStore store, string project, string kind, string title, string body, bool localOnly = false,
        ImmutableArray<string>? tags = null)
    {
        var lesson = File(store, project, kind, title, body, localOnly, tags);
        Assert.True(store.Approve(lesson.LessonId, "ok"));
        return store.Get(lesson.LessonId)!;
    }

    private static Lesson File(LessonStore store, string project, string kind, string title, string body, bool localOnly = false,
        ImmutableArray<string>? tags = null) =>
        store.File(new Lesson(Lesson.IdFor(project, kind, title, body), project, kind, title, "when building the widgets", body,
            tags ?? ["build"], LessonStates.Candidate, null, LessonAuthors.Agent, null, null, localOnly, default, null,
            [new LessonEvidence("s-1", [3, 4], "the build failed", new string('a', 64))], "s-1")).Lesson;

    private SharedMemoryTools Tools(bool projectMemory = true, bool conversations = false, ProjectIdentity? workspace = null) =>
        new(new HybridRetriever(_episodes, _facts, lessons: projectMemory ? _lessons : null), new SharedMemoryOptions
        {
            Facts = _facts,
            Episodes = _episodes,
            IncludeConversations = conversations,
            ProjectMemory = projectMemory,
            Lessons = projectMemory ? _lessons : null,
            Workspace = Workspace,
            WorkspaceProject = workspace ?? new ProjectIdentity(Widgets, ProjectSources.Remote),
            Home = Home,
        });

    private async Task<McpClient> ConnectAsync(SharedMemoryTools tools) =>
        await McpServeTests.ConnectAsync(new CorvidMcpServer(tools.ServedTools(), new McpServeOptions { Calls = _calls.Add }));

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: Token);
        Assert.True(result.IsError != true, ((TextContentBlock)result.Content[0]).Text);
        return result.StructuredContent!.Value;
    }

    private static async Task<string> ErrorAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: Token);
        Assert.True(result.IsError);
        return ((TextContentBlock)Assert.Single(result.Content)).Text;
    }

    private static string[] Ids(JsonElement result) =>
        [.. result.GetProperty("results").EnumerateArray().Select(item => item.GetProperty("id").GetString()!)];

    [Fact]
    public async Task OffByDefaultOnlySearchIsServedAndItKnowsNoLessonsOrProjects()
    {
        Approve(_lessons, Widgets, LessonKinds.Gotcha, "Hooks binary first", "Build the hooks binary before the tests.");
        await using var client = await ConnectAsync(Tools(projectMemory: false));
        var tools = await client.ListToolsAsync(cancellationToken: Token);
        Assert.Equal(["search_memory"], tools.Select(tool => tool.Name));

        Assert.Contains("must be one of: fact.", await ErrorAsync(client, "search_memory", new() { ["query"] = "hooks", ["kind"] = "lesson" }));
        Assert.Contains("'project'", await ErrorAsync(client, "search_memory", new() { ["query"] = "hooks", ["project"] = Widgets }));
        var result = await CallAsync(client, "search_memory", new() { ["query"] = "hooks binary" });
        Assert.Empty(Ids(result));
        Assert.False(result.TryGetProperty("project", out _));
    }

    [Fact]
    public async Task SearchServesOnlyApprovedSharedLessonsOfTheWorkspaceProject()
    {
        var approved = Approve(_lessons, Widgets, LessonKinds.Gotcha, "Hooks binary approved", "Build the hooks binary before the tests.");
        var candidate = File(_lessons, Widgets, LessonKinds.Gotcha, "Hooks binary candidate", "Delete the hooks binary to save space.");
        var rejected = File(_lessons, Widgets, LessonKinds.Gotcha, "Hooks binary rejected", "Ignore the hooks binary.");
        Assert.True(_lessons.Reject(rejected.LessonId, "wrong"));
        var old = Approve(_lessons, Widgets, LessonKinds.Workflow, "Hooks binary old", "Copy the hooks binary by hand.");
        var replacement = File(_lessons, Widgets, LessonKinds.Workflow, "Hooks binary new", "Run make build-hooks for the hooks binary.");
        Assert.True(_lessons.Approve(replacement.LessonId, "ok", supersedes: old.LessonId));
        var local = Approve(_lessons, Widgets, LessonKinds.Gotcha, "Hooks binary local", "The hooks binary key is in ~/secret.", localOnly: true);
        _facts.Add(new FactInput("The hooks binary is called hooks.bin"));

        await using var client = await ConnectAsync(Tools());
        var all = await CallAsync(client, "search_memory", new() { ["query"] = "hooks binary", ["max_tokens"] = 4_000 });
        Assert.Equal("workspace", all.GetProperty("project").GetString());
        var ids = Ids(all);
        Assert.Contains(approved.LessonId, ids);
        Assert.Contains(replacement.LessonId, ids);
        Assert.Contains(ids, id => id.StartsWith("fact:", StringComparison.Ordinal));
        Assert.DoesNotContain(candidate.LessonId, ids);
        Assert.DoesNotContain(rejected.LessonId, ids);
        Assert.DoesNotContain(old.LessonId, ids);
        Assert.DoesNotContain(local.LessonId, ids);
        Assert.DoesNotContain("secret", all.GetRawText());

        var lessons = await CallAsync(client, "search_memory", new() { ["query"] = "hooks binary", ["kind"] = "lesson" });
        Assert.All(lessons.GetProperty("results").EnumerateArray(), item => Assert.Equal("lesson", item.GetProperty("kind").GetString()));
        var gotcha = Assert.Single(lessons.GetProperty("results").EnumerateArray(), item => item.GetProperty("id").GetString() == approved.LessonId);
        Assert.Equal("gotcha", gotcha.GetProperty("lesson_kind").GetString());
        Assert.Equal("Hooks binary approved", gotcha.GetProperty("title").GetString());
        Assert.Equal(["build"], gotcha.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));

        // A lesson kind filters lessons; limit caps the list.
        Assert.Equal([approved.LessonId], Ids(await CallAsync(client, "search_memory", new() { ["query"] = "hooks binary", ["kind"] = "gotcha" })));
        Assert.Single(Ids(await CallAsync(client, "search_memory", new() { ["query"] = "hooks binary", ["limit"] = 1 })));
        Assert.Contains("between 1 and 20", await ErrorAsync(client, "search_memory", new() { ["query"] = "hooks", ["limit"] = 21 }));
        Assert.Contains("'kind'", await ErrorAsync(client, "search_memory", new() { ["query"] = "hooks", ["kind"] = "conversation" }));
        Assert.Contains("'kind'", await ErrorAsync(client, "search_memory", new() { ["query"] = "hooks", ["kind"] = "secrets" }));
    }

    [Fact]
    public async Task AnotherKnownProjectsLessonsNeedThatProjectAsTheArgument()
    {
        var gadget = Approve(_lessons, Gadgets, LessonKinds.Convention, "Gadget builds use ninja", "Build the gadgets with ninja.");
        await using var client = await ConnectAsync(Tools());

        Assert.DoesNotContain(gadget.LessonId, Ids(await CallAsync(client, "search_memory", new() { ["query"] = "gadgets ninja" })));
        foreach (var project in new[] { "https://github.com/example/gadgets", "ssh://git@github.com/example/gadgets.git", Gadgets, Gadgets.ToUpperInvariant() })
        {
            var result = await CallAsync(client, "search_memory", new() { ["query"] = "gadgets ninja", ["project"] = project });
            Assert.Equal("argument", result.GetProperty("project").GetString());
            Assert.Equal([gadget.LessonId], Ids(result));
        }
        // An unknown project is not an error: facts only, with the same source as a match, so the tool is no oracle for which
        // projects exist.
        var unknown = await CallAsync(client, "search_memory", new() { ["query"] = "gadgets ninja", ["project"] = "https://github.com/example/nothing" });
        Assert.Equal("argument", unknown.GetProperty("project").GetString());
        Assert.Empty(Ids(unknown));

        // get_memory honours the same scope: another project's lesson is not there.
        Assert.StartsWith($"No memory with id '{gadget.LessonId}'.", await ErrorAsync(client, "get_memory", new() { ["id"] = gadget.LessonId }));
        var fetched = await CallAsync(client, "get_memory", new() { ["id"] = gadget.LessonId, ["project"] = Gadgets });
        Assert.Equal("Build the gadgets with ninja.", fetched.GetProperty("body").GetString());
    }

    [Fact]
    public async Task AProjectWithOnlyLocalOnlyLessonsLooksLikeAnUnknownOne()
    {
        // Review of C-028: a project whose approved lessons are all local-only must answer exactly as one never seen, in every
        // tool, so a caller cannot learn that local-only memory exists for it.
        var secret = ProjectIds.FromRemote("https://github.com/acme/secret-merger");
        var hidden = Approve(_lessons, secret, LessonKinds.Gotcha, "Merger codename", "The merger is codenamed Falcon.", localOnly: true);
        _facts.Add(new FactInput("Sam prefers rebase over merge"));
        await using var client = await ConnectAsync(Tools());

        async Task<string> AnswerAsync(string project)
        {
            var search = await CallAsync(client, "search_memory", new() { ["query"] = "zzz", ["project"] = project });
            var merger = await CallAsync(client, "search_memory", new() { ["query"] = "merger codenamed", ["project"] = project });
            var context = await CallAsync(client, "get_memory_context", new() { ["task"] = "merger rebase", ["project"] = project });
            var error = await ErrorAsync(client, "get_memory", new() { ["id"] = hidden.LessonId, ["project"] = project });
            return string.Join("|", search.GetProperty("project").GetString(), string.Join(",", Ids(search)),
                merger.GetProperty("project").GetString(), string.Join(",", Ids(merger)),
                context.GetProperty("project").GetString(), string.Join(",", Ids(context)), error);
        }

        var secretAnswer = await AnswerAsync("https://github.com/acme/secret-merger");
        Assert.Equal(await AnswerAsync("https://github.com/acme/other"), secretAnswer);
        Assert.Equal(secretAnswer, await AnswerAsync(secret));
        Assert.DoesNotContain("Falcon", secretAnswer, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, _lessons.KnownProjects());
    }

    [Fact]
    public void CallerPathsMapLexicallyOntoProjectIdsWithoutTheDisk()
    {
        var repo = Path.Combine(_root, "repo");
        Assert.False(Directory.Exists(repo));
        // An ancestor walk: /repo/src stands for a checkout-keyed /repo.
        Assert.Contains(ProjectIds.FromPath(repo), ProjectIds.Candidates(Path.Combine(repo, "src"), Home));
        Assert.Equal(ProjectIds.FromPath(Path.Combine(repo, "src")), ProjectIds.Candidates(Path.Combine(repo, "src"), Home)[0]);
        // .. segments are resolved as text, not followed.
        Assert.DoesNotContain(ProjectIds.FromPath(repo), ProjectIds.Candidates(Path.Combine(repo + "x", "..", "other", "src"), Home));
        Assert.Contains(ProjectIds.FromPath(Path.Combine(_root, "other")), ProjectIds.Candidates(Path.Combine(repo, "..", "other", "src"), Home));
        // Relative paths, NULs and overlong arguments stand for nothing.
        Assert.Empty(ProjectIds.Candidates("repo/src", Home));
        Assert.Empty(ProjectIds.Candidates("./src", Home));
        Assert.Empty(ProjectIds.Candidates("../src", Home));
        Assert.Empty(ProjectIds.Candidates(repo + "\0/src", Home));
        Assert.Empty(ProjectIds.Candidates("/" + new string('a', ProjectIds.MaxArgumentLength), Home));
        // Below $HOME the walk stops under it: nothing at or above home, unless the argument is home itself.
        var below = ProjectIds.Candidates(Path.Combine(Home, "notes", "x"), Home);
        Assert.Equal([ProjectIds.FromPath(Path.Combine(Home, "notes", "x")), ProjectIds.FromPath(Path.Combine(Home, "notes"))], below);
        Assert.Equal([ProjectIds.FromPath(Home)], ProjectIds.Candidates(Home, Home));
        // Under the workspace, the workspace's project joins where the walk passes the workspace.
        var under = ProjectIds.Candidates(Path.Combine(Workspace, "src", "deep"), Home, Workspace, Widgets);
        Assert.Equal(ProjectIds.FromPath(Workspace), under[2]);
        Assert.Equal(Widgets, under[3]);
        Assert.DoesNotContain(Widgets, ProjectIds.Candidates(Workspace + "-other", Home, Workspace, Widgets));
        // Remotes and ids are strings too.
        Assert.Equal([Widgets], ProjectIds.Candidates("git@github.com:example/widgets.git", Home));
        Assert.Equal([Widgets], ProjectIds.Candidates(" " + Widgets + " ", Home));
    }

    [Fact]
    public async Task ACallerPathMapsOnlyOntoKnownProjects()
    {
        var repo = Path.Combine(_root, "repo");
        var checkout = Approve(_lessons, ProjectIds.FromPath(repo), LessonKinds.Gotcha, "Repo needs the hooks binary", "Build hooks first in repo.");
        var widgets = Approve(_lessons, Widgets, LessonKinds.Gotcha, "Widgets need the hooks binary", "Build hooks first in widgets.");
        var above = Approve(_lessons, ProjectIds.FromPath(Path.GetDirectoryName(Home)!), LessonKinds.Gotcha, "Home parent hooks", "Hooks above home.");
        await using var client = await ConnectAsync(Tools());

        async Task<string> SearchAsync(string project)
        {
            var result = await CallAsync(client, "search_memory", new() { ["query"] = "hooks", ["kind"] = "lesson", ["project"] = project });
            return result.GetProperty("project").GetString() + ":" + string.Join(",", Ids(result));
        }

        Assert.Equal("argument:" + checkout.LessonId, await SearchAsync(Path.Combine(repo, "src", "does-not-exist")));
        Assert.Equal("argument:" + widgets.LessonId, await SearchAsync(Path.Combine(Workspace, "lib")));
        Assert.Equal("argument:", await SearchAsync("repo/src"));
        Assert.Equal("argument:", await SearchAsync(Path.Combine(Home, "elsewhere", "..", "notes")));
        // A parent of $HOME is a known project, but a path below $HOME never walks up to it.
        Assert.Equal("argument:", await SearchAsync(Path.Combine(Home, "notes")));
        Assert.Equal("argument:" + above.LessonId, await SearchAsync(Path.GetDirectoryName(Home)!));
    }

    [Fact]
    public async Task GetMemoryAnswersHiddenAndMissingIdsAlike()
    {
        var shared = _facts.Add(new FactInput("Sam's editor is Helix"));
        var local = _facts.Add(new FactInput("Sam's salary is 90k", LocalOnly: true));
        var candidate = File(_lessons, Widgets, LessonKinds.Gotcha, "Candidate", "Not reviewed.");
        var localLesson = Approve(_lessons, Widgets, LessonKinds.Gotcha, "Local", "Local only.", localOnly: true);
        var lesson = Approve(_lessons, Widgets, LessonKinds.Workflow, "Build order", string.Join(' ', Enumerable.Repeat("Step.", 1_500)),
            tags: ["build", "ci"]);
        await using var client = await ConnectAsync(Tools());

        var fact = await CallAsync(client, "get_memory", new() { ["id"] = FactId(shared) });
        Assert.Equal("Sam's editor is Helix", fact.GetProperty("text").GetString());
        Assert.Equal("fact", fact.GetProperty("kind").GetString());
        var errors = new List<string>();
        foreach (var id in new[] { FactId(local), "fact:" + new string('0', 32), candidate.LessonId, localLesson.LessonId, "lesson:nope", "episode-1" })
        {
            var error = await ErrorAsync(client, "get_memory", new() { ["id"] = id });
            Assert.StartsWith($"No memory with id '{id}'.", error, StringComparison.Ordinal);
            errors.Add(error.Replace(id, "<id>", StringComparison.Ordinal));
        }
        Assert.Single(errors.Distinct());

        var full = await CallAsync(client, "get_memory", new() { ["id"] = lesson.LessonId });
        Assert.Equal("workflow", full.GetProperty("lesson_kind").GetString());
        Assert.Equal("when building the widgets", full.GetProperty("applicability").GetString());
        Assert.Equal(["build", "ci"], full.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()));
        Assert.True(full.TryGetProperty("approved", out _));
        Assert.False(full.TryGetProperty("evidence", out _));
        // Longer than search's 200 tokens per item, cut at 1,000.
        var body = full.GetProperty("body").GetString()!;
        Assert.InRange(body.Length, 3_000, 4_000);
        Assert.EndsWith("…", body, StringComparison.Ordinal);
    }

    private static string FactId(Fact fact) => fact.FactId;

    [Fact]
    public async Task ContextReturnsAFewSharedFactsAndLessons()
    {
        _facts.Add(new FactInput("The widgets build uses make"));
        _facts.Add(new FactInput("The widgets signing key is in the vault", LocalOnly: true));
        var lesson = Approve(_lessons, Widgets, LessonKinds.Workflow, "Widgets build order", "Run make hooks before make widgets.");
        for (var i = 0; i < 8; i++)
            _facts.Add(new FactInput($"The widgets build note {i}"));
        await using var client = await ConnectAsync(Tools());

        var context = await CallAsync(client, "get_memory_context", new() { ["task"] = "widgets build" });
        Assert.Equal("workspace", context.GetProperty("project").GetString());
        var ids = Ids(context);
        Assert.Equal(5, ids.Length);
        Assert.Contains(lesson.LessonId, ids);
        Assert.DoesNotContain("signing key", context.GetRawText());
        Assert.Equal(2, Ids(await CallAsync(client, "get_memory_context", new() { ["task"] = "widgets build", ["limit"] = 2 })).Length);
        Assert.Contains("between 1 and 5", await ErrorAsync(client, "get_memory_context", new() { ["task"] = "widgets", ["limit"] = 6 }));

        // An unknown workspace: facts only, never another project's lessons.
        await using var elsewhere = await ConnectAsync(Tools(workspace: new ProjectIdentity(ProjectIds.FromPath("/nowhere"), ProjectSources.Path)));
        var facts = await CallAsync(elsewhere, "get_memory_context", new() { ["task"] = "widgets build" });
        Assert.Equal("none", facts.GetProperty("project").GetString());
        Assert.All(Ids(facts), id => Assert.StartsWith("fact:", id, StringComparison.Ordinal));
        Assert.DoesNotContain("signing key", facts.GetRawText());
    }

    [Fact]
    public async Task EveryCallIsReportedWithItsItemCount()
    {
        _facts.Add(new FactInput("Sam likes green tea"));
        var lesson = Approve(_lessons, Widgets, LessonKinds.Gotcha, "Tea breaks", "Green tea after the build.");
        await using var client = await ConnectAsync(Tools());

        await CallAsync(client, "search_memory", new() { ["query"] = "green tea" });
        await CallAsync(client, "get_memory", new() { ["id"] = lesson.LessonId });
        await ErrorAsync(client, "get_memory_context", new() { ["task"] = " " });

        Assert.Equal(["search_memory", "get_memory", "get_memory_context"], _calls.Select(call => call.Tool));
        Assert.Equal([false, false, true], _calls.Select(call => call.IsError));
        Assert.Equal(2, _calls[0].Result!.Value.GetProperty("results").GetArrayLength());
        Assert.Equal(lesson.LessonId, _calls[1].Result!.Value.GetProperty("id").GetString());
        Assert.Null(_calls[2].Result);
        Assert.All(_calls, call => Assert.True(call.Chars > 0));
        Assert.Equal(3, _calls.Select(call => call.CallId).Distinct().Count());
    }

    [Fact]
    public async Task ConversationsNeedTheirOwnSwitch()
    {
        await using var client = await ConnectAsync(Tools());
        Assert.Contains("'kind'", await ErrorAsync(client, "search_memory", new() { ["query"] = "tea", ["kind"] = "conversation" }));
        await using var sharing = await ConnectAsync(Tools(conversations: true));
        Assert.Empty(Ids(await CallAsync(sharing, "search_memory", new() { ["query"] = "tea", ["kind"] = "conversation" })));
    }
}
