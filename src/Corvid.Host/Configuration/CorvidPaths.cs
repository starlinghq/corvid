namespace Corvid.Host.Configuration;

/// <summary>The configuration file this host read (it may not exist), for <c>corvid doctor</c> and <c>corvid model switch</c>.</summary>
public sealed record CorvidConfigFile(string Path);

/// <summary>Where Corvid keeps its files, following the XDG base directories on Linux.</summary>
public static class CorvidPaths
{
    /// <summary>Overrides the user configuration file.</summary>
    public const string ConfigVariable = "CORVID_CONFIG";

    public static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary><c>$XDG_CONFIG_HOME/corvid</c>, else <c>~/.config/corvid</c>. Approvals protect it from every tool.</summary>
    public static string ConfigDirectory => Path.Combine(Xdg("XDG_CONFIG_HOME", ".config"), "corvid");

    /// <summary><c>$CORVID_CONFIG</c>, else <c>corvid.json</c> in <see cref="ConfigDirectory"/>.</summary>
    public static string UserConfigFile =>
        Environment.GetEnvironmentVariable(ConfigVariable) is { Length: > 0 } path ? ExpandHome(path) : Path.Combine(ConfigDirectory, "corvid.json");

    /// <summary><c>$XDG_DATA_HOME/corvid</c>, else <c>~/.local/share/corvid</c>.</summary>
    public static string DefaultDataDirectory => Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(".local", "share")), "corvid");

    public static string DataDirectory(CorvidOptions options) =>
        options.DataDirectory is { Length: > 0 } directory ? ExpandHome(directory) : DefaultDataDirectory;

    public static string Workspace(CorvidOptions options) =>
        options.Workspace is { Length: > 0 } workspace ? ExpandHome(workspace) : Directory.GetCurrentDirectory();

    public static string EventLog(CorvidOptions options) => Path.Combine(DataDirectory(options), "events.db");

    public static string MemoryDatabase(CorvidOptions options) => Path.Combine(DataDirectory(options), "memory.db");

    /// <summary>The durable job store (C-012), its own SQLite file because jobs change.</summary>
    public static string JobStore(CorvidOptions options) => Path.Combine(DataDirectory(options), "jobs.db");

    /// <summary>The checkpoint stores (C-014): one shadow git repository per workspace, never inside it.</summary>
    public static string Checkpoints(CorvidOptions options) => Path.Combine(DataDirectory(options), "checkpoints");

    /// <summary>The skills repository (C-017). Default <c>$XDG_DATA_HOME/corvid-skills</c>, beside the data directory, not in it.</summary>
    public static string SkillsDirectory(CorvidOptions options) =>
        options.Skills.Directory is { Length: > 0 } directory ? ExpandHome(directory)
            : Path.Combine(Xdg("XDG_DATA_HOME", Path.Combine(".local", "share")), "corvid-skills");

    /// <summary>The gateway's pairings, polling cursors and ledger mark (C-020); the ledger itself is in the event log.</summary>
    public static string GatewayDatabase(CorvidOptions options) => Path.Combine(DataDirectory(options), "gateway.db");

    /// <summary>
    /// The pinned MCP tool definitions (C-022): Corvid state in the data directory, which no tool and no sandboxed server
    /// may write, never in the user's configuration.
    /// </summary>
    public static string McpPins(CorvidOptions options) => Path.Combine(DataDirectory(options), "mcp-pins.json");

    /// <summary>
    /// The sessions' driver locks (G-12): one lock file per session a process has opened to drive, with the note saying
    /// which process. Beside the event log they guard.
    /// </summary>
    public static string SessionDrivers(CorvidOptions options) => Path.Combine(DataDirectory(options), "drivers");

    /// <summary>The skill loop's queue, staged drafts, usage and eval results.</summary>
    public static string SkillsDatabase(CorvidOptions options) => Path.Combine(DataDirectory(options), "skills.db");

    /// <summary>
    /// The directory a stdio MCP server (C-018) may write, one per server: <c>$XDG_STATE_HOME/corvid-mcp/&lt;name&gt;</c>.
    /// Outside the data directory, which the sandbox hides (the launcher refuses a workspace inside a denied path).
    /// </summary>
    public static string McpWorkspace(string server) => Path.Combine(Xdg("XDG_STATE_HOME", Path.Combine(".local", "state")), "corvid-mcp", server);

    /// <summary>A leading <c>~/</c> means the home directory; anything else is returned as given.</summary>
    public static string ExpandHome(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path == "~" ? Home : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Home, path[2..]) : path;
    }

    private static string Xdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && Path.IsPathFullyQualified(value)
            ? value
            : Path.Combine(Home, fallback);
}
