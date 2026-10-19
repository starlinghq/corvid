using System.Text.RegularExpressions;

namespace Corvid.Sandbox.Launcher;

/// <summary>
/// The resolved policy and the bubblewrap command line that applies it. Every path is canonical (symlinks
/// resolved), because bubblewrap mounts on the resolved target and the containment checks must compare like with like.
/// </summary>
internal sealed partial record SandboxPlan
{
    /// <summary>Entries scanned for .env files before a workspace is refused as too large to check.</summary>
    public const int MaxScannedEntries = 100_000;

    private const int MaxScanDepth = 12;

    public required string Bubblewrap { get; init; }

    public required string Workspace { get; init; }

    public required string WorkingDirectory { get; init; }

    public required IReadOnlyList<string> ReadOnlyPaths { get; init; }

    /// <summary>Directories hidden behind an empty, mode-0000 tmpfs.</summary>
    public required IReadOnlyList<string> MaskedDirectories { get; init; }

    /// <summary>Files hidden behind <c>/dev/null</c>.</summary>
    public required IReadOnlyList<string> MaskedFiles { get; init; }

    /// <summary>NAME=VALUE pairs the command gets; nothing else from any environment reaches it.</summary>
    public required IReadOnlyList<string> Environment { get; init; }

    public required bool Network { get; init; }

    public required bool BestEffortLayers { get; init; }

    public string? ResolvConf { get; init; }

    /// <summary>How bubblewrap starts stage 2: the launcher itself, or <c>dotnet corvid-sandbox.dll</c> when run that way.</summary>
    public required IReadOnlyList<string> Stage2 { get; init; }

    /// <summary>Environment stage 2 itself needs to start (DOTNET_ROOT for a framework-dependent build). Never passed on.</summary>
    public required IReadOnlyList<KeyValuePair<string, string>> Stage2Environment { get; init; }

    public required IReadOnlyList<string> Command { get; init; }

    /// <summary>
    /// The launcher's own namespaces (NAME=pid:[…]). Stage 2 checks that it is in none of them, so a bubblewrap that
    /// silently skipped one (or a stand-in that is not bubblewrap) cannot run the command unconfined.
    /// </summary>
    public required IReadOnlyList<string> OutsideNamespaces { get; init; }

    /// <summary>Paths a command may write: the workspace and the sandbox's own empty /tmp and /dev/shm.</summary>
    public IReadOnlyList<string> WritablePaths => [Workspace, "/tmp", "/dev/shm"];

