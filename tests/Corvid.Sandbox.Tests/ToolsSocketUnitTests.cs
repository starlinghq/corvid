using System.Collections.Immutable;
using Corvid.Sandbox.Launcher;

namespace Corvid.Sandbox.Tests;

/// <summary>A7: the tools socket and environment values on the policy, the launcher's command line and its plan.</summary>
public sealed class ToolsSocketUnitTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void The_tools_socket_and_values_map_to_the_launcher_command_line_and_back()
    {
        SandboxPolicy policy = SandboxPolicy.ForWorkspace("/w") with
        {
            Environment = ["PATH"],
            EnvironmentValues = ImmutableSortedDictionary.CreateRange([new KeyValuePair<string, string>("CODE_MODE", "python=3 x")]),
            Tools = new SandboxTools("/run/user/1000/corvid/code/r1/tools.sock", Token),
        };
        policy.Validate();

        List<string> args = BubblewrapSandbox.Arguments(policy, new SandboxCommand("python3", "s.py"), null, new SandboxOptions(), toolsTokenFile: "/s/tools.token");

        Assert.Equal(
            ["--workspace", "/w", "--env", "PATH", "--setenv", "CODE_MODE=python=3 x",
             "--tools-socket", "/run/user/1000/corvid/code/r1/tools.sock", "--tools-token", "/s/tools.token", "--", "python3", "s.py"],
            args);
        LaunchRequest request = Arguments.ParseLaunch(args);
        Assert.Equal("/run/user/1000/corvid/code/r1/tools.sock", request.ToolsSocket);
        Assert.Equal("/s/tools.token", request.ToolsToken);
        Assert.Equal(["CODE_MODE=python=3 x"], request.SetEnvironment);
        // The token itself is never on the command line, and never printed with the policy.
        Assert.DoesNotContain(args, a => a.Contains(Token, StringComparison.Ordinal));
        Assert.DoesNotContain(Token, policy.Tools!.ToString(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => BubblewrapSandbox.Arguments(policy, new SandboxCommand("x"), null, new SandboxOptions()));
    }

    public static TheoryData<string, SandboxPolicy> Refused => new()
    {
        { "short token", SandboxPolicy.ForWorkspace("/w") with { Tools = new SandboxTools("/s/tools.sock", "short") } },
        { "token charset", SandboxPolicy.ForWorkspace("/w") with { Tools = new SandboxTools("/s/tools.sock", Token + "\n") } },
        { "relative socket", SandboxPolicy.ForWorkspace("/w") with { Tools = new SandboxTools("tools.sock", Token) } },
        { "tools name passed", SandboxPolicy.ForWorkspace("/w") with { Tools = new SandboxTools("/s/tools.sock", Token), Environment = ["CORVID_TOOLS_SOCKET"] } },
        { "tools name set", SandboxPolicy.ForWorkspace("/w") with { Tools = new SandboxTools("/s/tools.sock", Token), EnvironmentValues = Values("CORVID_TOOLS_SOCKET", "/x") } },
        { "secret-like value name", SandboxPolicy.ForWorkspace("/w") with { EnvironmentValues = Values("CORVID_TOOLS_TOKEN", "abc") } },
        { "invalid value name", SandboxPolicy.ForWorkspace("/w") with { EnvironmentValues = Values("1BAD", "x") } },
        { "both passed and set", SandboxPolicy.ForWorkspace("/w") with { EnvironmentValues = Values("PATH", "/x") } },
        { "NUL in value", SandboxPolicy.ForWorkspace("/w") with { EnvironmentValues = Values("CODE_MODE", "a\0b") } },
        { "proxy name set", SandboxPolicy.ForWorkspace("/w") with
            {
                Proxy = new SandboxProxy("/s/proxy.sock", "-----BEGIN CERTIFICATE-----\nAA==\n-----END CERTIFICATE-----\n"),
                EnvironmentValues = Values("HTTPS_PROXY", "http://evil"),
            } },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void Unsafe_tools_and_values_are_refused_before_anything_starts(string why, SandboxPolicy policy)
    {
        Assert.False(string.IsNullOrEmpty(why));
        Assert.Throws<ArgumentException>(policy.Validate);
    }

    [Fact]
    public void A_prepared_launch_refuses_the_tools_socket()
    {
        var sandbox = new BubblewrapSandbox(new SandboxOptions { LauncherPath = "/nonexistent" });
        Assert.Throws<ArgumentException>(() => sandbox.Prepare(SandboxPolicy.ForWorkspace("/w") with { Tools = new SandboxTools("/s/t.sock", Token) },
            new SandboxCommand("x")));
    }

    [Fact]
    public void Library_and_launcher_agree_on_the_tools_paths_and_names()
    {
        Assert.Equal(SandboxPlan.ToolsSocketInside, SandboxTools.SocketInside);
        Assert.Equal(SandboxPlan.ToolsTokenInside, SandboxTools.TokenFileInside);
        Assert.Equal(SandboxPolicy.ToolsEnvironmentNames, SandboxPlan.ToolsEnvironment.Select(pair => pair.Key));
        Assert.Equal([SandboxPlan.ToolsSocketInside, SandboxPlan.ToolsTokenInside], SandboxPlan.ToolsEnvironment.Select(pair => pair.Value));
    }

    [Fact]
    public void The_socket_and_token_are_mounted_after_the_masks_and_the_empty_run()
    {
        var plan = new SandboxPlan
        {
            Bubblewrap = "/usr/bin/bwrap",
            Workspace = "/home/u/project",
            WorkingDirectory = "/home/u/project",
            ReadOnlyPaths = [],
            MaskedDirectories = ["/run/user/1000/corvid/code/r1"],
            MaskedFiles = [],
            Environment = ["PATH=/usr/bin"],
            Network = false,
            BestEffortLayers = false,
            ToolsSocket = "/run/user/1000/corvid/code/r1/tools.sock",
            ToolsToken = "/tmp/s/tools.token",
            Stage2 = ["/opt/corvid-sandbox"],
            Stage2Environment = [],
            OutsideNamespaces = [],
            Command = ["python3"],
        };

        List<string> a = [.. plan.BubblewrapArguments(readyFd: 9)];

        int Index(string option, string path) => Enumerable.Range(0, a.Count - 1).First(i => a[i] == option && a[i + 1] == path);
        int socket = Index("--ro-bind", "/run/user/1000/corvid/code/r1/tools.sock");
        Assert.Equal(SandboxPlan.ToolsSocketInside, a[socket + 2]);
        Assert.Equal(SandboxPlan.ToolsTokenInside, a[Index("--ro-bind", "/tmp/s/tools.token") + 2]);
        Assert.True(Index("--tmpfs", "/run") < socket);
        Assert.True(Index("--tmpfs", "/run/user/1000/corvid/code/r1") < socket, "the socket must be mounted after its directory is masked");
        Assert.DoesNotContain("--share-net", a);
    }

    private static ImmutableSortedDictionary<string, string> Values(string name, string value) =>
        ImmutableSortedDictionary.CreateRange([new KeyValuePair<string, string>(name, value)]);
}
