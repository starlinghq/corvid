using System.Text.Json.Nodes;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Settings;
using Corvid.App.Engine.Settings;
using Corvid.Host.Tests;

namespace Corvid.App.Engine.Tests;

/// <summary>Settings over the test host's own corvid.json in its temporary directory, written through the host's config writer.</summary>
public sealed class EngineSettingsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string FileOf(EngineFixture fixture) => Path.Combine(fixture.Host.Root, "corvid.json");

    private static async Task<JsonNode> ReadAsync(EngineFixture fixture) =>
        JsonNode.Parse(await File.ReadAllTextAsync(FileOf(fixture), Token), documentOptions: SettingsJson.Reading)!;

    [Fact]
    public async Task LoadingReadsTheFileItsDefaultsAndThisRunsOverrides()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var service = new EngineSettingsService(fixture.Engine);

        var snapshot = await service.LoadAsync(Token);

        Assert.Equal(FileOf(fixture), snapshot.Path);
        Assert.False(snapshot.IsDemo);
        Assert.Contains("\"Corvid\"", snapshot.Json, StringComparison.Ordinal);
        Assert.Equal("8", snapshot.Values["Turn:MaxSteps"]);
        Assert.Equal("Smart", snapshot.Values["Approvals:Mode"]);
        Assert.Equal("Comfortable", snapshot.Values["Density"]);
        // The test host gives the data directory and the workspace for this run, over the file.
        Assert.Equal(Path.Combine(fixture.Host.Root, "data"), snapshot.RunOverrides["DataDirectory"]);
        Assert.Equal(Path.Combine(fixture.Host.Root, "work"), snapshot.RunOverrides["Workspace"]);
        Assert.False(snapshot.RunOverrides.ContainsKey("Turn:MaxSteps"));
        // The runtime reads corvid.json once, at start, except what it takes live: the approval mode and deadline at once, the
        // step budget and output tokens in sessions opened from then on.
        var live = new Dictionary<string, SettingApplies>
        {
            ["Approvals:Mode"] = SettingApplies.NewSessions,
            ["Approvals:TimeoutSeconds"] = SettingApplies.Now,
            ["Turn:MaxSteps"] = SettingApplies.NewSessions,
            ["Turn:MaxOutputTokens"] = SettingApplies.NewSessions,
        };
        Assert.All(service.Definitions.Where(definition => definition.Store == SettingStore.CorvidJson),
            definition => Assert.Equal(live.GetValueOrDefault(definition.Key, SettingApplies.Restart), definition.Applies));
        Assert.Contains(service.Definitions, definition => definition is { Store: SettingStore.App, Applies: SettingApplies.Now });
    }

    [Fact]
    public async Task SavingTypedValuesWritesThemAsJsonOfTheirKindAndSaysTheyNeedARestart()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var service = new EngineSettingsService(fixture.Engine);

        var write = await service.SaveAsync(new Dictionary<string, string>
        {
            ["Turn:MaxSteps"] = "12",
            ["Approvals:Mode"] = "Manual",
            ["Tools:ShellNetwork"] = "true",
            ["Privacy:Threshold"] = "0.5",
        }, Token);

        Assert.True(write.Written, write.Message);
        Assert.Contains("after a restart", write.Message, StringComparison.Ordinal);
        // The mode and the step budget apply to the running runtime now; the others after a restart.
        Assert.Contains("Applied now: default mode (in sessions opened from now on), steps per turn (in sessions opened from now on).", write.Message, StringComparison.Ordinal);
        Assert.Equal(["Privacy:Threshold", "Tools:ShellNetwork"], write.RestartKeys.Order(StringComparer.Ordinal));
        Assert.Equal((Corvid.Approvals.ApprovalMode.Manual, 12), (fixture.Runtime.Options.Approvals.Mode, fixture.Runtime.Options.Turn.MaxSteps));
        var file = await ReadAsync(fixture);
        Assert.Equal(12, (int)file["Corvid"]!["Turn"]!["MaxSteps"]!);
        Assert.True((bool)file["Corvid"]!["Tools"]!["ShellNetwork"]!);
        Assert.Equal(0.5, (double)file["Corvid"]!["Privacy"]!["Threshold"]!);
        Assert.Equal("Manual", (string?)file["Corvid"]!["Approvals"]!["Mode"]);

        var again = await service.LoadAsync(Token);
        Assert.Equal("12", again.Values["Turn:MaxSteps"]);
        Assert.Equal("true", again.Values["Tools:ShellNetwork"]);

        // An emptied text setting is removed from the file, so its default applies again.
        Assert.True((await service.SaveAsync(new Dictionary<string, string> { ["Telemetry:OtlpEndpoint"] = "http://127.0.0.1:4317" }, Token)).Written);
        Assert.True((await service.SaveAsync(new Dictionary<string, string> { ["Telemetry:OtlpEndpoint"] = "" }, Token)).Written);
        Assert.Null((await ReadAsync(fixture))["Corvid"]!["Telemetry"]!["OtlpEndpoint"]);
    }

    [Fact]
    public async Task AValueTheValidatorRefusesWritesNothing()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var service = new EngineSettingsService(fixture.Engine);

        var write = await service.SaveAsync(new Dictionary<string, string> { ["Turn:MaxSteps"] = "99" }, Token);

        Assert.False(write.Written);
        var problem = Assert.Single(write.Problems);
        Assert.Equal("Turn:MaxSteps", problem.Key);
        Assert.Equal("Corvid:Turn:MaxSteps must be between 1 and 64.", problem.Message);
        Assert.Contains(problem.Message, write.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(FileOf(fixture)));
    }

    [Fact]
    public async Task AValueThisRunOverridesIsWrittenAndTheMessageSaysTheRunKeepsTheOther()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var service = new EngineSettingsService(fixture.Engine);
        var elsewhere = Path.Combine(fixture.Host.Root, "elsewhere");

        var write = await service.SaveAsync(new Dictionary<string, string> { ["DataDirectory"] = elsewhere }, Token);

        Assert.True(write.Written, write.Message);
        Assert.Contains("overrides Corvid:DataDirectory", write.Message, StringComparison.Ordinal);
        Assert.Equal(elsewhere, (string?)(await ReadAsync(fixture))["Corvid"]!["DataDirectory"]);
    }

    [Fact]
    public async Task TheRawEditorWritesTheTextAsGivenAfterTheHostsValidatorPassesIt()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var service = new EngineSettingsService(fixture.Engine);
        const string text = "{\n  // kept as typed\n  \"Corvid\": { \"Turn\": { \"MaxSteps\": 5 } }\n}\n";

        Assert.True(service.Parse(text).IsValid);
        var write = await service.SaveJsonAsync(text, Token);

        Assert.True(write.Written, write.Message);
        Assert.Equal(text, await File.ReadAllTextAsync(FileOf(fixture), Token));
        // The step budget is taken live: not a restart key.
        Assert.DoesNotContain("Turn:MaxSteps", write.RestartKeys);
        Assert.Equal(5, fixture.Runtime.Options.Turn.MaxSteps);
        Assert.Equal("5", (await service.LoadAsync(Token)).Values["Turn:MaxSteps"]);
    }

    [Fact]
    public async Task ALiveSettingReachesTheRunningRuntimeAndTheNextSession()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var service = new EngineSettingsService(fixture.Engine);
        var open = fixture.Runtime.OpenSession(surface: "app");

        var write = await service.SaveAsync(new Dictionary<string, string>
        {
            ["Approvals:TimeoutSeconds"] = "45",
            ["Approvals:Mode"] = "Manual",
        }, Token);

        Assert.True(write.Written, write.Message);
        Assert.Contains("deadline (seconds) (from the next prompt)", write.Message, StringComparison.Ordinal);
        Assert.Empty(write.RestartKeys);
        Assert.DoesNotContain("after a restart", write.Message, StringComparison.Ordinal);
        // The deadline from the next prompt of every session; the mode in sessions opened from now on.
        Assert.Equal(TimeSpan.FromSeconds(45), fixture.Runtime.Approvals.Options.Timeout);
        Assert.Equal(Corvid.Approvals.ApprovalMode.Smart, open.ApprovalMode);
        Assert.Equal(Corvid.Approvals.ApprovalMode.Manual, fixture.Runtime.OpenSession(surface: "app").ApprovalMode);
    }

    [Fact]
    public async Task TheRawEditorRefusesWhatTheHostRefusesAndKeepsTheFile()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var service = new EngineSettingsService(fixture.Engine);
        const string original = "{ \"Corvid\": { \"Turn\": { \"MaxSteps\": 5 } } }";
        await File.WriteAllTextAsync(FileOf(fixture), original, Token);

        // A part the typed page does not show: only the host's validator knows it is wrong.
        const string remote = """{ "Corvid": { "Models": { "Remote": { "local": { "Provider": "nonsense" } } } } }""";
        var parse = service.Parse(remote);
        Assert.False(parse.IsValid);
        Assert.Contains(parse.Problems, problem => problem.Message.Contains("Corvid:Models:Remote:local:Provider must be one of", StringComparison.Ordinal));
        var refused = await service.SaveJsonAsync(remote, Token);
        Assert.False(refused.Written);
        Assert.Contains("Corvid:Models:Remote:local:Provider", refused.Message, StringComparison.Ordinal);

        // A key twice, and a hidden character written as an escape, are refused before the host sees them.
        var duplicate = service.Parse("{ \"Corvid\": { \"Workspace\": \"/a\", \"workspace\": \"/b\" } }");
        Assert.Contains("duplicate key", Assert.Single(duplicate.Problems).Message, StringComparison.Ordinal);
        var hidden = await service.SaveJsonAsync("{ \"Corvid\": { \"Telemetry\": { \"ServiceName\": \"cor\\u200Bvid\" } } }", Token);
        Assert.False(hidden.Written);
        Assert.Contains("hidden characters", hidden.Message, StringComparison.Ordinal);

        Assert.Equal(original, await File.ReadAllTextAsync(FileOf(fixture), Token));
    }

    [Fact]
    public async Task AFileThatDoesNotParseStillOpensInTheRawEditor()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var service = new EngineSettingsService(fixture.Engine);
        await File.WriteAllTextAsync(FileOf(fixture), "{ \"Corvid\": ", Token);

        var snapshot = await service.LoadAsync(Token);

        Assert.Equal("{ \"Corvid\": ", snapshot.Json);
        Assert.Equal("8", snapshot.Values["Turn:MaxSteps"]);
        Assert.Contains("does not parse at line 1", Assert.Single(service.Parse(snapshot.Json).Problems).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppPreferencesAreSavedInUiDbAndLastBeyondTheWindow()
    {
        await using var fixture = await EngineFixture.StartAsync();
        var service = new EngineSettingsService(fixture.Engine);

        var write = await service.SaveAsync(new Dictionary<string, string> { ["Density"] = "Compact", ["RailCollapsed"] = "true" }, Token);

        Assert.True(write.Written);
        Assert.Equal("App preferences saved in ui.db.", write.Message);
        Assert.Empty(write.RestartKeys);
        // Another window (a restart) reads them from ui.db; corvid.json is untouched.
        var next = new EngineSettingsService(fixture.Engine);
        Assert.Equal(("Compact", "true"), ((await next.LoadAsync(Token)).Values["Density"], (await next.LoadAsync(Token)).Values["RailCollapsed"]));
        Assert.Equal("Compact", fixture.Runtime.UiState.Preferences()["Density"]);
        Assert.False(File.Exists(FileOf(fixture)));
        Assert.Equal("saved in ui.db · kept across restarts", ((Corvid.App.Core.Settings.ISettingsService)next).PreferencesNote);
    }

    [Fact]
    public async Task TheWindowStartsWithTheLayoutTheyChose()
    {
        await using var fixture = await EngineFixture.StartAsync();
        fixture.Runtime.UiState.SetPreferences(new Dictionary<string, string> { ["RailCollapsed"] = "true", ["StartLayout"] = "Board" },
            DateTimeOffset.UtcNow);

        using var composition = new AppComposition(new AppOptions { Demo = false }, new InlineDispatcher(), fixture.Time,
            new EngineAppServices(fixture.Engine, fixture.Host.Root, _ => null));
        await composition.PreferencesApplied;

        Assert.Equal(Corvid.App.Core.Area.Board, composition.Shell.CurrentArea);
        composition.Shell.Layout = Corvid.App.Core.Shell.ShellLayout.Wide;
        Assert.False(composition.Shell.ShowRail);
    }

    [Fact]
    public async Task ThePageSavesTheRealFileInEngineMode()
    {
        await using var fixture = await EngineFixture.StartAsync();
        using var composition = new AppComposition(new AppOptions { Demo = false }, new InlineDispatcher(), fixture.Time, new EngineAppServices(fixture.Engine, fixture.Host.Root, _ => null));
        var page = composition.Settings;

        Assert.IsType<EngineSettingsService>(composition.SettingsService);
        await page.EnsureLoadedAsync(Token);
        Assert.False(page.IsDemo);
        Assert.Contains("set for this run", page.Subtitle, StringComparison.Ordinal);
        var steps = page.Groups.SelectMany(group => group.Fields).Single(field => field.Definition.Key == "Turn:MaxSteps");
        steps.Value = "3";
        Assert.True(page.CanSave);
        await page.SaveCommand.ExecuteAsync();

        // The step budget is taken live: the notice says so, and it needs no restart.
        Assert.Contains("Applied now: steps per turn (in sessions opened from now on).", page.Notice, StringComparison.Ordinal);
        Assert.Equal(3, fixture.Runtime.Options.Turn.MaxSteps);
        Assert.Equal(3, (int)(await ReadAsync(fixture))["Corvid"]!["Turn"]!["MaxSteps"]!);
    }
}
