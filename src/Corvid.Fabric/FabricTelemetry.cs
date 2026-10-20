using System.Diagnostics;

namespace Corvid.Fabric;

/// <summary>
/// The fabric's OpenTelemetry source (plan §16): one <c>corvid.decide</c> span per checkpoint, with the question ids,
/// confidences and actions as attributes and one event per question. System.Diagnostics only, so the fabric takes no
/// package; the host subscribes.
/// </summary>
public static class FabricTelemetry
{
    public const string SourceName = "Corvid.Fabric";

    public const string DecideSpan = "corvid.decide";

    public static ActivitySource Source { get; } = new(SourceName);
}
