using System.Collections.Immutable;

namespace Corvid.Sandbox;

/// <summary>A command to run: an executable (searched on the command's PATH when it has no slash) and its arguments.</summary>
public sealed record SandboxCommand
{
    public SandboxCommand(string fileName, params IEnumerable<string> arguments)
    {
        ArgumentException.ThrowIfNullOrEmpty(fileName);
        FileName = fileName;
        Arguments = [.. arguments];
    }

    public string FileName { get; }

    public ImmutableArray<string> Arguments { get; }

    /// <summary>Absolute, inside the workspace. Defaults to the workspace.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Written to the command's standard input, which is then closed. Null: input is closed at once.</summary>
    public string? StandardInput { get; init; }

    /// <summary><c>/bin/sh -c script</c>.</summary>
    public static SandboxCommand Shell(string script) => new("/bin/sh", "-c", script);
}

/// <summary>
/// The launcher's command line and environment for a process the caller starts itself
/// (<see cref="BubblewrapSandbox.Prepare"/>). The environment is complete: start the process with nothing inherited.
/// </summary>
public sealed record SandboxLaunch(string Launcher, ImmutableArray<string> Arguments, IReadOnlyDictionary<string, string> Environment);

/// <summary>What happened. Thrown instead when the sandbox could not be set up: see <see cref="SandboxRefusedException"/>.</summary>
public sealed record SandboxResult
{
    /// <summary>The command's exit status; 128 + n when it died from signal n.</summary>
    public required int ExitCode { get; init; }

    /// <summary>Standard output, decoded as UTF-8, cut at <see cref="SandboxOptions.MaxOutputBytes"/>.</summary>
    public required string StandardOutput { get; init; }

    public required string StandardError { get; init; }

    public bool StandardOutputTruncated { get; init; }

    public bool StandardErrorTruncated { get; init; }

    /// <summary>The command ran past <see cref="SandboxOptions.Timeout"/> and was killed with everything it started.</summary>
    public bool TimedOut { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>The confinement layers in force, for example <c>bubblewrap</c>, <c>landlock-v6</c>, <c>seccomp</c>.</summary>
    public ImmutableArray<string> Layers { get; init; } = [];
}

/// <summary>The sandbox could not be set up, so nothing ran. Corvid never falls back to running a command unconfined.</summary>
public sealed class SandboxRefusedException : Exception
{
    public SandboxRefusedException(string message)
        : base(message)
    {
    }

    public SandboxRefusedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>Runs commands under a <see cref="SandboxPolicy"/>. Linux uses bubblewrap; container and SSH backends come later.</summary>
public interface ISandbox
{
    /// <exception cref="SandboxRefusedException">The sandbox could not be set up; the command did not run.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; the command and everything it started were killed.</exception>
    Task<SandboxResult> RunAsync(SandboxPolicy policy, SandboxCommand command, CancellationToken cancellationToken = default);
}
