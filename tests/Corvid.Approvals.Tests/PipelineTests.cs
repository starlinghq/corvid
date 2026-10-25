using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Kernel.Events;
using Corvid.Tools;

namespace Corvid.Approvals.Tests;

public sealed class PipelineTests
{
    private const string Session = Fixtures.Session;

    private static CancellationToken Ct => Fixtures.Ct;

    private static List<SessionEvent> Events(TempLog db) => db.Log.Read(Session, cancellationToken: Ct).Select(e => e.Payload).ToList();

    [Fact]
    public async Task An_ask_records_the_request_the_answer_and_the_risk_outcome()
    {
        using var db = new TempLog();
        var prompter = ScriptedPrompter.Always(ApprovalAnswer.Approve());
        var result = await Fixtures.Pipeline(db, prompter: prompter).AuthorizeAsync(Fixtures.Context(), new ShellAction("rm -rf build"), Ct);

        Assert.True(result.Allowed);
        Assert.Equal(ApprovalResolution.UserApproved, result.Resolution);
        var prompt = Assert.Single(prompter.Prompts);
        Assert.Equal(result.ApprovalId, prompt.ApprovalId);
        Assert.Equal([ApprovalScope.Once, ApprovalScope.Session, ApprovalScope.Always], prompt.Offered);

        var events = Events(db);
        var decision = Assert.IsType<Decision>(events[0]);
        Assert.Equal("risk", decision.QuestionId);
        Assert.Equal("ask", decision.Action);
        Assert.Equal("degraded", decision.Decider);
        var requested = Assert.IsType<ApprovalRequested>(events[1]);
        Assert.Equal(result.ApprovalId, requested.ApprovalId);
        using (var action = JsonDocument.Parse(requested.Action))
        {
            var command = action.RootElement.GetProperty("commands")[0];
            Assert.Equal("rm", command.GetProperty("verb").GetString());
            Assert.Equal("inside", command.GetProperty("targets")[0].GetProperty("location").GetString());
        }

        Assert.Contains("risk: ask: no answer", requested.Reason);
        var resolved = Assert.IsType<ApprovalResolved>(events[2]);
        Assert.True(resolved.Approved);
        Assert.Equal("user: approved (once)", resolved.Reason);
        Assert.Equal(("once", (string?)null), (resolved.Scope, resolved.Note));
        Assert.Equal(new DecisionOutcomeRecorded(decision.DecisionId, "user_approved"), events[3]);

        var snapshot = SessionProjection.Load(db.Log, Session, cancellationToken: Ct);
        Assert.Empty(snapshot.PendingApprovals);
        Assert.Equal("user_approved", snapshot.Decisions[decision.DecisionId].Outcome);
        Assert.Empty(snapshot.Issues);
    }

    [Fact]
    public async Task An_unanswered_approval_is_denied_after_the_timeout()
    {
        using var db = new TempLog();
        var pipeline = Fixtures.Pipeline(db, prompter: ScriptedPrompter.Silent(),
            options: new ApprovalOptions { Timeout = TimeSpan.FromMilliseconds(100) });
        var result = await pipeline.AuthorizeAsync(Fixtures.Context(), new ShellAction("rm -rf build"), Ct);

        Assert.False(result.Allowed);
        Assert.Equal(ApprovalResolution.TimedOut, result.Resolution);
        var resolved = Assert.IsType<ApprovalResolved>(Events(db).Single(e => e is ApprovalResolved));
        Assert.False(resolved.Approved);
        Assert.StartsWith("timeout", resolved.Reason);
        var snapshot = SessionProjection.Load(db.Log, Session, cancellationToken: Ct);
        Assert.Empty(snapshot.PendingApprovals);
        Assert.Contains(snapshot.Decisions.Values, d => d.Outcome == "approval_timed_out");
    }

