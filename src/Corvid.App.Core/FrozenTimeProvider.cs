namespace Corvid.App.Core;

/// <summary>
/// A clock that never moves. Demo mode freezes it at 2026-10-24 14:05 local, so every relative time the views print
/// ("12 s", "2 m") and every screenshot is the same on every run (docs/plan/ui.md §8).
/// </summary>
public sealed class FrozenTimeProvider : TimeProvider
{
    /// <summary>The demo scenario's "now", as a wall-clock time in whatever zone the machine is in.</summary>
    public static readonly DateTime DemoLocalTime = new(2026, 10, 24, 14, 5, 0, DateTimeKind.Unspecified);

    private readonly DateTimeOffset _now;
    private readonly TimeZoneInfo _zone;

    public FrozenTimeProvider(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        _now = now;
        _zone = zone ?? TimeZoneInfo.Local;
    }

    public override TimeZoneInfo LocalTimeZone => _zone;

    /// <summary>The demo clock: <see cref="DemoLocalTime"/> in the given zone (the machine's by default).</summary>
    public static FrozenTimeProvider Demo(TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        return new FrozenTimeProvider(new DateTimeOffset(DemoLocalTime, zone.GetUtcOffset(DemoLocalTime)), zone);
    }

    public override DateTimeOffset GetUtcNow() => _now.ToUniversalTime();
}
