using System.Collections.Immutable;

namespace Corvid.App.Core.Sessions;

/// <summary>A child session's limits as it was started with them: generator steps, tokens and seconds.</summary>
public sealed record ChildBudgetInfo(int Steps, int Tokens, int Seconds)
{
    /// <summary>"8 steps · 64,000 tokens · 600 s".</summary>
    public string Text => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Steps} steps · {Tokens:N0} tokens · {Seconds} s");
}

/// <summary>
/// A5 (plan §14, docs/plan/ui.md §4.13): one child session of this one started or ended (<c>_corvid/child</c>, from the
/// host's <c>ChildStarted</c> and <c>ChildEnded</c>), for the session header's children and the Board's tree. A later
/// update for the same <see cref="ChildId"/> replaces the earlier one. The goal and the answer are model text, and the mix
/// names come from configuration: shown through <see cref="SafeText"/>.
/// </summary>
/// <param name="Goal">What it was asked.</param>
/// <param name="Status"><c>running</c>, or how it ended: <c>answered</c>, <c>failed</c>, <c>stuck</c>, <c>step_budget_exhausted</c>,
/// <c>token_budget_exhausted</c>, <c>timed_out</c>, <c>cancelled</c> or <c>invalid_output</c>.</param>
public sealed record ChildSessionChanged(string ChildId, string Goal, string Status) : SessionUpdate
{
    public const string Running = "running";

    public ImmutableArray<string> Tools { get; init; } = [];

    public ChildBudgetInfo? Budget { get; init; }

    /// <summary>The slots it runs on ("generator qwen3.5-9b"), or null when on the parent's.</summary>
    public string? Mix { get; init; }

    /// <summary><c>agent</c> (a <c>delegate_task</c> call) or <c>user</c> (<c>/delegate</c>, the app).</summary>
    public string By { get; init; } = "agent";

    public bool Tainted { get; init; }

    /// <summary>Its answer had to meet an output schema.</summary>
    public bool HasSchema { get; init; }

    /// <summary>Whether the answer met the schema; null when none was given or it has not ended.</summary>
    public bool? OutputValid { get; init; }

    /// <summary>Its answer, cut to a readable size.</summary>
    public string? Answer { get; init; }

    /// <summary>Why it ended other than answered.</summary>
    public string? Detail { get; init; }

    public int? Steps { get; init; }

    public long? Tokens { get; init; }

    public bool IsRunning => Status == Running;
}

/// <summary>
/// A5: this session is a child of another (its log's <c>ChildOf</c>): the header links the parent, and the session is
/// read-only (a child is its parent's record, never driven from a window).
/// </summary>
public sealed record ChildOfSession(string ParentId, string Goal) : SessionUpdate;
