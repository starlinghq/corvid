using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Corvid.Tools;

/// <summary>
/// Base class of the generated tool wrappers. The generated subclass binds arguments and calls the method directly;
/// nothing here uses reflection.
/// </summary>
public abstract class ToolFunction : AIFunction
{
    /// <summary>Keys under which <see cref="AITool.AdditionalProperties"/> carries <see cref="ToolRisk"/>, for code that sees only an <see cref="AITool"/>.</summary>
    public const string SideEffectsKey = "corvid.side_effects";
    public const string NetworkKey = "corvid.network";
    public const string DestructiveKey = "corvid.destructive";

    private readonly IReadOnlyDictionary<string, object?> _properties;

    protected ToolFunction(ToolDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
        _properties = new Dictionary<string, object?>
        {
            [SideEffectsKey] = descriptor.Risk.SideEffects,
            [NetworkKey] = descriptor.Risk.Network,
            [DestructiveKey] = descriptor.Risk.Destructive,
        };
    }

    public ToolDescriptor Descriptor { get; }

    public override string Name => Descriptor.Name;

    public override string Description => Descriptor.Description;

    public override JsonElement JsonSchema => Descriptor.JsonSchema;

    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _properties;

    /// <summary>Lets wrappers such as <see cref="DelegatingAIFunction"/> surface the descriptor with <c>GetService&lt;ToolDescriptor&gt;()</c>.</summary>
    public override object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        // The function itself comes first, so GetService(typeof(object)) still returns this function.
        return base.GetService(serviceType, serviceKey)
            ?? (serviceKey is null && serviceType.IsInstanceOfType(Descriptor) ? Descriptor : null);
    }
}

/// <summary>Implemented by generated code on a non-static class with <see cref="ToolAttribute"/> methods.</summary>
public interface IToolProvider
{
    /// <summary>One wrapper per tool method, bound to this instance.</summary>
    IReadOnlyList<ToolFunction> CreateTools();
}
