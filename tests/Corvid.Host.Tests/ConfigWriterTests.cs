using System.Text.Json.Nodes;
using Corvid.Host.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>The one writer of corvid.json: validated as the next start reads it, written atomically, refusals change nothing.</summary>
public sealed class ConfigWriterTests
{
    private const string Summarizer = "Corvid:Models:Slots:summarizer:Model";

    private static string File(TestHost host) => Path.Combine(host.Root, "corvid.json");

    private static CorvidConfigWriter Writer(TestHost host) => host.Services.GetRequiredService<CorvidConfigWriter>();

    private static async Task<string> ReadAsync(TestHost host) => await System.IO.File.ReadAllTextAsync(File(host), TestContext.Current.CancellationToken);

    private static void AssertNoTemporaryFiles(TestHost host) =>
        Assert.Empty(Directory.EnumerateFiles(host.Root, "*.tmp", SearchOption.TopDirectoryOnly));

    [Fact]
    public async Task AValidEditIsWrittenKeepingTheRestOfTheFile()
    {
        await using var host = TestHost.Create();
        await System.IO.File.WriteAllTextAsync(File(host), """{ "corvid": { "Turn": { "MaxSteps": 4 } } }""", TestContext.Current.CancellationToken);

        var result = Writer(host).Apply(ConfigEdit.Set(Summarizer, "fake-gen"));

        Assert.True(result.Written, result.FailureText);
        Assert.Equal(File(host), result.Path);
        Assert.Equal([Summarizer], result.RestartKeys);
        Assert.Empty(result.ShadowedKeys);
        var written = JsonNode.Parse(await ReadAsync(host))!;
        // The existing "corvid" object is extended rather than a second "Corvid" added beside it.
        Assert.Equal("fake-gen", (string?)written["corvid"]!["Models"]!["Slots"]!["summarizer"]!["Model"]);
        Assert.Equal(4, (int?)written["corvid"]!["Turn"]!["MaxSteps"]);
        Assert.Null(written["Corvid"]);
        AssertNoTemporaryFiles(host);
    }

    [Fact]
    public async Task AnInvalidEditIsRefusedAndTheFileIsUntouched()
    {
        await using var host = TestHost.Create();
        const string original = "{\n  // the user's own comment\n  \"Corvid\": { \"Turn\": { \"MaxSteps\": 4 } }\n}\n";
        await System.IO.File.WriteAllTextAsync(File(host), original, TestContext.Current.CancellationToken);

        var result = Writer(host).Apply(ConfigEdit.Set("Corvid:Models:Slots:extractor:Model", "fake-gen"));

        Assert.False(result.Written);
        Assert.Contains(result.Failures, failure => failure.Contains("'fake-gen' cannot serve extractor", StringComparison.Ordinal));
        Assert.Empty(result.RestartKeys);
        Assert.Equal(original, await ReadAsync(host));
        AssertNoTemporaryFiles(host);
    }

    [Fact]
    public async Task AValueOfTheWrongShapeIsRefused()
    {
        await using var host = TestHost.Create();
        var result = Writer(host).Apply(ConfigEdit.Set("Corvid:Turn:MaxSteps", "many"));

        Assert.False(result.Written);
        Assert.Contains(result.Failures, failure => failure.Contains("MaxSteps", StringComparison.Ordinal));
        Assert.False(System.IO.File.Exists(File(host)));
    }

    [Fact]
    public async Task AFileThatIsNotJsonIsNeitherReadNorReplaced()
    {
        await using var host = TestHost.Create();
        await System.IO.File.WriteAllTextAsync(File(host), "{ not json", TestContext.Current.CancellationToken);

        var result = Writer(host).Apply(ConfigEdit.Set(Summarizer, "fake-gen"));

        Assert.False(result.Written);
        Assert.Contains("cannot be read as JSON", result.FailureText, StringComparison.Ordinal);
        Assert.Equal("{ not json", await ReadAsync(host));
    }

    [Fact]
    public async Task RemovingAKeyLeavesItsDefault()
    {
        await using var host = TestHost.Create();
        await System.IO.File.WriteAllTextAsync(File(host), """{ "Corvid": { "Turn": { "MaxSteps": 4, "Other": 1 } } }""", TestContext.Current.CancellationToken);

        var result = Writer(host).Apply(ConfigEdit.Remove("corvid:turn:maxsteps"));

        Assert.True(result.Written, result.FailureText);
        Assert.Equal(["corvid:turn:maxsteps"], result.RestartKeys);
        var written = JsonNode.Parse(await ReadAsync(host))!;
        Assert.Null(written["Corvid"]!["Turn"]!["MaxSteps"]);
        Assert.Equal(1, (int?)written["Corvid"]!["Turn"]!["Other"]);
    }

    [Fact]
    public async Task AnEditToTheValueItHasNeedsNoRestart()
    {
        await using var host = TestHost.Create();
        Assert.True(Writer(host).Apply(ConfigEdit.Set(Summarizer, "fake-gen")).Written);

        var again = Writer(host).Apply(ConfigEdit.Set(Summarizer, "fake-gen"));

        Assert.True(again.Written, again.FailureText);
        Assert.Empty(again.RestartKeys);
    }

