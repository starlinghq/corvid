using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Corvid.Sandbox.Launcher;

/// <summary>
/// Starts bubblewrap, learns from stage 2 whether the sandbox came up, and waits. The launcher stays the parent so
/// bubblewrap's --die-with-parent ties the sandbox to it, and it in turn exits (killing the sandbox) when its own parent
/// does, or on SIGINT, SIGTERM, SIGHUP or SIGQUIT.
/// </summary>
internal static class Supervisor
{
    public const int NotStarted = 125;

    public static int Run(LaunchRequest request)
    {
        SafeFileHandle? statusFile;
        try
        {
            statusFile = OpenStatus(request.StatusFile);
        }
        catch (RefusedException e)
        {
            Console.Error.WriteLine("corvid-sandbox: " + e.Message);
            return NotStarted;
        }

        using (statusFile)
        {
            SandboxPlan plan;
            try
            {
                plan = SandboxPlan.Create(request, Environment.GetEnvironmentVariable, LauncherLocation.Current());
                CheckStatusOutside(statusFile, plan.Workspace);
            }
            catch (Exception e) when (e is RefusedException or IOException or UnauthorizedAccessException)
            {
                return Refuse(statusFile, e.Message);
            }

            return Run(plan, statusFile);
        }
    }

    /// <summary>
    /// Opens the status file before anything runs and keeps the descriptor, so the final write lands in this file even
    /// if its path is changed meanwhile. The launcher runs unconfined: following a symlink there (planted by an earlier
    /// sandboxed command) would let a command make it overwrite any file the user owns.
    /// </summary>
    private static SafeFileHandle? OpenStatus(string? path)
    {
        if (path is null)
        {
            return null;
        }

        int fd = Native.open(path, Native.OWronly | Native.OCreat | Native.OTrunc | Native.ONofollow | Native.OCloexec, 0b110_000_000);
        return fd >= 0
            ? new SafeFileHandle(fd, ownsHandle: true)
            : throw new RefusedException($"Cannot open the status file {path} (errno {Native.Errno}; it must not be a symlink).");
    }

    /// <summary>A status file the command can write could be replaced or forged while it runs.</summary>
    private static void CheckStatusOutside(SafeFileHandle? statusFile, string workspace)
    {
        if (statusFile is null)
        {
            return;
        }

        string? real = new FileInfo("/proc/self/fd/" + statusFile.DangerousGetHandle().ToString(System.Globalization.CultureInfo.InvariantCulture)).LinkTarget;
        if (real is null || SandboxPlan.Under(real, workspace))
        {
            throw new RefusedException($"The status file {real ?? "(unknown)"} must be outside the workspace {workspace}.");
        }
    }

    private static unsafe int Run(SandboxPlan plan, SafeFileHandle? statusFile)
    {
        // Stage 2 reports on this pipe. Only the write end is inherited (through bubblewrap, which passes descriptors on).
        int* fds = stackalloc int[2];
        if (Native.pipe2(fds, Native.OCloexec) != 0 || Native.fcntl(fds[1], Native.FSetFd, 0) != 0)
        {
            return Refuse(statusFile, $"Cannot create the status pipe (errno {Native.Errno}).");
        }

        int readFd = fds[0], writeFd = fds[1];
        var start = new ProcessStartInfo(plan.Bubblewrap) { UseShellExecute = false };
        foreach (string argument in plan.BubblewrapArguments(writeFd))
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Clear();
        Process child;
        try
        {
            child = Process.Start(start) ?? throw new Win32Exception("Process.Start returned no process.");
        }
        catch (Win32Exception e)
        {
            Native.close(readFd);
            return Refuse(statusFile, $"Cannot start {plan.Bubblewrap}: {e.Message}");
        }
        finally
        {
            Native.close(writeFd);
        }

        using (child)
        {
            var registrations = new[] { PosixSignal.SIGINT, PosixSignal.SIGTERM, PosixSignal.SIGHUP, PosixSignal.SIGQUIT }
                .Select(signal => PosixSignalRegistration.Create(signal, context =>
                {
                    context.Cancel = true;
                    Kill(child);
                }))
                .ToList();
            WatchParent(child);

            string report = ReadLine(readFd);
            Native.close(readFd);
            child.WaitForExit();
            registrations.ForEach(r => r.Dispose());
            int exitCode = child.ExitCode;

            if (report.StartsWith("ok", StringComparison.Ordinal))
            {
                string[] layers = ["bubblewrap", .. report[2..].Split(' ', StringSplitOptions.RemoveEmptyEntries)];
                WriteStatus(statusFile, started: true, exitCode, layers, error: null);
                return exitCode;
            }

            string error = report.StartsWith("error ", StringComparison.Ordinal)
                ? report["error ".Length..]
                : $"bubblewrap could not set up the sandbox (exit {exitCode}); its message is on stderr. Check that unprivileged user namespaces are allowed.";
            return Refuse(statusFile, error);
        }
    }

