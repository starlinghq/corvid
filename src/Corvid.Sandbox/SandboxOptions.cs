namespace Corvid.Sandbox;

public sealed record SandboxOptions
{
    /// <summary>
    /// The corvid-sandbox executable. Default: $CORVID_SANDBOX, then <c>corvid-sandbox</c> next to the application. It
    /// must not be inside a workspace: the launcher refuses to run from anywhere a command could overwrite it.
    /// </summary>
    public string? LauncherPath { get; init; }

    /// <summary>bubblewrap to use. Default: the first <c>bwrap</c> on PATH, then <c>/usr/bin/bwrap</c>.</summary>
    public string? BubblewrapPath { get; init; }

    /// <summary>Wall-clock limit; the command and everything it started are killed after it.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>Bytes kept from each of standard output and standard error; the rest is read and discarded.</summary>
    public int MaxOutputBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// Refuse to run when the kernel lacks Landlock or the architecture has no seccomp filter. bubblewrap is required
    /// regardless.
    /// </summary>
    public bool RequireKernelLayers { get; init; } = true;

    /// <summary>
    /// The environment the policy's names are read from, and the launcher's HOME and XDG directories. Default: this
    /// process's environment.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}
