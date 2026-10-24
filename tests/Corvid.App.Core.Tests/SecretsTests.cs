using System.Reflection;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Secrets;

namespace Corvid.App.Core.Tests;

public sealed class SecretsTests
{
    private static string Text(IEnumerable<Models.TextRun> runs) => string.Concat(runs.Select(run => run.Text));

    private static SecretsViewModel ViewModel(ISecretsService service) => new(service, ManualTimeProvider.DemoLocal());

    [Fact]
    public async Task TheDemoListsVaultsSecretsByNameAndTheTwoFeaturesWaiting()
    {
        using var secrets = ViewModel(new DemoSecretsService(ManualTimeProvider.DemoLocal()));
        await secrets.Loaded;

        Assert.Equal("running", secrets.VaultTag);
        Assert.Equal(Tone.Green, secrets.VaultTone);
        Assert.Equal(["anthropic.key", "discord.bot-token", "github.token", "google.key", "notes.token", "ntfy.token", "telegram.bot-token"],
            secrets.Secrets.Select(row => row.Name));
        Assert.Equal("7 secrets · names only", secrets.SecretsNote);

        var anthropic = secrets.Secrets[0];
        Assert.Equal("api.anthropic.com", anthropic.Hosts);
        Assert.Equal(["POST auto"], anthropic.Methods.Select(tag => tag.Text));
        Assert.Equal("model-provider · role model-provider", anthropic.Use);
        Assert.Equal("7 of 300 this hour", anthropic.Budget);
        Assert.Equal("claude", anthropic.UsedBy);
        Assert.Equal("last used 14:03", anthropic.LastUsed);
        var github = secrets.Secrets[2];
        Assert.Equal(["GET auto", "POST ask", "PATCH ask", "PUT ask", "DELETE confirm"], github.Methods.Select(tag => tag.Text));
        Assert.Equal("nothing uses it", secrets.Secrets[3].UsedBy);

        Assert.Equal("2 features", secrets.NeedsNote);
        var gemini = secrets.Needs[0];
        Assert.Equal("model gemini", gemini.Feature);
        Assert.Equal("gemini.key", gemini.SecretName);
        Assert.Equal("corvid-vault set gemini.key", gemini.Command);
        Assert.Equal(NeedState.Missing, gemini.State);
        Assert.Equal("waits on gemini.key, which Vault does not have · it calls generativelanguage.googleapis.com", Text(gemini.Problem));
        Assert.Equal("gateway slack", secrets.Needs[1].Feature);
        Assert.Equal(8, secrets.Consumers.Count);
        Assert.Equal("ready", secrets.Consumers[0].State);
    }

