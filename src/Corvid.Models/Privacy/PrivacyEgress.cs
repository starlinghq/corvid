using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Corvid.Abstractions.Extraction;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Privacy;

public sealed record PrivacyEgressOptions
{
    /// <summary>
    /// The policy for a target profile, or null when the profile is not gated. The default gates every
    /// <see cref="PrivacyClass.Cloud"/> profile with <see cref="EgressPolicy.Default"/> and nothing else.
    /// </summary>
    public Func<ModelProfile, EgressPolicy?> PolicyFor { get; init; } = DefaultPolicyFor;

    /// <summary>Characters per extractor call. GLiNER2 takes 512 tokens including the labels; a chunk it refuses is halved.</summary>
    public int ChunkChars { get; init; } = 1_200;

    /// <summary>Characters repeated between neighbouring chunks, so a value on a boundary is seen whole in one of them.</summary>
    public int ChunkOverlap { get; init; } = 100;

    /// <summary>Below this a refused chunk is not split again; the scan fails and the request is refused.</summary>
    public int MinChunkChars { get; init; } = 48;

    /// <summary>Extraction results kept by chunk digest, so a growing conversation is scanned only where it changed.</summary>
    public int MaxCachedChunks { get; init; } = 16_384;

    /// <summary>Sessions whose placeholder maps are kept; the least recently used is dropped beyond this.</summary>
    public int MaxSessions { get; init; } = 256;

    /// <summary>Shorter spans are not treated as values to redact (a stray initial would rewrite every word containing it).</summary>
    public int MinValueLength { get; init; } = 2;

    public static EgressPolicy? DefaultPolicyFor(ModelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.Privacy == PrivacyClass.Cloud ? EgressPolicy.Default : null;
    }
}

public enum EgressOutcome
{
    /// <summary>The target has no egress policy (a local model, by default).</summary>
    NotGated,
    Sent,
    Redacted,
    Refused,
}

/// <param name="Labels">How many distinct values of each label the request held.</param>
public sealed record EgressDecision(EgressOutcome Outcome, EgressAction? Action, string Reason, ImmutableSortedDictionary<string, int> Labels)
{
    public bool Refused => Outcome == EgressOutcome.Refused;
}

public sealed record DetectedValue(string Label, string Value);

/// <param name="Failure">Why the scan could not run (no extractor, or it failed); the gate then refuses.</param>
public sealed record EgressScan(ImmutableArray<DetectedValue> Values, string? Failure)
{
    public ImmutableSortedDictionary<string, int> Labels =>
        Values.GroupBy(value => value.Label, StringComparer.Ordinal).ToImmutableSortedDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
}

/// <param name="Map">The session's placeholders, when the request was redacted: the response is restored through it.</param>
public sealed record PreparedRequest(EgressDecision Decision, IReadOnlyList<ChatMessage> Messages, RedactionMap? Map);

/// <summary>A gated model would have received PII its policy keeps local, or the PII scan could not run.</summary>
public sealed class PrivacyEgressRefusedException : Exception
{
    public PrivacyEgressRefusedException(string modelId, string reason)
        : base($"Privacy egress refused the request to '{modelId}': {reason}")
    {
        ModelId = modelId;
        Reason = reason;
    }

    public PrivacyEgressRefusedException()
        : this("unknown", "refused")
    {
    }

    public PrivacyEgressRefusedException(string message)
        : this("unknown", message)
    {
    }

    public PrivacyEgressRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        ModelId = "unknown";
        Reason = message;
    }

    public string ModelId { get; }

    public string Reason { get; }
}

/// <summary>
/// The privacy egress gate of plan §10.4. Before a request reaches a gated model (every cloud model, by default), the
/// extractor scans the outgoing projection for PII labels, and the profile's policy decides: send as is, redact each
/// value to a stable per-session placeholder that the response maps back, or refuse so the work stays local. It fails
/// closed: with no extractor, or when extraction fails, a gated request is refused, never sent unscanned.
/// </summary>
/// <remarks>Thread-safe; one instance serves every session and binding of a process.</remarks>
public sealed class PrivacyEgress
{
    private readonly IExtractor? _extractor;
    private readonly ConcurrentDictionary<string, ImmutableArray<ExtractedSpan>> _chunks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RedactionMap> _sessions = new(StringComparer.Ordinal);
    private long _clock;

    public PrivacyEgress(IExtractor? extractor, PrivacyEgressOptions? options = null)
    {
        _extractor = extractor;
        Options = options ?? new PrivacyEgressOptions();
        ArgumentOutOfRangeException.ThrowIfLessThan(Options.MinChunkChars, 8);
        ArgumentOutOfRangeException.ThrowIfLessThan(Options.ChunkChars, Options.MinChunkChars);
        ArgumentOutOfRangeException.ThrowIfNegative(Options.ChunkOverlap);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(Options.ChunkOverlap, Options.ChunkChars / 2);
    }

