using System.Collections.Immutable;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Corvid.Memory;

/// <summary>An entity named in a fact: a label from the extractor's schema (person, place, ...) and its surface name.</summary>
public sealed record EntityRef(string Label, string Name)
{
    /// <summary>Entities match by label and normalized name: trimmed, inner whitespace collapsed, NFKC, lower-cased.</summary>
    public string Key => Normalize(Name);

    public string NormalizedLabel => Normalize(Label);

    internal static string Normalize(string value)
    {
        var builder = new StringBuilder();
        foreach (var part in value.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (builder.Length > 0)
                builder.Append(' ');
            builder.Append(part);
        }
        return builder.ToString().ToLowerInvariant();
    }
}

public sealed record MemoryEntity(string EntityId, string Label, string Name);

/// <summary>A relation: a fact that links two entities with a predicate (<c>sam works_at acme</c>).</summary>
public sealed record RelationInput(EntityRef Subject, string Predicate, EntityRef Object);

/// <summary>A fact to record.</summary>
/// <param name="Entities">Entities the fact mentions. A relation's subject and object are added automatically.</param>
/// <param name="ValidFrom">When the fact became true in the world; null means when it is recorded.</param>
public sealed record FactInput(
    string Text,
    ImmutableArray<EntityRef> Entities = default,
    RelationInput? Relation = null,
    DateTimeOffset? ValidFrom = null,
    string? SessionId = null,
    long? SourceSequence = null,
    bool LocalOnly = false);

/// <summary>
/// A bi-temporal fact. Valid time (<see cref="ValidFrom"/>, <see cref="ValidTo"/>) is when it held in the world; transaction
/// time (<see cref="RecordedAt"/>, <see cref="InvalidatedAt"/>) is when Corvid learned it and learned it had ended.
/// </summary>
public sealed record Fact(
    string FactId,
    string Text,
    ImmutableArray<MemoryEntity> Entities,
    string? SubjectId,
    string? Predicate,
    string? ObjectId,
    DateTimeOffset ValidFrom,
    DateTimeOffset? ValidTo,
    DateTimeOffset RecordedAt,
    DateTimeOffset? InvalidatedAt,
    string? Supersedes,
    string? SessionId,
    long? SourceSequence,
    bool LocalOnly = false)
{
    public bool IsCurrent => ValidTo is null;

    /// <summary>True in the world at <paramref name="validAt"/>, as Corvid believed at <paramref name="knownAt"/>.</summary>
    public bool HoldsAt(DateTimeOffset validAt, DateTimeOffset knownAt)
    {
        if (RecordedAt > knownAt || ValidFrom > validAt)
            return false;
        var endKnown = InvalidatedAt is { } invalidated && invalidated <= knownAt;
        return !endKnown || ValidTo > validAt;
    }
}

/// <param name="Hops">0 for a fact about a seed entity, 1 for a fact about an entity one fact away from a seed.</param>
public sealed record GraphHit(Fact Fact, int Hops);

/// <param name="Rank">Lower is better (BM25), or 0 for a LIKE match.</param>
public sealed record FactHit(Fact Fact, double Rank);

/// <summary>
/// The semantic tier (plan §9.1): entities, relations and facts with valid and transaction time, in SQLite tables. A fact is
/// never deleted or rewritten: invalidation sets its <c>valid_to</c> (plan §9.2, as in Graphiti), so "what did I believe
/// in March" stays answerable with <see cref="AsOf"/>. Triggers in the schema enforce both rules.
/// </summary>
public sealed class FactGraph
{
    private const string FactColumns = """
        f.fact_id, f.text, f.subject_id, f.predicate, f.object_id, f.valid_from, f.valid_to, f.recorded_at, f.invalidated_at,
        f.supersedes, f.session_id, f.source_sequence, f.local_only
        """;

    private readonly MemoryDatabase _database;
    private readonly TimeProvider _time;

    public FactGraph(MemoryDatabase database, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _time = time ?? TimeProvider.System;
    }

    public static bool IsFactItem(string itemId) => itemId.StartsWith("fact:", StringComparison.Ordinal);

    public Fact Add(FactInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var fact = Insert(connection, transaction, input, supersedes: null, _time.GetUtcNow());
        transaction.Commit();
        return fact;
    }

