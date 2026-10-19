using System.Collections.Concurrent;
using System.Diagnostics;
using Corvid.Fabric;
using Corvid.Host.Telemetry;
using Corvid.Kernel;

namespace Corvid.Host.Tests;

public sealed class TelemetryTests
{
    [Fact]
    public async Task ATurnProducesTheGenAiSpanTree()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("list_notes"), Reply.Say("You have two notes.")));
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        // Listeners are process-wide and tests run in parallel: keep only this session's spans.
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => HostTelemetry.Sources.Contains(source.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (HostTelemetry.SessionOf(activity) == session.Id)
                    spans.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);

        await session.SendAsync("list my notes", TestContext.Current.CancellationToken);

        var turn = Assert.Single(spans, span => span.DisplayName == KernelTelemetry.InvokeAgentSpan);
        Assert.Equal("invoke_agent", turn.GetTagItem("gen_ai.operation.name"));
        Assert.Equal("corvid", turn.GetTagItem("gen_ai.agent.name"));
        Assert.Equal("answered", turn.GetTagItem("corvid.turn.status"));

        // chat {model} under the turn, with GenAI usage attributes.
        var chats = spans.Where(span => span.DisplayName == "chat fake-gen@cpu" && span.Parent == turn).ToList();
        Assert.Equal(2, chats.Count);
        Assert.All(chats, chat =>
        {
            Assert.Equal(ActivityKind.Client, chat.Kind);
            Assert.Equal("chat", chat.GetTagItem("gen_ai.operation.name"));
            Assert.Equal("fake-gen@cpu", chat.GetTagItem("gen_ai.request.model"));
            Assert.Equal(42L, chat.GetTagItem("gen_ai.usage.input_tokens"));
            Assert.Equal("generator", chat.GetTagItem("corvid.slot"));
        });

        // execute_tool {tool} under the turn.
        var tool = Assert.Single(spans, span => span.DisplayName == "execute_tool list_notes");
        Assert.Same(turn, tool.Parent);
        Assert.Equal("list_notes", tool.GetTagItem("gen_ai.tool.name"));
        Assert.Equal("succeeded", tool.GetTagItem(KernelTelemetry.ToolOutcome));

        // corvid.decide per checkpoint, with question ids and confidences; the degraded decider's own yes/no model calls
        // are chat spans beneath it.
        var decides = spans.Where(span => span.DisplayName == FabricTelemetry.DecideSpan).ToList();
        Assert.All(decides, decide => Assert.Same(turn, decide.Parent));
        var ingress = Assert.Single(decides, decide => decide.GetTagItem("corvid.checkpoint") as string == "ingress");
        var questions = Assert.IsType<string[]>(ingress.GetTagItem("corvid.decide.questions"));
        var confidences = Assert.IsType<double[]>(ingress.GetTagItem("corvid.decide.confidences"));
        Assert.Contains("intent", questions);
        Assert.Contains("tool", questions);
        Assert.Equal(questions.Length, confidences.Length);
        Assert.Equal("degraded", ingress.GetTagItem("corvid.decider"));
        Assert.Equal(questions.Length, ingress.Events.Count(item => item.Name == "corvid.decision"));
        Assert.Contains(decides, decide => decide.GetTagItem("corvid.checkpoint") as string == "step");
        Assert.Contains(spans, span => span.DisplayName == "chat fake-gen@cpu" && span.GetTagItem("corvid.slot") as string == "decider"
            && span.Parent?.DisplayName == FabricTelemetry.DecideSpan);
    }

    [Fact]
    public async Task ARejectedCallStillGetsAToolSpan()
    {
        var models = new FakeModels(new FakeGenerator(Reply.Call("no_such_tool"), Reply.Say("Sorry.")));
        await using var host = TestHost.Create(models);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        var spans = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == KernelTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (HostTelemetry.SessionOf(activity) == session.Id)
                    spans.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);

        await session.SendAsync("list my notes", TestContext.Current.CancellationToken);

        var tool = Assert.Single(spans, span => span.DisplayName == "execute_tool no_such_tool");
        Assert.Equal("invalid_arguments", tool.GetTagItem(KernelTelemetry.ToolOutcome));
        Assert.Equal(ActivityStatusCode.Error, tool.Status);
    }
}
