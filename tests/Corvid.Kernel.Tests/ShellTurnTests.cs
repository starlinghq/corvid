using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Kernel.Turns;
using Corvid.Sandbox;
using Corvid.Tools.Builtin;

namespace Corvid.Kernel.Tests;

/// <summary>Records what would run in the sandbox instead of running it.</summary>
internal sealed class RecordingSandbox : ISandbox
{
    public List<(SandboxPolicy Policy, SandboxCommand Command)> Runs { get; } = [];

    public Task<SandboxResult> RunAsync(SandboxPolicy policy, SandboxCommand command, CancellationToken cancellationToken = default)
    {
        lock (Runs)
            Runs.Add((policy, command));
        return Task.FromResult(new SandboxResult { ExitCode = 0, StandardOutput = "ran: " + command.Arguments[^1], StandardError = "" });
    }
}

/// <summary>
/// The built-in shell tool (C-012) in the real turn loop: the call is assessed by the approval pipeline as the parsed
/// command, and the tool runs only with the approval the loop hands it.
/// </summary>
public sealed class ShellTurnTests
{
    private static readonly VaultLocations Vault = new("/srv/vault/data", "/etc/corvid-vault/policy.json", "/run/vault/vault.sock");

    private static (TurnHarness Harness, RecordingSandbox Sandbox) Harness(FakeGenerator generator, FakePrompter? prompter = null,
        TurnOptions? options = null)
    {
        var sandbox = new RecordingSandbox();
        var shell = new ShellTool(new SandboxedShell(sandbox, new ShellOptions { Workspace = TurnHarness.Workspace, Vault = Vault }));
        var harness = new TurnHarness(generator, prompter: prompter, options: options,
            approvalOptions: new ApprovalOptions { ShellTools = BuiltinTools.ShellToolMapping }, tools: tools => tools.Add(shell));
        return (harness, sandbox);
    }

    [Fact]
    public async Task A_read_only_command_runs_sandboxed_without_asking()
    {
        var prompter = FakePrompter.Always(approve: false);
        var (harness, sandbox) = Harness(new FakeGenerator(Reply.Call("shell", """{"command":"ls -la src"}"""), Reply.Say("Listed.")), prompter);
        using var _ = harness;

        var result = await harness.RunAsync("show the files in src with the shell");

        Assert.Equal(TurnStatus.Answered, result.Status);
        Assert.Empty(prompter.Prompts);
        var (policy, command) = Assert.Single(sandbox.Runs);
        Assert.Equal(["-c", "ls -la src"], command.Arguments);
        Assert.Equal("/bin/bash", command.FileName);
        Assert.False(policy.Network);
        // Vault's configured, non-default locations are hidden from every command.
        Assert.Equal(["/srv/vault/data", "/etc/corvid-vault/policy.json", "/run/vault/vault.sock"], policy.DeniedPaths);
        var output = Assert.Single(harness.Payloads().OfType<ToolResult>());
        Assert.False(output.IsError);
        Assert.Equal("ran: ls -la src", output.Result.GetProperty("stdout").GetString());
    }

    [Fact]
    public async Task A_side_effect_asks_with_the_parsed_command_and_runs_only_when_approved()
    {
        var approving = FakePrompter.Always(approve: true);
        var (approved, sandbox) = Harness(new FakeGenerator(Reply.Call("shell", """{"command":"rm -rf build"}"""), Reply.Say("Removed.")), approving);
        using (approved)
        {
            await approved.RunAsync("clean the build output");
            Assert.Equal("rm", Assert.Single(Assert.Single(approving.Prompts).Analysis.Commands).Verb);
            Assert.Single(sandbox.Runs);
        }

        var denying = FakePrompter.Always(approve: false);
        var (denied, untouched) = Harness(new FakeGenerator(Reply.Call("shell", """{"command":"rm -rf build"}"""), Reply.Say("Kept.")), denying);
        using (denied)
        {
            await denied.RunAsync("clean the build output");
            Assert.Single(denying.Prompts);
            Assert.Empty(untouched.Runs);
            Assert.Contains("denied by the user", Assert.Single(denied.Payloads().OfType<ToolResult>()).Result.GetRawText());
        }
    }