    /// <summary>Ends a current fact. Nothing is deleted.</summary>
    /// <param name="validTo">When it stopped being true; null means now. It cannot precede the fact's start.</param>
    public Fact Invalidate(string factId, DateTimeOffset? validTo = null)
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = _time.GetUtcNow();
        var fact = Close(connection, transaction, factId, validTo ?? now, now);
        transaction.Commit();
        return fact;
    }

    /// <summary>
    /// Replaces a current fact: the old one ends when the new one starts, and the new one records what it supersedes. A
    /// replacement of a local-only fact stays local-only.
    /// </summary>
    public (Fact Old, Fact New) Supersede(string factId, FactInput replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var now = _time.GetUtcNow();
        var from = replacement.ValidFrom ?? now;
        var old = Close(connection, transaction, factId, from, now);
        var added = Insert(connection, transaction, replacement with { ValidFrom = from, LocalOnly = replacement.LocalOnly || old.LocalOnly },
            factId, now);
        transaction.Commit();
        return (old, added);
    }

    /// <summary>
    /// Keeps a fact off every model that is not local (plan §10.4). The flag only tightens: nothing makes a local-only fact
    /// shareable again, short of the user superseding it.
    /// </summary>
    public Fact MarkLocalOnly(string factId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factId);
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE facts SET local_only=1 WHERE fact_id=$id;";
        command.Parameters.AddWithValue("$id", factId);
        if (command.ExecuteNonQuery() == 0)
            throw new KeyNotFoundException($"Unknown fact '{factId}'.");
        return Read(connection, null, factId)!;
    }

    public Fact? Get(string factId)
    {
        ArgumentNullException.ThrowIfNull(factId);
        using var connection = _database.Open();
        return Read(connection, null, factId);
    }

    public IReadOnlyList<Fact> Get(IEnumerable<string> factIds)
    {
        ArgumentNullException.ThrowIfNull(factIds);
        using var connection = _database.Open();
        return [.. factIds.Select(id => Read(connection, null, id)).OfType<Fact>()];
    }

    /// <summary>
    /// Facts true at <paramref name="validAt"/> as Corvid believed at <paramref name="knownAt"/> (default: now), oldest first.
    /// A fact invalidated after <paramref name="knownAt"/> still counts as open, because that is what was believed then.
    /// </summary>
    /// <param name="entity">Limits the result to facts that mention this entity.</param>
    public IReadOnlyList<Fact> AsOf(DateTimeOffset validAt, DateTimeOffset? knownAt = null, EntityRef? entity = null)
    {
        var known = knownAt ?? _time.GetUtcNow();
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {FactColumns} FROM facts f
            WHERE f.recorded_at <= $known AND f.valid_from <= $valid
              AND (f.invalidated_at IS NULL OR f.invalidated_at > $known OR f.valid_to > $valid)
              AND ($entity IS NULL OR EXISTS (SELECT 1 FROM fact_entities fe WHERE fe.fact_id = f.fact_id AND fe.entity_id = $entity))
            ORDER BY f.valid_from, f.recorded_at, f.fact_id;
            """;
        command.Parameters.AddWithValue("$known", Ticks.From(known));
        command.Parameters.AddWithValue("$valid", Ticks.From(validAt));
        string? entityId = null;
        if (entity is not null && (entityId = FindEntity(connection, entity)?.EntityId) is null)
            return [];
        command.Parameters.AddWithValue("$entity", (object?)entityId ?? DBNull.Value);
        return ReadFacts(connection, command);
    }

    /// <summary>Full-text search over current facts, ranked by BM25 (LIKE for short terms only, as in <see cref="EpisodicIndex"/>).</summary>
    public IReadOnlyList<FactHit> Search(string query, int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var (match, shortTerms) = FtsQuery.Build(query);
        if (match is null && shortTerms.Count == 0)
            return [];
        using var connection = _database.Open();
        using var command = connection.CreateCommand();
        if (match is not null)
        {
            command.CommandText = $"""
                SELECT {FactColumns}, bm25(facts_fts) AS rank FROM facts_fts JOIN facts f ON f.id = facts_fts.rowid
                WHERE facts_fts MATCH $match AND f.valid_to IS NULL
                ORDER BY rank, f.recorded_at DESC LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$match", match);
        }
        else
        {
            var likes = string.Join(" OR ", shortTerms.Select((_, i) => $"f.text LIKE $t{i} ESCAPE '\\'"));
            command.CommandText = $"""
                SELECT {FactColumns}, 0.0 AS rank FROM facts f WHERE ({likes}) AND f.valid_to IS NULL
                ORDER BY f.recorded_at DESC, f.id DESC LIMIT $limit;
                """;
            for (var i = 0; i < shortTerms.Count; i++)
                command.Parameters.AddWithValue($"$t{i}", FtsQuery.LikePattern(shortTerms[i]));
        }
        command.Parameters.AddWithValue("$limit", limit);
        var rows = new List<(Fact Fact, double Rank)>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                rows.Add((ReadFact(reader, []), reader.GetDouble(13)));
        return [.. rows.Select(row => new FactHit(WithEntities(connection, row.Fact), row.Rank))];
    }

    /// <summary>Known entities among <paramref name="entities"/>, matched by label and normalized name. Unknown ones are skipped.</summary>
    public IReadOnlyList<MemoryEntity> FindEntities(IEnumerable<EntityRef> entities)
    {
        ArgumentNullException.ThrowIfNull(entities);
        using var connection = _database.Open();
        return [.. entities.Select(entity => FindEntity(connection, entity)).OfType<MemoryEntity>().DistinctBy(entity => entity.EntityId)];
    }

    /// <summary>
    /// One-hop expansion (plan §9.3): current facts that mention a seed entity (hop 0), then current facts that mention an
    /// entity those facts link to (hop 1). Newest first within a hop.
    /// </summary>
    public IReadOnlyList<GraphHit> Expand(IEnumerable<string> entityIds, int limit = 20)
    {
        ArgumentNullException.ThrowIfNull(entityIds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var seeds = entityIds.ToHashSet(StringComparer.Ordinal);
        if (seeds.Count == 0)
            return [];
        using var connection = _database.Open();
        var hits = new List<GraphHit>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hop0 = FactsAbout(connection, seeds);
        foreach (var fact in hop0.Where(fact => seen.Add(fact.FactId)))
            hits.Add(new GraphHit(fact, 0));
        var neighbours = hop0.SelectMany(fact => fact.Entities).Select(entity => entity.EntityId)
            .Where(id => !seeds.Contains(id)).ToHashSet(StringComparer.Ordinal);
        foreach (var fact in FactsAbout(connection, neighbours).Where(fact => seen.Add(fact.FactId)))
            hits.Add(new GraphHit(fact, 1));
        return hits.Count > limit ? hits[..limit] : hits;
    }

    private List<Fact> FactsAbout(SqliteConnection connection, IReadOnlySet<string> entityIds)
    {
        if (entityIds.Count == 0)
            return [];
        var facts = new Dictionary<string, Fact>(StringComparer.Ordinal);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {FactColumns} FROM facts f JOIN fact_entities fe ON fe.fact_id = f.fact_id
            WHERE fe.entity_id = $entity AND f.valid_to IS NULL;
            """;
        var entity = command.Parameters.Add("$entity", SqliteType.Text);
        foreach (var id in entityIds.Order(StringComparer.Ordinal))
        {
            entity.Value = id;
            foreach (var fact in ReadFacts(connection, command))
                facts.TryAdd(fact.FactId, fact);
        }
        return [.. facts.Values.OrderByDescending(fact => fact.RecordedAt).ThenBy(fact => fact.FactId, StringComparer.Ordinal)];
    }

    private Fact Insert(SqliteConnection connection, SqliteTransaction transaction, FactInput input, string? supersedes, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input.Text);
        var mentioned = (input.Entities.IsDefault ? [] : input.Entities).ToList();
        string? subjectId = null, objectId = null, predicate = null;
        if (input.Relation is { } relation)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(relation.Predicate);
            subjectId = Resolve(connection, transaction, relation.Subject, now).EntityId;
            objectId = Resolve(connection, transaction, relation.Object, now).EntityId;
            predicate = relation.Predicate;
            mentioned.Add(relation.Subject);
            mentioned.Add(relation.Object);
        }
        var factId = "fact:" + Guid.NewGuid().ToString("N");
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO facts (fact_id, text, subject_id, predicate, object_id, valid_from, valid_to, recorded_at,
                    invalidated_at, supersedes, session_id, source_sequence, local_only)
                VALUES ($id, $text, $subject, $predicate, $object, $from, NULL, $at, NULL, $supersedes, $session, $sequence, $local);
                """;
            command.Parameters.AddWithValue("$id", factId);
            command.Parameters.AddWithValue("$text", input.Text);
            command.Parameters.AddWithValue("$subject", (object?)subjectId ?? DBNull.Value);
            command.Parameters.AddWithValue("$predicate", (object?)predicate ?? DBNull.Value);
            command.Parameters.AddWithValue("$object", (object?)objectId ?? DBNull.Value);
            command.Parameters.AddWithValue("$from", Ticks.From(input.ValidFrom ?? now));
            command.Parameters.AddWithValue("$at", Ticks.From(now));
            command.Parameters.AddWithValue("$supersedes", (object?)supersedes ?? DBNull.Value);
            command.Parameters.AddWithValue("$session", (object?)input.SessionId ?? DBNull.Value);
            command.Parameters.AddWithValue("$sequence", (object?)input.SourceSequence ?? DBNull.Value);
            command.Parameters.AddWithValue("$local", input.LocalOnly ? 1 : 0);
            command.ExecuteNonQuery();
        }
        foreach (var entity in mentioned)
        {
            var resolved = Resolve(connection, transaction, entity, now);
            using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = "INSERT OR IGNORE INTO fact_entities (fact_id, entity_id) VALUES ($fact, $entity);";
            link.Parameters.AddWithValue("$fact", factId);
            link.Parameters.AddWithValue("$entity", resolved.EntityId);
            link.ExecuteNonQuery();
        }
        return Read(connection, transaction, factId)!;
    }

    private static Fact Close(SqliteConnection connection, SqliteTransaction transaction, string factId, DateTimeOffset validTo,
        DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factId);
        var fact = Read(connection, transaction, factId) ?? throw new KeyNotFoundException($"Unknown fact '{factId}'.");
        if (!fact.IsCurrent)
            throw new InvalidOperationException($"Fact '{factId}' was already invalidated.");
        if (validTo < fact.ValidFrom)
            throw new ArgumentOutOfRangeException(nameof(validTo), "A fact cannot end before it started.");
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE facts SET valid_to=$to, invalidated_at=$at WHERE fact_id=$id AND valid_to IS NULL;";
        command.Parameters.AddWithValue("$to", Ticks.From(validTo));
        command.Parameters.AddWithValue("$at", Ticks.From(now));
        command.Parameters.AddWithValue("$id", factId);
        command.ExecuteNonQuery();
        return Read(connection, transaction, factId)!;
    }

    private static MemoryEntity Resolve(SqliteConnection connection, SqliteTransaction transaction, EntityRef entity, DateTimeOffset now)
    {
        Check(entity);
        if (FindEntity(connection, entity, transaction) is { } existing)
            return existing;
        var created = new MemoryEntity("entity:" + Guid.NewGuid().ToString("N"), entity.NormalizedLabel, entity.Name.Trim());
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO entities (entity_id, label, name, key, recorded_at) VALUES ($id, $label, $name, $key, $at);";
        command.Parameters.AddWithValue("$id", created.EntityId);
        command.Parameters.AddWithValue("$label", created.Label);
        command.Parameters.AddWithValue("$name", created.Name);
        command.Parameters.AddWithValue("$key", entity.Key);
        command.Parameters.AddWithValue("$at", Ticks.From(now));
        command.ExecuteNonQuery();
        return created;
    }

    private static MemoryEntity? FindEntity(SqliteConnection connection, EntityRef entity, SqliteTransaction? transaction = null)
    {
        Check(entity);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT entity_id, label, name FROM entities WHERE label=$label AND key=$key;";
        command.Parameters.AddWithValue("$label", entity.NormalizedLabel);
        command.Parameters.AddWithValue("$key", entity.Key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? new MemoryEntity(reader.GetString(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    private static void Check(EntityRef entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity.Label);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity.Name);
    }

    private static Fact? Read(SqliteConnection connection, SqliteTransaction? transaction, string factId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT {FactColumns} FROM facts f WHERE f.fact_id=$id;";
        command.Parameters.AddWithValue("$id", factId);
        return ReadFacts(connection, command).SingleOrDefault();
    }

    private static List<Fact> ReadFacts(SqliteConnection connection, SqliteCommand command)
    {
        var facts = new List<Fact>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                facts.Add(ReadFact(reader, []));
        return [.. facts.Select(fact => WithEntities(connection, fact, command.Transaction))];
    }

    private static Fact ReadFact(SqliteDataReader reader, ImmutableArray<MemoryEntity> entities) =>
        new(reader.GetString(0), reader.GetString(1), entities, Nullable(reader, 2), Nullable(reader, 3), Nullable(reader, 4),
            Ticks.To(reader.GetInt64(5)), Ticks.ToNullable(reader, 6), Ticks.To(reader.GetInt64(7)), Ticks.ToNullable(reader, 8),
            Nullable(reader, 9), Nullable(reader, 10), reader.IsDBNull(11) ? null : reader.GetInt64(11), reader.GetInt64(12) != 0);

    private static Fact WithEntities(SqliteConnection connection, Fact fact, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT e.entity_id, e.label, e.name FROM fact_entities fe JOIN entities e ON e.entity_id = fe.entity_id
            WHERE fe.fact_id=$id ORDER BY e.label, e.name, e.entity_id;
            """;
        command.Parameters.AddWithValue("$id", fact.FactId);
        using var reader = command.ExecuteReader();
        var entities = ImmutableArray.CreateBuilder<MemoryEntity>();
        while (reader.Read())
            entities.Add(new MemoryEntity(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return fact with { Entities = entities.ToImmutable() };
    }

    private static string? Nullable(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
