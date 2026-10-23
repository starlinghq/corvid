using System.Collections.Concurrent;
using System.Diagnostics;
using Corvid.Approvals;
using Corvid.Kernel;
using Corvid.Kernel.Events;
using Corvid.Kernel.Jobs;
using Corvid.Sandbox;
using Corvid.Vault.Client;
using static Corvid.Tools.Builtin.Tests.Fixtures;

namespace Corvid.Tools.Builtin.Tests;

/// <summary>
/// V-003b's route decision, against a sandbox that records the policy and a stand-in for Vault's sandbox proxy: who gets
/// HTTPS through Vault, who runs without a route, that the proxy is always closed, and that a route that cannot be opened
/// stops the command rather than running it some other way.
/// </summary>
public sealed class ShellEgressTests : IDisposable
{
    private const string Workspace = "/home/user/work/project";
    private const string Pem = "-----BEGIN CERTIFICATE-----\nMIIBfake\n-----END CERTIFICATE-----\n";

    private readonly TempTree _tree = new();
    private readonly SqliteEventLog _log;
    private readonly RecordingSandbox _sandbox = new();
    private readonly FakeEgress _egress = new();
    private readonly ConcurrentQueue<Activity> _spans = new();
    private readonly ActivityListener _listener;
    private readonly Activity _parent = new("shell-egress-test");

    public ShellEgressTests()
    {
        _log = new SqliteEventLog(_tree.Path("events.db"));
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == KernelTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _spans.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(_listener);
        _parent.Start();
    }

    public void Dispose()
    {
        _parent.Stop();
        _listener.Dispose();
        _tree.Dispose();
    }

    private Activity Span() => Assert.Single(_spans, span => span.ParentId == _parent.Id && span.OperationName == ShellTelemetry.EgressSpan);

    private SandboxedShell Shell(bool network = true, ISandboxEgress? egress = null) =>
        new(_sandbox, new ShellOptions { Workspace = Workspace, Network = network, EgressLifetime = TimeSpan.FromMinutes(3) }, egress ?? _egress);

    private ApprovalPipeline Pipeline(bool approve = true) =>
        Fixtures.Pipeline(_log, ApprovalEnvironment.Create(Workspace, "/home/user"), new Answering(approve));

    /// <summary>The approval for a shell command in a turn, tainted or not, the way the turn loop gets it.</summary>
    private async Task<ToolCallContext> ApprovedAsync(string command, bool tainted, ApprovalPipeline? pipeline = null)
    {
        pipeline ??= Pipeline();
        var result = await pipeline.AuthorizeAsync(new ApprovalContext(Session, ApprovalMode.Smart, State()) { CallId = "call-1", Tainted = tainted },
            new ShellAction(command), Ct);
        Assert.True(result.Allowed, result.Assessment.Summary);
        return new ToolCallContext(Session, "call-1") { Approval = result, Tainted = tainted };
    }

    private static async Task<System.Text.Json.Nodes.JsonNode> RunAsync(SandboxedShell shell, string command, ToolCallContext context) =>
        await new ShellTool(shell).RunAsync(command, context, Ct);

    [Fact]
    public async Task With_the_network_off_there_is_no_proxy_no_span_and_no_network_field()
    {
        var context = await ApprovedAsync("curl -s https://api.example.org/", tainted: false);

        var result = await RunAsync(Shell(network: false), "curl -s https://api.example.org/", context);

        Assert.Null(Assert.Single(_sandbox.Runs).Policy.Proxy);
        Assert.False(_sandbox.Runs[0].Policy.Network);
        Assert.Empty(_egress.Opened);
        Assert.Null(result["network"]);
        Assert.False(context.ResultTainted);
        Assert.DoesNotContain(_spans, span => span.ParentId == _parent.Id);
    }

