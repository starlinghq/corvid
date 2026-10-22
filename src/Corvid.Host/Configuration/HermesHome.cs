using System.Text.RegularExpressions;

namespace Corvid.Host.Configuration;

/// <summary>
/// The Hermes home an import reads (<see cref="Home"/>), the sticky profile that chose it (<see cref="Profile"/>), or why
/// it cannot be found (<see cref="Problem"/>).
/// </summary>
/// <param name="Profile">The named profile <c>active_profile</c> selected, or null for the root (or an explicit or profile home).</param>
public sealed record HermesHomeLocation(string? Home, string? Profile, string? Problem);

/// <summary>
/// Where Hermes Agent keeps its files (D-9, C-035), resolved once for both imports: <c>/memory import hermes</c> reads
/// <c>&lt;home&gt;/memories</c> and <c>/skills import hermes</c> reads <c>&lt;home&gt;/skills</c>. It follows what a
/// <c>hermes</c> process started from the same environment would use (NousResearch/hermes-agent, <c>hermes_constants.py</c>
/// <c>get_default_hermes_root</c>, <c>_hermes_root_of</c>, <c>_expand_hermes_home</c> and <c>_platform_default_hermes_home</c>;
/// <c>hermes_cli/main.py</c> <c>_apply_profile_override</c>; <c>hermes_cli/profiles.py</c> <c>normalize_profile_name</c>,
/// <c>validate_profile_name</c> and <c>resolve_profile_env</c>; checked at 55ee4acd):
/// <list type="number">
/// <item>An explicit home wins, used as given, with no profile applied.</item>
/// <item><c>$HERMES_HOME</c> is trimmed, and <c>$VAR</c>, <c>${VAR}</c> (on Windows <c>%VAR%</c>) and a leading <c>~</c>
/// are expanded as <c>os.path.expandvars</c> and <c>expanduser</c> do. Still not absolute: a problem.</item>
/// <item>A <c>HERMES_HOME</c> whose parent is named <c>profiles</c> is a profile's home and is used as it is: Hermes then
/// skips the sticky profile. (Hermes looks at the unexpanded value; Corvid at the expanded path.)</item>
/// <item>Otherwise the sticky profile is read from <c>active_profile</c> in the Hermes root: the platform default
/// (<c>~/.hermes</c> or <c>%LOCALAPPDATA%\hermes</c>, with <c>$HERMES_DATA_DIR_SUFFIX</c>) when <c>HERMES_HOME</c> is unset or
/// lies inside it, else <c>HERMES_HOME</c>. The file is read as UTF-8 with a BOM dropped and trimmed; a read error counts
/// as no file.</item>
/// <item>Empty or <c>default</c> (any case): the home is <c>HERMES_HOME</c>, or the platform default when it is unset.
/// Otherwise the name is lowercased and must match Hermes' profile id (<c>^[a-z0-9][a-z0-9_-]{0,63}$</c>) and not be one
/// of its reserved names, and the home is <c>profiles/&lt;name&gt;</c> under that same directory, which must exist and not
/// be tombstoned (<c>profiles/.deleted/&lt;name&gt;</c>).</item>
/// </list>
/// </summary>
/// <remarks>
/// Every refusal is a problem rather than a fallback: Hermes itself exits on an invalid or missing sticky profile, and
/// importing another profile's data silently is worse than asking for the home. Not followed: Hermes' <c>-p</c> flag, which
/// belongs to one invocation, its recovery commands' fallback to the default profile, its identity-marker test for a profile
/// directory (any existing directory counts here), and its <c>sudo</c> remapping.
/// </remarks>
public static partial class HermesHome
{
    /// <summary>The file in the Hermes root that names the sticky profile (<c>hermes profile use</c>).</summary>
    public const string ActiveProfileFile = "active_profile";

