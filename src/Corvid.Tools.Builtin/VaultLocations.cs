using System.Collections.Immutable;
using Corvid.Approvals;

namespace Corvid.Tools.Builtin;

/// <summary>
/// Where Corvid Vault keeps its store, policy and socket on this machine. The sandbox launcher and the approval pipeline
/// already hide and protect Vault's default directories (<see cref="Corvid.Sandbox.BuiltInDenials"/>); a Vault started
/// with <c>--data</c>, <c>--config</c> or <c>--socket</c> elsewhere is only covered when the host passes these, which the
/// shell tool adds to every sandbox policy as denied paths and <see cref="Protect"/> adds to the hardline list.
/// </summary>
/// <param name="DataDirectory">Vault's store, master key credential and audit log.</param>
/// <param name="ConfigPath">Vault's policy file.</param>
/// <param name="SocketPath">The Unix socket Corvid talks to Vault on, when known.</param>
public sealed record VaultLocations(string DataDirectory, string ConfigPath, string? SocketPath = null)
{
    public string DataDirectory { get; } = Absolute(DataDirectory, nameof(DataDirectory));

    public string ConfigPath { get; } = Absolute(ConfigPath, nameof(ConfigPath));

    public string? SocketPath { get; } = SocketPath is null ? null : Absolute(SocketPath, nameof(SocketPath));

    /// <summary>Every location, normalized: the data directory, the policy file and the socket.</summary>
    public ImmutableArray<string> Paths =>
        [.. new[] { DataDirectory, ConfigPath, SocketPath }.OfType<string>().Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Vault's defaults (as <c>corvid-vault</c> computes them): <c>$XDG_DATA_HOME/corvid-vault</c>,
    /// <c>$XDG_CONFIG_HOME/corvid-vault/vault.json</c> and <c>$XDG_RUNTIME_DIR/corvid-vault/vault.sock</c>.
    /// </summary>
    /// <param name="environment">Reads a variable; null, empty or relative counts as unset.</param>
    public static VaultLocations Default(string home, Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(environment);
        string? Variable(string name) => environment(name) is { Length: > 0 } value && Path.IsPathFullyQualified(value) ? value : null;
        var data = Variable("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share");
        var config = Variable("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config");
        var runtime = Variable("XDG_RUNTIME_DIR");
        return new VaultLocations(Path.Combine(data, "corvid-vault"), Path.Combine(config, "corvid-vault", "vault.json"),
            runtime is null ? null : Path.Combine(runtime, "corvid-vault", "vault.sock"));
    }

    /// <summary>
    /// The approval environment with these locations added to its Vault paths, so the hardline list denies any access to
    /// them exactly as it does for the defaults.
    /// </summary>
    public ApprovalEnvironment Protect(ApprovalEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var protectedPaths = environment.Protected with
        {
            Vault = [.. environment.Protected.Vault.Concat(Paths).Distinct(StringComparer.Ordinal)],
        };
        return new ApprovalEnvironment(environment.Workspace, environment.Home, protectedPaths, environment.Variables);
    }

    private static string Absolute(string path, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, name);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException($"{name}: {path} is not an absolute path.", name);
        var full = Path.GetFullPath(path);
        return full.Length > 1 ? full.TrimEnd('/') : full;
    }
}
