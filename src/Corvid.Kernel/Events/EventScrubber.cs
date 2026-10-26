using System.Collections.Immutable;
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
/// <see cref="ClarificationAsked"/>'s text and options, <see cref="ApprovalRequested.Action"/> and <see cref="ApprovalRequested.Reason"/>, <see cref="Decision.State"/> and
/// <see cref="AskedQuestion.Instructions"/> (a pre_execute or memory_op question carries arguments and text), <see cref="ApprovalAssessed"/>'s reasons and rule,
/// <see cref="SessionTitled.Title"/> and <see cref="SessionRecapped.Text"/>, <see cref="EgressRecorded.Reason"/> and <see cref="EgressRecorded.Unkept"/>, and
/// <see cref="DeliveryQueued.Text"/>, and since A6 (verification round 2, A6V2-06) <see cref="UserMessage.Text"/> and <see cref="UserSteered.Text"/>:
/// a password or key pasted into the composer is replaced by its marker before the message is logged, so neither the log,
/// the episodic index (and so <c>/search</c> and the palette's search) nor prompt history keeps it, and the prompt rendered
/// from the log shows the model the marker (<c>[REDACTED:prose-value]</c>, <c>ghp_[REDACTED:github-token:…]</c>), as the
/// egress gate would for a gated model. A user message's sender, flags and image references are not text and are kept.
/// What the person typed stays in their composer until it is sent; a key meant for a tool goes through Vault. Not
/// scrubbed: ids, sequences, revisions and paths. Only the egress rules run, never the encoded-blob rules, so commit SHAs
/// and digests in shell output survive.
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
            // A6V2-06: what the person typed or steered with, as the egress gate would see it.
            UserMessage user when Text(user.Text) is { } text => user with { Text = text },
            UserSteered steer when Text(steer.Text) is { } text => steer with { Text = text },
            // The model's question (K-6), like its answer; the options are its words too.
            ClarificationAsked question => Clarification(question),
            ApprovalRequested approval => Approval(approval),
            Decision decision => Decision(decision),
            DeliveryQueued delivery when Text(delivery.Text) is { } text => delivery with { Text = text },
            // G-6: a generated title or recap is model output over the conversation; a rename is scrubbed too, since a
            // title is shown in every list rather than kept as a recorded message.
            SessionTitled titled when Text(titled.Title) is { } text => titled with { Title = text },
            SessionRecapped recap when Text(recap.Text) is { } text => recap with { Text = text },
            ApprovalAssessed assessed => Assessed(assessed),
            // G-9: the gate's reason and why the store could not keep the request (an exception's message) can quote text.
            EgressRecorded egress => Egress(egress),
            _ => payload,
        };
    }

    // A reason quotes the command line a rule matched (a remembered rule's source), and a rule names hosts and recipients.
    private static ApprovalAssessed Assessed(ApprovalAssessed assessed)
    {
        var changed = false;
        var reasons = assessed.Reasons.Select(reason =>
        {
            if (Text(reason.Detail) is not { } detail)
                return reason;
            changed = true;
            return reason with { Detail = detail };
        }).ToImmutableArray();
        var rule = assessed.Rule is { } words ? Text(words) : null;
        return !changed && rule is null ? assessed : assessed with { Reasons = reasons, Rule = rule ?? assessed.Rule };
    }

    private static EgressRecorded Egress(EgressRecorded egress)
    {
        var reason = Text(egress.Reason);
        var unkept = Text(egress.Unkept);
        return reason is null && unkept is null ? egress : egress with { Reason = reason ?? egress.Reason, Unkept = unkept ?? egress.Unkept };
    }

    private static ApprovalRequested Approval(ApprovalRequested approval)
    {
        var action = Text(approval.Action);
        var reason = Text(approval.Reason);
        return action is null && reason is null ? approval : approval with { Action = action ?? approval.Action, Reason = reason ?? approval.Reason };
    }

    private static ClarificationAsked Clarification(ClarificationAsked question)
    {
        var text = Text(question.Text);
        var options = question.Options.IsDefault ? [] : question.Options.Select(option => Text(option) ?? option).ToImmutableArray();
        return text is null && options.SequenceEqual(question.Options.IsDefault ? [] : question.Options)
            ? question
            : question with { Text = text ?? question.Text, Options = options };
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

    /// <summary>
    /// A6V2-06: a user's message text as the log keeps it (credential values replaced by their markers), so a surface that
    /// matches its own sent text against the log's compares like with like.
    /// </summary>
    public static string UserText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Text(text) ?? text;
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
