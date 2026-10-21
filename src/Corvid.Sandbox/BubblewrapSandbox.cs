using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Corvid.Sandbox;

/// <summary>
/// Runs commands through the corvid-sandbox launcher (bubblewrap, Landlock, seccomp). The launcher is a separate
/// executable found by path, shared with Corvid Vault, so the confinement policy lives in one place (plan §3.4).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class BubblewrapSandbox(SandboxOptions? options = null) : ISandbox
{
    /// <summary>The launcher's exit code when it refused; its status file says why.</summary>
    internal const int NotStarted = 125;

    // The launcher needs these to find bubblewrap, compute Vault's directories, and start (a framework-dependent build).
    // They go to the launcher only; the command gets just the policy's names.
    private static readonly string[] LauncherEnvironment =
        ["PATH", "HOME", "XDG_DATA_HOME", "XDG_CONFIG_HOME", "XDG_RUNTIME_DIR", "DOTNET_ROOT"];

    private readonly SandboxOptions _options = options ?? new SandboxOptions();

    public async Task<SandboxResult> RunAsync(SandboxPolicy policy, SandboxCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(command);
        policy.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        string launcher = LocateLauncher();
        DirectoryInfo statusDirectory = Directory.CreateTempSubdirectory("corvid-sandbox-"); // mode 0700
        try
        {
            string statusFile = Path.Combine(statusDirectory.FullName, "status.json");
            var start = new ProcessStartInfo(launcher)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in Arguments(policy, command, statusFile, _options))
            {
                start.ArgumentList.Add(argument);
            }

            start.Environment.Clear();
            IReadOnlyDictionary<string, string> source = _options.Environment ?? CurrentEnvironment();
            foreach (string name in policy.Environment.Concat(LauncherEnvironment).Distinct(StringComparer.Ordinal))
            {
                if (source.TryGetValue(name, out string? value))
                {
                    start.Environment[name] = value;
                }
            }

            return await RunAsync(start, command, statusFile, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            statusDirectory.Delete(recursive: true);
        }
    }

    private async Task<SandboxResult> RunAsync(ProcessStartInfo start, SandboxCommand command, string statusFile, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new SandboxRefusedException($"Could not start {start.FileName}.");
        }
        catch (Win32Exception e)
        {
            throw new SandboxRefusedException($"Could not start {start.FileName}: {e.Message}", e);
        }

        using (process)
        {
            var stdout = new BoundedCapture(_options.MaxOutputBytes);
            var stderr = new BoundedCapture(_options.MaxOutputBytes);
            Task pumps = Task.WhenAll(
                stdout.PumpAsync(process.StandardOutput.BaseStream),
                stderr.PumpAsync(process.StandardError.BaseStream));

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.Timeout);
            bool timedOut = false;
            try
            {
                await WriteInputAsync(process, command.StandardInput, timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The launcher, bubblewrap and the sandbox's PID namespace all go; --die-with-parent covers anything
                // the host-side tree walk misses.
                KillTree(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                timedOut = true;
            }

            // Pipes close when the sandbox's last process exits; bound the wait in case something unexpected holds them.
            try
            {
                await pumps.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Report what was read so far.
            }
            clock.Stop();

            Status? status = Status.Read(statusFile);
            if (status is { Started: false } || (status is null && !timedOut))
            {
                string reason = status?.Error ?? $"corvid-sandbox exited with {process.ExitCode} without reporting a status.";
                string detail = stderr.Text.Trim();
                throw new SandboxRefusedException(detail.Length > 0 && !detail.Contains(reason, StringComparison.Ordinal)
                    ? $"{reason} {detail}"
                    : reason);
            }

            return new SandboxResult
            {
                ExitCode = status?.ExitCode ?? process.ExitCode,
                StandardOutput = stdout.Text,
                StandardError = stderr.Text,
                StandardOutputTruncated = stdout.Truncated,
                StandardErrorTruncated = stderr.Truncated,
                TimedOut = timedOut,
                Duration = clock.Elapsed,
                Layers = status?.Layers ?? [],
            };
        }
    }

    /// <summary>
    /// How to start <paramref name="command"/> under the launcher as a long-lived process whose standard streams the caller
    /// owns (an MCP server over stdio, C-018), instead of running it to completion. The same policy checks, arguments and
    /// cleared environment as <see cref="RunAsync"/>; the caller starts the launcher, kills the tree when done, and may read
    /// <paramref name="statusFile"/> (in a directory outside the workspace) to tell a refusal from a command that exited.
    /// </summary>
    /// <exception cref="SandboxRefusedException">The launcher is missing; nothing may run unconfined.</exception>
    public SandboxLaunch Prepare(SandboxPolicy policy, SandboxCommand command, string? statusFile = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(command);
        policy.Validate();
        if (command.StandardInput is not null)
            throw new ArgumentException("A prepared launch owns its standard input; StandardInput must be null.", nameof(command));
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, string> source = _options.Environment ?? CurrentEnvironment();
        foreach (string name in policy.Environment.Concat(LauncherEnvironment).Distinct(StringComparer.Ordinal))
        {
            if (source.TryGetValue(name, out string? value))
                environment[name] = value;
        }

        return new SandboxLaunch(LocateLauncher(), [.. Arguments(policy, command, statusFile, _options)], environment);
    }

    /// <summary>The launcher's exit code when it refused and nothing ran; its status file says why.</summary>
    public const int RefusedExitCode = NotStarted;

    /// <summary>The launcher's command line for a policy; the launcher's usage text is the contract.</summary>
    internal static List<string> Arguments(SandboxPolicy policy, SandboxCommand command, string? statusFile, SandboxOptions options)
    {
        var a = new List<string> { "--workspace", policy.Workspace };
        if (command.WorkingDirectory is not null)
        {
            a.AddRange(["--chdir", command.WorkingDirectory]);
        }

        foreach (string path in policy.ReadOnlyPaths)
        {
            a.AddRange(["--ro", path]);
        }

        foreach (string path in policy.DeniedPaths)
        {
            a.AddRange(["--deny", path]);
        }

        foreach (string name in policy.Environment)
        {
            a.AddRange(["--env", name]);
        }

        if (policy.Network)
        {
            a.Add("--network");
        }

        if (!options.RequireKernelLayers)
        {
            a.Add("--best-effort-layers");
        }

        if (options.BubblewrapPath is not null)
        {
            a.AddRange(["--bwrap", options.BubblewrapPath]);
        }

        if (statusFile is not null)
        {
            a.AddRange(["--status-file", statusFile]);
        }

        a.Add("--");
        a.Add(command.FileName);
        a.AddRange(command.Arguments);
        return a;
    }

    private string LocateLauncher()
    {
        string path = _options.LauncherPath
            ?? (System.Environment.GetEnvironmentVariable("CORVID_SANDBOX") is { Length: > 0 } fromEnvironment ? fromEnvironment : null)
            ?? Path.Combine(AppContext.BaseDirectory, "corvid-sandbox");
        return File.Exists(path)
            ? path
            : throw new SandboxRefusedException($"corvid-sandbox not found at {path}; refusing to run unsandboxed. Set SandboxOptions.LauncherPath or CORVID_SANDBOX.");
    }

    private static async Task WriteInputAsync(Process process, string? input, CancellationToken cancellationToken)
    {
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The command exited without reading its input.
        }
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // Already exited.
        }
    }

    private static Dictionary<string, string> CurrentEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                environment[key] = value;
            }
        }

        return environment;
    }

    /// <summary>The launcher's status file: {"started":…,"exitCode":…,"layers":[…],"error":…}.</summary>
    private sealed record Status(bool Started, int? ExitCode, ImmutableArray<string> Layers, string? Error)
    {
        /// <summary>Null when the launcher wrote nothing: it opens the file at start and writes it only on the way out.</summary>
        public static Status? Read(string path)
        {
            byte[] bytes = File.Exists(path) ? File.ReadAllBytes(path) : [];
            if (bytes.Length == 0)
            {
                return null;
            }

            try
            {
                using var json = JsonDocument.Parse(bytes);
                JsonElement root = json.RootElement;
                return new Status(
                    root.GetProperty("started").GetBoolean(),
                    root.TryGetProperty("exitCode", out JsonElement code) ? code.GetInt32() : null,
                    root.TryGetProperty("layers", out JsonElement layers) ? [.. layers.EnumerateArray().Select(l => l.GetString() ?? "")] : [],
                    root.TryGetProperty("error", out JsonElement error) ? error.GetString() : null);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                throw new SandboxRefusedException($"corvid-sandbox wrote an unreadable status: {e.Message}", e);
            }
        }
    }

    /// <summary>Keeps the first <c>limit</c> bytes of a stream and drains the rest, so a chatty command cannot block on a full pipe.</summary>
    private sealed class BoundedCapture(int limit)
    {
        private readonly MemoryStream _kept = new();
        private long _total;

        public bool Truncated => _total > limit;

        public string Text => Encoding.UTF8.GetString(_kept.GetBuffer(), 0, (int)_kept.Length);

        public async Task PumpAsync(Stream stream)
        {
            byte[] buffer = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                int keep = (int)Math.Clamp(limit - _total, 0, read);
                _kept.Write(buffer, 0, keep);
                _total += read;
            }
        }
    }
}