    public PrivacyEgressOptions Options { get; }

    /// <summary>Whether an extractor is bound. Without one every gated request is refused.</summary>
    public bool HasExtractor => _extractor is not null;

    public EgressPolicy? PolicyFor(ModelProfile profile) => Options.PolicyFor(profile)?.Validate();

    /// <summary>The session's placeholders; a request without a session gets a map of its own.</summary>
    public RedactionMap MapFor(string? sessionId)
    {
        if (sessionId is null)
            return new RedactionMap();
        var map = _sessions.GetOrAdd(sessionId, _ => new RedactionMap());
        map.LastUsed = Interlocked.Increment(ref _clock);
        if (_sessions.Count > Options.MaxSessions)
        {
            var oldest = _sessions.Where(pair => pair.Key != sessionId).MinBy(pair => pair.Value.LastUsed);
            if (oldest.Key is not null)
                _sessions.TryRemove(oldest.Key, out _);
        }
        return map;
    }

    /// <summary>Runs the extractor over every text, in chunks it accepts. Never throws for an extractor failure: it reports it.</summary>
    public async Task<EgressScan> ScanAsync(IEnumerable<string> texts, EgressPolicy policy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(policy);
        if (_extractor is null)
            return new([], "no PII extractor is bound (the extractor slot), so nothing can be checked before it leaves the machine");
        var labels = policy.AllLabels;
        var values = new Dictionary<string, DetectedValue>(StringComparer.Ordinal);
        foreach (var text in texts.Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.Ordinal))
        {
            foreach (var chunk in Chunks(text))
            {
                ImmutableArray<ExtractedSpan> spans;
                try
                {
                    spans = await ExtractAsync(chunk, labels, policy.Threshold, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    return new([.. values.Values], "the PII extractor failed: " + exception.Message);
                }
                foreach (var span in spans)
                {
                    var value = span.Text.Trim();
                    if (span.Confidence < policy.Threshold || value.Length < Options.MinValueLength || !value.Any(char.IsLetterOrDigit))
                        continue;
                    values.TryAdd(value, new DetectedValue(span.Label, value));
                }
            }
        }
        return new([.. values.Values], null);
    }

    /// <summary>
    /// What the gate would do with these texts for <paramref name="target"/>, without preparing a request: the check before a
    /// switch to a gated model (C-006's privacy hook), so a move that every request would refuse is not made.
    /// </summary>
    public async Task<EgressDecision> AssessAsync(ModelProfile target, IEnumerable<string> texts, bool tainted,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        var policy = PolicyFor(target);
        if (policy is null)
            return new(EgressOutcome.NotGated, null, "not gated", ImmutableSortedDictionary<string, int>.Empty);
        var action = policy.EffectiveAction(tainted);
        var scan = await ScanAsync(texts, policy, cancellationToken).ConfigureAwait(false);
        return Decide(action, scan);
    }

    /// <summary>Applies the target's policy to a request: the messages to send, or a refusal.</summary>
    public async Task<PreparedRequest> PrepareAsync(ModelProfile target, IReadOnlyList<ChatMessage> messages, ModelRequestContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(messages);
        var policy = PolicyFor(target);
        if (policy is null)
            return new(new(EgressOutcome.NotGated, null, "not gated", ImmutableSortedDictionary<string, int>.Empty), messages, null);
        var action = policy.EffectiveAction(context.Tainted);

        // Images, audio and files cannot be read by the extractor, so only a plain send may carry them.
        var opaque = messages.SelectMany(message => message.Contents).FirstOrDefault(content => !IsReadable(content));
        if (opaque is not null && action != EgressAction.Send)
            return new(new(EgressOutcome.Refused, action, $"the request holds {opaque.GetType().Name}, which the PII extractor cannot read",
                ImmutableSortedDictionary<string, int>.Empty), messages, null);

        var scan = await ScanAsync(OutgoingTexts(messages), policy, cancellationToken).ConfigureAwait(false);
        var decision = Decide(action, scan);
        if (decision.Outcome != EgressOutcome.Redacted)
            return new(decision, messages, null);

        var map = MapFor(context.SessionId);
        foreach (var value in scan.Values)
            map.PlaceholderFor(value.Label, value.Value);
        return new(decision, [.. messages.Select(message => Transform(message, map.Redact))], map);
    }

