using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Corvid.Abstractions.Tokens;

namespace Corvid.Fabric;

/// <summary>
/// The default estimate: one token per four UTF-16 characters, rounded up. Deterministic and model-free; a model's own
/// counter (for example the Starling generator binding's, C-010) can replace it.
/// </summary>
public sealed class CharsPerTokenCounter : ITokenCounter
{
    public static CharsPerTokenCounter Instance { get; } = new();

    public int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (text.Length + 3) / 4;
    }
}

/// <summary>What the fabric knows about the turn (plan §5.1). Never the transcript.</summary>
/// <param name="Recent">One-line summaries of recent log events, oldest first.</param>
/// <param name="Entities">Extracted spans by label (from the extractor slot; not wired yet). Labels are written in ordinal order.</param>
/// <param name="Generator">The generator slot as the decider should read it, for example <c>qwen3.5-0.8b local, 8k</c>.</param>
/// <param name="Steering">
/// What the user sent into the running turn (K-3, steer now), newest last, joined by newlines: rendered as <c>steering</c>
/// after <c>user_message</c>, and only when there is some, so a state without it is unchanged.
/// </param>
public sealed record TurnStateInput(
    string UserMessage,
    string Surface,
    string Generator,
    ImmutableArray<string> Recent = default,
    string? Goal = null,
    ImmutableSortedDictionary<string, ImmutableArray<string>>? Entities = null,
    int Step = 0,
    string? LastToolResult = null,
    string? Steering = null);

/// <summary>Token budgets for the TurnState. Field budgets bound each field; <see cref="Total"/> bounds the whole state.</summary>
/// <param name="Total">The target size of the rendered state (plan §5.1: 600).</param>
/// <param name="HardStop">Beyond this the state goes to no decider that enforces it; the checkpoint falls back (plan §5.1: 1,500).</param>
public sealed record TurnStateBudget(
    int Total = 600,
    int HardStop = 1_500,
    int UserMessage = 200,
    int Surface = 8,
    int Recent = 160,
    int Goal = 48,
    int Entities = 64,
    int LastToolResult = 80,
    int Generator = 16,
    int Steering = 120)
{
    public static TurnStateBudget Default { get; } = new();

    public TurnStateBudget Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(Total);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(Total, HardStop);
        foreach (var field in new[] { UserMessage, Surface, Recent, Goal, Entities, LastToolResult, Generator, Steering })
            ArgumentOutOfRangeException.ThrowIfNegative(field);
        return this;
    }
}

/// <summary>A rendered TurnState.</summary>
/// <param name="Tokens">The rendered JSON's size by the builder's counter.</param>
/// <param name="ExceedsHardStop">
/// Still over the hard stop after every truncation (only fixed fields remain). The fabric skips the calibrated decider and
/// uses the degraded tier, which can still embed the truncated text.
/// </param>
/// <param name="Truncated">Fields shortened or dropped, in field order.</param>
public sealed record TurnState(JsonElement Json, int Tokens, bool ExceedsHardStop, ImmutableArray<string> Truncated)
{
    public string ToJsonString() => Json.GetRawText();
}

/// <summary>
/// Builds the TurnState with per-field budgets and deterministic truncation: strings keep their longest prefix that fits
/// (ending in "…"), <c>recent</c> keeps its newest items, entities keep labels and values in order. If the whole state is
/// still over <see cref="TurnStateBudget.Total"/>, fields are cut further in a fixed order. The same input always yields the
/// same bytes.
/// </summary>
public sealed class TurnStateBuilder(ITokenCounter? counter = null, TurnStateBudget? budget = null)
{
    private const string Ellipsis = "…";
    // Each list item or entity value costs at least its separators; this keeps zero-length items from being free.
    private const int ItemOverhead = 1;

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // The state is ASCII-escaped by default; relaxed escaping keeps it readable to the decider. It is never embedded in HTML.
    private readonly ITokenCounter _counter = counter ?? CharsPerTokenCounter.Instance;
    private readonly TurnStateBudget _budget = (budget ?? TurnStateBudget.Default).Validate();

    // Cut first what the decider needs least.
    private static readonly string[] TrimOrder = ["recent", "last_tool_result", "entities", "goal", "steering", "user_message"];

    public ITokenCounter Counter => _counter;

    public TurnStateBudget Budget => _budget;

    public TurnState Build(TurnStateInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.UserMessage);
        ArgumentNullException.ThrowIfNull(input.Surface);
        ArgumentNullException.ThrowIfNull(input.Generator);
        ArgumentOutOfRangeException.ThrowIfNegative(input.Step);

