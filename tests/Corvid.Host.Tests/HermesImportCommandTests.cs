using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.Host.Tests;

/// <summary>Tests that set the process's <c>HERMES_HOME</c>, so they run alone.</summary>
[CollectionDefinition(nameof(HermesEnvironment), DisableParallelization = true)]
public sealed class HermesEnvironment;

/// <summary>Both Hermes imports resolve the same home (C-035): <c>HERMES_HOME</c>, then the sticky <c>active_profile</c>.</summary>
[Collection(nameof(HermesEnvironment))]
public sealed class HermesImportCommandTests
{
    private static ValueTask<CommandOutput> Run(TestHost host, CorvidRuntime runtime, CorvidSession session, string line)
    {
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        return commands.ExecuteAsync(line, new SlashCommandContext(runtime, session, commands), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Skills_and_memories_come_from_the_sticky_profile_under_hermes_home()
    {
        await using var host = TestHost.Create();
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        var root = Path.Combine(host.Root, "hermes-root");
        var coder = Path.Combine(root, "profiles", "coder");
        Directory.CreateDirectory(Path.Combine(coder, "skills", "devops", "deploy"));
        File.WriteAllText(Path.Combine(coder, "skills", "devops", "deploy", "SKILL.md"), "---\nname: deploy\ndescription: Deploy it.\n---\n1. Deploy.\n");
        Directory.CreateDirectory(Path.Combine(root, "skills", "other"));
        File.WriteAllText(Path.Combine(root, "skills", "other", "SKILL.md"), "---\nname: other\ndescription: The root's skill.\n---\n1. No.\n");
        Directory.CreateDirectory(Path.Combine(coder, "memories"));
        File.WriteAllText(Path.Combine(coder, "memories", "MEMORY.md"), "The coder profile builds with make.");
        File.WriteAllText(Path.Combine(root, "active_profile"), "coder\n");

        var previous = Environment.GetEnvironmentVariable("HERMES_HOME");
        Environment.SetEnvironmentVariable("HERMES_HOME", root);
        try
        {
            var skills = await Run(host, runtime, session, "/skills import hermes");
            Assert.Equal(OutputTone.Success, skills.Tone);
            Assert.Contains("hub:hermes", skills.Lines[0], StringComparison.Ordinal);
            Assert.Contains(": deploy.", skills.Lines[0], StringComparison.Ordinal);
            Assert.DoesNotContain("other", skills.Lines[0], StringComparison.Ordinal);

            var memories = await Run(host, runtime, session, "/memory import hermes --dry-run");
            Assert.Equal($"Hermes memories from {Path.Combine(coder, "memories")} (profile coder)", memories.Title);
            Assert.StartsWith("Dry run: 1 entry(ies)", memories.Lines[0], StringComparison.Ordinal);

            // A sticky profile with no directory is refused, not read from the root.
            File.WriteAllText(Path.Combine(root, "active_profile"), "writer");
            var refused = await Run(host, runtime, session, "/skills import hermes");
            Assert.Equal(OutputTone.Warning, refused.Tone);
            Assert.Contains("active profile is 'writer'", refused.Lines[0], StringComparison.Ordinal);
            Assert.EndsWith("Usage: /skills import hermes <hermes home>.", refused.Lines[0], StringComparison.Ordinal);

            // An explicit home wins over both.
            var explicitHome = await Run(host, runtime, session, $"/skills import hermes {root}");
            Assert.Contains(": other.", explicitHome.Lines[0], StringComparison.Ordinal);
            Assert.Equal(OutputTone.Error, (await Run(host, runtime, session, "/skills import hermes a b")).Tone);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HERMES_HOME", previous);
        }
    }
}
