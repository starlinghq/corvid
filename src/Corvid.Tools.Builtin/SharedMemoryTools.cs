using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Corvid.Memory;

namespace Corvid.Tools.Builtin;

/// <summary>
/// Memory search for another agent (C-018's MCP server mode, plan §12). Whoever asks is not Corvid's own model, and its
/// model may well be a cloud one, so local-only facts (plan §10.4) are never returned, and earlier conversations, which hold
/// raw tool results and outside content, are returned only when the user shares them. Read-only: nothing is written.
/// </summary>
public sealed partial class SharedMemoryTools
{
    private readonly HybridRetriever _retriever;
    private readonly ImmutableArray<MemoryItemKind> _kinds;

    /// <param name="includeConversations">Also search earlier conversations (episodes), not only facts.</param>
    public SharedMemoryTools(HybridRetriever retriever, bool includeConversations = false)
    {
        ArgumentNullException.ThrowIfNull(retriever);
        _retriever = retriever;
        _kinds = includeConversations ? [MemoryItemKind.Fact, MemoryItemKind.Episode] : [MemoryItemKind.Fact];
    }

    [Tool("search_memory", Description = "Search what Corvid remembers about its user and their world.")]
    public async Task<JsonNode> SearchAsync(
        [Description("What to look for, in a few words.")] string query,
        [Description("The most tokens of results to return.")] int max_tokens = 400,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ToolArgumentException("Argument 'query' must not be empty.", nameof(query));
        if (max_tokens is < 50 or > 4_000)
            throw new ToolArgumentException("Argument 'max_tokens' must be between 50 and 4000.", nameof(max_tokens));
        var result = await _retriever.RetrieveAsync(new RetrievalRequest(query, max_tokens, Kinds: _kinds), cancellationToken).ConfigureAwait(false);
        var items = new JsonArray();
        // Local-only facts are left out without a count: the count would itself tell something about them.
        foreach (var item in result.Items.Where(item => !item.LocalOnly))
        {
            items.Add((JsonNode)new JsonObject
            {
                ["kind"] = item.Kind == MemoryItemKind.Fact ? "fact" : "conversation",
                ["text"] = item.Text,
                ["recorded"] = item.RecordedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            });
        }

        return new JsonObject { ["query"] = query, ["results"] = items, ["omitted"] = result.Dropped };
    }
}
