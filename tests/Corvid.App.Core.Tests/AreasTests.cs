using Corvid.App.Core;

namespace Corvid.App.Core.Tests;

public sealed class AreasTests
{
    [Fact]
    public void TheRailListsTheMockupsDestinationsInOrder() =>
        Assert.Equal(["Sessions", "Board", "Review", "Memory", "Skills", "Jobs", "Models", "Safety", "MCP", "Gateway", "Secrets", "Audit"],
            Areas.Rail.Select(area => area.Title()));

    [Fact]
    public void EveryRailDestinationHasAnIcon()
    {
        foreach (var area in Areas.Rail)
            Assert.NotEqual(IconKind.None, area.Icon());
        Assert.Equal(Areas.Rail.Count, Areas.Rail.Select(area => area.Icon()).Distinct().Count());
    }

    [Fact]
    public void EveryAreaHasATitleAndRewindKeepsSessionsSelected()
    {
        foreach (var area in Enum.GetValues<Area>())
            Assert.False(string.IsNullOrWhiteSpace(area.Title()));
        Assert.Equal("First run", Area.FirstRun.Title());
        Assert.Equal(Area.Sessions, Area.Rewind.RailRow());
        Assert.Null(Area.Settings.RailRow());
        Assert.Null(Area.FirstRun.RailRow());
        Assert.Equal(Area.Audit, Area.Audit.RailRow());
    }

    [Theory]
    [InlineData("sessions", true, Area.Sessions)]
    [InlineData("first_run", true, Area.FirstRun)]
    [InlineData("First run", true, Area.FirstRun)]
    [InlineData("", false, Area.Sessions)]
    [InlineData(null, false, Area.Sessions)]
    [InlineData("chat", false, Area.Sessions)]
    public void AreasParseByNameOrTitle(string? text, bool parsed, Area area)
    {
        Assert.Equal(parsed, Areas.TryParse(text, out var result));
        if (parsed)
            Assert.Equal(area, result);
    }
}