    [Theory]
    [InlineData("cat ~/.local/share/corvid-vault/store.bin")]
    [InlineData("timeout 5 bash -c 'rm -rf /home/user/other'")]
    [InlineData("cat /srv/vault/data/store.bin")]
    public async Task Hardline_commands_never_reach_the_sandbox_or_the_user(string command)
    {
        var prompter = FakePrompter.Always(approve: true);
        var generator = new FakeGenerator(Reply.Call("shell", JsonSerializer.Serialize(new Dictionary<string, string> { ["command"] = command })),
            Reply.Say("Refused."));
        var sandbox = new RecordingSandbox();
        var shell = new ShellTool(new SandboxedShell(sandbox, new ShellOptions { Workspace = TurnHarness.Workspace, Vault = Vault }));
        using var harness = new TurnHarness(generator, prompter: prompter,
            approvalOptions: new ApprovalOptions { ShellTools = BuiltinTools.ShellToolMapping }, tools: tools => tools.Add(shell));
        // The host protects Vault's configured locations as it does the defaults.
        var approvals = new ApprovalPipeline(Vault.Protect(ApprovalEnvironment.Create(TurnHarness.Workspace, "/home/user")), harness.Fabric,
            harness.Log, prompter, options: new ApprovalOptions { ShellTools = BuiltinTools.ShellToolMapping });
        var services = harness.Services();
        var runner = new TurnRunner(TurnHarness.Session, new TurnServices
        {
            Log = services.Log,
            Slots = services.Slots,
            Fabric = services.Fabric,
            Tools = services.Tools,
            Approvals = approvals,
            Options = services.Options,
        });

        await harness.RunAsync(runner, "run that command");

        Assert.Empty(sandbox.Runs);
        Assert.Empty(prompter.Prompts);
        Assert.Contains("hardline", Assert.Single(harness.Payloads().OfType<ToolResult>()).Result.GetRawText());
    }

    [Fact]
    public async Task Unattended_turns_deny_what_would_ask()
    {
        var prompter = FakePrompter.Always(approve: true);
        var (harness, sandbox) = Harness(new FakeGenerator(Reply.Call("shell", """{"command":"rm -rf build"}"""), Reply.Say("Could not.")),
            prompter, new TurnOptions { Interactive = false });
        using var _ = harness;

        await harness.RunAsync("clean the build output");

        Assert.Empty(prompter.Prompts);
        Assert.Empty(sandbox.Runs);
        Assert.Contains("nobody could approve it", Assert.Single(harness.Payloads().OfType<ToolResult>()).Result.GetRawText());
    }

    [Fact]
    public async Task Without_the_shell_mapping_the_tool_refuses_even_an_approved_call()
    {
        // Assessed as an opaque tool call, the approval is not a shell approval for the command, so the tool will not run it.
        var prompter = FakePrompter.Always(approve: true);
        var sandbox = new RecordingSandbox();
        var shell = new ShellTool(new SandboxedShell(sandbox, new ShellOptions { Workspace = TurnHarness.Workspace }));
        using var harness = new TurnHarness(new FakeGenerator(Reply.Call("shell", """{"command":"ls"}"""), Reply.Say("Failed.")),
            prompter: prompter, tools: tools => tools.Add(shell));

        await harness.RunAsync("show the files in src with the shell");

        Assert.Single(prompter.Prompts);
        Assert.Empty(sandbox.Runs);
        var result = Assert.Single(harness.Payloads().OfType<ToolResult>());
        Assert.True(result.IsError);
        Assert.Contains("only after the approval pipeline", result.Result.GetRawText());
    }
}
