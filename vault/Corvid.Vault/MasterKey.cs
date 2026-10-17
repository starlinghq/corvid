using System.Diagnostics;
using System.Security.Cryptography;

namespace Corvid.Vault;

/// <summary>The 256-bit key that encrypts the store.</summary>
internal static class MasterKey
{
    private const int KeyBytes = 32;
    private const string CredentialName = "corvid-vault-master";

    /// <summary>Loads the master key, creating it on first use.</summary>
    public static byte[] LoadOrCreate(VaultOptions options) => options.KeyProvider switch
    {
        "systemd" => File.Exists(options.KeyPath) ? SystemdDecrypt(options.KeyPath) : SystemdCreate(options.KeyPath),
        "file" => File.Exists(options.KeyPath) ? ReadPlain(options.KeyPath) : CreatePlain(options.KeyPath),
        _ => throw new InvalidOperationException($"Unknown key provider '{options.KeyProvider}' (use systemd or file)."),
    };

    // systemd-creds --user binds the credential to this host and this user (and to the TPM where one is available).
    private static byte[] SystemdCreate(string path)
    {
        byte[] key = RandomNumberGenerator.GetBytes(KeyBytes);
        RunSystemdCreds(["--user", "encrypt", $"--name={CredentialName}", "-", path], key);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return key;
    }

    private static byte[] SystemdDecrypt(string path)
    {
        byte[] key = RunSystemdCreds(["--user", "decrypt", $"--name={CredentialName}", path, "-"], input: null);
        return key.Length == KeyBytes
            ? key
            : throw new InvalidOperationException($"The master key credential decrypted to {key.Length} bytes, expected {KeyBytes}.");
    }

    private static byte[] RunSystemdCreds(string[] arguments, byte[]? input)
    {
        var start = new ProcessStartInfo("systemd-creds")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start systemd-creds.");
        if (input is not null)
        {
            process.StandardInput.BaseStream.Write(input);
        }

        process.StandardInput.Close();
        using var output = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(output);
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"systemd-creds {arguments[1]} failed ({process.ExitCode}): {error.Trim()}");
        }

        return output.ToArray();
    }

    private static byte[] CreatePlain(string path)
    {
        byte[] key = RandomNumberGenerator.GetBytes(KeyBytes);
        PrivateDirectory.WriteAtomically(path, key);
        return key;
    }

    private static byte[] ReadPlain(string path)
    {
        byte[] key = File.ReadAllBytes(path);
        return key.Length == KeyBytes ? key : throw new InvalidOperationException($"{path} holds {key.Length} bytes, expected {KeyBytes}.");
    }
}
