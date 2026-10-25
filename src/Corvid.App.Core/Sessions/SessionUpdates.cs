using System.Collections.Immutable;

namespace Corvid.App.Core.Sessions;

/// <summary>
/// One update in a session's stream (docs/plan/ui.md §0, §8; tui.md §3.2). The records are shaped like ACP's
/// <c>session/update</c> notifications, plus Corvid's extensions, which travel as <c>_corvid/*</c> notifications in
/// attached mode (G-1). The app's state for a session is a fold over them (<see cref="SessionFold"/>), so a session
/// loaded from the log and a live one draw the same screens.
/// <para>
/// The in-process engine produces them once <c>TurnReporter</c>'s mapping moves to <c>Corvid.Host</c> as
/// <c>SessionUpdates</c> (K-10), which every record here assumes. Records that need more than that say so: K-n is the
/// kernel work of tui.md §9, G-n the host work of docs/plan/ui.md §9.
/// </para>
/// </summary>
public abstract record SessionUpdate
{
    /// <summary>When it happened: the event's timestamp for one read from the log, the report's time for a live one.</summary>
    public DateTimeOffset At { get; init; }
}

/// <summary>
/// A message the user sent (ACP <c>user_message_chunk</c>, as one update for the whole message): a turn's prompt, or a
/// steer delivered into a running turn. From <c>UserMessage</c> events.
/// </summary>
/// <param name="Text">The message as sent.</param>
public sealed record UserMessage(string Text) : SessionUpdate
{
    /// <summary>The message's position in the event log: the fork point for "edit and resend" and "branch from here".</summary>
    public long? Position { get; init; }

    /// <summary>
    /// Delivered at a step checkpoint of the running turn (Ctrl+Enter) rather than starting a turn (K-3: the log's
    /// <c>UserSteered</c>, live and on replay).
    /// </summary>
    public bool Steered { get; init; }

    /// <summary>Who wrote it when it is not the person (a gateway guest, security M2): the transcript names them.</summary>
    public string? From { get; init; }
}

/// <summary>Streamed answer text (ACP <c>agent_message_chunk</c>): appended to the running turn's answer.</summary>
public sealed record AgentMessageChunk(string Text) : SessionUpdate;

/// <summary>
/// Streamed reasoning (ACP <c>agent_thought_chunk</c>), drawn as the collapsed "thought for 4 s" row. Needs K-1: the
/// generator's <c>TextReasoningContent</c> deltas forwarded through the feed.
/// </summary>
public sealed record AgentThoughtChunk(string Text) : SessionUpdate;

/// <summary>
/// A prompt turn began (<c>_corvid/turn_started</c>): its number in the session (forks keep the source's numbering) and
/// the generator it starts on. ACP marks a turn by the prompt request; the stream needs it as an update.
/// </summary>
/// <param name="Model">The generator slot's model when the turn starts ("qwen3.5-0.8b").</param>
public sealed record TurnStarted(int Turn, string Model) : SessionUpdate
{
    /// <summary>The turn's step budget, for "step 2 of 6".</summary>
    public int? MaxSteps { get; init; }
}

/// <summary>
/// The running turn moved to another step (<c>_corvid/turn_progress</c>): a generator response that calls tools. Live from
/// the turn loop with the turn's budget; on replay from the step each call's round records, with the budget unknown.
/// </summary>
public sealed record TurnProgress(int Step) : SessionUpdate
{
    public int? MaxSteps { get; init; }
}

/// <summary>How a turn ended: Corvid's <c>TurnStatus</c>, which refines ACP's stop reasons.</summary>
public enum TurnOutcome
{
    /// <summary>The answer was delivered (ACP <c>end_turn</c>).</summary>
    Answered,
    /// <summary>The answer is one question for the user (<c>clarify</c> acted).</summary>
    Clarifying,
    /// <summary><c>stuck</c> acted and escalation went nowhere.</summary>
    Stuck,
    /// <summary>The step budget ran out (ACP <c>max_turn_requests</c>).</summary>
    StepBudgetExhausted,
    Failed,
    /// <summary>Interrupted (Esc) or cancelled; the work so far stays (ACP <c>cancelled</c>).</summary>
    Cancelled,
}

/// <summary>
/// The turn ended (the <c>session/prompt</c> response, as an update), with what the turn header shows afterwards. Tokens
/// and cost need K-5.
/// </summary>
public sealed record TurnEnded(int Turn, TurnOutcome Outcome) : SessionUpdate
{
    public TimeSpan? Elapsed { get; init; }

    /// <summary>The generator the turn ended on, when it differs from the one it started on.</summary>
    public string? Model { get; init; }

    /// <summary>Tokens the turn used across its generator calls (K-5).</summary>
    public int? Tokens { get; init; }

    /// <summary>What the turn's cloud calls cost, when the provider reported usage (K-5).</summary>
    public decimal? Cost { get; init; }

    /// <summary>Why a failed turn failed, in a line.</summary>
    public string? Error { get; init; }
}

