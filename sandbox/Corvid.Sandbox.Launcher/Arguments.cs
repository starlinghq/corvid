namespace Corvid.Sandbox.Launcher;

/// <summary>A refusal: the command did not run. Reported on stderr and in the status file, exit code 125.</summary>
internal sealed class RefusedException(string message) : Exception(message);

/// <summary>What the caller asked for. Paths are as given; <see cref="SandboxPlan"/> resolves and checks them.</summary>
internal sealed record LaunchRequest
{
    public required string Workspace { get; init; }

    public string? WorkingDirectory { get; init; }

    public IReadOnlyList<string> ReadOnlyPaths { get; init; } = [];

    public IReadOnlyList<string> DeniedPaths { get; init; } = [];

    /// <summary>Names passed through from the launcher's own environment. Everything else is cleared.</summary>
    public IReadOnlyList<string> Environment { get; init; } = [];

    public bool Network { get; init; }

    /// <summary>Corvid Vault's per-sandbox proxy socket (V-003b), bridged to 127.0.0.1:3128 inside. Given with <see cref="ProxyCa"/>.</summary>
    public string? ProxySocket { get; init; }

    /// <summary>The sandbox CA's PEM, the only certificate the command's trust variables name.</summary>
    public string? ProxyCa { get; init; }

    /// <summary>
    /// A7: code mode's tools socket on the host (a Unix socket Corvid listens on for one run), mounted read-only at
    /// <see cref="SandboxPlan.ToolsSocketInside"/>. Given with <see cref="ToolsToken"/>.
    /// </summary>
    public string? ToolsSocket { get; init; }

    /// <summary>A7: the file holding the run's tools token, mounted read-only at <see cref="SandboxPlan.ToolsTokenInside"/>.</summary>
    public string? ToolsToken { get; init; }

    /// <summary>A7: NAME=VALUE pairs set for the command as given (not read from any environment). Never secrets.</summary>
    public IReadOnlyList<string> SetEnvironment { get; init; } = [];

    /// <summary>Run even when Landlock or seccomp is unavailable. bubblewrap is never optional.</summary>
    public bool BestEffortLayers { get; init; }

    public string? BubblewrapPath { get; init; }

    public string? StatusFile { get; init; }

    public required IReadOnlyList<string> Command { get; init; }
}

/// <summary>The second stage, run by bubblewrap inside the new namespaces: kernel layers, then exec.</summary>
internal sealed record Stage2Request
{
    public int? ReadyFd { get; init; }

    public IReadOnlyList<string> WritablePaths { get; init; } = [];

    /// <summary>The command's whole environment, as NAME=VALUE. Stage 2's own environment is never passed on.</summary>
    public IReadOnlyList<string> Environment { get; init; } = [];

    public bool BestEffortLayers { get; init; }

    /// <summary>The supervisor's namespaces, as /proc/self/ns links (NAME=pid:[…]); stage 2 must be in none of them.</summary>
    public IReadOnlyList<string> OutsideNamespaces { get; init; } = [];

    /// <summary>The proxy socket's path inside the sandbox: stage 2 listens on 127.0.0.1:3128 and runs the command under the relay.</summary>
    public string? Relay { get; init; }

    public required IReadOnlyList<string> Command { get; init; }
}

/// <summary>Relay mode (V-003b): stage 2 re-executed, confined, as the command's parent and its loopback-to-Vault bridge.</summary>
internal sealed record RelayRequest
{
    /// <summary>The listening TCP socket stage 2 bound on 127.0.0.1:3128, inherited across execve.</summary>
    public required int ListenFd { get; init; }

    /// <summary>Vault's proxy socket, as mounted inside the sandbox.</summary>
    public required string Socket { get; init; }

    /// <summary>The command's whole environment, as NAME=VALUE.</summary>
    public IReadOnlyList<string> Environment { get; init; } = [];

    public required IReadOnlyList<string> Command { get; init; }
}

internal static class Arguments
{
    public const string Stage2Flag = "--stage2";

    public const string RelayFlag = "--relay-run";

