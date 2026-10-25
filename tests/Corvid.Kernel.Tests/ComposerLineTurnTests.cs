using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Sessions;
using Corvid.Approvals;
using Corvid.Kernel.Events;
using Corvid.Kernel.Turns;
using Corvid.Sandbox;
using Corvid.Tools.Builtin;

namespace Corvid.Kernel.Tests;

/// <summary>
/// The composer's <c>!</c> and <c>!!</c> lines (A4, docs/plan/tui.md §4.1): the command runs as the user's own <c>shell</c>
/// call through the approval pipeline and the sandbox, with no generator call; <c>!!</c> keeps the line and its output out
/// of every later prompt while the log keeps them.
/// </summary>
public sealed class ComposerLineTurnTests
{
    private static readonly VaultLocations Vault = new("/srv/vault/data", "/etc/corvid-vault/policy.json", "/run/vault/vault.sock");

    private static (TurnHarness Harness, RecordingSandbox Sandbox) Harness(FakeGenerator generator, FakePrompter? prompter = null)
    {
        var sandbox = new RecordingSandbox();
        var shell = new ShellTool(new SandboxedShell(sandbox, new ShellOptions { Workspace = TurnHarness.Workspace, Vault = Vault }));
        var harness = new TurnHarness(generator, prompter: prompter,
            approvalOptions: new ApprovalOptions { ShellTools = BuiltinTools.ShellToolMapping }, tools: tools => tools.Add(shell));
        return (harness, sandbox);
    }

