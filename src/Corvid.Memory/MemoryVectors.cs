using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;

namespace Corvid.Memory;

/// <summary>An <c>embedder.text</c> binding as memory sees it. <see cref="Id"/> keys its vectors, so it must name the model exactly.</summary>
public sealed record MemoryEmbedder(string Id, IEmbeddingGenerator<string, Embedding<float>> Generator)
{
    public string Id { get; } = !string.IsNullOrWhiteSpace(Id) ? Id : throw new ArgumentException("An embedder needs an id.", nameof(Id));

    public IEmbeddingGenerator<string, Embedding<float>> Generator { get; } = Generator ?? throw new ArgumentNullException(nameof(Generator));
}

/// <param name="Active">The embedder whose vectors retrieval reads; null until the first one is set.</param>
/// <param name="Pending">The embedder a swap is building an index for; null when no swap is running.</param>
public sealed record EmbedderState(string? Active, string? Pending);

/// <param name="Remaining">Items still without a vector for <see cref="EmbedderId"/> after this run.</param>
/// <param name="Completed">No item is missing; for a swap, the pending embedder is now active and the old vectors are gone.</param>
public sealed record ReembedProgress(string EmbedderId, int Embedded, int Remaining, bool Completed);

/// <summary>
/// Vectors for memory items and the embedder swap rule (plan §4.3). Changing <c>embedder.text</c> invalidates every stored
/// vector, so a swap builds the new embedder's index beside the old one:
/// <list type="bullet">
/// <item><see cref="BeginSwap"/> records the pending embedder. New items are embedded with both embedders from then on.</item>
/// <item>The re-embed job (<see cref="ReembedAsync"/>, an idle-time job like consolidation, plan §9.5) writes the new vector
/// of each item in its own transaction, so every row switches atomically and the job can stop and resume anywhere.</item>
/// <item>Until no item is missing, retrieval embeds queries with the old embedder and searches the old index; FTS covers
/// everything. If the old embedder is not loaded, retrieval uses FTS and the graph only.</item>
/// <item>When the last item has its new vector, one update makes the new embedder active, and the old vectors are removed.</item>
/// </list>
/// Items are episodes and current facts; invalidated facts are not re-embedded, because retrieval never returns them.
/// </summary>
public sealed class MemoryVectors
{
    private const int ScanPage = 512;

    private readonly MemoryDatabase _database;
    private readonly Dictionary<string, MemoryEmbedder> _embedders;
    private readonly int _maxEmbedChars;

