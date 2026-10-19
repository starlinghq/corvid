using System.ComponentModel;
using System.Globalization;
using System.Text.Json.Nodes;
using Corvid.Memory;

namespace Corvid.Tools.Builtin;

/// <summary>
/// The memory tools (plan §9). Search is hybrid retrieval (§9.3). <c>memory_add</c> goes through the same write path as
/// <c>remember</c> (§9.2): extraction, neighbours, the <c>memory_op</c> gate, so without a calibrated decider every add is
/// staged for review, never written unreviewed. The pending list and its approve and reject are Hermes' write approval;
/// approving or rejecting is a side effect, so the turn loop asks the user before either runs.
/// </summary>
public sealed partial class MemoryTools
{
    private readonly MemoryWriter _writer;
    private readonly HybridRetriever _retriever;

    public MemoryTools(MemoryWriter writer, HybridRetriever retriever)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(retriever);
        _writer = writer;
        _retriever = retriever;
    }

    [Tool("memory_search", Description = "Search what Corvid remembers: facts about the user and their world, and earlier conversations.")]
    public async Task<JsonNode> SearchAsync(
        [Description("What to look for, in a few words.")] string query,
        [Description("The most tokens of results to return.")] int max_tokens = 400,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ToolArgumentException("Argument 'query' must not be empty.", nameof(query));
        if (max_tokens is < 50 or > 4_000)
            throw new ToolArgumentException("Argument 'max_tokens' must be between 50 and 4000.", nameof(max_tokens));
        var result = await _retriever.RetrieveAsync(new RetrievalRequest(query, max_tokens), cancellationToken).ConfigureAwait(false);
        var items = new JsonArray();
        foreach (var item in result.Items)
        {
            items.Add((JsonNode)new JsonObject
            {
                ["id"] = item.ItemId,
                ["kind"] = item.Kind == MemoryItemKind.Fact ? "fact" : "conversation",
                ["text"] = item.Text,
                ["recorded"] = item.RecordedAt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            });
        }

        return new JsonObject { ["query"] = query, ["results"] = items, ["omitted"] = result.Dropped };
    }

    [Tool("memory_add", Description = "Ask Corvid to remember one fact for future conversations, written as a single statement. " +
        "Until the decider is calibrated the fact waits for the user's review (see memory_pending) instead of being saved at once.")]
    [SideEffect(SideEffects.Memory)]
    public async Task<JsonNode> AddAsync(
        [Description("The fact as one statement, for example 'Sam's birthday is 3 May'.")] string fact,
        ToolCallContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(fact))
            throw new ToolArgumentException("Argument 'fact' must not be empty.", nameof(fact));
        var written = await _writer.WriteAsync(new MemoryWriteRequest(context.SessionId, fact.Trim(), Surface: "memory_add"), cancellationToken)
            .ConfigureAwait(false);
        var result = new JsonObject
        {
            ["status"] = written.Status switch
            {
                MemoryWriteStatus.Applied => "saved",
                MemoryWriteStatus.Staged => "pending_review",
                _ => "already_known",
            },
        };
        if (written.Operation is { } operation)
            result["operation"] = operation;
        if (written.Pending is { } pending)
        {
            result["pending_id"] = pending.PendingId;
            result["reason"] = pending.Reason;
        }
        else if (written.MemoryId is { } id)
        {
            result["memory_id"] = id;
        }

        return result;
    }

    [Tool("memory_pending", Description = "List memory writes waiting for the user's review, oldest first.")]
    public JsonNode Pending()
    {
        var items = new JsonArray();
        foreach (var pending in _writer.ListPending())
        {
            var proposal = pending.Proposal;
            var item = new JsonObject
            {
                ["pending_id"] = pending.PendingId,
                ["kind"] = pending.Kind == PendingKind.Fact ? "fact" : "block",
                ["text"] = pending.Kind == PendingKind.Fact ? proposal.Text : proposal.Content,
                ["reason"] = pending.Reason,
                ["created"] = pending.CreatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            };
            if (proposal.Operation is { } operation)
                item["proposed_operation"] = operation;
            if (proposal.TargetFactId is { } target)
                item["target_fact"] = target;
            if (proposal.Block is { } block)
                item["block"] = block;
            items.Add((JsonNode)item);
        }

        return new JsonObject { ["pending"] = items };
    }

    [Tool("memory_approve", Description = "Save a memory write that is waiting for review, after the user agreed to it.")]
    [SideEffect(SideEffects.Memory)]
    public async Task<JsonNode> ApproveAsync(
        [Description("The pending_id from memory_pending.")] string pending_id,
        [Description("Use this operation instead of the proposed one: add, update, invalidate or noop.")] string? operation = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pending_id))
            throw new ToolArgumentException("Argument 'pending_id' must not be empty.", nameof(pending_id));
        if (operation is not (null or MemoryOperations.Add or MemoryOperations.Update or MemoryOperations.Invalidate or MemoryOperations.Noop))
            throw ToolArguments.NotAllowed(nameof(operation), operation, "add, update, invalidate, noop");
        RequirePending(pending_id);
        var written = await _writer.ApproveAsync(pending_id, operation, cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = new JsonObject { ["pending_id"] = pending_id, ["status"] = written.Status == MemoryWriteStatus.Noop ? "nothing_to_write" : "saved" };
        if (written.Operation is { } applied)
            result["operation"] = applied;
        if (written.MemoryId is { } id)
            result["memory_id"] = id;
        return result;
    }

    [Tool("memory_reject", Description = "Discard a memory write that is waiting for review, because the user does not want it kept.")]
    [SideEffect(SideEffects.Memory)]
    // The proposal is gone once rejected; it would have to be proposed again.
    [Destructive]
    public JsonNode Reject(
        [Description("The pending_id from memory_pending.")] string pending_id,
        [Description("Why it is rejected, in a few words.")] string reason = "rejected by the user")
    {
        if (string.IsNullOrWhiteSpace(pending_id))
            throw new ToolArgumentException("Argument 'pending_id' must not be empty.", nameof(pending_id));
        RequirePending(pending_id);
        var rejected = _writer.Reject(pending_id, string.IsNullOrWhiteSpace(reason) ? "rejected by the user" : reason);
        return new JsonObject { ["pending_id"] = rejected.PendingId, ["status"] = "rejected" };
    }

    /// <summary>A missing or already reviewed id is the model's mistake to repair, not a tool failure.</summary>
    private void RequirePending(string pendingId)
    {
        switch (_writer.GetPending(pendingId))
        {
            case null:
                throw new ToolArgumentException($"There is no pending memory write '{pendingId}'; call memory_pending for the ids.", "pending_id");
            case { Status: not PendingStatus.Pending } resolved:
                throw new ToolArgumentException($"Memory write '{pendingId}' was already {resolved.Status.ToString().ToLowerInvariant()}.", "pending_id");
        }
    }
}
