using System.Globalization;
using System.Text.Json;
using Corvid.Tools.CodeMode;

namespace Corvid.Host.Sessions;

/// <summary>
/// A7: what a surface shows of a code mode run (<c>execute_code</c>): its language, its source, and how much of the run's
/// limits it used. Read from the call's arguments and result as logged; the source is model text, so a surface shows it
/// through its own cleaning (the app's <c>SafeText</c>, the terminal's <c>TerminalText</c>), revealed where a person approves it.
/// </summary>
/// <param name="Language">The language as the call named it ("python"), or null when it named none.</param>
/// <param name="DisplayLanguage">"Python", "C#", "JavaScript", or the name as given when it is none of those.</param>
/// <param name="Source">The script, as the call holds it.</param>
public sealed record CodeRunView(string? Language, string DisplayLanguage, string Source)
{
    /// <summary>The script's lines, for a card that says how long it is.</summary>
    public int Lines => Source.Length == 0 ? 0 : Source.TrimEnd('\n').Split('\n').Length;
}

/// <summary>A7: code mode's tool as the surfaces present it.</summary>
public static class CodeRunPresentation
{
    /// <summary>Code mode's tool (plan §15, parity row 1.6).</summary>
    public const string ToolName = "execute_code";

    /// <summary>The argument names the script may be under, first found wins.</summary>
    private static readonly string[] SourceNames = ["code", "source", "script"];

    public static bool IsCodeRun(string? tool) => tool == ToolName;

    /// <summary>The run's language and source from its arguments; null for another tool or arguments without a script.</summary>
    public static CodeRunView? Read(string? tool, JsonElement? arguments)
    {
        if (!IsCodeRun(tool) || arguments is not { ValueKind: JsonValueKind.Object } args)
            return null;
        var source = SourceNames.Select(name => args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null)
            .FirstOrDefault(value => value is not null);
        if (source is null)
            return null;
        var language = args.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String ? lang.GetString() : null;
        var display = CodeModeClients.TryParseLanguage(language, out var known) ? CodeModeClients.DisplayName(known) : language ?? "script";
        return new CodeRunView(language, display, source);
    }

    /// <summary>
    /// How much of the run's limits it used, in a line: "3 of 50 tool calls · 12 KB of 8 MB results · exit 0 · timed out".
    /// The calls are counted from the run's nested calls (<paramref name="nestedCalls"/>) unless its result says; the rest
    /// is read from the result when it has it (<c>calls</c>, <c>response_bytes</c>, <c>exit_code</c>, <c>timed_out</c>).
    /// </summary>
    public static string Usage(int nestedCalls, JsonElement? result = null, CodeModeLimits? limits = null)
    {
        limits ??= CodeModeLimits.Default;
        var calls = Integer(result, "tool_calls", "calls") ?? nestedCalls;
        var used = string.Create(CultureInfo.InvariantCulture, $"{calls} of {limits.MaxCalls} tool call{(limits.MaxCalls == 1 ? "" : "s")}");
        var outcome = Outcome(result, limits);
        return outcome.Length == 0 ? used : used + " · " + outcome;
    }

    /// <summary>
    /// How a run ended, from its result when it says: "12 KB of 8 MB results · exit 1 · timed out"; empty when it says
    /// none of these (<c>response_bytes</c>, <c>exit_code</c>, <c>timed_out</c>).
    /// </summary>
    public static string Outcome(JsonElement? result, CodeModeLimits? limits = null)
    {
        limits ??= CodeModeLimits.Default;
        var parts = new List<string>();
        if (Integer(result, "response_bytes", "output_bytes") is { } bytes)
            parts.Add($"{Size(bytes)} of {Size(limits.MaxTotalResponseBytes)} results");
        if (Integer(result, "exit_code", "exitCode") is { } exit)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"exit {exit}"));
        if (Boolean(result, "timed_out", "timedOut") == true)
            parts.Add("timed out");
        return string.Join(" · ", parts);
    }

    /// <summary>The limits a run starts with, in a line: "at most 50 tool calls, 8 MB of results, no network".</summary>
    public static string LimitsText(CodeModeLimits? limits = null)
    {
        limits ??= CodeModeLimits.Default;
        return string.Create(CultureInfo.InvariantCulture,
            $"at most {limits.MaxCalls} tool calls, {Size(limits.MaxResponseBytes)} per result and {Size(limits.MaxTotalResponseBytes)} of results in all; no network");
    }

    /// <summary>The line an approval card adds for every run: what is and is not granted by approving the script.</summary>
    public const string EachCallAsked =
        "Approving runs the script in the sandbox. Each tool it calls goes through the approval pipeline on its own, asked exactly as the model's own call would be; taint and egress apply to every call.";

    private static long? Integer(JsonElement? result, params string[] names)
    {
        if (result is not { ValueKind: JsonValueKind.Object } value)
            return null;
        foreach (var name in names)
            if (value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.Number && item.TryGetInt64(out var number))
                return number;
        return null;
    }

    private static bool? Boolean(JsonElement? result, params string[] names)
    {
        if (result is not { ValueKind: JsonValueKind.Object } value)
            return null;
        foreach (var name in names)
            if (value.TryGetProperty(name, out var item) && item.ValueKind is JsonValueKind.True or JsonValueKind.False)
                return item.GetBoolean();
        return null;
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
        >= 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
    };
}
