using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.Secrets;

namespace Corvid.Kernel.Events;

/// <summary>
/// The credential scrub on the event log (C-040): the fields of an event that carry tool, model or approval text, with every
/// credential value <see cref="CredentialDetector.Egress"/> finds replaced by a <see cref="CredentialScrub"/> marker.
/// <see cref="SqliteEventLog.Append"/> applies it to every event, so no writer needs wiring and no credential value from a
/// tool result reaches the log, the prompt rendered from it, or the episodic index derived from it. Pure; never throws for
/// a detector timeout (the field becomes <see cref="CredentialScrub.Unscannable"/>).
/// </summary>
/// <remarks>
/// Scrubbed: <see cref="ToolCallIssued.Arguments"/>, <see cref="ToolResult.Result"/>, <see cref="AssistantMessage.Text"/>,
/// <see cref="ApprovalRequested.Action"/> and <see cref="ApprovalRequested.Reason"/>, <see cref="Decision.State"/> and
/// <see cref="AskedQuestion.Instructions"/> (a pre_execute or memory_op question carries arguments and text), and
/// <see cref="DeliveryQueued.Text"/>. Not scrubbed: <see cref="UserMessage"/> (what the user said is a recorded decision; a
/// key the user hands over goes through Vault), and ids, sequences, revisions and paths. Only the egress rules run, never the
/// encoded-blob rules, so commit SHAs and digests in shell output survive.
/// </remarks>
public static class EventScrubber
{
    private const CredentialRules Rules = CredentialDetector.Egress;

    /// <summary><paramref name="payload"/> with its text-bearing fields scrubbed; the same instance when nothing changed.</summary>
    public static SessionEvent Scrub(SessionEvent payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload switch
        {
            ToolCallIssued call when Json(call.Arguments) is { } arguments => call with { Arguments = arguments },
            ToolResult result when Json(result.Result) is { } value => result with { Result = value },
            AssistantMessage message when Text(message.Text) is { } text => message with { Text = text },
            ApprovalRequested approval => Approval(approval),
            Decision decision => Decision(decision),
            DeliveryQueued delivery when Text(delivery.Text) is { } text => delivery with { Text = text },
            _ => payload,
        };
    }

    private static ApprovalRequested Approval(ApprovalRequested approval)
    {
        var action = Text(approval.Action);
        var reason = Text(approval.Reason);
        return action is null && reason is null ? approval : approval with { Action = action ?? approval.Action, Reason = reason ?? approval.Reason };
    }

    private static Decision Decision(Decision decision)
    {
        var state = decision.State is { } current ? Json(current) : null;
        var instructions = decision.Question is { } question ? Json(question.Instructions) : null;
        if (state is null && instructions is null)
            return decision;
        return decision with
        {
            State = state ?? decision.State,
            Question = instructions is { } scrubbed ? decision.Question! with { Instructions = scrubbed } : decision.Question,
        };
    }

    // Null when nothing changed, so an unchanged event keeps its instance and its persisted form.
    private static string? Text(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        var scrubbed = CredentialScrub.Scrub(text, Rules);
        return scrubbed.Count == 0 ? null : scrubbed.Text;
    }

    private static JsonElement? Json(JsonElement element)
    {
        var scrubbed = CredentialScrub.ScrubJson(element, Rules);
        return scrubbed.Count == 0 ? null : scrubbed.Element;
    }
}
