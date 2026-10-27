using System.Text.Json;
using Corvid.Approvals;
using Corvid.Host.Sessions;

namespace Corvid.Acp;

/// <summary>
/// Corvid's approval prompts as <c>session/request_permission</c> (plan §12: approvals in the editor). The options are the
/// scopes the pipeline offers: once, for this session and always, plus deny. A cancelled request, an unknown option, a
/// client error and the prompt's deadline all deny; nothing is ever approved on the user's behalf.
/// </summary>
internal sealed class PermissionPrompter(AcpConnectionContext context, AcpSessionState state) : IApprovalPrompter
{
    internal const string Once = "allow-once";
    internal const string Session = "allow-session";
    internal const string Always = "allow-always";
    internal const string Deny = "deny";

    public async ValueTask<ApprovalAnswer> AskAsync(ApprovalPrompt prompt, CancellationToken cancellationToken)
    {
        var wait = prompt.Deadline - DateTimeOffset.UtcNow;
        if (wait <= TimeSpan.Zero)
            return ApprovalAnswer.Deny();
        // A prompt outside a running prompt turn has no reporter to order it after; there is nobody watching, so deny.
        if (state.Reporter is not { } reporter)
            return ApprovalAnswer.Deny();

        var tools = context.Tools;
        var callId = prompt.CallId ?? "approval-" + prompt.ApprovalId;
        var call = prompt.CallId is { } id ? reporter.Call(id) : null;
        // The diff, locations and raw input show what runs: the prompt's in-memory action, not the logged call, whose
        // arguments are credential-scrubbed (C-040). The logged call is the fallback for a bare shell action.
        var action = prompt.Action as ToolAction;
        var arguments = action?.Arguments ?? call?.Arguments;
        var toolName = action?.Tool.Name ?? call?.ToolName ?? "approval";
        // The call is announced before the request names it, and everything the turn queued before it is sent first.
        await reporter.AnnounceAsync(callId, prompt.Analysis.Text, tools?.Kind(toolName) ?? ToolKinds.Other).ConfigureAwait(false);

        var content = new List<ToolCallContent>();
        if (tools?.ProposedDiff(toolName, arguments) is { } diff)
            content.Add(TurnReporter.Diff(diff));
        // A5: a spawn's children (goal, tools, budget, models) or the child that asks, as every surface shows them; A7: a code
        // run's language, limits and whole source, and that each tool the script calls is asked on its own.
        var children = await Corvid.Host.Composition.CodeRunCard.WithDelegationAsync(context.Runtime, prompt, cancellationToken, whole: false)
            .ConfigureAwait(false);
        if (children.Count > 0)
            content.Add(new ContentToolCallContent(new TextContent(string.Join("\n", children))));
        var reasons = string.Join("\n", prompt.Reasons.Select(reason => $"• {reason.Detail} ({reason.Code})"));
        content.Add(new ContentToolCallContent(new TextContent(
            (reasons.Length > 0 ? reasons + "\n" : "") + $"Denied automatically at {prompt.Deadline.ToLocalTime():HH:mm:ss} if unanswered.")));

        var options = new List<PermissionOption> { new(Once, "Allow once", PermissionKinds.AllowOnce) };
        if (prompt.Offered.Contains(ApprovalScope.Session))
            options.Add(new PermissionOption(Session, "Allow for this session", PermissionKinds.AllowAlways));
        if (prompt.Offered.Contains(ApprovalScope.Always))
            options.Add(new PermissionOption(Always, "Always allow", PermissionKinds.AllowAlways));
        options.Add(new PermissionOption(Deny, "Deny", PermissionKinds.RejectOnce));

        var request = new RequestPermissionRequest(state.Session.Id, new ToolCallUpdate(callId)
        {
            Title = prompt.Analysis.Text,
            Status = ToolStatuses.Pending,
            RawInput = arguments,
            Locations = tools is null ? null : TurnReporter.Locations(tools.Locations(arguments)),
            Content = [.. content],
            // A7: a script's call names the code run it was made from.
            Meta = TurnReporter.ParentMeta(call?.ParentCallId),
        }, [.. options]);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, state.TurnCancelled);
        deadline.CancelAfter(wait);
        JsonElement result;
        try
        {
            result = await context.Connection.RequestAsync(AcpMethods.RequestPermission, request, AcpJson.Default.RequestPermissionRequest,
                deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ApprovalAnswer.Deny();
        }
        catch (AcpException exception)
        {
            context.Log($"corvid acp: the client refused the permission request: {exception.Message}");
            return ApprovalAnswer.Deny();
        }
        return Answer(result, prompt);
    }

    /// <summary>The client's choice as an answer; anything but an option the prompt offered denies.</summary>
    internal static ApprovalAnswer Answer(JsonElement result, ApprovalPrompt prompt)
    {
        RequestPermissionResponse? response;
        try
        {
            response = result.Deserialize(AcpJson.Default.RequestPermissionResponse);
        }
        catch (JsonException)
        {
            return ApprovalAnswer.Deny();
        }
        if (response?.Outcome is not { Outcome: "selected", OptionId: { } option })
            return ApprovalAnswer.Deny();
        return option switch
        {
            Once => ApprovalAnswer.Approve(),
            Session when prompt.Offered.Contains(ApprovalScope.Session) => ApprovalAnswer.Approve(ApprovalScope.Session),
            Always when prompt.Offered.Contains(ApprovalScope.Always) => ApprovalAnswer.Approve(ApprovalScope.Always),
            _ => ApprovalAnswer.Deny(),
        };
    }
}
