using System.Collections.Immutable;

namespace Corvid.Models;

/// <summary>How one slot is configured: its default, where escalation goes, and what to try when a provider fails.</summary>
/// <param name="Escalation">Profile-ordered targets, for example local 0.8B, then a local-server 27B, then cloud (plan §6).</param>
/// <param name="Fallbacks">Ordered alternatives tried after a failing binding, for example cloud A, cloud B, local.</param>
public sealed record SlotConfiguration(
    string Slot,
    ModelBinding Default,
    ImmutableArray<ModelBinding> Escalation,
    ImmutableArray<ModelBinding> Fallbacks);

/// <summary>
/// The process-wide catalogue of slot bindings. Sessions take their starting bindings from here and switch through
/// <see cref="SessionModelSlots"/>; rebinding a slot here changes only where new sessions start and where de-escalation returns.
/// </summary>
public sealed class ModelSlotRegistry
{
    private ImmutableDictionary<string, SlotConfiguration> _slots = ImmutableDictionary.Create<string, SlotConfiguration>(StringComparer.Ordinal);

    public ModelSlotRegistry Bind(string slot, ModelBinding defaultBinding, IEnumerable<ModelBinding>? escalation = null,
        IEnumerable<ModelBinding>? fallbacks = null)
    {
        ModelSlots.Check(slot, defaultBinding);
        var ladder = escalation?.ToImmutableArray() ?? [];
        var chain = fallbacks?.ToImmutableArray() ?? [];
        foreach (var binding in ladder.Concat(chain))
            ModelSlots.Check(slot, binding);
        var configuration = new SlotConfiguration(slot, defaultBinding, ladder, chain);
        ImmutableInterlocked.Update(ref _slots, slots => slots.SetItem(slot, configuration));
        return this;
    }

    public IReadOnlyCollection<string> BoundSlots => _slots.Keys.ToArray();

    public bool TryGet(string slot, out SlotConfiguration configuration) =>
        _slots.TryGetValue(slot, out configuration!);

    public SlotConfiguration Get(string slot)
    {
        ModelSlots.ContractOf(slot);
        return TryGet(slot, out var configuration)
            ? configuration
            : throw new InvalidOperationException($"Model slot '{slot}' is not bound.");
    }
}
