using System.Collections.Immutable;
using System.Text;
using Corvid.Abstractions.Tokens;
using Corvid.Fabric;
using Microsoft.Data.Sqlite;

namespace Corvid.Memory;

/// <summary>The three core blocks of plan §9.1. Names are persisted.</summary>
public static class CoreBlockNames
{
    /// <summary>Who the user is.</summary>
    public const string User = "user";
    /// <summary>The agent's standing notes.</summary>
    public const string Agent = "agent";
    /// <summary>The persona (SOUL). Only the user edits it; the write path refuses it.</summary>
    public const string Persona = "persona";

    public static ImmutableArray<string> All { get; } = [User, Agent, Persona];
}

/// <param name="BudgetTokens">The block's size budget, counted with the store's <see cref="ITokenCounter"/>.</param>
/// <param name="LocalOnly">Never sent to a model whose privacy class is not local (enforced by the snapshot's renderer).</param>
/// <param name="Revision">Starts at 0 and increases with every applied change.</param>
public sealed record CoreBlock(string Name, string Content, int BudgetTokens, bool LocalOnly, long Revision, DateTimeOffset UpdatedAt)
{
    public bool IsEmpty => Content.Length == 0;
}

/// <summary>
/// The core blocks as they were at one moment. A session takes one snapshot at its start and renders it into every prompt
/// (Hermes' cache-friendly design, plan §9.1), so edits during the session reach the next session, not this prompt prefix.
/// </summary>
public sealed record CoreSnapshot(ImmutableArray<CoreBlock> Blocks)
{
    public CoreBlock this[string name] => Blocks.Single(block => block.Name == name);

    /// <summary>The non-empty blocks in name order, leaving out local-only blocks unless <paramref name="includeLocalOnly"/>.</summary>
    /// <param name="includeLocalOnly">True only when the target model is local; a cloud or shared server must pass false.</param>
    public string Render(bool includeLocalOnly)
    {
        var builder = new StringBuilder();
        foreach (var block in Blocks.Where(block => !block.IsEmpty && (includeLocalOnly || !block.LocalOnly)))
        {
            if (builder.Length > 0)
                builder.Append('\n');
            builder.Append('[').Append(block.Name).Append("]\n").Append(block.Content).Append('\n');
        }
        return builder.ToString();
    }
}

/// <summary>Default budgets: about Hermes' character limits (user 1,375, memory 2,200) at four characters a token.</summary>
public sealed record CoreBlockOptions(int UserBudget = 350, int AgentBudget = 550, int PersonaBudget = 500)
{
    public static CoreBlockOptions Default { get; } = new();

    internal int BudgetOf(string name) => name switch
    {
        CoreBlockNames.User => UserBudget,
        CoreBlockNames.Agent => AgentBudget,
        CoreBlockNames.Persona => PersonaBudget,
        _ => throw new ArgumentException($"Unknown core block '{name}'.", nameof(name)),
    };
}

public enum BlockWriteStatus
{
    Applied,
    /// <summary>The content is over the block's budget; nothing changed. The write path stages it for a rewrite instead.</summary>
    OverBudget,
    /// <summary>The block changed since the caller read it; nothing changed.</summary>
    Conflict,
}

public sealed record BlockWriteResult(BlockWriteStatus Status, CoreBlock Block, int ContentTokens);

/// <summary>Core blocks (plan §9.1): SQLite rows with a token budget per block and a local-only flag.</summary>
public sealed class CoreBlockStore
{
    private readonly MemoryDatabase _database;
    private readonly TimeProvider _time;

    public CoreBlockStore(MemoryDatabase database, ITokenCounter? counter = null, CoreBlockOptions? options = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        Counter = counter ?? CharsPerTokenCounter.Instance;
        _time = time ?? TimeProvider.System;
        var budgets = options ?? CoreBlockOptions.Default;

        // Rows are created once with the configured budgets; later budget changes go through SetBudget.
        using var connection = _database.Open();
        foreach (var name in CoreBlockNames.All)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO core_blocks (name, content, budget_tokens, local_only, revision, updated_at)
                VALUES ($name, '', $budget, 0, 0, $at);
                """;
            command.Parameters.AddWithValue("$name", name);
            command.Parameters.AddWithValue("$budget", budgets.BudgetOf(name));
            command.Parameters.AddWithValue("$at", Ticks.From(_time.GetUtcNow()));
            command.ExecuteNonQuery();
        }
    }

    public ITokenCounter Counter { get; }

    public CoreBlock Get(string name)
    {
        CheckName(name);
        using var connection = _database.Open();
        return Read(connection, null, name);
    }

    public CoreSnapshot Snapshot()
    {
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        return new CoreSnapshot([.. CoreBlockNames.All.Order(StringComparer.Ordinal).Select(name => Read(connection, transaction, name))]);
    }

    public int CountTokens(string content) => Counter.CountTokens(content);

    /// <summary>
    /// Replaces a block's content. A reviewed write: the user's own edit, or an approved staged one. Content over the budget
    /// is refused rather than truncated.
    /// </summary>
    /// <param name="expectedRevision">When given, the write applies only if the block is still at that revision.</param>
    public BlockWriteResult Set(string name, string content, long? expectedRevision = null)
    {
        CheckName(name);
        ArgumentNullException.ThrowIfNull(content);
        var tokens = Counter.CountTokens(content);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        var current = Read(connection, transaction, name);
        if (expectedRevision is { } expected && expected != current.Revision)
            return new(BlockWriteStatus.Conflict, current, tokens);
        if (tokens > current.BudgetTokens)
            return new(BlockWriteStatus.OverBudget, current, tokens);
        var updated = current with { Content = content, Revision = current.Revision + 1, UpdatedAt = _time.GetUtcNow() };
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE core_blocks SET content=$content, revision=$revision, updated_at=$at WHERE name=$name;";
        command.Parameters.AddWithValue("$content", updated.Content);
        command.Parameters.AddWithValue("$revision", updated.Revision);
        command.Parameters.AddWithValue("$at", Ticks.From(updated.UpdatedAt));
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
        transaction.Commit();
        return new(BlockWriteStatus.Applied, updated, tokens);
    }

    public CoreBlock SetLocalOnly(string name, bool localOnly) => Update(name, "local_only", localOnly ? 1 : 0);

    /// <summary>Changes a budget. Content already over a lowered budget stays until a rewrite; new writes must fit.</summary>
    public CoreBlock SetBudget(string name, int budgetTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(budgetTokens);
        return Update(name, "budget_tokens", budgetTokens);
    }

    private CoreBlock Update(string name, string column, int value)
    {
        CheckName(name);
        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        // The column is one of two constants above, never caller text.
        command.CommandText = $"UPDATE core_blocks SET {column}=$value, revision=revision+1, updated_at=$at WHERE name=$name;";
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$at", Ticks.From(_time.GetUtcNow()));
        command.Parameters.AddWithValue("$name", name);
        command.ExecuteNonQuery();
        var result = Read(connection, transaction, name);
        transaction.Commit();
        return result;
    }

    private static CoreBlock Read(SqliteConnection connection, SqliteTransaction? transaction, string name)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT content, budget_tokens, local_only, revision, updated_at FROM core_blocks WHERE name=$name;";
        command.Parameters.AddWithValue("$name", name);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidDataException($"Core block '{name}' is missing.");
        return new CoreBlock(name, reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2) == 1, reader.GetInt64(3),
            Ticks.To(reader.GetInt64(4)));
    }

    internal static void CheckName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!CoreBlockNames.All.Contains(name))
            throw new ArgumentException($"Unknown core block '{name}'.", nameof(name));
    }
}
