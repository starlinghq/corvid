using System.Collections.Immutable;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// The fold from session updates to view state (docs/plan/ui.md §8): <c>state = Apply(state, update)</c>, pure and
/// deterministic, the same for a session replayed from the log and one streaming live. The transcript it builds is a
/// flat list of entries in reading order, the rest the panels' and the status bar's state.
/// <para>
/// Placement rules: a turn's entries follow its header in the order they happened, except its thought row, which stays
/// at the turn's foot; the open approval card follows its tool card; each turn's summary chips row sits under its header.
/// An update this version does not know is ignored, as ACP clients ignore unknown updates.
/// </para>
/// </summary>
public static class SessionFold
{
    public static SessionView Start(string sessionId) => new() { SessionId = sessionId };

    /// <summary>The state after every update, in order: what opening a session shows.</summary>
    public static SessionView Replay(string sessionId, IEnumerable<SessionUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var state = Start(sessionId);
        foreach (var update in updates)
            state = Apply(state, update);
        return state;
    }

    public static SessionView Apply(SessionView state, SessionUpdate update)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(update);
        if (update.At > state.LastActivity)
            state = state with { LastActivity = update.At };
        // Thinking ends when anything else happens.
        if (update is not AgentThoughtChunk && state.ThinkingOpen)
            state = EndThinking(state, update.At);

        var next = update switch
        {
            UserMessage message => OnUserMessage(state, message),
            AgentMessageChunk chunk => OnAnswerChunk(state, chunk),
            AgentThoughtChunk thought => OnThoughtChunk(state, thought),
            TurnStarted started => OnTurnStarted(state, started),
            TurnProgress progress => OnTurnProgress(state, progress),
            TurnEnded ended => OnTurnEnded(state, ended),
            UsageUpdate usage => OnUsage(state, usage),
            PlanUpdate plan => state with { Plan = plan.Entries.IsDefault ? [] : plan.Entries },
            TitleChanged title => state with { Title = SafeText.Clean(title.Title) },
            GoalChanged goal => state with { Goal = NonBlank(SafeText.Clean(goal.Goal)) },
            ApprovalModeChanged mode => state with { Mode = mode.Mode },
            SessionForked fork => state with { ForkOf = fork.FromSession, ForkTurn = fork.AtTurn },
            DriverChanged driver => state with { ReadOnly = driver.ReadOnly, Holder = driver.ReadOnly ? NonBlank(SafeText.Clean(driver.Holder)) : null },
            AvailableCommandsUpdate commands => state with { Commands = commands.Commands.IsDefault ? [] : commands.Commands },
            QueueChanged queue => state with { Queue = queue.Items.IsDefault ? [] : queue.Items },
            CommandResult result => AddNew(state, "command", id => new CommandEntry(id, result)),
            ToolCall call => OnToolCall(state, call),
            ToolCallUpdate change => OnToolCallUpdate(state, change),
            ApprovalRequested request => OnApprovalRequested(state, request),
            ApprovalResolved resolved => OnApprovalResolved(state, resolved),
            DetectionMatched detection => AddNew(state, "detection", id => new DetectionEntry(id, detection.Detection, detection.At)),
            CheckpointDecided decided => OnDecided(state, decided),
            ModelSwitched switched => OnModelSwitched(state, switched),
            TaintChanged taint => OnTaint(state, taint),
            ChangesUpdate changes => state with
            {
                TurnChanges = changes.Turn.IsDefault ? [] : changes.Turn,
                SessionChanges = changes.Session.IsDefault ? [] : changes.Session,
                ChangesSinceTurn = changes.SinceTurn,
            },
            CheckpointRecorded checkpoint => OnCheckpoint(state, checkpoint),
            SessionRewound rewound => OnRewound(state, rewound),
            EngineStatus engine => OnEngine(state, engine),
            QuestionAsked question => OnQuestion(state, question),
            QuestionAnswered answered => OnQuestionAnswered(state, answered),
            CompactionUpdate compaction => AddNew(state, "compaction", id => new CompactionEntry(id, compaction) { Turn = 0 }),
            Notice notice => AddNew(state, "notice", id => new NoticeEntry(id, notice)),
            _ => state,
        };

