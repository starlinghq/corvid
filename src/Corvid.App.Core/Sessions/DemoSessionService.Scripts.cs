using System.Collections.Immutable;
using System.Globalization;
using Corvid.App.Core.Shell;

namespace Corvid.App.Core.Sessions;

public sealed partial class DemoSessionService
{
    // How fast the scripted engine streams: a chunk of a few words this often.
    private const int ChunkMilliseconds = 45;

    /// <summary>
    /// Main.png's turn 12 goes on: the shell call finishes, the answer streams to its end, the turn ends, and the queue
    /// ("also check the retry count") goes next.
    /// </summary>
    private void ResumeTurn12(DemoSession session)
    {
        var script = new Script(this, session);
        session.Script = script;
        var shellStarted = (session.State.Transcript.OfType<ToolEntry>().FirstOrDefault(tool => tool.CallId == "call-4"))?.StartedAt ?? _clock;
        var steers = new List<string>();
        script
            .Then(300, () => Emit(session, new ToolCallUpdate("call-4") { Tail = ShellTailDone }))
            .Then(400, () =>
            {
                Emit(session, new ToolCallUpdate("call-4")
                {
                    Status = ToolCallStatus.Completed,
                    Outcome = ToolOutcome.Succeeded,
                    Summary = "1 failed",
                    Output = ShellTailDone,
                    Elapsed = _clock - shellStarted,
                });
                Emit(session, new CheckpointRecorded("c-44b", CheckpointKind.After) { CallId = "call-4", Before = "c-44", Turn = 12 });
            });
        // The generation in flight finishes its answer; a steer reaches the turn at the checkpoint after it.
        Stream(script, session, () => Turn12AnswerRest);
        script.Then(200, () => steers.AddRange(DeliverSteers(session)));
        Stream(script, session, () => Acknowledge(steers).TrimStart());
        script.Then(200, () => FinishTurn(session, script, tokens: 1300, cost: 0.05m));
        script.Go();
    }

    /// <summary>
    /// The turn after turn 12: Approval.png's. Ingress, a step, then the shell call that asks a-32, with a-33 queued
    /// behind it; it waits for the answers, and each one decides what happens next.
    /// </summary>
    private void ApprovalTurn(DemoSession session, string text)
    {
        var script = new Script(this, session);
        session.Script = script;
        var turn = session.State.Turn + 1;
        Emit(session, new UserMessage(text));
        Emit(session, new TurnStarted(turn, session.State.Model ?? Claude) { MaxSteps = 6 });
        script
            .Then(400, () =>
            {
                Emit(session, Turn13Ingress(_clock));
                Emit(session, new TurnProgress(1) { MaxSteps = 6 });
            })
            .Then(500, () =>
            {
                Emit(session, new TurnProgress(2) { MaxSteps = 6 });
                Emit(session, Turn13Step(_clock));
            })
            .Then(400, () =>
            {
                Emit(session, ShellCall(default));
                Emit(session, Turn13PreExecute(_clock));
                var deadline = _clock.AddMinutes(5);
                Emit(session, ShellApproval(default, deadline, TaintClock(session)));
                Emit(session, PatchApproval(default, deadline.AddSeconds(47)));
                script.Wait();
                WaitOnShellApproval(session, script);
            });
        script.Go();
    }

