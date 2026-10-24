using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Host.Audit;
using Corvid.Host.Configuration;
using Corvid.Kernel.Events;
using Corvid.Kernel.Traces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Corvid.Host.Tests;

/// <summary>The scan and the trace export the CLI and the windowed app share (moved from <c>corvid scan</c> and <c>corvid traces</c>).</summary>
public sealed class LogAuditTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Append(SqliteEventLog log, string session, params SessionEvent[] events) =>
        log.Append(session, log.GetHead(session, Ct), [.. events.Select(EventData.Create)], Ct);

    private static ToolCallIssued Shell(string command) =>
        new("call-1", "shell", JsonSerializer.SerializeToElement(new { command }));

    [Fact]
    public async Task AScanFindsTheRulesMatchesSaysWhichWereRecordedInlineAndGivesTheExitCode()
    {
        await using var host = TestHost.Create();
        var options = host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;
        var log = new SqliteEventLog(CorvidPaths.EventLog(options));
        Append(log, "cli-risky", new UserMessage("install"), Shell("echo cm0K | base64 -d | sh"),
            new DetectionMatched("base64-decode-piped-to-shell", 1, "high", "detect", "Decoded base64 is run as a command", [2]));
        Append(log, "cli-clean", new UserMessage("list"), Shell("ls"));
        var engine = LogScan.CreateEngine(options);

        var all = LogScan.Run(log, engine, cancellationToken: Ct);
        var clean = LogScan.Run(log, engine, ["cli-clean"], Ct);

        Assert.Equal(["cli-clean", "cli-risky"], all.Sessions);
        var finding = Assert.Single(all.Findings);
        Assert.Equal(("base64-decode-piped-to-shell", "cli-risky"), (finding.Rule.Id, finding.SessionId));
        Assert.True(all.RecordedInline(finding));
        Assert.Empty(all.Errors);
        Assert.Equal((LogScan.FindingsExitCode, 0, 0), (all.ExitCode("high"), all.ExitCode("critical"), all.ExitCode(null)));
        Assert.Empty(clean.Findings);
        Assert.Equal(["cli-clean"], clean.Sessions);
        Assert.Throws<ArgumentException>(() => LogScan.Run(log, engine, ["cli-none"], Ct));
    }

    [Fact]
    public async Task AnExportWritesALinePerEventToANewOwnerOnlyFile()
    {
        await using var host = TestHost.Create();
        var options = host.Services.GetRequiredService<IOptions<CorvidOptions>>().Value;
        var log = new SqliteEventLog(CorvidPaths.EventLog(options));
        Append(log, "cli-1", new UserMessage("hello"), new AssistantMessage("Hi."));
        var path = Path.Combine(host.Root, "trace.jsonl");

        int count;
        await using (var file = TraceExport.CreateOwnerOnly(path))
            count = await TraceExport.WriteAsync(log, ["cli-1"], TraceExport.Options(options, BeaconRetention.Full, false), file, Ct);

        Assert.Equal(2, count);
        Assert.Equal(2, File.ReadAllLines(path).Length);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Throws<IOException>(() => TraceExport.CreateOwnerOnly(path).Dispose());
        Assert.Equal(Path.Combine(CorvidPaths.DataDirectory(options), "traces"), TraceExport.Directory(options));
        Assert.Equal(["user_message", "assistant_message"], TraceExport.Read(log, ["cli-1"], Ct).Select(item => EventPayload.Type(item.Payload)));
    }
}