    [Fact]
    public async Task An_untainted_command_gets_the_vault_route_and_the_proxy_is_closed_after()
    {
        var context = await ApprovedAsync("ls", tainted: false);

        var result = await RunAsync(Shell(), "ls", context);

        var (policy, _) = Assert.Single(_sandbox.Runs);
        Assert.Equal(new SandboxProxy("/run/v/sandbox/sb1.sock", Pem), policy.Proxy);
        Assert.False(policy.Network);
        Assert.Equal([(false, true, TimeSpan.FromMinutes(3))], _egress.Opened);
        Assert.Equal(["sb1"], _egress.Closed);
        Assert.Equal("vault", result["network"]!.GetValue<string>());
        // What came back through the proxy is outside content: the turn loop taints the session after this call.
        Assert.True(context.ResultTainted);

        var span = Span();
        Assert.Equal("vault", span.GetTagItem(ShellTelemetry.Route));
        Assert.Equal("sb1", span.GetTagItem(ShellTelemetry.SandboxId));
        Assert.Equal(true, span.GetTagItem(ShellTelemetry.Passthrough));
        Assert.Equal(false, span.GetTagItem(ShellTelemetry.Tainted));
        Assert.Equal(FakeEgress.Expiry.ToString("O", System.Globalization.CultureInfo.InvariantCulture), span.GetTagItem(ShellTelemetry.Expires));
        Assert.Contains(span.Events, e => e.Name == ShellTelemetry.ClosedEvent);
        // Never the CA or the socket path.
        Assert.DoesNotContain(span.TagObjects, tag => tag.Value?.ToString() is { } value && (value.Contains("BEGIN", StringComparison.Ordinal) || value.Contains(".sock", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_tainted_command_nobody_approved_as_egress_runs_without_a_route()
    {
        // Read-only, so it runs without asking; and an opaque interpreter the parser does not mark as network.
        var context = await ApprovedAsync("ls", tainted: true);
        Assert.Equal(ApprovalResolution.Automatic, ((ApprovalResult)context.Approval!).Resolution);

        var result = await RunAsync(Shell(), "ls", context);

        Assert.Null(Assert.Single(_sandbox.Runs).Policy.Proxy);
        Assert.Empty(_egress.Opened);
        Assert.Equal("withheld", result["network"]!.GetValue<string>());
        Assert.False(context.ResultTainted); // reached nothing outside (the turn is tainted already)
        Assert.Equal("withheld", Span().GetTagItem(ShellTelemetry.Route));
        Assert.Equal(true, Span().GetTagItem(ShellTelemetry.Tainted));
    }

    [Fact]
    public async Task A_tainted_command_approved_for_another_reason_runs_without_a_route()
    {
        // Asked about (it writes a file), but nobody was told it reaches the network: python3 is opaque to the parser.
        var context = await ApprovedAsync("python3 fetch.py > out.txt", tainted: true);
        var approval = (ApprovalResult)context.Approval!;
        Assert.Equal(ApprovalResolution.UserApproved, approval.Resolution);
        Assert.DoesNotContain(approval.Assessment.Reasons, reason => reason.Code == ApprovalReasons.TaintedEgress);

        var result = await RunAsync(Shell(), "python3 fetch.py > out.txt", context);

        Assert.Null(Assert.Single(_sandbox.Runs).Policy.Proxy);
        Assert.Equal("withheld", result["network"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_tainted_command_a_person_approved_as_network_egress_gets_the_route_with_the_taint()
    {
        var context = await ApprovedAsync("curl -s https://api.example.org/", tainted: true);
        var approval = (ApprovalResult)context.Approval!;
        Assert.Equal(ApprovalResolution.UserApproved, approval.Resolution);
        Assert.Contains(approval.Assessment.Reasons, reason => reason.Code == ApprovalReasons.TaintedEgress);

        var result = await RunAsync(Shell(), "curl -s https://api.example.org/", context);

        Assert.NotNull(Assert.Single(_sandbox.Runs).Policy.Proxy);
        Assert.Equal([(true, true, TimeSpan.FromMinutes(3))], _egress.Opened); // Vault applies its tainted floor too
        Assert.Equal("vault", result["network"]!.GetValue<string>());
        Assert.True(context.ResultTainted);
        Assert.Equal(true, Span().GetTagItem(ShellTelemetry.Tainted));
    }

    [Fact]
    public async Task Scheduled_scripts_get_the_route_only_when_untainted()
    {
        var pipeline = new ApprovalPipeline(ApprovalEnvironment.Create(Workspace, "/home/user"), DegradedFabric(_log), _log, new AlwaysApproving(),
            options: new ApprovalOptions { ShellTools = BuiltinTools.ShellToolMapping });
        var runner = new SandboxedScriptRunner(Shell(), pipeline);

        var clean = await runner.RunAsync(new JobScriptRequest("job:e:1", "e", "ls", State()), Ct);
        var tainted = await runner.RunAsync(new JobScriptRequest("job:e:2", "e", "ls", State(), Tainted: true), Ct);

        Assert.True(clean.Ran);
        Assert.True(tainted.Ran);
        Assert.NotNull(_sandbox.Runs[0].Policy.Proxy);
        Assert.Null(_sandbox.Runs[1].Policy.Proxy);
        Assert.Single(_egress.Opened);
        Assert.Equal(["sb1"], _egress.Closed);
    }

    [Fact]
    public async Task The_proxy_is_closed_when_the_command_fails_or_is_cancelled()
    {
        var context = await ApprovedAsync("ls", tainted: false);
        var failing = new SandboxedShell(new ThrowingSandbox(new SandboxRefusedException("bubblewrap failed")),
            new ShellOptions { Workspace = Workspace, Network = true }, _egress);
        var cancelling = new SandboxedShell(new ThrowingSandbox(new OperationCanceledException()),
            new ShellOptions { Workspace = Workspace, Network = true }, _egress);

        await Assert.ThrowsAsync<SandboxRefusedException>(() => RunAsync(failing, "ls", context));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunAsync(cancelling, "ls", context));

        Assert.Equal(["sb1", "sb2"], _egress.Closed);
    }

    [Fact]
    public async Task A_failed_close_does_not_fail_the_command()
    {
        var context = await ApprovedAsync("ls", tainted: false);
        _egress.FailClose = true;

        var result = await RunAsync(Shell(), "ls", context);

        Assert.Equal(0, result["exit_code"]!.GetValue<int>());
        Assert.Equal(false, Assert.Single(Span().Events, e => e.Name == ShellTelemetry.ClosedEvent).Tags.Single(t => t.Key == "closed").Value);
    }

    [Fact]
    public async Task When_vault_cannot_open_the_route_nothing_runs_and_nothing_falls_back()
    {
        var context = await ApprovedAsync("ls", tainted: false);
        _egress.OpenFailure = new VaultException("sandbox_limit", "too many sandboxes");

        var e = await Assert.ThrowsAsync<SandboxRefusedException>(() => RunAsync(Shell(), "ls", context));

        Assert.Contains("did not run", e.Message, StringComparison.Ordinal);
        Assert.Contains("sandbox_limit", e.Message, StringComparison.Ordinal);
        Assert.Empty(_sandbox.Runs);
        Assert.Equal("sandbox_limit", Span().GetTagItem(ShellTelemetry.Error));
        Assert.Equal(ActivityStatusCode.Error, Span().Status);

        // No Vault configured at all: the same refusal.
        var none = new SandboxedShell(_sandbox, new ShellOptions { Workspace = Workspace, Network = true });
        await Assert.ThrowsAsync<SandboxRefusedException>(() => RunAsync(none, "ls", context));
        Assert.Empty(_sandbox.Runs);

        // The scheduler reports it as a refusal and runs nothing.
        var runner = new SandboxedScriptRunner(Shell(), Pipeline());
        var script = await runner.RunAsync(new JobScriptRequest("job:f:1", "f", "ls", State()), Ct);
        Assert.False(script.Ran);
        Assert.Contains("sandbox_limit", script.Refusal, StringComparison.Ordinal);
        Assert.Empty(_sandbox.Runs);
    }

    [Fact]
    public async Task Skill_scripts_and_the_plain_overload_stay_offline()
    {
        await Shell().RunAsync("ls", Ct);

        Assert.Null(Assert.Single(_sandbox.Runs).Policy.Proxy);
        Assert.Empty(_egress.Opened);
    }

    [Fact]
    public async Task An_untainted_turn_that_used_the_route_is_tainted_for_the_rest_of_the_session()
    {
        // The shell is not [Network], so the turn loop taints only on the result's say-so; a later command in the session
        // is then tainted, and gets the route only with a person's approval as network egress.
        var first = await ApprovedAsync("ls", tainted: false);
        await RunAsync(Shell(), "ls", first);
        Assert.True(first.ResultTainted);

        var next = await ApprovedAsync("ls", tainted: first.ResultTainted);
        var result = await RunAsync(Shell(), "ls", next);

        Assert.Equal("withheld", result["network"]!.GetValue<string>());
        Assert.Single(_egress.Opened);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task The_sandbox_socket_directory_exists_before_any_command_runs()
    {
        // Vault creates it at its first sandbox_open and the launcher skips a denied path that does not exist, so a command
        // started earlier would see a later sandbox's socket appear. The shell creates it, 0700, before every command.
        var vault = new VaultLocations(_tree.Path("vault-data"), _tree.Path("vault.json"), _tree.Path("run", "vault.sock"));
        var seen = new List<bool>();
        var sandbox = new RecordingSandbox(_ =>
        {
            seen.Add(Directory.Exists(vault.SandboxDirectory));
            return new SandboxResult { ExitCode = 0, StandardOutput = "", StandardError = "" };
        });
        foreach (var network in new[] { false, true })
        {
            var shell = new SandboxedShell(sandbox, new ShellOptions { Workspace = Workspace, Network = network, Vault = vault }, _egress);
            await shell.RunAsync("ls", Ct);
            Directory.Delete(vault.SandboxDirectory!);
            await RunAsync(shell, "ls", await ApprovedAsync("ls", tainted: true)); // withheld when the network is on
            Directory.Delete(vault.SandboxDirectory!);
        }

        Assert.Equal([true, true, true, true], seen);
        Assert.All(sandbox.Runs, run => Assert.Contains(vault.SandboxDirectory!, run.Policy.DeniedPaths));
        vault.EnsureSandboxDirectory();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(vault.SandboxDirectory!));

        // Where this user cannot create it, Vault (the same user) cannot either: nothing to do, and nothing thrown.
        new VaultLocations("/srv/vault", "/etc/vault.json", "/proc/corvid-vault/vault.sock").EnsureSandboxDirectory();
        Assert.False(Directory.Exists("/proc/corvid-vault"));
    }

    [Fact]
    public void The_sandbox_socket_directory_is_denied_and_on_the_hardline_list()
    {
        var vault = new VaultLocations("/srv/vault", "/etc/vault.json", "/srv/run/vault.sock");

        Assert.Equal("/srv/run/sandbox", vault.SandboxDirectory);
        Assert.Contains("/srv/run/sandbox", new SandboxedShell(_sandbox, new ShellOptions { Workspace = Workspace, Vault = vault }).Policy.DeniedPaths);
        Assert.Contains("/srv/run/sandbox", vault.Protect(ApprovalEnvironment.Create(Workspace, "/home/user")).Protected.Vault);
        Assert.Null(new VaultLocations("/srv/vault", "/etc/vault.json").SandboxDirectory);
    }

    private sealed class FakeEgress : ISandboxEgress
    {
        public static readonly DateTimeOffset Expiry = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

        private int _next;

        public List<(bool Tainted, bool Network, TimeSpan Lifetime)> Opened { get; } = [];

        public List<string> Closed { get; } = [];

        public Exception? OpenFailure { get; set; }

        public bool FailClose { get; set; }

        public Task<SandboxEgressLease> OpenAsync(bool tainted, bool network, TimeSpan lifetime, CancellationToken cancellationToken)
        {
            if (OpenFailure is { } failure)
                return Task.FromException<SandboxEgressLease>(failure);
            Opened.Add((tainted, network, lifetime));
            var id = "sb" + ++_next;
            return Task.FromResult(new SandboxEgressLease(id, $"/run/v/sandbox/{id}.sock", Pem, Expiry, () =>
            {
                Closed.Add(id);
                return FailClose ? Task.FromException(new VaultException("unknown_sandbox", "gone")) : Task.CompletedTask;
            }));
        }
    }

    private sealed class ThrowingSandbox(Exception exception) : ISandbox
    {
        public Task<SandboxResult> RunAsync(SandboxPolicy policy, SandboxCommand command, CancellationToken cancellationToken = default) =>
            Task.FromException<SandboxResult>(exception);
    }
}