    /// <summary>What answering a-32 does: the shell call runs or is denied, then a-33's call is announced and waits.</summary>
    private void WaitOnShellApproval(DemoSession session, Script script)
    {
        ArmDeadline(session, script, "a-32");
        ArmDeadline(session, script, "a-33");
        script.Approvals["a-32"] = (answer, timedOut) =>
        {
            Emit(session, Resolve("a-32", answer, timedOut, "`rm -r` under build/ and `dotnet test` in the workspace"));
            QueueReason(session, answer, timedOut);
            if (Approved(answer, timedOut))
            {
                var started = _clock;
                script
                    .Then(200, () =>
                    {
                        Emit(session, new ToolCallUpdate("call-5") { Status = ToolCallStatus.InProgress, Tail = "removed build/ (212 files)" });
                        Emit(session, new CheckpointRecorded("c-45", CheckpointKind.Before)
                        {
                            CallId = "call-5",
                            Tool = "shell",
                            Target = "rm -rf build/ && dotnet test Corvid.slnx",
                            Turn = session.State.Turn,
                        });
                    })
                    .Then(600, () => Emit(session, new ToolCallUpdate("call-5") { Tail = "removed build/ (212 files)\n  Determining projects to restore...\n  Build succeeded." }))
                    .Then(700, () => Emit(session, new ToolCallUpdate("call-5")
                    {
                        Tail = "removed build/ (212 files)\n  Build succeeded.\n[xUnit.net 00:00:09.40]   Finished: Corvid.Sandbox.Tests\nPassed 12   Failed 0   Skipped 0   Total 12   (11.8 s)",
                    }))
                    .Then(300, () =>
                    {
                        Emit(session, new ToolCallUpdate("call-5")
                        {
                            Status = ToolCallStatus.Completed,
                            Outcome = ToolOutcome.Succeeded,
                            Summary = "12 passed",
                            Output = "removed build/ (212 files)\nBuild succeeded.\nPassed 12   Failed 0   Skipped 0   Total 12   (11.8 s)",
                            Elapsed = _clock - started,
                        });
                        Emit(session, new CheckpointRecorded("c-46", CheckpointKind.After)
                        {
                            CallId = "call-5",
                            Before = "c-45",
                            Turn = session.State.Turn,
                            Changes = [new FileChange("build/", 0, 0)],
                        });
                    });
            }

            script.Then(300, () =>
            {
                Emit(session, new TurnProgress(3) { MaxSteps = 6 });
                Emit(session, new ToolCall("call-6", "patch_file", ToolKind.Edit, ToolCallStatus.Pending)
                {
                    Target = DemoFiles.BubblewrapPath,
                    Locations = [DemoFiles.BubblewrapPath],
                });
                script.Wait();
            });
            script.Approvals["a-33"] = (patch, patchTimedOut) => AnswerPatch(session, script, patch, patchTimedOut, Approved(answer, timedOut));
            script.Go();
        };
    }

    /// <summary>What answering a-33 does: the launcher is patched (against the task's wish) or left alone, then the answer.</summary>
    private void AnswerPatch(DemoSession session, Script script, ApprovalAnswer answer, bool timedOut, bool shellRan)
    {
        Emit(session, Resolve("a-33", answer, timedOut, $"`patch_file` under {DemoFiles.BubblewrapPath}"));
        QueueReason(session, answer, timedOut);
        var steers = new List<string>();
        string text;
        if (Approved(answer, timedOut))
        {
            var started = _clock;
            script
                .Then(200, () =>
                {
                    Emit(session, new ToolCallUpdate("call-6") { Status = ToolCallStatus.InProgress });
                    Emit(session, new CheckpointRecorded("c-47", CheckpointKind.Before)
                    {
                        CallId = "call-6",
                        Tool = "patch_file",
                        Target = DemoFiles.BubblewrapPath,
                        Turn = session.State.Turn,
                    });
                })
                .Then(300, () =>
                {
                    Emit(session, new ToolCallUpdate("call-6")
                    {
                        Status = ToolCallStatus.Completed,
                        Outcome = ToolOutcome.Succeeded,
                        Summary = "+2 −0",
                        Elapsed = _clock - started,
                        Diffs = [new FileEdit(DemoFiles.BubblewrapPath, DemoFiles.BubblewrapBefore, DemoFiles.BubblewrapAfter)],
                    });
                    Emit(session, new CheckpointRecorded("c-48", CheckpointKind.After)
                    {
                        CallId = "call-6",
                        Before = "c-47",
                        Turn = session.State.Turn,
                        Changes = [new FileChange(DemoFiles.BubblewrapPath, 2, 0)],
                    });
                    Emit(session, new ChangesUpdate
                    {
                        Turn = [new FileChange(DemoFiles.BubblewrapPath, 2, 0)],
                        Session =
                        [
                            new FileChange(DemoFiles.BubblewrapPath, 21, 5), new FileChange(DemoFiles.SeccompTestsPath, 9, 4),
                            new FileChange(".github/workflows/ci.yml", 3, 0), new FileChange("vault.json", 0, 0) { Readable = false },
                        ],
                        SinceTurn = 1,
                    });
                });
            text = "The launcher's preflight now names the missing sysctl, so a runner without user namespaces fails with its fix "
                + "instead of a bare `unshare` error. That touches `BubblewrapSandbox.cs`, the production launcher you asked me to "
                + "leave alone; `/undo` takes it back from checkpoint c-47.";
        }
        else
        {
            text = "I left `BubblewrapSandbox.cs` as it is, so the production launcher is untouched. The test is the only change.";
        }

        if (shellRan)
            text = "With `build/` cleared, the suite passes: 12 of 12, the control step included.\n\n" + text;
        text += "\n\nThe retry count in `ci.yml` is `retry: 2` on the test job, which would hide a flaky failure; I would set it to `0`.";
        script.Then(300, () =>
        {
            Emit(session, new TurnProgress(4) { MaxSteps = 6 });
            steers.AddRange(DeliverSteers(session));
        });
        Stream(script, session, () => text + Acknowledge(steers));
        script.Then(200, () => FinishTurn(session, script, tokens: 900, cost: 0.03m));
        script.Go();
    }

