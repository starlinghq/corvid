using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Corvid.Abstractions.Extraction;
using Corvid.Secrets;
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

    /// <summary>
    /// A5: Corvid's own fixed instructions (the configured system prompt). Inside a system message their text is neither
    /// scanned nor rewritten, since it holds no PII and GLiNER2 reads its words as names ("Corvid", "status"); whatever else
    /// the system message carries (core memory blocks, recalled memory) is scanned and redacted as before. Honoured only in
    /// system messages, so text the person or a tool sends that repeats it is scanned like any other.
    /// </summary>
    public ImmutableArray<string> TrustedTexts { get; init; } = [];

    /// <summary>
    /// Finds credential values (C-032): the shared detector under its egress rules. Tests replace it to reach the timeout path.
    /// </summary>
    internal Func<string, ImmutableArray<CredentialMatch>> FindCredentials { get; init; } =
        text => CredentialDetector.Find(text, CredentialDetector.Egress);

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

/// <param name="Credential">
/// Found by the credential detector (C-032), not by the extractor: a credential value, which no policy sends. GLiNER2's own
/// <c>credential</c> spans are the noun ("API key") and leave this false.
/// </param>
public sealed record DetectedValue(string Label, string Value, bool Credential = false);

/// <param name="Failure">Why the scan could not run (no extractor, it failed, or the credential detector timed out); the gate then refuses.</param>
public sealed record EgressScan(ImmutableArray<DetectedValue> Values, string? Failure)
{
    public ImmutableSortedDictionary<string, int> Labels =>
        Values.GroupBy(value => value.Label, StringComparer.Ordinal).ToImmutableSortedDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

    /// <summary>How many distinct credential values the credential detector found.</summary>
    public int Credentials => Values.Count(value => value.Credential);

    public bool HasCredentials => Values.Any(value => value.Credential);
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
/// <remarks>
/// Beside the extractor, the credential detector (C-032) finds credential values (token shapes, labelled assignments, URL
/// passwords, prose values) with no threshold and whatever the policy's labels. A credential value is never sent: it raises
/// send to redact, and refuse stays refuse. A detector timeout refuses the request.
/// </remarks>
/// <remarks>Thread-safe; one instance serves every session and binding of a process.</remarks>
public sealed partial class PrivacyEgress
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

    /// <summary>
    /// Runs the credential detector over every whole text, then the extractor over every text in chunks it accepts. Never
    /// throws for an extractor failure or a detector timeout: it reports them.
    /// </summary>
    public Task<EgressScan> ScanAsync(IEnumerable<string> texts, EgressPolicy policy, CancellationToken cancellationToken = default) =>
        ScanAsync(texts, [], policy, cancellationToken);

    /// <param name="structured">
    /// Tool-call arguments and JSON tool results as JSON text, for the credential detector only: the leaves in
    /// <paramref name="texts"/> lose the key that labels a value (<c>{"password": "..."}</c>).
    /// </param>
    /// <param name="vocabulary">
    /// A5: the request's structural words, Corvid's fixed <see cref="StructureWords"/>: a lowercase identifier-shaped span
    /// made only of them is the request's structure, not a value ("status", "details"). Neither the request's own JSON keys
    /// (a key is data too: a result keyed by a username) nor, since A5's review, the trusted prompts' words are among them:
    /// a prompt's word can be someone's name or handle where the person or a tool writes it.
    /// </param>
    private async Task<EgressScan> ScanAsync(IEnumerable<string> texts, IEnumerable<string> structured, EgressPolicy policy,
        CancellationToken cancellationToken, IReadOnlySet<string>? vocabulary = null)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentNullException.ThrowIfNull(policy);
        if (_extractor is null)
            return new([], "no PII extractor is bound (the extractor slot), so nothing can be checked before it leaves the machine");
        var labels = policy.AllLabels;
        var distinct = texts.Where(text => !string.IsNullOrWhiteSpace(text)).Distinct(StringComparer.Ordinal).ToList();
        var values = new Dictionary<string, DetectedValue>(StringComparer.Ordinal);

