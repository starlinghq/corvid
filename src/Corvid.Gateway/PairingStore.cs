using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Corvid.Gateway;

/// <summary>Who a sender is to the agent.</summary>
public enum SenderRole
{
    /// <summary>Unpaired: they get the pairing flow and nothing else.</summary>
    None,
    /// <summary>Allowed to talk to the agent; their messages are outside content and taint the session (plan §10.3).</summary>
    Guest,
    /// <summary>The user. Their messages are the user's, and only they can run commands and answer approvals.</summary>
    Owner,
}

public sealed record PairedUser(string Adapter, string UserId, string? Name, SenderRole Role, DateTimeOffset PairedAt);

/// <param name="Code">Shown as <c>ABCD-EFGH</c>; the owner approves it on the machine.</param>
public sealed record PairingRequest(string Code, string Adapter, string UserId, string? Name, string ChatId, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>A sender asked to pair: the code to tell them, or null when they have been told recently or too many codes wait.</summary>
public sealed record PairingTicket(PairingRequest? Request, bool Notify);

/// <summary>
/// Pairing and the gateway's small durable state (plan §12, as in Hermes): <c>gateway.db</c>, its own SQLite file because
/// pairings change. An unknown sender writing to the bot directly (or naming it in a group) gets a code; the owner approves
/// it on the machine (<c>corvid gateway pair approve CODE</c>), never from a chat, so holding a chat account is not enough to
/// become the owner. Codes are random, bound to the sender who asked, few per adapter, and expire; a sender is told their
/// code at most once per <see cref="RemindAfter"/>. Allowlists in configuration need no pairing. Also kept here: adapters'
/// polling cursors and the delivery ledger's low-water mark.
/// </summary>
public sealed class PairingStore
{
    private const int SchemaVersion = 1;
    // No 0/O, 1/I/L: codes are read off a phone and typed into a terminal.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly string _connectionString;
    private readonly TimeProvider _time;

    public PairingStore(string path, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path == ":memory:")
            throw new ArgumentException("The pairing store requires an on-disk database.", nameof(path));
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        _time = time ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5,
        }.ToString();

        using var connection = Open();
        using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        if (version is not (0 or SchemaVersion))
            throw new InvalidDataException($"Unsupported gateway database schema version {version}.");
        if (version == 0)
        {
            command.CommandText = Schema;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public string Path { get; }

    public TimeSpan CodeLifetime { get; init; } = TimeSpan.FromHours(1);

    public TimeSpan RemindAfter { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Codes waiting per adapter; further unknown senders are ignored until some expire or are approved.</summary>
    public int MaxPendingPerAdapter { get; init; } = 10;

    /// <summary>The sender's role: configuration first (owners, then guests), then pairing.</summary>
    public SenderRole RoleOf(AdapterOptions adapter, string userId)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        if (adapter.Owners.Contains(userId))
            return SenderRole.Owner;
        if (adapter.Guests.Contains(userId))
            return SenderRole.Guest;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT role FROM users WHERE adapter = $adapter AND user_id = $user;";
        command.Parameters.AddWithValue("$adapter", adapter.Name);
        command.Parameters.AddWithValue("$user", userId);
        return command.ExecuteScalar() is string role ? ParseRole(role) : SenderRole.None;
    }

    /// <summary>Paired users, by adapter then id.</summary>
    public IReadOnlyList<PairedUser> Users(string? adapter = null)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT adapter, user_id, name, role, paired_at FROM users " +
            (adapter is null ? "" : "WHERE adapter = $adapter ") + "ORDER BY adapter, user_id;";
        if (adapter is not null)
            command.Parameters.AddWithValue("$adapter", adapter);
        using var reader = command.ExecuteReader();
        var users = new List<PairedUser>();
        while (reader.Read())
            users.Add(new PairedUser(reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                ParseRole(reader.GetString(3)), Time(reader.GetInt64(4))));
        return users;
    }

    /// <summary>
    /// An unknown sender asked to pair. Returns their unexpired code (or a new one), and whether to tell them now; a new code
    /// is refused (null) while the adapter already has <see cref="MaxPendingPerAdapter"/> waiting.
    /// </summary>
    public PairingTicket Request(string adapter, string userId, string? name, string chatId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapter);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        var now = _time.GetUtcNow();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM codes WHERE expires_at <= $now;";
        command.Parameters.AddWithValue("$now", now.UtcTicks);
        command.ExecuteNonQuery();

        command.CommandText = "SELECT code, name, chat_id, created_at, expires_at, notified_at FROM codes WHERE adapter = $adapter AND user_id = $user;";
        command.Parameters.AddWithValue("$adapter", adapter);
        command.Parameters.AddWithValue("$user", userId);
        using (var reader = command.ExecuteReader())
        {
            if (reader.Read())
            {
                var request = new PairingRequest(reader.GetString(0), adapter, userId, reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2), Time(reader.GetInt64(3)), Time(reader.GetInt64(4)));
                var notify = Time(reader.GetInt64(5)) + RemindAfter <= now;
                reader.Close();
                if (notify)
                {
                    command.CommandText = "UPDATE codes SET notified_at = $now WHERE code = $code;";
                    command.Parameters.AddWithValue("$code", request.Code);
                    command.ExecuteNonQuery();
                }
                transaction.Commit();
                return new PairingTicket(request, notify);
            }
        }

        command.CommandText = "SELECT COUNT(*) FROM codes WHERE adapter = $adapter;";
        if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) >= MaxPendingPerAdapter)
        {
            transaction.Commit();
            return new PairingTicket(null, false);
        }
        var created = new PairingRequest(NewCode(), adapter, userId, name, chatId, now, now + CodeLifetime);
        command.CommandText = """
            INSERT INTO codes (code, adapter, user_id, name, chat_id, created_at, expires_at, notified_at)
            VALUES ($code, $adapter, $user, $name, $chat, $now, $expires, $now);
            """;
        command.Parameters.AddWithValue("$code", created.Code);
        command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("$chat", chatId);
        command.Parameters.AddWithValue("$expires", created.ExpiresAt.UtcTicks);
        command.ExecuteNonQuery();
        transaction.Commit();
        return new PairingTicket(created, true);
    }

    /// <summary>Codes waiting for the owner, oldest first.</summary>
    public IReadOnlyList<PairingRequest> Pending()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT code, adapter, user_id, name, chat_id, created_at, expires_at FROM codes WHERE expires_at > $now ORDER BY created_at;";
        command.Parameters.AddWithValue("$now", _time.GetUtcNow().UtcTicks);
        using var reader = command.ExecuteReader();
        var pending = new List<PairingRequest>();
        while (reader.Read())
            pending.Add(new PairingRequest(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4), Time(reader.GetInt64(5)), Time(reader.GetInt64(6))));
        return pending;
    }

    /// <summary>Approves a code (case and the dash do not matter) as owner or guest. Null when no such unexpired code exists.</summary>
    public PairedUser? Approve(string code, SenderRole role = SenderRole.Owner)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (role == SenderRole.None)
            throw new ArgumentException("Approve as owner or guest.", nameof(role));
        var normalized = Normalize(code);
        if (normalized is null)
            return null;
        var now = _time.GetUtcNow();
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT adapter, user_id, name FROM codes WHERE code = $code AND expires_at > $now;";
        command.Parameters.AddWithValue("$code", normalized);
        command.Parameters.AddWithValue("$now", now.UtcTicks);
        string adapter, user;
        string? name;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                return null;
            (adapter, user, name) = (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
        }
        command.CommandText = """
            INSERT INTO users (adapter, user_id, name, role, paired_at) VALUES ($adapter, $user, $name, $role, $now)
            ON CONFLICT (adapter, user_id) DO UPDATE SET name = excluded.name, role = excluded.role, paired_at = excluded.paired_at;
            DELETE FROM codes WHERE code = $code;
            """;
        command.Parameters.AddWithValue("$adapter", adapter);
        command.Parameters.AddWithValue("$user", user);
        command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        command.Parameters.AddWithValue("$role", RoleName(role));
        command.ExecuteNonQuery();
        transaction.Commit();
        return new PairedUser(adapter, user, name, role, now);
    }

    /// <summary>
    /// Drops a waiting code without pairing anyone (G-14, the app's Ignore): the sender stays unpaired, and their next
    /// message gets a new code. Case and the dash do not matter. Returns whether such a code was waiting.
    /// </summary>
    public bool Dismiss(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        if (Normalize(code) is not { } normalized)
            return false;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM codes WHERE code = $code;";
        command.Parameters.AddWithValue("$code", normalized);
        return command.ExecuteNonQuery() > 0;
    }

    /// <summary>Removes a pairing (configuration allowlists are not affected). Returns whether one existed.</summary>
    public bool Revoke(string adapter, string userId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM users WHERE adapter = $adapter AND user_id = $user;";
        command.Parameters.AddWithValue("$adapter", adapter);
        command.Parameters.AddWithValue("$user", userId);
        return command.ExecuteNonQuery() > 0;
    }

    public string? GetState(string key)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM state WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    public void SetState(string key, string value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO state (key, value) VALUES ($key, $value) ON CONFLICT (key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    /// <summary><c>abcd-efgh</c>, <c>ABCDEFGH</c> → <c>ABCDEFGH</c>; null when it cannot be a code.</summary>
    public static string? Normalize(string code)
    {
        var compact = code.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
        return compact.Length == 8 && compact.All(c => Alphabet.Contains(c, StringComparison.Ordinal)) ? compact : null;
    }

    /// <summary>For people: <c>ABCD-EFGH</c>.</summary>
    public static string Display(string code) => code.Length == 8 ? code[..4] + "-" + code[4..] : code;

    private static string NewCode() => RandomNumberGenerator.GetString(Alphabet, 8);

    private static string RoleName(SenderRole role) => role == SenderRole.Owner ? "owner" : "guest";

    private static SenderRole ParseRole(string role) => role switch
    {
        "owner" => SenderRole.Owner,
        "guest" => SenderRole.Guest,
        _ => throw new InvalidDataException($"Unknown pairing role '{role}'."),
    };

    private static DateTimeOffset Time(long ticks) => new(ticks, TimeSpan.Zero);

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA synchronous=FULL;";
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private const string Schema = """
        CREATE TABLE users (
            adapter TEXT NOT NULL,
            user_id TEXT NOT NULL,
            name TEXT,
            role TEXT NOT NULL CHECK (role IN ('owner', 'guest')),
            paired_at INTEGER NOT NULL,
            PRIMARY KEY (adapter, user_id)
        );

        CREATE TABLE codes (
            code TEXT PRIMARY KEY,
            adapter TEXT NOT NULL,
            user_id TEXT NOT NULL,
            name TEXT,
            chat_id TEXT NOT NULL,
            created_at INTEGER NOT NULL,
            expires_at INTEGER NOT NULL,
            notified_at INTEGER NOT NULL,
            UNIQUE (adapter, user_id)
        );

        CREATE TABLE state (
            key TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        PRAGMA user_version = 1;
        """;
}
