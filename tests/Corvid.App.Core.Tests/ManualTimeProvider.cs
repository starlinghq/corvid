namespace Corvid.App.Core.Tests;

/// <summary>
/// A clock the test moves: <see cref="Advance"/> fires every timer that falls due on the way, in time order, on the
/// calling thread. With <c>InlineDispatcher</c>, a scripted turn runs to the same point every time.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private readonly TimeZoneInfo _zone;
    private DateTimeOffset _now;
    private long _sequence;

    public ManualTimeProvider(DateTimeOffset start, TimeZoneInfo? zone = null)
    {
        _now = start;
        _zone = zone ?? TimeZoneInfo.Utc;
    }

    /// <summary>The demo's moment, 2026-10-24 14:05, in UTC so the scenario's wall-clock times are the mockups'.</summary>
    public static ManualTimeProvider Demo() => new(FrozenTimeProvider.Demo(TimeZoneInfo.Utc).GetUtcNow(), TimeZoneInfo.Utc);

    public override DateTimeOffset GetUtcNow() => _now;

    public override TimeZoneInfo LocalTimeZone => _zone;

    /// <summary>Timers that will still fire.</summary>
    public int ActiveTimers => _timers.Count(timer => timer.Active);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        var target = _now + by;
        while (true)
        {
            var next = _timers.Where(timer => timer.Active && timer.Due <= target)
                .OrderBy(timer => timer.Due).ThenBy(timer => timer.Order).FirstOrDefault();
            if (next is null)
                break;
            if (next.Due > _now)
                _now = next.Due;
            next.Fire();
        }

        _now = target;
    }

    public void Advance(double seconds) => Advance(TimeSpan.FromSeconds(seconds));

    private sealed class ManualTimer(ManualTimeProvider provider, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period;

        public DateTimeOffset Due { get; private set; }

        public long Order { get; private set; }

        public bool Active { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime == Timeout.InfiniteTimeSpan)
            {
                Active = false;
                return true;
            }

            Due = provider._now + dueTime;
            _period = period;
            Order = ++provider._sequence;
            Active = true;
            return true;
        }

        public void Fire()
        {
            if (_period > TimeSpan.Zero && _period != Timeout.InfiniteTimeSpan)
            {
                Due += _period;
                Order = ++provider._sequence;
            }
            else
            {
                Active = false;
            }

            callback(state);
        }

        public void Dispose()
        {
            Active = false;
            provider._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