    private static int Refuse(SafeFileHandle? statusFile, string error)
    {
        Console.Error.WriteLine("corvid-sandbox: " + error);
        try
        {
            WriteStatus(statusFile, started: false, exitCode: null, layers: [], error);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"corvid-sandbox: cannot write the status file: {e.Message}");
        }

        return NotStarted;
    }

    /// <summary>
    /// {"started":…,"exitCode":…,"layers":[…],"error":…}, read by Corvid.Sandbox. Written once, from the start of the
    /// file, replacing anything else that reached it.
    /// </summary>
    internal static void WriteStatus(SafeFileHandle? statusFile, bool started, int? exitCode, IReadOnlyList<string> layers, string? error)
    {
        if (statusFile is null)
        {
            return;
        }

        using var stream = new FileStream(statusFile, FileAccess.Write, bufferSize: 0);
        stream.SetLength(0);
        stream.Position = 0;
        using var json = new Utf8JsonWriter(stream);
        json.WriteStartObject();
        json.WriteBoolean("started", started);
        if (exitCode is { } code)
        {
            json.WriteNumber("exitCode", code);
        }

        json.WriteStartArray("layers");
        foreach (string layer in layers)
        {
            json.WriteStringValue(layer);
        }

        json.WriteEndArray();
        if (error is not null)
        {
            json.WriteString("error", error);
        }

        json.WriteEndObject();
    }

    /// <summary>One line from stage 2, or empty at end of file (every writer exited before the sandbox came up).</summary>
    private static unsafe string ReadLine(int fd)
    {
        var line = new List<byte>();
        byte* buffer = stackalloc byte[512];
        while (line.Count < 4096)
        {
            nint n = Native.read(fd, buffer, 512);
            if (n < 0 && Native.Errno == Native.EINTR)
            {
                continue;
            }

            if (n <= 0)
            {
                break;
            }

            var chunk = new ReadOnlySpan<byte>(buffer, (int)n);
            int newline = chunk.IndexOf((byte)'\n');
            line.AddRange((newline >= 0 ? chunk[..newline] : chunk).ToArray());
            if (newline >= 0)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString([.. line]);
    }

    /// <summary>
    /// Kills the sandbox when the launcher's parent exits. A pidfd becomes readable when the process ends; on kernels
    /// without pidfd_open (before 5.3) the parent pid is polled instead.
    /// </summary>
    private static unsafe void WatchParent(Process child)
    {
        int parent = Native.getppid();
        int pidfd = (int)Native.syscall(Native.SysPidfdOpen, parent, 0, 0, 0);
        if (Native.getppid() != parent)
        {
            Kill(child); // Reparented already: the parent died before the watch began.
            return;
        }

        var thread = new Thread(() =>
        {
            if (pidfd >= 0)
            {
                var poll = new Native.PollFd { Fd = pidfd, Events = Native.PollIn };
                while (Native.poll(&poll, 1, -1) < 0 && Native.Errno == Native.EINTR)
                {
                }
            }
            else
            {
                while (Native.getppid() == parent)
                {
                    Thread.Sleep(250);
                }
            }

            Kill(child);
        })
        { IsBackground = true, Name = "parent watch" };
        thread.Start();
    }

    private static void Kill(Process child)
    {
        try
        {
            child.Kill(); // SIGKILL to bubblewrap; --die-with-parent and the PID namespace take the command with it.
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Already exited.
        }
    }
}
