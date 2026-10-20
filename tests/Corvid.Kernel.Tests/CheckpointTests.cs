using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Approvals;
using Corvid.Kernel.Checkpoints;
using Corvid.Kernel.Events;
using Corvid.Kernel.Turns;
using Corvid.Tools.Builtin;

namespace Corvid.Kernel.Tests;

/// <summary>A workspace and a checkpoint store root in their own temporary directories, removed on dispose.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class CheckpointFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "corvid-checkpoints-" + Guid.NewGuid().ToString("N"));

    public CheckpointFixture(Func<CheckpointOptions, CheckpointOptions>? configure = null)
    {
        Workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        StoreRoot = Path.Combine(_root, "data", "checkpoints");
        Options = new CheckpointOptions
        {
            Workspace = Workspace,
            StoreRoot = StoreRoot,
            IsExcludedName = name => Corvid.Sandbox.BuiltInDenials.IsEnvFileName(name) || ProtectedPaths.IsCredentialFileName(name),
        };
        if (configure is not null)
            Options = configure(Options);
        Store = new ShadowStore(Options);
        Service = new CheckpointService(Store);
    }

    public string Workspace { get; }

    public string StoreRoot { get; }

    public CheckpointOptions Options { get; }

    public ShadowStore Store { get; }

    public CheckpointService Service { get; }

    public string PathOf(string relative) => Path.Combine(Workspace, relative);

    public void Write(string relative, string text) => Write(relative, Encoding.UTF8.GetBytes(text));

    public void Write(string relative, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf(relative))!);
        File.WriteAllBytes(PathOf(relative), bytes);
    }

    public string Read(string relative) => File.ReadAllText(PathOf(relative));

    /// <summary>Runs the real git as a user would, with no global or system configuration of the machine running the tests.</summary>
    public static string Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Remove("GIT_DIR");
        start.Environment.Remove("GIT_WORK_TREE");
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        foreach (var argument in new[] { "-c", "user.name=User", "-c", "user.email=user@example.com", "-c", "init.defaultBranch=main" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
        return output;
    }

    /// <summary>Every file under a directory with its SHA-256, for proving nothing in it changed.</summary>
    public static SortedDictionary<string, string> Fingerprint(string directory) => new(
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).ToDictionary(
            file => Path.GetRelativePath(directory, file), file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))),
        StringComparer.Ordinal);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

