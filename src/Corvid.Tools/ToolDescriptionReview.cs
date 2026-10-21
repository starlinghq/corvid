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
/// Where the turn loop reports a confident <c>injection</c> verdict over tool descriptions (C-033). The source of the tools
/// (the MCP clients) stops offering them until the user reviews them; the kernel only drops them from its own turn. It sits in
/// <c>Corvid.Tools</c> beside <see cref="ToolDescriptor.Origin"/> so the kernel never references a surface project.
/// </summary>
public interface IToolDescriptionReview
{
    /// <summary>Called on the turn's thread; must not throw for a tool that is already gone.</summary>
    void Withhold(ToolWithholding request);
}
