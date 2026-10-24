using System.Text.Json.Nodes;
using Corvid.App.Core;
using Corvid.App.Core.Composition;
using Corvid.App.Core.FirstRun;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;
using Corvid.App.Core.Tests;
using Corvid.App.Engine.FirstRun;
using Corvid.Host.Tests;

namespace Corvid.App.Engine.Tests;

/// <summary>The first run over the test host: the doctor's checks, a home directory of its own, and the choices written to its corvid.json.</summary>
public sealed class EngineFirstRunTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "corvid-home-" + Guid.NewGuid().ToString("N"));

    public EngineFirstRunTests() => Directory.CreateDirectory(_home);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_home))
            Directory.Delete(_home, recursive: true);
    }

    private EngineFirstRunService Service(EngineFixture fixture) => new(fixture.Engine, new InlineDispatcher(), _home, _ => null);

    [Fact]
    public async Task TheChecksAreTheDoctorsGroupedAsThePageShowsThem()
    {
        await using var fixture = await EngineFixture.StartAsync();
        using var service = Service(fixture);

        var report = await service.RunChecksAsync(Token);

        var configuration = report.Checks.Where(check => check.Section == CheckSections.Configuration).ToList();
        Assert.Contains(configuration, check => check is { Name: "configuration", Status: CheckStatus.Ok });
        Assert.Contains(configuration, check => check.Name == "data directory" && check.Detail.Contains(Path.Combine(fixture.Host.Root, "data"), StringComparison.Ordinal));
        Assert.Contains(report.Checks, check => check is { Section: CheckSections.Sandbox, Name: "bubblewrap" });
        Assert.Contains(report.Checks, check => check is { Section: CheckSections.Sandbox, Name: "Vault socket" });
        Assert.Contains(report.Checks, check => check is { Section: CheckSections.Gpu, Name: "driver" });
        // The models are the Models step's, from the catalogue: none of them is a check row here.
        Assert.DoesNotContain(report.Checks, check => check.Name.StartsWith("fake-", StringComparison.Ordinal) || check.Name == "decider");
        Assert.All(report.Checks, check => Assert.Contains(check.Section, CheckSections.All));
        Assert.True(report.Build is "CUDA build" or "CPU build" or "", report.Build);
    }

    [Fact]
    public async Task TheHostsTextIsTextAndAFixThatIsACommandCanBeCopied()
    {
        await using var fixture = await EngineFixture.StartAsync();
        using var service = Service(fixture);
        // A configuration the validator refuses: the configuration check fails with the validator's words and its fix.
        await File.WriteAllTextAsync(Path.Combine(fixture.Host.Root, "corvid.json"), """{ "Corvid": { "Turn": { "MaxSteps": 99 } } }""", Token);

        var report = await service.RunChecksAsync(Token);

        var check = report.Checks.Single(item => item.Name == "configuration");
        Assert.Equal(CheckStatus.Failed, check.Status);
        Assert.Contains("Corvid:Turn:MaxSteps must be between 1 and 64.", check.Detail, StringComparison.Ordinal);
        Assert.Null(check.FixCommand);
        // A detail with the page's marks in it (a backtick, a tilde) keeps them as text.
        var malloc = report.Checks.Single(item => item.Name == Corvid.Host.Diagnostics.Doctor.MallocVariable);
        if (malloc.Status != CheckStatus.Ok)
        {
            Assert.Contains("\\`corvid chat\\`", malloc.Detail, StringComparison.Ordinal);
            Assert.Equal("export MALLOC_MMAP_THRESHOLD_=65536 for other hosts of the extractor or the decider", malloc.FixCommand);
        }
    }

    [Fact]
    public async Task TheConfiguredWorkspaceComesFirstThenTheGitRepositoriesInTheHome()
    {
        await using var fixture = await EngineFixture.StartAsync();
        using var service = Service(fixture);
        Directory.CreateDirectory(Path.Combine(_home, "src", "corvid", ".git"));
        Directory.CreateDirectory(Path.Combine(_home, "notes"));
        Directory.CreateDirectory(Path.Combine(_home, "site", ".git"));
        Directory.CreateDirectory(Path.Combine(_home, ".config", "corvid", ".git"));

        var suggestions = await service.SuggestWorkspacesAsync(Token);

        Assert.Equal([Path.Combine(fixture.Host.Root, "work"), "~/site", "~/src/corvid"], suggestions);
    }

    [Fact]
    public async Task AWorkspaceIsDescribedByWhatIsThere()
    {
        await using var fixture = await EngineFixture.StartAsync();
        using var service = Service(fixture);
        Directory.CreateDirectory(Path.Combine(_home, "src", "corvid", ".git"));
        Directory.CreateDirectory(Path.Combine(_home, "src", "corvid", "docs"));
        Directory.CreateDirectory(Path.Combine(_home, "notes"));

        Assert.StartsWith("A git repository", (await service.DescribeWorkspaceAsync("~/src/corvid/docs", Token)).Description, StringComparison.Ordinal);
        var notes = await service.DescribeWorkspaceAsync("~/notes", Token);
        Assert.Equal("~/notes", notes.Path);
        Assert.StartsWith("Not a git repository", notes.Description, StringComparison.Ordinal);
        Assert.StartsWith("There is no such folder", (await service.DescribeWorkspaceAsync("~/nowhere", Token)).Description, StringComparison.Ordinal);
        Assert.StartsWith("Not an absolute path", (await service.DescribeWorkspaceAsync("src/corvid", Token)).Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHermesHomeIsFoundWithWhatAnImportWouldRead()
    {
        await using var fixture = await EngineFixture.StartAsync();
        using var service = Service(fixture);
        Assert.Null(await service.FindHermesAsync(Token));

        var hermes = Path.Combine(_home, ".hermes");
        Directory.CreateDirectory(Path.Combine(hermes, "memories"));
        await File.WriteAllTextAsync(Path.Combine(hermes, "memories", "MEMORY.md"), "The user prefers tabs.\n§\nThe build uses make.", Token);
        await File.WriteAllTextAsync(Path.Combine(hermes, "memories", "USER.md"), "Name: Ada.", Token);
        Directory.CreateDirectory(Path.Combine(hermes, "skills", "coding", "review"));
        await File.WriteAllTextAsync(Path.Combine(hermes, "skills", "coding", "review", "SKILL.md"), "---\nname: review\n---\n", Token);

        var found = await service.FindHermesAsync(Token);

        Assert.Equal(new HermesImport("~/.hermes", Memories: 3, Skills: 1), found);
    }

    [Fact]
    public async Task SavingWritesTheChoicesToCorvidJson()
    {
        await using var fixture = await EngineFixture.StartAsync();
        using var service = Service(fixture);

        var workspace = Path.Combine(fixture.Host.Root, "work");
        await service.SaveAsync(new SetupChoices(workspace, ApprovalMode.Manual, "local only"), Token);

        var file = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Host.Root, "corvid.json"), Token))!;
        Assert.Equal(workspace, (string?)file["Corvid"]!["Workspace"]);
        Assert.Equal("Manual", (string?)file["Corvid"]!["Approvals"]!["Mode"]);
        Assert.Equal("Refuse", (string?)file["Corvid"]!["Privacy"]!["Cloud"]);

        await service.SaveAsync(new SetupChoices(workspace, ApprovalMode.Smart, "redact"), Token);
        file = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(fixture.Host.Root, "corvid.json"), Token))!;
        Assert.Equal("Redact", (string?)file["Corvid"]!["Privacy"]!["Cloud"]);
    }

    [Fact]
    public async Task AChoiceTheValidatorRefusesIsNotSavedAndSaysWhy()
    {
        await using var fixture = await EngineFixture.StartAsync();
        using var service = Service(fixture);

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveAsync(new SetupChoices("src/corvid", ApprovalMode.Smart, "redact"), Token));

        Assert.Contains("Corvid:Workspace must be an absolute path", refusal.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(fixture.Host.Root, "corvid.json")));
    }

    [Fact]
    public async Task TheEngineLineFollowsTheStart()
    {
        await using var fixture = await EngineFixture.StartAsync();
        using (var running = Service(fixture))
            Assert.Equal("in-process", running.EngineStatus);

        await using var host = TestHost.Create(new FakeModels(generatorReady: false));
        await using var engine = await AppEngine.StartAsync(host.Host, ownsHost: false, cancellationToken: Token);
        using var missing = new EngineFirstRunService(engine, new InlineDispatcher(), _home, _ => null);
        Assert.Equal("not started · 1 model missing", missing.EngineStatus);
    }

    [Fact]
    public async Task ThePageStartsOnTheConfiguredWorkspaceAndTheEnginesLine()
    {
        await using var host = TestHost.Create(new FakeModels(generatorReady: false));
        await using var engine = await AppEngine.StartAsync(host.Host, ownsHost: false, cancellationToken: Token);
        using var composition = new AppComposition(new AppOptions { Demo = false, Page = Area.FirstRun }, new InlineDispatcher(),
            ManualTimeProvider.Demo(), new EngineAppServices(engine));
        var page = composition.FirstRun;
        Assert.IsType<EngineFirstRunService>(composition.FirstRunService);

        await WaitForAsync(page, () => page.Sections.Count > 0 && page.Workspace.Length > 0);

        var models = composition.ModelService;
        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        models.Changed += (_, _) =>
        {
            if (models.Snapshot.IsLoaded)
                loaded.TrySetResult();
        };
        if (models.Snapshot.IsLoaded)
            loaded.TrySetResult();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);

        // The models step lists the catalogue's local models from the engine, the missing generator picked.
        Assert.Contains(page.Downloads.Items, item => item is { Name: "fake-gen", IsEnabled: true, IsSelected: true });
        Assert.Equal(Path.Combine(host.Root, "work"), page.Workspace);
        Assert.Equal("not started · 1 model missing", composition.StatusBar.Engine);
        Assert.False(page.HasNotice, string.Concat(page.Notice.Select(run => run.Text)));
    }

    /// <summary>Waits for work the page started on the pool to land, as its properties and lists announce it; never by the clock.</summary>
    private static async Task WaitForAsync(FirstRunViewModel page, Func<bool> condition)
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Check()
        {
            if (condition())
                reached.TrySetResult();
        }

        page.PropertyChanged += (_, _) => Check();
        page.Sections.CollectionChanged += (_, _) => Check();
        Check();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
    }
}
