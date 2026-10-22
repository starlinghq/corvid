using System.Collections.Immutable;
using System.Text;

namespace Corvid.Memory;

/// <summary>One Hermes memory file's entries, or why it could not be read.</summary>
/// <param name="File">The file name: <c>MEMORY.md</c> or <c>USER.md</c>.</param>
/// <param name="Exists">Whether the file was there. A missing file has no entries, as in Hermes.</param>
/// <param name="Entries">The entries in file order, stripped, blanks and repeats dropped.</param>
/// <param name="Blocked">
/// Entries that are a <c>[BLOCKED: …]</c> placeholder, which are not memories. Hermes writes that placeholder only into its
/// system-prompt snapshot, never to these files, so one appears here only when copied in by hand; the files hold the raw
/// entries, and the import screens them itself (<c>SkillScanner</c>), not with Hermes' <c>scan_for_threats</c> rules.
/// </param>
/// <param name="Problem">Why the whole file was refused (not valid UTF-8), or null.</param>
public sealed record HermesMemoryFile(string File, string Path, bool Exists, ImmutableArray<string> Entries, int Blocked,
    string? Problem);

/// <summary>
/// Hermes Agent's built-in memory files (D-9, C-027): <c>$HERMES_HOME/memories/MEMORY.md</c> (the agent's notes) and
/// <c>USER.md</c> (the user profile); the host finds the Hermes home (<c>HermesHome</c> in <c>Corvid.Host</c>, C-035).
/// Read as Hermes reads them (NousResearch/hermes-agent, <c>tools/memory_tool_store.py</c>, <c>MemoryStore._read_raw_checked</c> and
/// <c>_parse_entries</c>, checked at 1ab79836): strict UTF-8 with a leading BOM stripped, entries split on the whole
/// <c>"\n§\n"</c> delimiter so a bare <c>§</c> inside an entry survives, each entry stripped, empty ones dropped, and
/// repeats dropped keeping the first.
/// </summary>
public static class HermesMemory
{
    /// <summary>Hermes' <c>ENTRY_DELIMITER</c>.</summary>
    public const string EntryDelimiter = "\n§\n";

    public const string AgentFile = "MEMORY.md";
    public const string UserFile = "USER.md";

    /// <summary>Largest file read: Hermes' default limits are a few thousand characters, so a larger file is not a memory file.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Both files of a memories directory, <c>MEMORY.md</c> first.</summary>
    public static ImmutableArray<HermesMemoryFile> Read(string directory) =>
        [ReadFile(System.IO.Path.Combine(directory, AgentFile)), ReadFile(System.IO.Path.Combine(directory, UserFile))];

    public static HermesMemoryFile ReadFile(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        if (!File.Exists(path))
            return new HermesMemoryFile(name, path, false, [], 0, null);
        if (new FileInfo(path).Length > MaxFileBytes)
            return new HermesMemoryFile(name, path, true, [], 0, $"over {MaxFileBytes / 1024} KiB, which no Hermes memory file is");
        string raw;
        try
        {
            // Bytes, then a throwing decoder: File.ReadAllText would swap in its own, lenient decoder on a BOM.
            raw = Strict.GetString(File.ReadAllBytes(path));
        }
        catch (DecoderFallbackException)
        {
            // Hermes refuses an undecodable file rather than read a lossy copy of it; so does the import.
            return new HermesMemoryFile(name, path, true, [], 0, "not valid UTF-8");
        }
        if (raw.StartsWith('\uFEFF'))
            raw = raw[1..];
        // Python reads text with universal newlines, so a hand-edited CRLF file splits the same way.
        raw = raw.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var (entries, blocked) = Parse(raw);
        return new HermesMemoryFile(name, path, true, entries, blocked, null);
    }

    /// <summary>Hermes' <c>_parse_entries</c> and the load-time deduplication, without entries shaped like its <c>[BLOCKED:</c> placeholder.</summary>
    public static (ImmutableArray<string> Entries, int Blocked) Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = ImmutableArray.CreateBuilder<string>();
        var blocked = 0;
        foreach (var part in raw.Split(EntryDelimiter))
        {
            var entry = part.Trim();
            if (entry.Length == 0 || !seen.Add(entry))
                continue;
            if (entry.StartsWith("[BLOCKED:", StringComparison.Ordinal))
                blocked++;
            else
                entries.Add(entry);
        }
        return (entries.ToImmutable(), blocked);
    }
}
