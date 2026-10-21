using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Corvid.Abstractions.Tokens;
using Corvid.Fabric;
using Corvid.Memory;

namespace Corvid.Tools.Builtin;

/// <summary>What <see cref="SharedMemoryTools"/> may share, and the project it starts from.</summary>
public sealed record SharedMemoryOptions
{
    public required FactGraph Facts { get; init; }

    public required EpisodicIndex Episodes { get; init; }

    /// <summary>Also search and fetch earlier conversations (episodes), not only facts.</summary>
    public bool IncludeConversations { get; init; }

    /// <summary>
    /// C-028: <c>get_memory</c>, <c>get_memory_context</c>, lessons in <c>search_memory</c> and the <c>project</c> argument.
    /// Off, <c>search_memory</c> behaves as before C-028 apart from each result's <c>id</c>.
    /// </summary>
    public bool ProjectMemory { get; init; }

    /// <summary>Approved lessons, served for a project in scope; null for none.</summary>
    public LessonStore? Lessons { get; init; }

    /// <summary>The server's workspace: a caller path equal to or under it maps to <see cref="WorkspaceProject"/>.</summary>
    public string? Workspace { get; init; }

    /// <summary>The workspace's project, resolved by the host; the scope when the caller names none.</summary>
    public ProjectIdentity? WorkspaceProject { get; init; }

    /// <summary>The user's home directory, the ceiling of a caller path's ancestor walk.</summary>
    public string? Home { get; init; }
}

/// <summary>
/// Memory for another agent (C-018's MCP server mode, plan §12; C-028's project memory). Whoever asks is not Corvid's own
/// model, and its model may well be a cloud one, so local-only facts and lessons (plan §10.4) are never returned, and earlier
/// conversations, which hold raw tool results and outside content, are returned only when the user shares them. Lessons are
/// served only when approved and only for a project in scope; a caller's project argument is mapped onto projects the
/// store already knows by string work alone, never git or the file system. Read-only: nothing is written.
/// </summary>
public sealed partial class SharedMemoryTools
{
    /// <summary>The tools that exist only with <see cref="SharedMemoryOptions.ProjectMemory"/>.</summary>
    public static ImmutableArray<string> ProjectMemoryTools { get; } = ["get_memory", "get_memory_context"];

    /// <summary>The caller passed a project, whether or not it maps onto a known one: the field never tells which.</summary>
    public const string FromArgument = "argument";
    /// <summary>The scope's source when the server's workspace is a known project.</summary>
    public const string FromWorkspace = "workspace";
    /// <summary>No argument and no known workspace project: facts only.</summary>
    public const string FromNone = "none";

    internal const int MaxSearchLimit = 20;
    internal const int DefaultSearchLimit = 10;
    internal const int MaxContextLimit = 5;
    internal const int ContextTokens = 1_500;
    internal const int GetTokens = 1_000;

    private static readonly ITokenCounter Counter = CharsPerTokenCounter.Instance;

    private readonly HybridRetriever _retriever;
    private readonly SharedMemoryOptions _options;
    private readonly ImmutableArray<MemoryItemKind> _kinds;

    public SharedMemoryTools(HybridRetriever retriever, SharedMemoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        ArgumentNullException.ThrowIfNull(options);
        _retriever = retriever;
        _options = options;
        _kinds = options.IncludeConversations ? [MemoryItemKind.Fact, MemoryItemKind.Episode] : [MemoryItemKind.Fact];
    }

    /// <summary>The tools to serve: <see cref="ProjectMemoryTools"/> only with project memory on.</summary>
    public IReadOnlyList<ToolFunction> ServedTools() =>
        [.. CreateTools().Where(tool => _options.ProjectMemory || !ProjectMemoryTools.Contains(tool.Name))];

    private sealed record Scope(string? ProjectId, string Source);

