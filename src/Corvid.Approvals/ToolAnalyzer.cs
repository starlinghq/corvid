using System.Collections.Immutable;
using System.Text.Json;
using Corvid.Tools;

namespace Corvid.Approvals;

/// <summary>Describes a non-shell tool call from its C-005 risk metadata and the targets in its arguments.</summary>
internal static class ToolAnalyzer
{
    // Parameter names (lower case, without separators) whose string values are paths or addresses. Corvid.Tools has no
    // typed path parameters yet (C-005 deferred validators), so names are the signal; a caller can pass targets instead.
    private static readonly ImmutableHashSet<string> PathNames = ImmutableHashSet.Create(StringComparer.Ordinal,
        "path", "paths", "file", "files", "filename", "filepath", "dir", "directory", "folder", "destination", "dest", "source",
        "src", "target", "output", "input", "from", "to", "cwd", "workdir");

    private static readonly ImmutableHashSet<string> AddressNames = ImmutableHashSet.Create(StringComparer.Ordinal,
        "url", "uri", "urls", "endpoint", "host", "hostname", "server", "domain");

    public static ActionEffects Effects(ToolRisk risk)
    {
        var effects = ActionEffects.None;
        if (risk.SideEffects.HasFlag(SideEffects.Filesystem))
            effects |= ActionEffects.Write;
        if (risk.SideEffects.HasFlag(SideEffects.Process))
            effects |= ActionEffects.Execute;
        if (risk.SideEffects.HasFlag(SideEffects.Messaging))
            effects |= ActionEffects.Messaging | ActionEffects.Remote;
        if (risk.SideEffects.HasFlag(SideEffects.Schedule))
            effects |= ActionEffects.Schedule;
        if (risk.SideEffects.HasFlag(SideEffects.Memory))
            effects |= ActionEffects.Memory;
        if (risk.SideEffects.HasFlag(SideEffects.Configuration))
            effects |= ActionEffects.Configuration;
        if (risk.SideEffects.HasFlag(SideEffects.Remote))
            effects |= ActionEffects.Remote;
        if (risk.Network)
            effects |= ActionEffects.Network;
        if (risk.Destructive && risk.SideEffects.HasFlag(SideEffects.Filesystem))
            effects |= ActionEffects.Delete;
        return effects == ActionEffects.None ? ActionEffects.Read : effects;
    }

    public static ActionAnalysis Analyze(ToolAction action, ApprovalEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(action);
        var effects = Effects(action.Tool.Risk);
        var access = action.Tool.Risk.Destructive && action.Tool.Risk.SideEffects.HasFlag(SideEffects.Filesystem) ? TargetAccess.Delete
            : action.Tool.Risk.SideEffects.HasFlag(SideEffects.Filesystem) ? TargetAccess.Write
            : TargetAccess.Read;
        var targets = action.Targets.IsDefault
            ? FromArguments(action.Arguments, access, env)
            : [.. action.Targets.Select(target => Locate(target, env))];
        // Recipients are targets, so a remembered approval for one recipient does not cover another.
        targets = targets.AddRange(action.Recipients.OrEmpty().Select(recipient =>
            new ActionTarget(TargetKind.Recipient, recipient, TargetAccess.Send, Written: recipient)));
        var text = $"{action.Tool.Name} {action.Arguments.GetRawText()}";
        var command = new AnalyzedCommand(action.Tool.Name, null, effects, targets, [], [], false, text,
            [action.Tool.Name, .. Strings(action.Arguments)]);
        return new ActionAnalysis("tool", text, [command], null);
    }

    private static ImmutableArray<ActionTarget> FromArguments(JsonElement arguments, TargetAccess access, ApprovalEnvironment env)
    {
        var targets = ImmutableArray.CreateBuilder<ActionTarget>();
        if (arguments.ValueKind != JsonValueKind.Object)
            return [];
        foreach (var property in arguments.EnumerateObject())
        {
            var name = property.Name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
            var values = property.Value.ValueKind switch
            {
                JsonValueKind.String => [property.Value.GetString()!],
                JsonValueKind.Array => property.Value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!).ToArray(),
                _ => Array.Empty<string>(),
            };
            foreach (var value in values.Where(value => value.Length > 0))
            {
                if (AddressNames.Contains(name) || PathNames.Contains(name) && value.Contains("://", StringComparison.Ordinal))
                {
                    if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
                    {
                        targets.Add(new ActionTarget(TargetKind.Url, value, TargetAccess.Connect, Written: value));
                        targets.Add(new ActionTarget(TargetKind.Host, uri.Host.ToLowerInvariant(), TargetAccess.Connect, Written: value));
                    }
                    else
                        targets.Add(new ActionTarget(TargetKind.Host, value.ToLowerInvariant(), TargetAccess.Connect, Written: value));
                }
                else if (PathNames.Contains(name))
                    targets.Add(Locate(new ActionTarget(TargetKind.Path, value, access, Written: value), env));
            }
        }

        return targets.ToImmutable();
    }

    private static IEnumerable<string> Strings(JsonElement arguments) => arguments.ValueKind switch
    {
        JsonValueKind.Object => arguments.EnumerateObject().SelectMany(property => Strings(property.Value)),
        JsonValueKind.Array => arguments.EnumerateArray().SelectMany(Strings),
        JsonValueKind.String => [arguments.GetString()!],
        _ => [],
    };

    /// <summary>Resolves a caller's or argument's path against the workspace (a leading <c>~</c> is the home directory).</summary>
    private static ActionTarget Locate(ActionTarget target, ApprovalEnvironment env)
    {
        if (target.Kind != TargetKind.Path || target.Location is not null)
            return target;
        var value = target.Value;
        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal))
            value = env.Home + value[1..];
        if (value.Contains('\0'))
            return target with { Location = PathLocation.Unresolved };
        var full = PathMath.Normalize(Path.IsPathRooted(value) ? value : Path.Combine(env.Workspace, value));
        var location = full == env.Workspace ? PathLocation.WorkspaceRoot
            : PathMath.IsUnder(full, env.Workspace) ? PathLocation.Inside
            : PathLocation.Outside;
        return target with { Value = full, Location = location };
    }
}
