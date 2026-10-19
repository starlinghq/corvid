using System.Collections.Concurrent;
using Corvid.Kernel.Scheduling;

namespace Corvid.Host.Composition;

/// <summary>
/// One <see cref="DeviceScheduler"/> per local device a profile names (<c>cpu</c>, <c>cuda:0</c>), created on first use
/// (plan §3.3). Bindings placed elsewhere (a local server, a cloud provider) are not device work and are not scheduled.
/// </summary>
public sealed class DeviceSchedulers(int cpuThreads = 1) : IDisposable
{
    private readonly ConcurrentDictionary<string, DeviceScheduler> _devices = new(StringComparer.Ordinal);

    public static bool IsLocalDevice(string placement) =>
        placement == "cpu" || placement.StartsWith("cuda", StringComparison.Ordinal);

    public DeviceScheduler For(string placement)
    {
        if (!IsLocalDevice(placement))
            throw new ArgumentException($"'{placement}' is not a local device.", nameof(placement));
        // Starling serializes native work per GPU; more threads only help a CPU pool.
        return _devices.GetOrAdd(placement, device => new DeviceScheduler(device, device == "cpu" ? cpuThreads : 1));
    }

    public IReadOnlyCollection<DeviceScheduler> All => [.. _devices.Values.OrderBy(device => device.Device, StringComparer.Ordinal)];

    public bool TryGet(string placement, out DeviceScheduler device) => _devices.TryGetValue(placement, out device!);

    /// <summary>Queued items across priorities, for <c>/status</c>.</summary>
    public int QueueDepth(string placement) =>
        TryGet(placement, out var device) ? Enum.GetValues<DevicePriority>().Sum(device.QueueDepth) : 0;

    public void Dispose()
    {
        foreach (var device in _devices.Values)
            device.Dispose();
        _devices.Clear();
    }
}
