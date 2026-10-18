using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace Corvid.Memory;

/// <summary>
/// Operation names recorded in <c>MemoryWritten</c> events and staged proposals. They are persisted: renaming one changes
/// the log's meaning. The first four are the <c>memory_op</c> options (Mem0's operation set).
/// </summary>
public static class MemoryOperations
{
    public const string Add = "add";
    public const string Update = "update";
    public const string Invalidate = "invalidate";
    public const string Noop = "noop";
    /// <summary>Recorded when a write is staged for review (<c>/memory pending</c>); the memory id is the pending id.</summary>
    public const string Staged = "staged";

    internal static bool IsOperation(string? value) => value is Add or Update or Invalidate or Noop;
}

public enum PendingKind
{
    Fact,
    Block,
}

public enum PendingStatus
{
    Pending,
    Approved,
    Rejected,
}

public sealed record ProposedEntity(string Label, string Name)
{
    internal EntityRef ToEntity() => new(Label, Name);

    internal static ProposedEntity From(EntityRef entity) => new(entity.Label, entity.Name);
}

/// <summary>A relation the caller supplied with the write, kept so an approved write records it.</summary>
public sealed record ProposedRelation(ProposedEntity Subject, string Predicate, ProposedEntity Object)
{
    internal RelationInput ToRelation() => new(Subject.ToEntity(), Predicate, Object.ToEntity());

    internal static ProposedRelation From(RelationInput relation) =>
        new(ProposedEntity.From(relation.Subject), relation.Predicate, ProposedEntity.From(relation.Object));
}

/// <summary>A staged write: what would be written, and what the decider proposed when it answered.</summary>
/// <param name="Operation">The decider's choice among the <c>memory_op</c> options, or null when it gave no answer.</param>
/// <param name="TargetFactId">For update and invalidate, the neighbour the operation applies to (the best-ranked one).</param>
/// <param name="BlockRevision">For a block edit, the revision the new content was written against.</param>
public sealed record MemoryProposal(
    string? Operation,
    string? TargetFactId,
    string? Text,
    ImmutableArray<ProposedEntity> Entities,
    DateTimeOffset? ValidFrom,
    long? SourceSequence,
    ImmutableArray<string> NeighbourIds,
    string? Block = null,
    string? Content = null,
    long? BlockRevision = null,
    ProposedRelation? Relation = null);

/// <param name="Reason">Why it was staged: the policy's reason (<c>no answer</c> from the degraded tier, <c>below threshold</c>), or <c>over_budget</c>.</param>
/// <param name="DecisionId">The <c>memory_op</c> decision, whose outcome is recorded when the write is resolved.</param>
public sealed record PendingWrite(
    string PendingId,
    string SessionId,
    PendingKind Kind,
    MemoryProposal Proposal,
    string? DecisionId,
    string Reason,
    DateTimeOffset CreatedAt,
    PendingStatus Status,
    DateTimeOffset? ResolvedAt,
    string? Resolution);

/// <summary>The staging table behind <c>/memory pending</c> (plan §9.2, Hermes' <c>write_approval</c>).</summary>
internal sealed class PendingStore(MemoryDatabase database)
{
    public PendingWrite Stage(string sessionId, PendingKind kind, MemoryProposal proposal, string? decisionId, string reason,
        DateTimeOffset now)
    {
        var pending = new PendingWrite("pending:" + Guid.NewGuid().ToString("N"), sessionId, kind, proposal, decisionId, reason, now,
            PendingStatus.Pending, null, null);
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO pending_writes (pending_id, session_id, kind, proposal, decision_id, reason, created_at, status)
            VALUES ($id, $session, $kind, $proposal, $decision, $reason, $at, 'pending');
            """;
        command.Parameters.AddWithValue("$id", pending.PendingId);
        command.Parameters.AddWithValue("$session", sessionId);
        command.Parameters.AddWithValue("$kind", Wire(kind));
        command.Parameters.AddWithValue("$proposal", JsonSerializer.Serialize(proposal, MemoryJsonContext.Default.MemoryProposal));
        command.Parameters.AddWithValue("$decision", (object?)decisionId ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$at", Ticks.From(now));
        command.ExecuteNonQuery();
        return pending;
    }

    public IReadOnlyList<PendingWrite> List(PendingStatus status)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM pending_writes WHERE status=$status ORDER BY created_at, pending_id;";
        command.Parameters.AddWithValue("$status", Wire(status));
        return Read(command);
    }

    public PendingWrite? Get(string pendingId)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM pending_writes WHERE pending_id=$id;";
        command.Parameters.AddWithValue("$id", pendingId);
        return Read(command).SingleOrDefault();
    }

    /// <summary>Resolves a pending write once; false when it was already resolved.</summary>
    public bool Resolve(string pendingId, PendingStatus status, string resolution, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE pending_writes SET status=$status, resolved_at=$at, resolution=$resolution
            WHERE pending_id=$id AND status='pending';
            """;
        command.Parameters.AddWithValue("$status", Wire(status));
        command.Parameters.AddWithValue("$at", Ticks.From(now));
        command.Parameters.AddWithValue("$resolution", resolution);
        command.Parameters.AddWithValue("$id", pendingId);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>Undoes a resolution whose write was then refused, so the write is pending again.</summary>
    public void Reopen(string pendingId, PendingStatus from)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE pending_writes SET status='pending', resolved_at=NULL, resolution=NULL
            WHERE pending_id=$id AND status=$from;
            """;
        command.Parameters.AddWithValue("$id", pendingId);
        command.Parameters.AddWithValue("$from", Wire(from));
        command.ExecuteNonQuery();
    }

    private const string Columns = "pending_id, session_id, kind, proposal, decision_id, reason, created_at, status, resolved_at, resolution";

    private static List<PendingWrite> Read(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var result = new List<PendingWrite>();
        while (reader.Read())
            result.Add(new PendingWrite(reader.GetString(0), reader.GetString(1),
                reader.GetString(2) == "block" ? PendingKind.Block : PendingKind.Fact,
                JsonSerializer.Deserialize(reader.GetString(3), MemoryJsonContext.Default.MemoryProposal)
                    ?? throw new InvalidDataException("Null memory proposal."),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), Ticks.To(reader.GetInt64(6)),
                reader.GetString(7) switch
                {
                    "pending" => PendingStatus.Pending,
                    "approved" => PendingStatus.Approved,
                    _ => PendingStatus.Rejected,
                },
                Ticks.ToNullable(reader, 8), reader.IsDBNull(9) ? null : reader.GetString(9)));
        return result;
    }

    private static string Wire(PendingKind kind) => kind == PendingKind.Block ? "block" : "fact";

    private static string Wire(PendingStatus status) => status switch
    {
        PendingStatus.Pending => "pending",
        PendingStatus.Approved => "approved",
        _ => "rejected",
    };
}

// Persisted in pending_writes.proposal: renaming a property needs a migration.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MemoryProposal))]
internal sealed partial class MemoryJsonContext : JsonSerializerContext;