        return update is AgentMessageChunk or AgentThoughtChunk or UsageUpdate or EngineStatus or QueueChanged or PlanUpdate
            ? next
            : Arrange(next);
    }

    /// <summary>The entry with this id, or null.</summary>
    public static TranscriptEntry? Find(SessionView state, string id)
    {
        var index = IndexOf(state.Transcript, id);
        return index < 0 ? null : state.Transcript[index];
    }

    /// <summary>The id a turn's header has, which its chips row and thought row extend.</summary>
    public static string TurnId(int turn) => $"turn-{turn}";

    public static string ToolId(string callId) => "tool-" + callId;

    public static string ApprovalId(string approvalId) => "approval-" + approvalId;

    public static string QuestionId(string questionId) => "question-" + questionId;

    /// <summary>A cleaned engine string, or null when the update did not carry one (so the old value stays).</summary>
    private static string? Cleaned(string? text) => text is null ? null : SafeText.Clean(text);

    /// <summary>Cleaned text, trimmed; null when nothing visible is left (a goal of only zero-width characters is no goal).</summary>
    private static string? NonBlank(string cleaned) => string.IsNullOrWhiteSpace(cleaned) ? null : cleaned.Trim();

    private static SessionView OnUserMessage(SessionView state, UserMessage message)
    {
        if (message.Steered && state.Running)
        {
            var steer = new UserEntry(NewId(state, "steer"), message.Text, message.At)
            {
                Turn = state.Turn,
                Steered = true,
                Position = message.Position,
            };
            return Add(Advance(state), steer);
        }

        // A prompt belongs to the turn it starts; TurnStarted confirms the number.
        var prompt = new UserEntry(NewId(state, "user"), message.Text, message.At)
        {
            Turn = state.Turn + 1,
            Position = message.Position,
        };
        return Add(Advance(state), prompt);
    }

    private static SessionView OnTurnStarted(SessionView state, TurnStarted started)
    {
        if (state.Running)
            state = CloseTurn(state, null);
        var transcript = state.Transcript;
        if (transcript.Count > 0 && transcript[^1] is UserEntry { Steered: false } prompt && prompt.Turn != started.Turn)
            transcript = transcript.SetItem(transcript.Count - 1, prompt with { Turn = started.Turn });

        var id = TurnId(started.Turn);
        if (IndexOf(transcript, id) >= 0)
            id = $"{id}-{state.NextId}";
        var header = new TurnHeaderEntry(id, started.Turn, started.At)
        {
            Turn = started.Turn,
            Models = [started.Model],
            MaxSteps = started.MaxSteps,
        };
        return Advance(state) with
        {
            Transcript = transcript.Add(header),
            TurnHeaderId = id,
            Turn = started.Turn,
            Turns = state.Turns + 1,
            Running = true,
            TurnStartedAt = started.At,
            ActivitySince = started.At,
            Step = null,
            MaxSteps = started.MaxSteps,
            Model = started.Model,
        };
    }

    private static SessionView OnTurnProgress(SessionView state, TurnProgress progress)
    {
        var maxSteps = progress.MaxSteps ?? state.MaxSteps;
        state = UpdateHeader(state, header => header with { Step = progress.Step, MaxSteps = maxSteps });
        return state with { Step = progress.Step, MaxSteps = maxSteps, ActivitySince = progress.At };
    }

    private static SessionView OnTurnEnded(SessionView state, TurnEnded ended)
    {
        if (!state.Running)
            return state;
        state = UpdateHeader(state, header =>
        {
            var models = ended.Model is { Length: > 0 } model && (header.Models.IsEmpty || header.Models[^1] != model)
                ? header.Models.Add(model)
                : header.Models;
            return header with
            {
                Running = false,
                Outcome = ended.Outcome,
                Elapsed = ended.Elapsed ?? (ended.At > header.StartedAt ? ended.At - header.StartedAt : TimeSpan.Zero),
                Tokens = ended.Tokens ?? header.Tokens,
                Cost = ended.Cost ?? header.Cost,
                Error = ended.Error,
                Models = models,
            };
        });
        return CloseTurn(state, ended.Outcome);
    }

    /// <summary>
    /// What ends with a turn: streaming stops, calls still pending or running are cancelled, and open prompts close (a
    /// stale prompt runs nothing). A clarifying turn's question stays open: it is the answer.
    /// </summary>
    private static SessionView CloseTurn(SessionView state, TurnOutcome? outcome)
    {
        var turn = state.Turn;
        var transcript = state.Transcript;
        for (var i = transcript.Count - 1; i >= 0; i--)
        {
            var entry = transcript[i];
            if (entry.Turn != turn)
            {
                if (entry is TurnHeaderEntry)
                    break;
                continue;
            }

            switch (entry)
            {
                case AnswerEntry { Streaming: true } answer:
                    transcript = transcript.SetItem(i, answer with { Streaming = false });
                    break;
                case ToolEntry { Status: ToolCallStatus.Pending or ToolCallStatus.InProgress } tool:
                    transcript = transcript.SetItem(i, tool with
                    {
                        Status = ToolCallStatus.Failed,
                        Outcome = tool.Outcome ?? ToolOutcome.Cancelled,
                        Verdict = tool.ApprovalId is not null ? new CallVerdict(VerdictKind.Cancelled) { ApprovalId = tool.ApprovalId } : tool.Verdict,
                        ApprovalId = null,
                    });
                    break;
                case TurnHeaderEntry { Running: true } header:
                    transcript = transcript.SetItem(i, header with { Running = false, Outcome = header.Outcome ?? outcome });
                    break;
            }
        }

        var question = outcome is TurnOutcome.Cancelled ? null : state.Question;
        if (question is null && state.Question is { } open)
        {
            var index = IndexOf(transcript, QuestionId(open.QuestionId));
            if (index >= 0 && transcript[index] is QuestionEntry entry && entry.Answer is null)
                transcript = transcript.SetItem(index, entry with { Answer = "" });
        }

        return state with
        {
            Transcript = transcript,
            Running = false,
            TurnStartedAt = null,
            ActivitySince = null,
            Step = null,
            MaxSteps = null,
            Approvals = [],
            Question = question,
        };
    }

    private static SessionView OnAnswerChunk(SessionView state, AgentMessageChunk chunk)
    {
        var text = chunk.Text;
        if (string.IsNullOrEmpty(text))
            return state;
        var transcript = state.Transcript;
        var index = LastBodyIndex(state);
        if (index >= 0 && transcript[index] is AnswerEntry answer && answer.Turn == CurrentTurn(state) && (answer.Streaming || !state.Running))
            return state with { Transcript = transcript.SetItem(index, answer with { Text = answer.Text + text }) };
        return AddNew(state, "answer", id => new AnswerEntry(id, text, state.Running));
    }

    private static SessionView OnThoughtChunk(SessionView state, AgentThoughtChunk chunk)
    {
        if (string.IsNullOrEmpty(chunk.Text))
            return state;
        var id = ThoughtId(state);
        var index = IndexOf(state.Transcript, id);
        if (index >= 0 && state.Transcript[index] is ThoughtEntry thought)
        {
            return state with
            {
                Transcript = state.Transcript.SetItem(index, thought with
                {
                    Text = thought.Text + chunk.Text,
                    PhaseStartedAt = thought.PhaseStartedAt ?? chunk.At,
                }),
                ThinkingOpen = true,
            };
        }

        // The thought row is the turn's foot: added last, and everything after it in the turn goes above it.
        var entry = new ThoughtEntry(id, chunk.Text, TimeSpan.Zero, chunk.At) { Turn = CurrentTurn(state) };
        return state with { Transcript = state.Transcript.Add(entry), ThinkingOpen = true };
    }

    private static SessionView EndThinking(SessionView state, DateTimeOffset at)
    {
        var index = IndexOf(state.Transcript, ThoughtId(state));
        if (index >= 0 && state.Transcript[index] is ThoughtEntry { PhaseStartedAt: { } started } thought)
        {
            var phase = at > started ? at - started : TimeSpan.Zero;
            state = state with
            {
                Transcript = state.Transcript.SetItem(index, thought with { Thinking = thought.Thinking + phase, PhaseStartedAt = null }),
            };
        }

        return state with { ThinkingOpen = false };
    }

    private static SessionView OnUsage(SessionView state, UsageUpdate usage)
    {
        if (state.Running && (usage.TurnTokens is not null || usage.TurnCost is not null))
            state = UpdateHeader(state, header => header with { Tokens = usage.TurnTokens ?? header.Tokens, Cost = usage.TurnCost ?? header.Cost });
        return state with
        {
            ContextTokens = usage.ContextTokens ?? state.ContextTokens,
            ContextWindow = usage.ContextWindow ?? state.ContextWindow,
            CompactAt = usage.CompactAt ?? state.CompactAt,
            ContextBreakdown = usage.Breakdown.IsDefaultOrEmpty ? state.ContextBreakdown : usage.Breakdown,
            SessionCost = usage.SessionCost ?? state.SessionCost,
        };
    }

    private static SessionView OnToolCall(SessionView state, ToolCall call)
    {
        var id = ToolId(call.CallId);
        var index = IndexOf(state.Transcript, id);
        if (index >= 0 && state.Transcript[index] is ToolEntry existing)
        {
            var updated = existing with
            {
                Status = call.Status,
                Target = call.Target ?? existing.Target,
                Input = call.Input ?? existing.Input,
                Locations = call.Locations.IsDefaultOrEmpty ? existing.Locations : call.Locations,
                Verdict = call.Verdict ?? existing.Verdict,
                StartedAt = call.Status == ToolCallStatus.InProgress && existing.Status == ToolCallStatus.Pending ? call.At : existing.StartedAt,
            };
            state = state with { Transcript = state.Transcript.SetItem(index, updated) };
        }
        else
        {
            var entry = new ToolEntry(id, call.CallId, call.Tool, call.Kind, call.At)
            {
                Turn = CurrentTurn(state),
                Status = call.Status,
                Target = call.Target,
                Input = call.Input,
                Locations = call.Locations.IsDefault ? [] : call.Locations,
                Verdict = call.Verdict,
                ApprovalId = state.Approvals.FirstOrDefault(request => request.CallId == call.CallId)?.ApprovalId,
            };
            state = Add(state, entry);
        }

        return call.Status == ToolCallStatus.InProgress && state.Running ? state with { ActivitySince = call.At } : state;
    }

    private static SessionView OnToolCallUpdate(SessionView state, ToolCallUpdate change)
    {
        var id = ToolId(change.CallId);
        var index = IndexOf(state.Transcript, id);
        if (index < 0 || state.Transcript[index] is not ToolEntry entry)
        {
            // An update for a call never announced: announce it with what the update says.
            state = Add(state, new ToolEntry(id, change.CallId, "tool", ToolKind.Other, change.At)
            {
                Turn = CurrentTurn(state),
                Status = ToolCallStatus.Pending,
            });
            index = IndexOf(state.Transcript, id);
            entry = (ToolEntry)state.Transcript[index];
        }

        var started = change.Status == ToolCallStatus.InProgress && entry.Status == ToolCallStatus.Pending;
        var updated = entry with
        {
            Status = change.Status ?? entry.Status,
            Outcome = change.Outcome ?? entry.Outcome,
            Target = change.Target ?? entry.Target,
            Summary = change.Summary ?? entry.Summary,
            Tail = change.Tail ?? entry.Tail,
            Output = change.Output ?? entry.Output,
            Elapsed = change.Elapsed ?? entry.Elapsed,
            Diffs = change.Diffs.IsDefaultOrEmpty ? entry.Diffs : change.Diffs,
            Locations = change.Locations.IsDefaultOrEmpty ? entry.Locations : change.Locations,
            Verdict = change.Verdict ?? entry.Verdict,
            StartedAt = started ? change.At : entry.StartedAt,
        };
        state = state with { Transcript = state.Transcript.SetItem(index, updated) };
        return started && state.Running ? state with { ActivitySince = change.At } : state;
    }

    private static SessionView OnApprovalRequested(SessionView state, ApprovalRequested request)
    {
        var existing = state.Approvals.FindIndex(open => open.ApprovalId == request.ApprovalId);
        var approvals = existing >= 0 ? state.Approvals.SetItem(existing, request) : state.Approvals.Add(request);
        state = state with { Approvals = approvals };
        if (request.CallId is { } callId && IndexOf(state.Transcript, ToolId(callId)) is >= 0 and var index
            && state.Transcript[index] is ToolEntry tool)
        {
            state = state with
            {
                Transcript = state.Transcript.SetItem(index, tool with
                {
                    ApprovalId = request.ApprovalId,
                    Status = tool.Status == ToolCallStatus.InProgress ? tool.Status : ToolCallStatus.Pending,
                }),
            };
        }

        return state;
    }

    private static SessionView OnApprovalResolved(SessionView state, ApprovalResolved resolved)
    {
        var request = state.Approvals.FirstOrDefault(open => open.ApprovalId == resolved.ApprovalId);
        if (request is not null)
            state = state with { Approvals = state.Approvals.Remove(request) };

        var transcript = state.Transcript;
        for (var i = transcript.Count - 1; i >= 0; i--)
        {
            if (transcript[i] is not ToolEntry tool
                || tool.ApprovalId != resolved.ApprovalId && (request?.CallId is null || tool.CallId != request.CallId))
                continue;
            var approved = resolved.Outcome == ApprovalOutcome.Approved;
            var verdict = new CallVerdict(Verdict(resolved))
            {
                ApprovalId = resolved.ApprovalId,
                Rule = resolved.Rule,
                Detail = resolved.Reason,
                CheckpointId = request?.CheckpointId ?? tool.Verdict?.CheckpointId,
                RiskLevel = tool.Verdict?.RiskLevel,
                RiskConfidence = tool.Verdict?.RiskConfidence,
            };
            transcript = transcript.SetItem(i, tool with
            {
                ApprovalId = null,
                Verdict = verdict,
                Status = approved || tool.Status != ToolCallStatus.Pending ? tool.Status : ToolCallStatus.Failed,
                Outcome = approved || tool.Status != ToolCallStatus.Pending ? tool.Outcome : ToolOutcome.Denied,
            });
            break;
        }

        return state with { Transcript = transcript };
    }

    private static VerdictKind Verdict(ApprovalResolved resolved) => resolved.Outcome switch
    {
        ApprovalOutcome.Approved => resolved.Scope switch
        {
            ApprovalScope.Session => VerdictKind.ApprovedSession,
            ApprovalScope.Always => VerdictKind.ApprovedAlways,
            _ => VerdictKind.ApprovedOnce,
        },
        ApprovalOutcome.TimedOut => VerdictKind.TimedOut,
        ApprovalOutcome.Cancelled => VerdictKind.Cancelled,
        _ => VerdictKind.Denied,
    };

    private static SessionView OnDecided(SessionView state, CheckpointDecided decided)
    {
        var turn = CurrentTurn(state);
        var group = new DecisionGroup(NewId(state, "decisions"), turn, decided.Checkpoint, decided.Decider, decided.At,
            decided.Answers.IsDefault ? [] : decided.Answers)
        {
            Latency = decided.Latency,
        };
        state = Advance(state) with { Decisions = state.Decisions.Add(group) };
        return turn == 0 ? state : Add(state, new DecisionsEntry(group.Id, group, Summary: false) { Turn = turn });
    }

    private static SessionView OnModelSwitched(SessionView state, ModelSwitched switched)
    {
        state = AddNew(state, "switch", id => new SwitchEntry(id, switched));
        if (switched.Kind == ModelSwitchKind.KeptLocal)
            return state;
        if (state.Running)
        {
            state = UpdateHeader(state, header => header.Models.IsEmpty || header.Models[^1] != switched.To
                ? header with { Models = header.Models.Add(switched.To) }
                : header);
        }

        return state with { Model = switched.To };
    }

    private static SessionView OnTaint(SessionView state, TaintChanged taint)
    {
        if (taint.Tainted == state.Tainted && (!taint.Tainted || taint.Source == state.TaintSource))
            return state;
        state = AddNew(state, "taint", id => new TaintEntry(id, taint.Tainted, taint.Source, taint.At));
        return state with
        {
            Tainted = taint.Tainted,
            TaintSource = taint.Tainted ? taint.Source : null,
            TaintedAt = taint.Tainted ? taint.At : null,
        };
    }

    private static SessionView OnCheckpoint(SessionView state, CheckpointRecorded checkpoint)
    {
        var ledger = state.Ledger;
        if (checkpoint.Kind == CheckpointKind.After)
        {
            var index = ledger.FindLastIndex(row => row.Kind != CheckpointKind.Restore
                && (checkpoint.CallId is not null && row.CallId == checkpoint.CallId || checkpoint.Before is not null && row.BeforeId == checkpoint.Before));
            if (index >= 0)
            {
                var row = ledger[index];
                return state with
                {
                    Ledger = ledger.SetItem(index, row with
                    {
                        Kind = CheckpointKind.After,
                        AfterId = checkpoint.CheckpointId,
                        Changes = checkpoint.Changes.IsDefault ? [] : checkpoint.Changes,
                        Tool = row.Tool ?? checkpoint.Tool,
                        Target = row.Target ?? checkpoint.Target,
                    }),
                };
            }
        }

        var added = new LedgerRow(checkpoint.CheckpointId, checkpoint.Kind, checkpoint.At, checkpoint.Turn ?? CurrentTurn(state))
        {
            CallId = checkpoint.CallId,
            Tool = checkpoint.Tool,
            Target = checkpoint.Target,
            BeforeId = checkpoint.Kind == CheckpointKind.Before ? checkpoint.CheckpointId : checkpoint.Before,
            AfterId = checkpoint.Kind == CheckpointKind.After ? checkpoint.CheckpointId : null,
            Changes = checkpoint.Changes.IsDefault ? [] : checkpoint.Changes,
        };
        return state with { Ledger = ledger.Add(added) };
    }

    private static SessionView OnRewound(SessionView state, SessionRewound rewound)
    {
        if (!rewound.Refused && rewound.Scope != RewindScope.ConversationOnly)
            state = UndoFiles(state, rewound);
        if (!rewound.Refused && rewound.Scope != RewindScope.FilesOnly)
        {
            var transcript = state.Transcript.RemoveAll(entry => entry.Turn >= rewound.Turn);
            if (rewound.FromCallId is { } call)
                transcript = KeepTurnBefore(transcript, rewound.Turn - 1, call, rewound.UnansweredError);
            var question = state.Question is { } open && IndexOf(transcript, QuestionId(open.QuestionId)) < 0 ? null : state.Question;
            var removedTurn = state.Turn >= rewound.Turn;
            state = state with
            {
                Transcript = transcript,
                Question = question,
                // A prompt of a turn that left the transcript is stale: it runs nothing.
                Approvals = removedTurn ? [] : state.Approvals,
                Running = state.Running && !removedTurn,
            };
            if (removedTurn)
            {
                // The latest turn is now the one before the first that left; its changes are what the ledger still has of it.
                var turn = Math.Max(0, rewound.Turn - 1);
                state = state with
                {
                    Turn = turn,
                    TurnHeaderId = null,
                    TurnChanges = LedgerChanges(state.Ledger, row => row.Turn == turn),
                };
            }
        }

        return AddNew(state, "rewind", id => new RewindEntry(id, rewound) { Turn = 0 });
    }

    /// <summary>
    /// A rollback to before a call inside <paramref name="turn"/>: the turn keeps its prompt and what came before the call,
    /// and the call's card and everything after it in the turn leave, as a replay of the log shows it. When nothing of the
    /// turn's answer is left, its header reads <paramref name="unanswered"/> (when given) as an interrupted turn, as the
    /// replay's does: the rollback cut it short.
    /// </summary>
    private static ImmutableList<TranscriptEntry> KeepTurnBefore(ImmutableList<TranscriptEntry> transcript, int turn, string callId, string? unanswered)
    {
        var from = transcript.FindIndex(entry => entry.Turn == turn && entry is ToolEntry tool && tool.CallId == callId);
        if (from < 0)
            return transcript;
        var builder = transcript.ToBuilder();
        for (var i = builder.Count - 1; i >= from; i--)
            if (builder[i].Turn == turn)
                builder.RemoveAt(i);
        var kept = builder.ToImmutable();
        if (unanswered is null || kept.Any(entry => entry.Turn == turn && entry is AnswerEntry { Text.Length: > 0 }))
            return kept;
        var header = kept.FindIndex(entry => entry.Turn == turn && entry is TurnHeaderEntry { Running: false });
        return header < 0
            ? kept
            : kept.SetItem(header, (TurnHeaderEntry)kept[header] with { Outcome = TurnOutcome.Cancelled, Error = unanswered });
    }

    /// <summary>
    /// The files a restore put back: the ledger's calls from <see cref="SessionRewound.Turn"/> on that changed them keep
    /// their rows but mark those paths undone, and the turn's and the session's change lists count each such path again
    /// from what the ledger still has of it (gone when nothing does).
    /// </summary>
    private static SessionView UndoFiles(SessionView state, SessionRewound rewound)
    {
        var paths = rewound.Restored.Concat(rewound.Merged).ToHashSet(StringComparer.Ordinal);
        if (paths.Count == 0)
            return state;
        var ledger = state.Ledger;
        // From the row the restore point names (the ledger runs oldest first), so a call earlier in the same turn keeps
        // its change; without a point, from the turn's first call.
        var start = rewound.RestorePoint is { } point
            ? ledger.FindIndex(row => row.BeforeId == point || row.Id == point)
            : -1;
        for (var i = 0; i < ledger.Count; i++)
        {
            var row = ledger[i];
            if (row.Kind == CheckpointKind.Restore || (start >= 0 ? i < start : row.Turn < rewound.Turn))
                continue;
            var undone = row.LiveChanges.Where(change => paths.Contains(change.Path)).Select(change => change.Path).ToList();
            if (undone.Count > 0)
                ledger = ledger.SetItem(i, row with { UndonePaths = row.UndonePaths.IsDefault ? [.. undone] : row.UndonePaths.AddRange(undone) });
        }

        var live = LedgerChanges(ledger, _ => true);
        var turnLive = LedgerChanges(ledger, row => row.Turn == state.Turn);
        return state with
        {
            Ledger = ledger,
            TurnChanges = Recount(state.TurnChanges, paths, turnLive),
            SessionChanges = Recount(state.SessionChanges, paths, live),
        };
    }

    /// <summary>A change list with each put-back path counted again from the ledger, or left out when the ledger has none of it.</summary>
    private static ImmutableArray<FileChange> Recount(ImmutableArray<FileChange> changes, HashSet<string> paths, ImmutableArray<FileChange> live)
    {
        if (changes.IsDefaultOrEmpty)
            return [];
        var builder = ImmutableArray.CreateBuilder<FileChange>(changes.Length);
        foreach (var change in changes)
        {
            if (!paths.Contains(change.Path))
                builder.Add(change);
            else if (live.FirstOrDefault(candidate => candidate.Path == change.Path) is { } left)
                builder.Add(change with { Added = left.Added, Removed = left.Removed });
        }

        return builder.ToImmutable();
    }

    /// <summary>What the ledger's finished calls still have in the files, each path's counts summed, in the order first changed.</summary>
    private static ImmutableArray<FileChange> LedgerChanges(ImmutableList<LedgerRow> ledger, Func<LedgerRow, bool> include)
    {
        var files = new List<FileChange>();
        foreach (var row in ledger.Where(row => row.Kind == CheckpointKind.After && include(row)))
        {
            foreach (var change in row.LiveChanges)
            {
                var index = files.FindIndex(file => file.Path == change.Path);
                if (index < 0)
                    files.Add(change);
                else
                    files[index] = files[index] with { Added = files[index].Added + change.Added, Removed = files[index].Removed + change.Removed };
            }
        }

        return [.. files];
    }

    private static SessionView OnEngine(SessionView state, EngineStatus engine) => state with
    {
        Generator = engine.Generator is { } generator ? generator with { Model = SafeText.Clean(generator.Model), Device = SafeText.Clean(generator.Device) } : state.Generator,
        // Engine strings reach the header and status bar: cleaned once here (a branch name may hold bidi marks).
        Model = Cleaned(engine.Generator?.Model) ?? Cleaned(engine.Model) ?? state.Model,
        Decider = Cleaned(engine.Decider) ?? state.Decider,
        Scheduler = Cleaned(engine.Scheduler) ?? state.Scheduler,
        Workspace = Cleaned(engine.Workspace) ?? state.Workspace,
        Branch = Cleaned(engine.Branch) ?? state.Branch,
        Project = Cleaned(engine.Project) ?? state.Project,
        Loading = engine.Loading,
    };

    private static SessionView OnQuestion(SessionView state, QuestionAsked question)
    {
        var id = QuestionId(question.QuestionId);
        var index = IndexOf(state.Transcript, id);
        if (index >= 0 && state.Transcript[index] is QuestionEntry entry)
            return state with { Question = question, Transcript = state.Transcript.SetItem(index, entry with { Question = question, Answer = null }) };
        return Add(state, new QuestionEntry(id, question) { Turn = CurrentTurn(state) }) with { Question = question };
    }

    private static SessionView OnQuestionAnswered(SessionView state, QuestionAnswered answered)
    {
        var index = IndexOf(state.Transcript, QuestionId(answered.QuestionId));
        if (index >= 0 && state.Transcript[index] is QuestionEntry entry)
            state = state with { Transcript = state.Transcript.SetItem(index, entry with { Answer = answered.Answer }) };
        return state.Question?.QuestionId == answered.QuestionId ? state with { Question = null } : state;
    }

    /// <summary>
    /// Keeps the derived entries where they belong: the open approval card after its tool card, and the running turn's
    /// summary chips row under its header.
    /// </summary>
    private static SessionView Arrange(SessionView state)
    {
        state = PlaceApproval(state);
        return state.TurnHeaderId is null ? state : PlaceSummaryChips(state);
    }

    private static SessionView PlaceApproval(SessionView state)
    {
        var transcript = state.Transcript;
        var existing = transcript.FindLastIndex(entry => entry is ApprovalEntry);
        var current = state.CurrentApproval;
        if (current is null)
            return existing < 0 ? state : state with { Transcript = transcript.RemoveAt(existing) };

        var without = existing >= 0 ? transcript.RemoveAt(existing) : transcript;
        var toolIndex = current.CallId is { } callId ? IndexOf(without, ToolId(callId)) : -1;
        var turn = toolIndex >= 0 ? without[toolIndex].Turn : CurrentTurn(state);
        var card = new ApprovalEntry(ApprovalId(current.ApprovalId), current, 1, state.Approvals.Count)
        {
            Turn = turn,
            Next = state.Approvals.Count > 1 ? state.Approvals[1] : null,
        };
        var index = toolIndex >= 0 ? toolIndex + 1 : FootIndex(without, turn);
        if (existing == index)
            return transcript[existing] == card ? state : state with { Transcript = transcript.SetItem(existing, card) };
        return state with { Transcript = without.Insert(index, card) };
    }

    private static SessionView PlaceSummaryChips(SessionView state)
    {
        var transcript = state.Transcript;
        var headerId = state.TurnHeaderId!;
        var headerIndex = IndexOf(transcript, headerId);
        if (headerIndex < 0 || transcript[headerIndex] is not TurnHeaderEntry header)
            return state;
        var turn = header.Number;
        var groups = state.Decisions.Where(group => group.Turn == turn).ToList();
        DecisionGroup? chosen = null;
        if (state.Running && state.Turn == turn && !state.Approvals.IsEmpty)
            chosen = groups.LastOrDefault(group => group.Asked);
        chosen ??= groups.FirstOrDefault(group => group.Checkpoint == "ingress") ?? groups.FirstOrDefault();

        var id = headerId + "-chips";
        var existing = IndexOf(transcript, id);
        if (chosen is null)
            return existing < 0 ? state : state with { Transcript = transcript.RemoveAt(existing) };
        var row = new DecisionsEntry(id, chosen, Summary: true) { Turn = turn };
        if (existing >= 0)
            return transcript[existing] == row ? state : state with { Transcript = transcript.SetItem(existing, row) };
        return state with { Transcript = transcript.Insert(headerIndex + 1, row) };
    }

    /// <summary>Adds an entry to the running turn (or after everything, outside a turn), above the turn's thought row.</summary>
    private static SessionView Add(SessionView state, TranscriptEntry entry)
    {
        var transcript = state.Transcript;
        var index = entry.Turn == 0 ? transcript.Count : FootIndex(transcript, entry.Turn);
        // Answer text stops streaming once something follows it in the turn.
        for (var i = index - 1; i >= 0 && entry is not AnswerEntry; i--)
        {
            if (transcript[i].Turn != entry.Turn || transcript[i] is TurnHeaderEntry)
                break;
            if (transcript[i] is AnswerEntry { Streaming: true } answer)
            {
                transcript = transcript.SetItem(i, answer with { Streaming = false });
                break;
            }
        }

        return state with { Transcript = transcript.Insert(index, entry) };
    }

    private static SessionView AddNew(SessionView state, string prefix, Func<string, TranscriptEntry> create)
    {
        var entry = create(NewId(state, prefix));
        if (entry.Turn == 0 && entry is not (CompactionEntry or RewindEntry))
            entry = entry with { Turn = CurrentTurn(state) };
        return Add(Advance(state), entry);
    }

    /// <summary>Where an entry of a turn goes: after its last entry, but above its thought row.</summary>
    private static int FootIndex(ImmutableList<TranscriptEntry> transcript, int turn)
    {
        var index = transcript.Count;
        if (index > 0 && transcript[^1] is ThoughtEntry thought && thought.Turn == turn && turn != 0)
            index--;
        return index;
    }

    /// <summary>The last entry of the running turn other than its thought row; -1 when there is none.</summary>
    private static int LastBodyIndex(SessionView state)
    {
        var transcript = state.Transcript;
        var index = FootIndex(transcript, CurrentTurn(state)) - 1;
        return index >= 0 ? index : -1;
    }

    private static int CurrentTurn(SessionView state) => state.Running ? state.Turn : 0;

    private static string ThoughtId(SessionView state) => (state.Running ? state.TurnHeaderId : null) is { } header ? header + "-thought" : "thought-0";

    private static SessionView UpdateHeader(SessionView state, Func<TurnHeaderEntry, TurnHeaderEntry> change)
    {
        if (state.TurnHeaderId is not { } id)
            return state;
        var index = IndexOf(state.Transcript, id);
        if (index < 0 || state.Transcript[index] is not TurnHeaderEntry header)
            return state;
        var updated = change(header);
        return updated == header ? state : state with { Transcript = state.Transcript.SetItem(index, updated) };
    }

    private static int IndexOf(ImmutableList<TranscriptEntry> transcript, string id)
    {
        // Recent entries are the ones that change.
        for (var i = transcript.Count - 1; i >= 0; i--)
        {
            if (string.Equals(transcript[i].Id, id, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static string NewId(SessionView state, string prefix) => $"{prefix}-{state.NextId}";

    private static SessionView Advance(SessionView state) => state with { NextId = state.NextId + 1 };
}
