using Microsoft.Extensions.AI;

namespace Corvid.Models;

/// <summary>The per-role slots of plan §4.1. Names are persisted in <c>ModelSwitched</c> events.</summary>
public static class ModelSlots
{
    public const string Generator = "generator";
    public const string Summarizer = "summarizer";
    public const string Decider = "decider";
    public const string Extractor = "extractor";
    public const string TextEmbedder = "embedder.text";
    public const string MediaEmbedder = "embedder.media";
    public const string Vision = "vision";

    public static IReadOnlyList<string> All { get; } =
        [Generator, Summarizer, Decider, Extractor, TextEmbedder, MediaEmbedder, Vision];

    /// <summary>
    /// The service a binding must provide, or null where the contract is not defined yet: the decider and extractor
    /// contracts arrive with C-004, and media embeddings use a typed Starling API that core cannot reference.
    /// </summary>
    public static Type? ContractOf(string slot) => slot switch
    {
        Generator or Summarizer or Vision => typeof(IChatClient),
        TextEmbedder => typeof(IEmbeddingGenerator<string, Embedding<float>>),
        Decider or Extractor or MediaEmbedder => null,
        _ => throw new ArgumentException($"Unknown model slot '{slot}'.", nameof(slot)),
    };

    internal static void Check(string slot, ModelBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        var contract = ContractOf(slot);
        if (contract is not null && !contract.IsInstanceOfType(binding.Service))
            throw new ArgumentException($"Slot '{slot}' requires {contract.Name}; '{binding.Profile.Id}' provides {binding.Service.GetType().Name}.",
                nameof(binding));
    }
}

/// <summary>A profile and the service that implements it. Identity is by reference: two bindings are never "the same model".</summary>
public sealed class ModelBinding
{
    public ModelBinding(ModelProfile profile, object service)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(service);
        Profile = profile.Validate();
        Service = service;
    }

    public ModelProfile Profile { get; }

    public object Service { get; }

    public T GetService<T>() where T : class =>
        Service as T ?? throw new InvalidOperationException($"'{Profile.Id}' does not implement {typeof(T).Name}.");

    public override string ToString() => Profile.Id;
}
