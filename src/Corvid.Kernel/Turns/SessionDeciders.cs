using System.Runtime.CompilerServices;
using Corvid.Abstractions.Fabric;
using Corvid.Fabric;
using Corvid.Models;

namespace Corvid.Kernel.Turns;

/// <summary>
/// A5, the decider per session: the fabric a session's checkpoints (and its approvals' <c>risk</c> question) run on, from the
/// session's current <c>decider</c> slot. A session whose decider is the registry's default runs on the shared fabric, as
/// before; one whose mix names another decider, or whose person switched it (<c>/model decider …</c>), runs on a fabric
/// with that decider as its first tier and its slot's first escalation step as the second (<see cref="DecisionFabric.WithDeciders"/>).
/// Everything else (policy, degraded tier, log, checkpoint profile) is the shared fabric's, so calibration still reads every
/// decision. Thread-safe; fabrics are made once per decider pair and kept as long as the bindings live.
/// </summary>
public sealed class SessionDeciders
{
    private readonly DecisionFabric _shared;
    private readonly ModelBinding? _sharedBinding;
    private readonly ConditionalWeakTable<ModelBinding, Derived> _derived = [];

    /// <param name="shared">The fabric built on <paramref name="registry"/>'s decider as it is now.</param>
    /// <param name="registry">
    /// Read once, for the binding the shared fabric was built on: a session on that binding runs on the shared fabric. A later
    /// rebinding of the registry's decider (a new default for new sessions) reaches them through a fabric of its own.
    /// </param>
    public SessionDeciders(DecisionFabric shared, ModelSlotRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(registry);
        _shared = shared;
        _sharedBinding = registry.TryGet(ModelSlots.Decider, out var decider) ? decider.Default : null;
    }

    /// <summary>The shared fabric: the configured decider, for sessions that chose none of their own.</summary>
    public DecisionFabric Shared => _shared;

    /// <summary>The fabric for the session's decider now.</summary>
    public DecisionFabric For(SessionModelSlots slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        if (!slots.TryCurrent(ModelSlots.Decider, out var binding))
            return _shared;
        // The decider the shared fabric was built on, unless the session's mix gave it another ladder.
        if (ReferenceEquals(binding, _sharedBinding) && !slots.OwnSlots.Contains(ModelSlots.Decider))
            return _shared;
        if (binding.Service is not IDecider calibrated)
            return _shared;
        var escalation = slots.TryConfiguration(ModelSlots.Decider, out var configuration)
            ? configuration.Escalation.FirstOrDefault(step => !ReferenceEquals(step, binding))?.Service as IDecider
            : null;
        var derived = _derived.GetValue(binding, _ => new Derived());
        lock (derived)
        {
            if (derived.Fabric is null || !ReferenceEquals(derived.Escalation, escalation))
                (derived.Fabric, derived.Escalation) = (_shared.WithDeciders(calibrated, escalation), escalation);
            return derived.Fabric;
        }
    }

    /// <summary>The decider the session's checkpoints are answered by now, for status lines: its name, or null on the degraded tier.</summary>
    public string? DeciderName(SessionModelSlots slots) => For(slots).CalibratedDecider;

    private sealed class Derived
    {
        public DecisionFabric? Fabric;
        public IDecider? Escalation;
    }
}
