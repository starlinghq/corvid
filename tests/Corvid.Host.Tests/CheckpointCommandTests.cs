using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Host.Diagnostics;
using Corvid.Kernel.Checkpoints;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Corvid.Host.Tests;

/// <summary>C-014 in the host: the store under the data directory, <c>/undo</c>, <c>/rollback</c> and the doctor's git check.</summary>
public sealed class CheckpointCommandTests
{
    private static readonly Dictionary<string, string?> BuiltinOn = new() { ["Corvid:Tools:Builtin"] = "true" };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ValueTask<CommandOutput> Run(TestHost host, CorvidRuntime runtime, CorvidSession session, string line)
    {
        var commands = host.Services.GetRequiredService<SlashCommandRegistry>();
        return commands.ExecuteAsync(line, new SlashCommandContext(runtime, session, commands), Ct);
    }

    private static void Append(CorvidRuntime runtime, CorvidSession session, params SessionEvent[] events) =>
        runtime.Log.Append(session.Id, runtime.Log.GetHead(session.Id, Ct), [.. events.Select(EventData.Create)], Ct);

    /// <summary>One turn as the loop records a write: the call, the ledger's before and after checkpoints, the result.</summary>
    private static async Task WriteTurnAsync(CorvidRuntime runtime, CorvidSession session, string callId, string path, string content)
    {
        Append(runtime, session, new UserMessage($"write {path}"),
            new ToolCallIssued(callId, "write_file", JsonSerializer.SerializeToElement(new { path, content })));
        var analysis = runtime.Approvals.Analyze(new ShellAction("rm " + path));
        var before = await runtime.Checkpoints!.BeforeAsync(callId, analysis);
        Append(runtime, session, before);
        await File.WriteAllTextAsync(Path.Combine(runtime.Builtin.Environment.Workspace, path), content, Ct);
        Append(runtime, session, await runtime.Checkpoints.AfterAsync(callId, before, analysis),
            new ToolResult(callId, JsonSerializer.SerializeToElement(new { path })), new AssistantMessage("Written."));
    }

    [Fact]
    public async Task TheStoreLivesUnderTheDataDirectoryAndLeavesCorvidsOwnStateOut()
    {
        await using var host = TestHost.Create(settings: new Dictionary<string, string?>
        {
            ["Corvid:Tools:Builtin"] = "true",
            ["Corvid:DataDirectory"] = "{root}/work/state",
        });
        await using var runtime = await host.StartAsync();

        var store = runtime.Checkpoints!.Store;
        Assert.True(store.IsAvailable);
        Assert.StartsWith(Path.Combine(host.Root, "work", "state", "checkpoints"), store.Directory, StringComparison.Ordinal);
        Assert.True(store.Excluded("state/events.db"));
        Assert.True(store.Excluded(".env"));
        Assert.False(store.Excluded("src/main.c"));
    }

    [Fact]
    public async Task UndoPutsTheFilesBackAndRollbackListsAndRestoresCheckpoints()
    {
        await using var host = TestHost.Create(settings: BuiltinOn);
        var work = Path.Combine(host.Root, "work");
        await File.WriteAllTextAsync(Path.Combine(work, "a.txt"), "original\n", Ct);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        await WriteTurnAsync(runtime, session, "call-1", "a.txt", "first\n");
        await WriteTurnAsync(runtime, session, "call-2", "b.txt", "second\n");
        await WriteTurnAsync(runtime, session, "call-3", "a.txt", "third\n");

        var listed = await Run(host, runtime, session, "/rollback");
        Assert.Equal([["1", "call-3", "write_file", "a.txt"], ["2", "call-2", "write_file", "b.txt"], ["3", "call-1", "write_file", "a.txt"]],
            listed.Table!.Rows.Select(row => row.ToArray()));

        var undone = await Run(host, runtime, session, "/undo");
        Assert.Equal(OutputTone.Success, undone.Tone);
        Assert.Contains("a.txt", undone.Lines[0], StringComparison.Ordinal);
        Assert.Equal("first\n", await File.ReadAllTextAsync(Path.Combine(work, "a.txt"), Ct));
        Assert.Contains("write b.txt", undone.Lines[^2], StringComparison.Ordinal);

        var rolled = await Run(host, runtime, session, "/rollback 2");
        Assert.Equal(OutputTone.Success, rolled.Tone);
        Assert.Equal("original\n", await File.ReadAllTextAsync(Path.Combine(work, "a.txt"), Ct));
        Assert.False(File.Exists(Path.Combine(work, "b.txt")));
        Assert.Empty(session.Checkpoints());
        Assert.Equal(OutputTone.Error, (await Run(host, runtime, session, "/rollback 1")).Tone);
        Assert.Equal(OutputTone.Error, (await Run(host, runtime, session, "/rollback two")).Tone);
    }

