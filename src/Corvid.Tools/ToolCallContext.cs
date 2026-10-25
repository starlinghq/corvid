using Microsoft.Extensions.AI;

namespace Corvid.Tools;

/// <summary>
/// What the turn loop knows about one tool call that its arguments do not carry: the session, the call, taint and the
/// approval that let it run. The kernel puts it in <see cref="AIFunctionArguments.Context"/>; a <c>[Tool]</c> method gets
/// it by declaring a parameter of this type, which the generator binds from there and leaves out of the schema.
/// </summary>
/// <param name="SessionId">The session the call belongs to.</param>
/// <param name="CallId">The call's id in the event log.</param>
public sealed record ToolCallContext(string SessionId, string CallId)
{
    public string SessionId { get; } = !string.IsNullOrWhiteSpace(SessionId) ? SessionId : throw new ArgumentException("A tool call needs a session.", nameof(SessionId));

    public string CallId { get; } = !string.IsNullOrWhiteSpace(CallId) ? CallId : throw new ArgumentException("A tool call needs an id.", nameof(CallId));

    /// <summary>The session held content from outside the user when the call was made (plan §10.3).</summary>
    public bool Tainted { get; init; }

    /// <summary>False for scheduled and background work, where nobody can be asked.</summary>
    public bool Interactive { get; init; } = true;

    /// <summary>
    /// The approval pipeline's result for this call (a <c>Corvid.Approvals.ApprovalResult</c>), or null when the call needed
    /// none. Typed as object because Corvid.Approvals references this library.
    /// </summary>
    public object? Approval { get; init; }

    /// <summary>
    /// K-2: where the tool writes output as it runs (a command's lines, a fetch's progress), for a surface to show as the
    /// call's live tail. Null when nobody is watching. Untrusted text; it never reaches the model, which reads the result.
    /// Safe to call from any thread, and it never throws.
    /// </summary>
    public Action<string>? Output { get; init; }

    /// <summary>
    /// A5: the tools the calling turn may be offered, after a guest's scope and the turn's own filters, by name; null outside
    /// a turn. A tool that hands work on (<c>delegate_task</c>) never grants more than this.
    /// </summary>
    public IReadOnlySet<string>? OfferedTools { get; init; }

    private int _resultTainted;

    /// <summary>
    /// The tool returned content from outside the user (a hub skill's resource, say) without being a <c>[Network]</c> tool.
    /// The turn loop taints the session after the call when it is set (plan §10.3).
    /// </summary>
    public bool ResultTainted => Volatile.Read(ref _resultTainted) != 0;

    /// <summary>Says this call's result is outside content; see <see cref="ResultTainted"/>.</summary>
    public void MarkResultTainted() => Interlocked.Exchange(ref _resultTainted, 1);

    /// <summary>The context the turn loop attached, or null when the call was made outside a turn.</summary>
    public static ToolCallContext? From(AIFunctionArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Context is { } context && context.TryGetValue(typeof(ToolCallContext), out object? value)
            ? value as ToolCallContext
            : null;
    }

    /// <summary>The attached context; a tool that needs one refuses to run without it rather than guess a session.</summary>
    public static ToolCallContext Require(AIFunctionArguments arguments, string tool) =>
        From(arguments) ?? throw new InvalidOperationException($"'{tool}' runs only inside a turn, and this call carries no ToolCallContext.");

    /// <summary>Attaches this context to <paramref name="arguments"/> for the call.</summary>
    public AIFunctionArguments AttachTo(AIFunctionArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        arguments.Context ??= new Dictionary<object, object?>();
        arguments.Context[typeof(ToolCallContext)] = this;
        return arguments;
    }
}
