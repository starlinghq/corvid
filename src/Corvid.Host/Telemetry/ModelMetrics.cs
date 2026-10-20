using System.Collections.Concurrent;

namespace Corvid.Host.Telemetry;

/// <summary>Latency of one binding's calls, for <c>/status</c> (plan §4.4): the median of the most recent calls.</summary>
public sealed class ModelLatency
{
    private const int Window = 128;
    private readonly double[] _samples = new double[Window];
    private readonly Lock _gate = new();
    private int _next;
    private int _count;
    private long _calls;
    private long _failures;

    public long Calls => Interlocked.Read(ref _calls);

    public long Failures => Interlocked.Read(ref _failures);

    public void Record(TimeSpan elapsed, bool failed)
    {
        Interlocked.Increment(ref _calls);
        if (failed)
            Interlocked.Increment(ref _failures);
        lock (_gate)
        {
            _samples[_next] = elapsed.TotalMilliseconds;
            _next = (_next + 1) % Window;
            _count = Math.Min(_count + 1, Window);
        }
    }

    /// <summary>The median of the last 128 calls, or null before the first.</summary>
    public TimeSpan? P50
    {
        get
        {
            double[] window;
            lock (_gate)
                window = _samples[.._count];
            if (window.Length == 0)
                return null;
            Array.Sort(window);
            var middle = window.Length / 2;
            var median = window.Length % 2 == 1 ? window[middle] : (window[middle - 1] + window[middle]) / 2;
            return TimeSpan.FromMilliseconds(median);
        }
    }
}

/// <summary>Latency per binding, keyed by slot and profile id; shared by every session.</summary>
public sealed class ModelMetrics
{
    private readonly ConcurrentDictionary<(string Slot, string Profile), ModelLatency> _latency = new();

    public ModelLatency For(string slot, string profileId) => _latency.GetOrAdd((slot, profileId), _ => new ModelLatency());

    public bool TryGet(string slot, string profileId, out ModelLatency latency) => _latency.TryGetValue((slot, profileId), out latency!);
}