/// <summary>A part of the rendered prompt, for the context breakdown ("results 61k").</summary>
public sealed record ContextPart(string Label, int Tokens);

/// <summary>
/// Token usage and the context gauge (ACP draft <c>usage_update</c>). Needs K-5: usage persisted and reported per call
/// and per session, and the renderer's prompt token count.
/// </summary>
public sealed record UsageUpdate : SessionUpdate
{
    /// <summary>The running turn's tokens so far.</summary>
    public int? TurnTokens { get; init; }

    /// <summary>The running turn's cloud cost so far.</summary>
    public decimal? TurnCost { get; init; }

    public decimal? SessionCost { get; init; }

    /// <summary>Tokens in the rendered prompt.</summary>
    public int? ContextTokens { get; init; }

    /// <summary>The accepted context window of the generator in use.</summary>
    public int? ContextWindow { get; init; }

    /// <summary>The fraction of the window at which compaction runs next (0.7).</summary>
    public double? CompactAt { get; init; }

    /// <summary>What fills the prompt: system prompt, memory, skills, recent results, stubs.</summary>
    public ImmutableArray<ContextPart> Breakdown { get; init; } = [];
}

public enum PlanEntryStatus
{
    Pending,
    InProgress,
    Completed,
}

/// <summary>A task in the model's list.</summary>
public sealed record PlanEntry(string Text, PlanEntryStatus Status);

/// <summary>The model's task list, replaced whole (ACP <c>plan</c>, from <c>TodosSet</c>). The todos belong to the model.</summary>
public sealed record PlanUpdate(ImmutableArray<PlanEntry> Entries) : SessionUpdate;

/// <summary>
/// The session's title (<c>_corvid/title</c>): generated locally after the first exchange (K-9), or the user's rename
/// (G-6).
/// </summary>
public sealed record TitleChanged(string Title) : SessionUpdate
{
    /// <summary>Written by the local generator rather than the user.</summary>
    public bool Generated { get; init; }
}

/// <summary>The standing goal was set or cleared (<c>GoalSet</c>); null clears it.</summary>
public sealed record GoalChanged(string? Goal) : SessionUpdate;

/// <summary>The session's approval mode (<c>_corvid/mode</c>). Per session needs G-10; today the mode is the runtime's.</summary>
public sealed record ApprovalModeChanged(Shell.ApprovalMode Mode) : SessionUpdate;

/// <summary>The session is a fork of another at a turn (<c>session/fork</c> metadata), for the header's fork pill.</summary>
public sealed record SessionForked(string FromSession, int AtTurn) : SessionUpdate;

/// <summary>
/// Who drives the session (<c>_corvid/driver</c>): this window, or another process that holds it, in which case the app
/// tails the log as a read-only live view. Needs G-12 (a single-driver lock per session, and tailing).
/// </summary>
public sealed record DriverChanged(bool ReadOnly) : SessionUpdate
{
    /// <summary>What holds the session ("a terminal: corvid chat --session cli-c3d2"), for the read-only note.</summary>
    public string? Holder { get; init; }
}

/// <summary>A slash command the session accepts (ACP <c>available_commands_update</c> entries).</summary>
public sealed record SlashCommandInfo(string Name, string Summary, string Usage = "");

/// <summary>The slash commands the composer's palette offers (ACP <c>available_commands_update</c>).</summary>
public sealed record AvailableCommandsUpdate(ImmutableArray<SlashCommandInfo> Commands) : SessionUpdate;

public enum OutputTone
{
    Normal,
    Success,
    Warning,
    Error,
}

/// <summary>
/// What a slash command printed (<c>_corvid/command</c>): the registry's <c>CommandOutput</c>, drawn as a card in the
/// transcript (docs/plan/ui.md §4.1).
/// </summary>
/// <param name="Line">The command as typed ("/status").</param>
public sealed record CommandResult(string Line) : SessionUpdate
{
    public string? Title { get; init; }

    public ImmutableArray<string> Lines { get; init; } = [];

    /// <summary>A table's column names; empty when the output has no table.</summary>
    public ImmutableArray<string> Columns { get; init; } = [];

    public ImmutableArray<ImmutableArray<string>> Rows { get; init; } = [];

    public OutputTone Tone { get; init; }
}

/// <summary>A message waiting to be sent: queued behind the running turn (Enter), or a steer (Ctrl+Enter, K-3).</summary>
public sealed record QueuedMessage(string Id, string Text)
{
    /// <summary>Delivered at the running turn's next step checkpoint rather than after it.</summary>
    public bool Steer { get; init; }
}

/// <summary>
/// The session's queue, replaced whole (<c>_corvid/queue</c>). The service holds it, so every window and the phone
/// see the same chips; the first item is sent when the running turn ends (docs/plan/ui.md F6).
/// </summary>
public sealed record QueueChanged(ImmutableArray<QueuedMessage> Items) : SessionUpdate;
