using System.Diagnostics;
using System.Text;

namespace Corvid.Kernel.Checkpoints;

/// <summary>A git command that exited with an error, or could not be started.</summary>
public sealed class CheckpointException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

internal sealed record GitResult(int ExitCode, byte[] Output, string Error)
{
    public string Text => Encoding.UTF8.GetString(Output);
}

/// <summary>
/// Runs the git CLI against the shadow store with a cleared environment and configuration of Corvid's choosing, so nothing
/// of the user's git setup runs or applies: no system or global configuration, no hooks, no fsmonitor command, no
/// credential helpers or pager, and the store's own directory as <c>GIT_DIR</c>, never the workspace's <c>.git</c>.
/// <see cref="ForDiscovery"/> is the one exception (C-025): it runs in the user's workspace to find its repository, read-only,
/// with the same environment and configuration but no <c>GIT_DIR</c>.
/// </summary>
internal sealed class GitRunner
{
    // Every invocation carries these, so even a config file someone planted in the store could not turn them back on.
    private static readonly string[] SafeConfig =
    [
        "core.hooksPath=/dev/null", "core.fsmonitor=false", "core.untrackedCache=false", "core.autocrlf=false",
        "core.symlinks=true", "core.filemode=true", "core.quotePath=false", "core.splitIndex=false",
        "core.excludesFile=/dev/null", "core.attributesFile=/dev/null", "core.pager=cat", "core.editor=false",
        "core.askPass=false", "credential.helper=", "commit.gpgSign=false", "gc.auto=0", "maintenance.auto=false",
        "protocol.allow=never", "submodule.recurse=false","advice.detachedHead=false", "init.defaultBranch=checkpoints",
    ];

    private readonly string _git;
    private readonly string _gitDirectory;
    private readonly string _workTree;
    private readonly string _home;
    private readonly TimeSpan _timeout;
    private readonly bool _discovery;
    private readonly string? _ceiling;

    /// <summary>The directory git runs in.</summary>
    public string WorkingDirectory => _workTree;

    public GitRunner(string git, string gitDirectory, string workTree, string home, TimeSpan timeout)
    {
        _git = git;
        _gitDirectory = gitDirectory;
        _workTree = workTree;
        _home = home;
        _timeout = timeout;
    }

    private GitRunner(string git, string workingDirectory, string? ceiling, TimeSpan timeout)
    {
        _git = git;
        _gitDirectory = "";
        _workTree = workingDirectory;
        // Nothing is read from it: global configuration is /dev/null and system configuration is off.
        _home = "/nonexistent";
        _timeout = timeout;
        _discovery = true;
        _ceiling = ceiling;
    }

    /// <summary>
    /// Discovery mode (C-025): git runs in <paramref name="workingDirectory"/> and finds its repository the usual way, with
    /// no <c>GIT_DIR</c> or <c>GIT_WORK_TREE</c>, so the caller can ask which repository a workspace belongs to. The cleared
    /// environment and the safe configuration still apply, so no hook, fsmonitor command, pager or credential helper runs,
    /// and with global configuration off a repository git considers of dubious ownership is refused. Discovery stops below
    /// <paramref name="ceiling"/> (<c>GIT_CEILING_DIRECTORIES</c>) when one is given.
    /// </summary>
    public static GitRunner ForDiscovery(string git, string workingDirectory, string? ceiling, TimeSpan timeout) =>
        new(git, workingDirectory, ceiling, timeout);

    /// <summary>
    /// The git executable: <paramref name="configured"/> when it is a path, else the first <c>git</c> on <c>PATH</c>.
    /// Null when there is none.
    /// </summary>
    public static string? Resolve(string configured, string? path)
    {
        if (configured.Contains('/'))
            return File.Exists(configured) && IsExecutable(configured) ? Path.GetFullPath(configured) : null;
        foreach (var directory in (path ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathFullyQualified(directory))
                continue;
            var candidate = Path.Combine(directory, configured);
            if (File.Exists(candidate) && IsExecutable(candidate))
                return candidate;
        }
        return null;
    }

    private static bool IsExecutable(string file) =>
        OperatingSystem.IsWindows() || (File.GetUnixFileMode(file) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;

    /// <summary>Runs git; throws <see cref="CheckpointException"/> unless the exit code is one of <paramref name="accept"/> (0 by default).</summary>
    public async Task<GitResult> RunAsync(IReadOnlyList<string> arguments, byte[]? input = null, int[]? accept = null)
    {
        var result = await RunRawAsync(arguments, input).ConfigureAwait(false);
        if (!(accept ?? [0]).Contains(result.ExitCode))
            throw new CheckpointException($"git {arguments[0]} failed (exit {result.ExitCode}): {result.Error.Trim()}");
        return result;
    }

    public async Task<GitResult> RunRawAsync(IReadOnlyList<string> arguments, byte[]? input = null)
    {
        var start = new ProcessStartInfo(_git)
        {
            WorkingDirectory = _workTree,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // A cleared environment: the user's GIT_DIR, GIT_CONFIG_*, GIT_SSH and the like never reach the store.
        start.Environment.Clear();
        start.Environment["PATH"] = "/usr/local/bin:/usr/bin:/bin";
        start.Environment["HOME"] = _home;
        start.Environment["XDG_CONFIG_HOME"] = _home;
        start.Environment["LANG"] = "C";
        start.Environment["LC_ALL"] = "C";
        if (!_discovery)
        {
            start.Environment["GIT_DIR"] = _gitDirectory;
            start.Environment["GIT_WORK_TREE"] = _workTree;
        }
        else if (_ceiling is not null)
        {
            start.Environment["GIT_CEILING_DIRECTORIES"] = _ceiling;
        }
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "Corvid";
        start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "corvid@localhost";
        foreach (var setting in SafeConfig)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(setting);
        }
        start.ArgumentList.Add("--no-pager");
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        Process process;
        try
        {
            process = Process.Start(start) ?? throw new CheckpointException($"git could not be started from {_git}.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException)
        {
            throw new CheckpointException($"git could not be started from {_git}: {exception.Message}", exception);
        }

        using (process)
        {
            var output = new MemoryStream();
            var reading = process.StandardOutput.BaseStream.CopyToAsync(output);
            var error = process.StandardError.ReadToEndAsync();
            try
            {
                if (input is not null)
                    await process.StandardInput.BaseStream.WriteAsync(input).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // git exited without reading everything; its exit code and error say why.
            }
            finally
            {
                process.StandardInput.Close();
            }

            using var timeout = new CancellationTokenSource(_timeout);
            try
            {
                // Never cancelled by the caller: a git killed half way leaves its lock file behind and the store unusable.
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw new CheckpointException($"git {arguments[0]} did not finish within {_timeout.TotalSeconds:0} s.");
            }
            await reading.ConfigureAwait(false);
            return new GitResult(process.ExitCode, output.ToArray(), await error.ConfigureAwait(false));
        }
    }
}
