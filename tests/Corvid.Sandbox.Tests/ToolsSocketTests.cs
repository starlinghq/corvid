using System.Collections.Immutable;
using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Corvid.Sandbox.Tests;

/// <summary>
/// A7: code mode's tools socket. A sandboxed script reaches the one socket Corvid mounts for its run, and nothing new beside
/// it: not the socket's neighbours on the host, not the socket at its host path, not a host abstract socket. Real
/// bubblewrap and the real launcher; Python's standard library is the client, as a script would use it.
/// </summary>
[Trait("Category", "Confinement")]
public sealed class ToolsSocketTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SandboxLayout _layout = new();

    // The host side: a short path (sun_path holds 108 bytes) in a directory only this user can enter, as code mode makes it.
    private readonly DirectoryInfo _run = Directory.CreateTempSubdirectory("cts-");

    private readonly string _token = RandomNumberGenerator.GetHexString(48, lowercase: true);

    public void Dispose()
    {
        _run.Delete(recursive: true);
        _layout.Dispose();
    }

    private string ToolsSocket => Path.Combine(_run.FullName, "tools.sock");

    private string OtherSocket => Path.Combine(_run.FullName, "other.sock");

    private static Socket Listen(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        socket.Listen(4);
        return socket;
    }

    [Fact]
    public async Task A_sandboxed_script_reaches_the_tools_socket_and_nothing_new_beside_it()
    {
        Host.RequireBubblewrap();
        Assert.SkipUnless(File.Exists("/usr/bin/python3"), "/usr/bin/python3 is not installed.");
        using Socket tools = Listen(ToolsSocket);
        using Socket other = Listen(OtherSocket);
        string abstractName = "corvid-a7-" + RandomNumberGenerator.GetHexString(12, lowercase: true);
        using Socket hostAbstract = Listen("\0" + abstractName);

        // The host answers one connection: it reads the token line and says whether it matched.
        Task<string> served = Task.Run(async () =>
        {
            using Socket client = await tools.AcceptAsync(Ct);
            using var stream = new NetworkStream(client, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line = await reader.ReadLineAsync(Ct);
            bool matched = line is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(line), Encoding.UTF8.GetBytes(_token));
            await stream.WriteAsync(Encoding.UTF8.GetBytes(matched ? "{\"ok\":true}\n" : "{\"ok\":false}\n"), Ct);
            return line ?? "";
        }, Ct);

        File.WriteAllText(Path.Combine(_layout.Workspace, "client.py"), $$"""
            import os, socket
            print("run=" + " ".join(sorted(os.listdir("/run/corvid-sandbox"))))
            print("socket-env=" + os.environ.get("CORVID_TOOLS_SOCKET", ""))
            print("token-env=" + os.environ.get("CORVID_TOOLS_TOKEN_FILE", ""))
            print("value=" + os.environ.get("CODE_MODE_LANGUAGE", ""))
            token = open(os.environ["CORVID_TOOLS_TOKEN_FILE"]).read()
            try:
                open(os.environ["CORVID_TOOLS_TOKEN_FILE"], "w").write("x")
                print("token-write=allowed")
            except OSError:
                print("token-write=refused")
            for name, address in [("other", "{{OtherSocket}}"), ("host-path", "{{ToolsSocket}}"), ("abstract", "\0{{abstractName}}")]:
                probe = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                try:
                    probe.connect(address)
                    print(name + "=reached")
                except OSError:
                    print(name + "=unreachable")
                finally:
                    probe.close()
            s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
            s.connect(os.environ["CORVID_TOOLS_SOCKET"])
            s.sendall((token + "\n").encode())
            print("reply=" + s.makefile().readline().strip())
            """);
        SandboxPolicy policy = _layout.Policy with
        {
            Tools = new SandboxTools(ToolsSocket, _token),
            // The run directory is outside /tmp on a host whose TMPDIR is elsewhere (CI): hidden as code mode hides it.
            DeniedPaths = [_run.FullName],
            EnvironmentValues = ImmutableSortedDictionary.CreateRange([new KeyValuePair<string, string>("CODE_MODE_LANGUAGE", "python")]),
        };

        SandboxResult result = await _layout.Sandbox().RunAsync(policy, new SandboxCommand("/usr/bin/python3", "-I", "client.py"), Ct);

        Assert.True(result.ExitCode == 0, $"exit {result.ExitCode}\nstdout: {result.StandardOutput}\nstderr: {result.StandardError}");
        string output = result.StandardOutput;
        Assert.Contains("run=tools.sock tools.token\n", output, StringComparison.Ordinal);
        Assert.Contains("socket-env=/run/corvid-sandbox/tools.sock\n", output, StringComparison.Ordinal);
        Assert.Contains("token-env=/run/corvid-sandbox/tools.token\n", output, StringComparison.Ordinal);
        Assert.Contains("value=python\n", output, StringComparison.Ordinal);
        Assert.Contains("token-write=refused\n", output, StringComparison.Ordinal);
        Assert.Contains("other=unreachable\n", output, StringComparison.Ordinal);
        Assert.Contains("host-path=unreachable\n", output, StringComparison.Ordinal);
        Assert.Contains("abstract=unreachable\n", output, StringComparison.Ordinal);
        // The control: the mounted socket works in the same run, with the token the host wrote.
        Assert.EndsWith("reply={\"ok\":true}\n", output, StringComparison.Ordinal);
        Assert.Equal(_token, await served.WaitAsync(TimeSpan.FromSeconds(30), Ct));
        Assert.False(other.Poll(0, SelectMode.SelectRead), "a connection reached the socket that was not mounted");
        Assert.False(hostAbstract.Poll(0, SelectMode.SelectRead), "a connection reached the host's abstract socket");
        Assert.DoesNotContain(_token, output + result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_launcher_refuses_unsafe_tools_arguments_and_runs_nothing()
    {
        Host.RequireBubblewrap();
        using Socket tools = Listen(ToolsSocket);
        string token = Path.Combine(_layout.Outside, "tools.token");
        File.WriteAllText(token, _token);
        string link = Path.Combine(_layout.Outside, "token-link");
        File.CreateSymbolicLink(link, token);
        string inWorkspace = Path.Combine(_layout.Workspace, "tools.token");
        File.WriteAllText(inWorkspace, _token);
        string empty = Path.Combine(_layout.Outside, "empty.token");
        File.WriteAllText(empty, "");
        string marker = Path.Combine(_layout.Workspace, "ran");
        string[] ok = ["--tools-socket", ToolsSocket, "--tools-token", token];

        (string[] Arguments, string Expected)[] cases =
        [
            (["--tools-socket", Path.Combine(_run.FullName, "missing.sock"), "--tools-token", token], "does not exist"),
            (["--tools-socket", ToolsSocket], "go together"),
            (["--tools-token", token], "go together"),
            (["--tools-socket", ToolsSocket, "--tools-token", link], "is a symlink"),
            (["--tools-socket", ToolsSocket, "--tools-token", inWorkspace], "inside the workspace"),
            (["--tools-socket", ToolsSocket, "--tools-token", empty], "empty or larger"),
            ([.. ok, "--env", "CORVID_TOOLS_SOCKET"], "collides with a variable the tools socket sets"),
            ([.. ok, "--setenv", "corvid_tools_token_file=/x"], "looks like a secret"),
            ([.. ok, "--setenv", "CORVID_TOOLS_SOCKET=/x"], "collides with a variable the tools socket sets"),
            (["--setenv", "GITHUB_TOKEN=x"], "looks like a secret"),
            (["--setenv", "NOVALUE"], "must be NAME=VALUE"),
            (["--setenv", "PATH=/x", "--env", "PATH"], "given twice"),
        ];
        foreach (var (arguments, expected) in cases)
        {
            using Process launcher = Process.Start(_layout.Launcher(["--workspace", _layout.Workspace, .. arguments, "--", "/usr/bin/touch", marker]))!;
            Task<string> stdout = launcher.StandardOutput.ReadToEndAsync(Ct);
            string stderr = await launcher.StandardError.ReadToEndAsync(Ct);
            await launcher.WaitForExitAsync(Ct);
            await stdout;
            Assert.True(launcher.ExitCode == 125, $"{string.Join(' ', arguments)}: exit {launcher.ExitCode}, {stderr}");
            Assert.Contains(expected, stderr, StringComparison.Ordinal);
            Assert.False(File.Exists(marker));
        }

        // The control: the same launcher with valid arguments runs the command.
        using (Process control = Process.Start(_layout.Launcher(["--workspace", _layout.Workspace, .. ok, "--", "/usr/bin/touch", marker]))!)
        {
            Task<string> stdout = control.StandardOutput.ReadToEndAsync(Ct);
            string stderr = await control.StandardError.ReadToEndAsync(Ct);
            await control.WaitForExitAsync(Ct);
            await stdout;
            Assert.True(control.ExitCode == 0, $"control: exit {control.ExitCode}, {stderr}");
        }

        Assert.True(File.Exists(marker));
        Assert.False(tools.Poll(0, SelectMode.SelectRead));
    }
}
