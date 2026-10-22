using System.Collections.Immutable;

namespace Corvid.Models.Privacy;

/// <summary>What the privacy egress gate does with PII in a request bound for a gated model (plan §10.4).</summary>
public enum EgressAction
{
    /// <summary>
    /// Send as is. The scan still runs: tainted content and a credential value (C-032) raise the action to redact, and an
    /// unavailable extractor refuses.
    /// </summary>
    Send,
    /// <summary>Replace each PII value with a stable placeholder (<c>&lt;PERSON_1&gt;</c>), mapped back in the response.</summary>
    Redact,
    /// <summary>Refuse the request when it holds PII; the slot stays local.</summary>
    Refuse,
}

/// <summary>One profile's egress policy. Profiles without one (local models by default) are not gated.</summary>
public sealed record EgressPolicy
{
    /// <summary>The labels of plan §10.4. Multi-word labels read better to GLiNER2 than codes.</summary>
    public static ImmutableArray<string> DefaultLabels { get; } =
        ["person", "email", "phone number", "address", "account number", "credential"];

    /// <summary>The default for cloud profiles.</summary>
    public static EgressPolicy Default { get; } = new();

    public EgressAction Action { get; init; } = EgressAction.Redact;

    public ImmutableArray<string> Labels { get; init; } = DefaultLabels;

    /// <summary>User-defined labels, scanned with <see cref="Labels"/> (for example "project codename", "medical condition").</summary>
    public ImmutableArray<string> ExtraLabels { get; init; } = [];

    /// <summary>Minimum span confidence. Lower is stricter: more spans count as PII.</summary>
    public float Threshold { get; init; } = 0.4f;

    /// <summary>
    /// The least the gate does when the request carries content from outside the user (plan §10.3): other people's text is
    /// never sent raw to a cloud model, whatever <see cref="Action"/> says. It only ever makes the gate stricter.
    /// </summary>
    public EgressAction Tainted { get; init; } = EgressAction.Redact;

    public ImmutableArray<string> AllLabels =>
        [.. (Labels.IsDefault ? [] : Labels).Concat(ExtraLabels.IsDefault ? [] : ExtraLabels)
            .Where(label => !string.IsNullOrWhiteSpace(label)).Select(label => label.Trim()).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The action for one request: the stricter of the policy's and, for tainted content, <see cref="Tainted"/>.</summary>
    public EgressAction EffectiveAction(bool tainted) => tainted && Tainted > Action ? Tainted : Action;

    public EgressPolicy Validate()
    {
        if (!Enum.IsDefined(Action) || !Enum.IsDefined(Tainted))
            throw new ArgumentOutOfRangeException(nameof(Action), "Unknown egress action.");
        if (Threshold is < 0 or > 1 || float.IsNaN(Threshold))
            throw new ArgumentOutOfRangeException(nameof(Threshold), "The threshold is a confidence between 0 and 1.");
        if (AllLabels.IsEmpty)
            throw new ArgumentException("An egress policy needs at least one label.", nameof(Labels));
        return this;
    }
}

/// <summary>Request metadata the turn loop passes to model clients through <c>ChatOptions.AdditionalProperties</c>.</summary>
/// <remarks>
/// Gated clients and the provider adapters strip every key with the <see cref="Prefix"/> before a request leaves, so
/// none of it reaches a provider as a request field.
/// </remarks>
public static class ModelRequestKeys
{
    public const string Prefix = "corvid.";

    /// <summary>The session the request belongs to: redaction placeholders are stable within it.</summary>
    public const string SessionId = "corvid.session_id";

    /// <summary>True when the session holds content from outside the user (plan §10.3).</summary>
    public const string Tainted = "corvid.tainted";

    /// <summary>True when the messages hold local-only memory: no cloud model may receive them (plan §10.4).</summary>
    public const string LocalOnly = "corvid.local_only";
}
