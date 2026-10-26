using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Corvid.Models;

/// <summary>
/// One slot of a <see cref="ModelMix"/>: the model by its catalogue name, and optionally the slot's escalation ladder and
/// fallbacks. Names are untrusted text (a model wrote them, or a configuration file): <see cref="ModelMix"/> refuses one
/// holding hidden characters, and a surface shows them through its own sanitizer.
/// </summary>
/// <param name="Model">The catalogue name of the slot's default for the session.</param>
/// <param name="Ladder">
/// Where escalation goes from it, in order; null keeps the ladder the session would otherwise have (its parent's, or the
/// registry's), without <paramref name="Model"/> itself.
/// </param>
/// <param name="Fallbacks">What a failed provider falls back to, in order; null keeps the configured chain.</param>
/// <param name="Escalate">False: the slot never escalates in this session (an empty ladder, whatever is configured).</param>
public sealed record ModelMixSlot(string Model, ImmutableArray<string>? Ladder = null, ImmutableArray<string>? Fallbacks = null, bool Escalate = true);

/// <summary>A mix that does not parse or validate; the message names the slot and says what is wrong, names escaped.</summary>
public sealed class ModelMixException(string message) : FormatException(message);

/// <summary>
/// A model mix (A5): which model serves each slot for one session, by slot name (<see cref="ModelSlots.All"/>), with an
/// optional ladder per slot and which slots may escalate. The agent passes one to <c>delegate_task</c> (any slot), the person
/// to <c>/delegate</c> or the app, and <c>corvid.json</c> names them under <c>Corvid:Agents:Mixes</c>. This type checks only
/// the shape; the host resolves the names against the model catalogue, their availability, the allowlist
/// (<c>Corvid:Agents:Models</c>) and the privacy egress gate before anything is bound.
/// </summary>
/// <remarks>
/// The JSON shape (<see cref="Schema"/>), the same in a tool argument and in configuration, is an object keyed by slot whose
/// values are a model name, or an object:
/// <code>
/// { "generator": "qwen3.5-9b",
///   "decider": { "model": "kev-4b", "escalate": false },
///   "summarizer": { "model": "qwen3.5-0.8b", "ladder": ["qwen3.5-9b"], "fallbacks": [] } }
/// </code>
/// Property names are matched without regard to case; an unknown slot or property is refused rather than ignored.
/// </remarks>
public sealed class ModelMix
{
    /// <summary>The longest model name accepted.</summary>
    public const int MaxNameLength = 200;

    /// <summary>The most models on one slot's ladder or fallback chain.</summary>
    public const int MaxChain = 8;

    private ModelMix(ImmutableSortedDictionary<string, ModelMixSlot> slots) => Slots = slots;

    public static ModelMix Empty { get; } = new(ImmutableSortedDictionary.Create<string, ModelMixSlot>(StringComparer.Ordinal));

    /// <summary>The slots the mix sets, by slot name in ordinal order; slots it leaves out keep the session's.</summary>
    public ImmutableSortedDictionary<string, ModelMixSlot> Slots { get; }

    public bool IsEmpty => Slots.IsEmpty;

    /// <summary>The JSON Schema of the shape, for a tool's argument.</summary>
    public static string Schema { get; } = BuildSchema();

