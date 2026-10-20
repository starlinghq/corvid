using System.Collections.Immutable;
using System.Text;

namespace Corvid.Skills;

/// <summary>
/// Hermes' activation metadata (plan §11), read from <c>metadata.hermes</c> as Hermes writes it, or from the top level.
/// <c>platforms</c> is top-level in Hermes.
/// </summary>
/// <param name="Platforms"><c>linux</c>, <c>macos</c>, <c>windows</c>; empty means every platform.</param>
/// <param name="RequiresToolsets">Hidden unless every listed toolset is available.</param>
/// <param name="RequiresTools">Hidden unless every listed tool is registered.</param>
/// <param name="FallbackForToolsets">Hidden while any listed toolset is available: the skill stands in for it.</param>
/// <param name="FallbackForTools">Hidden while any listed tool is registered.</param>
public sealed record SkillActivation(
    ImmutableArray<string> Platforms,
    ImmutableArray<string> RequiresToolsets,
    ImmutableArray<string> RequiresTools,
    ImmutableArray<string> FallbackForToolsets,
    ImmutableArray<string> FallbackForTools)
{
    public static SkillActivation Always { get; } = new([], [], [], [], []);
}

/// <summary>A parsed SKILL.md: its frontmatter (L0 and metadata) and its body (L1).</summary>
/// <param name="Examples">Example requests for the multi-vector shortlist (plan §5.4), from <c>metadata.corvid.examples</c>.</param>
public sealed record SkillDocument(
    string Name,
    string Description,
    string Body,
    SkillActivation Activation,
    ImmutableArray<string> Examples,
    ImmutableArray<string> Tags,
    string? Version,
    YamlMap Frontmatter,
    string Text)
{
    public const string FileName = "SKILL.md";

    /// <summary>agentskills.io's limits: a name of 1–64 characters, a description of 1–1,024.</summary>
    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1_024;

    /// <summary>Corvid's own cap on SKILL.md, so a single skill cannot fill the prompt or the store.</summary>
    public const int MaxBytes = 256 * 1024;

    /// <exception cref="SkillFormatException">The file is not a SKILL.md Corvid can read exactly.</exception>
    public static SkillDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaxBytes)
            throw new SkillFormatException($"SKILL.md is larger than {MaxBytes / 1024} KiB.");
        var normalized = text.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            throw new SkillFormatException("SKILL.md must start with a '---' frontmatter block.");
        var close = normalized.IndexOf("\n---", 3, StringComparison.Ordinal);
        while (close >= 0 && !(close + 4 == normalized.Length || normalized[close + 4] == '\n'))
            close = normalized.IndexOf("\n---", close + 4, StringComparison.Ordinal);
        if (close < 0)
            throw new SkillFormatException("SKILL.md's frontmatter block is not closed with '---'.");
        var header = normalized[4..(close + 1)];
        var body = close + 4 >= normalized.Length ? "" : normalized[(close + 5)..];

        YamlMap map;
        try
        {
            map = Corvid.Skills.Frontmatter.Parse(header);
        }
        catch (FrontmatterException exception)
        {
            throw new SkillFormatException("frontmatter " + exception.Message);
        }

        var name = map.Text("name")?.Trim();
        if (string.IsNullOrEmpty(name))
            throw new SkillFormatException("the frontmatter has no name.");
        if (!IsValidName(name))
            throw new SkillFormatException($"'{name}' is not a valid skill name: 1–{MaxNameLength} lowercase letters, digits, '-' or '_', starting with a letter or digit.");
        var description = map.Text("description")?.Trim();
        if (string.IsNullOrEmpty(description))
            throw new SkillFormatException("the frontmatter has no description.");
        if (description.Length > MaxDescriptionLength)
            throw new SkillFormatException($"the description is longer than {MaxDescriptionLength} characters.");

        var hermes = map.Map("metadata")?.Map("hermes");
        var corvid = map.Map("metadata")?.Map("corvid");
        ImmutableArray<string> Field(string key) => hermes is not null && hermes[key] is not null ? hermes.Strings(key) : map.Strings(key);
        var activation = new SkillActivation(
            [.. Field("platforms").Select(platform => platform.ToLowerInvariant())],
            Field("requires_toolsets"), Field("requires_tools"), Field("fallback_for_toolsets"), Field("fallback_for_tools"));
        return new SkillDocument(name, description, body, activation,
            corvid?.Strings("examples") ?? [], Field("tags"), map.Text("version"), map, normalized);
    }

    public static bool IsValidName(string name) =>
        name.Length is > 0 and <= MaxNameLength && (char.IsAsciiLetterLower(name[0]) || char.IsAsciiDigit(name[0]))
        && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '_');

    /// <summary>A SKILL.md with the given fields, for skills the agent writes. Strings are double-quoted and escaped.</summary>
    public static string Compose(string name, string description, string body, IEnumerable<string>? examples = null)
    {
        var text = new StringBuilder("---\n");
        text.Append("name: ").Append(name).Append('\n');
        text.Append("description: ").Append(Quote(description)).Append('\n');
        var list = examples?.Where(example => !string.IsNullOrWhiteSpace(example)).ToList() ?? [];
        if (list.Count > 0)
        {
            text.Append("metadata:\n  corvid:\n    examples:\n");
            foreach (var example in list)
                text.Append("      - ").Append(Quote(example)).Append('\n');
        }
        text.Append("---\n\n").Append(body.Trim()).Append('\n');
        return text.ToString();
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "\"";
}

public sealed class SkillFormatException(string message) : FormatException(message);
