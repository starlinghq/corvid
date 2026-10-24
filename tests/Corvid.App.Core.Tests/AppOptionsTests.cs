using Corvid.App.Core;
using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Tests;

public sealed class AppOptionsTests
{
    private static AppOptions Parse(params string[] args)
    {
        Assert.True(AppOptions.TryParse(args, out var options, out var error), error);
        return options;
    }

    private static string Fail(params string[] args)
    {
        Assert.False(AppOptions.TryParse(args, out _, out var error));
        return error;
    }

    [Fact]
    public void NoArgumentsOpenTheDemoOnSessionsInTheSystemTheme()
    {
        var options = Parse();

        Assert.True(options.Demo);
        Assert.Equal(AppTheme.System, options.Theme);
        Assert.Null(options.Page);
        Assert.Null(options.Session);
        Assert.Equal(DemoScene.Main, options.Scene);
        Assert.False(options.Help);
    }

    [Fact]
    public void TheSceneIsTheMockupsMomentToOpenAt()
    {
        Assert.Equal(DemoScene.Approval, Parse("--scene", "approval").Scene);
        Assert.Equal(DemoScene.Main, Parse("--scene=MAIN").Scene);
        Assert.Equal("--scene takes main or approval, not \"board\".", Fail("--scene", "board"));
        Assert.Equal("--scene takes main or approval, not \"1\".", Fail("--scene", "1"));
    }

    [Theory]
    [InlineData(new[] { "--theme", "dark" }, AppTheme.Dark)]
    [InlineData(new[] { "--theme=light" }, AppTheme.Light)]
    [InlineData(new[] { "--theme", "SYSTEM" }, AppTheme.System)]
    public void ThemesAreReadInEitherForm(string[] args, AppTheme theme) => Assert.Equal(theme, Parse(args).Theme);

    [Theory]
    [InlineData("models", Area.Models)]
    [InlineData("MCP", Area.Mcp)]
    [InlineData("first-run", Area.FirstRun)]
    [InlineData("FirstRun", Area.FirstRun)]
    [InlineData("rewind", Area.Rewind)]
    public void PagesAreAreaNamesInAnyCase(string page, Area area)
    {
        Assert.Equal(area, Parse("--page", page).Page);
        Assert.Equal(area, Parse($"--page={page}").Page);
    }

    [Fact]
    public void OptionsCombine()
    {
        var options = Parse("--demo", "--session", "cli-77b0", "--page", "review", "--theme", "dark");

        Assert.Equal("cli-77b0", options.Session);
        Assert.Equal(Area.Review, options.Page);
        Assert.Equal(AppTheme.Dark, options.Theme);
        Assert.True(Parse("--help").Help);
    }

    [Theory]
    [InlineData(new[] { "--bogus" }, "Unknown argument \"--bogus\"")]
    [InlineData(new[] { "stray" }, "Unknown argument \"stray\"")]
    [InlineData(new[] { "--theme", "blue" }, "--theme takes light, dark or system")]
    [InlineData(new[] { "--page", "nowhere" }, "--page takes an area")]
    [InlineData(new[] { "--session" }, "--session needs a value")]
    [InlineData(new[] { "--page", "--theme", "dark" }, "--page needs a value")]
    [InlineData(new[] { "--session=" }, "--session needs a value")]
    [InlineData(new[] { "--demo=yes" }, "Unknown argument \"--demo=yes\"")]
    public void AWrongArgumentSaysWhich(string[] args, string error) => Assert.Contains(error, Fail(args), StringComparison.Ordinal);

    [Fact]
    public void TheUsageNamesEveryOption()
    {
        foreach (var option in new[] { "--demo", "--theme", "--page", "--session", "--help" })
            Assert.Contains(option, AppOptions.Usage, StringComparison.Ordinal);
    }
}