    [Tool("search_memory", Description = "Search Corvid's memory: facts about the user and, for a known project, its approved lessons.")]
    public async Task<JsonNode> SearchAsync(
        [Description("What to look for, in a few words.")] string query,
        [Description("The most tokens of results to return, 50 to 4000.")] int max_tokens = 400,
        [Description("Only this kind: fact, lesson, conversation, or a lesson kind (workflow, correction, debugging_pattern, gotcha, convention).")] string? kind = null,
        [Description("The most results, 1 to 20 (default 10).")] int? limit = null,
        [Description("Absolute project path, origin remote URL or project id; default the server's workspace.")] string? project = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ToolArgumentException("Argument 'query' must not be empty.", nameof(query));
        if (max_tokens is < 50 or > 4_000)
            throw new ToolArgumentException("Argument 'max_tokens' must be between 50 and 4000.", nameof(max_tokens));
        if (limit is < 1 or > MaxSearchLimit)
            throw new ToolArgumentException($"Argument 'limit' must be between 1 and {MaxSearchLimit}.", nameof(limit));
        var (kinds, lessonKind) = Kinds(kind);
        var scope = ResolveScope(project);
        var result = await _retriever.RetrieveAsync(new RetrievalRequest(query, max_tokens, Kinds: kinds, ProjectId: scope.ProjectId),
            cancellationToken).ConfigureAwait(false);
        // Local-only items are left out without a count: the count would itself tell something about them.
        var shared = result.Items.Where(item => !item.LocalOnly && (lessonKind is null || item.Lesson?.Kind == lessonKind)).ToList();
        var take = limit ?? (_options.ProjectMemory ? DefaultSearchLimit : int.MaxValue);
        var items = new JsonArray();
        foreach (var item in shared.Take(take))
            items.Add((JsonNode)Item(item));
        var output = new JsonObject { ["query"] = query, ["results"] = items, ["omitted"] = result.Dropped + Math.Max(0, shared.Count - take) };
        if (_options.ProjectMemory)
            output["project"] = scope.Source;
        return output;
    }