    [Fact]
    public void NoSecretRecordCanHoldAValue()
    {
        foreach (var type in new[] { typeof(SecretInfo), typeof(SecretConsumer), typeof(SecretsSnapshot), typeof(SetterLaunch), typeof(VaultStatus), typeof(SecretRowView), typeof(BindChoiceView), typeof(SecretNeedView) })
        {
            Assert.DoesNotContain(type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                property => property.Name.Contains("Value", StringComparison.OrdinalIgnoreCase) || property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public async Task F17SettingAMissingSecretStartsNothingInDemoAndCheckAgainFindsIt()
    {
        var service = new DemoSecretsService(ManualTimeProvider.DemoLocal());
        using var secrets = ViewModel(service);
        await secrets.Loaded;
        var gemini = secrets.Needs[0];

        await gemini.SetCommand.ExecuteAsync();

        Assert.True(secrets.IsSetOpen);
        Assert.Equal("gemini.key", secrets.SetName);
        Assert.True(secrets.HasLaunch);
        Assert.False(secrets.Launch!.Launched);
        Assert.Equal("corvid-vault set gemini.key", secrets.LaunchCommand);
        Assert.StartsWith("Demo mode opens no terminal.", secrets.LaunchNote, StringComparison.Ordinal);
        Assert.Equal(["gemini.key"], service.Pending);

        await secrets.RefreshCommand.ExecuteAsync();

        Assert.False(secrets.IsSetOpen);
        Assert.Contains(secrets.Secrets, row => row.Name == "gemini.key" && row.UsedBy == "gemini");
        Assert.Equal(["gateway slack"], secrets.Needs.Select(need => need.Feature));
        Assert.Equal(Tone.Green, secrets.NoticeTone);
        Assert.Equal("gemini.key is in Vault now: model gemini can use it. Corvid never saw its value.", Text(secrets.Notice));
    }

    [Fact]
    public async Task F17BindingGeminiToAnExistingNameOffersOnlyNamesBoundToItsHost()
    {
        var service = new DemoSecretsService(ManualTimeProvider.DemoLocal());
        using var secrets = ViewModel(service);
        await secrets.Loaded;

        secrets.Needs[0].BindCommand.Execute(null);

        Assert.True(secrets.IsBindOpen);
        Assert.Equal("Bind model gemini to a secret", secrets.BindTitle);
        var google = secrets.BindChoices[0];
        Assert.Equal("google.key", google.Name);
        Assert.True(google.CanChoose);
        var github = Assert.Single(secrets.BindChoices, choice => choice.Name == "github.token");
        Assert.False(github.CanChoose);
        Assert.Equal("github.token is not bound to generativelanguage.googleapis.com; Vault would refuse to send it there.", github.Reason);

        await google.Choose.ExecuteAsync();

        Assert.False(secrets.IsPickerOpen);
        Assert.Equal([(DemoSecretsService.GeminiConsumerId, "google.key")], service.Bound);
        Assert.DoesNotContain(secrets.Needs, need => need.Feature == "model gemini");
        Assert.Equal("model gemini now uses google.key (written to corvid.json). Corvid never saw its value.", Text(secrets.Notice));
    }

    [Fact]
    public async Task AModelProviderKeyCannotServeAGatewayAdapter()
    {
        var secret = new SecretInfo("shared.key") { Hosts = ["slack.com"], Role = "model-provider" };
        var service = new DemoSecretsService(ManualTimeProvider.DemoLocal(), [secret]);
        using var secrets = ViewModel(service);
        await secrets.Loaded;

        secrets.Needs.Single(need => need.Feature == "gateway slack").BindCommand.Execute(null);
        var choice = Assert.Single(secrets.BindChoices, c => c.Name == "shared.key");
        Assert.False(choice.CanChoose);
        Assert.Equal("shared.key has the model-provider role: only a model slot may use it.", choice.Reason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BindAsync(DemoSecretsService.SlackConsumerId, "shared.key", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ANameWithHiddenCharactersIsCleanedInTheTableRevealedInThePickerAndNeverBound()
    {
        var hidden = "slack\u202E.bot-token\U000E0041";
        var service = new DemoSecretsService(ManualTimeProvider.DemoLocal(), [new SecretInfo(hidden) { Hosts = ["slack.com"], Description = "bot\u202E" }]);
        using var secrets = ViewModel(service);
        await secrets.Loaded;

        var row = Assert.Single(secrets.Secrets, r => r.HasHidden);
        Assert.Equal("slack.bot-token", row.Name);
        Assert.Equal("bot", row.Description);

        secrets.Needs.Single(need => need.Feature == "gateway slack").BindCommand.Execute(null);
        var choice = Assert.Single(secrets.BindChoices, c => c.HasHidden);
        Assert.Equal("slack⟨U+202E⟩.bot-token⟨U+E0041⟩", choice.Name);
        Assert.False(choice.CanChoose);
        Assert.False(choice.Choose.CanExecute(null));
        Assert.StartsWith("the name holds hidden characters", choice.Reason, StringComparison.Ordinal);
        await choice.Choose.ExecuteAsync();
        Assert.Empty(service.Bound);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BindAsync(DemoSecretsService.SlackConsumerId, hidden, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TheSetPickerRefusesANameWithHiddenCharactersAndShowsThem()
    {
        using var secrets = ViewModel(new DemoSecretsService(ManualTimeProvider.DemoLocal()));
        await secrets.Loaded;

        secrets.OpenSetCommand.Execute(null);
        Assert.Equal("corvid-vault set NAME", secrets.SetPreview);
        Assert.False(secrets.StartSetCommand.CanExecute(null));

        secrets.SetName = "gemini\u202E.key";
        Assert.False(secrets.SetNameIsValid);
        Assert.False(secrets.StartSetCommand.CanExecute(null));
        Assert.StartsWith("\"gemini⟨U+202E⟩.key\" holds hidden characters.", secrets.SetNameProblem, StringComparison.Ordinal);
        Assert.Equal("corvid-vault set gemini⟨U+202E⟩.key", secrets.SetPreview);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new DemoSecretsService(ManualTimeProvider.DemoLocal()).StartSetAsync("gemini\u202E.key", TestContext.Current.CancellationToken));

        secrets.SetName = "Has Space";
        Assert.Equal(SecretNames.Rule, secrets.SetNameProblem);
        secrets.SetName = "mistral.key";
        Assert.True(secrets.StartSetCommand.CanExecute(null));
        await secrets.StartSetCommand.ExecuteAsync();
        Assert.Equal("corvid-vault set mistral.key", secrets.LaunchCommand);

        secrets.ClosePickerCommand.Execute(null);
        Assert.False(secrets.IsPickerOpen);
    }

    [Fact]
    public async Task AFailedLoadAndARefusedBindingShowRedNotices()
    {
        var failing = new FailingService();
        using var secrets = ViewModel(failing);
        await secrets.Loaded;
        Assert.Equal(Tone.Red, secrets.NoticeTone);
        Assert.Equal("Vault could not be read: socket closed", Text(secrets.Notice));
    }

    [Fact]
    public async Task VaultAbsentMakesEveryFeatureWait()
    {
        using var secrets = ViewModel(new AbsentService());
        await secrets.Loaded;

        Assert.Equal("absent", secrets.VaultTag);
        Assert.Equal(Tone.Red, secrets.VaultTone);
        Assert.True(secrets.IsVaultAbsent);
        var need = Assert.Single(secrets.Needs);
        Assert.Equal("Vault does not answer", Text(need.Problem));
    }

    [Fact]
    public async Task TheCompositionMakesSecretsOnceAndDisposesIt()
    {
        Assert.True(AppOptions.TryParse(["--page", "secrets"], out var options, out var error), error);
        var time = ManualTimeProvider.DemoLocal();
        var app = new AppComposition(options, new InlineDispatcher(), time);
        Assert.Same(app.Secrets, app.Secrets);
        await app.Secrets.Loaded;
        Assert.Equal(Area.Secrets, app.Shell.CurrentArea);

        app.Dispose();
        Assert.Equal(0, time.ActiveTimers);
    }

    private sealed class FailingService : ISecretsService
    {
        public event EventHandler? Changed { add { } remove { } }

        public Task<SecretsSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<SecretsSnapshot>(new IOException("socket closed"));

        public Task<SetterLaunch> StartSetAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task BindAsync(string consumerId, string secretName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class AbsentService : ISecretsService
    {
        public event EventHandler? Changed { add { } remove { } }

        public Task<SecretsSnapshot> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SecretsSnapshot(new VaultStatus(VaultState.Absent), [],
                [new SecretConsumer("model:claude", ConsumerKind.Model, "claude", "generator", "anthropic.key", "api.anthropic.com")]));

        public Task<SetterLaunch> StartSetAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task BindAsync(string consumerId, string secretName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
