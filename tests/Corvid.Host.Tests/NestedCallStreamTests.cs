using System.Diagnostics;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Host.Sessions;
using Corvid.Host.Telemetry;
using Corvid.Kernel;

namespace Corvid.Host.Tests;

/// <summary>A7: a nested call (code mode's script calling a tool) reaches surfaces with its parent, live and on replay.</summary>
public sealed class NestedCallStreamTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static readonly SessionEvent[] Logged =
    [
        new UserMessage("run the script"),
        new ToolCallIssued("c1", "run_script", Json("{}"), 1),
        new ToolCallIssued("c1.1", "read_file", Json("""{"path":"notes.txt"}"""), 1, true, ParentCallId: "c1"),
        new ToolResult("c1.1", Json("""{"content":"hello"}""")),
        new ToolResult("c1", Json("""{"stdout":"hello"}""")),
    ];

    [Fact]
    public async Task A_replay_carries_each_nested_calls_parent()
    {
        await using var host = TestHost.Create();
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        runtime.Log.Append(session.Id, runtime.Log.GetHead(session.Id, Ct), [.. Logged.Select(EventData.Create)], Ct);

        var replay = await SessionReplay.ReadAsync(runtime, session.Id, new ToolPresentation(runtime), cancellationToken: Ct);

        var started = replay.Updates.OfType<ToolCallStarted>().ToDictionary(update => update.CallId);
        Assert.Null(started["c1"].ParentCallId);
        Assert.Equal("c1", started["c1.1"].ParentCallId);
        var changed = replay.Updates.OfType<ToolCallChanged>().ToDictionary(update => update.CallId);
        Assert.Null(changed["c1"].ParentCallId);
        Assert.Equal("c1", changed["c1.1"].ParentCallId);
    }

    [Fact]
    public async Task The_live_stream_carries_the_parent_from_the_span_and_the_log()
    {
        await using var host = TestHost.Create();
        var runtime = await host.StartAsync();
        await using var _ = runtime;
        var session = runtime.OpenSession(surface: "test");
        var head = runtime.Log.GetHead(session.Id, Ct);
        var live = new List<SessionStreamUpdate>();
        var reporter = new SessionUpdateReporter(runtime, session.Id, new ToolPresentation(runtime), head, update =>
        {
            lock (live)
                live.Add(update);
        });

        using (runtime.Feed.Subscribe(session.Id, reporter))
        {
            // The kernel's span for a nested call, as TurnRunner starts it: the feed reads the parent from its tag.
            using (var span = KernelTelemetry.Source.StartActivity(ActivityKind.Internal, tags:
                   [
                       new(KernelTelemetry.OperationName, "execute_tool"),
                       new(KernelTelemetry.ToolName, "read_file"),
                       new(KernelTelemetry.ToolCallId, "c1.1"),
                       new(KernelTelemetry.ConversationId, session.Id),
                       new(KernelTelemetry.ToolApproval, false),
                       new(KernelTelemetry.ToolParentCallId, "c1"),
                   ], name: KernelTelemetry.ExecuteToolPrefix + "read_file"))
            {
                Assert.NotNull(span);
                runtime.Log.Append(session.Id, runtime.Log.GetHead(session.Id, Ct), [.. Logged.Select(EventData.Create)], Ct);
                span.SetTag(KernelTelemetry.ToolOutcome, "succeeded");
            }
            await reporter.FinishAsync(null);
        }

        var started = Assert.Single(live.OfType<ToolCallStarted>(), update => update.CallId == "c1.1");
        Assert.Equal("c1", started.ParentCallId);
        var changed = Assert.Single(live.OfType<ToolCallChanged>(), update => update.CallId == "c1.1");
        Assert.Equal("c1", changed.ParentCallId);
    }

    [Fact]
    public void An_observer_that_knows_only_the_older_overload_still_hears_a_nested_call()
    {
        var observer = new OlderObserver();
        ((ITurnObserver)observer).ToolStarted("c1.1", "read_file", approval: true, parentCallId: "c1");
        Assert.Equal(("c1.1", "read_file", true), observer.Started);
    }

    private sealed class OlderObserver : ITurnObserver
    {
        public (string, string, bool) Started { get; private set; }

        public void ToolStarted(string callId, string tool, bool approval) => Started = (callId, tool, approval);
    }
}
