using Corvid.App.Core.Keys;
using Corvid.App.Core.Settings;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Tests;

/// <summary>
/// Settings (docs/plan/ui.md F24) over the demo's in-memory corvid.json: the validator's messages inline, what needs a
/// restart, values set for this run, the raw editor, keybindings, failures shown, and values with hidden characters.
/// </summary>
public sealed class SettingsTests
{
    private const string Override = "‮";
    private const string Tag = "\U000E0041";

    private static async Task<(SettingsViewModel Settings, ShellViewModel Shell, KeyMap Keys)> Open(ISettingsService? service = null)
    {
        var shell = new ShellViewModel(new StatusBarViewModel());
        var keys = new KeyMap();
        var settings = new SettingsViewModel(shell, service ?? new DemoSettingsService(), keys);
        await settings.LoadAsync(TestContext.Current.CancellationToken);
        return (settings, shell, keys);
    }

    private static SettingFieldViewModel Field(SettingsViewModel settings, string key) =>
        settings.Groups.SelectMany(group => group.Fields)
            .Concat(settings.AppearanceFields).Concat(settings.NotificationFields).Concat(settings.LayoutFields)
            .Single(field => field.Definition.Key == key);

    [Fact]
    public async Task TheDemoShowsItsInMemoryFileTheValuesAndWhatTheCommandLineSet()
    {
        var (settings, _, _) = await Open();

        Assert.True(settings.IsLoaded);
        Assert.True(settings.IsDemo);
        Assert.Contains("Nothing is written to disk", settings.DemoText, StringComparison.Ordinal);
        Assert.Equal("~/.config/corvid/corvid.json · 1 set for this run with --set", settings.Subtitle);
        Assert.Equal(["Workspace and data", "Approvals", "Turns", "Privacy", "Devices and tools", "Serve and telemetry"],
            settings.Groups.Select(group => group.Title));
        var steps = Field(settings, "Turn:MaxSteps");
        Assert.Equal("8", steps.Value);
        Assert.Equal("set for this run: 6", steps.RunOverrideText);
        Assert.Equal("Corvid:Turn:MaxSteps", steps.KeyText);
        Assert.Equal("next turn", steps.AppliesText);
        Assert.Equal("needs a restart", Field(settings, "Devices:CpuThreads").AppliesText);
        Assert.Equal("4", Field(settings, "Devices:CpuThreads").Value);
        Assert.True(Field(settings, "Approvals:Mode").Choices.Single(choice => choice.Label == "Smart").IsSelected);
        Assert.Equal("No changes", settings.ChangesText);
        Assert.False(settings.CanSave);
        Assert.Contains("\"Corvid\"", settings.JsonText, StringComparison.Ordinal);
        Assert.Equal("Valid · saved", settings.JsonStatus);
    }

    [Theory]
    [InlineData("eighty", "Corvid:Turn:MaxSteps must be a whole number, not 'eighty'.")]
    [InlineData("80", "Corvid:Turn:MaxSteps must be between 1 and 64.")]
    [InlineData("0", "Corvid:Turn:MaxSteps must be between 1 and 64.")]
    public async Task TheValidatorsMessageShowsUnderTheSettingAndBlocksSaving(string value, string message)
    {
        var (settings, _, _) = await Open();
        var steps = Field(settings, "Turn:MaxSteps");

        steps.Value = value;

        Assert.Equal(message, steps.Problem);
        Assert.True(steps.IsChanged);
        Assert.False(settings.CanSave);
        Assert.False(settings.SaveCommand.CanExecute(null));
        Assert.Equal("1 problem to fix before saving", settings.ChangesText);
        Assert.Equal(Tone.Red, settings.ChangesTone);

        steps.Value = "6";
        Assert.Equal("", steps.Problem);
        Assert.True(settings.CanSave);
        Assert.Equal("1 change", settings.ChangesText);
    }