    [Tool("get_memory", Description = "Fetch one memory item by an id from search_memory or get_memory_context.")]
    public JsonNode Get(
        [Description("The item's id.")] string id,
        [Description("Absolute project path, origin remote URL or project id; default the server's workspace.")] string? project = null)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ToolArgumentException("Argument 'id' must not be empty.", nameof(id));
        RequireProjectMemory();
        id = id.Trim();
        var scope = ResolveScope(project);
        JsonObject? item = null;
        if (FactGraph.IsFactItem(id))
        {
            if (_options.Facts.Get(id) is { IsCurrent: true, LocalOnly: false } fact)
                item = new JsonObject
                {
                    ["id"] = fact.FactId,
                    ["kind"] = "fact",
                    ["text"] = TextBudget.Truncate(Counter, fact.Text, GetTokens),
                    ["recorded"] = Date(fact.RecordedAt),
                };
        }
        else if (Lesson.IsLessonItem(id))
        {
            if (_options.Lessons?.Get(id) is { IsApproved: true, LocalOnly: false } lesson && lesson.ProjectId == scope.ProjectId)
                item = new JsonObject
                {
                    ["id"] = lesson.LessonId,
                    ["kind"] = "lesson",
                    ["lesson_kind"] = lesson.Kind,
                    ["title"] = lesson.Title,
                    ["applicability"] = lesson.Applicability,
                    ["body"] = TextBudget.Truncate(Counter, lesson.Body, GetTokens),
                    ["tags"] = Tags(lesson.Tags),
                    ["approved"] = Date(lesson.ResolvedAt ?? lesson.CreatedAt),
                };
        }
        else if (_options.IncludeConversations && _options.Episodes.Get([id]) is [var episode])
        {
            item = new JsonObject
            {
                ["id"] = episode.ItemId,
                ["kind"] = "conversation",
                ["text"] = TextBudget.Truncate(Counter, episode.Text, GetTokens),
                ["recorded"] = Date(episode.RecordedAt),
            };
        }
        // Hidden and missing are one answer, so the tool never tells whether a local-only or unapproved item exists.
        if (item is null)
            throw new ToolArgumentException($"No memory with id '{id}'.", nameof(id));
        item["project"] = scope.Source;
        return item;
    }

    [Tool("get_memory_context", Description = "The few facts and lessons most relevant to a task; call once before starting it.")]
    public async Task<JsonNode> ContextAsync(
        [Description("The task, in a sentence.")] string task,
        [Description("The most items, 1 to 5.")] int limit = MaxContextLimit,
        [Description("Absolute project path, origin remote URL or project id; default the server's workspace.")] string? project = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(task))
            throw new ToolArgumentException("Argument 'task' must not be empty.", nameof(task));
        if (limit is < 1 or > MaxContextLimit)
            throw new ToolArgumentException($"Argument 'limit' must be between 1 and {MaxContextLimit}.", nameof(limit));
        RequireProjectMemory();
        var scope = ResolveScope(project);
        // Explicit kinds: null would pull conversations too. Without a project the retriever returns facts only.
        var result = await _retriever.RetrieveAsync(new RetrievalRequest(task, ContextTokens, Kinds: [MemoryItemKind.Fact, MemoryItemKind.Lesson],
            ProjectId: scope.ProjectId), cancellationToken).ConfigureAwait(false);
        var items = new JsonArray();
        foreach (var item in result.Items.Where(item => !item.LocalOnly).Take(limit))
            items.Add((JsonNode)Item(item));
        return new JsonObject { ["task"] = task, ["results"] = items, ["project"] = scope.Source };
    }

    private void RequireProjectMemory()
    {
        if (!_options.ProjectMemory)
            throw new InvalidOperationException("Project memory is not served (Corvid:Mcp:Serve:ProjectMemory).");
    }

    private (ImmutableArray<MemoryItemKind> Kinds, string? LessonKind) Kinds(string? kind)
    {
        var value = kind?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
            return (_options.ProjectMemory ? _kinds.Add(MemoryItemKind.Lesson) : _kinds, null);
        if (value == "fact")
            return ([MemoryItemKind.Fact], null);
        if (value == "conversation" && _options.IncludeConversations)
            return ([MemoryItemKind.Episode], null);
        if (value == "lesson" && _options.ProjectMemory)
            return ([MemoryItemKind.Lesson], null);
        if (LessonKinds.IsKind(value) && _options.ProjectMemory)
            return ([MemoryItemKind.Lesson], value);
        var accepted = new List<string> { "fact" };
        if (_options.IncludeConversations)
            accepted.Add("conversation");
        if (_options.ProjectMemory)
            accepted.AddRange(["lesson", .. LessonKinds.All]);
        throw new ToolArgumentException($"Argument 'kind' must be one of: {string.Join(", ", accepted)}.", nameof(kind));
    }

    /// <summary>
    /// The call's project: the caller's argument mapped onto known projects, else the workspace's project when known, else
    /// none (facts only). An argument that maps onto nothing is not an error and reports the same source,
    /// <see cref="FromArgument"/>, as one that matches, so the result is no oracle for which paths are projects: only the
    /// shared lessons a match serves tell it. Known projects are re-read on every call.
    /// </summary>
    private Scope ResolveScope(string? project)
    {
        if (!_options.ProjectMemory)
        {
            if (!string.IsNullOrWhiteSpace(project))
                throw new ToolArgumentException("Argument 'project' needs project memory, which this server does not serve.", nameof(project));
            return new Scope(null, FromNone);
        }
        if (_options.Lessons is null)
            return new Scope(null, string.IsNullOrWhiteSpace(project) ? FromNone : FromArgument);
        var known = _options.Lessons.KnownProjects();
        if (!string.IsNullOrWhiteSpace(project))
        {
            var match = ProjectIds.Candidates(project, _options.Home, _options.Workspace, _options.WorkspaceProject?.Id).FirstOrDefault(known.Contains);
            return new Scope(match, FromArgument);
        }
        return _options.WorkspaceProject is { } workspace && known.Contains(workspace.Id)
            ? new Scope(workspace.Id, FromWorkspace)
            : new Scope(null, FromNone);
    }

    private static JsonObject Item(RetrievedItem item)
    {
        var json = new JsonObject
        {
            ["id"] = item.ItemId,
            ["kind"] = item.Kind switch
            {
                MemoryItemKind.Fact => "fact",
                MemoryItemKind.Lesson => "lesson",
                _ => "conversation",
            },
            ["text"] = item.Text,
            ["recorded"] = Date(item.RecordedAt),
        };
        if (item.Lesson is { } lesson)
        {
            json["lesson_kind"] = lesson.Kind;
            json["title"] = lesson.Title;
            json["tags"] = Tags(lesson.Tags);
        }
        return json;
    }

    private static JsonArray Tags(ImmutableArray<string> tags) => [.. tags.Select(tag => (JsonNode)JsonValue.Create(tag))];

    private static string Date(DateTimeOffset value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