    // hermes_cli/profiles.py _RESERVED_NAMES: validate_profile_name refuses them, so a sticky one makes Hermes exit.
    private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal) { "hermes", "test", "tmp", "root", "sudo" };

    /// <param name="home">The user's home directory, for <c>~</c> and the platform default.</param>
    /// <param name="explicitHome">A Hermes home the user named, which wins over the environment.</param>
    /// <param name="environment">Reads an environment variable; the process environment by default.</param>
    /// <param name="readFile">Reads a text file, null when it cannot; the file system by default.</param>
    public static HermesHomeLocation Resolve(string home, string? explicitHome = null, Func<string, string?>? environment = null,
        Func<string, string?>? readFile = null)
    {
        ArgumentNullException.ThrowIfNull(home);
        if (explicitHome is { Length: > 0 })
            return new HermesHomeLocation(explicitHome, null, null);
        environment ??= Environment.GetEnvironmentVariable;
        readFile ??= ReadFile;

        var native = DefaultHome(home, environment);
        var configured = environment("HERMES_HOME")?.Trim();
        string? configuredHome = null;
        if (configured is { Length: > 0 })
        {
            configuredHome = Expand(configured, home, environment);
            if (!Path.IsPathFullyQualified(configuredHome))
                return new HermesHomeLocation(null, null,
                    $"HERMES_HOME is set to '{configured}', which is not an absolute path; give the Hermes home explicitly.");
            if (string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(configuredHome))), "profiles",
                    StringComparison.Ordinal))
                return new HermesHomeLocation(configuredHome, null, null);
        }

        var root = configuredHome is null || Inside(configuredHome, native) ? native : configuredHome;
        var activeFile = Path.Combine(root, ActiveProfileFile);
        var active = readFile(activeFile)?.TrimStart('﻿').Trim();
        var profilesParent = configuredHome ?? native;
        if (active is not { Length: > 0 } || string.Equals(active, "default", StringComparison.OrdinalIgnoreCase))
            return new HermesHomeLocation(profilesParent, null, null);

        var name = active.ToLowerInvariant();
        if (!ProfileId().IsMatch(name) || Reserved.Contains(name))
            return new HermesHomeLocation(null, name,
                $"Hermes' active profile '{active}' (in {activeFile}) is not a valid profile name; give the Hermes home explicitly.");
        var profiles = Path.Combine(profilesParent, "profiles");
        var profileHome = Path.Combine(profiles, name);
        if (!Directory.Exists(profileHome) || Path.Exists(Path.Combine(profiles, ".deleted", name)))
            return new HermesHomeLocation(null, name,
                $"Hermes' active profile is '{name}' (in {activeFile}), but there is no profile at {profileHome}; give the Hermes home explicitly.");
        return new HermesHomeLocation(profileHome, name, null);
    }

    /// <summary>Hermes' <c>_platform_default_hermes_home</c>; the suffix is appended literally, as in Hermes.</summary>
    private static string DefaultHome(string home, Func<string, string?> environment)
    {
        var suffix = environment("HERMES_DATA_DIR_SUFFIX") ?? "";
        if (!OperatingSystem.IsWindows())
            return Path.Combine(home, ".hermes" + suffix);
        var localAppData = environment("LOCALAPPDATA")?.Trim();
        return Path.Combine(localAppData is { Length: > 0 } ? localAppData : Path.Combine(home, "AppData", "Local"), "hermes" + suffix);
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
            return Path.Combine(home, expanded[2..]);
        return expanded;
    }

    // _hermes_root_of: a HERMES_HOME at or under the platform default (normal or profile mode) has that default as its root.
    // Lexical, where Hermes resolves links.
    private static bool Inside(string path, string root)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative == "." || !(relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                                    || Path.IsPathRooted(relative));
    }

    private static string? ReadFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"%([^%]+)%|\$\{([^}]+)\}|\$([A-Za-z0-9_]+)", RegexOptions.CultureInvariant)]
    private static partial Regex Variable();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ProfileId();
}
