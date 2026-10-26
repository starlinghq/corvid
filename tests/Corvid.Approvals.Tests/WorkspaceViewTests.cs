namespace Corvid.Approvals.Tests;

/// <summary>
/// A6 (G-4): the pipeline over another workspace (its paths judged there, everything else shared), and a caller's
/// <see cref="ApprovalContext.AlwaysAsk"/>, which asks the person whatever the mode or a remembered rule says.
/// </summary>
public sealed class WorkspaceViewTests
{
    private static CancellationToken Ct => Fixtures.Ct;

    private const string Other = "/home/user/work/other";

    [Fact]
    public async Task A_view_judges_paths_in_its_own_workspace_and_shares_rules_and_the_deadline()
    {
        using var db = new TempLog();
        var prompter = ScriptedPrompter.Always(ApprovalAnswer.Approve(ApprovalScope.Session));
        var pipeline = Fixtures.Pipeline(db, prompter: prompter);
        var view = pipeline.ForWorkspace(ApprovalEnvironment.Create(Other, Fixtures.Home, runtimeDirectory: "/run/user/1000"));

        Assert.Equal(PathLocation.Inside, Location(view.Analyze(new ShellAction($"rm {Other}/build.log"))));
        Assert.Equal(PathLocation.Outside, Location(pipeline.Analyze(new ShellAction($"rm {Other}/build.log"))));
        Assert.Equal(PathLocation.Outside, Location(view.Analyze(new ShellAction($"rm {Fixtures.Workspace}/build.log"))));

        // A rule remembered through the view is the pipeline's, and a deadline set on either applies to both.
        var asked = await view.AuthorizeAsync(Fixtures.Context(ApprovalMode.Manual), new ShellAction("touch notes.txt"), Ct);
        Assert.True(asked.Allowed);
        Assert.NotEmpty(pipeline.SessionRules(Fixtures.Session));
        Assert.Same(pipeline.SessionRules(Fixtures.Session).ToArray()[0], view.SessionRules(Fixtures.Session).ToArray()[0]);
        view.SetTimeout(TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7), pipeline.Options.Timeout);
        pipeline.SetTimeout(TimeSpan.FromSeconds(9));
        Assert.Equal(TimeSpan.FromSeconds(9), view.Options.Timeout);
        Assert.Same(pipeline.Options, view.ForWorkspace(Fixtures.Environment).Options);
    }

    [Fact]
    public async Task Always_ask_asks_once_in_every_mode_past_a_remembered_rule_and_is_denied_with_nobody_to_ask()
    {
        using var db = new TempLog();
        var always = new InMemoryApprovalRuleStore();
        var prompter = ScriptedPrompter.Always(ApprovalAnswer.Approve(ApprovalScope.Always));
        var pipeline = Fixtures.Pipeline(db, prompter: prompter, always: always);
        // The person allowed git always.
        await pipeline.AuthorizeAsync(Fixtures.Context(ApprovalMode.Manual), new ShellAction("git worktree add -b x /tmp/x HEAD"), Ct);
        Assert.NotEmpty(always.Rules);
        prompter.Prompts.Clear();

        foreach (var mode in Fixtures.Modes)
        {
            var context = Fixtures.Context(mode) with { AlwaysAsk = "creates a git worktree outside the workspace" };
            var result = await pipeline.AuthorizeAsync(context, new ShellAction("git worktree add -b x /tmp/x HEAD"), Ct);
            Assert.Equal(ApprovalResolution.UserApproved, result.Resolution);
        }
        Assert.Equal(Fixtures.Modes.Length, prompter.Prompts.Count);
        Assert.All(prompter.Prompts, prompt =>
        {
            Assert.Equal([ApprovalScope.Once], prompt.Offered);
            Assert.Contains(prompt.Reasons, reason => reason is { Code: ApprovalReasons.Required, Detail: "creates a git worktree outside the workspace" });
        });

        var nobody = await pipeline.AuthorizeAsync(Fixtures.Context(ApprovalMode.Permissive) with { AlwaysAsk = "a worktree", Interactive = false },
            new ShellAction("git worktree add -b y /tmp/y HEAD"), Ct);
        Assert.Equal(ApprovalResolution.NoApprover, nobody.Resolution);
        Assert.False(nobody.Allowed);
    }

    private static PathLocation Location(ActionAnalysis analysis) =>
        analysis.Commands[0].Targets.First(target => target.Kind == TargetKind.Path).Location!.Value;
}