    public const string Usage = """
        corvid-sandbox: runs one command under bubblewrap, Landlock and seccomp.

        Usage:
          corvid-sandbox --workspace DIR [options] -- COMMAND [ARG]...

        The workspace is read-write; the rest of the file system is read-only. ~/.ssh, ~/.gnupg, ~/.aws,
        ~/.config/gh, Corvid Vault's data and config directories, and .env files in the workspace and at the top of
        home are hidden. /tmp and /run are empty. The network, PID, IPC and UTS namespaces are new, the environment is
        cleared, and the command dies with the launcher.

        Options:
          --workspace DIR         Read-write directory; the default working directory.
          --chdir DIR             Working directory inside the workspace.
          --ro PATH               Bind PATH read-only (for example, part of the workspace). Repeatable.
          --deny PATH             Also hide PATH. Repeatable. The built-in denials cannot be removed.
          --env NAME              Pass NAME from this process's environment. Repeatable. Secret-like names are refused.
          --network               Share the host network. Off by default.
          --proxy-socket PATH     Corvid Vault's per-sandbox proxy socket. The network stays private (loopback only); inside,
                                  127.0.0.1:3128 relays to this socket, HTTPS_PROXY and HTTP_PROXY point there, and the trust
                                  variables (SSL_CERT_FILE and others) name the CA alone. Needs --proxy-ca; not with --network.
          --proxy-ca PATH         The sandbox CA certificate (PEM, certificates only, at most 64 KiB, not a symlink, outside
                                  the workspace). Needs --proxy-socket. An --env name the proxy sets is refused.
          --setenv NAME=VALUE     Set NAME to VALUE for the command. Repeatable. The --env name rules apply (secret-like
                                  names are refused); the value is on this command line, so it must never be a secret.
          --tools-socket PATH     Code mode's tools socket (a Unix socket on the host), mounted read-only at
                                  /run/corvid-sandbox/tools.sock, which CORVID_TOOLS_SOCKET names. Needs --tools-token.
          --tools-token PATH      The run's token file (a regular file, not a symlink, at most 1 KiB, outside the
                                  workspace), mounted read-only at /run/corvid-sandbox/tools.token, which
                                  CORVID_TOOLS_TOKEN_FILE names. Needs --tools-socket.
          --best-effort-layers    Run without Landlock or seccomp when the kernel lacks them (never without bubblewrap).
          --bwrap PATH            bubblewrap to use (default: the first bwrap on PATH, then /usr/bin/bwrap).
          --status-file PATH      Write a JSON status: whether the command started, its exit code, the layers applied.
                                  Must be outside the workspace and not a symlink.

        Exit status: the command's, or 125 when the sandbox could not be set up (nothing ran).
        """;

    public static LaunchRequest ParseLaunch(IReadOnlyList<string> args)
    {
        string? workspace = null, chdir = null, bwrap = null, status = null, proxySocket = null, proxyCa = null, toolsSocket = null, toolsToken = null;
        bool network = false, bestEffort = false;
        var ro = new List<string>();
        var deny = new List<string>();
        var env = new List<string>();
        var setEnv = new List<string>();
        int i = 0;
        for (; i < args.Count && args[i] != "--"; i++)
        {
            switch (args[i])
            {
                case "--workspace": workspace = Value(args, ref i); break;
                case "--chdir": chdir = Value(args, ref i); break;
                case "--ro": ro.Add(Value(args, ref i)); break;
                case "--deny": deny.Add(Value(args, ref i)); break;
                case "--env": env.Add(Value(args, ref i)); break;
                case "--network": network = true; break;
                case "--proxy-socket": proxySocket = Value(args, ref i); break;
                case "--proxy-ca": proxyCa = Value(args, ref i); break;
                case "--setenv": setEnv.Add(Value(args, ref i)); break;
                case "--tools-socket": toolsSocket = Value(args, ref i); break;
                case "--tools-token": toolsToken = Value(args, ref i); break;
                case "--best-effort-layers": bestEffort = true; break;
                case "--bwrap": bwrap = Value(args, ref i); break;
                case "--status-file": status = Value(args, ref i); break;
                default: throw new RefusedException($"Unknown option {args[i]}. Run corvid-sandbox --help.");
            }
        }

        if ((proxySocket is null) != (proxyCa is null))
        {
            throw new RefusedException("--proxy-socket and --proxy-ca go together: give both or neither.");
        }

        if (proxySocket is not null && network)
        {
            throw new RefusedException("--proxy-socket cannot be combined with --network: the proxy is the only route out.");
        }

        if ((toolsSocket is null) != (toolsToken is null))
        {
            throw new RefusedException("--tools-socket and --tools-token go together: give both or neither.");
        }

        return new LaunchRequest
        {
            Workspace = workspace ?? throw new RefusedException("--workspace is required."),
            WorkingDirectory = chdir,
            ReadOnlyPaths = ro,
            DeniedPaths = deny,
            Environment = env,
            Network = network,
            ProxySocket = proxySocket,
            ProxyCa = proxyCa,
            ToolsSocket = toolsSocket,
            ToolsToken = toolsToken,
            SetEnvironment = setEnv,
            BestEffortLayers = bestEffort,
            BubblewrapPath = bwrap,
            StatusFile = status,
            Command = Command(args, i),
        };
    }

