using System.Collections.Immutable;

namespace Corvid.Sandbox;

/// <summary>
/// What <c>corvid-sandbox</c> hides from every command whatever the policy says (plan §10.1, <c>vault.md</c> §8): secret
/// directories in each home, Corvid Vault's data and config directories, attached mode's token directory, the runtime
/// directory, and <c>.env</c> files.
/// The launcher has no dependencies and keeps its own copy, which is authoritative; a test keeps the two in step. The
/// approval pipeline (C-007b) builds its hardline list from these, so approvals and the sandbox protect the same paths.
/// </summary>
public static class BuiltInDenials
{
    /// <summary>The directory name Vault uses under the XDG data, config and runtime directories.</summary>
    public const string VaultDirectoryName = "corvid-vault";

    /// <summary>Relative to each home: credentials other tools keep in plain files.</summary>
    public static ImmutableArray<string> SecretDirectories { get; } = [".ssh", ".gnupg", ".aws", ".config/gh"];

    /// <summary>
    /// Vault's data and config directories: <c>~/.local/share/corvid-vault</c> and <c>~/.config/corvid-vault</c>, and the
    /// same under <c>$XDG_DATA_HOME</c> and <c>$XDG_CONFIG_HOME</c> when those are set. Both, because the variables can
    /// differ between the process that started Vault and the one that runs a command.
    /// </summary>
    /// <param name="environment">Reads a variable; null or a relative path counts as unset.</param>
    public static ImmutableArray<string> VaultDirectories(string home, Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        ArgumentNullException.ThrowIfNull(environment);
        var directories = new List<string>
        {
            Path.Combine(home, ".local", "share", VaultDirectoryName),
            Path.Combine(home, ".config", VaultDirectoryName),
        };
        if (Absolute(environment("XDG_DATA_HOME")) is { } data)
        {
            directories.Add(Path.Combine(data, VaultDirectoryName));
        }

        if (Absolute(environment("XDG_CONFIG_HOME")) is { } config)
        {
            directories.Add(Path.Combine(config, VaultDirectoryName));
        }

        return [.. directories.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>Attached mode's directory under the XDG state directory.</summary>
    public const string AttachStateDirectory = "corvid/attach";

    /// <summary>
    /// Attached mode's state directory (G-1): <c>~/.local/state/corvid/attach</c> and <c>$XDG_STATE_HOME/corvid/attach</c>
    /// when set. It holds the owner token, the paired devices' digests and a client's device tokens, which answer approvals
    /// over <c>corvid serve --attach</c>: no command may read them and no tool may write the devices file (A6 review H2).
    /// Corvid.App.Core's <c>AttachedPaths.StateDirectory</c> computes the same path; a test keeps the two in step.
    /// </summary>
    /// <param name="environment">Reads <c>XDG_STATE_HOME</c>; null or a relative path counts as unset.</param>
    public static ImmutableArray<string> AttachStateDirectories(string home, Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        ArgumentNullException.ThrowIfNull(environment);
        var directories = new List<string> { Path.Combine(home, ".local", "state", AttachStateDirectory) };
        if (Absolute(environment("XDG_STATE_HOME")) is { } state)
        {
            directories.Add(Path.Combine(state, AttachStateDirectory));
        }

        return [.. directories.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Secret paths outside Vault: <see cref="SecretDirectories"/> under the home, and <c>$XDG_CONFIG_HOME/gh</c> when set.
    /// </summary>
    public static ImmutableArray<string> SecretPaths(string home, Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        ArgumentNullException.ThrowIfNull(environment);
        var paths = SecretDirectories.Select(relative => Path.Combine(home, relative)).ToList();
        if (Absolute(environment("XDG_CONFIG_HOME")) is { } config)
        {
            paths.Add(Path.Combine(config, "gh"));
        }

        return [.. paths.Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// <c>$XDG_RUNTIME_DIR</c> when set: it holds Vault's socket and those of ssh-agent, gpg-agent and the session bus, so
    /// the sandbox hides all of it.
    /// </summary>
    public static string? RuntimeDirectory(Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        return Absolute(environment("XDG_RUNTIME_DIR"));
    }

    /// <summary><c>.env</c> and <c>.env.*</c>, examples included: a copy of a real file is often named like one.</summary>
    public static bool IsEnvFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return name == ".env" || name.StartsWith(".env.", StringComparison.Ordinal);
    }

    private static string? Absolute(string? path) => path is { Length: > 0 } && Path.IsPathFullyQualified(path) ? path : null;
}
