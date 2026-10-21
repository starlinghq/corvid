using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Corvid.Vault.Client;

namespace Corvid.Vault.Tests;

/// <summary>A Vault serving on a temporary socket, with one secret bound to 127.0.0.1 over plain HTTP.</summary>
internal sealed class TestVault : IDisposable
{
    public const string SecretName = "test.token";

    private readonly CancellationTokenSource _stop = new();
    private readonly VaultServer _server;
    private int _policyWrites;

    public TestVault(Action<PolicyDocument>? configure = null)
    {
        // Short, because the socket path under it is limited to 108 bytes.
        Root = Corvid.Tests.ShortTemp.Create("cv-");
        Options = new VaultOptions
        {
            DataDirectory = Path.Combine(Root, "data"),
            ConfigPath = Path.Combine(Root, "vault.json"),
            SocketPath = Path.Combine(Root, "run", "vault.sock"),
            KeyProvider = "file",
        };
        SecretValue = "tkn_" + RandomNumberGenerator.GetString("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789", 40);
        Policy = new PolicyDocument
        {
            AllowPlainHttpLoopback = true,
            Secrets = { [SecretName] = new SecretPolicy { Hosts = ["127.0.0.1"] } },
        };
        // A failure from here on (configure, Vault's start) leaves no directory behind.
        (Core, _server) = Corvid.Tests.ShortTemp.SetUp(Root, () =>
        {
            configure?.Invoke(Policy);
            WritePolicy();
            var core = new VaultCore(Options);
            VaultServer? server = null;
            try
            {
                core.Store.Set(SecretName, Encoding.UTF8.GetBytes(SecretValue), "Test API token");
                server = new VaultServer(core, Options.SocketPath);
                server.Start(_stop.Token);
                return (core, server);
            }
            catch
            {
                server?.Dispose();
                core.Dispose();
                throw;
            }
        });
    }

    public string Root { get; }

    public VaultOptions Options { get; }

    public string SecretValue { get; }

    public PolicyDocument Policy { get; }

    public VaultCore Core { get; }

    public VaultClient Client(string session = "session-1") => new(session, Options.SocketPath);

    /// <summary>Rewrites vault.json after a change to <see cref="Policy"/>, with a distinct timestamp so Vault reloads it.</summary>
    public void WritePolicy()
    {
        File.WriteAllBytes(Options.ConfigPath, JsonSerializer.SerializeToUtf8Bytes(Policy, VaultInternalJsonContext.Default.PolicyDocument));
        File.SetLastWriteTimeUtc(Options.ConfigPath, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_policyWrites));
    }

    public void Dispose()
    {
        _stop.Cancel();
        _server.Dispose();
        Core.Dispose();
        _stop.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}

/// <summary>
/// A loopback HTTP server that echoes the Authorization header it received: in the body and an X-Echo header (any
/// path), gzip-compressed (<c>/gzip</c>), or in a redirect's Location (<c>/redirect</c>).
/// </summary>
internal sealed class EchoServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public EchoServer()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; }

    public ConcurrentQueue<string?> ReceivedAuthorization { get; } = new();

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            string? auth = context.Request.Headers["Authorization"];
            ReceivedAuthorization.Enqueue(auth);
            HttpListenerResponse response = context.Response;
            byte[] body = Encoding.UTF8.GetBytes($"you sent: {auth}");
            switch (context.Request.Url!.AbsolutePath)
            {
                case "/redirect":
                    response.StatusCode = 302;
                    response.RedirectLocation = $"http://elsewhere.invalid/?leak={Uri.EscapeDataString(auth ?? "")}";
                    body = [];
                    break;
                case "/gzip":
                    using (var compressed = new MemoryStream())
                    {
                        using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                        {
                            gzip.Write(body);
                        }

                        body = compressed.ToArray();
                    }

                    response.AddHeader("Content-Encoding", "gzip");
                    break;
                default:
                    response.AddHeader("X-Echo", auth ?? "");
                    break;
            }

            response.ContentLength64 = body.Length;
            await response.OutputStream.WriteAsync(body);
            response.Close();
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>Reads a child process's memory through /proc. Needs ptrace access: Yama scope 0 or 1 with the reader as an ancestor.</summary>
internal static class ProcessMemory
{
    public static bool CanRun => OperatingSystem.IsLinux() && File.Exists("/proc/self/maps");

    /// <summary>The labels of the patterns found anywhere in the readable memory of <paramref name="pid"/>.</summary>
    public static HashSet<string> Find(int pid, IReadOnlyDictionary<string, byte[]> patterns, out long bytesScanned)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        int overlap = patterns.Values.Max(p => p.Length) - 1;
        bytesScanned = 0;
        using var mem = new FileStream($"/proc/{pid}/mem", FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize: 1);
        byte[] buffer = new byte[(1 << 20) + overlap];
        foreach (string line in File.ReadLines($"/proc/{pid}/maps"))
        {
            string[] parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
            string[] range = parts[0].Split('-');
            ulong start = Convert.ToUInt64(range[0], 16);
            ulong end = Convert.ToUInt64(range[1], 16);
            string path = parts.Length > 5 ? parts[5].Trim() : "";
            if (parts[1][0] != 'r' || end > long.MaxValue || path.StartsWith("[vvar", StringComparison.Ordinal) || path == "[vsyscall]")
            {
                continue;
            }

            try
            {
                long position = (long)start;
                int carried = 0;
                while (position < (long)end)
                {
                    int want = (int)Math.Min(1 << 20, (long)end - position);
                    mem.Seek(position, SeekOrigin.Begin);
                    int read = mem.Read(buffer, carried, want);
                    if (read <= 0)
                    {
                        break;
                    }

                    var window = buffer.AsSpan(0, carried + read);
                    foreach (var (label, pattern) in patterns)
                    {
                        if (!found.Contains(label) && window.IndexOf(pattern) >= 0)
                        {
                            found.Add(label);
                        }
                    }

                    bytesScanned += read;
                    position += read;
                    carried = Math.Min(overlap, window.Length);
                    window[^carried..].CopyTo(buffer);
                }
            }
            catch (IOException)
            {
                // Some mappings (device memory, guard pages) refuse reads; nothing of ours lives there.
            }
        }

        return found;
    }

    /// <summary>The value as it could appear in a .NET process: UTF-8, UTF-16, and the encodings the scrubber covers.</summary>
    public static Dictionary<string, byte[]> Encodings(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["utf8"] = utf8,
            ["utf16"] = Encoding.Unicode.GetBytes(value),
            ["utf8-prefix"] = utf8[..12],
            ["utf16-prefix"] = Encoding.Unicode.GetBytes(value[..12]),
            ["base64"] = Encoding.ASCII.GetBytes(Convert.ToBase64String(utf8).TrimEnd('=')),
            ["base64-utf16"] = Encoding.Unicode.GetBytes(Convert.ToBase64String(utf8).TrimEnd('=')),
            ["hex"] = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(utf8)),
            ["HEX"] = Encoding.ASCII.GetBytes(Convert.ToHexString(utf8)),
        };
    }
}
