namespace Corvid.Vault;

/// <summary>Where Vault keeps things. Defaults follow the XDG base directories.</summary>
internal sealed record VaultOptions
{
    /// <summary>Store, master key credential and audit log. Created with mode 0700.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>Policy file (<c>vault.json</c>).</summary>
    public required string ConfigPath { get; init; }

    /// <summary>The Unix socket Corvid connects to. Its directory is created with mode 0700.</summary>
    public required string SocketPath { get; init; }

    /// <summary><c>systemd</c> (default) or <c>file</c> (testing only: the master key sits unencrypted on disk).</summary>
    public string KeyProvider { get; init; } = "systemd";

    public string StorePath => Path.Combine(DataDirectory, "store.bin");

    public string AuditPath => Path.Combine(DataDirectory, "audit.jsonl");

    public string KeyPath => Path.Combine(DataDirectory, KeyProvider == "file" ? "master.key" : "master.cred");

    public static VaultOptions Default()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string data = Env("XDG_DATA_HOME") ?? Path.Combine(home, ".local", "share");
        string config = Env("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config");
        string runtime = Env("XDG_RUNTIME_DIR") ?? throw new InvalidOperationException("XDG_RUNTIME_DIR is not set; pass --socket.");
        return new VaultOptions
        {
            DataDirectory = Path.Combine(data, "corvid-vault"),
            ConfigPath = Path.Combine(config, "corvid-vault", "vault.json"),
            SocketPath = Path.Combine(runtime, "corvid-vault", "vault.sock"),
        };
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
}

internal static class PrivateDirectory
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Creates the directory with mode 0700, or tightens an existing one to 0700.</summary>
    public static void Ensure(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, OwnerOnly);
        File.SetUnixFileMode(path, OwnerOnly);
    }

    /// <summary>Writes a file with mode 0600 via a temporary file and an atomic rename.</summary>
    public static void WriteAtomically(string path, ReadOnlySpan<byte> contents)
    {
        string temp = path + ".tmp-" + Environment.ProcessId;
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(temp, options))
        {
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}