    /// <summary>A mix from slots already in hand (configuration), checked as <see cref="Parse(string)"/> checks JSON.</summary>
    /// <exception cref="ModelMixException">A slot is unknown, a name is blank, too long or holds hidden characters, or a chain is wrong.</exception>
    public static ModelMix Create(IEnumerable<KeyValuePair<string, ModelMixSlot>> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);
        var builder = ImmutableSortedDictionary.CreateBuilder<string, ModelMixSlot>(StringComparer.Ordinal);
        foreach (var (name, slot) in slots)
        {
            var key = SlotName(name);
            if (builder.ContainsKey(key))
                throw new ModelMixException($"The mix names the {key} slot twice.");
            builder[key] = Checked(key, slot ?? throw new ModelMixException($"The {key} slot has no model."));
        }
        return new(builder.ToImmutable());
    }

    /// <exception cref="ModelMixException">Not JSON, or not a valid mix.</exception>
    public static ModelMix Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            return Parse(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new ModelMixException($"The mix is not JSON: {exception.Message}");
        }
    }

    /// <exception cref="ModelMixException">Not a valid mix.</exception>
    public static ModelMix Parse(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return Empty;
        if (element.ValueKind != JsonValueKind.Object)
            throw new ModelMixException("A mix is an object keyed by slot (generator, summarizer, decider, extractor, embedder.text, embedder.media, vision).");
        var slots = new List<KeyValuePair<string, ModelMixSlot>>();
        foreach (var property in element.EnumerateObject())
        {
            var slot = SlotName(property.Name);
            slots.Add(KeyValuePair.Create(slot, property.Value.ValueKind switch
            {
                JsonValueKind.String => new ModelMixSlot(property.Value.GetString()!),
                JsonValueKind.Object => SlotFrom(slot, property.Value),
                _ => throw new ModelMixException($"The {slot} slot is a model name or an object with a model."),
            }));
        }
        return Create(slots);
    }

    /// <summary>Parses, or says why not.</summary>
    public static bool TryParse(JsonElement element, out ModelMix mix, out string? error)
    {
        try
        {
            mix = Parse(element);
            error = null;
            return true;
        }
        catch (ModelMixException exception)
        {
            mix = Empty;
            error = exception.Message;
            return false;
        }
    }

    /// <summary>This mix with <paramref name="other"/>'s slots over it (a passed mix over a configured one).</summary>
    public ModelMix With(ModelMix other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return other.IsEmpty ? this : new(Slots.SetItems(other.Slots));
    }

    /// <summary>The mix in its JSON shape: a bare name for a slot with nothing else set, an object otherwise.</summary>
    public JsonObject ToJson()
    {
        var root = new JsonObject();
        foreach (var (name, slot) in Slots)
        {
            if (slot is { Ladder: null, Fallbacks: null, Escalate: true })
            {
                root[name] = slot.Model;
                continue;
            }
            var value = new JsonObject { ["model"] = slot.Model };
            if (slot.Ladder is { } ladder)
                value["ladder"] = new JsonArray([.. ladder.Select(step => (JsonNode?)JsonValue.Create(step))]);
            if (slot.Fallbacks is { } fallbacks)
                value["fallbacks"] = new JsonArray([.. fallbacks.Select(step => (JsonNode?)JsonValue.Create(step))]);
            if (!slot.Escalate)
                value["escalate"] = false;
            root[name] = value;
        }
        return root;
    }

    public override string ToString() => ToJson().ToJsonString();

    /// <summary>
    /// Untrusted text written so nothing in it is hidden: control, format, private-use, unassigned and variation-selector
    /// characters as <c>\uXXXX</c>, cut to <paramref name="max"/> characters. For messages about a name that was refused.
    /// </summary>
    public static string Escape(string text, int max = 80)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(Math.Min(text.Length, max) + 8);
        foreach (var rune in text.EnumerateRunes())
        {
            if (builder.Length >= max)
            {
                builder.Append('…');
                break;
            }
            if (IsHidden(rune) || rune.Value is '\n' or '\r' or '\t')
                builder.Append(CultureInfo.InvariantCulture, $"\\u{rune.Value:X4}");
            else
                builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    private static ModelMixSlot SlotFrom(string slot, JsonElement value)
    {
        string? model = null;
        ImmutableArray<string>? ladder = null, fallbacks = null;
        var escalate = true;
        foreach (var property in value.EnumerateObject())
        {
            switch (property.Name.ToLowerInvariant())
            {
                case "model":
                    model = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()
                        : throw new ModelMixException($"The {slot} slot's model is a name.");
                    break;
                case "ladder" or "escalation":
                    ladder = Names(slot, "ladder", property.Value);
                    break;
                case "fallbacks":
                    fallbacks = Names(slot, "fallbacks", property.Value);
                    break;
                case "escalate":
                    escalate = property.Value.ValueKind switch
                    {
                        JsonValueKind.True => true,
                        JsonValueKind.False => false,
                        _ => throw new ModelMixException($"The {slot} slot's escalate is true or false."),
                    };
                    break;
                default:
                    throw new ModelMixException($"The {slot} slot has no setting \"{Escape(property.Name, 40)}\"; it takes model, ladder, fallbacks and escalate.");
            }
        }
        return new ModelMixSlot(model ?? throw new ModelMixException($"The {slot} slot names no model."), ladder, fallbacks, escalate);
    }

    private static ImmutableArray<string> Names(string slot, string what, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
            throw new ModelMixException($"The {slot} slot's {what} is a list of model names.");
        var names = ImmutableArray.CreateBuilder<string>();
        foreach (var item in value.EnumerateArray())
            names.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new ModelMixException($"The {slot} slot's {what} holds something other than a name."));
        return names.ToImmutable();
    }

    private static string SlotName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var trimmed = name.Trim();
        return ModelSlots.All.FirstOrDefault(slot => string.Equals(slot, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? throw new ModelMixException($"There is no slot named \"{Escape(name, 40)}\"; the slots are {string.Join(", ", ModelSlots.All)}.");
    }

    private static ModelMixSlot Checked(string slot, ModelMixSlot value)
    {
        var model = Name(slot, "model", value.Model);
        ImmutableArray<string>? Chain(ImmutableArray<string>? chain, string what)
        {
            if (chain is not { } names)
                return null;
            if (names.Length > MaxChain)
                throw new ModelMixException($"The {slot} slot's {what} has {names.Length} models; at most {MaxChain}.");
            var checkedNames = names.Select(step => Name(slot, what, step)).ToImmutableArray();
            if (checkedNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != checkedNames.Length)
                throw new ModelMixException($"The {slot} slot's {what} names a model twice.");
            if (checkedNames.Contains(model, StringComparer.OrdinalIgnoreCase))
                throw new ModelMixException($"The {slot} slot's {what} holds its own model, {Escape(model)}.");
            return checkedNames;
        }
        var ladder = Chain(value.Ladder, "ladder");
        if (!value.Escalate && ladder is { Length: > 0 })
            throw new ModelMixException($"The {slot} slot has a ladder but may not escalate; give one or the other.");
        return new ModelMixSlot(model, value.Escalate ? ladder : [], Chain(value.Fallbacks, "fallbacks"), value.Escalate);
    }

    private static string Name(string slot, string what, string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            throw new ModelMixException($"The {slot} slot's {what} has a blank model name.");
        if (trimmed.Length > MaxNameLength)
            throw new ModelMixException($"The {slot} slot's {what} names a model {trimmed.Length} characters long; at most {MaxNameLength}.");
        foreach (var rune in trimmed.EnumerateRunes())
            if (IsHidden(rune) || Rune.IsWhiteSpace(rune) && rune.Value != ' ')
                throw new ModelMixException($"The {slot} slot's {what} names \"{Escape(trimmed)}\", which holds characters nobody would see.");
        return trimmed;
    }

    private static bool IsHidden(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse
            or UnicodeCategory.OtherNotAssigned or UnicodeCategory.Surrogate or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
        || rune.Value is >= 0xFE00 and <= 0xFE0F or >= 0xE0100 and <= 0xE01EF;

    private static string BuildSchema()
    {
        var names = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" }, ["maxItems"] = MaxChain };
        var slot = new JsonObject
        {
            ["anyOf"] = new JsonArray(
                new JsonObject { ["type"] = "string", ["description"] = "The model's catalogue name." },
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["model"] = new JsonObject { ["type"] = "string", ["description"] = "The model's catalogue name." },
                        ["ladder"] = names.DeepClone(),
                        ["fallbacks"] = names.DeepClone(),
                        ["escalate"] = new JsonObject { ["type"] = "boolean" },
                    },
                    ["required"] = new JsonArray("model"),
                    ["additionalProperties"] = false,
                }),
        };
        var properties = new JsonObject();
        foreach (var name in ModelSlots.All)
            properties[name] = slot.DeepClone();
        return new JsonObject
        {
            ["type"] = "object",
            ["description"] = "Which model serves each slot of the child session; slots left out keep the parent's.",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        }.ToJsonString();
    }
}
