using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;

namespace Corvid.Memory;

/// <param name="Similarity">Cosine similarity, -1 to 1.</param>
public sealed record VectorHit(string ItemId, double Similarity);

/// <summary>
/// Vectors keyed by <c>(item, embedder_id)</c> (plan §4.3), so vectors from two embedders never mix and a swap can build the
/// new index beside the old one. Vectors are compared only within one embedder.
/// </summary>
public interface IVectorIndex
{
    /// <summary>Adds or replaces one item's vector for one embedder, atomically.</summary>
    void Upsert(string itemId, string embedderId, ReadOnlySpan<float> vector);

    /// <summary>The nearest items by cosine similarity, best first, among those <paramref name="filter"/> accepts.</summary>
    IReadOnlyList<VectorHit> Search(string embedderId, ReadOnlySpan<float> query, int limit, Func<string, bool>? filter = null);

    /// <summary>Which of <paramref name="itemIds"/> have a vector for the embedder.</summary>
    IReadOnlySet<string> Having(string embedderId, IReadOnlyCollection<string> itemIds);

    int Count(string embedderId);

    /// <summary>Removes every vector of an embedder (after a swap completes).</summary>
    void RemoveEmbedder(string embedderId);
}

/// <summary>
/// A managed brute-force index in the memory database: vectors are float32 BLOBs, normalized on write, and a search scans
/// every vector of the embedder. Exact, and linear in the number of items. sqlite-vec would replace the scan; it has no
/// first-party .NET package yet, so it is a follow-up behind this interface.
/// </summary>
public sealed class SqliteVectorIndex(MemoryDatabase database) : IVectorIndex
{
    private readonly MemoryDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public void Upsert(string itemId, string embedderId, ReadOnlySpan<float> vector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(itemId);
        ArgumentException.ThrowIfNullOrWhiteSpace(embedderId);
        var normalized = Normalize(vector);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        // One embedder has one dimension count; a mismatch means the embedder id was reused for another model.
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT dimensions FROM vectors WHERE embedder_id=$embedder LIMIT 1;";
            check.Parameters.AddWithValue("$embedder", embedderId);
            if (check.ExecuteScalar() is long dimensions && dimensions != normalized.Length)
                throw new ArgumentException($"Embedder '{embedderId}' has {dimensions} dimensions, not {normalized.Length}.", nameof(vector));
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO vectors (item_id, embedder_id, dimensions, vector) VALUES ($item, $embedder, $dimensions, $vector)
            ON CONFLICT (item_id, embedder_id) DO UPDATE SET dimensions=excluded.dimensions, vector=excluded.vector;
            """;
        command.Parameters.AddWithValue("$item", itemId);
        command.Parameters.AddWithValue("$embedder", embedderId);
        command.Parameters.AddWithValue("$dimensions", normalized.Length);
        command.Parameters.AddWithValue("$vector", MemoryMarshal.AsBytes(normalized.AsSpan()).ToArray());
        command.ExecuteNonQuery();
        transaction.Commit();
    }

    public IReadOnlyList<VectorHit> Search(string embedderId, ReadOnlySpan<float> query, int limit, Func<string, bool>? filter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(embedderId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var normalized = Normalize(query);
        var best = new PriorityQueue<VectorHit, double>();
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_id, vector FROM vectors WHERE embedder_id=$embedder;";
        command.Parameters.AddWithValue("$embedder", embedderId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var itemId = reader.GetString(0);
            if (filter is not null && !filter(itemId))
                continue;
            var bytes = (byte[])reader.GetValue(1);
            var stored = MemoryMarshal.Cast<byte, float>(bytes);
            if (stored.Length != normalized.Length)
                throw new ArgumentException($"The query has {normalized.Length} dimensions; embedder '{embedderId}' has {stored.Length}.", nameof(query));
            var similarity = Dot(normalized, stored);
            best.Enqueue(new VectorHit(itemId, similarity), similarity);
            if (best.Count > limit)
                best.Dequeue();
        }
        var hits = new List<VectorHit>(best.Count);
        while (best.Count > 0)
            hits.Add(best.Dequeue());
        hits.Reverse();
        // Equal similarities keep a stable order.
        return [.. hits.OrderByDescending(hit => hit.Similarity).ThenBy(hit => hit.ItemId, StringComparer.Ordinal)];
    }

    public IReadOnlySet<string> Having(string embedderId, IReadOnlyCollection<string> itemIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(embedderId);
        ArgumentNullException.ThrowIfNull(itemIds);
        var result = new HashSet<string>(StringComparer.Ordinal);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM vectors WHERE item_id=$item AND embedder_id=$embedder;";
        var item = command.Parameters.Add("$item", SqliteType.Text);
        command.Parameters.AddWithValue("$embedder", embedderId);
        foreach (var id in itemIds)
        {
            item.Value = id;
            if (command.ExecuteScalar() is not null)
                result.Add(id);
        }
        return result;
    }

    public int Count(string embedderId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM vectors WHERE embedder_id=$embedder;";
        command.Parameters.AddWithValue("$embedder", embedderId);
        return Convert.ToInt32(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    public void RemoveEmbedder(string embedderId)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM vectors WHERE embedder_id=$embedder;";
        command.Parameters.AddWithValue("$embedder", embedderId);
        command.ExecuteNonQuery();
    }

    private static float[] Normalize(ReadOnlySpan<float> vector)
    {
        if (vector.IsEmpty)
            throw new ArgumentException("A vector needs at least one dimension.", nameof(vector));
        double norm = 0;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value))
                throw new ArgumentException("A vector must be finite.", nameof(vector));
            norm += (double)value * value;
        }
        if (norm == 0)
            throw new ArgumentException("A zero vector has no direction.", nameof(vector));
        var scale = 1 / Math.Sqrt(norm);
        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++)
            result[i] = (float)(vector[i] * scale);
        return result;
    }

    private static double Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
            sum += (double)a[i] * b[i];
        return sum;
    }
}
