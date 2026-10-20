using System.Diagnostics;
using Corvid.Fabric;
using Corvid.Kernel;
using Corvid.Models;

namespace Corvid.Host.Telemetry;

/// <summary>
/// The span tree of plan §16: <c>invoke_agent corvid</c> (kernel) → <c>chat {model}</c> (host, around every model call) →
/// <c>execute_tool {tool}</c> (kernel), plus <c>corvid.decide</c> (fabric) per checkpoint. Exported over OTLP when an
/// endpoint is configured.
/// </summary>
public static class HostTelemetry
{
    public const string SourceName = "Corvid.Host";

    public static ActivitySource Source { get; } = new(SourceName);

    /// <summary>Every source Corvid emits spans from.</summary>
    /// <remarks>
    /// Built from constants on each read, not stored: creating <see cref="Source"/> calls every listener's
    /// <c>ShouldListenTo</c> inside this class's static constructor, and a listener that reads this list there would
    /// otherwise see it unassigned.
    /// </remarks>
    public static IReadOnlyList<string> Sources => [KernelTelemetry.SourceName, FabricTelemetry.SourceName, SourceName];

    /// <summary>A GenAI <c>chat {model}</c> client span for one model call.</summary>
    internal static Activity? StartChat(ModelProfile profile, string slot) =>
        Source.StartActivity(ActivityKind.Client, tags:
        [
            new(KernelTelemetry.OperationName, "chat"),
            new("gen_ai.request.model", profile.Id),
            new("corvid.slot", slot),
            new("corvid.placement", profile.Placement),
        ], name: "chat " + profile.Id);

    /// <summary>The session a span belongs to: the nearest ancestor that names a conversation.</summary>
    public static string? SessionOf(Activity? activity)
    {
        for (var current = activity; current is not null; current = current.Parent)
            if (current.GetTagItem(KernelTelemetry.ConversationId) is string session)
                return session;
        return null;
    }
}
