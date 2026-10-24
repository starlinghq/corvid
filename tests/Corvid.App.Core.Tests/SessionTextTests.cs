using Corvid.App.Core.Sessions;

namespace Corvid.App.Core.Tests;

public sealed class SessionTextTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("x", "about 1 token")]
    [InlineData("four", "about 1 token")]
    [InlineData("fives", "about 2 tokens")]
    public void TheTokenEstimateIsAQuarterOfTheCharactersInWords(string draft, string expected) =>
        Assert.Equal(expected, SessionText.TokenEstimate(draft));
}
