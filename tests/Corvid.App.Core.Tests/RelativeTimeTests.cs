using Corvid.App.Core;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

public sealed class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 14, 5, 0, TimeSpan.FromHours(2));

    [Theory]
    [InlineData(0, "0 s")]
    [InlineData(12, "12 s")]
    [InlineData(59, "59 s")]
    [InlineData(60, "1 m")]
    [InlineData(120, "2 m")]
    [InlineData(3599, "59 m")]
    [InlineData(3600, "1 h")]
    [InlineData(86399, "23 h")]
    [InlineData(3 * 86400, "3 d")]
    [InlineData(-30, "0 s")]
    public void SinceReadsAsTheRailPrintsIt(int seconds, string expected) =>
        Assert.Equal(expected, RelativeTime.Since(Now, Now.AddSeconds(-seconds)));

    [Fact]
    public void AJobRunIsListedByItsClockTimeInTheLocalZone() =>
        Assert.Equal("09:00", RelativeTime.ClockTime(new DateTimeOffset(2026, 10, 24, 7, 0, 0, TimeSpan.Zero), TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2")));

    [Fact]
    public void TheDemoClockIsFrozenAtTheScenariosMinuteInTheMachinesZone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("minus5", TimeSpan.FromHours(-5), "minus5", "minus5");
        var clock = FrozenTimeProvider.Demo(zone);

        var local = clock.GetLocalNow();

        Assert.Equal(new DateTime(2026, 10, 24, 14, 5, 0), local.DateTime);
        Assert.Equal(TimeSpan.FromHours(-5), local.Offset);
        Assert.Equal(clock.GetUtcNow(), clock.GetUtcNow());
    }
}
