using System.Collections.Immutable;

namespace Corvid.Skills;

/// <summary>
/// Hermes' activation rules over Corvid's tools (plan §11). Corvid has no toolsets yet (C-005), so a Hermes toolset is
/// available when any of the Corvid tools it maps to is registered; Hermes tool names map to their Corvid equivalents.
/// A toolset or tool Corvid does not know is unavailable: a skill that requires it stays hidden, and a skill that stands in
/// for it is shown.
/// </summary>
public static class SkillAvailability
{
    public static ImmutableDictionary<string, ImmutableArray<string>> Toolsets { get; } = new Dictionary<string, ImmutableArray<string>>(StringComparer.OrdinalIgnoreCase)
    {
        ["terminal"] = ["shell"],
        ["file"] = ["read_file", "list_files", "search_files", "write_file", "patch_file"],
        ["web"] = ["web_fetch"],
        ["memory"] = ["memory_search", "memory_add"],
        ["cronjob"] = ["cron_create", "cron_list", "cron_remove"],
        ["skills"] = ["skill_read"],
    }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>Hermes tool names that Corvid calls something else.</summary>
    public static ImmutableDictionary<string, string> ToolNames { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["terminal"] = "shell",
        ["patch"] = "patch_file",
        ["web_extract"] = "web_fetch",
        ["skill_view"] = "skill_read",
        ["skills_list"] = "skill_read",
    }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>The platform name Hermes uses for this process: <c>linux</c>, <c>macos</c> or <c>windows</c>.</summary>
    public static string CurrentPlatform =>
        OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "other";

    /// <summary>Whether the skill may be shown, and if not, why.</summary>
    public static bool IsAvailable(SkillActivation activation, IReadOnlyCollection<string> tools, string platform, out string? reason)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ArgumentNullException.ThrowIfNull(tools);
        var registered = tools as IReadOnlySet<string> ?? tools.ToHashSet(StringComparer.Ordinal);
        bool Tool(string name) => registered.Contains(ToolNames.TryGetValue(name, out var mapped) ? mapped : name);
        bool Toolset(string name) => Toolsets.TryGetValue(name, out var members) && members.Any(registered.Contains);

        reason = null;
        if (!activation.Platforms.IsDefaultOrEmpty && !activation.Platforms.Contains(platform, StringComparer.OrdinalIgnoreCase))
            reason = $"platforms: {string.Join(", ", activation.Platforms)}";
        else if (activation.RequiresToolsets.FirstOrDefault(set => !Toolset(set)) is { } missingSet)
            reason = $"requires toolset {missingSet}";
        else if (activation.RequiresTools.FirstOrDefault(tool => !Tool(tool)) is { } missingTool)
            reason = $"requires tool {missingTool}";
        else if (activation.FallbackForToolsets.FirstOrDefault(Toolset) is { } presentSet)
            reason = $"fallback for toolset {presentSet}, which is available";
        else if (activation.FallbackForTools.FirstOrDefault(Tool) is { } presentTool)
            reason = $"fallback for tool {presentTool}, which is available";
        return reason is null;
    }
}