    public static Stage2Request ParseStage2(IReadOnlyList<string> args)
    {
        int? ready = null;
        string? relay = null;
        bool bestEffort = false;
        var rw = new List<string>();
        var env = new List<string>();
        var outside = new List<string>();
        int i = 0;
        if (i < args.Count && args[i] == Stage2Flag)
        {
            i++;
        }

        for (; i < args.Count && args[i] != "--"; i++)
        {
            switch (args[i])
            {
                case "--ready-fd":
                    ready = int.TryParse(Value(args, ref i), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int fd) && fd > 2
                        ? fd
                        : throw new RefusedException("--ready-fd needs a descriptor number above 2.");
                    break;
                case "--rw": rw.Add(Value(args, ref i)); break;
                case "--env": env.Add(Value(args, ref i)); break;
                case "--not-in": outside.Add(Value(args, ref i)); break;
                case "--best-effort-layers": bestEffort = true; break;
                case "--relay": relay = Value(args, ref i); break;
                default: throw new RefusedException($"Unknown stage-2 option {args[i]}.");
            }
        }

        return new Stage2Request
        {
            ReadyFd = ready, WritablePaths = rw, Environment = env, OutsideNamespaces = outside, BestEffortLayers = bestEffort, Relay = relay,
            Command = Command(args, i),
        };
    }

    /// <summary><c>--relay-run --listen-fd N --socket PATH [--env NAME=VALUE]... -- COMMAND [ARG]...</c>, from stage 2 only.</summary>
    public static RelayRequest ParseRelay(IReadOnlyList<string> args)
    {
        int? listen = null;
        string? socket = null;
        var env = new List<string>();
        int i = 0;
        if (i < args.Count && args[i] == RelayFlag)
        {
            i++;
        }

        for (; i < args.Count && args[i] != "--"; i++)
        {
            switch (args[i])
            {
                case "--listen-fd":
                    listen = int.TryParse(Value(args, ref i), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int fd) && fd > 2
                        ? fd
                        : throw new RefusedException("--listen-fd needs a descriptor number above 2.");
                    break;
                case "--socket": socket = Value(args, ref i); break;
                case "--env": env.Add(Value(args, ref i)); break;
                default: throw new RefusedException($"Unknown relay option {args[i]}.");
            }
        }

        return new RelayRequest
        {
            ListenFd = listen ?? throw new RefusedException("--listen-fd is required."),
            Socket = socket ?? throw new RefusedException("--socket is required."),
            Environment = env,
            Command = Command(args, i),
        };
    }

    private static string Value(IReadOnlyList<string> args, ref int i) =>
        ++i < args.Count && args[i] != "--" ? args[i] : throw new RefusedException($"{args[i - 1]} needs a value.");

    private static string[] Command(IReadOnlyList<string> args, int separator) =>
        separator < args.Count - 1 && args[separator + 1].Length > 0
            ? args.Skip(separator + 1).ToArray()
            : throw new RefusedException("No command: put it after --.");
}