    /// <summary>A turn of the scripted engine: ingress, one read-only call that runs and finishes, a thought, the answer streamed.</summary>
    private void ScriptedTurn(DemoSession session, string text)
    {
        var script = new Script(this, session);
        session.Script = script;
        var turn = session.State.Turn + 1;
        var model = session.State.Model ?? Qwen;
        var callId = $"call-{session.Id[^4..]}-{++session.Calls}";
        var keyword = Keyword(text);
        var steers = new List<string>();
        Emit(session, new UserMessage(text));
        Emit(session, new TurnStarted(turn, model) { MaxSteps = 3 });
        script
            .Then(350, () =>
            {
                Emit(session, new CheckpointDecided("ingress", Kev,
                [
                    new DecisionAnswer("intent", 0.92, "task", true),
                    new DecisionAnswer("difficulty", 0.34, "local", true),
                    new DecisionAnswer("recall", 0.81, "yes", true),
                    new DecisionAnswer("clarify", 0.12, "no", true),
                ]) { Latency = TimeSpan.FromMilliseconds(246) });
                Emit(session, new TurnProgress(1) { MaxSteps = 3 });
            })
            .Then(300, () => Emit(session, new ToolCall(callId, "search_files", ToolKind.Search, ToolCallStatus.InProgress)
            {
                Target = $"\"{keyword}\" in src/",
                Verdict = new CallVerdict(VerdictKind.ReadOnly),
            }))
            .Then(600, () => Emit(session, new ToolCallUpdate(callId)
            {
                Status = ToolCallStatus.Completed,
                Outcome = ToolOutcome.Succeeded,
                Summary = "4 hits",
                Elapsed = TimeSpan.FromSeconds(0.6),
                Output = $"src/Corvid.Sandbox/BubblewrapSandbox.cs:88: // {keyword}\n"
                    + $"src/Corvid.Host/Composition/CorvidRuntime.cs:212: // {keyword}\n"
                    + $"tests/Corvid.Sandbox.Tests/SeccompTests.cs:46: // {keyword}\n"
                    + $".github/workflows/ci.yml:14: # {keyword}",
            }))
            .Then(200, () =>
            {
                steers.AddRange(DeliverSteers(session));
                Emit(session, new TurnProgress(2) { MaxSteps = 3 });
                Emit(session, new AgentThoughtChunk($"Four files mention {keyword}; the workflow is the one that decides what CI does. "));
            })
            .Then(400, () => Emit(session, new AgentThoughtChunk("Say where each is and what I would change, briefly.")))
            .Then(300, () => { });
        Stream(script, session, () => (steers.Count > 0 ? Acknowledge(steers).TrimStart() + "\n\n" : "") + Answer(keyword));
        script.Then(200, () => FinishTurn(session, script, tokens: 420, cost: null));
        script.Go();
    }

