using System.Text.Json.Nodes;
using Corvid.Host.Configuration;

namespace Corvid.Host.Tests;

/// <summary>A3 review M3: corvid.json edited in place, the rest of its text (comments, layout, line breaks) as it was.</summary>
public sealed class JsoncTextTests
{
    [Fact]
    public void RemovingAMiddleKeyKeepsTheCommentsAroundIt()
    {
        const string text = "{\n  // about a\n  \"a\": 1,\n  // about b\n  \"b\": 2, // trailing b\n  \"c\": 3\n}";

        var result = JsoncText.Set(text, ["B"], null);

        Assert.Equal("{\n  // about a\n  \"a\": 1,\n  // about b\n  \"c\": 3\n}", result);
    }

    [Fact]
    public void ANewKeyFollowsTheFilesLineBreaksAndIndentation()
    {
        const string text = "{\r\n    \"Corvid\": {\r\n        \"Turn\": 1\r\n    }\r\n}\r\n";

        var result = JsoncText.Set(text, ["Corvid", "Models", "Cache"], JsonValue.Create("~/models"));

        Assert.Equal("{\r\n    \"Corvid\": {\r\n        \"Turn\": 1,\r\n        \"Models\": {\r\n            \"Cache\": \"~/models\"\r\n        }\r\n    }\r\n}\r\n", result);
    }

    [Fact]
    public void ACommentBeforeTheCommaIsNotTakenForAMissingComma()
    {
        Assert.Equal("{ \"a\": 1 /* one */, \"b\": 2, \"c\": 3 }", JsoncText.Set("{ \"a\": 1 /* one */, \"b\": 2 }", ["c"], JsonValue.Create(3)));
        Assert.Equal("{ \"b\": 2 }", JsoncText.Set("{ \"a\": 1 /* one */, \"b\": 2 }", ["a"], null));
        Assert.Equal("{\n  \"a\": 1 // one\n  , \"b\": 2, \"c\": 3\n}", JsoncText.Set("{\n  \"a\": 1 // one\n  , \"b\": 2\n}", ["c"], JsonValue.Create(3)));
    }

    [Fact]
    public void AnEmptyObjectGetsItsFirstKeyOnALineOfItsOwn()
    {
        Assert.Equal("{\n  \"a\": true\n}", JsoncText.Set("{}", ["a"], JsonValue.Create(true)));
        Assert.Equal("{ /* nothing yet */ }", JsoncText.Set("{ /* nothing yet */ }", ["a"], null));
    }

    [Fact]
    public void AnArrayIsEnteredByIndexOnly()
    {
        Assert.Equal("{ \"x\": [1, 5] }", JsoncText.Set("{ \"x\": [1, 2] }", ["x", "1"], JsonValue.Create(5)));
        Assert.Equal("{ \"x\": [1, 2, 3] }", JsoncText.Set("{ \"x\": [1, 2] }", ["x", "2"], JsonValue.Create(3)));
        Assert.Equal("{ \"x\": [2] }", JsoncText.Set("{ \"x\": [1, 2] }", ["x", "0"], null));
        Assert.Throws<InvalidOperationException>(() => JsoncText.Set("{ \"x\": [1] }", ["x", "name"], JsonValue.Create(1)));
    }

    [Fact]
    public void APlainValueInTheWayBecomesAnObject()
    {
        Assert.Equal("{ \"a\": {\n  \"b\": 1\n} }",JsoncText.Set("{ \"a\": 3 }", ["a", "b"], JsonValue.Create(1)));
    }
}