    [Fact]
    public async Task SavingKeepsTheChangeInMemoryAndTagsWhatWaitsOnARestart()
    {
        var (settings, _, _) = await Open();
        var threads = Field(settings, "Devices:CpuThreads");
        threads.Value = "8";
        Field(settings, "Approvals:Mode").Choices.Single(choice => choice.Label == "Manual").Choose.Execute(null);
        Assert.Equal("2 changes · 1 needs a restart", settings.ChangesText);

        await settings.SaveCommand.ExecuteAsync();

        Assert.Equal(Tone.Green, settings.NoticeTone);
        Assert.Equal("Kept in the demo's in-memory copy of corvid.json; nothing was written to disk. Devices:CpuThreads takes effect after a restart.",
            settings.Notice);
        Assert.False(threads.IsChanged);
        Assert.Equal("restart to apply", threads.AppliesText);
        Assert.Equal("No changes", settings.ChangesText);
        Assert.Contains("\"CpuThreads\": 8", settings.JsonText, StringComparison.Ordinal);
        Assert.Contains("\"Mode\": \"Manual\"", settings.JsonText, StringComparison.Ordinal);

        // A fresh read of the service sees the saved values: the copy lives as long as the run.
        var again = await new SettingsViewModelLoader(settings).ReloadAsync();
        Assert.Equal("8", again["Devices:CpuThreads"]);
    }

    [Fact]
    public async Task RevertPutsBackTheSavedValues()
    {
        var (settings, _, _) = await Open();
        var timeout = Field(settings, "Approvals:TimeoutSeconds");
        timeout.Value = "90000";
        Assert.Equal("Corvid:Approvals:TimeoutSeconds must be between 1 and 86400.", timeout.Problem);

        settings.RevertCommand.Execute(null);

        Assert.Equal("300", timeout.Value);
        Assert.Equal("", timeout.Problem);
        Assert.Equal("No changes", settings.ChangesText);
    }

    [Fact]
    public async Task ANonLoopbackListenerNeedsATokenSecret()
    {
        var (settings, _, _) = await Open();
        var url = Field(settings, "Serve:Url");
        var token = Field(settings, "Serve:TokenSecret");

        url.Value = "http://0.0.0.0:8411";
        Assert.Equal("Corvid:Serve:Url http://0.0.0.0:8411 is not a loopback address: set Corvid:Serve:TokenSecret to a Vault secret, so remote callers must present it.",
            token.Problem);
        token.Value = "serve.token";
        Assert.Equal("", token.Problem);

        url.Value = "http://127.0.0.1:8411/api";
        Assert.StartsWith("Corvid:Serve:Url must be an http URL with no path", url.Problem, StringComparison.Ordinal);
        url.Value = "http://[::1]:8411";
        Assert.Equal("", url.Problem);
        Field(settings, "Telemetry:OtlpEndpoint").Value = "ftp://collector";
        Assert.Equal("Corvid:Telemetry:OtlpEndpoint must be an absolute http or https URI, not 'ftp://collector'.", Field(settings, "Telemetry:OtlpEndpoint").Problem);
        Field(settings, "Workspace").Value = "src/corvid";
        Assert.Equal("Corvid:Workspace must be an absolute path (or start with ~/), not 'src/corvid'.", Field(settings, "Workspace").Problem);
    }

    [Fact]
    public async Task AValueWithHiddenCharactersIsShownAndNeverSaved()
    {
        var (settings, _, _) = await Open();
        var token = Field(settings, "Serve:TokenSecret");

        token.Value = $"serve{Override}.token{Tag}";

        Assert.Equal("Corvid:Serve:TokenSecret holds hidden characters: serve⟨U+202E⟩.token⟨U+E0041⟩. Type it again.", token.Problem);
        Assert.False(settings.CanSave);
        token.Value = "serve.token";
        Assert.True(settings.CanSave);
    }