    [Fact]
    public async Task AnUndoThatWouldLoseTheUsersEditIsRefusedAndListsThePath()
    {
        await using var host = TestHost.Create(settings: BuiltinOn);
        var work = Path.Combine(host.Root, "work");
        await File.WriteAllTextAsync(Path.Combine(work, "a.txt"), "original\n", Ct);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        await WriteTurnAsync(runtime, session, "call-1", "a.txt", "corvid\n");
        await File.WriteAllTextAsync(Path.Combine(work, "a.txt"), "the user's\n", Ct);

        var output = await Run(host, runtime, session, "/undo");

        Assert.Equal(OutputTone.Error, output.Tone);
        Assert.StartsWith("Nothing was changed", output.Lines[0], StringComparison.Ordinal);
        Assert.Equal("a.txt", Assert.Single(output.Table!.Rows)[0]);
        Assert.Equal("the user's\n", await File.ReadAllTextAsync(Path.Combine(work, "a.txt"), Ct));
        Assert.Contains(session.Snapshot().Transcript, entry => entry.Content is UserMessage { Text: "write a.txt" });
    }

    [Fact]
    public async Task WithCheckpointsOffUndoMovesOnlyTheConversationAndRefusesWhenATurnChangedFiles()
    {
        await using var host = TestHost.Create(settings: new Dictionary<string, string?>
        {
            ["Corvid:Tools:Builtin"] = "true",
            ["Corvid:Checkpoints:Enabled"] = "false",
        });
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();
        Assert.Null(runtime.Checkpoints);
        Assert.Equal("Nothing to undo: the conversation has no turn left.", Assert.Single((await Run(host, runtime, session, "/undo")).Lines));

        Append(runtime, session, new UserMessage("hello"), new AssistantMessage("Hi."));
        Assert.Equal(OutputTone.Success, (await Run(host, runtime, session, "/undo")).Tone);
        Assert.Empty(session.Snapshot().Transcript);

        Append(runtime, session, new UserMessage("write it"),
            new ToolCallIssued("call-1", "write_file", JsonSerializer.SerializeToElement(new { path = "x.txt", content = "x" })),
            new ToolResult("call-1", JsonSerializer.SerializeToElement(new { path = "x.txt" })), new AssistantMessage("Written."));
        var refused = await Run(host, runtime, session, "/undo");
        Assert.Equal(OutputTone.Error, refused.Tone);
        Assert.Contains("without a checkpoint", refused.Lines[0], StringComparison.Ordinal);
        Assert.Equal(4, session.Snapshot().Transcript.Length);
    }

    [Fact]
    public async Task TheDoctorReportsGitAndAMissingGitIsAWarningWithTheFix()
    {
        await using (var host = TestHost.Create())
        {
            var checks = await host.Services.GetRequiredService<Doctor>().RunAsync(cancellationToken: Ct);
            var git = Assert.Single(checks, check => check.Area == "checkpoints");
            Assert.Equal(CheckStatus.Ok, git.Status);
        }
        await using (var host = TestHost.Create(settings: new Dictionary<string, string?> { ["Corvid:Checkpoints:GitPath"] = "/nonexistent/git" }))
        {
            var git = Assert.Single(await host.Services.GetRequiredService<Doctor>().RunAsync(cancellationToken: Ct), check => check.Area == "checkpoints");
            Assert.Equal(CheckStatus.Warning, git.Status);
            Assert.Contains("not reversible", git.Detail, StringComparison.Ordinal);
            Assert.Contains("Corvid:Checkpoints:GitPath", git.Fix, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WithoutGitTheRuntimeStartsAndRollbackSaysWhy()
    {
        await using var host = TestHost.Create(settings: new Dictionary<string, string?>
        {
            ["Corvid:Tools:Builtin"] = "true",
            ["Corvid:Checkpoints:GitPath"] = "/nonexistent/git",
        });
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession();

        Assert.False(runtime.Checkpoints!.Store.IsAvailable);
        var output = await Run(host, runtime, session, "/rollback");
        Assert.Contains("git was not found at /nonexistent/git", Assert.Single(output.Lines), StringComparison.Ordinal);
    }

    [Fact]
    public void CheckpointSettingsAreValidated()
    {
        var validator = new CorvidOptionsValidator(new Corvid.Host.Models.ModelCatalogue([new FakeModels().Provider]));
        var options = new CorvidOptions();
        foreach (var (slot, model) in new[] { ("generator", "fake-gen"), ("embedder.text", "fake-embed") })
            options.Models.Slots[slot] = new SlotSettings { Model = model };
        options.Checkpoints = new CheckpointSettings { GitPath = "bin/git", MaxFileBytes = 0, MaxFiles = -1 };

        var result = validator.Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("Checkpoints:GitPath", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("Checkpoints:MaxFileBytes", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, failure => failure.Contains("Checkpoints:MaxFiles", StringComparison.Ordinal));
    }
}
