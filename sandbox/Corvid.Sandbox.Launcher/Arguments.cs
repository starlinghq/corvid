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

    public required IReadOnlyList<string> Command { get; init; }
}

internal static class Arguments
{
    public const string Stage2Flag = "--stage2";

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
          --best-effort-layers    Run without Landlock or seccomp when the kernel lacks them (never without bubblewrap).
          --bwrap PATH            bubblewrap to use (default: the first bwrap on PATH, then /usr/bin/bwrap).
          --status-file PATH      Write a JSON status: whether the command started, its exit code, the layers applied.

        Exit status: the command's, or 125 when the sandbox could not be set up (nothing ran).
        """;

    public static LaunchRequest ParseLaunch(IReadOnlyList<string> args)
    {
        string? workspace = null, chdir = null, bwrap = null, status = null;
        bool network = false, bestEffort = false;
        var ro = new List<string>();
        var deny = new List<string>();
        var env = new List<string>();
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
                case "--best-effort-layers": bestEffort = true; break;
                case "--bwrap": bwrap = Value(args, ref i); break;
                case "--status-file": status = Value(args, ref i); break;
                default: throw new RefusedException($"Unknown option {args[i]}. Run corvid-sandbox --help.");
            }
        }

        return new LaunchRequest
        {
            Workspace = workspace ?? throw new RefusedException("--workspace is required."),
            WorkingDirectory = chdir,
            ReadOnlyPaths = ro,
            DeniedPaths = deny,
            Environment = env,
            Network = network,
            BestEffortLayers = bestEffort,
            BubblewrapPath = bwrap,
            StatusFile = status,
            Command = Command(args, i),
        };
    }

    public static Stage2Request ParseStage2(IReadOnlyList<string> args)
    {
        int? ready = null;
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
                default: throw new RefusedException($"Unknown stage-2 option {args[i]}.");
            }
        }

        return new Stage2Request { ReadyFd = ready, WritablePaths = rw, Environment = env, OutsideNamespaces = outside, BestEffortLayers = bestEffort, Command = Command(args, i) };
    }

    private static string Value(IReadOnlyList<string> args, ref int i) =>
        ++i < args.Count && args[i] != "--" ? args[i] : throw new RefusedException($"{args[i - 1]} needs a value.");

    private static string[] Command(IReadOnlyList<string> args, int separator) =>
        separator < args.Count - 1 && args[separator + 1].Length > 0
            ? args.Skip(separator + 1).ToArray()
            : throw new RefusedException("No command: put it after --.");
}
