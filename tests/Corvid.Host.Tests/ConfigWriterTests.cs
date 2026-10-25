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

    /// <summary>A3 ops: what a written key changes is read as the next start would see it, the run's overrides still on top.</summary>
    [Fact]
    public async Task TheNextStartReadsTheFileAsWrittenUnderThisRunsOverrides()
    {
        await using var host = TestHost.Create();
        var writer = Writer(host);
        Assert.Null(writer.NextStart()["Corvid:Mcp:Servers:notes:Trust"]);

        Assert.True(writer.Apply(ConfigEdit.Set("Corvid:Mcp:Servers:notes:Command", "notes-mcp"),
            ConfigEdit.Set("Corvid:Mcp:Servers:notes:Trust", "Trusted"), ConfigEdit.Set("Corvid:Workspace", host.Root)).Written);

        var next = writer.NextStart();
        Assert.Equal("Trusted", next["Corvid:Mcp:Servers:notes:Trust"]);
        // The run's own configuration never reloads, and an override still wins over the file.
        Assert.Null(host.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Corvid:Mcp:Servers:notes:Trust"]);
        Assert.Equal(Path.Combine(host.Root, "work"), next["Corvid:Workspace"]);
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

    [Fact]
    public async Task ARemoteModelTheHostDidNotStartWithIsValidatedToo()
    {
        await using var host = TestHost.Create();
        var writer = Writer(host);

        var refused = writer.Apply(ConfigEdit.Set("Corvid:Models:Remote:local:Provider", "nonsense"));
        Assert.False(refused.Written);
        Assert.Contains(refused.Failures, failure => failure.StartsWith("Corvid:Models:Remote:local:Provider must be one of", StringComparison.Ordinal));
        Assert.False(System.IO.File.Exists(File(host)));

        var server = new JsonObject { ["Provider"] = "openai-compatible", ["Endpoint"] = "http://127.0.0.1:11434/v1", ["Model"] = "qwen" };
        Assert.True(writer.Apply(new ConfigEdit("Corvid:Models:Remote:local", server)).Written);
    }

    /// <summary>A3 review M3: an edit changes only its keys' text; the person's comments, layout and key order stay.</summary>
    [Fact]
    public async Task AnEditKeepsTheFilesCommentsAndLayout()
    {
        await using var host = TestHost.Create();
        const string original = """
            {
              // the person's own notes
              "corvid": {
                "Turn": { "MaxSteps": 4 }, // four is enough
                "Privacy": { "Threshold": 0.5 },
                /* models below */
                "Models": {
                  "Slots": {
                    "summarizer": { "Model": "fake-gen" } // for now
                  }
                },
                "Devices": {
                  "CpuThreads": 2,
                  // keep this one
                  "Unused": 1,
                },
              },
            }
            """;
        await System.IO.File.WriteAllTextAsync(File(host), original, TestContext.Current.CancellationToken);

        var result = Writer(host).Apply(
            new ConfigEdit("Corvid:Turn:MaxSteps", JsonValue.Create(6)),
            new ConfigEdit("Corvid:Turn:MaxOutputTokens", JsonValue.Create(512)),
            ConfigEdit.Remove("Corvid:Devices:Unused"),
            ConfigEdit.Set("Corvid:Approvals:Mode", "Manual"),
            new ConfigEdit("Corvid:Privacy:ExtraLabels", new JsonArray("account")));

        Assert.True(result.Written, result.FailureText);
        Assert.Equal("""
            {
              // the person's own notes
              "corvid": {
                "Turn": { "MaxSteps": 6, "MaxOutputTokens": 512 }, // four is enough
                "Privacy": { "Threshold": 0.5, "ExtraLabels": ["account"] },
                /* models below */
                "Models": {
                  "Slots": {
                    "summarizer": { "Model": "fake-gen" } // for now
                  }
                },
                "Devices": {
                  "CpuThreads": 2
                  // keep this one
                },
                "Approvals": {
                  "Mode": "Manual"
                }
              },
            }
            """, await ReadAsync(host));
        var next = Writer(host).NextStart();
        Assert.Equal("6", next["Corvid:Turn:MaxSteps"]);
        Assert.Equal("account", next["Corvid:Privacy:ExtraLabels:0"]);
        Assert.Null(next["Corvid:Devices:Unused"]);
    }

    /// <summary>A3 review M3: a new file and the objects a key needs are written indented, one key a line.</summary>
    [Fact]
    public async Task ANewFileIsWrittenIndented()
    {
        await using var host = TestHost.Create();

        Assert.True(Writer(host).Apply(ConfigEdit.Set(Summarizer, "fake-gen")).Written);
        Assert.True(Writer(host).Apply(ConfigEdit.Set("Corvid:Approvals:Mode", "Manual"), ConfigEdit.Remove(Summarizer)).Written);

        Assert.Equal("""
            {
              "Corvid": {
                "Models": {
                  "Slots": {
                    "summarizer": {
                    }
                  }
                },
                "Approvals": {
                  "Mode": "Manual"
                }
              }
            }
            """, await ReadAsync(host));
    }

    /// <summary>A3 review M4: a server added and bound in one edit is judged by the edited file's own remote models.</summary>
    [Fact]
    public async Task AServerAddedAndBoundToASlotInOneEditIsWritten()
    {
        await using var host = TestHost.Create();
        var writer = Writer(host);
        var server = new JsonObject { ["Provider"] = "openai-compatible", ["Endpoint"] = "http://127.0.0.1:11434/v1", ["Model"] = "qwen" };

        var applied = writer.Apply(new ConfigEdit("Corvid:Models:Remote:local", server), ConfigEdit.Set(Summarizer, "local"));
        Assert.True(applied.Written, applied.FailureText);

        // The raw editor's whole file, the same way; and a slot naming a server the file no longer has is refused.
        const string replaced = """{ "Corvid": { "Models": { "Remote": { "other": { "Provider": "openai-compatible", "Endpoint": "http://127.0.0.1:8080/v1", "Model": "m" } }, "Slots": { "summarizer": { "Model": "other" } } } } }""";
        var replace = writer.Replace(replaced);
        Assert.True(replace.Written, replace.FailureText);
        var gone = writer.Apply(ConfigEdit.Remove("Corvid:Models:Remote:other"));
        Assert.False(gone.Written);
        Assert.Contains(gone.Failures, failure => failure.StartsWith("Corvid:Models:Slots:summarizer:Model: no model is named 'other'", StringComparison.Ordinal));
    }

    /// <summary>A3 review M4: a broken remote entry the host started with does not block the edit that fixes it.</summary>
    [Fact]
    public async Task ABrokenRemoteEntryCanBeFixed()
    {
        // The provider the host started with, built from the broken entry (the fakes replace the host's own providers).
        var started = new Models.RemoteModelProvider(
            new Dictionary<string, RemoteModelSettings> { ["local"] = new() { Provider = "nonsense" } }, vaultSocket: null);
        Assert.NotEmpty(started.Problems);
        await using var host = TestHost.Create(services: services => services.AddSingleton<Corvid.Host.Models.IModelProvider>(started));
        await System.IO.File.WriteAllTextAsync(File(host), """{ "Corvid": { "Models": { "Remote": { "local": { "Provider": "nonsense" } } } } }""",
            TestContext.Current.CancellationToken);
        var writer = Writer(host);
        Assert.False(writer.Apply(ConfigEdit.Set(Summarizer, "fake-gen")).Written);

        var fixedServer = new JsonObject { ["Provider"] = "openai-compatible", ["Endpoint"] = "http://127.0.0.1:11434/v1", ["Model"] = "qwen" };
        var result = writer.Apply(new ConfigEdit("Corvid:Models:Remote:local", fixedServer));

        Assert.True(result.Written, result.FailureText);
        Assert.Equal("openai-compatible", writer.NextStart()["Corvid:Models:Remote:local:Provider"]);
    }

    /// <summary>A3 review M4: a remote model cannot take an in-process model's name; the next start's catalogue would refuse it.</summary>
    [Fact]
    public async Task ARemoteModelCannotTakeAnInProcessModelsName()
    {
        await using var host = TestHost.Create();
        var server = new JsonObject { ["Provider"] = "openai-compatible", ["Endpoint"] = "http://127.0.0.1:11434/v1", ["Model"] = "qwen" };

        var result = Writer(host).Apply(new ConfigEdit("Corvid:Models:Remote:fake-gen", server));

        Assert.False(result.Written);
        Assert.Contains(result.Failures, failure => failure.StartsWith("Corvid:Models:Remote:fake-gen: an in-process model is named", StringComparison.Ordinal));
    }

    /// <summary>A3 review L5: the replacement keeps the file's permissions, and a new file is the owner's alone.</summary>
    [Fact]
    public async Task TheFileKeepsItsPermissions()
    {
        if (OperatingSystem.IsWindows())
            return;
        await using var host = TestHost.Create();
        Assert.True(Writer(host).Apply(ConfigEdit.Set(Summarizer, "fake-gen")).Written);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, System.IO.File.GetUnixFileMode(File(host)));

        var shared = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;
        System.IO.File.SetUnixFileMode(File(host), shared);
        Assert.True(Writer(host).Apply(ConfigEdit.Set("Corvid:Approvals:Mode", "Manual")).Written);

        Assert.Equal(shared, System.IO.File.GetUnixFileMode(File(host)));
    }

    /// <summary>A3 review L5: a corvid.json that is a symbolic link is written through to its target and stays a link.</summary>
    [Fact]
    public async Task ASymbolicLinkIsWrittenThroughAndStaysALink()
    {
        if (OperatingSystem.IsWindows())
            return;
        await using var host = TestHost.Create();
        var dotfiles = Directory.CreateDirectory(Path.Combine(host.Root, "dotfiles")).FullName;
        var target = Path.Combine(dotfiles, "corvid.json");
        await System.IO.File.WriteAllTextAsync(target, """{ "Corvid": { "Turn": { "MaxSteps": 4 } } }""", TestContext.Current.CancellationToken);
        System.IO.File.CreateSymbolicLink(File(host), target);

        var result = Writer(host).Apply(new ConfigEdit("Corvid:Turn:MaxSteps", JsonValue.Create(5)));

        Assert.True(result.Written, result.FailureText);
        Assert.Equal(target, new FileInfo(File(host)).LinkTarget);
        Assert.Equal("""{ "Corvid": { "Turn": { "MaxSteps": 5 } } }""", await System.IO.File.ReadAllTextAsync(target, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFiles(dotfiles, "*.tmp"));
    }

    /// <summary>A3 review L6: a second writer (another process) waits for the lock beside the file, and is refused past its wait.</summary>
    [Fact]
    public async Task AWriteWaitsForAnotherWritersLock()
    {
        await using var host = TestHost.Create();
        var writer = Writer(host);
        writer.LockTimeout = TimeSpan.Zero;
        Assert.True(writer.Apply(ConfigEdit.Set(Summarizer, "fake-gen")).Written);
        var before = await ReadAsync(host);

        // The lock another process's writer holds while it reads, validates and replaces the file.
        var lockPath = Path.Combine(host.Root, ".corvid.json.lock");
        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var refused = writer.Apply(ConfigEdit.Set("Corvid:Approvals:Mode", "Manual"));
            Assert.False(refused.Written);
            Assert.Contains("Another Corvid process is writing", refused.FailureText, StringComparison.Ordinal);
            Assert.Equal(before, await ReadAsync(host));
            // Checking writes nothing, so it needs no lock.
            Assert.True(writer.Check(ConfigEdit.Set("Corvid:Approvals:Mode", "Manual")).Failures.IsEmpty);
        }

        Assert.True(writer.Apply(ConfigEdit.Set("Corvid:Approvals:Mode", "Manual")).Written);
    }

    /// <summary>A3 leftover: a read-only configuration directory refuses at once and says so, rather than reading as another writer.</summary>
    [Fact]
    public async Task AReadOnlyConfigurationDirectoryIsReportedAsSuch()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
            return; // a read-only directory is a Unix mode here, and root writes through it
        await using var host = TestHost.Create();
        var writer = Writer(host);
        writer.LockTimeout = TimeSpan.FromMinutes(5);
        Assert.True(writer.Apply(ConfigEdit.Set(Summarizer, "fake-gen")).Written);
        var lockPath = Path.Combine(host.Root, ".corvid.json.lock");
        System.IO.File.Delete(lockPath);
        var mode = System.IO.File.GetUnixFileMode(host.Root);
        System.IO.File.SetUnixFileMode(host.Root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var refused = writer.Apply(ConfigEdit.Set("Corvid:Approvals:Mode", "Manual"));
            Assert.False(refused.Written);
            Assert.Contains("is read-only to Corvid", refused.FailureText, StringComparison.Ordinal);
            Assert.DoesNotContain("Another Corvid process", refused.FailureText, StringComparison.Ordinal);
        }
        finally
        {
            System.IO.File.SetUnixFileMode(host.Root, mode);
        }
    }

    /// <summary>A3 review L6: a file changed by something that takes no lock (an editor) after it was read is not overwritten.</summary>
    [Fact]
    public async Task AFileChangedDuringTheEditIsNotOverwritten()
    {
        await using var host = TestHost.Create();
        var writer = Writer(host);
        await System.IO.File.WriteAllTextAsync(File(host), """{ "Corvid": { "Turn": { "MaxSteps": 4 } } }""", TestContext.Current.CancellationToken);
        const string edited = """{ "Corvid": { "Turn": { "MaxSteps": 9 } } }""";
        writer.BeforeReplace = () => System.IO.File.WriteAllText(File(host), edited);

        var result = writer.Apply(ConfigEdit.Set("Corvid:Approvals:Mode", "Manual"));

        Assert.False(result.Written);
        Assert.Contains("changed while this edit was being made", result.FailureText, StringComparison.Ordinal);
        Assert.Equal(edited, await ReadAsync(host));
        AssertNoTemporaryFiles(host);
    }
}
