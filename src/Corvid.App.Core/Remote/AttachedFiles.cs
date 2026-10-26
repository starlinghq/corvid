using System.Security.Cryptography;
using System.Text;

namespace Corvid.App.Core.Remote;

/// <summary>
/// Where attached mode keeps its files on the engine's machine (G-1), all under <c>$XDG_STATE_HOME/corvid/attach</c>
/// (<c>~/.local/state/corvid/attach</c>), a directory only its owner can enter: the owner token <c>corvid serve --attach</c>
/// generates when no Vault secret names one (<see cref="OwnerTokenFile"/>), the paired devices (<see cref="DevicesFile"/>,
/// digests only), and, on a client, the device tokens it got by pairing (<see cref="ClientTokensFile"/>). The Unix socket is
/// <c>$XDG_RUNTIME_DIR/corvid/attach.sock</c>, or in the state directory when there is no runtime directory.
/// </summary>
public static class AttachedPaths
{
    /// <summary>The state directory for attached mode, from <c>XDG_STATE_HOME</c> or the home directory.</summary>
    public static string StateDirectory
    {
        get
        {
            var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            if (string.IsNullOrWhiteSpace(state) || !Path.IsPathRooted(state))
                state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
            return Path.Combine(state, "corvid", "attach");
        }
    }

    /// <summary>The owner token generated on the engine's machine (0600), read by an app attaching on the same machine.</summary>
    public static string OwnerTokenFile => Path.Combine(StateDirectory, "owner.token");

    /// <summary>The paired devices: names and SHA-256 digests of their tokens, never a token (0600).</summary>
    public static string DevicesFile => Path.Combine(StateDirectory, "devices.json");

    /// <summary>A client's device tokens, by the address it paired with (0600).</summary>
    public static string ClientTokensFile => Path.Combine(StateDirectory, "client-tokens.json");

    /// <summary>The default Unix socket <c>corvid serve --attach</c> listens on.</summary>
    public static string DefaultSocket
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return !string.IsNullOrWhiteSpace(runtime) && Path.IsPathRooted(runtime)
                ? Path.Combine(runtime, "corvid", "attach.sock")
                : Path.Combine(StateDirectory, "attach.sock");
        }
    }
}

/// <summary>
/// Files only their owner may read (G-1's tokens): written whole through a temporary file and a rename, in a directory made
/// 0700 and with the file 0600 from the moment it exists; read only when neither group nor others can read it, so a token a
/// careless <c>chmod</c> exposed is refused rather than trusted.
/// </summary>
public static class PrivateFiles
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode Exposed = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite;

    /// <summary>Makes the directory (0700 when new) and returns its path.</summary>
    public static string EnsureDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory, OwnerDirectory);
        return directory;
    }

    /// <summary>Writes the text whole: readers see the old file or the new one, never a part.</summary>
    public static void Write(string path, string text)
    {
        EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)) + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerOnly;
        try
        {
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(Encoding.UTF8.GetBytes(text));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            throw;
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> holding <c>path.lock</c> exclusively, so two processes (the server pairing a device,
    /// <c>corvid serve revoke</c>) never write over each other's change. Waits up to 5 s for the other.
    /// </summary>
    /// <exception cref="IOException">Another process held the lock throughout.</exception>
    public static T Locked<T>(string path, Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = OwnerOnly;
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            FileStream held;
            try
            {
                held = new FileStream(path + ".lock", options);
            }
            catch (IOException) when (DateTime.UtcNow < until)
            {
                Thread.Sleep(20);
                continue;
            }
            using (held)
                return action();
        }
    }

    /// <summary>The file's text, or null when it does not exist.</summary>
    /// <exception cref="UnauthorizedAccessException">Group or others can read or write it.</exception>
    public static string? Read(string path)
    {
        if (!File.Exists(path))
            return null;
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & Exposed) != 0)
            throw new UnauthorizedAccessException($"{path} can be read by others than its owner; refusing it (chmod 600 it, or delete it to have it made again).");
        return File.ReadAllText(path, Encoding.UTF8);
    }
}

/// <summary>Attached mode's secrets: tokens and pairing codes, made from the system's random generator.</summary>
public static class AttachedSecrets
{
    // No 0/O, 1/I/L, so a code read off a screen is typed right.
    private const string CodeAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    /// <summary>A new token: 32 random bytes, base64url (43 characters).</summary>
    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>A new pairing code: ten characters of a 31-letter alphabet (about 49 bits), shown as <c>XXXXX-XXXXX</c>.</summary>
    public static string NewPairingCode()
    {
        Span<char> code = stackalloc char[11];
        for (var i = 0; i < code.Length; i++)
            code[i] = i == 5 ? '-' : CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(code);
    }

    /// <summary>A code as the person typed it, in the form <see cref="NewPairingCode"/> makes: upper case, no spaces or dashes.</summary>
    public static string NormalizeCode(string code) =>
        new([.. code.Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant)]);

    /// <summary>The SHA-256 digest of a token, as stored: lower-case hex.</summary>
    public static string Digest(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>Whether two digests or tokens are equal, in time that does not depend on where they differ.</summary>
    public static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}

/// <summary>
/// The owner token on the engine's machine (G-1): generated once into <see cref="AttachedPaths.OwnerTokenFile"/> (0600)
/// when no Vault secret is configured, and found by a client on the same machine there, or in
/// <see cref="EnvironmentVariable"/> (the way to hand a Vault-held one to the app or <c>corvid serve pair</c>).
/// </summary>
public static class OwnerTokens
{
    /// <summary>The environment variable a client reads the owner token from before the file.</summary>
    public const string EnvironmentVariable = "CORVID_ATTACH_TOKEN";

    /// <summary>The token in the file, made first when there is none.</summary>
    /// <exception cref="UnauthorizedAccessException">The file can be read by others than its owner.</exception>
    public static string Ensure(string path) => PrivateFiles.Locked(path, () =>
    {
        if (PrivateFiles.Read(path)?.Trim() is { Length: >= 24 } existing)
            return existing;
        var token = AttachedSecrets.NewToken();
        PrivateFiles.Write(path, token + "\n");
        return token;
    });

    /// <summary>The owner token from <see cref="EnvironmentVariable"/> or the file, or null when neither has one.</summary>
    /// <exception cref="UnauthorizedAccessException">The file can be read by others than its owner.</exception>
    public static string? Find(string path) =>
        Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim() is { Length: > 0 } given ? given
        : PrivateFiles.Read(path)?.Trim() is { Length: > 0 } stored ? stored
        : null;
}
