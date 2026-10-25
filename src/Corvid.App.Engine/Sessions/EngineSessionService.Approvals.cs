using System.Collections.Immutable;
using System.Text.Json;
using Corvid.App.Core;
using Corvid.App.Core.Sessions;
using Corvid.Approvals;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using AppAnswer = Corvid.App.Core.Sessions.ApprovalAnswer;
using AppRequested = Corvid.App.Core.Sessions.ApprovalRequested;
using AppResolved = Corvid.App.Core.Sessions.ApprovalResolved;
using AppScope = Corvid.App.Core.Sessions.ApprovalScope;
using ApprovalOutcome = Corvid.App.Core.Sessions.ApprovalOutcome;
using PipelineAnswer = Corvid.Approvals.ApprovalAnswer;
using PipelineScope = Corvid.Approvals.ApprovalScope;

namespace Corvid.App.Engine.Sessions;

public sealed partial class EngineSessionService
{
    /// <summary>
    /// Answers an open prompt (docs/plan/ui.md F3). Once, session and always answer as the pipeline offered them; always
    /// writes the previewed rules, narrowed to the card's paths when the person narrowed them (G-11), and a rule with a hidden
    /// character is never written. A denial's reason goes to the head of the queue, so it is the next message. An answer
    /// to a prompt that has closed is ignored: nothing runs on a stale prompt.
    /// </summary>
    /// <exception cref="InvalidOperationException">The answer is not one the prompt offers, or its narrowing is not narrower; the prompt stays open.</exception>
    public async Task AnswerApprovalAsync(string sessionId, string approvalId, AppAnswer answer, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentNullException.ThrowIfNull(answer);
        await _engine.RuntimeAsync().ConfigureAwait(false);
        if (!_channels.TryGetValue(sessionId, out var channel))
            return;
        PendingApproval? pending;
        lock (channel.Gate)
            channel.Pending.TryGetValue(approvalId, out pending);
        if (pending is null)
            return;

        var (reply, written) = Decide(pending, answer);
        var reason = answer.Choice == ApprovalChoice.Deny && !string.IsNullOrWhiteSpace(answer.Reason) ? answer.Reason.Trim() : null;
        // The answer carries its rules and reason, so a second answer that loses the race changes nothing of the first's.
        if (!pending.Answer.TrySetResult(new CardAnswer(reply, written, reason)))
            return;
        if (reason is not null)
        {
            lock (channel.Gate)
                channel.Queue = channel.Queue.Insert(0, new QueuedMessage($"q-{++channel.QueueCounter}", reason));
            PublishQueue(channel);
        }
    }

    /// <summary>
    /// Closes the channel's open prompts as cancelled (an interrupt, or the turn ending around them): their cards close at
    /// once, and the prompters still waiting deny. Nothing runs on a stale prompt.
    /// </summary>
    private void ClosePending(Channel channel, string reason)
    {
        PendingApproval[] open;
        lock (channel.Gate)
        {
            open = [.. channel.Pending.Values];
            channel.Pending.Clear();
        }
        if (open.Length == 0)
            return;
        var at = _time.GetUtcNow();
        Publish(channel, _ => [.. open.Select(pending => new AppResolved(pending.Prompt.ApprovalId, ApprovalOutcome.Cancelled) { At = at, Reason = reason })]);
        foreach (var pending in open)
            pending.Answer.TrySetResult(null);
    }

    /// <summary>The pipeline's answer for the card's, and the rules it writes; refuses what the prompt did not offer.</summary>
    private static (PipelineAnswer Reply, ImmutableArray<ApprovalRule> Written) Decide(PendingApproval pending, AppAnswer answer)
    {
        var offered = pending.Prompt.Offered;
        switch (answer.Choice)
        {
            case ApprovalChoice.Once:
                return (PipelineAnswer.Approve(), []);
            case ApprovalChoice.Session:
                if (!offered.Contains(PipelineScope.Session))
                    throw new InvalidOperationException("This prompt asks for this action only; it cannot be allowed for the session.");
                RefuseHidden(pending.Rules, []);
                return (PipelineAnswer.Approve(PipelineScope.Session), pending.Rules);
            case ApprovalChoice.Always:
                if (!offered.Contains(PipelineScope.Always))
                    throw new InvalidOperationException("This prompt asks for this action only; no rule can be written for it.");
                var paths = answer.RulePaths.IsDefault ? [] : answer.RulePaths;
                RefuseHidden(pending.Rules, paths);
                if (!ApprovalPipeline.Narrow(pending.Rules, paths, out var narrowed, out var error))
                    throw new InvalidOperationException("The rule was not written: " + error);
                return (PipelineAnswer.Approve(PipelineScope.Always) with { RulePaths = paths }, narrowed);
            default:
                // The reason goes into the log with the answer, so a replay shows it as this card does.
                return (PipelineAnswer.Deny() with
                {
                    Reason = answer.Choice == ApprovalChoice.Deny && !string.IsNullOrWhiteSpace(answer.Reason) ? answer.Reason.Trim() : null,
                }, []);
        }
    }

    /// <summary>
    /// A session or always rule is a standing grant written as structure the person must be able to read: nothing with a
    /// hidden character in it is granted.
    /// </summary>
    private static void RefuseHidden(ImmutableArray<ApprovalRule> rules, ImmutableArray<ImmutableArray<string>> paths)
    {
        if (rules.Any(rule => SafeText.HasHidden(rule.Verb) || SafeText.HasHidden(rule.Subcommand)
                || rule.Paths.Any(SafeText.HasHidden) || rule.Names.Any(SafeText.HasHidden))
            || paths.Any(row => !row.IsDefault && row.Any(SafeText.HasHidden)))
            throw new InvalidOperationException("The rule holds hidden characters, so it is not written. Allow once or deny.");
    }