    [Fact]
    public async Task Hardline_and_non_interactive_denials_ask_nobody()
    {
        using var db = new TempLog();
        var prompter = ScriptedPrompter.Always(ApprovalAnswer.Approve(ApprovalScope.Always));
        var pipeline = Fixtures.Pipeline(db, new FixedRiskDecider(0), prompter);

        var hardline = await pipeline.AuthorizeAsync(Fixtures.Context(ApprovalMode.Permissive), new ShellAction("sudo rm -rf --no-preserve-root /"), Ct);
        Assert.Equal(ApprovalResolution.Hardline, hardline.Resolution);
        Assert.Contains(hardline.Assessment.Reasons, r => r.Code == HardlineRules.RecursiveDeleteOutsideWorkspace);

        var background = await pipeline.AuthorizeAsync(Fixtures.Context(ApprovalMode.Manual) with { Interactive = false },
            new ShellAction("rm -rf build"), Ct);
        Assert.Equal(ApprovalResolution.NoApprover, background.Resolution);
        Assert.False(background.Allowed);

        Assert.Empty(prompter.Prompts);
        Assert.DoesNotContain(Events(db), e => e is ApprovalRequested or ApprovalResolved);
    }

    [Fact]
    public async Task A_session_approval_is_a_structural_rule_that_only_covers_what_was_approved()
    {
        using var db = new TempLog();
        var prompter = ScriptedPrompter.Always(ApprovalAnswer.Approve(ApprovalScope.Session));
        var pipeline = Fixtures.Pipeline(db, prompter: prompter);
        var context = Fixtures.Context(ApprovalMode.Manual);

        Assert.Equal(ApprovalScope.Session, (await pipeline.AuthorizeAsync(context, new ShellAction("rm -rf build"), Ct)).Scope);
        var rule = Assert.Single(pipeline.SessionRules(Session));
        Assert.Equal(("rm", RuleDecision.Allow), (rule.Verb, rule.Decision));
        Assert.Equal([Fixtures.Workspace + "/build"], rule.Paths);

        Assert.Equal(ApprovalResolution.RuleAllowed, (await pipeline.AuthorizeAsync(context, new ShellAction("rm -rf build/cache"), Ct)).Resolution);
        Assert.Equal(ApprovalResolution.RuleAllowed, (await pipeline.AuthorizeAsync(context, new ShellAction("timeout 9 rm -r build/*"), Ct)).Resolution);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context, new ShellAction("rm -rf src"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context, new ShellAction("rm -rf build src"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context, new ShellAction("rm -rf \"$X\""), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context, new ShellAction("sudo rm -rf build"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Deny, (await pipeline.AssessAsync(context, new ShellAction("rm -rf /"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context with { SessionId = "s-2" }, new ShellAction("rm -rf build"), Ct)).Outcome);
    }

    [Fact]
    public async Task An_always_answer_writes_the_previewed_rule_narrowed_to_the_users_paths()
    {
        using var db = new TempLog();
        var build = Fixtures.Workspace + "/build";
        var answer = ApprovalAnswer.Approve(ApprovalScope.Always) with { RulePaths = [[build + "/cache"]] };
        var pipeline = Fixtures.Pipeline(db, prompter: ScriptedPrompter.Always(answer));
        var context = Fixtures.Context(ApprovalMode.Manual);

        // G-11: the preview is exactly what the answer writes, before narrowing.
        var preview = ApprovalPipeline.RulesFor(Fixtures.Analyze("rm -rf build"), RuleDecision.Allow, DateTimeOffset.UnixEpoch);
        Assert.Equal([build], Assert.Single(preview).Paths);

        Assert.Equal(ApprovalScope.Always, (await pipeline.AuthorizeAsync(context, new ShellAction("rm -rf build"), Ct)).Scope);
        Assert.Equal([build + "/cache"], Assert.Single(pipeline.AlwaysRules).Paths);
        Assert.Equal(ApprovalOutcome.Auto, (await pipeline.AssessAsync(context, new ShellAction("rm -rf build/cache/x"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context, new ShellAction("rm -rf build/other"), Ct)).Outcome);
    }

    [Fact]
    public async Task A_narrowing_that_widens_writes_no_rule_and_applies_once()
    {
        using var db = new TempLog();
        var answer = ApprovalAnswer.Approve(ApprovalScope.Session) with { RulePaths = [[Fixtures.Workspace + "/src"]] };
        var pipeline = Fixtures.Pipeline(db, prompter: ScriptedPrompter.Always(answer));

        var result = await pipeline.AuthorizeAsync(Fixtures.Context(ApprovalMode.Manual), new ShellAction("rm -rf build"), Ct);

        Assert.True(result.Allowed);
        Assert.Equal(ApprovalScope.Once, result.Scope);
        Assert.Empty(pipeline.SessionRules(Session));
        Assert.Empty(pipeline.AlwaysRules);
    }

    [Theory]
    [InlineData("build/cache", "not an absolute path")]
    [InlineData("/elsewhere", "a rule can only be narrowed")]
    [InlineData("{ws}/build/../src", "a rule can only be narrowed")]
    public void Narrowing_accepts_only_absolute_paths_at_or_under_the_rules_own(string path, string error)
    {
        var rules = ApprovalPipeline.RulesFor(Fixtures.Analyze("rm -rf build"), RuleDecision.Allow, DateTimeOffset.UnixEpoch);

        Assert.False(ApprovalPipeline.Narrow(rules, [[path.Replace("{ws}", Fixtures.Workspace, StringComparison.Ordinal)]], out var kept, out var why));
        Assert.Contains(error, why, StringComparison.Ordinal);
        Assert.Equal(rules, kept);
        Assert.True(ApprovalPipeline.Narrow(rules, [[]], out var unchanged, out _));
        Assert.Equal(rules, unchanged);
        Assert.False(ApprovalPipeline.Narrow(rules, [[], []], out _, out _));
    }

    [Fact]
    public async Task An_approved_pattern_covers_only_what_it_matches()
    {
        using var db = new TempLog();
        var pipeline = Fixtures.Pipeline(db, prompter: ScriptedPrompter.Always(ApprovalAnswer.Approve(ApprovalScope.Session)));
        var context = Fixtures.Context(ApprovalMode.Manual);

        await pipeline.AuthorizeAsync(context, new ShellAction("rm *.o"), Ct);
        Assert.Equal([Fixtures.Workspace + "/*.o"], Assert.Single(pipeline.SessionRules(Session)).Paths);

        Assert.Equal(ApprovalOutcome.Auto, (await pipeline.AssessAsync(context, new ShellAction("rm *.o"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Auto, (await pipeline.AssessAsync(context, new ShellAction("rm main.o util.o"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context, new ShellAction("rm main.c"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context, new ShellAction("rm src/main.o"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context, new ShellAction("rm *"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context, new ShellAction("rm .hidden.o"), Ct)).Outcome);
    }

    [Fact]
    public async Task Tools_without_targets_ask_once_whatever_the_score()
    {
        using var db = new TempLog();
        var pipeline = Fixtures.Pipeline(db, new FixedRiskDecider(level: 0), ScriptedPrompter.Always(ApprovalAnswer.Approve(ApprovalScope.Session)));
        var context = Fixtures.Context(ApprovalMode.Permissive);

        // A filesystem tool whose path the analyzer cannot find: the hardline list would have nothing to check.
        var patch = new ToolAction(Fixtures.Tool("apply_patch", SideEffects.Filesystem, parameters: "patch"),
            Fixtures.Json("""{"patch":"*** Update File: /home/user/.ssh/authorized_keys"}"""));
        var assessment = await pipeline.AssessAsync(context, patch, Ct);
        Assert.Equal(ApprovalOutcome.Ask, assessment.Outcome);
        Assert.Equal([ApprovalScope.Once], assessment.Offered);
        Assert.Equal(ApprovalOutcome.Auto,
            (await pipeline.AssessAsync(context, patch with { Targets = [new ActionTarget(TargetKind.Path, "src/a.cs", TargetAccess.Write)] }, Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Deny,
            (await pipeline.AssessAsync(context, patch with { Targets = [new ActionTarget(TargetKind.Path, "~/.ssh/authorized_keys", TargetAccess.Write)] }, Ct)).Outcome);

        // A send whose recipients were not given: a rule approving one recipient must not cover it.
        var manual = Fixtures.Context(ApprovalMode.Manual);
        var send = Fixtures.Tool("send_message", SideEffects.Messaging, parameters: "to");
        var toSam = new ToolAction(send, Fixtures.Json("""{"to":"sam","text":"hi"}"""), Recipients: ["sam"]);
        await pipeline.AuthorizeAsync(manual, toSam, Ct);
        Assert.Equal(ApprovalOutcome.Auto, (await pipeline.AssessAsync(manual, toSam, Ct)).Outcome);
        var unknown = await pipeline.AssessAsync(manual, toSam with { Recipients = default }, Ct);
        Assert.Equal(ApprovalOutcome.Ask, unknown.Outcome);
        Assert.Equal([ApprovalScope.Once], unknown.Offered);
    }

    [Fact]
    public async Task Always_rules_persist_in_a_versioned_file_and_denials_are_remembered()
    {
        using var db = new TempLog();
        var path = Path.Combine(db.Root, "config", "corvid", "approvals.json");
        var answers = new Queue<ApprovalAnswer>([ApprovalAnswer.Approve(ApprovalScope.Always), ApprovalAnswer.Deny(ApprovalScope.Always)]);
        var pipeline = Fixtures.Pipeline(db, prompter: new ScriptedPrompter(_ => answers.Dequeue()), always: new JsonFileApprovalRuleStore(path));
        var context = Fixtures.Context(ApprovalMode.Manual);
        await pipeline.AuthorizeAsync(context, new ShellAction("git commit -m wip"), Ct);
        await pipeline.AuthorizeAsync(context, new ShellAction("git push origin main"), Ct);

        var reloaded = new JsonFileApprovalRuleStore(path);
        Assert.Equal(2, reloaded.Rules.Length);
        Assert.Equal(pipeline.AlwaysRules, reloaded.Rules);
        using (var file = JsonDocument.Parse(File.ReadAllText(path)))
        {
            Assert.Equal("corvid-approval-rules", file.RootElement.GetProperty("format").GetString());
            Assert.Equal("commit", file.RootElement.GetProperty("rules")[0].GetProperty("subcommand").GetString());
        }

        using var other = new TempLog();
        var fresh = Fixtures.Pipeline(other, always: reloaded);
        var commit = await fresh.AuthorizeAsync(context with { SessionId = "s-9" }, new ShellAction("git commit -am again"), Ct);
        Assert.Equal(ApprovalResolution.RuleAllowed, commit.Resolution);
        var push = await fresh.AuthorizeAsync(context with { SessionId = "s-9" }, new ShellAction("git push"), Ct);
        Assert.Equal(ApprovalResolution.RuleDenied, push.Resolution);
    }

    [Fact]
    public async Task Remembered_rules_are_listed_with_their_scope_and_removed_K7()
    {
        using var db = new TempLog();
        var path = Path.Combine(db.Root, "config", "corvid", "approvals.json");
        var answers = new Queue<ApprovalAnswer>([ApprovalAnswer.Approve(ApprovalScope.Session), ApprovalAnswer.Approve(ApprovalScope.Always)]);
        var pipeline = Fixtures.Pipeline(db, prompter: new ScriptedPrompter(_ => answers.Dequeue()), always: new JsonFileApprovalRuleStore(path));
        var context = Fixtures.Context(ApprovalMode.Manual);
        await pipeline.AuthorizeAsync(context, new ShellAction("git commit -m wip"), Ct);
        await pipeline.AuthorizeAsync(context, new ShellAction("git push origin main"), Ct);

        var (sessionId, sessionRules) = Assert.Single(pipeline.AllSessionRules);
        Assert.Equal(Session, sessionId);
        var sessionRule = Assert.Single(sessionRules);
        Assert.Equal("commit", sessionRule.Subcommand);
        var alwaysRule = Assert.Single(pipeline.AlwaysRules);
        Assert.Equal("push", alwaysRule.Subcommand);

        // Removed, each call asks again; removing twice changes nothing.
        Assert.True(pipeline.RemoveSessionRule(Session, sessionRule));
        Assert.False(pipeline.RemoveSessionRule(Session, sessionRule));
        Assert.Empty(pipeline.AllSessionRules);
        Assert.True(pipeline.RemoveAlwaysRule(alwaysRule));
        Assert.False(pipeline.RemoveAlwaysRule(alwaysRule));
        Assert.Empty(new JsonFileApprovalRuleStore(path).Rules);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context, new ShellAction("git commit -m again"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context, new ShellAction("git push origin main"), Ct)).Outcome);
    }

    [Fact]
    public void The_in_memory_store_removes_equal_rules_only()
    {
        var store = new InMemoryApprovalRuleStore();
        var at = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var a = new ApprovalRule(RuleDecision.Allow, "shell", "make", null, ActionEffects.None, false, ["/w"], [], "make", at);
        var b = a with { Verb = "ninja", Source = "ninja" };
        store.Add([a, b]);
        Assert.True(store.Remove(a with { Paths = ["/w"] }));
        Assert.Equal([b], store.Rules);
        Assert.False(store.Remove(a));
    }

    [Fact]
    public void Rule_files_reject_unknown_fields_and_versions()
    {
        Assert.Throws<InvalidDataException>(() => JsonFileApprovalRuleStore.Parse("""{"format":"corvid-approval-rules","format_version":2,"rules":[]}"""));
        Assert.Throws<InvalidDataException>(() => JsonFileApprovalRuleStore.Parse("""{"format":"corvid-approval-rules","format_version":1,"rules":[],"extra":1}"""));
        Assert.Throws<InvalidDataException>(() => JsonFileApprovalRuleStore.Parse(
            """{"format":"corvid-approval-rules","format_version":1,"rules":[{"decision":"allow","kind":"shell","verb":"rm","subcommand":null,"effects":["teleport"],"elevated":false,"paths":[],"names":[],"source":"rm","created_at":"2026-10-18T00:00:00Z"}]}"""));
        Assert.Empty(JsonFileApprovalRuleStore.Parse("""{"format":"corvid-approval-rules","format_version":1,"rules":[]}"""));
    }

    [Fact]
    public async Task Forced_prompts_offer_once_only_and_store_no_rule()
    {
        using var db = new TempLog();
        var pipeline = Fixtures.Pipeline(db, prompter: ScriptedPrompter.Always(ApprovalAnswer.Approve(ApprovalScope.Always)));
        var result = await pipeline.AuthorizeAsync(Fixtures.Context(), new ShellAction("cat ~/.ssh/config"), Ct);
        Assert.True(result.Allowed);
        Assert.Equal(ApprovalScope.Once, result.Scope);
        Assert.Contains(result.Assessment.Reasons, r => r.Code == ApprovalReasons.CredentialRead);
        Assert.Empty(pipeline.AlwaysRules);
        Assert.Empty(pipeline.SessionRules(Session));
    }

    [Fact]
    public async Task The_risk_question_reads_the_parsed_action()
    {
        using var db = new TempLog();
        var decider = new FixedRiskDecider(level: 3);
        var assessment = await Fixtures.Pipeline(db, decider).AssessAsync(Fixtures.Context(ApprovalMode.Permissive),
            new ShellAction("timeout 5 sh -c 'rm -rf build'"), Ct);

        Assert.Equal(ApprovalOutcome.Ask, assessment.Outcome);
        Assert.Equal("ask", assessment.Risk!.Verdict.Action);
        var question = Assert.Single(Assert.Single(decider.Requests).Questions);
        Assert.Equal("risk", question.Key);
        var action = question.Value.Instructions.Json!.Value.GetProperty("action");
        var command = action.GetProperty("commands")[0];
        Assert.Equal("rm", command.GetProperty("verb").GetString());
        Assert.Equal(["timeout", "sh -c"], command.GetProperty("via").EnumerateArray().Select(v => v.GetString()));
        Assert.Contains("recursive", command.GetProperty("effects").EnumerateArray().Select(v => v.GetString()));
    }

    [Fact]
    public async Task Smart_mode_needs_a_low_score_and_a_contained_action()
    {
        using var db = new TempLog();
        var pipeline = Fixtures.Pipeline(db, new FixedRiskDecider(level: 1));
        var smart = Fixtures.Context(ApprovalMode.Smart);
        Assert.Equal(ApprovalOutcome.Auto, (await pipeline.AssessAsync(smart, new ShellAction("touch notes.txt"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(smart, new ShellAction("touch /tmp/notes.txt"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(smart, new ShellAction("curl -o out.html https://example.org"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Auto,
            (await pipeline.AssessAsync(Fixtures.Context(ApprovalMode.Permissive), new ShellAction("touch /tmp/notes.txt"), Ct)).Outcome);

        var moderate = Fixtures.Pipeline(db, new FixedRiskDecider(level: 2));
        Assert.Equal(ApprovalOutcome.Ask, (await moderate.AssessAsync(smart, new ShellAction("touch notes.txt"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask, (await moderate.AssessAsync(Fixtures.Context(ApprovalMode.Permissive), new ShellAction("touch notes.txt"), Ct)).Outcome);
        var uncertain = Fixtures.Pipeline(db, new FixedRiskDecider(level: 0, probability: 0.5));
        Assert.Equal(ApprovalOutcome.Ask, (await uncertain.AssessAsync(Fixtures.Context(ApprovalMode.Permissive), new ShellAction("touch notes.txt"), Ct)).Outcome);
    }

    [Fact]
    public async Task Prompter_failures_and_cancellation_close_the_prompt()
    {
        using var db = new TempLog();
        var failing = Fixtures.Pipeline(db, prompter: new ScriptedPrompter(_ => throw new IOException("surface gone")));
        var failed = await failing.AuthorizeAsync(Fixtures.Context(), new ShellAction("rm -rf build"), Ct);
        Assert.Equal(ApprovalResolution.Failed, failed.Resolution);
        Assert.False(failed.Allowed);

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var silent = Fixtures.Pipeline(db, prompter: new ScriptedPrompter(_ =>
        {
            cancel.CancelAfter(50);
            return null;
        }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => silent.AuthorizeAsync(Fixtures.Context(), new ShellAction("rm -rf build"), cancel.Token));
        var resolutions = Events(db).OfType<ApprovalResolved>().ToList();
        Assert.Equal(2, resolutions.Count);
        Assert.StartsWith("prompt failed", resolutions[0].Reason);
        Assert.Equal("cancelled", resolutions[1].Reason);
        Assert.Empty(SessionProjection.Load(db.Log, Session, cancellationToken: Ct).PendingApprovals);
    }

    [Fact]
    public async Task Side_effecting_tools_go_through_approval_and_read_only_tools_do_not()
    {
        using var db = new TempLog();
        var pipeline = Fixtures.Pipeline(db, new FixedRiskDecider(level: 0));
        var context = Fixtures.Context(ApprovalMode.Smart);
        var read = new ToolAction(Fixtures.Tool("read_file", parameters: "path"), Fixtures.Json("""{"path":"src/a.cs"}"""));
        Assert.Equal(ApprovalOutcome.Auto, (await pipeline.AssessAsync(context, read, Ct)).Outcome);

        var write = new ToolAction(Fixtures.Tool("write_file", SideEffects.Filesystem, parameters: "path"), Fixtures.Json("""{"path":"src/a.cs"}"""));
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context with { Mode = ApprovalMode.Manual }, write, Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Auto, (await pipeline.AssessAsync(context, write, Ct)).Outcome);
        var outside = write with { Arguments = Fixtures.Json("""{"path":"/etc/hosts"}""") };
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context, outside, Ct)).Outcome);
        var destructive = new ToolAction(Fixtures.Tool("delete_tree", SideEffects.Filesystem, destructive: true, parameters: "path"),
            Fixtures.Json("""{"path":"build"}"""));
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context, destructive, Ct)).Outcome);
        var settings = new ToolAction(Fixtures.Tool("set_option", SideEffects.Configuration), Fixtures.Json("""{"key":"mode"}"""));
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context with { Mode = ApprovalMode.Permissive }, settings, Ct)).Outcome);

        using var degradedDb = new TempLog();
        var degraded = Fixtures.Pipeline(degradedDb);
        Assert.Equal(ApprovalOutcome.Ask, (await degraded.AssessAsync(context with { Mode = ApprovalMode.Permissive }, write, Ct)).Outcome);
    }

    [Fact]
    public async Task Tools_hit_the_hardline_list_through_their_path_arguments()
    {
        using var db = new TempLog();
        var pipeline = Fixtures.Pipeline(db, new FixedRiskDecider(level: 0));
        var context = Fixtures.Context(ApprovalMode.Permissive);
        var write = Fixtures.Tool("write_file", SideEffects.Filesystem, parameters: "path");
        var read = Fixtures.Tool("read_file", parameters: "path");

        var keys = await pipeline.AssessAsync(context, new ToolAction(write, Fixtures.Json("""{"path":"~/.ssh/authorized_keys"}""")), Ct);
        Assert.Contains(keys.Reasons, r => r.Code == HardlineRules.CredentialStoreWrite);
        var vault = await pipeline.AssessAsync(context, new ToolAction(read, Fixtures.Json("""{"path":"/home/user/.local/share/corvid-vault/store.bin"}""")), Ct);
        Assert.Contains(vault.Reasons, r => r.Code == HardlineRules.VaultAccess);
        var hidden = await pipeline.AssessAsync(context, new ToolAction(read, Fixtures.Json("""{"query":"x","extra":{"note":"/home/user/.config/corvid-vault/vault.json"}}""")), Ct);
        Assert.Equal(ApprovalOutcome.Deny, hidden.Outcome);
        var explicitTarget = new ToolAction(write, Fixtures.Json("""{"body":"x"}"""),
            [new ActionTarget(TargetKind.Path, "/home/user/.config/corvid/approvals.json", TargetAccess.Write)]);
        Assert.Contains((await pipeline.AssessAsync(context, explicitTarget, Ct)).Reasons, r => r.Code == HardlineRules.SafetyConfigurationChange);
        var credential = await pipeline.AssessAsync(context, new ToolAction(read, Fixtures.Json("""{"path":"~/.aws/credentials"}""")), Ct);
        Assert.Equal(ApprovalOutcome.Ask, credential.Outcome);
    }

    [Fact]
    public async Task A_remembered_send_covers_only_the_recipients_approved()
    {
        using var db = new TempLog();
        var pipeline = Fixtures.Pipeline(db, prompter: ScriptedPrompter.Always(ApprovalAnswer.Approve(ApprovalScope.Session)));
        var context = Fixtures.Context(ApprovalMode.Manual);
        var send = Fixtures.Tool("send_message", SideEffects.Messaging, parameters: "to");
        var toSam = new ToolAction(send, Fixtures.Json("""{"to":"sam","text":"hi"}"""), Recipients: ["sam"]);

        Assert.Equal(ApprovalResolution.UserApproved, (await pipeline.AuthorizeAsync(context, toSam, Ct)).Resolution);
        Assert.Equal(["recipient:sam"], Assert.Single(pipeline.SessionRules(Session)).Names);
        Assert.Equal(ApprovalOutcome.Auto, (await pipeline.AssessAsync(context, toSam with { Arguments = Fixtures.Json("""{"to":"sam","text":"again"}""") }, Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context, toSam with { Recipients = ["mallory"] }, Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(context, toSam with { Recipients = ["sam", "mallory"] }, Ct)).Outcome);
    }

    [Fact]
    public async Task Tainted_turns_ask_for_network_egress_and_new_recipients()
    {
        using var db = new TempLog();
        var pipeline = Fixtures.Pipeline(db, new FixedRiskDecider(level: 0), ScriptedPrompter.Always(ApprovalAnswer.Approve(ApprovalScope.Session)));
        var clean = Fixtures.Context(ApprovalMode.Permissive);
        var tainted = clean with { Tainted = true };

        var fetch = new ToolAction(Fixtures.Tool("web_fetch", network: true, parameters: "url"), Fixtures.Json("""{"url":"https://example.org/a"}"""));
        Assert.Equal(ApprovalOutcome.Auto, (await pipeline.AssessAsync(clean, fetch, Ct)).Outcome);
        var taintedFetch = await pipeline.AssessAsync(tainted, fetch, Ct);
        Assert.Equal(ApprovalOutcome.Ask, taintedFetch.Outcome);
        Assert.Contains(taintedFetch.Reasons, r => r.Code == ApprovalReasons.TaintedEgress);

        var send = Fixtures.Tool("send_message", SideEffects.Messaging, parameters: "to");
        var toSam = new ToolAction(send, Fixtures.Json("""{"to":"sam","text":"hi"}"""), Recipients: ["sam"]);
        var known = tainted with { KnownRecipients = new HashSet<string>(["sam"]) };
        Assert.Equal(ApprovalOutcome.Auto, (await pipeline.AssessAsync(known, toSam, Ct)).Outcome);
        var stranger = await pipeline.AssessAsync(known, toSam with { Recipients = ["mallory"] }, Ct);
        Assert.Equal(ApprovalOutcome.Ask, stranger.Outcome);
        Assert.Contains(stranger.Reasons, r => r.Code == ApprovalReasons.TaintedRecipient);
        Assert.Equal(ApprovalOutcome.Ask, (await pipeline.AssessAsync(known, toSam with { Recipients = default }, Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Auto, (await pipeline.AssessAsync(clean, toSam with { Recipients = ["mallory"] }, Ct)).Outcome);

        // A session approval of a download does not carry over into a tainted turn.
        var manual = Fixtures.Context(ApprovalMode.Manual);
        await pipeline.AuthorizeAsync(manual, new ShellAction("curl -o page.html https://example.org"), Ct);
        Assert.Equal(ApprovalOutcome.Auto, (await pipeline.AssessAsync(manual, new ShellAction("curl -o page.html https://example.org"), Ct)).Outcome);
        Assert.Equal(ApprovalOutcome.Ask,
            (await pipeline.AssessAsync(manual with { Tainted = true }, new ShellAction("curl -o page.html https://example.org"), Ct)).Outcome);
    }
    [Fact]
    public async Task A_shell_tool_call_is_assessed_as_the_command_it_runs()
    {
        using var db = new TempLog();
        var prompter = ScriptedPrompter.Always(ApprovalAnswer.Approve());
        var pipeline = Fixtures.Pipeline(db, prompter: prompter,
            options: new ApprovalOptions { ShellTools = new Dictionary<string, string> { ["shell"] = "command" } });
        var shell = Fixtures.Tool("shell", SideEffects.Process | SideEffects.Filesystem, destructive: true, parameters: "command");

        // Read-only commands run without asking; the hardline list sees through the tool call to the parsed command.
        var listing = await pipeline.AuthorizeAsync(Fixtures.Context(), new ToolAction(shell, Fixtures.Json("""{"command":"ls -la src"}""")), Ct);
        Assert.True(listing.Allowed);
        Assert.Equal(ApprovalResolution.Automatic, listing.Resolution);
        Assert.Equal("shell", listing.Assessment.Analysis.Kind);
        Assert.Equal("ls -la src", listing.Assessment.Analysis.Text);

        var vault = await pipeline.AuthorizeAsync(Fixtures.Context(),
            new ToolAction(shell, Fixtures.Json("""{"command":"timeout 5 cat ~/.local/share/corvid-vault/store.bin"}""")), Ct);
        Assert.False(vault.Allowed);
        Assert.Equal(ApprovalResolution.Hardline, vault.Resolution);
        var wrapped = await pipeline.AuthorizeAsync(Fixtures.Context(),
            new ToolAction(shell, Fixtures.Json("""{"command":"bash -c 'rm -rf /home/user/other'"}""")), Ct);
        Assert.Equal(ApprovalResolution.Hardline, wrapped.Resolution);
        Assert.Empty(prompter.Prompts);

        // A side effect asks, with the parsed command in the request.
        var remove = await pipeline.AuthorizeAsync(Fixtures.Context(), new ToolAction(shell, Fixtures.Json("""{"command":"rm -rf build"}""")), Ct);
        Assert.True(remove.Allowed);
        Assert.Equal("rm", Assert.Single(Assert.Single(prompter.Prompts).Analysis.Commands).Verb);

        // Without its command the call cannot be parsed, so it stays an unresolved tool call that asks once.
        var missing = await pipeline.AssessAsync(Fixtures.Context(), new ToolAction(shell, Fixtures.Json("""{"cmd":"rm -rf /"}""")), Ct);
        Assert.Equal(ApprovalOutcome.Ask, missing.Outcome);
        Assert.Equal("tool", missing.Analysis.Kind);
        Assert.Equal([ApprovalScope.Once], missing.Offered);
    }
}