/// <summary>The shadow store (C-014): snapshots outside the workspace, byte for byte, never touching the user's own git.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ShadowStoreTests
{
    [Fact]
    public async Task The_store_lives_outside_the_workspace_and_holds_files_byte_for_byte()
    {
        using var fixture = new CheckpointFixture();
        var binary = RandomNumberGenerator.GetBytes(512 * 1024);
        binary[10] = 0;
        fixture.Write("notes/plan.md", "line one\r\nline two\n");
        fixture.Write("bin/blob.dat", binary);
        fixture.Write("run.sh", "#!/bin/sh\necho hi\n");
        File.SetUnixFileMode(fixture.PathOf("run.sh"), (UnixFileMode)0b111_101_101);
        File.CreateSymbolicLink(fixture.PathOf("link"), "notes/plan.md");

        var commit = await fixture.Store.SnapshotAsync("test");
        var files = await fixture.Store.ReadAsync(commit);

        Assert.StartsWith(fixture.StoreRoot, fixture.Store.Directory, StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.PathOf(".git")));
        Assert.Equal(["bin/blob.dat", "link", "notes/plan.md", "run.sh"], files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(binary, await fixture.Store.ReadBlobAsync(files["bin/blob.dat"].Blob));
        Assert.Equal("line one\r\nline two\n"u8.ToArray(), await fixture.Store.ReadBlobAsync(files["notes/plan.md"].Blob));
        Assert.True(files["run.sh"].IsExecutable);
        Assert.True(files["link"].IsLink);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(fixture.Store.Directory));
    }

    [Fact]
    public async Task Secrets_protected_paths_nested_repositories_and_oversized_files_never_enter_the_store()
    {
        using var protectedFixture = new CheckpointFixture(options => options with
        {
            MaxFileBytes = 1024,
            ExcludedPaths = [Path.Combine(options.Workspace, "corvid-data")],
        });
        protectedFixture.Write(".env", "TOKEN=secret");
        protectedFixture.Write("config/.env.local", "TOKEN=secret");
        protectedFixture.Write("keys/server.pem", "-----BEGIN PRIVATE KEY-----");
        protectedFixture.Write("corvid-data/events.db", "log");
        protectedFixture.Write("big.bin", new byte[4096]);
        protectedFixture.Write("kept.txt", "kept");
        Directory.CreateDirectory(protectedFixture.PathOf("vendor/lib"));
        CheckpointFixture.Git(protectedFixture.PathOf("vendor/lib"), "init", "-q");
        protectedFixture.Write("vendor/lib/code.c", "int main;");

        var files = await protectedFixture.Store.ReadAsync(await protectedFixture.Store.SnapshotAsync("test"));

        Assert.Equal(["kept.txt"], files.Keys);
        Assert.Contains("/corvid-data", File.ReadAllText(Path.Combine(protectedFixture.Store.Directory, "git", "info", "exclude")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_file_that_grows_past_the_limit_leaves_the_checkpoint_instead_of_keeping_a_stale_copy()
    {
        using var fixture = new CheckpointFixture(options => options with { MaxFileBytes = 1024 });
        fixture.Write("data.bin", new byte[100]);
        var first = await fixture.Store.ReadAsync(await fixture.Store.SnapshotAsync("small"));
        fixture.Write("data.bin", new byte[2048]);
        var second = await fixture.Store.ReadAsync(await fixture.Store.SnapshotAsync("large"));

        Assert.Contains("data.bin", first.Keys);
        Assert.DoesNotContain("data.bin", second.Keys);
    }

    [Fact]
    public async Task A_workspace_that_is_a_git_repository_keeps_its_own_git_untouched_and_its_hooks_never_run()
    {
        using var fixture = new CheckpointFixture();
        CheckpointFixture.Git(fixture.Workspace, "init", "-q");
        fixture.Write("README.md", "# project\n");
        // Attributes that would convert line endings on checkout, and hooks that would leave a mark if git ran them.
        fixture.Write(".gitattributes", "* text eol=crlf\n");
        fixture.Write("unix.txt", "lf only\n");
        CheckpointFixture.Git(fixture.Workspace, "add", "-A");
        CheckpointFixture.Git(fixture.Workspace, "commit", "-q", "-m", "first");
        var status = CheckpointFixture.Git(fixture.Workspace, "status", "--porcelain");
        var marker = Path.Combine(fixture.Workspace, "..", "hook-ran");
        var hooks = new[] { "post-checkout", "pre-commit", "post-commit", "post-index-change", "reference-transaction" }
            .Select(hook => Path.Combine(fixture.Workspace, ".git", "hooks", hook)).ToList();
        foreach (var hook in hooks)
        {
            File.WriteAllText(hook, $"#!/bin/sh\ntouch '{marker}'\n");
            File.SetUnixFileMode(hook, (UnixFileMode)0b111_101_101);
        }
        CheckpointFixture.Git(fixture.Workspace, "config", "core.fsmonitor", "touch '" + marker + "'");
        var userGit = CheckpointFixture.Fingerprint(Path.Combine(fixture.Workspace, ".git"));

        var before = await fixture.Service.BeforeAsync("call-1", Analysis(fixture, "unix.txt"));
        fixture.Write("unix.txt", "changed by corvid\n");
        var after = await fixture.Service.AfterAsync("call-1", before, Analysis(fixture, "unix.txt"));
        var files = await fixture.Store.ReadAsync(before.Revision);

        Assert.Equal(["unix.txt"], after.Paths!.Value);
        Assert.DoesNotContain(files.Keys, path => path.StartsWith(".git/", StringComparison.Ordinal));
        Assert.Equal("lf only\n"u8.ToArray(), await fixture.Store.ReadBlobAsync(files["unix.txt"].Blob));
        Assert.False(File.Exists(marker));
        Assert.Equal(userGit, CheckpointFixture.Fingerprint(Path.Combine(fixture.Workspace, ".git")));
        // The user's own git (its hooks removed first, since git itself would run them) sees the same repository.
        hooks.ForEach(File.Delete);
        CheckpointFixture.Git(fixture.Workspace, "config", "--unset", "core.fsmonitor");
        fixture.Write("unix.txt", "lf only\n");
        Assert.Equal(status, CheckpointFixture.Git(fixture.Workspace, "status", "--porcelain"));
    }

    [Fact]
    public async Task A_workspace_named_through_a_link_is_checked_and_captured_like_its_real_path()
    {
        using var real = new CheckpointFixture();
        var link = Path.Combine(Path.GetDirectoryName(real.Workspace)!, "linked-workspace");
        Directory.CreateSymbolicLink(link, real.Workspace);
        var linked = new LinkedFixture(real, link, options => options with { MaxFileBytes = 1024 });
        real.Write(".gitignore", "*.log\n");
        real.Write("big.bin", new byte[4096]);
        real.Write("debug.log", "ignored, but about to be written\n");

        // The approval pipeline resolves targets against the workspace as configured: the link.
        Assert.False(await linked.Service.IsReversibleAsync(Analyze(linked.Workspace, new ShellAction("rm big.bin"))));
        Assert.True(await linked.Service.IsReversibleAsync(Analyze(linked.Workspace, new ShellAction("rm notes.txt"))));
        var before = await linked.Service.BeforeAsync("call-1", Analyze(linked.Workspace, new ShellAction("rm debug.log")));
        Assert.Contains("debug.log", (await linked.Store.ReadAsync(before.Revision)).Keys);
        Assert.Equal(real.Workspace, linked.Store.Workspace);
    }

    /// <summary>A store whose configured workspace is a link to another fixture's workspace.</summary>
    private sealed class LinkedFixture
    {
        public LinkedFixture(CheckpointFixture real, string link, Func<CheckpointOptions, CheckpointOptions> configure)
        {
            Workspace = link;
            Store = new ShadowStore(configure(real.Options with { Workspace = link }));
            Service = new CheckpointService(Store);
        }

        public string Workspace { get; }

        public ShadowStore Store { get; }

        public CheckpointService Service { get; }
    }

    [Fact]
    public async Task Without_git_the_store_is_unavailable_and_says_why()
    {
        using var fixture = new CheckpointFixture(options => options with { GitPath = "/nonexistent/bin/git" });

        Assert.False(fixture.Store.IsAvailable);
        Assert.Equal("git was not found at /nonexistent/bin/git", fixture.Store.UnavailableReason);
        var refused = await Assert.ThrowsAsync<CheckpointException>(() => fixture.Service.BeforeAsync("call-1", Analysis(fixture, "a.txt")));
        Assert.Contains("git was not found", refused.Message, StringComparison.Ordinal);
        Assert.False(await fixture.Service.IsReversibleAsync(Analysis(fixture, "a.txt")));
        Assert.False(Directory.Exists(fixture.StoreRoot));
    }

    [Fact]
    public void Git_missing_from_path_is_reported_as_such()
    {
        Assert.Null(GitRunner.Resolve("git", "/nonexistent:/also-missing"));
        Assert.Null(GitRunner.Resolve("git", null));
        Assert.Null(GitRunner.Resolve("/etc/hostname", null));
    }

    [Fact]
    public async Task Reversibility_follows_what_a_checkpoint_can_take_back()
    {
        using var fixture = new CheckpointFixture(options => options with { MaxFileBytes = 1024 });
        fixture.Write(".gitignore", "build/\n");
        fixture.Write("build/out.o", "obj");
        fixture.Write("big.bin", new byte[4096]);
        Task<bool> Reversible(string command) => fixture.Service.IsReversibleAsync(Analyze(fixture, new ShellAction(command)));

        Assert.True(await Reversible("rm notes.txt"));
        Assert.True(await Reversible("rm -rf src"));
        Assert.True(await Reversible("ls"));
        Assert.False(await Reversible("rm -rf build"));
        Assert.False(await Reversible("rm big.bin"));
        Assert.False(await Reversible("rm /etc/hosts"));
        Assert.False(await Reversible("rm $TARGET"));
        Assert.False(await Reversible("curl -o page.html https://example.com"));
        Assert.False(await Reversible("rm .env"));
        Assert.False(await Reversible("make"));
    }

    /// <summary>The approval pipeline's analysis of an action; nothing is decided, so nothing reaches the log.</summary>
    internal static ActionAnalysis Analyze(CheckpointFixture fixture, ProposedAction action) => Analyze(fixture.Workspace, action);

    internal static ActionAnalysis Analyze(string workspace, ProposedAction action)
    {
        using var log = new TempLog();
        var fabric = new Corvid.Fabric.DecisionFabric(Corvid.Fabric.DecisionPolicy.Default,
            new Corvid.Fabric.DegradedDecider(new KeywordEmbedder("x"), new FakeGenerator()), log.Log);
        return new ApprovalPipeline(ApprovalEnvironment.Create(workspace, "/home/user"), fabric, log.Log).Analyze(action);
    }

    internal static ActionAnalysis Analysis(CheckpointFixture fixture, string path) => Analyze(fixture, new ShellAction("rm " + path));
}

/// <summary>Checkpoints in the turn loop, and <c>/undo</c> and <c>/rollback</c> as one operation on log positions.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class CheckpointTurnTests : IDisposable
{
    private readonly CheckpointFixture _fixture;

    public CheckpointTurnTests() => _fixture = new CheckpointFixture(options => options with { MaxFileBytes = 64 * 1024 });

    public void Dispose() => _fixture.Dispose();

    private TurnHarness Harness(FakeGenerator generator, CheckpointService? checkpoints = null) =>
        new(generator, prompter: FakePrompter.Always(approve: true), workspace: _fixture.Workspace,
            options: TurnOptions.Default with { ToolFilter = tool => tool.Name is "write_file" or "patch_file" },
            approvalOptions: new ApprovalOptions { ShellTools = BuiltinTools.ShellToolMapping },
            tools: tools => tools.Add(new FileTools(new Workspace(_fixture.Workspace))))
        {
            Checkpoints = checkpoints ?? _fixture.Service,
        };

    private static Func<GeneratorCall, Microsoft.Extensions.AI.ChatResponse> Write(string path, string content) =>
        Reply.Call("write_file", JsonSerializer.Serialize(new { path, content }));

    private static Func<GeneratorCall, Microsoft.Extensions.AI.ChatResponse> Patch(string path, string old_text, string new_text) =>
        Reply.Call("patch_file", JsonSerializer.Serialize(new { path, old_text, new_text }));

    private static SessionSnapshot Snapshot(TurnHarness harness) =>
        SessionProjection.Load(harness.Log, TurnHarness.Session, cancellationToken: TestContext.Current.CancellationToken);

    private static Task<RestoreResult> Undo(TurnHarness harness, CheckpointService service) =>
        service.RestoreAsync(harness.Log, TurnHarness.Session, CheckpointService.PlanUndo(Snapshot(harness))!,
            TestContext.Current.CancellationToken);

    private static Task<RestoreResult> Rollback(TurnHarness harness, CheckpointService service, int number) =>
        service.RestoreAsync(harness.Log, TurnHarness.Session,
            CheckpointService.PlanRollback(Snapshot(harness), number)!, TestContext.Current.CancellationToken);

    [Fact]
    public async Task A_write_takes_a_checkpoint_before_and_after_linked_to_its_call()
    {
        _fixture.Write("notes.txt", "old\n");
        using var harness = Harness(new FakeGenerator(Write("notes.txt", "new\n"), Reply.Say("Saved.")));

        await harness.RunAsync("save the notes");

        var events = harness.Events();
        var call = Assert.Single(events, item => item.Payload is ToolCallIssued);
        var checkpoints = events.Where(item => item.Payload is CheckpointTaken).ToList();
        Assert.Equal(2, checkpoints.Count);
        var before = (CheckpointTaken)checkpoints[0].Payload;
        var after = (CheckpointTaken)checkpoints[1].Payload;
        var result = events.Single(item => item.Payload is ToolResult);
        Assert.Equal((CheckpointKinds.Before, ((ToolCallIssued)call.Payload).CallId), (before.Kind, before.CallId));
        Assert.Equal((CheckpointKinds.After, before.CallId), (after.Kind, after.CallId));
        Assert.True(call.Sequence < checkpoints[0].Sequence && checkpoints[1].Sequence < result.Sequence);
        Assert.Equal(["notes.txt"], after.Paths!.Value);
        Assert.Equal("old\n"u8.ToArray(), await _fixture.Store.ReadBlobAsync((await _fixture.Store.ReadAsync(before.Revision))["notes.txt"].Blob));
        // The approval was asked knowing the write can be undone (plan §10.2 step 4).
        var asked = Assert.Single(harness.Payloads().OfType<ApprovalRequested>());
        Assert.True(JsonDocument.Parse(asked.Action).RootElement.GetProperty("reversible").GetBoolean());
    }

    [Fact]
    public async Task Undo_reverts_the_last_turns_files_and_moves_the_transcript_back()
    {
        _fixture.Write("notes.txt", "first\n");
        using var harness = Harness(new FakeGenerator(Write("notes.txt", "second\n"), Reply.Say("Saved."), Write("todo.txt", "new file\n"),
            Reply.Say("Created.")));
        var runner = harness.Runner();
        await harness.RunAsync(runner, "update the notes");
        await harness.RunAsync(runner, "make a todo file");
        var head = harness.Log.GetHead(TurnHarness.Session, TestContext.Current.CancellationToken);

        var undone = await Undo(harness, _fixture.Service);

        Assert.Equal(RestoreStatus.Restored, undone.Status);
        Assert.Equal(["todo.txt"], undone.Restored);
        Assert.False(File.Exists(_fixture.PathOf("todo.txt")));
        Assert.Equal("second\n", _fixture.Read("notes.txt"));
        var snapshot = Snapshot(harness);
        Assert.Equal("Saved.", Assert.IsType<AssistantMessage>(snapshot.Transcript[^1].Content).Text);
        Assert.Empty(snapshot.Issues);
        var rewound = Assert.IsType<SessionRewound>(harness.Events()[^1].Payload);
        Assert.Equal(("undo", undone.Plan.Position), (rewound.Reason, rewound.ThroughSequence));
        Assert.True(head > rewound.ThroughSequence);

        // Undo again walks back one more turn; the first turn's write goes too.
        var again = await Undo(harness, _fixture.Service);
        Assert.Equal(["notes.txt"], again.Restored);
        Assert.Equal("first\n", _fixture.Read("notes.txt"));
        Assert.DoesNotContain(Snapshot(harness).Transcript, entry => entry.Content is UserMessage);
    }

    [Fact]
    public async Task Rollback_n_returns_to_the_nth_latest_checkpoint_on_the_same_model()
    {
        _fixture.Write("a.txt", "a0\n");
        using var harness = Harness(new FakeGenerator(Write("a.txt", "a1\n"), Reply.Say("One."), Write("b.txt", "b1\n"), Reply.Say("Two."),
            Write("a.txt", "a2\n"), Reply.Say("Three.")));
        var runner = harness.Runner();
        await harness.RunAsync(runner, "first");
        await harness.RunAsync(runner, "second");
        await harness.RunAsync(runner, "third");
        var ledger = CheckpointService.Ledger(Snapshot(harness));
        Assert.Equal(3, ledger.Length);

        var result = await Rollback(harness, _fixture.Service, 2);

        Assert.Equal(RestoreStatus.Restored, result.Status);
        Assert.Equal(ledger[1].CallSequence - 1, result.Plan.Position);
        Assert.Equal("a1\n", _fixture.Read("a.txt"));
        Assert.False(File.Exists(_fixture.PathOf("b.txt")));
        var snapshot = Snapshot(harness);
        Assert.Single(CheckpointService.Ledger(snapshot));
        Assert.Empty(snapshot.Issues);
        // The transcript ends inside the second turn, before its write, with no call left open.
        Assert.Equal("second", Assert.IsType<UserMessage>(snapshot.Transcript[^1].Content).Text);
        Assert.Null(CheckpointService.PlanRollback(snapshot, 2));

        // The conversation goes on from there: the prompt holds the second message, unanswered, then the new one.
        var next = await harness.RunAsync(runner, "fourth");
        Assert.Equal(TurnStatus.Answered, next.Status);
        var prompt = harness.Generator.Calls[^1].Messages.Where(message => message.Role == Microsoft.Extensions.AI.ChatRole.User)
            .Select(message => message.Text).ToList();
        Assert.Equal(["first", "second", "fourth"], prompt);
    }

    [Fact]
    public async Task A_call_cancelled_at_its_approval_prompt_changed_nothing_and_does_not_block_an_undo()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var prompter = new FakePrompter(async (_, token) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return ApprovalAnswer.Deny();
        });
        using var harness = new TurnHarness(new FakeGenerator(Write("notes.txt", "x")), prompter: prompter, workspace: _fixture.Workspace,
            options: TurnOptions.Default with { ToolFilter = tool => tool.Name is "write_file" },
            tools: tools => tools.Add(new FileTools(new Workspace(_fixture.Workspace)))) { Checkpoints = _fixture.Service };

        var result = await harness.Runner().RunAsync(new Abstractions.Sessions.Envelope(TurnHarness.Session, "cli", "write it",
            Abstractions.Sessions.EnvelopeOrigin.User), cancel.Token);

        Assert.Equal(TurnStatus.Cancelled, result.Status);
        var plan = CheckpointService.PlanUndo(Snapshot(harness), call => call.ToolName == "write_file")!;
        Assert.Empty(plan.Unrecorded);
        Assert.Equal(RestoreStatus.Restored, (await _fixture.Service.RestoreAsync(harness.Log, TurnHarness.Session, plan,
            TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task The_users_own_edits_between_checkpoints_survive_a_restore()
    {
        _fixture.Write("story.txt", "title\n\nbody\n\nend\n");
        _fixture.Write("mine.txt", "mine\n");
        using var harness = Harness(new FakeGenerator(Patch("story.txt", "title", "TITLE"), Reply.Say("Renamed.")));
        await harness.RunAsync("capitalize the title");
        // After Corvid's write, the user edits another part of the same file and a file Corvid never touched.
        _fixture.Write("story.txt", "TITLE\n\nbody\n\nthe end, by me\n");
        _fixture.Write("mine.txt", "mine, edited\n");

        var result = await Undo(harness, _fixture.Service);

        Assert.Equal(RestoreStatus.Restored, result.Status);
        Assert.Equal(["story.txt"], result.Merged);
        Assert.Equal("title\n\nbody\n\nthe end, by me\n", _fixture.Read("story.txt"));
        Assert.Equal("mine, edited\n", _fixture.Read("mine.txt"));
    }

    [Fact]
    public async Task A_restore_that_would_overwrite_the_users_edit_is_refused_and_changes_nothing()
    {
        _fixture.Write("story.txt", "title\n");
        using var harness = Harness(new FakeGenerator(Write("story.txt", "Corvid's title\n"), Reply.Say("Done."),
            Write("other.txt", "corvid\n"), Reply.Say("Done.")));
        var runner = harness.Runner();
        await harness.RunAsync(runner, "retitle");
        await harness.RunAsync(runner, "write other");
        _fixture.Write("story.txt", "The user's title\n");
        var head = harness.Log.GetHead(TurnHarness.Session, TestContext.Current.CancellationToken);

        var result = await Rollback(harness, _fixture.Service, 2);

        Assert.Equal(RestoreStatus.Refused, result.Status);
        Assert.Equal("story.txt", Assert.Single(result.Conflicts).Path);
        Assert.Equal("The user's title\n", _fixture.Read("story.txt"));
        Assert.Equal("corvid\n", _fixture.Read("other.txt"));
        Assert.Equal(head, harness.Log.GetHead(TurnHarness.Session, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_users_edit_made_between_two_of_corvids_writes_to_one_file_is_kept()
    {
        _fixture.Write("list.txt", "one\ntwo\nthree\nfour\nfive\nsix\nseven\n");
        using var harness = Harness(new FakeGenerator(Patch("list.txt", "one", "ONE"), Reply.Say("Done."),
            Patch("list.txt", "seven", "SEVEN"), Reply.Say("Done.")));
        var runner = harness.Runner();
        await harness.RunAsync(runner, "first");
        _fixture.Write("list.txt", "ONE\ntwo\nthree\nfour, by the user\nfive\nsix\nseven\n");
        await harness.RunAsync(runner, "second");

        var result = await Rollback(harness, _fixture.Service, 2);

        Assert.Equal(RestoreStatus.Restored, result.Status);
        Assert.Equal("one\ntwo\nthree\nfour, by the user\nfive\nsix\nseven\n", _fixture.Read("list.txt"));
    }

    [Fact]
    public async Task Binary_files_are_restored_exactly_and_a_changed_binary_is_never_merged()
    {
        var original = RandomNumberGenerator.GetBytes(48 * 1024);
        original[0] = 0;
        _fixture.Write("image.bin", original);
        using var harness = Harness(new FakeGenerator(Write("image.bin", "replaced"), Reply.Say("Done.")));
        await harness.RunAsync("replace it");

        Assert.Equal(RestoreStatus.Restored, (await Undo(harness, _fixture.Service)).Status);
        Assert.Equal(original, File.ReadAllBytes(_fixture.PathOf("image.bin")));

        using var second = new CheckpointFixture(options => options with { MaxFileBytes = 64 * 1024 });
        second.Write("image.bin", original);
        using var other = new TurnHarness(new FakeGenerator(Write("image.bin", "replaced"), Reply.Say("Done.")),
            prompter: FakePrompter.Always(approve: true), workspace: second.Workspace,
            options: TurnOptions.Default with { ToolFilter = tool => tool.Name is "write_file" },
            tools: tools => tools.Add(new FileTools(new Workspace(second.Workspace)))) { Checkpoints = second.Service };
        await other.RunAsync("replace it");
        var edited = (byte[])original.Clone();
        edited[^1] ^= 0xFF;
        second.Write("image.bin", edited);

        var refused = await second.Service.RestoreAsync(other.Log, TurnHarness.Session,
            CheckpointService.PlanUndo(Snapshot(other))!, TestContext.Current.CancellationToken);
        Assert.Equal(RestoreStatus.Refused, refused.Status);
        Assert.Equal(edited, File.ReadAllBytes(second.PathOf("image.bin")));
    }

    [Fact]
    public async Task A_write_over_the_size_limit_is_marked_irreversible_and_an_undo_leaves_it_alone()
    {
        using var small = new CheckpointFixture(options => options with { MaxFileBytes = 16 });
        small.Write("big.txt", new string('x', 100));
        using var harness = new TurnHarness(new FakeGenerator(Write("big.txt", "short"), Reply.Say("Done.")),
            prompter: FakePrompter.Always(approve: true), workspace: small.Workspace,
            options: TurnOptions.Default with { ToolFilter = tool => tool.Name is "write_file" },
            tools: tools => tools.Add(new FileTools(new Workspace(small.Workspace)))) { Checkpoints = small.Service };

        await harness.RunAsync("shorten it");
        var asked = Assert.Single(harness.Payloads().OfType<ApprovalRequested>());
        Assert.False(JsonDocument.Parse(asked.Action).RootElement.GetProperty("reversible").GetBoolean());
        small.Write("big.txt", new string('y', 100));

        var result = await small.Service.RestoreAsync(harness.Log, TurnHarness.Session,
            CheckpointService.PlanUndo(Snapshot(harness))!, TestContext.Current.CancellationToken);

        // The big file was not in the "before" checkpoint; the restore cannot tell whose it is and keeps it.
        Assert.Equal(RestoreStatus.Refused, result.Status);
        Assert.Equal(new string('y', 100), small.Read("big.txt"));
    }

    [Fact]
    public async Task Without_git_writes_still_ask_marked_irreversible_and_undo_refuses_with_the_reason()
    {
        using var missing = new CheckpointFixture(options => options with { GitPath = "/nonexistent/bin/git" });
        missing.Write("notes.txt", "old\n");
        using var harness = new TurnHarness(new FakeGenerator(Write("notes.txt", "new\n"), Reply.Say("Saved.")),
            prompter: FakePrompter.Always(approve: true), workspace: missing.Workspace,
            options: TurnOptions.Default with { ToolFilter = tool => tool.Name is "write_file" },
            tools: tools => tools.Add(new FileTools(new Workspace(missing.Workspace)))) { Checkpoints = missing.Service };

        await harness.RunAsync("save");

        Assert.Equal("new\n", missing.Read("notes.txt"));
        Assert.Empty(harness.Payloads().OfType<CheckpointTaken>());
        var asked = Assert.Single(harness.Payloads().OfType<ApprovalRequested>());
        Assert.False(JsonDocument.Parse(asked.Action).RootElement.GetProperty("reversible").GetBoolean());
        var plan = CheckpointService.PlanUndo(Snapshot(harness), call => call.ToolName == "write_file")!;
        Assert.Empty(plan.Entries);
        var refused = await missing.Service.RestoreAsync(harness.Log, TurnHarness.Session, plan, TestContext.Current.CancellationToken);
        Assert.Equal(RestoreStatus.Refused, refused.Status);
        Assert.Contains("changed files without a checkpoint", refused.Detail, StringComparison.Ordinal);
        Assert.Equal("new\n", missing.Read("notes.txt"));
        Assert.DoesNotContain(harness.Payloads(), payload => payload is SessionRewound);
    }

    [Fact]
    public async Task A_checkpoint_that_fails_stops_the_write()
    {
        using var limited = new CheckpointFixture(options => options with { MaxFiles = 1 });
        limited.Write("one.txt", "1");
        limited.Write("two.txt", "2");
        using var harness = new TurnHarness(new FakeGenerator(Write("three.txt", "3"), Reply.Say("Tried.")),
            prompter: FakePrompter.Always(approve: true), workspace: limited.Workspace,
            options: TurnOptions.Default with { ToolFilter = tool => tool.Name is "write_file" },
            tools: tools => tools.Add(new FileTools(new Workspace(limited.Workspace)))) { Checkpoints = limited.Service };

        await harness.RunAsync("write three");

        Assert.False(File.Exists(limited.PathOf("three.txt")));
        var result = Assert.Single(harness.Payloads().OfType<ToolResult>());
        Assert.True(result.IsError);
        Assert.Contains("no checkpoint could be taken", result.Result.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>Stands in for bubblewrap: <c>rm</c> deletes inside the workspace, anything else only prints.</summary>
    private sealed class DeletingSandbox(string workspace) : Corvid.Sandbox.ISandbox
    {
        public List<string> Commands { get; } = [];

        public Task<Corvid.Sandbox.SandboxResult> RunAsync(Corvid.Sandbox.SandboxPolicy policy, Corvid.Sandbox.SandboxCommand command,
            CancellationToken cancellationToken = default)
        {
            var line = command.Arguments[^1];
            Commands.Add(line);
            if (line.StartsWith("rm ", StringComparison.Ordinal))
                File.Delete(Path.Combine(workspace, line[3..]));
            return Task.FromResult(new Corvid.Sandbox.SandboxResult { ExitCode = 0, StandardOutput = "", StandardError = "" });
        }
    }

    [Fact]
    public async Task A_destructive_shell_command_is_checkpointed_and_undone_and_a_read_only_one_is_not()
    {
        _fixture.Write("notes.txt", "keep me\n");
        var sandbox = new DeletingSandbox(_fixture.Workspace);
        var shell = new ShellTool(new SandboxedShell(sandbox, new ShellOptions { Workspace = _fixture.Workspace }));
        using var harness = new TurnHarness(new FakeGenerator(Reply.Call("shell", """{"command":"ls"}"""), Reply.Say("Listed."),
                Reply.Call("shell", """{"command":"rm notes.txt"}"""), Reply.Say("Deleted.")),
            prompter: FakePrompter.Always(approve: true), workspace: _fixture.Workspace,
            options: TurnOptions.Default with { ToolFilter = tool => tool.Name is "shell" },
            approvalOptions: new ApprovalOptions { ShellTools = BuiltinTools.ShellToolMapping }, tools: tools => tools.Add(shell))
        {
            Checkpoints = _fixture.Service,
        };
        var runner = harness.Runner();

        await harness.RunAsync(runner, "list the files");
        Assert.Empty(harness.Payloads().OfType<CheckpointTaken>());
        await harness.RunAsync(runner, "delete the notes");

        Assert.Equal(["ls", "rm notes.txt"], sandbox.Commands);
        Assert.False(File.Exists(_fixture.PathOf("notes.txt")));
        var after = harness.Payloads().OfType<CheckpointTaken>().Single(checkpoint => checkpoint.Kind == CheckpointKinds.After);
        Assert.Equal(["notes.txt"], after.Paths!.Value);
        var asked = Assert.Single(harness.Payloads().OfType<ApprovalRequested>());
        Assert.True(JsonDocument.Parse(asked.Action).RootElement.GetProperty("reversible").GetBoolean());

        Assert.Equal(RestoreStatus.Restored, (await Undo(harness, _fixture.Service)).Status);
        Assert.Equal("keep me\n", _fixture.Read("notes.txt"));
    }

    [Fact]
    public async Task Undo_runs_on_the_actor_after_the_running_turn_and_reuses_no_call_id()
    {
        var gate = new TaskCompletionSource();
        var generator = new FakeGenerator(Reply.Call("write_file", """{"path":"a.txt","content":"x"}""", callId: "call_0"), Reply.Say("Done."),
            Reply.Call("write_file", """{"path":"b.txt","content":"y"}""", callId: "call_0"), Reply.Say("Again."));
        using var harness = Harness(generator);
        var sink = new CollectingSink();
        await using var actor = harness.Actor(sink);
        await actor.SubmitAsync(new Abstractions.Sessions.Envelope(TurnHarness.Session, "cli", "write a", Abstractions.Sessions.EnvelopeOrigin.User),
            TestContext.Current.CancellationToken);

        var undo = actor.RunExclusiveAsync(token => _fixture.Service.RestoreAsync(harness.Log, TurnHarness.Session,
            CheckpointService.PlanUndo(Snapshot(harness))!, token), TestContext.Current.CancellationToken);
        await sink.NextAsync();
        var result = await undo;

        Assert.Equal(RestoreStatus.Restored, result.Status);
        Assert.False(File.Exists(_fixture.PathOf("a.txt")));
        await actor.SubmitAsync(new Abstractions.Sessions.Envelope(TurnHarness.Session, "cli", "write b", Abstractions.Sessions.EnvelopeOrigin.User),
            TestContext.Current.CancellationToken);
        await sink.NextAsync();
        var calls = harness.Payloads().OfType<ToolCallIssued>().Select(call => call.CallId).ToList();
        Assert.Equal(["call_0", "call_0-2"], calls);
        Assert.Equal("y", _fixture.Read("b.txt"));
    }
}