    /// <summary>The card's answer: the pipeline's reply, the rules it writes, and a denial's reason.</summary>
    private sealed record CardAnswer(PipelineAnswer Reply, ImmutableArray<ApprovalRule> Written, string? Reason);

    /// <summary>An open prompt: what it asked, the rules a session or always answer writes, and the card's answer when it comes.</summary>
    private sealed class PendingApproval(ApprovalPrompt prompt, ImmutableArray<ApprovalRule> rules)
    {
        public ApprovalPrompt Prompt { get; } = prompt;

        public ImmutableArray<ApprovalRule> Rules { get; } = rules;

        /// <summary>Null when the prompt closed without an answer: the deadline, the turn's cancellation, the window closing.</summary>
        public TaskCompletionSource<CardAnswer?> Answer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// The approval prompter a turn the window runs attaches (as Corvid.Acp's <c>PermissionPrompter</c> does for an editor):
    /// it announces the call the prompt is about, after everything the turn reported before it, puts the card on the stream
    /// (<see cref="ApprovalRequested"/>), waits for <see cref="AnswerApprovalAsync"/>, and closes the card
    /// (<see cref="ApprovalResolved"/>). A prompt nobody answers is denied: by its deadline, which the pipeline keeps, by the
    /// turn's cancellation, or when the window closes. Nothing is approved on the person's behalf.
    /// </summary>
    private sealed class CardPrompter(EngineSessionService service, Channel channel, CorvidRuntime runtime, ToolPresentation tools,
        SessionUpdateReporter reporter) : IApprovalPrompter
    {
        public async ValueTask<PipelineAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
        {
            if (service._disposed)
                return PipelineAnswer.Deny();
            var call = prompt.CallId is { } id ? reporter.Call(id) : null;
            var callId = prompt.CallId ?? "approval-" + prompt.ApprovalId;
            var tool = ApprovalCards.ToolOf(prompt, call);
            // What runs is the prompt's in-memory action; the logged call's arguments are credential-scrubbed (C-040).
            JsonElement? arguments = (prompt.Action as ToolAction)?.Arguments ?? call?.Arguments;
            // The call is announced before the card names it, and everything the turn queued before it goes first.
            await reporter.AnnounceAsync(callId, prompt.Analysis.Text, tools.Kind(tool)).ConfigureAwait(false);

            var facts = ApprovalCards.Read(runtime.Log, channel.SessionId, prompt);
            // A5: a spawn's children (goal, tools, budget, model mix) or the child that asks, as every surface's card has them.
            var delegation = await DelegationCard.LinesAsync(runtime, prompt, cancellationToken).ConfigureAwait(false);
            var now = service._time.GetUtcNow();
            var pending = new PendingApproval(prompt, ApprovalPipeline.RulesFor(prompt.Analysis, RuleDecision.Allow, now));
            await service.PauseAt("card").ConfigureAwait(false);
            // The prompt is registered and its card put on the stream under one lock, so an interrupt or the turn's end
            // either comes first (no card, deny) or closes the card it sees: never a close before the card it closes.
            var registered = false;
            service.Publish(channel, translator =>
            {
                if (channel.Cancelling || !ReferenceEquals(channel.Reporter, reporter) || service._disposed)
                    return [];
                channel.Pending[prompt.ApprovalId] = pending;
                registered = true;
                AppRequested request = ApprovalCards.Request(prompt, callId, tool, arguments, tools, translator, facts, channel.TaintSource, now) with
                {
                    Delegation = [.. delegation],
                };
                return
                [
                    // An enforced detection's red row comes first, then the card it forced.
                    .. facts.Detections.Where(detection => detection.Enforced).Select(detection => new DetectionMatched(detection) { At = now }),
                    request,
                ];
            });
            if (!registered)
                return PipelineAnswer.Deny();

            CardAnswer? answer;
            using (cancellationToken.Register(() => pending.Answer.TrySetResult(null)))
                answer = await pending.Answer.Task.ConfigureAwait(false);
            await service.PauseAt("answered").ConfigureAwait(false);

            bool interrupted, open;
            lock (channel.Gate)
            {
                open = channel.Pending.Remove(prompt.ApprovalId);
                interrupted = channel.Cancelling;
            }
            // Closed already (an interrupt, the turn's end): its card says so; deny.
            if (!open && !service._disposed)
                return PipelineAnswer.Deny();
            var at = service._time.GetUtcNow();
            if (answer is null)
            {
                // The pipeline cancels the prompt at its deadline; an interrupt or the window closing cancels it too.
                var cancelled = interrupted || service._disposed;
                service.Publish(channel, _ =>
                [
                    new AppResolved(prompt.ApprovalId, cancelled ? ApprovalOutcome.Cancelled : ApprovalOutcome.TimedOut)
                    {
                        At = at,
                        Reason = cancelled ? "interrupted" : "deadline",
                    },
                ]);
                return PipelineAnswer.Deny();
            }
            var reply = answer.Reply;
            service.Publish(channel, _ =>
            [
                new AppResolved(prompt.ApprovalId, reply.Approved ? ApprovalOutcome.Approved : ApprovalOutcome.Denied)
                {
                    At = at,
                    Scope = reply.Approved ? ApprovalCards.Scope(reply.Scope) : AppScope.Once,
                    Rule = answer.Written.IsDefaultOrEmpty ? null : string.Join("; ", answer.Written.Select(ApprovalCards.Describe)),
                    Reason = answer.Reason,
                },
            ]);
            return reply;
        }
    }
}