        // Credential values first, over whole texts (a PEM block or a JWT on a chunk boundary is seen whole), so a GLiNER2
        // label for the same string never replaces them, and with no threshold: a credential is never sent.
        try
        {
            foreach (var text in distinct)
                AddCredentials(text, json: false, values);
            foreach (var text in structured.Distinct(StringComparer.Ordinal))
                if (!AddCredentials(text, json: true, values))
                    return new([.. values.Values], "a credential value in a tool call's JSON could not be decoded, so it could not be redacted");
        }
        catch (RegexMatchTimeoutException)
        {
            return new([.. values.Values], "the credential detector timed out, so the request could not be checked for credentials");
        }

        foreach (var text in distinct)
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
                    if (Structural(span.Label, value, vocabulary))
                        continue;
                    values.TryAdd(value, new DetectedValue(span.Label, value));
                }
            }
        }
        return new([.. values.Values], null);
    }

    /// <summary>
    /// A5: spans GLiNER2 labels that are not values: a <c>credential</c> span made only of credential nouns ("API key",
    /// "password", "wifi password"; values are the credential detector's), and a lowercase single word the request uses as
    /// structure. A5 review: any other credential span is a value, letters only or not ("sunshinerainbow" in "my wifi
    /// password is sunshinerainbow").
    /// </summary>
    internal static bool Structural(string label, string value, IReadOnlySet<string>? vocabulary)
    {
        if (string.Equals(label, CredentialLabel, StringComparison.OrdinalIgnoreCase) && CredentialNoun(value))
            return true;
        // A value of a built-in label has its shape: an email has an @, a phone or account number has digits. GLiNER2 called
        // "status" an email in the A4 pass. A user-defined label has no known shape and is taken as found.
        if (label == "email" && (value.IndexOf('@', StringComparison.Ordinal) is var at && at <= 0 || value.IndexOf('.', at + 1) < 0))
            return true;
        // A pronoun or a role is nobody in particular ("You answer one question", "User:", "the assistant").
        if (label == "person" && RoleWords.Contains(value))
            return true;
        if (label is "phone number" or "account number" && !value.Any(char.IsAsciiDigit))
            return true;
        return vocabulary is not null && IdentifierKey(value) && KnownWords(value, vocabulary);
    }

    /// <summary>
    /// A5 fix: a JSON key Corvid takes as structure (neither scanned nor rewritten): identifier-shaped (<see cref="IdentifierKey"/>)
    /// and made only of <see cref="StructureWords"/> ("status", "exit_code", "toolCallId"). Any other key is read as a value
    /// would be, so a result keyed by a username (<c>{"jsmith": …}</c>) is redacted like the username.
    /// </summary>
    internal static bool StructureKey(string key) => IdentifierKey(key) && KnownWords(key, StructureWords);

    // Every part of an identifier (split at _, - and a capital) is one of the words.
    private static bool KnownWords(string identifier, IReadOnlySet<string> words)
    {
        if (words.Contains(identifier))
            return true;
        var start = 0;
        for (var i = 1; i <= identifier.Length; i++)
        {
            if (i < identifier.Length && !char.IsAsciiLetterUpper(identifier[i]) && identifier[i] is not ('_' or '-'))
                continue;
            if (i == start || !words.Contains(identifier[start..i].ToLowerInvariant()))
                return false;
            start = i < identifier.Length && identifier[i] is '_' or '-' ? i + 1 : i;
        }
        return true;
    }

    /// <summary>
    /// The words of JSON structure and tool plumbing Corvid knows, lowercase: field names tools and APIs use. A fixed list, so
    /// nothing in a request can make a value read as structure.
    /// </summary>
    internal static readonly FrozenSet<string> StructureWords = new[]
    {
        "id", "ids", "uid", "uuid", "key", "keys", "name", "names", "type", "kind", "value", "values", "status", "state", "code",
        "exit", "error", "errors", "message", "messages", "detail", "details", "result", "results", "data", "item", "items",
        "list", "count", "total", "size", "length", "offset", "limit", "page", "next", "prev", "previous", "cursor", "more",
        "text", "content", "body", "title", "summary", "description", "label", "labels", "tag", "tags", "note", "notes",
        "owner", "author", "user", "group", "role", "roles", "created", "updated", "modified", "deleted", "at", "on", "by",
        "time", "timestamp", "date", "start", "end", "started", "ended", "duration", "elapsed", "ms", "seconds",
        "path", "paths", "file", "files", "dir", "directory", "line", "lines", "column", "url", "uri", "link", "links", "href",
        "host", "port", "method", "query", "params", "args", "arguments", "options", "config", "settings", "env",
        "input", "output", "stdout", "stderr", "command", "cmd", "tool", "tools", "call", "calls", "function", "response",
        "request", "headers", "header", "format", "mode", "level", "version", "schema", "properties", "required", "default",
        "enum", "min", "max", "minimum", "maximum", "pattern", "number", "string", "boolean", "object", "array", "null",
        "ok", "success", "failed", "enabled", "disabled", "is", "has", "can", "visible", "hidden", "public", "private",
        "parent", "child", "children", "source", "target", "from", "to", "old", "new", "before", "after", "diff", "changes",
        "match", "matches", "score", "rank", "index", "position", "row", "rows", "cell", "field", "fields", "entry", "entries",
        "branch", "commit", "repo", "ref", "sha", "hash", "language", "lang", "encoding", "mime", "extension", "ext",
        "priority", "progress", "step", "steps", "budget", "tokens", "usage", "model", "models", "session", "turn", "reason",
        "category", "categories", "action", "actions", "event", "events", "warning", "warnings", "info", "debug",
        "width", "height", "x", "y", "lat", "lon", "unit", "units", "amount", "currency", "price", "quantity",
        "email", "emails", "phone", "address", "username", "login", "account", "first", "last", "full", "display", "city",
        "country", "zip", "company", "org", "team", "members", "member",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// A5 review: whether a span names a kind of credential rather than holding one: every word (split at spaces, hyphens and
    /// underscores, any case) is one of <see cref="CredentialWords"/>, and one of them is a head noun ("key", "password",
    /// "token"…), so "API key" and "wifi password" are nouns and "sunshinerainbow" or "hunter2" are values.
    /// </summary>
    internal static bool CredentialNoun(string value)
    {
        var words = value.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        // A word that is as likely the value as the noun ("my password is secret", "the pin is admin") is a value on its own.
        if (words.Length == 1 && AmbiguousAlone.Contains(words[0]))
            return false;
        return words.Length > 0 && words.All(CredentialWords.Contains) && words.Any(CredentialHeads.Contains);
    }

    private static readonly FrozenSet<string> AmbiguousAlone = new[]
    {
        "secret", "secrets", "key", "keys", "pin", "pins", "handle", "login", "master", "root", "admin", "private", "public",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> CredentialHeads = new[]
    {
        "key", "keys", "password", "passwords", "passphrase", "passphrases", "passcode", "passcodes", "pin", "pins", "token",
        "tokens", "secret", "secrets", "credential", "credentials", "apikey", "apikeys", "pwd", "passwd",
        // Names of a login rather than secrets, which GLiNER2 also calls credentials ("his handle is SmithJ" in the A5 review).
        "handle", "handles", "username", "usernames", "login", "logins",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // The head nouns and the words that qualify them ("API key", "wifi password", "SSH private key", "client secret").
    private static readonly FrozenSet<string> CredentialWords = CredentialHeads.Concat(
    [
        "api", "access", "auth", "authentication", "bearer", "client", "private", "public", "secret", "ssh", "gpg", "pgp", "wifi",
        "wi", "fi", "login", "account", "admin", "root", "user", "app", "application", "refresh", "session", "personal", "master",
        "recovery", "backup", "encryption", "signing", "deploy", "github", "aws", "my", "the", "your", "a", "an", "new", "old",
    ]).ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> RoleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "I", "me", "you", "he", "him", "she", "her", "we", "us", "they", "them", "it", "someone", "everyone", "user", "users",
        "assistant", "system", "human", "agent", "model", "person", "people",
    };

    /// <summary>The label credential values are redacted under: <c>&lt;CREDENTIAL_1&gt;</c>.</summary>
    public const string CredentialLabel = "credential";

    // Shorter values would rewrite unrelated text everywhere in the session (RedactionMap replaces substrings).
    internal const int MinCredentialLength = 4;

    // False when a value found in JSON text cannot be decoded: it would not match the leaf it came from, so it could not be
    // redacted, and the scan refuses rather than report it redacted.
    private bool AddCredentials(string text, bool json, Dictionary<string, DetectedValue> values)
    {
        foreach (var match in Options.FindCredentials(text))
        {
            var value = match.ValueIn(text);
            if (json && value.Contains('\\', StringComparison.Ordinal))
            {
                if (!TryJsonUnescape(value, out value))
                    return false;
                // An escaped placeholder (<CREDENTIAL_1> in a re-serialized tool result) is a placeholder once decoded.
                if (CredentialDetector.IsExempt(value))
                    continue;
            }
            value = value.Trim();
            if (value.Length >= MinCredentialLength)
                values[value] = new DetectedValue(CredentialLabel, value, Credential: true);
        }
        return true;
    }

    // A value found in JSON text, as the request's leaves hold it: escapes decoded, so the redaction finds it.
    private static bool TryJsonUnescape(string value, out string decoded)
    {
        try
        {
            using var document = JsonDocument.Parse("\"" + value + "\"");
            decoded = document.RootElement.GetString() ?? value;
            return true;
        }
        catch (JsonException)
        {
            decoded = value;
            return false;
        }
    }

    /// <summary>
    /// Why the redacted request still carries a credential, or null. A found credential value still in a leaf, or a credential
    /// the detector finds again in the leaves or the JSON of tool calls (a value cut short, or one a PII placeholder split),
    /// means the redaction missed part of it, and the request is refused rather than reported redacted.
    /// </summary>
    private string? CredentialLeft(EgressScan scan, IReadOnlyList<ChatMessage> redacted)
    {
        var leaves = OutgoingTexts(redacted).ToList();
        foreach (var value in scan.Values.Where(value => value.Credential))
            if (leaves.Any(leaf => leaf.Contains(value.Value, StringComparison.Ordinal)))
                return "a credential value is still in the request after redaction";
        var left = new Dictionary<string, DetectedValue>(StringComparer.Ordinal);
        try
        {
            foreach (var leaf in leaves.Distinct(StringComparer.Ordinal))
                AddCredentials(leaf, json: false, left);
            foreach (var text in StructuredTexts(redacted).Distinct(StringComparer.Ordinal))
                if (!AddCredentials(text, json: true, left))
                    return "a credential value in a tool call's JSON could not be decoded after redaction";
        }
        catch (RegexMatchTimeoutException)
        {
            return "the credential detector timed out on the redacted request";
        }
        return left.Count == 0 ? null : "part of a credential value is still in the request after redaction";
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
        // A request rendered for a local model (local-only core blocks or facts in it) that a fallback handed on: local-only
        // memory is not PII the scan would find, so it is refused outright rather than scanned.
        if (context.LocalOnly && target.Privacy == PrivacyClass.Cloud)
            return new(new(EgressOutcome.Refused, null, "the request holds local-only memory, which never leaves the machine",
                ImmutableSortedDictionary<string, int>.Empty), messages, null);
        var policy = PolicyFor(target);
        if (policy is null)
            return new(new(EgressOutcome.NotGated, null, "not gated", ImmutableSortedDictionary<string, int>.Empty), messages, null);
        var action = policy.EffectiveAction(context.Tainted);

        // Images, audio and files cannot be read by the extractor, so only a plain send may carry them.
        var opaque = messages.SelectMany(message => message.Contents).FirstOrDefault(content => !IsReadable(content));
        if (opaque is not null && action != EgressAction.Send)
            return new(new(EgressOutcome.Refused, action, $"the request holds {opaque.GetType().Name}, which the PII extractor cannot read",
                ImmutableSortedDictionary<string, int>.Empty), messages, null);

        var scan = await ScanAsync(ScannedTexts(messages), StructuredTexts(messages), policy, cancellationToken, StructureWords)
            .ConfigureAwait(false);
        var decision = Decide(action, scan);
        if (decision.Outcome != EgressOutcome.Redacted)
            return new(decision, messages, null);
        // A credential raised a plain send to redact, and redaction cannot reach what the extractor cannot read.
        if (opaque is not null)
            return new(new(EgressOutcome.Refused, decision.Action,
                $"the request holds {Count(scan.Credentials, "credential value")} and {opaque.GetType().Name}, which the PII extractor cannot read",
                decision.Labels), messages, null);

        var map = MapFor(context.SessionId);
        foreach (var value in scan.Values)
            map.PlaceholderFor(value.Label, value.Value);
        List<ChatMessage> redacted = [.. messages.Select(message => Transform(message, map.Redact, message.Role == ChatRole.System ? Options.TrustedTexts : []))];
        // The credential floor is checked on what would be sent, not assumed from the scan.
        if (CredentialLeft(scan, redacted) is { } left)
            return new(new(EgressOutcome.Refused, decision.Action, left, decision.Labels), messages, null);
        return new(decision, redacted, map);
    }

    /// <summary>
    /// Maps the session's placeholders in a response back to their values, in text and in tool-call arguments. A credential
    /// placeholder (<c>&lt;CREDENTIAL_1&gt;</c>) is restored in text but never into tool-call arguments (C-040): a call would
    /// carry the value off the machine on the cloud model's say-so. The tool receives the placeholder; secrets go through Vault.
    /// </summary>
    public static ChatResponse Restore(ChatResponse response, RedactionMap map)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(map);
        string RestoreArgument(string text) => map.Restore(text, credentials: false);
        foreach (var message in response.Messages)
        {
            for (var i = 0; i < message.Contents.Count; i++)
            {
                message.Contents[i] = message.Contents[i] switch
                {
                    TextContent text => Copy(text, new TextContent(map.Restore(text.Text ?? ""))),
                    TextReasoningContent reasoning => Copy(reasoning, new TextReasoningContent(map.Restore(reasoning.Text ?? ""))),
                    FunctionCallContent call => Copy(call, new FunctionCallContent(call.CallId, call.Name, Arguments(call.Arguments, RestoreArgument))),
                    var other => other,
                };
            }
        }
        return response;
    }

    // The credential floor (C-032) applies here, after the scan, as the taint floor applies before it: a credential value
    // raises a plain send to redact; redact and refuse stay as they are. Reasons name counts, never a value.
    private static EgressDecision Decide(EgressAction action, EgressScan scan)
    {
        var labels = scan.Labels;
        if (scan.Failure is { } failure)
            return new(EgressOutcome.Refused, action, failure, labels);
        var floored = scan.HasCredentials && action == EgressAction.Send;
        if (floored)
            action = EgressAction.Redact;
        var credentials = scan.HasCredentials ? $" ({Count(scan.Credentials, "credential value")})" : "";
        if (action == EgressAction.Refuse && !scan.Values.IsEmpty)
            return new(EgressOutcome.Refused, action,
                $"it holds {string.Join(", ", labels.Keys)}{credentials}, which this model's privacy policy keeps on the machine", labels);
        if (floored)
            return new(EgressOutcome.Redacted, action,
                $"redacted {string.Join(", ", labels.Keys)}{credentials}: a credential value is never sent, whatever the policy", labels);
        return action == EgressAction.Redact
            ? new(EgressOutcome.Redacted, action, labels.IsEmpty ? "no PII found" : $"redacted {string.Join(", ", labels.Keys)}{credentials}", labels)
            : new(EgressOutcome.Sent, action, labels.IsEmpty ? "no PII found" : $"sent {string.Join(", ", labels.Keys)} as the policy allows", labels);
    }

    private static string Count(int count, string noun) =>
        string.Create(CultureInfo.InvariantCulture, $"{count} {noun}{(count == 1 ? "" : "s")}");

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

    // A value the scan reads exactly as a provider adapter would serialize it. Any other object (a tool's own result type)
    // would be scanned as its ToString() but sent as JSON, so it counts as unreadable and only a plain send may carry it.
    private static bool IsReadable(AIContent content) => content switch
    {
        TextContent or TextReasoningContent or UsageContent => true,
        FunctionCallContent call => call.Arguments is null || call.Arguments.Values.All(IsReadableValue),
        FunctionResultContent result => IsReadableValue(result.Result),
        _ => false,
    };

    private static bool IsReadableValue(object? value) => value is null or bool or string or JsonElement or IFormattable;

    /// <summary>
    /// A5: what the extractor reads, as <see cref="OutgoingTexts"/> less the request's structure: a system message's trusted
    /// text (<see cref="PrivacyEgressOptions.TrustedTexts"/>) and JSON keys that are identifiers (<c>status</c>,
    /// <c>exit_code</c>). The person's words, the model's, memory and tool results' values are all read.
    /// </summary>
    internal IEnumerable<string> ScannedTexts(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (message.Role != ChatRole.System || Options.TrustedTexts.IsDefaultOrEmpty)
            {
                foreach (var text in OutgoingTexts([message]))
                    yield return text;
                continue;
            }
            foreach (var content in message.Contents)
            {
                if (content is TextContent { Text: { Length: > 0 } text })
                {
                    foreach (var (segment, trusted) in Segments(text, Options.TrustedTexts))
                        if (!trusted && !string.IsNullOrWhiteSpace(segment))
                            yield return segment;
                }
                else
                {
                    foreach (var other in OutgoingTexts([new ChatMessage(message.Role, [content])]))
                        yield return other;
                }
            }
        }
    }

    /// <summary>
    /// A JSON key shaped like structure: a lowercase identifier of letters (snake, kebab or camel case) of at most 32
    /// characters. A key with a capital first, a number, a space, a dot or <c>@</c> (a name, an email, a token) is always read
    /// as a value; one of this shape is structure only when <see cref="StructureKey"/> also knows its words.
    /// </summary>
    internal static bool IdentifierKey(string key) => key.Length <= 32 && IdentifierPattern().IsMatch(key);

    [GeneratedRegex(@"^[a-z]+(?:[_-][a-z]+)*$|^[a-z]+(?:[A-Z][a-z]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    /// <summary>Splits text into the trusted texts it holds (each occurrence) and what lies between them, in order.</summary>
    internal static IEnumerable<(string Text, bool Trusted)> Segments(string text, ImmutableArray<string> trusted)
    {
        var position = 0;
        while (position < text.Length)
        {
            var next = -1;
            var length = 0;
            foreach (var candidate in trusted)
            {
                if (string.IsNullOrEmpty(candidate))
                    continue;
                var at = text.IndexOf(candidate, position, StringComparison.Ordinal);
                if (at >= 0 && (next < 0 || at < next || at == next && candidate.Length > length))
                    (next, length) = (at, candidate.Length);
            }
            if (next < 0)
            {
                yield return (text[position..], false);
                yield break;
            }
            if (next > position)
                yield return (text[position..next], false);
            yield return (text.Substring(next, length), true);
            position = next + length;
        }
    }

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

    /// <summary>
    /// Tool-call arguments and object or array tool results as JSON text, so the credential detector sees a value with the key
    /// that labels it. The extractor never sees these: it reads the leaves.
    /// </summary>
    internal static IEnumerable<string> StructuredTexts(IEnumerable<ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent { Arguments.Count: > 0 } call:
                        yield return Json(call.Arguments);
                        break;
                    case FunctionResultContent { Result: JsonElement { ValueKind: JsonValueKind.Object or JsonValueKind.Array } json }:
                        yield return json.GetRawText();
                        break;
                }
            }
        }
    }

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Json(IDictionary<string, object?> arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            writer.WriteStartObject();
            foreach (var (key, value) in arguments)
            {
                writer.WritePropertyName(key);
                switch (value)
                {
                    case null:
                        writer.WriteNullValue();
                        break;
                    case bool flag:
                        writer.WriteBooleanValue(flag);
                        break;
                    case JsonElement json:
                        json.WriteTo(writer);
                        break;
                    case IFormattable formattable:
                        writer.WriteStringValue(formattable.ToString(null, CultureInfo.InvariantCulture));
                        break;
                    default:
                        writer.WriteStringValue(value.ToString() ?? "");
                        break;
                }
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
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
                {
                    // A key can be a value too (results keyed by email address or username); only a key of Corvid's
                    // structure words is left unread (A5).
                    if (!StructureKey(property.Name))
                        yield return property.Name;
                    foreach (var leaf in Leaves(property.Value))
                        yield return leaf;
                }
                break;
        }
    }

    private static ChatMessage Transform(ChatMessage message, Func<string, string> rewrite, ImmutableArray<string> trusted = default)
    {
        var contents = message.Contents.Select(content => content switch
        {
            // A5: a system message's trusted text is sent as written; the rest of it is redacted.
            TextContent text when !trusted.IsDefaultOrEmpty => Copy(text, new TextContent(string.Concat(
                Segments(text.Text ?? "", trusted).Select(segment => segment.Trusted ? segment.Text : rewrite(segment.Text))))),
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
                    obj[StructureKey(property.Name) ? property.Name : rewrite(property.Name)] = Rewrite(property.Value, rewrite);
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
