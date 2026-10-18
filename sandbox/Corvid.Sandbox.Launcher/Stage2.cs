using System.Runtime.InteropServices;
using System.Text;

namespace Corvid.Sandbox.Launcher;

/// <summary>
/// Runs inside bubblewrap's namespaces, as the sandbox's first process: applies Landlock and seccomp to this thread,
/// tells the supervisor which layers are in place, and execs the command with exactly the environment it was given.
/// Everything here runs on the main thread, because both layers bind the calling thread and survive only its execve.
/// </summary>
internal static class Stage2
{
    private const string DefaultPath = "/usr/local/bin:/usr/bin:/bin";

    public static int Run(Stage2Request request)
    {
        var layers = new List<string>();
        try
        {
            foreach (string pair in request.OutsideNamespaces)
            {
                string name = pair[..Math.Max(pair.IndexOf('=', StringComparison.Ordinal), 0)];
                string? current = new FileInfo("/proc/self/ns/" + name).LinkTarget;
                if (name.Length == 0 || current is null || current == pair[(name.Length + 1)..])
                {
                    throw new RefusedException($"Not in a new {name} namespace; refusing to run unconfined.");
                }
            }

            if (Native.prctl(Native.PrSetNoNewPrivs, 1, 0, 0, 0) != 0)
            {
                throw new RefusedException($"PR_SET_NO_NEW_PRIVS failed (errno {Native.Errno}).");
            }

            int abi = Landlock.Apply(request.WritablePaths);
            if (abi > 0)
            {
                layers.Add($"landlock-v{abi}");
            }
            else if (!request.BestEffortLayers)
            {
                throw new RefusedException("Landlock is not available in this kernel (needs Linux 5.13 or later with landlock in the lsm= list). Pass --best-effort-layers to run without it.");
            }

            if (Seccomp.ForCurrentArchitecture() is { } table)
            {
                Seccomp.Apply(Seccomp.Build(table));
                layers.Add("seccomp");
            }
            else if (!request.BestEffortLayers)
            {
                throw new RefusedException($"No seccomp filter for {RuntimeInformation.ProcessArchitecture}. Pass --best-effort-layers to run without it.");
            }
        }
        catch (RefusedException e)
        {
            if (request.ReadyFd is null)
            {
                Console.Error.WriteLine("corvid-sandbox: " + e.Message);
            }

            Report(request.ReadyFd, "error " + e.Message); // The supervisor prints it and writes the status.
            return 125;
        }

        Report(request.ReadyFd, "ok " + string.Join(' ', layers));
        string? path = Resolve(request.Command[0], request.Environment);
        if (path is null)
        {
            Console.Error.WriteLine($"corvid-sandbox: {request.Command[0]}: command not found");
            return 127;
        }

        int errno = Exec(path, request.Command, request.Environment);
        Console.Error.WriteLine($"corvid-sandbox: cannot execute {path}: errno {errno}");
        return errno == Native.ENOENT ? 127 : 126;
    }

    /// <summary>A command with a slash is used as given; otherwise the command's own PATH is searched, as a shell would.</summary>
    internal static string? Resolve(string command, IReadOnlyList<string> environment)
    {
        if (command.Contains('/', StringComparison.Ordinal))
        {
            return command;
        }

        string path = environment.FirstOrDefault(e => e.StartsWith("PATH=", StringComparison.Ordinal))?["PATH=".Length..] ?? DefaultPath;
        foreach (string dir in path.Split(':'))
        {
            string candidate = Path.Combine(dir.Length == 0 ? "." : dir, command);
            if (File.Exists(candidate)
                && (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>One line to the supervisor, then the descriptor is closed so the command never holds it.</summary>
    private static unsafe void Report(int? fd, string message)
    {
        if (fd is not { } ready)
        {
            return;
        }

        byte[] bytes = Encoding.UTF8.GetBytes(message.ReplaceLineEndings(" ") + "\n");
        fixed (byte* p = bytes)
        {
            nint written = 0;
            while (written < bytes.Length)
            {
                nint n = Native.write(ready, p + written, bytes.Length - written);
                if (n < 0 && Native.Errno == Native.EINTR)
                {
                    continue;
                }

                if (n <= 0)
                {
                    break; // The supervisor is gone; it will report the run as not started.
                }

                written += n;
            }
        }

        Native.close(ready);
    }

    /// <summary>Replaces this process. Returns errno only when execve fails.</summary>
    private static unsafe int Exec(string path, IReadOnlyList<string> argv, IReadOnlyList<string> environment)
    {
        // Not freed: the process image is replaced, or exits right after a failure.
        nint[] args = [.. argv.Select(Marshal.StringToCoTaskMemUTF8), 0];
        nint[] env = [.. environment.Select(Marshal.StringToCoTaskMemUTF8), 0];
        fixed (nint* a = args, e = env)
        {
            Native.execve(path, a, e);
        }

        return Native.Errno;
    }
}
