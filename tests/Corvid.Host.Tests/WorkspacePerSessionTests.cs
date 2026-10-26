using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Kernel.Checkpoints;
using Corvid.Models;

namespace Corvid.Host.Tests;

/// <summary>
/// A6 (G-4): a workspace per session. A session records the workspace it was created in, and its file tools, sandboxed
/// shell, approvals, checkpoints and the presentation of its calls follow it; the configured workspace stays the default.
/// </summary>
public sealed class WorkspacePerSessionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Dictionary<string, string?> FileTools = new()
    {
        ["Corvid:Tools:Builtin"] = "true",
        ["Corvid:Models:Slots:embedder.text:Model"] = "file-embed",
    };

    private static FakeModels Writes(params string[] contents)
    {
        var script = contents.SelectMany(content => new[]
        {
            Reply.Call("write_file", $$"""{"path":"notes.txt","content":"{{content}}"}"""),
            Reply.Say("Wrote it."),
        }).ToArray();
        var models = new FakeModels(new FakeGenerator(script));
        models.Provider.Add("file-embed", [ModelSlots.TextEmbedder], () => new KeywordEmbedder("write", "save"), context: 512, tools: ToolCallSupport.None);
        return models;
    }

    private sealed class Approve : IApprovalPrompter
    {
        public ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken) => ValueTask.FromResult(ApprovalAnswer.Approve());
    }

    private static string Other(TestHost host)
    {
        var other = Path.Combine(host.Root, "other");
        Directory.CreateDirectory(other);
        return other;
    }

    [Fact]
    public async Task A_session_in_another_workspace_writes_there_and_records_it()
    {
        await using var host = TestHost.Create(Writes("there", "here"), FileTools);
        var other = Other(host);
        await using var runtime = await host.StartAsync();

        var elsewhere = runtime.OpenSession(surface: "test", settings: new SessionSettings { Workspace = other });
        using (runtime.AttachPrompter(elsewhere.Id, new Approve()))
            await elsewhere.SendAsync("write the notes", Ct);
        var home = runtime.OpenSession(surface: "test");
        using (runtime.AttachPrompter(home.Id, new Approve()))
            await home.SendAsync("write the notes", Ct);

        Assert.Equal("there", await File.ReadAllTextAsync(Path.Combine(other, "notes.txt"), Ct));
        Assert.Equal("here", await File.ReadAllTextAsync(Path.Combine(host.Root, "work", "notes.txt"), Ct));
        var bound = Assert.IsType<WorkspaceBound>(runtime.Log.Read(elsewhere.Id, 0, long.MaxValue, 1, Ct)[0].Payload);
        Assert.Equal(other, bound.Path);
        Assert.Equal(new SessionWorkspace(other), runtime.WorkspaceOf(elsewhere.Id));
        Assert.True(runtime.WorkspaceOf(home.Id).IsDefault);
        Assert.DoesNotContain(runtime.Log.Read(home.Id, 0, long.MaxValue, 256, Ct), stored => stored.Payload is WorkspaceBound);
        // Approvals judge the session's paths against its own workspace; rules and prompts stay the runtime's.
        Assert.Equal(other, elsewhere.Workspace.Approvals.Environment.Workspace);
        Assert.Same(runtime.Approvals.Options, elsewhere.Workspace.Approvals.Options);
        Assert.Equal(other, elsewhere.Workspace.Shell.Options.Workspace);
        Assert.Same(runtime.DefaultWorkspace, home.Workspace);
        Assert.Equal([runtime.DefaultWorkspace.Root, other], runtime.OpenWorkspaces.Select(workspace => workspace.Root));
    }

    [Fact]
    public async Task Undo_puts_back_the_files_of_the_sessions_own_workspace()
    {
        await using var host = TestHost.Create(Writes("first", "second"), FileTools);
        var other = Other(host);
        await File.WriteAllTextAsync(Path.Combine(other, "notes.txt"), "original", Ct);
        await File.WriteAllTextAsync(Path.Combine(host.Root, "work", "notes.txt"), "untouched", Ct);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession(surface: "test", settings: new SessionSettings { Workspace = other });
        using (runtime.AttachPrompter(session.Id, new Approve()))
        {
            await session.SendAsync("write the notes", Ct);
            await session.SendAsync("write the notes again", Ct);
        }
        Assert.Equal("second", await File.ReadAllTextAsync(Path.Combine(other, "notes.txt"), Ct));
        Assert.Equal(2, session.Checkpoints().Length);
        Assert.NotSame(runtime.Checkpoints, session.Workspace.Checkpoints);

        var undone = await session.UndoAsync(Ct);

        Assert.Equal(RestoreStatus.Restored, undone!.Status);
        Assert.Equal("first", await File.ReadAllTextAsync(Path.Combine(other, "notes.txt"), Ct));
        Assert.Equal("untouched", await File.ReadAllTextAsync(Path.Combine(host.Root, "work", "notes.txt"), Ct));
        // The Rewind preview and the call's diffs read the same workspace.
        var first = session.Checkpoints()[0];
        var draft = await runtime.DraftRollbackToAsync(session.Id, first.Before.Checkpoint.CheckpointId, cancellationToken: Ct);
        Assert.True(draft!.CanRestore, draft.Detail);
        Assert.Contains("notes.txt", draft.Files.Select(file => file.Path));
        var replay = await SessionReplay.ReadAsync(runtime, session.Id, new ToolPresentation(runtime), cancellationToken: Ct);
        var written = replay.Updates.OfType<ToolCallChanged>().First();
        Assert.Equal(new FileDiff(Path.Combine(other, "notes.txt"), "original", "first"), Assert.Single(written.Diffs));
    }

    [Fact]
    public async Task A_session_keeps_its_workspace_when_opened_again()
    {
        await using var host = TestHost.Create(Writes("there"), FileTools);
        var other = Other(host);
        string id;
        await using (var runtime = await host.StartAsync())
        {
            id = runtime.OpenSession(surface: "test", settings: new SessionSettings { Workspace = other }).Id;
        }
        await using var again = await host.StartAsync();
        var session = again.OpenSession(id, surface: "test");
        Assert.Equal(other, session.Workspace.Root);
        // Asking for the same workspace is fine; another one is refused, and nothing is written.
        Assert.Same(session, again.OpenSession(id, surface: "test", settings: new SessionSettings { Workspace = other + "/" }));
        var head = again.Log.GetHead(id, Ct);
        var refused = Assert.Throws<WorkspaceUnavailableException>(() =>
            again.OpenSession(id, surface: "test", settings: new SessionSettings { Workspace = Path.Combine(host.Root, "work") }));
        Assert.Contains("keeps its workspace", refused.Message, StringComparison.Ordinal);
        Assert.Equal(head, again.Log.GetHead(id, Ct));
    }

    [Fact]
    public async Task A_workspace_must_be_an_existing_directory_outside_what_Corvid_protects()
    {
        await using var host = TestHost.Create(Writes(), FileTools);
        await using var runtime = await host.StartAsync();

        Assert.Contains("absolute path", Assert.Throws<WorkspaceUnavailableException>(() => runtime.CheckWorkspace("relative/dir")).Message,
            StringComparison.Ordinal);
        Assert.Contains("not a directory", Assert.Throws<WorkspaceUnavailableException>(() =>
            runtime.CheckWorkspace(Path.Combine(host.Root, "missing"))).Message, StringComparison.Ordinal);
        var data = Path.Combine(host.Root, "data");
        Directory.CreateDirectory(Path.Combine(data, "inside"));
        Assert.Contains("approvals protect", Assert.Throws<WorkspaceUnavailableException>(() =>
            runtime.OpenSession(surface: "test", settings: new SessionSettings { Workspace = Path.Combine(data, "inside") })).Message,
            StringComparison.Ordinal);
        // A link to a workspace is the same workspace.
        var other = Other(host);
        var link = Path.Combine(host.Root, "link");
        Directory.CreateSymbolicLink(link, other);
        Assert.Same(runtime.WorkspaceAt(other), runtime.WorkspaceAt(link));
        // A hidden character in a refused path is shown escaped.
        var refused = Assert.Throws<WorkspaceUnavailableException>(() => runtime.CheckWorkspace("/nowhere‮/x"));
        Assert.DoesNotContain("‮", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_whose_workspace_is_gone_is_refused_rather_than_moved()
    {
        await using var host = TestHost.Create(Writes(), FileTools);
        var other = Other(host);
        string id;
        await using (var runtime = await host.StartAsync())
        {
            id = runtime.OpenSession(surface: "test", settings: new SessionSettings { Workspace = other }).Id;
        }
        Directory.Delete(other);
        await using var again = await host.StartAsync();

        var refused = Assert.Throws<WorkspaceUnavailableException>(() => again.OpenSession(id, surface: "test"));

        Assert.Contains(other, refused.Message, StringComparison.Ordinal);
        Assert.Equal(new SessionWorkspace(other), again.WorkspaceOf(id));
    }

    [Fact]
    public async Task The_workspace_bound_tools_are_the_sessions_own_and_the_rest_are_shared()
    {
        await using var host = TestHost.Create(Writes(), FileTools);
        var other = Other(host);
        await using var runtime = await host.StartAsync();
        var session = runtime.OpenSession(surface: "test", settings: new SessionSettings { Workspace = other });

        Assert.Equal(runtime.Tools.List().Select(tool => tool.Name), session.Tools.List().Select(tool => tool.Name));
        Assert.True(session.Tools.TryGet("write_file", out var write));
        Assert.True(runtime.Tools.TryGet("write_file", out var configured));
        Assert.NotSame(configured, write);
        Assert.True(session.Tools.TryGet("web_fetch", out var fetch));
        Assert.True(runtime.Tools.TryGet("web_fetch", out var shared));
        Assert.Same(shared, fetch);
    }
}