    /// <summary>cli-a410's turn, which waited on its question, goes on with the version the user chose.</summary>
    private void ResumeReleaseNotes(DemoSession session, string version)
    {
        var script = new Script(this, session);
        session.Script = script;
        var started = session.State.Transcript.OfType<ToolEntry>().FirstOrDefault(tool => tool.CallId == "call-a3")?.StartedAt ?? _clock;
        var notes = DemoFiles.ReleaseNotes.Replace("Corvid 0.2", $"Corvid {version}", StringComparison.Ordinal);
        script.Then(400, () =>
        {
            Emit(session, new ToolCallUpdate("call-a3")
            {
                Status = ToolCallStatus.Completed,
                Outcome = ToolOutcome.Succeeded,
                Summary = "new file",
                Elapsed = _clock - started,
                Diffs = [new FileEdit(DemoFiles.ReleaseNotesPath, null, notes)],
            });
            Emit(session, new ChangesUpdate
            {
                Turn = [new FileChange(DemoFiles.ReleaseNotesPath, FileDiffLines(notes), 0)],
                Session = [new FileChange(DemoFiles.ReleaseNotesPath, FileDiffLines(notes), 0)],
            });
            Emit(session, new TurnProgress(4) { MaxSteps = 5 });
        });
        Stream(script, session, () => $"Drafted `{DemoFiles.ReleaseNotesPath}` for **{version}**: three features and one fix, "
            + "from `CHANGELOG.md` and 18 merged pull requests. Review it before tagging.");
        script.Then(200, () => FinishTurn(session, script, tokens: 1100, cost: null));
        script.Go();
    }

    private static int FileDiffLines(string text) => Diffs.FileDiff.SplitLines(text).Count;

    /// <summary>Streams text as the generator would: a few words per chunk.</summary>
    private void Stream(Script script, DemoSession session, Func<string> text)
    {
        // The text is decided when the stream starts, so a steer delivered before it is part of it.
        var chunks = new Queue<string>();
        script.Then(ChunkMilliseconds, () =>
        {
            foreach (var chunk in Chunks(text()))
                chunks.Enqueue(chunk);
            if (chunks.Count > 0)
                Emit(session, new AgentMessageChunk(chunks.Dequeue()));
            StreamNext(script, session, chunks);
        });
    }

    private void StreamNext(Script script, DemoSession session, Queue<string> chunks)
    {
        if (chunks.Count == 0)
            return;
        script.Insert(ChunkMilliseconds, () =>
        {
            Emit(session, new AgentMessageChunk(chunks.Dequeue()));
            StreamNext(script, session, chunks);
        });
    }