    [Fact]
    public async Task AFileWithHiddenCharactersShowsThemOnLoadAndCannotBeSavedAsIs()
    {
        var service = new DemoSettingsService();
        var (settings, _, _) = await Open(service);
        var json = settings.JsonText.Replace("\"~/src/corvid\"", $"\"~/src/corvid{Override}\"", StringComparison.Ordinal);

        settings.JsonText = json;

        Assert.False(settings.CanApplyJson);
        Assert.Contains(settings.JsonProblems, problem => problem.Contains("holds hidden characters: \"Workspace\": \"~/src/corvid⟨U+202E⟩\"", StringComparison.Ordinal));
        Assert.Contains(settings.JsonProblems, problem => problem.StartsWith("Corvid:Workspace holds hidden characters", StringComparison.Ordinal));
        Assert.Equal(Tone.Red, settings.JsonTone);
        var write = await service.SaveJsonAsync(json, TestContext.Current.CancellationToken);
        Assert.False(write.Written);
    }

    [Fact]
    public async Task TheRawEditorParsesAsYouTypeAndSavesIntoTheTypedPage()
    {
        var (settings, _, _) = await Open();

        settings.JsonText = "{ \"Corvid\": { \"Turn\": { \"MaxSteps\": 8 } ";
        Assert.False(settings.CanApplyJson);
        Assert.StartsWith("corvid.json does not parse at line 1", Assert.Single(settings.JsonProblems), StringComparison.Ordinal);

        settings.JsonText = "{ \"Corvid\": { \"Turn\": { \"MaxSteps\": 99 } } }";
        Assert.Equal("Corvid:Turn:MaxSteps must be between 1 and 64.", Assert.Single(settings.JsonProblems));
        Assert.False(settings.CanApplyJson);

        // A text box hands back carriage returns; they are line breaks, not hidden characters.
        settings.JsonText = "{\r  // a comment\r  \"Corvid\": { \"Devices\": { \"CpuThreads\": 12 }, \"Turn\": { \"MaxSteps\": 5, }, }\r}";
        Assert.Empty(settings.JsonProblems);
        Assert.True(settings.CanApplyJson);
        Assert.Equal("Valid · not saved", settings.JsonStatus);

        await settings.ApplyJsonCommand.ExecuteAsync();

        Assert.Equal(Tone.Green, settings.NoticeTone);
        Assert.Contains("Devices:CpuThreads", settings.Notice, StringComparison.Ordinal);
        Assert.Contains("after a restart", settings.Notice, StringComparison.Ordinal);
        Assert.Equal("5", Field(settings, "Turn:MaxSteps").Value);
        Assert.Equal("12", Field(settings, "Devices:CpuThreads").Value);
        Assert.Equal("restart to apply", Field(settings, "Devices:CpuThreads").AppliesText);
        // Settings the file no longer names fall back to their defaults.
        Assert.Equal("1024", Field(settings, "Turn:MaxOutputTokens").Value);
        Assert.Equal("Valid · saved", settings.JsonStatus);
    }

    [Fact]
    public async Task AFailedSaveIsShownInRedNotSwallowed()
    {
        var (settings, _, _) = await Open(new FailingSettingsService());
        Field(settings, "Turn:MaxSteps").Value = "6";

        await settings.SaveCommand.ExecuteAsync();

        Assert.Equal(Tone.Red, settings.NoticeTone);
        Assert.Equal("Not saved: the disk is full", settings.Notice);
        Assert.True(Field(settings, "Turn:MaxSteps").IsChanged);
        Assert.Null(settings.SaveCommand.Error);
    }

    [Fact]
    public async Task AFailedLoadIsShownInRed()
    {
        var shell = new ShellViewModel(new StatusBarViewModel());
        var settings = new SettingsViewModel(shell, new FailingSettingsService { FailLoad = true }, new KeyMap());

        await settings.EnsureLoadedAsync(TestContext.Current.CancellationToken);

        Assert.False(settings.IsLoaded);
        Assert.Equal(Tone.Red, settings.NoticeTone);
        Assert.Equal("Could not read the configuration: permission denied", settings.Notice);
    }

