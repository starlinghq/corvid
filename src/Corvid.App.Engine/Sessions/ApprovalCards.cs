using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Abstractions.Events;
using Corvid.App.Core.Sessions;
using Corvid.Approvals;
using Corvid.Host.Sessions;
using AppReason = Corvid.App.Core.Sessions.ApprovalReason;
using AppRequested = Corvid.App.Core.Sessions.ApprovalRequested;
using AppScope = Corvid.App.Core.Sessions.ApprovalScope;
using LoggedDetection = Corvid.Abstractions.Events.DetectionMatched;
using LoggedRequest = Corvid.Abstractions.Events.ApprovalRequested;
using PipelineScope = Corvid.Approvals.ApprovalScope;

namespace Corvid.App.Engine.Sessions;

/// <summary>
/// An approval prompt as the card draws it (docs/plan/ui.md §4.4): the pipeline's analysis, reasons and offered scopes, the
/// proposed diff from the in-memory action, the rules a session or always answer would write (<see cref="ApprovalPipeline.RulesFor"/>),
/// and what the log recorded beside the prompt (reversibility, taint, the detections that forced it). Text is passed as
/// the pipeline has it, never cleaned: the card reveals hidden characters to the person who approves.
/// </summary>
internal static class ApprovalCards
{
    /// <summary>The short label the card shows beside a reason, by the pipeline's code.</summary>
    internal static string Label(string code) => code switch
    {
        "risk" => "risk",
        "mode" => "mode",
        ApprovalReasons.Unparsed => "unparsed",
        ApprovalReasons.Unresolved => "unresolved",
        ApprovalReasons.CredentialRead => "credential",
        ApprovalReasons.VaultSweep => "vault",
        ApprovalReasons.DeviceRead => "device",
        ApprovalReasons.ProcessRead => "process",
        ApprovalReasons.Elevated => "elevated",
        ApprovalReasons.Configuration => "config",
        ApprovalReasons.TaintedEgress or ApprovalReasons.TaintedRecipient => "taint",
        ApprovalReasons.Signal => "signal",
        ApprovalReasons.Detection => "detection",
        _ => code,
    };

    /// <summary>The tool a prompt is about: the in-memory action's, the logged call's, or the shell for a bare command line.</summary>
    internal static string ToolOf(ApprovalPrompt prompt, ToolCallIssued? call) =>
        (prompt.Action as ToolAction)?.Tool.Name ?? call?.ToolName ?? (prompt.Analysis.Kind == "shell" ? "shell" : "approval");

    public static AppRequested Request(ApprovalPrompt prompt, string callId, string tool, JsonElement? arguments, ToolPresentation tools,
        SessionUpdateTranslator paths, LoggedFacts logged, string? taintSource, DateTimeOffset at)
    {
        var analysis = prompt.Analysis;
        var offered = prompt.Offered.Select(Scope).ToImmutableArray();
        var rules = prompt.Offered.Contains(PipelineScope.Session) || prompt.Offered.Contains(PipelineScope.Always)
            ? ApprovalPipeline.RulesFor(analysis, RuleDecision.Allow, at)
            : [];
        return new AppRequested(prompt.ApprovalId, tool, analysis.Text, prompt.Deadline)
        {
            At = at,
            CallId = callId,
            Commands = [.. analysis.Commands.Select(Command)],
            Reasons = [.. prompt.Reasons.Select(reason => new AppReason(reason.Code, Label(reason.Code), reason.Detail))],
            Offered = offered.IsEmpty ? [AppScope.Once] : offered,
            Unparsed = analysis.Unsupported,
            Reversible = logged.Reversible,
            TaintSource = logged.Tainted ? taintSource ?? "outside content in this session's context" : null,
            ProposedDiff = tools.ProposedDiff(tool, arguments) is { } diff ? new FileEdit(paths.RelativePath(diff.Path), diff.OldText, diff.NewText) : null,
            Rules = [.. rules.Select(Preview)],
            Detections = logged.Detections,
        };
    }

    public static AppScope Scope(PipelineScope scope) => scope switch
    {
        PipelineScope.Session => AppScope.Session,
        PipelineScope.Always => AppScope.Always,
        _ => AppScope.Once,
    };

    /// <summary>A rule in the card's words: "allow <c>rm -r</c>" with its absolute paths, which narrowing may only shorten.</summary>
    public static RulePreview Preview(ApprovalRule rule) => new(rule.Decision == RuleDecision.Allow ? "allow" : "deny",
        rule.Subcommand is { Length: > 0 } sub ? $"{rule.Verb} {sub}" : rule.Verb)
    {
        Paths = rule.Paths,
        Scope = Scope(rule),
    };

    /// <summary>The rule's scope beyond its paths, in words: its effects, hosts and processes, and elevation.</summary>
    private static string Scope(ApprovalRule rule)
    {
        var parts = new List<string>();
        var effects = Effects(rule.Effects);
        if (!effects.IsEmpty)
            parts.Add("effects: " + string.Join(", ", effects));
        if (!rule.Names.IsEmpty)
            parts.Add("names: " + string.Join(", ", rule.Names));
        parts.Add(rule.Elevated ? "elevated" : "no elevation");
        return string.Join("; ", parts);
    }