    [Fact]
    public async Task AKeyThisRunOverridesIsWrittenAndSaidToBeShadowed()
    {
        // The test host gives Corvid:Workspace in memory, as --set would.
        await using var host = TestHost.Create();
        Directory.CreateDirectory(Path.Combine(host.Root, "elsewhere"));
        var result = Writer(host).Apply(ConfigEdit.Set("Corvid:Workspace", Path.Combine(host.Root, "elsewhere")));

        Assert.True(result.Written, result.FailureText);
        Assert.Equal(["Corvid:Workspace"], result.ShadowedKeys);
    }

    [Fact]
    public async Task ValidationSeesThisRunsOverridesOverTheEditedFile()
    {
        // The override's escalation ladder holds fake-gen-large, which cannot also be the generator's default.
        await using var host = TestHost.Create();
        var result = Writer(host).Apply(ConfigEdit.Set("Corvid:Models:Slots:generator:Model", "fake-gen-large"));

        Assert.False(result.Written);
        Assert.Contains("cannot also be its own escalation", result.FailureText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckSaysWhatApplyWouldWithoutWriting()
    {
        await using var host = TestHost.Create();
        var check = Writer(host).Check(ConfigEdit.Set(Summarizer, "fake-gen"));

        Assert.False(check.Written);
        Assert.Empty(check.Failures);
        Assert.Equal([Summarizer], check.RestartKeys);
        Assert.False(System.IO.File.Exists(File(host)));
    }

    [Fact]
    public async Task ReplacingTheWholeFileKeepsItsTextAndRefusesBadJson()
    {
        await using var host = TestHost.Create();
        const string raw = "{\n  // kept as typed\n  \"Corvid\": { \"Turn\": { \"MaxSteps\": 9 } }\n}\n";

        var written = Writer(host).Replace(raw);
        Assert.True(written.Written, written.FailureText);
        Assert.Contains("Corvid:Turn:MaxSteps", written.RestartKeys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(raw, await ReadAsync(host));

        var refused = Writer(host).Replace("{ \"Corvid\": ");
        Assert.False(refused.Written);
        Assert.Contains("not valid JSON", refused.FailureText, StringComparison.Ordinal);
        Assert.Equal(raw, await ReadAsync(host));

        var invalid = Writer(host).Replace("""{ "Corvid": { "Models": { "Slots": { "extractor": { "Model": "fake-gen" } } } } }""");
        Assert.False(invalid.Written);
        Assert.Equal(raw, await ReadAsync(host));
        AssertNoTemporaryFiles(host);
    }

    [Fact]
    public async Task AnArrayIsSetWhole()
    {
        await using var host = TestHost.Create();
        var result = Writer(host).Apply(new ConfigEdit("Corvid:Models:Slots:embedder.text:Fallbacks", new JsonArray("fake-embed-alt")));

        Assert.True(result.Written, result.FailureText);
        var written = JsonNode.Parse(await ReadAsync(host))!;
        Assert.Equal("fake-embed-alt", (string?)written["Corvid"]!["Models"]!["Slots"]!["embedder.text"]!["Fallbacks"]![0]);
    }

    [Fact]
    public async Task NextStartReadsTheFileAsWrittenWithThisRunsOverridesOverIt()
    {
        await using var host = TestHost.Create(settings: new Dictionary<string, string?> { ["Corvid:Turn:MaxSteps"] = "6" });
        var writer = Writer(host);

        Assert.Null(writer.NextStart()[Summarizer]);
        Assert.True(writer.Apply(ConfigEdit.Set(Summarizer, "fake-gen"), new ConfigEdit("Corvid:Turn:MaxSteps", JsonValue.Create(4))).Written);

        var next = writer.NextStart();
        Assert.Equal("fake-gen", next[Summarizer]);
        // --set (the test host's overrides) still wins over the file.
        Assert.Equal("6", next["Corvid:Turn:MaxSteps"]);
        // The host's own configuration was read at start and does not follow the file.
        Assert.Null(host.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()[Summarizer]);
    }

    [Fact]
    public async Task AnOverrideIsFoundOnlyForKeysASourceOverTheFileSets()
    {
        await using var host = TestHost.Create(settings: new Dictionary<string, string?> { ["Corvid:Turn:MaxSteps"] = "6" });
        await System.IO.File.WriteAllTextAsync(File(host), """{ "Corvid": { "Turn": { "MaxSteps": 4, "MaxOutputTokens": 512 } } }""",
            TestContext.Current.CancellationToken);
        var writer = Writer(host);

        Assert.True(writer.TryGetOverride("corvid:turn:maxsteps", out var steps));
        Assert.Equal("6", steps);
        Assert.False(writer.TryGetOverride("Corvid:Turn:MaxOutputTokens", out var tokens));
        Assert.Null(tokens);
    }

    [Fact]
    public async Task NextStartOfAFileThatIsNotJsonSaysSo()
    {
        await using var host = TestHost.Create();
        await System.IO.File.WriteAllTextAsync(File(host), "{ not json", TestContext.Current.CancellationToken);

        var exception = Assert.Throws<InvalidDataException>(() => Writer(host).NextStart());

        Assert.Contains(File(host), exception.Message, StringComparison.Ordinal);
    }
}
