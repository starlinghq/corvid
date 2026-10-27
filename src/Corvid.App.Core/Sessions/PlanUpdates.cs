namespace Corvid.App.Core.Sessions;

/// <summary>
/// A7, an architect/editor turn's plan (plan §6; <c>_corvid/turn_planned</c>, from the host's <c>TurnPlanned</c>): the
/// escalated model (<see cref="Planner"/>) wrote it, without tools, for the session's own model or the configured editor
/// (<see cref="Editor"/>), which makes the edits after it. Live once it is logged and on replay alike. The transcript draws
/// it as "planned by X · edited by Y" with the plan collapsed under it. Model text and model names: shown through
/// <see cref="SafeText"/>.
/// </summary>
/// <param name="Text">The plan as the planner wrote it.</param>
/// <param name="Planner">The profile id of the model that planned ("claude-sonnet-5").</param>
/// <param name="Editor">The profile id of the model that applies it ("qwen3.5-0.8b").</param>
/// <param name="Attempt">1 for the plan, 2 for the one re-plan a turn may make when its editor got stuck.</param>
public sealed record TurnPlanned(string Text, string Planner, string Editor, int Attempt) : SessionUpdate
{
    /// <summary>
    /// What made the turn plan: <c>difficulty</c>, <c>context</c> or <c>repair</c> (an escalation of a coding turn), <c>stuck</c>
    /// (the re-plan), or <c>architect</c> (the person asked with <c>/architect</c>).
    /// </summary>
    public string? Trigger { get; init; }

    /// <summary>The plan's position in the event log.</summary>
    public long? Position { get; init; }
}
