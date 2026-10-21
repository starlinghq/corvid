using System.Diagnostics;

namespace Corvid.Kernel;

/// <summary>
/// The kernel's OpenTelemetry source (plan §16), named after the GenAI semantic conventions: <c>invoke_agent corvid</c> for a
/// turn and <c>execute_tool {tool}</c> for each call. The host adds <c>chat {model}</c> around model calls, and the fabric
/// <c>corvid.decide</c>. System.Diagnostics only, so the kernel takes no package; the host subscribes and exports.
/// </summary>
public static class KernelTelemetry
{
    public const string SourceName = "Corvid.Kernel";

    public const string AgentName = "corvid";

    public const string InvokeAgentSpan = "invoke_agent " + AgentName;

    public const string ExecuteToolPrefix = "execute_tool ";

    /// <summary>One compaction of a range (C-016), around its summarizer calls. Surfaces do not stream text under it.</summary>
    public const string CompactSpan = "corvid.compact";

    // GenAI semantic-convention attribute names (https://opentelemetry.io/docs/specs/semconv/gen-ai/).
    public const string OperationName = "gen_ai.operation.name";
    public const string ConversationId = "gen_ai.conversation.id";
    public const string AgentNameTag = "gen_ai.agent.name";
    public const string ToolName = "gen_ai.tool.name";
    public const string ToolCallId = "gen_ai.tool.call.id";
    public const string ToolType = "gen_ai.tool.type";

    /// <summary><c>succeeded</c>, <c>failed</c>, <c>invalid_arguments</c> or <c>denied</c>.</summary>
    public const string ToolOutcome = "corvid.tool.outcome";

    /// <summary>Set on <c>execute_tool</c> when the call went through the approval pipeline.</summary>
    public const string ToolApproval = "corvid.tool.approval";

    /// <summary>Set on <c>execute_tool</c> when the call completed detection rule matches (C-023): their ids.</summary>
    public const string DetectionRules = "corvid.detection.rules";

    public static ActivitySource Source { get; } = new(SourceName);
}