    /// <summary>Text in chunks of about three words, keeping the spaces and line breaks where they were.</summary>
    internal static IEnumerable<string> Chunks(string text)
    {
        var start = 0;
        var words = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not (' ' or '\n'))
                continue;
            words++;
            if (words < 3 && text[i] != '\n')
                continue;
            yield return text[start..(i + 1)];
            start = i + 1;
            words = 0;
        }

        if (start < text.Length)
            yield return text[start..];
    }

    private void FinishTurn(DemoSession session, Script script, int tokens, decimal? cost)
    {
        var state = session.State;
        Emit(session, new UsageUpdate { TurnTokens = tokens, TurnCost = cost });
        Emit(session, new TurnEnded(state.Turn, TurnOutcome.Answered)
        {
            Elapsed = state.TurnStartedAt is { } started ? _clock - started : null,
            Tokens = tokens,
            Cost = cost,
        });
        script.End();
        session.Script = null;
        StartNextQueued(session, TimeSpan.FromMilliseconds(400));
    }

    /// <summary>Steers waiting in the queue reach the running turn at this checkpoint (K-3).</summary>
    private List<string> DeliverSteers(DemoSession session)
    {
        var steers = session.Queue.Where(message => message.Steer).ToList();
        if (steers.Count == 0)
            return [];
        SetQueue(session, session.Queue.RemoveAll(message => message.Steer));
        foreach (var steer in steers)
            Emit(session, new UserMessage(steer.Text) { Steered = true });
        return [.. steers.Select(steer => steer.Text)];
    }

    private static string Acknowledge(List<string> steers) => steers.Count == 0
        ? ""
        : "\n\nOn your note (" + string.Join("; ", steers.Select(steer => $"\"{steer}\"")) + "): I took it into account before writing this.";

    private static string Answer(string keyword) =>
        $"Four files mention **{keyword}**:\n\n"
        + "- `src/Corvid.Sandbox/BubblewrapSandbox.cs`, where the launcher starts the sandbox\n"
        + "- `src/Corvid.Host/Composition/CorvidRuntime.cs`, in the session setup\n"
        + "- `tests/Corvid.Sandbox.Tests/SeccompTests.cs`, in the control step\n"
        + "- `.github/workflows/ci.yml`, on the test job\n\n"
        + "Nothing here needs to change for it; the workflow is the one place that decides what CI does:\n\n"
        + "```yaml\n- name: test\n  run: dotnet test Corvid.slnx -c Release\n```";

    /// <summary>The word a scripted search looks for: the message's longest word.</summary>
    internal static string Keyword(string text)
    {
        var words = text.Split([' ', '\t', '\n', ',', '.', ':', ';', '?', '!', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word.Length >= 4 && word.All(char.IsLetterOrDigit))
            .ToList();
        return words.Count == 0 ? "TODO" : words.OrderByDescending(word => word.Length).First().ToLowerInvariant();
    }

    private static bool Approved(ApprovalAnswer answer, bool timedOut) => !timedOut && answer.Choice != ApprovalChoice.Deny;

    private static ApprovalResolved Resolve(string approvalId, ApprovalAnswer answer, bool timedOut, string rule)
    {
        if (timedOut)
            return new ApprovalResolved(approvalId, ApprovalOutcome.TimedOut) { Reason = "deadline" };
        var resolved = Resolution(approvalId, answer);
        return answer.Choice is ApprovalChoice.Session or ApprovalChoice.Always
            ? resolved with { Rule = answer.RulePaths.IsDefaultOrEmpty ? rule : NarrowedRule(rule, answer.RulePaths) }
            : resolved;
    }

    /// <summary>The rule in words with the paths the user narrowed it to.</summary>
    private static string NarrowedRule(string rule, ImmutableArray<ImmutableArray<string>> paths)
    {
        var first = paths.FirstOrDefault(list => !list.IsDefaultOrEmpty);
        if (first.IsDefaultOrEmpty)
            return rule;
        var under = rule.IndexOf(" under ", StringComparison.Ordinal);
        var and = rule.IndexOf(" and ", StringComparison.Ordinal);
        if (under < 0)
            return rule;
        var tail = and > under ? rule[and..] : "";
        return $"{rule[..under]} under {string.Join(" ", first)}{tail}";
    }

    /// <summary>"Deny and say why": the reason goes as the next message, ahead of anything queued.</summary>
    private void QueueReason(DemoSession session, ApprovalAnswer answer, bool timedOut)
    {
        if (timedOut || answer.Choice != ApprovalChoice.Deny || string.IsNullOrWhiteSpace(answer.Reason))
            return;
        SetQueue(session, session.Queue.Insert(0, new QueuedMessage($"q-{++session.QueueCounter}", answer.Reason.Trim())));
    }

    /// <summary>A prompt nobody answers is denied at its deadline (the timer runs on the provider's clock).</summary>
    private void ArmDeadline(DemoSession session, Script script, string approvalId)
    {
        if (session.State.Approvals.FirstOrDefault(open => open.ApprovalId == approvalId) is not { } request)
            return;
        var wait = request.Deadline - _clock;
        if (wait < TimeSpan.Zero)
            wait = TimeSpan.Zero;
        script.Hold(_time.CreateTimer(_ => _dispatcher.Post(() =>
        {
            if (script.Ended || session.State.Approvals.All(open => open.ApprovalId != approvalId))
                return;
            AdvanceClock(request.Deadline);
            if (script.Approvals.Remove(approvalId, out var handler))
                handler(ApprovalAnswer.Deny, true);
            else
                Emit(session, new ApprovalResolved(approvalId, ApprovalOutcome.TimedOut) { Reason = "deadline" });
        }), null, wait, Timeout.InfiniteTimeSpan));
    }

    private void Compact(DemoSession session, string? focus)
    {
        var through = Math.Max(1, session.State.Turn - 1);
        Emit(session, new CompactionUpdate(1, through)
        {
            Goal = session.State.Goal,
            Decisions = ["fix the test, not the launcher", "a session rule allows `dotnet test` in the workspace"],
            Files = [DemoFiles.SeccompTestsPath, DemoFiles.BubblewrapPath, ".github/workflows/ci.yml"],
            NextStep = "make the control step tell the host's refusal from the sandbox's",
            Summarizer = Qwen,
            Focus = string.IsNullOrWhiteSpace(focus) ? null : focus.Trim(),
        });
        if (session.State.ContextWindow is { } window)
            Emit(session, new UsageUpdate { ContextTokens = window / 10 });
    }

    public Task RunCommandAsync(string sessionId, string line, CancellationToken cancellationToken = default)
    {
        var session = Writable(sessionId);
        var trimmed = line.Trim();
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        var name = (space < 0 ? trimmed[1..] : trimmed[1..space]).ToLowerInvariant();
        var rest = space < 0 ? "" : trimmed[(space + 1)..].Trim();
        var state = session.State;
        CommandResult result;
        switch (name)
        {
            case "help":
                result = new CommandResult(trimmed)
                {
                    Title = "Commands",
                    Columns = ["command", "what it does"],
                    Rows = [.. Commands.Select(command => ImmutableArray.Create($"/{command.Name}{(command.Usage.Length > 0 ? " " + command.Usage : "")}", command.Summary))],
                };
                break;
            case "status":
                result = new CommandResult(trimmed)
                {
                    Title = "Status",
                    Lines =
                    [
                        $"generator: {Summary(session).Model}",
                        $"decider: {state.Decider ?? "degraded tier"}",
                        $"context: {(state.ContextFraction is { } fraction ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(fraction * 100)}%") : "not reported")}",
                        $"approval mode: {(state.Mode is { } mode ? SessionText.Mode(mode) : "smart")}",
                        $"scheduler: {state.Scheduler ?? "off"}",
                        $"tainted: {(state.Tainted ? $"yes ({state.TaintSource})" : "no")}",
                    ],
                };
                break;
            case "goal" when rest.Length == 0:
                result = new CommandResult(trimmed) { Lines = [state.Goal is { } goal ? $"goal: {goal}" : "No standing goal. /goal <text> sets one."] };
                break;
            case "goal":
            {
                var clear = rest.Equals("clear", StringComparison.OrdinalIgnoreCase);
                Emit(session, new GoalChanged(clear ? null : rest));
                result = new CommandResult(trimmed) { Lines = [clear ? "Goal cleared." : $"Goal set: {rest}"], Tone = OutputTone.Success };
                break;
            }

            case "compact" when state.Running:
                result = new CommandResult(trimmed) { Lines = ["Compaction runs between turns; this one is still running."], Tone = OutputTone.Warning };
                break;
            case "compact":
                Compact(session, rest);
                result = new CommandResult(trimmed)
                {
                    Lines = [string.Create(CultureInfo.InvariantCulture, $"compacted turns 1 to {Math.Max(1, state.Turn - 1)} by {Qwen} in 1 chunk")],
                    Tone = OutputTone.Success,
                };
                break;
            case "todos":
                result = new CommandResult(trimmed)
                {
                    Title = "Todos",
                    Columns = ["", "task"],
                    Rows = [.. state.Plan.Select(entry => ImmutableArray.Create(entry.Status switch
                    {
                        PlanEntryStatus.Completed => "done",
                        PlanEntryStatus.InProgress => "doing",
                        _ => "to do",
                    }, entry.Text))],
                };
                break;
            case "approvals":
                result = new CommandResult(trimmed)
                {
                    Title = "Approvals",
                    Lines = state.Approvals.IsEmpty ? ["Nothing waits on you here."] : [.. state.Approvals.Select(open => $"{open.ApprovalId}: {open.Text}")],
                    Columns = ["scope", "rule", "source"],
                    Rows =
                    [
                        ["session", "allow dotnet test in the workspace", "approval a-31, turn 10"],
                        ["always", "allow git status, git diff", "approval a-7, cli-8c11"],
                    ],
                };
                break;
            case "cancel" when state.Running:
                Interrupt(session);
                result = new CommandResult(trimmed) { Lines = ["Cancelled the running turn; its work so far stays."] };
                break;
            case "cancel":
                result = new CommandResult(trimmed) { Lines = ["No turn is running."] };
                break;
            case "model":
                result = new CommandResult(trimmed)
                {
                    Title = "Model slots",
                    Columns = ["slot", "model", "device"],
                    Rows =
                    [
                        ["generator", Summary(session).Model ?? Qwen, state.Generator?.Device ?? "cpu"],
                        ["decider", "kev-0.8b", "cuda:0"],
                        ["summarizer", Qwen, "cuda:0"],
                        ["embedder.text", "embeddinggemma-2", "cpu"],
                    ],
                };
                break;
            default:
                result = new CommandResult(trimmed) { Lines = [$"Unknown command /{name}. /help lists them."], Tone = OutputTone.Error };
                break;
        }

        Emit(session, result);
        return Task.CompletedTask;
    }
}
