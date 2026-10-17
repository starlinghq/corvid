using System.Collections.Concurrent;
using System.Text.Json;

namespace Corvid.Vault;

internal sealed class AuditEvent
{
    public DateTimeOffset Time { get; set; }

    /// <summary><c>connect</c>, <c>placeholder</c>, <c>forward</c>, <c>scrub</c>, <c>set</c>, <c>remove</c>.</summary>
    public string Op { get; set; } = "";

    /// <summary><c>allowed</c>, <c>refused</c> or <c>error</c>.</summary>
    public string Decision { get; set; } = "";

    public string? Code { get; set; }

    public string? Secret { get; set; }

    public string? Session { get; set; }

    public int? Pid { get; set; }

    public uint? Uid { get; set; }

    public string? Destination { get; set; }

    public int? Status { get; set; }

    public long? Bytes { get; set; }

    public int? ScrubHits { get; set; }
}

/// <summary>Append-only JSON Lines audit log, mode 0600. Records names and decisions, never values.</summary>
internal sealed class AuditLog
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.Ordinal);

    public AuditLog(string path)
    {
        _path = path;
        if (File.Exists(path))
        {
            foreach (string line in File.ReadLines(path))
            {
                if (line.Length > 0 && JsonSerializer.Deserialize(line, VaultInternalJsonContext.Default.AuditEvent) is { } e)
                {
                    Count(e);
                }
            }
        }
    }

    public IReadOnlyDictionary<string, int> Counts => new Dictionary<string, int>(_counts, StringComparer.Ordinal);

    public void Write(AuditEvent e)
    {
        string line = JsonSerializer.Serialize(e, VaultInternalJsonContext.Default.AuditEvent) + "\n";
        lock (_gate)
        {
            var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using var writer = new StreamWriter(_path, System.Text.Encoding.UTF8, options);
            writer.Write(line);
        }

        Count(e);
    }

    public IEnumerable<string> Tail(int count) =>
        File.Exists(_path) ? File.ReadLines(_path).TakeLast(count) : [];

    private void Count(AuditEvent e) => _counts.AddOrUpdate($"{e.Op}.{e.Decision}", 1, (_, n) => n + 1);
}