    /// <param name="embedders">The embedders this process has loaded, by id. Usually the slot's current binding, plus the new
    /// one during a swap.</param>
    /// <param name="maxEmbedChars">Longer item text is cut before embedding (embedders reject over-long input); FTS keeps it all.</param>
    public MemoryVectors(MemoryDatabase database, IVectorIndex index, IEnumerable<MemoryEmbedder> embedders, int maxEmbedChars = 2_000)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(embedders);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEmbedChars);
        _database = database;
        Index = index;
        _embedders = embedders.ToDictionary(embedder => embedder.Id, StringComparer.Ordinal);
        _maxEmbedChars = maxEmbedChars;
    }

    public IVectorIndex Index { get; }

    public EmbedderState State
    {
        get
        {
            using var connection = _database.Open();
            return ReadState(connection, null);
        }
    }

    /// <summary>The embedder retrieval uses: the active one, when this process has it loaded. During a swap it is the old one.</summary>
    public MemoryEmbedder? RetrievalEmbedder => State.Active is { } active && _embedders.TryGetValue(active, out var embedder) ? embedder : null;

    /// <summary>
    /// Starts a swap to <paramref name="embedderId"/>. With no active embedder and no items yet, it becomes active at once.
    /// Starting a swap to the active embedder cancels a running swap; to another embedder, it replaces the pending one.
    /// </summary>
    public EmbedderState BeginSwap(string embedderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(embedderId);
        string? discard;
        EmbedderState state;
        using (var connection = _database.Open())
        using (var transaction = connection.BeginTransaction(deferred: false))
        {
            var current = ReadState(connection, transaction);
            discard = current.Pending is { } pending && pending != embedderId ? pending : null;
            state = current.Active is null && !HasItems(connection, transaction)
                ? new EmbedderState(embedderId, null)
                : current.Active == embedderId ? current with { Pending = null } : current with { Pending = embedderId };
            WriteState(connection, transaction, state);
            transaction.Commit();
        }
        if (discard is not null)
            Index.RemoveEmbedder(discard);
        return state;
    }

    /// <summary>
    /// Embeds the given items with the active and pending embedders this process has loaded. Best effort: an embedder
    /// failure leaves the items for <see cref="ReembedAsync"/>, which finds every item still missing a vector.
    /// </summary>
    /// <returns>False when an embedder failed or was not loaded.</returns>
    public async Task<bool> EmbedAsync(IReadOnlyList<(string ItemId, string Text)> items, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0)
            return true;
        var state = State;
        var complete = true;
        foreach (var id in new[] { state.Active, state.Pending }.OfType<string>())
        {
            if (!_embedders.TryGetValue(id, out var embedder))
            {
                complete = false;
                continue;
            }
            try
            {
                await EmbedWithAsync(embedder, items, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not ArgumentException)
            {
                complete = false;
            }
        }
        return complete;
    }

    /// <summary>
    /// One run of the re-embed job: embeds up to <paramref name="batchSize"/> items missing a vector for the pending embedder
    /// (or, with no swap running, the active one, which backfills items an earlier failure skipped). When none remain, a
    /// swap completes.
    /// </summary>
    public async Task<ReembedProgress> ReembedAsync(int batchSize = 64, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var state = State;
        var target = state.Pending ?? state.Active ?? throw new InvalidOperationException("No embedder is set; call BeginSwap first.");
        if (!_embedders.TryGetValue(target, out var embedder))
            throw new InvalidOperationException($"Embedder '{target}' is not loaded in this process.");

        var (batch, missing) = Missing(target, batchSize, cancellationToken);
        if (batch.Count > 0)
            await EmbedWithAsync(embedder, batch, cancellationToken);
        var remaining = missing - batch.Count;
        if (remaining > 0)
            return new ReembedProgress(target, batch.Count, remaining, false);

        // Items written while this batch ran are embedded with the pending embedder too, so a final scan finds none unless
        // a write's embedding failed; then the swap waits for the next run.
        if (state.Pending is not null && Missing(target, 1, cancellationToken).Missing == 0 && Complete(state))
            Index.RemoveEmbedder(state.Active!);
        return new ReembedProgress(target, batch.Count, 0, true);
    }

    /// <summary>Embeds a query with the retrieval embedder and searches its index; empty when that embedder is not loaded.</summary>
    public async Task<IReadOnlyList<VectorHit>> SearchAsync(string query, int limit, Func<string, bool>? filter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (RetrievalEmbedder is not { } embedder || string.IsNullOrWhiteSpace(query))
            return [];
        var embeddings = await embedder.Generator.GenerateAsync([TextBudget.Prefix(query, _maxEmbedChars)], cancellationToken: cancellationToken);
        return Index.Search(embedder.Id, embeddings[0].Vector.Span, limit, filter);
    }

    private async Task EmbedWithAsync(MemoryEmbedder embedder, IReadOnlyList<(string ItemId, string Text)> items,
        CancellationToken cancellationToken)
    {
        var embeddings = await embedder.Generator.GenerateAsync(items.Select(item => TextBudget.Prefix(item.Text, _maxEmbedChars)),
            cancellationToken: cancellationToken);
        if (embeddings.Count != items.Count)
            throw new InvalidOperationException($"Embedder '{embedder.Id}' returned {embeddings.Count} vectors for {items.Count} inputs.");
        for (var i = 0; i < items.Count; i++)
            Index.Upsert(items[i].ItemId, embedder.Id, embeddings[i].Vector.Span);
    }

    private bool Complete(EmbedderState expected)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (ReadState(connection, transaction) != expected)
            return false;
        WriteState(connection, transaction, new EmbedderState(expected.Pending, null));
        transaction.Commit();
        return expected.Active is not null;
    }

    /// <summary>Up to <paramref name="take"/> items without a vector for the embedder, and how many are missing in all.</summary>
    private (List<(string ItemId, string Text)> Batch, int Missing) Missing(string embedderId, int take, CancellationToken cancellationToken)
    {
        var batch = new List<(string, string)>();
        var missing = 0;
        var after = "";
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = new List<(string ItemId, string Text)>();
            using (var connection = _database.Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    SELECT item_id, text FROM (
                        SELECT item_id, text FROM episodes
                        UNION ALL
                        SELECT fact_id AS item_id, text FROM facts WHERE valid_to IS NULL)
                    WHERE item_id > $after ORDER BY item_id LIMIT $limit;
                    """;
                command.Parameters.AddWithValue("$after", after);
                command.Parameters.AddWithValue("$limit", ScanPage);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    page.Add((reader.GetString(0), reader.GetString(1)));
            }
            if (page.Count == 0)
                return (batch, missing);
            var having = Index.Having(embedderId, [.. page.Select(item => item.ItemId)]);
            foreach (var item in page.Where(item => !having.Contains(item.ItemId)))
            {
                missing++;
                if (batch.Count < take)
                    batch.Add(item);
            }
            after = page[^1].ItemId;
        }
    }

    private static bool HasItems(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT EXISTS (SELECT 1 FROM episodes) OR EXISTS (SELECT 1 FROM facts);";
        return (long)command.ExecuteScalar()! == 1;
    }

    private static EmbedderState ReadState(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT active, pending FROM embedder_state WHERE id = 1;";
        using var reader = command.ExecuteReader();
        reader.Read();
        return new EmbedderState(reader.IsDBNull(0) ? null : reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static void WriteState(SqliteConnection connection, SqliteTransaction transaction, EmbedderState state)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE embedder_state SET active=$active, pending=$pending WHERE id = 1;";
        command.Parameters.AddWithValue("$active", (object?)state.Active ?? DBNull.Value);
        command.Parameters.AddWithValue("$pending", (object?)state.Pending ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
