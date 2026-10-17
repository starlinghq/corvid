using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Corvid.Vault;
using Corvid.Vault.Contracts;

return await Cli.RunAsync(args).ConfigureAwait(false);

internal static class Cli
{
    private const string Usage = """
        corvid-vault: holds secrets for Corvid and uses them on its behalf.

        Usage:
          corvid-vault serve                  Listen on the socket until interrupted.
          corvid-vault init                   Create the master key and a starter vault.json.
          corvid-vault set NAME [--description TEXT]
                                              Store a secret. The value is read without echo from the
                                              terminal, or from standard input when it is not a terminal.
          corvid-vault list                   Names, descriptions and bound hosts (never values).
          corvid-vault rm NAME                Remove a secret.
          corvid-vault audit [--tail N]       Show the last N audit entries (default 20).

        Options for every command:
          --data-dir DIR      Store, key and audit log (default $XDG_DATA_HOME/corvid-vault).
          --config FILE       Policy file (default $XDG_CONFIG_HOME/corvid-vault/vault.json).
          --socket PATH       Socket (default $XDG_RUNTIME_DIR/corvid-vault/vault.sock).
          --key-provider P    systemd (default) or file (testing only; the key is stored unencrypted).
        """;

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 2 : 0;
        }

        try
        {
            var (positional, flags) = Parse(args);
            VaultOptions options = Options(flags);
            return positional[0] switch
            {
                "serve" => await ServeAsync(options).ConfigureAwait(false),
                "init" => Init(options),
                "set" when positional.Count == 2 => Set(options, positional[1], flags.GetValueOrDefault("description") ?? ""),
                "list" => List(options),
                "rm" when positional.Count == 2 => Remove(options, positional[1]),
                "audit" => Audit(options, int.Parse(flags.GetValueOrDefault("tail") ?? "20", System.Globalization.CultureInfo.InvariantCulture)),
                _ => Fail("Unknown command or wrong arguments. Run corvid-vault --help."),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or ArgumentException or FormatException or IOException)
        {
            return Fail(e.Message);
        }
    }

    private static async Task<int> ServeAsync(VaultOptions options)
    {
        if (!Native.TryMakeNonDumpable())
        {
            Console.Error.WriteLine("warning: could not clear the dumpable flag; core dumps and same-uid memory reads are possible.");
        }

        using var core = new VaultCore(options);
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
        using var server = new VaultServer(core, options.SocketPath);
        Task loop = server.Start(stop.Token);
        Console.Error.WriteLine($"corvid-vault listening on {options.SocketPath}");
        await loop.ConfigureAwait(false);
        return 0;
    }

    private static int Init(VaultOptions options)
    {
        PrivateDirectory.Ensure(options.DataDirectory);
        MasterKey.LoadOrCreate(options);
        if (!File.Exists(options.ConfigPath))
        {
            PrivateDirectory.Ensure(Path.GetDirectoryName(options.ConfigPath)!);
            var starter = new PolicyDocument
            {
                Secrets =
                {
                    ["github.token"] = new SecretPolicy
                    {
                        Hosts = ["api.github.com"],
                        Methods = new() { ["GET"] = Rule.Auto, ["POST"] = Rule.Ask, ["PATCH"] = Rule.Ask, ["PUT"] = Rule.Ask, ["DELETE"] = Rule.Confirm },
                        DenyPaths = ["/user/keys*", "/repos/*/*/hooks*", "/authorizations*"],
                        RequestsPerHour = 300,
                    },
                },
            };
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                JsonSerializer.Serialize(writer, starter, VaultInternalJsonContext.Default.PolicyDocument);
            }

            PrivateDirectory.WriteAtomically(options.ConfigPath, buffer.ToArray());
            Console.WriteLine($"Wrote a starter policy to {options.ConfigPath}.");
        }

        Console.WriteLine($"Master key ready ({options.KeyProvider}) in {options.DataDirectory}.");
        return 0;
    }

    private static int Set(VaultOptions options, string name, string description)
    {
        if (!Placeholders.IsValidSecretName(name))
        {
            return Fail($"'{name}' is not a valid name: lower-case letters, digits, '.', '-'; 1 to 64 characters.");
        }

        byte[] value = ReadSecretValue(name);
        try
        {
            if (Entropy.Problem(value) is { } problem)
            {
                return Fail($"The value {problem}");
            }

            using var core = new VaultCore(options);
            core.Store.Set(name, value, description);
            core.Audit.Write(new AuditEvent { Time = DateTimeOffset.UtcNow, Op = "set", Decision = "allowed", Secret = name });
            bool bound = core.Policy.Current.Secrets.TryGetValue(name, out SecretPolicy? policy) && policy.Hosts.Count > 0;
            Console.WriteLine(bound
                ? $"Stored '{name}'."
                : $"Stored '{name}'. It is not usable until vault.json binds it to a host ({options.ConfigPath}).");
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static byte[] ReadSecretValue(string name)
    {
        if (Console.IsInputRedirected)
        {
            using var stdin = Console.OpenStandardInput();
            using var buffer = new MemoryStream();
            stdin.CopyTo(buffer);
            byte[] all = buffer.ToArray();
            int end = all.Length;
            while (end > 0 && all[end - 1] is (byte)'\n' or (byte)'\r')
            {
                end--;
            }

            byte[] trimmed = all[..end];
            CryptographicOperations.ZeroMemory(all);
            return trimmed;
        }

        Console.Error.Write($"Value for {name} (input hidden): ");
        var chars = new List<char>();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                {
                    chars.RemoveAt(chars.Count - 1);
                }

                continue;
            }

            chars.Add(key.KeyChar);
        }

        Console.Error.WriteLine();
        char[] array = [.. chars];
        byte[] value = Encoding.UTF8.GetBytes(array);
        Array.Clear(array);
        chars.Clear();
        return value;
    }

    private static int List(VaultOptions options)
    {
        using var core = new VaultCore(options);
        PolicyDocument policy = core.Policy.Current;
        var secrets = core.Store.Snapshot();
        if (secrets.Count == 0)
        {
            Console.WriteLine("No secrets stored.");
        }

        foreach (var (name, secret) in secrets.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            string hosts = policy.Secrets.TryGetValue(name, out SecretPolicy? p) && p.Hosts.Count > 0 ? string.Join(", ", p.Hosts) : "(not bound)";
            Console.WriteLine($"{name,-32} {hosts,-40} {secret.Description}");
        }

        return 0;
    }

    private static int Remove(VaultOptions options, string name)
    {
        using var core = new VaultCore(options);
        if (!core.Store.Remove(name))
        {
            return Fail($"No secret named '{name}'.");
        }

        core.Audit.Write(new AuditEvent { Time = DateTimeOffset.UtcNow, Op = "remove", Decision = "allowed", Secret = name });
        Console.WriteLine($"Removed '{name}'.");
        return 0;
    }

    private static int Audit(VaultOptions options, int tail)
    {
        using var core = new VaultCore(options);
        foreach (string line in core.Audit.Tail(tail))
        {
            Console.WriteLine(line);
        }

        return 0;
    }

    private static VaultOptions Options(Dictionary<string, string> flags)
    {
        VaultOptions options = flags.ContainsKey("data-dir") && flags.ContainsKey("config") && flags.ContainsKey("socket")
            ? new VaultOptions { DataDirectory = flags["data-dir"], ConfigPath = flags["config"], SocketPath = flags["socket"] }
            : VaultOptions.Default();
        return options with
        {
            DataDirectory = flags.GetValueOrDefault("data-dir") ?? options.DataDirectory,
            ConfigPath = flags.GetValueOrDefault("config") ?? options.ConfigPath,
            SocketPath = flags.GetValueOrDefault("socket") ?? options.SocketPath,
            KeyProvider = flags.GetValueOrDefault("key-provider") ?? options.KeyProvider,
        };
    }

    private static (List<string> Positional, Dictionary<string, string> Flags) Parse(string[] args)
    {
        var positional = new List<string>();
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"{args[i]} needs a value.");
                }

                flags[args[i][2..]] = args[++i];
            }
            else
            {
                positional.Add(args[i]);
            }
        }

        return positional.Count == 0 ? throw new ArgumentException("No command given.") : (positional, flags);
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"corvid-vault: {message}");
        return 1;
    }
}
