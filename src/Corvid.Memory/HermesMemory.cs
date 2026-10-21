using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;

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

/// <summary>Where the Hermes memories directory is (<see cref="Directory"/>), or why it cannot be found (<see cref="Problem"/>).</summary>
public sealed record HermesMemoryLocation(string? Directory, string? Problem);

/// <summary>
/// Hermes Agent's built-in memory files (D-9, C-027): <c>$HERMES_HOME/memories/MEMORY.md</c> (the agent's notes) and
/// <c>USER.md</c> (the user profile), <c>$HERMES_HOME</c> defaulting to <c>~/.hermes</c>. Read as Hermes reads them
/// (NousResearch/hermes-agent, <c>tools/memory_tool_store.py</c>, <c>MemoryStore._read_raw_checked</c> and
/// <c>_parse_entries</c>, checked at 1ab79836): strict UTF-8 with a leading BOM stripped, entries split on the whole
/// <c>"\n§\n"</c> delimiter so a bare <c>§</c> inside an entry survives, each entry stripped, empty ones dropped, and
/// repeats dropped keeping the first.
/// </summary>
public static partial class HermesMemory
{
    /// <summary>Hermes' <c>ENTRY_DELIMITER</c>.</summary>
    public const string EntryDelimiter = "\n§\n";

    public const string AgentFile = "MEMORY.md";
    public const string UserFile = "USER.md";

    /// <summary>Largest file read: Hermes' default limits are a few thousand characters, so a larger file is not a memory file.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// The memories directory, as Hermes' <c>get_hermes_home</c> and <c>get_memory_dir</c> find it (<c>hermes_constants.py</c>):
    /// an explicit home, else <c>$HERMES_HOME</c> trimmed, with <c>$VAR</c>, <c>${VAR}</c> (and on Windows <c>%VAR%</c>) and a
    /// leading <c>~</c> expanded as <c>os.path.expandvars</c> and <c>expanduser</c> do, else the platform default,
    /// <c>~/.hermes</c> or <c>%LOCALAPPDATA%\hermes</c> with <c>$HERMES_DATA_DIR_SUFFIX</c> appended; then <c>memories</c>.
    /// </summary>
    /// <param name="hermesHome">An explicit Hermes home, which wins over the environment.</param>
    /// <returns>
    /// The directory, or a problem when <c>HERMES_HOME</c> is set but still not an absolute path after expansion: Hermes would
    /// resolve it against its own working directory, which the import cannot know, and falling back to the default would
    /// import another profile's memories.
    /// </returns>
    public static HermesMemoryLocation Directory(string home, string? hermesHome = null, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(home);
        if (hermesHome is { Length: > 0 })
            return new HermesMemoryLocation(System.IO.Path.Combine(hermesHome, "memories"), null);
        environment ??= Environment.GetEnvironmentVariable;
        var configured = environment("HERMES_HOME")?.Trim();
        if (configured is not { Length: > 0 })
            return new HermesMemoryLocation(System.IO.Path.Combine(DefaultHome(home, environment), "memories"), null);
        var expanded = Expand(configured, home, environment);
        return System.IO.Path.IsPathFullyQualified(expanded)
            ? new HermesMemoryLocation(System.IO.Path.Combine(expanded, "memories"), null)
            : new HermesMemoryLocation(null,
                $"HERMES_HOME is set to '{configured}', which is not an absolute path; give the Hermes home explicitly.");
    }

    /// <summary>Hermes' <c>_platform_default_hermes_home</c>; the suffix is appended literally, as in Hermes.</summary>
    private static string DefaultHome(string home, Func<string, string?> environment)
    {
        var suffix = environment("HERMES_DATA_DIR_SUFFIX") ?? "";
        if (!OperatingSystem.IsWindows())
            return System.IO.Path.Combine(home, ".hermes" + suffix);
        var localAppData = environment("LOCALAPPDATA")?.Trim();
        return System.IO.Path.Combine(localAppData is { Length: > 0 } ? localAppData : System.IO.Path.Combine(home, "AppData", "Local"),
            "hermes" + suffix);
    }

    /// <summary><c>os.path.expanduser(os.path.expandvars(value))</c>: an undefined variable is left as written.</summary>
    private static string Expand(string value, string home, Func<string, string?> environment)
    {
        var expanded = Variable().Replace(value, match =>
        {
            if (match.Groups[1].Success && !OperatingSystem.IsWindows())
                return match.Value;
            var name = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value
                : match.Groups[3].Value;
            return environment(name) ?? match.Value;
        });
        if (expanded == "~")
            return home;
        if (expanded.StartsWith("~/", StringComparison.Ordinal)
            || (OperatingSystem.IsWindows() && expanded.StartsWith("~\\", StringComparison.Ordinal)))
            return System.IO.Path.Combine(home, expanded[2..]);
        return expanded;
    }

    [GeneratedRegex(@"%([^%]+)%|\$\{([^}]+)\}|\$([A-Za-z0-9_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Variable();

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
