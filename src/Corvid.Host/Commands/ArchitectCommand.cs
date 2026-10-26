using Corvid.Kernel.Turns;
using Corvid.Models;

namespace Corvid.Host.Commands;

/// <summary>
/// <c>/architect</c> (A7, plan §6): architect/editor turns asked for by the person. With no argument the next turn plans
/// first: the generator escalates, the escalated model writes a plan, and the session's own model (or
/// <c>Corvid:Turn:EditorModel</c>) makes the edits with it. <c>on</c> does that for every turn of the session until
/// <c>off</c>; <c>status</c> says what is set. Held by this process only, like the approval mode. The plan's request passes
/// the egress gate like any request to that model, and every edit still goes through the approvals.
/// </summary>
internal static class ArchitectCommand
{
    public const string Usage = "[on | off | status]";

    public const string Summary = "Plan the next turn (or every turn) on the escalated model, then edit on this session's own.";

    public static ValueTask<CommandOutput> Run(SlashCommandContext context, SlashInvocation invocation, CancellationToken cancellationToken)
    {
        var session = context.Session;
        var runner = session.Runner;
        var planner = Planner(session.Slots);
        var editor = context.Runtime.Options.Turn.EditorModel is { Length: > 0 } configured ? configured
            : session.Slots.SessionDefault(ModelSlots.Generator).Profile.Id;
        var split = planner is null ? null : $"{planner} plans, {TerminalText.Visible(editor)} edits";
        switch (invocation.Arguments)
        {
            case []:
                if (planner is null)
                    return ValueTask.FromResult(NoPlanner());
                runner.Architect = ArchitectRequest.NextTurn;
                return ValueTask.FromResult(CommandOutput.Success($"The next turn plans first: {split}."));
            case ["on"]:
                if (planner is null)
                    return ValueTask.FromResult(NoPlanner());
                runner.Architect = ArchitectRequest.Session;
                return ValueTask.FromResult(CommandOutput.Success($"Every turn of this session plans first, until /architect off: {split}."));
            case ["off"]:
                runner.Architect = ArchitectRequest.None;
                return ValueTask.FromResult(CommandOutput.Success(context.Runtime.Options.Turn.ArchitectEditor
                    ? "Turns plan first only when a coding turn escalates (Corvid:Turn:ArchitectEditor)."
                    : "Turns no longer plan first."));
            case ["status"]:
                return ValueTask.FromResult(CommandOutput.Text(Status(runner.Architect, context.Runtime.Options.Turn.ArchitectEditor, split)));
            default:
                return ValueTask.FromResult(CommandOutput.Error("Usage: /architect " + Usage + "."));
        }
    }

    private static string Status(ArchitectRequest request, bool automatic, string? split)
    {
        var asked = request switch
        {
            ArchitectRequest.NextTurn => "the next turn plans first",
            ArchitectRequest.Session => "every turn of this session plans first",
            _ => automatic ? "a coding turn plans first when it escalates (Corvid:Turn:ArchitectEditor)" : "off",
        };
        return split is null ? $"Architect/editor: {asked}; the generator has no escalation target to plan with."
            : $"Architect/editor: {asked} ({split}).";
    }

    // The model a plan would come from: the first rung of the generator's escalation ladder.
    private static string? Planner(SessionModelSlots slots) =>
        slots.TryConfiguration(ModelSlots.Generator, out var configuration) && !configuration.Escalation.IsEmpty
            ? TerminalText.Visible(configuration.Escalation[0].Profile.Id)
            : null;

    private static CommandOutput NoPlanner() => CommandOutput.Error(
        "The generator has no escalation target to plan with: give it a ladder (Corvid:Models:Slots:generator:Escalation).");
}
