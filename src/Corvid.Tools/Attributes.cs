namespace Corvid.Tools;

/// <summary>
/// Marks a method as a tool. Corvid.Tools.Generators emits an <see cref="ToolFunction"/> for it at compile time, so the
/// containing type must be a top-level <c>partial</c> class. Parameter descriptions come from
/// <see cref="System.ComponentModel.DescriptionAttribute"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class ToolAttribute : Attribute
{
    /// <summary>Names the tool after the method in snake_case, without an <c>Async</c> suffix.</summary>
    public ToolAttribute()
    {
    }

    /// <param name="name">The name the model calls: 1 to 64 letters, digits, '_' or '-'.</param>
    public ToolAttribute(string name)
    {
        Name = name;
    }

    public string? Name { get; }

    /// <summary>What the tool does, for the model. Falls back to a <see cref="System.ComponentModel.DescriptionAttribute"/> on the method.</summary>
    public string? Description { get; set; }
}

/// <summary>State outside the process that a tool can change. A tool with none is read-only (§3.3: may run in parallel).</summary>
[Flags]
public enum SideEffects
{
    None = 0,
    Filesystem = 1,
    Process = 2,
    Messaging = 4,
    Schedule = 8,
    Memory = 16,
    Configuration = 32,

    /// <summary>State held by another service, such as an issue tracker or a calendar.</summary>
    Remote = 64,
}

/// <summary>Declares what a tool can change (§10.1: every side-effecting tool goes through approval). Repeatable.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = false)]
public sealed class SideEffectAttribute(SideEffects effects) : Attribute
{
    public SideEffects Effects { get; } = effects;
}

/// <summary>The tool sends or receives data over the network, so it is subject to egress and taint policy (§10.3).</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class NetworkAttribute : Attribute;

/// <summary>The tool's side effects can lose data or are hard to reverse. Requires a <see cref="SideEffectAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class DestructiveAttribute : Attribute;