    /// <summary>The rule a session or always answer wrote, as the resolved card says it ("`rm -r` under /work/build").</summary>
    public static string Describe(ApprovalRule rule) =>
        $"`{(rule.Subcommand is { Length: > 0 } sub ? $"{rule.Verb} {sub}" : rule.Verb)}`"
        + (rule.Paths.IsEmpty ? "" : " under " + string.Join(", ", rule.Paths))
        + (rule.Names.IsEmpty ? "" : " for " + string.Join(", ", rule.Names));

    private static ParsedCommand Command(AnalyzedCommand command) => new(command.Text, command.Verb)
    {
        Subcommand = command.Subcommand,
        Flags = command.Arguments.IsDefaultOrEmpty ? [] : [.. command.Arguments.Skip(1).Where(argument => argument.StartsWith('-') && argument.Length > 1)],
        Effects = Effects(command.Effects),
        Targets = [.. command.Targets.Select(target => new CommandTarget(target.Value, Access(target.Access))
        {
            Kind = Kind(target.Kind),
            Location = Location(target.Location),
            Detail = target.Dynamic ? "known only when it runs" : target.Recursive ? "and everything under it" : null,
        })],
        Via = command.Via.IsDefault ? [] : command.Via,
        Unresolved = command.Unresolved.IsDefault ? [] : command.Unresolved,
    };

    /// <summary>The pipeline's effect names, lower case, in flag order.</summary>
    internal static ImmutableArray<string> Effects(ActionEffects effects) =>
        [.. Enum.GetValues<ActionEffects>().Where(flag => flag != ActionEffects.None && effects.HasFlag(flag)).Select(flag => flag.ToString().ToLowerInvariant())];

    private static string Access(TargetAccess access) => access.ToString().ToLowerInvariant();

    private static string Kind(TargetKind kind) => kind switch
    {
        TargetKind.ProcessName => "process_name",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string? Location(PathLocation? location) => location switch
    {
        null => null,
        PathLocation.WorkspaceRoot => "workspace_root",
        _ => location.Value.ToString().ToLowerInvariant(),
    };

    /// <summary>What the log holds beside a prompt: the pipeline appends its request (with the analysed action) just before asking.</summary>
    internal sealed record LoggedFacts(bool? Reversible, bool Tainted, ImmutableArray<DetectionInfo> Detections)
    {
        public static LoggedFacts None { get; } = new(null, false, []);
    }

    /// <summary>
    /// Reads the prompt's <c>ApprovalRequested</c> event (its action document says whether a checkpoint makes the call
    /// reversible and whether the turn was tainted) and the detections that forced it, from the latest events.
    /// </summary>
    public static LoggedFacts Read(IEventLog log, string sessionId, ApprovalPrompt prompt, int window = 128)
    {
        var head = log.GetHead(sessionId);
        var events = log.Read(sessionId, Math.Max(0, head - window), head, window);
        bool? reversible = null;
        var tainted = false;
        if (events.Select(item => item.Payload).OfType<LoggedRequest>().LastOrDefault(request => request.ApprovalId == prompt.ApprovalId) is { } logged)
        {
            try
            {
                using var document = JsonDocument.Parse(logged.Action);
                var root = document.RootElement;
                if (root.TryGetProperty("reversible", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    reversible = value.GetBoolean();
                tainted = root.TryGetProperty("tainted", out var taint) && taint.ValueKind == JsonValueKind.True;
            }
            catch (JsonException)
            {
                // An action document that does not parse says nothing; the card shows "unknown".
            }
        }
        var detections = prompt.CallId is not { } callId
            ? []
            : events.Select(item => item.Payload).OfType<LoggedDetection>()
                .Where(matched => matched.CallId == callId)
                .Select(matched => Detection(log, sessionId, matched))
                .ToImmutableArray();
        return new LoggedFacts(reversible, tainted, detections);
    }

    public static DetectionInfo Detection(IEventLog log, string sessionId, LoggedDetection matched) =>
        new(matched.RuleId, matched.Severity, matched.Posture, matched.Reason)
        {
            Events = [.. matched.Sequences.Select(sequence => new DetectionEvent(sequence, Summary(log, sessionId, sequence)))],
            Enforced = matched.Enforced == "ask",
            CallId = matched.CallId,
        };

    private static string Summary(IEventLog log, string sessionId, long sequence)
    {
        var item = log.Read(sessionId, sequence - 1, sequence, 1).FirstOrDefault();
        return item?.Payload switch
        {
            ToolCallIssued call => $"{call.ToolName} call",
            ToolResult result => $"result of {result.CallId}",
            Corvid.Abstractions.Events.UserMessage => "a message",
            null => "an event no longer in the log",
            var other => other.GetType().Name,
        };
    }
}