    [Fact]
    public async Task TheAppsOwnPreferencesSaveWithTheRest()
    {
        var (settings, _, _) = await Open();
        settings.SelectSection(SettingsCatalogue.Notifications);
        Assert.True(settings.IsNotifications);
        Assert.True(settings.ShowSaveBar);
        var sounds = Field(settings, "Sounds");
        Assert.False(sounds.IsOn);

        sounds.IsOn = true;
        await settings.SaveCommand.ExecuteAsync();

        Assert.True(sounds.IsOn);
        Assert.False(sounds.IsChanged);
        Assert.Equal("", sounds.KeyText);
        Assert.DoesNotContain("Sounds", settings.JsonText, StringComparison.Ordinal);
        settings.SelectSection("corvid.json");
        Assert.False(settings.ShowSaveBar);
    }

    [Fact]
    public async Task TheThemeAppliesAtOnceAndItsNoteStays()
    {
        var (settings, shell, _) = await Open();
        settings.SelectSection(SettingsCatalogue.Appearance);

        settings.Themes.Single(choice => choice.Label == "dark").Choose.Execute(null);

        Assert.Equal(AppTheme.Dark, shell.Theme);
        Assert.Equal(AppTheme.Dark, settings.Theme);
        Assert.True(settings.Themes.Single(choice => choice.Label == "dark").IsSelected);
    }

    [Fact]
    public async Task KeybindingsAreRemappedWithConflictsRefusedAndShadowsNamed()
    {
        var (settings, _, keys) = await Open();
        var rows = settings.KeyGroups.SelectMany(group => group.Rows).ToList();
        var history = rows.Single(row => row.Id == KeyCommands.History);
        var find = rows.Single(row => row.Id == KeyCommands.Find);
        Assert.Equal("Anywhere", settings.KeyGroups[0].Heading);
        Assert.Equal("In Audit, Ctrl+F puts the caret in the path or tool filter instead. In Memory, Ctrl+F searches the facts instead.", find.ShadowText);
        Assert.True(rows.Single(row => row.Id == "composer.send").IsFixed);
        Assert.Equal("comes with the scratch fork (F7)", rows.Single(row => row.Id == KeyCommands.Aside).ComesWith);

        history.EditText = "Ctrl+B";
        history.Apply();
        Assert.Equal("Ctrl+B already shows or hides the dock; change that one first.", history.Problem);
        Assert.Equal("Ctrl+R", keys[KeyCommands.History].KeyText);

        history.EditText = "ctrl+shift+h";
        history.Apply();
        Assert.Equal("", history.Problem);
        Assert.Equal("Ctrl+Shift+H", history.EditText);
        Assert.True(history.IsChanged);
        Assert.Contains("\"Ctrl+Shift+H\": \"history\"", settings.KeysJson, StringComparison.Ordinal);
        Assert.True(settings.ResetKeysCommand.CanExecute(null));

        settings.ResetKeysCommand.Execute(null);
        Assert.Equal("Ctrl+R", history.EditText);
        Assert.False(history.IsChanged);
    }

    /// <summary>Reads the service again through a second view model, as a page opened later would.</summary>
    private sealed class SettingsViewModelLoader(SettingsViewModel settings)
    {
        public async Task<IReadOnlyDictionary<string, string>> ReloadAsync()
        {
            await settings.LoadAsync(TestContext.Current.CancellationToken);
            return settings.Groups.SelectMany(group => group.Fields).ToDictionary(field => field.Definition.Key, field => field.Value);
        }
    }

    private sealed class FailingSettingsService : ISettingsService
    {
        private readonly DemoSettingsService _demo = new();

        public bool FailLoad { get; init; }

        public IReadOnlyList<SettingDefinition> Definitions => _demo.Definitions;

        public Task<SettingsSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            FailLoad ? Task.FromException<SettingsSnapshot>(new UnauthorizedAccessException("permission denied")) : _demo.LoadAsync(cancellationToken);

        public IReadOnlyList<SettingProblem> Validate(IReadOnlyDictionary<string, string> values) => _demo.Validate(values);

        public SettingsParse Parse(string json) => _demo.Parse(json);

        public Task<SettingsWrite> SaveAsync(IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken = default) =>
            Task.FromException<SettingsWrite>(new IOException("the disk is full"));

        public Task<SettingsWrite> SaveJsonAsync(string json, CancellationToken cancellationToken = default) =>
            Task.FromException<SettingsWrite>(new IOException("the disk is full"));
    }
}