        var budgets = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["user_message"] = _budget.UserMessage,
            ["surface"] = _budget.Surface,
            ["recent"] = _budget.Recent,
            ["goal"] = _budget.Goal,
            ["entities"] = _budget.Entities,
            ["last_tool_result"] = _budget.LastToolResult,
            ["generator"] = _budget.Generator,
            ["steering"] = _budget.Steering,
        };

        var rendered = Render(input, budgets);
        // Field counts exclude JSON escaping, so one cut may not be enough; repeat until the field fits or is empty.
        foreach (var field in TrimOrder)
        {
            while (rendered.Tokens > _budget.Total && budgets[field] > 0)
            {
                var overflow = rendered.Tokens - _budget.Total;
                budgets[field] = Math.Max(0, Math.Min(budgets[field], rendered.FieldTokens[field]) - overflow);
                rendered = Render(input, budgets);
            }
        }

        using var document = JsonDocument.Parse(rendered.Json);
        return new TurnState(document.RootElement.Clone(), rendered.Tokens, rendered.Tokens > _budget.HardStop,
            [.. rendered.Truncated]);
    }

    private Rendered Render(TurnStateInput input, IReadOnlyDictionary<string, int> budgets)
    {
        var truncated = new List<string>();
        var fieldTokens = new Dictionary<string, int>(StringComparer.Ordinal);

        string? Text(string field, string? value)
        {
            if (value is null)
            {
                fieldTokens[field] = 0;
                return null;
            }
            var result = Truncate(value, budgets[field]);
            if (!ReferenceEquals(result, value))
                truncated.Add(field);
            fieldTokens[field] = _counter.CountTokens(result);
            return result;
        }

        var userMessage = Text("user_message", input.UserMessage)!;
        var steering = Text("steering", string.IsNullOrEmpty(input.Steering) ? null : input.Steering);
        var surface = Text("surface", input.Surface)!;
        var recent = Recent(input.Recent.IsDefault ? [] : input.Recent, budgets["recent"], out var recentTokens, out var recentCut);
        fieldTokens["recent"] = recentTokens;
        if (recentCut)
            truncated.Add("recent");
        var goal = Text("goal", input.Goal);
        var entities = Entities(input.Entities ?? ImmutableSortedDictionary<string, ImmutableArray<string>>.Empty,
            budgets["entities"], out var entityTokens, out var entitiesCut);
        fieldTokens["entities"] = entityTokens;
        if (entitiesCut)
            truncated.Add("entities");
        var lastToolResult = Text("last_tool_result", input.LastToolResult);
        var generator = Text("generator", input.Generator)!;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            // Field order and names are part of the decider's prompt (plan §5.1).
            writer.WriteStartObject();
            writer.WriteString("user_message", userMessage);
            // K-3: written only when the user steered the turn, so every other state keeps its bytes.
            if (steering is not null)
                writer.WriteString("steering", steering);
            writer.WriteString("surface", surface);
            writer.WriteStartArray("recent");
            foreach (var item in recent)
                writer.WriteStringValue(item);
            writer.WriteEndArray();
            WriteNullable(writer, "goal", goal);
            writer.WriteStartObject("entities");
            foreach (var (label, values) in entities)
            {
                writer.WriteStartArray(label);
                foreach (var value in values)
                    writer.WriteStringValue(value);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
            writer.WriteNumber("step", input.Step);
            WriteNullable(writer, "last_tool_result", lastToolResult);
            writer.WriteString("generator", generator);
            writer.WriteEndObject();
        }

        var json = Encoding.UTF8.GetString(buffer.WrittenSpan);
        var order = new[] { "user_message", "steering", "surface", "recent", "goal", "entities", "last_tool_result", "generator" };
        return new Rendered(json, _counter.CountTokens(json), fieldTokens, order.Where(truncated.Contains).ToList());
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
            writer.WriteNull(name);
        else
            writer.WriteString(name, value);
    }

    /// <summary>The newest items that fit, each cut to at most half the budget so one long line cannot crowd out the rest.</summary>
    private List<string> Recent(ImmutableArray<string> items, int budget, out int tokens, out bool cut)
    {
        var kept = new List<string>();
        tokens = 0;
        cut = false;
        var perItem = Math.Max(1, budget / 2);
        for (var i = items.Length - 1; i >= 0; i--)
        {
            var item = Truncate(items[i] ?? "", perItem);
            cut |= !ReferenceEquals(item, items[i]);
            var cost = _counter.CountTokens(item) + ItemOverhead;
            if (tokens + cost > budget)
            {
                cut = true;
                break;
            }
            kept.Add(item);
            tokens += cost;
        }
        kept.Reverse();
        return kept;
    }

    private List<KeyValuePair<string, List<string>>> Entities(ImmutableSortedDictionary<string, ImmutableArray<string>> entities,
        int budget, out int tokens, out bool cut)
    {
        var kept = new List<KeyValuePair<string, List<string>>>();
        tokens = 0;
        cut = false;
        foreach (var (label, values) in entities.WithComparers(StringComparer.Ordinal))
        {
            var labelCost = _counter.CountTokens(label) + ItemOverhead;
            if (tokens + labelCost > budget)
            {
                cut = true;
                break;
            }
            tokens += labelCost;
            var list = new List<string>();
            foreach (var value in values.IsDefault ? [] : values)
            {
                var cost = _counter.CountTokens(value ?? "") + ItemOverhead;
                if (tokens + cost > budget)
                {
                    cut = true;
                    break;
                }
                list.Add(value ?? "");
                tokens += cost;
            }
            kept.Add(new(label, list));
            if (cut)
                break;
        }
        return kept;
    }

    /// <summary>The longest prefix that fits with an ellipsis, never splitting a surrogate pair. Returns the same instance when it fits.</summary>
    internal string Truncate(string text, int budget)
    {
        if (_counter.CountTokens(text) <= budget)
            return text;
        if (budget <= 0 || _counter.CountTokens(Ellipsis) > budget)
            return "";
        int low = 0, high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (_counter.CountTokens(string.Concat(Prefix(text, mid), Ellipsis)) <= budget)
                low = mid;
            else
                high = mid - 1;
        }
        return string.Concat(Prefix(text, low), Ellipsis);
    }

    private static ReadOnlySpan<char> Prefix(string text, int length)
    {
        if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1]))
            length--;
        return text.AsSpan(0, length);
    }

    private sealed record Rendered(string Json, int Tokens, Dictionary<string, int> FieldTokens, List<string> Truncated);
}