    private static Task<TurnResult> Line(TurnHarness harness, TurnRunner runner, string line, EnvelopeOrigin origin = EnvelopeOrigin.User)
    {
        var local = line.StartsWith("!!", StringComparison.Ordinal);
        return runner.RunAsync(new Envelope(TurnHarness.Session, "cli", line, origin)
        {
            Shell = new ShellLine(line[(local ? 2 : 1)..].Trim(), local),
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task A_shell_line_runs_its_command_sandboxed_asks_the_generator_nothing_and_the_next_turn_sees_the_output()
    {
        var prompter = FakePrompter.Always(approve: false);
        var (harness, sandbox) = Harness(new FakeGenerator(Reply.Say("The listing shows src.")), prompter);
        using var _ = harness;
        var runner = harness.Runner();

        var result = await Line(harness, runner, "!ls -la src");

        Assert.Equal(TurnStatus.Answered, result.Status);
        Assert.Null(result.Text);
        Assert.Equal(1, result.ToolCalls);
        // A read-only command: the pipeline allows it without asking, as for a model's call.
        Assert.Empty(prompter.Prompts);
        Assert.Empty(harness.Generator.Calls);
        Assert.Equal(["-c", "ls -la src"], Assert.Single(sandbox.Runs).Command.Arguments);
        var payloads = harness.Payloads();
        Assert.Equal("!ls -la src", Assert.Single(payloads.OfType<UserMessage>()).Text);
        var call = Assert.Single(payloads.OfType<ToolCallIssued>());
        Assert.Equal(("user-shell", "shell", "ls -la src", (bool?)null), (call.CallId, call.ToolName, call.Arguments.GetProperty("command").GetString(), call.LocalOnly));
        Assert.Empty(payloads.OfType<AssistantMessage>());

        await harness.RunAsync(runner, "what is in src?");

        Assert.Contains("ran: ls -la src", Assert.Single(harness.Generator.Calls).AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shell_line_that_changes_things_asks_first_and_a_denial_runs_nothing()
    {
        var prompter = FakePrompter.Always(approve: false);
        var (harness, sandbox) = Harness(new FakeGenerator(), prompter);
        using var _ = harness;

        await Line(harness, harness.Runner(), "!rm -rf build");

        Assert.Equal("rm", Assert.Single(Assert.Single(prompter.Prompts).Analysis.Commands).Verb);
        Assert.Empty(sandbox.Runs);
        Assert.Contains("denied by the user", Assert.Single(harness.Payloads().OfType<ToolResult>()).Result.GetRawText());
        Assert.Empty(harness.Generator.Calls);
    }

    [Fact]
    public async Task A_double_bang_line_is_logged_local_only_and_no_later_prompt_holds_it()
    {
        var (harness, sandbox) = Harness(new FakeGenerator(Reply.Say("Hello.")), FakePrompter.Always(approve: true));
        using var _ = harness;
        var runner = harness.Runner();

        await Line(harness, runner, "!!cat notes/private.txt");
        await harness.RunAsync(runner, "list my notes");

        Assert.Single(sandbox.Runs);
        var payloads = harness.Payloads();
        Assert.True(payloads.OfType<UserMessage>().First().LocalOnly);
        Assert.True(Assert.Single(payloads.OfType<ToolCallIssued>()).LocalOnly);
        Assert.True(Assert.Single(payloads.OfType<ToolResult>()).LocalOnly);
        var prompt = harness.Generator.Calls[^1].AllText;
        Assert.DoesNotContain("private.txt", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("ran: ", prompt, StringComparison.Ordinal);
        Assert.Contains("list my notes", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_shell_line_from_anyone_but_the_user_is_refused_unrun()
    {
        var prompter = FakePrompter.Always(approve: true);
        var (harness, sandbox) = Harness(new FakeGenerator(), prompter);
        using var _ = harness;

        var result = await Line(harness, harness.Runner(), "!ls", EnvelopeOrigin.External);

        Assert.Equal(TurnStatus.Failed, result.Status);
        Assert.Empty(sandbox.Runs);
        Assert.Empty(prompter.Prompts);
        Assert.Empty(harness.Payloads());
    }

    [Fact]
    public async Task A_shell_lines_turn_takes_no_steers_so_one_sent_while_its_command_waits_is_answered_at_once()
    {
        TurnRunner? runner = null;
        Task<bool>? steer = null;
        bool? takes = null;
        var prompter = new FakePrompter((_, _) =>
        {
            // M2: no step checkpoint follows the user's command; a steer is handed back now, to be sent as a message.
            takes = runner!.TakesSteers;
            steer = runner.SteerAsync("and then build");
            return Task.FromResult(ApprovalAnswer.Approve());
        });
        var (harness, _) = Harness(new FakeGenerator(), prompter);
        using var __ = harness;
        runner = harness.Runner();

        await Line(harness, runner, "!rm -rf build");

        Assert.False(takes);
        Assert.True(steer!.IsCompleted);
        Assert.False(await steer);
        Assert.Empty(harness.Payloads().OfType<UserSteered>());
    }

    [Fact]
    public async Task A_models_call_never_keeps_the_id_reserved_for_the_users_own_shell_line()
    {
        var generator = new FakeGenerator(Reply.Call("shell", """{"command":"ls"}""", callId: "user-shell"),
            Reply.Call("shell", """{"command":"ls src"}""", callId: "USER-SHELL-2"), Reply.Say("Listed."));
        var (harness, _) = Harness(generator, FakePrompter.Always(approve: true));
        using var __ = harness;

        await harness.RunAsync(harness.Runner(), "list the files with the shell");

        var ids = harness.Payloads().OfType<ToolCallIssued>().Select(call => call.CallId).ToList();
        Assert.NotEmpty(ids);
        // Security L1: surfaces read an id starting user-shell as the person's own ! line.
        Assert.All(ids, id => Assert.False(id.StartsWith(TurnRunner.UserShellCall, StringComparison.OrdinalIgnoreCase), id));
        Assert.Equal("call-user-shell", ids[0]);
    }

    [Fact]
    public async Task Call_ids_stay_unique_across_shell_lines()
    {
        var (harness, _) = Harness(new FakeGenerator(), FakePrompter.Always(approve: true));
        using var __ = harness;
        var runner = harness.Runner();

        await Line(harness, runner, "!ls");
        await Line(harness, runner, "!ls src");

        Assert.Equal(["user-shell", "user-shell-2"], harness.Payloads().OfType<ToolCallIssued>().Select(call => call.CallId));
    }
}

/// <summary>G-6: titles and recaps are session metadata in the log, never transcript.</summary>
public sealed class SessionTitleProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-titles-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void The_latest_title_and_recap_are_on_the_snapshot_and_out_of_the_transcript()
    {
        var log = new SqliteEventLog(Path.Combine(_root, "events.db"));
        log.Append("s", 0, [
            EventData.Create(new UserMessage("fix the flaky test")),
            EventData.Create(new AssistantMessage("Fixed.")),
            EventData.Create(new SessionTitled("Fix the flaky test", TitleSources.Generated)),
            EventData.Create(new SessionTitled("Flaky sandbox test", TitleSources.User)),
            EventData.Create(new SessionRecapped("You fixed the flaky test.", 0, 2, RecapSources.Requested)),
        ], TestContext.Current.CancellationToken);

        var snapshot = SessionProjection.Load(log, "s", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(("Flaky sandbox test", TitleSources.User), (snapshot.Title!.Title, snapshot.Title.Source));
        Assert.Equal("You fixed the flaky test.", snapshot.Recap!.Text);
        Assert.Equal(2, snapshot.Transcript.Length);
        // The prompt has only the exchange.
        var prompt = PromptRenderer.Render(snapshot, "system", Corvid.Fabric.CharsPerTokenCounter.Instance, int.MaxValue);
        Assert.DoesNotContain(prompt.Messages, message => message.Text.Contains("Flaky sandbox", StringComparison.Ordinal)
            || message.Text.Contains("You fixed", StringComparison.Ordinal));
    }

    [Fact]
    public void A_title_and_a_recap_round_trip_and_are_scrubbed_of_credentials()
    {
        const string token = "ghp_A1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r8";
        var log = new SqliteEventLog(Path.Combine(_root, "events.db"));
        log.Append("s", 0, [
            EventData.Create(new SessionTitled($"Use {token}", TitleSources.Generated)),
            EventData.Create(new SessionRecapped($"The token {token} was set.", 0, 1, RecapSources.Absence)),
        ], TestContext.Current.CancellationToken);

        var stored = log.Read("s", cancellationToken: TestContext.Current.CancellationToken);

        var title = Assert.IsType<SessionTitled>(stored[0].Payload);
        var recap = Assert.IsType<SessionRecapped>(stored[1].Payload);
        Assert.DoesNotContain(token, title.Title, StringComparison.Ordinal);
        Assert.DoesNotContain(token, recap.Text, StringComparison.Ordinal);
        Assert.Equal((TitleSources.Generated, RecapSources.Absence, 1L), (title.Source, recap.Source, recap.ThroughSequence));
        Assert.Equal("session_titled", EventPayload.Type(title));
    }
}
