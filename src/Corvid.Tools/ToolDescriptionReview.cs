using System.Collections.Immutable;

namespace Corvid.Tools;

/// <summary>
/// C-033: tools whose descriptions the decision fabric judged, with a calibrated answer at or above its floor, to try to
/// instruct the agent. <paramref name="Origin"/> is the tools' <see cref="ToolDescriptor.Origin"/> (<c>mcp:&lt;server&gt;</c>),
/// <paramref name="Tools"/> their registered names, exactly those whose descriptions were in the question, and
/// <paramref name="DecisionId"/> the logged <c>Decision</c> that judged them.
/// </summary>
public sealed record ToolWithholding(string Origin, ImmutableArray<string> Tools, string DecisionId);

/// <summary>
/// Where a confident verdict over tool descriptions is reported (C-033), so the source of the tools (the MCP clients) stops
/// offering them until the user reviews them. C-037: the turn loop no longer reports the generic <c>injection</c> verdict
/// here (spike S2: it answers yes on benign descriptions); the seam stays for a dedicated <c>description_injection</c>
/// question with its own fitted floor, which would decide before the turn reads its tools. It sits in <c>Corvid.Tools</c>
/// beside <see cref="ToolDescriptor.Origin"/> so the kernel never references a surface project.
/// </summary>
public interface IToolDescriptionReview
{
    /// <summary>Called on the turn's thread; must not throw for a tool that is already gone.</summary>
    void Withhold(ToolWithholding request);
}