    /// <summary>Resolves and checks a request. Throws <see cref="RefusedException"/> when the policy cannot be applied safely.</summary>
    public static SandboxPlan Create(LaunchRequest request, Func<string, string?> environment, LauncherLocation launcher)
    {
        string workspace = ExistingDirectory(request.Workspace, "--workspace");
        if (workspace == "/" || Under(workspace, "/proc") || Under(workspace, "/sys") || Under(workspace, "/dev"))
        {
            throw new RefusedException($"The workspace cannot be {workspace}.");
        }

        string cwd = request.WorkingDirectory is null ? workspace : ExistingDirectory(request.WorkingDirectory, "--chdir");
        if (!Under(cwd, workspace))
        {
            throw new RefusedException($"The working directory {cwd} is outside the workspace {workspace}.");
        }

        // A launcher or bubblewrap the command can overwrite would run unconfined next time.
        string bwrap = FindBubblewrap(request.BubblewrapPath, environment);
        foreach (string executable in (string[])[launcher.Directory, launcher.Executable, bwrap])
        {
            if (Native.RealPath(executable) is { } real && Under(real, workspace))
            {
                throw new RefusedException($"{real} is inside the workspace, where the command could replace it.");
            }
        }

        var readOnly = new List<string>();
        foreach (string path in request.ReadOnlyPaths)
        {
            readOnly.Add(Absolute(path, "--ro") is var p && Native.RealPath(p) is { } real ? real : throw new RefusedException($"--ro {path} does not exist."));
        }

        var homes = Homes(environment);
        var denied = new List<string>();
        // Vault finds its directories from HOME and the XDG variables, as computed here. They are created (0700, as
        // Vault does) when missing, so a store created while a command runs is still hidden. Under the account's other
        // home, if HOME points elsewhere, existing ones are hidden but nothing is created.
        denied.AddRange(homes.Skip(1).SelectMany(home => VaultDirectories(_ => null, home)));
        foreach (string vault in VaultDirectories(environment, homes[0]))
        {
            if (!Directory.Exists(vault) && !File.Exists(vault))
            {
                Directory.CreateDirectory(vault, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            denied.Add(vault);
        }

        foreach (string home in homes)
        {
            denied.AddRange(SecretDirectories.Select(relative => Path.Combine(home, relative)));
            denied.AddRange(EnvFiles(home, recurse: false));
        }

        if (environment("XDG_CONFIG_HOME") is { } configHome && Path.IsPathFullyQualified(configHome))
        {
            denied.Add(Path.Combine(configHome, "gh"));
        }

        if (environment("XDG_RUNTIME_DIR") is { } runtime && Path.IsPathFullyQualified(runtime))
        {
            // Sockets live here: Vault's, ssh-agent's, gpg-agent's, the session bus. /run is replaced anyway; this
            // covers a runtime directory elsewhere.
            denied.Add(runtime);
        }

        denied.AddRange(request.DeniedPaths.Select(path => Absolute(path, "--deny")));
        denied.AddRange(EnvFiles(workspace, recurse: true));

        var maskedDirectories = new SortedSet<string>(StringComparer.Ordinal);
        var maskedFiles = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string path in denied)
        {
            if (Native.RealPath(path) is not { } real)
            {
                continue; // Nothing there to hide.
            }

            if (Under(workspace, real))
            {
                throw new RefusedException($"The workspace {workspace} is inside the denied path {real}.");
            }

            if (Under(cwd, real))
            {
                throw new RefusedException($"The working directory {cwd} is inside the denied path {real}.");
            }

            (Directory.Exists(real) ? maskedDirectories : maskedFiles).Add(real);
        }

        var env = new List<string>();
        foreach (string name in request.Environment.Distinct(StringComparer.Ordinal))
        {
            if (!EnvironmentName().IsMatch(name))
            {
                throw new RefusedException($"--env {name} is not a valid variable name.");
            }

            if (IsSecretLike(name))
            {
                throw new RefusedException($"--env {name} looks like a secret. Secrets reach commands only through Corvid Vault.");
            }

            if (environment(name) is { } value)
            {
                env.Add(name + "=" + value);
            }
        }

        string? resolv = null;
        if (request.Network && Native.RealPath("/etc/resolv.conf") is { } resolvTarget && (Under(resolvTarget, "/run") || Under(resolvTarget, "/tmp")))
        {
            resolv = resolvTarget; // systemd-resolved's stub lives under /run, which the sandbox replaces.
        }

        var stage2Env = new List<KeyValuePair<string, string>>();
        if (environment("DOTNET_ROOT") is { Length: > 0 } dotnetRoot)
        {
            stage2Env.Add(new("DOTNET_ROOT", dotnetRoot));
        }

        var outside = new List<string>();
        foreach (string ns in (string[])["user", "mnt", "pid", "ipc", "uts", .. request.Network ? (string[])[] : ["net"]])
        {
            outside.Add(ns + "=" + (new FileInfo("/proc/self/ns/" + ns).LinkTarget
                ?? throw new RefusedException($"Cannot read /proc/self/ns/{ns}.")));
        }

        return new SandboxPlan
        {
            Bubblewrap = bwrap,
            Workspace = workspace,
            WorkingDirectory = cwd,
            ReadOnlyPaths = readOnly,
            MaskedDirectories = [.. maskedDirectories],
            MaskedFiles = [.. maskedFiles],
            Environment = env,
            Network = request.Network,
            BestEffortLayers = request.BestEffortLayers,
            ResolvConf = resolv,
            Stage2 = launcher.Stage2Command,
            Stage2Environment = stage2Env,
            OutsideNamespaces = outside,
            Command = request.Command,
        };
    }

    /// <summary>bubblewrap's arguments. Order matters: later mounts shadow earlier ones, so the masks come last.</summary>
    public IReadOnlyList<string> BubblewrapArguments(int readyFd)
    {
        var a = new List<string>
        {
            // New user, mount, PID, network, IPC, UTS and cgroup namespaces; bubblewrap fails rather than run without
            // one it was asked for. --disable-userns stops the command from making a nested user namespace, where it
            // would be root and could rearrange its mounts.
            "--unshare-all", "--unshare-user", "--disable-userns",
        };
        if (Network)
        {
            a.Add("--share-net");
        }

        a.AddRange(["--die-with-parent", "--new-session"]);
        a.AddRange(["--ro-bind", "/", "/", "--dev", "/dev", "--proc", "/proc", "--tmpfs", "/tmp", "--tmpfs", "/run"]);
        if (ResolvConf is not null)
        {
            a.AddRange(["--ro-bind", ResolvConf, ResolvConf]);
        }

        // Parents before children, so a read-only path inside the workspace stays read-only and a read-only parent
        // of the workspace does not cover it.
        var binds = ReadOnlyPaths.Select(p => (Path: p, Option: "--ro-bind")).Append((Path: Workspace, Option: "--bind"))
            .OrderBy(b => b.Path.Count(c => c == '/')).ThenBy(b => b.Option == "--ro-bind" ? 1 : 0);
        foreach (var (path, option) in binds)
        {
            a.AddRange([option, path, path]);
        }

        foreach (string directory in MaskedDirectories)
        {
            a.AddRange(["--perms", "0000", "--tmpfs", directory]);
        }

        foreach (string file in MaskedFiles)
        {
            a.AddRange(["--ro-bind", "/dev/null", file]);
        }

        a.AddRange(["--chdir", WorkingDirectory, "--clearenv"]);
        foreach (var (name, value) in Stage2Environment)
        {
            a.AddRange(["--setenv", name, value]);
        }

        a.Add("--");
        a.AddRange(Stage2);
        a.AddRange([Arguments.Stage2Flag, "--ready-fd", readyFd.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        foreach (string path in WritablePaths)
        {
            a.AddRange(["--rw", path]);
        }

        foreach (string pair in Environment)
        {
            a.AddRange(["--env", pair]);
        }

        foreach (string ns in OutsideNamespaces)
        {
            a.AddRange(["--not-in", ns]);
        }

        if (BestEffortLayers)
        {
            a.Add("--best-effort-layers");
        }

        a.Add("--");
        a.AddRange(Command);
        return a;
    }

    /// <summary>Relative to each home: credentials other tools keep in plain files (vault.md §8).</summary>
    internal static readonly string[] SecretDirectories = [".ssh", ".gnupg", ".aws", ".config/gh"];

    /// <summary>
    /// Refused even when asked for: values a command would only want if it held a secret, or that open a path to an
    /// agent holding one (SSH_AUTH_SOCK, GPG_AGENT_INFO, the session bus).
    /// </summary>
    internal static bool IsSecretLike(string name)
    {
        string n = name.ToUpperInvariant();
        return n.EndsWith("KEY", StringComparison.Ordinal) || n.Contains("_KEY_", StringComparison.Ordinal)
            || SecretMarkers.Any(m => n.Contains(m, StringComparison.Ordinal))
            || SecretPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal));
    }

    private static readonly string[] SecretMarkers =
        ["TOKEN", "SECRET", "PASSWORD", "PASSWD", "PASSPHRASE", "CREDENTIAL", "PRIVATE", "AUTH", "COOKIE", "SESSION", "APIKEY"];

    private static readonly string[] SecretPrefixes =
        ["AWS_", "AZURE_", "GOOGLE_", "GCP_", "GH_", "GITHUB_", "GITLAB_", "OPENAI_", "ANTHROPIC_", "HF_", "SSH_", "GPG_", "GNUPG",
         "DBUS_", "KRB5", "VAULT_", "CORVID_VAULT", "DOCKER_", "KUBECONFIG", "NPM_CONFIG_", "PGPASS", "MYSQL_PWD"];

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvironmentName();

    internal static bool Under(string path, string root) =>
        path == root || root == "/" || path.StartsWith(root + "/", StringComparison.Ordinal);

    /// <summary>$HOME, and the account's home from the password database when it differs, so both are covered.</summary>
    private static List<string> Homes(Func<string, string?> environment)
    {
        var homes = new List<string>();
        if (environment("HOME") is { } home && Path.IsPathFullyQualified(home))
        {
            homes.Add(Path.TrimEndingDirectorySeparator(home));
        }

        if (Native.PasswordHome() is { } passwd && Path.IsPathFullyQualified(passwd) && !homes.Contains(passwd))
        {
            homes.Add(Path.TrimEndingDirectorySeparator(passwd));
        }

        return homes.Count > 0 ? homes : throw new RefusedException("Cannot find the home directory (HOME is unset and there is no password entry).");
    }

    /// <summary>Where Corvid Vault keeps its store and policy: the XDG locations and their defaults (vault/README.md).</summary>
    internal static IEnumerable<string> VaultDirectories(Func<string, string?> environment, string home)
    {
        var dirs = new List<string>
        {
            Path.Combine(home, ".local", "share", "corvid-vault"),
            Path.Combine(home, ".config", "corvid-vault"),
        };

        if (environment("XDG_DATA_HOME") is { } data && Path.IsPathFullyQualified(data))
        {
            dirs.Add(Path.Combine(data, "corvid-vault"));
        }

        if (environment("XDG_CONFIG_HOME") is { } config && Path.IsPathFullyQualified(config))
        {
            dirs.Add(Path.Combine(config, "corvid-vault"));
        }

        return dirs.Distinct(StringComparer.Ordinal);
    }

    /// <summary><c>.env</c> and <c>.env.*</c>, examples included: a copy of a real file is often named like one.</summary>
    internal static bool IsEnvFileName(string name) => name == ".env" || name.StartsWith(".env.", StringComparison.Ordinal);

    /// <summary>
    /// <c>.env</c> and <c>.env.*</c> files (not directories: <c>python -m venv .env</c> is common). The workspace is
    /// scanned to a bounded size and depth, skipping <c>.git</c> and <c>node_modules</c> and not following symlinked
    /// directories; a workspace too large to scan is refused rather than run with .env files possibly visible.
    /// </summary>
    internal static List<string> EnvFiles(string root, bool recurse)
    {
        var found = new List<string>();
        var options = new EnumerationOptions { IgnoreInaccessible = false, AttributesToSkip = 0, RecurseSubdirectories = false };
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        int entries = 0;
        while (pending.TryPop(out var current))
        {
            IEnumerable<FileSystemInfo> children;
            try
            {
                children = new DirectoryInfo(current.Path).EnumerateFileSystemInfos("*", options).ToList();
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException)
            {
                if (!recurse)
                {
                    continue; // An unreadable home is the command's problem too.
                }

                throw new RefusedException($"Cannot scan {current.Path} for .env files ({e.Message}).");
            }

            foreach (FileSystemInfo child in children)
            {
                if (++entries > MaxScannedEntries)
                {
                    throw new RefusedException(
                        $"The workspace has more than {MaxScannedEntries} entries to scan for .env files; use a smaller workspace.");
                }

                bool link = child.LinkTarget is not null;
                if (child is DirectoryInfo && !link)
                {
                    if (recurse && current.Depth < MaxScanDepth && child.Name is not (".git" or "node_modules"))
                    {
                        pending.Push((child.FullName, current.Depth + 1));
                    }
                }
                else if (IsEnvFileName(child.Name) && File.Exists(child.FullName))
                {
                    found.Add(child.FullName);
                }
            }
        }

        return found;
    }

    private static string FindBubblewrap(string? requested, Func<string, string?> environment)
    {
        IEnumerable<string> candidates = requested is not null
            ? [Absolute(requested, "--bwrap")]
            : (environment("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Where(Path.IsPathFullyQualified)
                .Select(dir => Path.Combine(dir, "bwrap"))
                .Append("/usr/bin/bwrap").Append("/bin/bwrap");
        foreach (string candidate in candidates)
        {
            if (Native.RealPath(candidate) is { } real && File.Exists(real)
                && (File.GetUnixFileMode(real) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
            {
                return real;
            }
        }

        throw new RefusedException(requested is not null
            ? $"bubblewrap not found at {requested}."
            : "bubblewrap (bwrap) is not installed; refusing to run unsandboxed. Install it, for example: apt install bubblewrap.");
    }

    private static string Absolute(string path, string option) =>
        Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) is { Length: > 0 } p ? p : "/"
            : throw new RefusedException($"{option} {path} must be an absolute path.");

    private static string ExistingDirectory(string path, string option) =>
        Native.RealPath(Absolute(path, option)) is { } real && Directory.Exists(real)
            ? real
            : throw new RefusedException($"{option} {path} is not an existing directory.");
}

/// <summary>Where the running launcher lives, and how to start it again as stage 2 inside the sandbox.</summary>
internal sealed record LauncherLocation(string Executable, string Directory, IReadOnlyList<string> Stage2Command)
{
    public static LauncherLocation Current()
    {
        string executable = Environment.ProcessPath ?? throw new RefusedException("Cannot find the launcher's own path.");
        string directory = AppContext.BaseDirectory;
        // Run as `dotnet corvid-sandbox.dll` (framework-dependent, no apphost): start stage 2 the same way.
        IReadOnlyList<string> stage2 = Path.GetFileNameWithoutExtension(executable) == "dotnet"
            ? [executable, Path.Combine(directory, "corvid-sandbox.dll")]
            : [executable];
        return new LauncherLocation(executable, directory, stage2);
    }
}