    /// <summary>Maps the session's placeholders in a response back to their values, in text and in tool-call arguments.</summary>
    public static ChatResponse Restore(ChatResponse response, RedactionMap map)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(map);
        foreach (var message in response.Messages)
        {
            for (var i = 0; i < message.Contents.Count; i++)
            {
                message.Contents[i] = message.Contents[i] switch
                {
                    TextContent text => Copy(text, new TextContent(map.Restore(text.Text ?? ""))),
                    TextReasoningContent reasoning => Copy(reasoning, new TextReasoningContent(map.Restore(reasoning.Text ?? ""))),
                    FunctionCallContent call => Copy(call, new FunctionCallContent(call.CallId, call.Name, Arguments(call.Arguments, map.Restore))),
                    var other => other,
                };
            }
        }
        return response;
    }

    private static EgressDecision Decide(EgressAction action, EgressScan scan)
    {
        var labels = scan.Labels;
        if (scan.Failure is { } failure)
            return new(EgressOutcome.Refused, action, failure, labels);
        if (action == EgressAction.Refuse && !scan.Values.IsEmpty)
            return new(EgressOutcome.Refused, action,
                $"it holds {string.Join(", ", labels.Keys)}, which this model's privacy policy keeps on the machine", labels);
        return action == EgressAction.Redact
            ? new(EgressOutcome.Redacted, action, labels.IsEmpty ? "no PII found" : $"redacted {string.Join(", ", labels.Keys)}", labels)
            : new(EgressOutcome.Sent, action, labels.IsEmpty ? "no PII found" : $"sent {string.Join(", ", labels.Keys)} as the policy allows", labels);
    }

    private async Task<ImmutableArray<ExtractedSpan>> ExtractAsync(string chunk, ImmutableArray<string> labels, float threshold,
        CancellationToken cancellationToken)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{threshold:R}\u0001{string.Join('\u0002', labels)}\u0001") +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(chunk)));
        if (_chunks.TryGetValue(key, out var cached))
            return cached;
        ImmutableArray<ExtractedSpan> spans;
        try
        {
            spans = (await _extractor!.ExtractAsync(new ExtractionRequest(chunk, labels, threshold), cancellationToken).ConfigureAwait(false)).Spans;
        }
        catch (ArgumentException) when (chunk.Length / 2 >= Options.MinChunkChars)
        {
            // Over the model's input limit (dense text such as CJK has more tokens per character): halve it, with overlap.
            var builder = ImmutableArray.CreateBuilder<ExtractedSpan>();
            var half = chunk.Length / 2;
            var overlap = Math.Min(Options.ChunkOverlap, half / 2);
            builder.AddRange(await ExtractAsync(chunk[..(half + overlap)], labels, threshold, cancellationToken).ConfigureAwait(false));
            builder.AddRange(await ExtractAsync(chunk[(half - overlap)..], labels, threshold, cancellationToken).ConfigureAwait(false));
            spans = builder.ToImmutable();
        }
        if (_chunks.Count >= Options.MaxCachedChunks)
            _chunks.Clear();
        _chunks[key] = spans;
        return spans;
    }

    /// <summary>Splits text into overlapping chunks, preferring to cut at whitespace so a value is less often split.</summary>
    internal IEnumerable<string> Chunks(string text)
    {
        var size = Options.ChunkChars;
        if (text.Length <= size)
        {
            yield return text;
            yield break;
        }
        for (var start = 0; start < text.Length;)
        {
            var end = Math.Min(text.Length, start + size);
            if (end < text.Length)
            {
                var cut = text.LastIndexOfAny([' ', '\n', '\t'], end - 1, Math.Max(1, size / 4));
                if (cut > start)
                    end = cut + 1;
                // Never split a surrogate pair.
                if (char.IsHighSurrogate(text[end - 1]))
                    end--;
            }
            yield return text[start..end];
            if (end >= text.Length)
                yield break;
            var next = Math.Max(start + 1, end - Options.ChunkOverlap);
            if (char.IsLowSurrogate(text[next]))
                next--;
            start = Math.Max(start + 1, next);
        }
    }

    private static bool IsReadable(AIContent content) =>
        content is TextContent or TextReasoningContent or FunctionCallContent or FunctionResultContent or UsageContent;

    /// <summary>Every string the request carries: text, tool-call arguments and tool results, at their leaves.</summary>
    public static IEnumerable<string> OutgoingTexts(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case TextContent text when text.Text is { Length: > 0 }:
                        yield return text.Text;
                        break;
                    case TextReasoningContent reasoning when reasoning.Text is { Length: > 0 }:
                        yield return reasoning.Text;
                        break;
                    case FunctionCallContent call when call.Arguments is not null:
                        foreach (var value in call.Arguments.Values)
                        foreach (var leaf in Leaves(value))
                            yield return leaf;
                        break;
                    case FunctionResultContent result:
                        foreach (var leaf in Leaves(result.Result))
                            yield return leaf;
                        break;
                }
            }
        }
    }

    private static IEnumerable<string> Leaves(object? value)
    {
        switch (value)
        {
            case null or bool:
                yield break;
            case string text:
                yield return text;
                break;
            case JsonElement json:
                foreach (var leaf in Leaves(json))
                    yield return leaf;
                break;
            case IFormattable formattable:
                yield return formattable.ToString(null, CultureInfo.InvariantCulture);
                break;
            default:
                yield return value.ToString() ?? "";
                break;
        }
    }

    private static IEnumerable<string> Leaves(JsonElement json)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.String:
                yield return json.GetString()!;
                break;
            case JsonValueKind.Number:
                yield return json.GetRawText();
                break;
            case JsonValueKind.Array:
                foreach (var item in json.EnumerateArray())
                foreach (var leaf in Leaves(item))
                    yield return leaf;
                break;
            case JsonValueKind.Object:
                foreach (var property in json.EnumerateObject())
                foreach (var leaf in Leaves(property.Value))
                    yield return leaf;
                break;
        }
    }

    private static ChatMessage Transform(ChatMessage message, Func<string, string> rewrite)
    {
        var contents = message.Contents.Select(content => content switch
        {
            TextContent text => Copy(text, new TextContent(rewrite(text.Text ?? ""))),
            TextReasoningContent reasoning => Copy(reasoning, new TextReasoningContent(rewrite(reasoning.Text ?? ""))),
            FunctionCallContent call => Copy(call, new FunctionCallContent(call.CallId, call.Name, Arguments(call.Arguments, rewrite))),
            FunctionResultContent result => Copy(result, new FunctionResultContent(result.CallId, Value(result.Result, rewrite))),
            var other => other,
        }).ToList();
        // RawRepresentation is left behind on purpose: an adapter may prefer it over the contents, and it is unredacted.
        return new ChatMessage(message.Role, contents)
        {
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            AdditionalProperties = message.AdditionalProperties is { Count: > 0 } properties ? new(properties) : null,
        };
    }

    // Annotations such as a cache breakpoint travel with the content; RawRepresentation does not (see Transform).
    private static T Copy<T>(AIContent from, T to) where T : AIContent
    {
        if (from.AdditionalProperties is { Count: > 0 } properties)
            to.AdditionalProperties = new AdditionalPropertiesDictionary(properties);
        return to;
    }

    private static Dictionary<string, object?>? Arguments(IDictionary<string, object?>? arguments, Func<string, string> rewrite) =>
        arguments?.ToDictionary(pair => pair.Key, pair => Value(pair.Value, rewrite), StringComparer.Ordinal);

    private static object? Value(object? value, Func<string, string> rewrite)
    {
        switch (value)
        {
            case null or bool:
                return value;
            case string text:
                return rewrite(text);
            case JsonElement json:
                var node = Rewrite(json, rewrite);
                using (var document = JsonDocument.Parse(node?.ToJsonString() ?? "null"))
                    return document.RootElement.Clone();
            case IFormattable formattable:
                var raw = formattable.ToString(null, CultureInfo.InvariantCulture);
                var changed = rewrite(raw);
                return changed == raw ? value : changed;
            default:
                var shown = value.ToString() ?? "";
                var rewritten = rewrite(shown);
                return rewritten == shown ? value : rewritten;
        }
    }

    // A number that is PII (an account number) becomes its placeholder string; everything else keeps its JSON type.
    private static JsonNode? Rewrite(JsonElement json, Func<string, string> rewrite)
    {
        switch (json.ValueKind)
        {
            case JsonValueKind.String:
                return JsonValue.Create(rewrite(json.GetString()!));
            case JsonValueKind.Number:
                var raw = json.GetRawText();
                var changed = rewrite(raw);
                return changed == raw ? JsonNode.Parse(raw) : JsonValue.Create(changed);
            case JsonValueKind.Array:
                var array = new JsonArray();
                foreach (var item in json.EnumerateArray())
                    array.Add(Rewrite(item, rewrite));
                return array;
            case JsonValueKind.Object:
                var obj = new JsonObject();
                foreach (var property in json.EnumerateObject())
                    obj[property.Name] = Rewrite(property.Value, rewrite);
                return obj;
            case JsonValueKind.True:
                return JsonValue.Create(true);
            case JsonValueKind.False:
                return JsonValue.Create(false);
            default:
                return null;
        }
    }
}
