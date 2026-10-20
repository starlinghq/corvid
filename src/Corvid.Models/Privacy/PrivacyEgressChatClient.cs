using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Privacy;

/// <summary>
/// Wraps a gated model's client (every cloud binding): each request passes <see cref="PrivacyEgress"/> first. A refusal
/// throws <see cref="PrivacyEgressRefusedException"/> before anything is sent; a redacted request's response is mapped back
/// before the caller sees it. Corvid's request keys are stripped here, so they never reach a provider.
/// </summary>
/// <remarks>
/// The host wraps the binding itself, not the slot's client, so every path to a cloud model passes the gate: the turn's
/// generator, a fallback hop, and the degraded decider's questions alike. A redacted request is not streamed: a placeholder
/// can be split across deltas, and Vault's proxy returns whole bodies anyway, so the response is restored whole and then
/// replayed as updates.
/// </remarks>
public sealed class PrivacyEgressChatClient : DelegatingChatClient
{
    public static readonly ActivitySource Source = new("Corvid.Models");

    private readonly ModelProfile _profile;
    private readonly PrivacyEgress _egress;

    public PrivacyEgressChatClient(IChatClient inner, ModelProfile profile, PrivacyEgress egress)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(egress);
        _profile = profile;
        _egress = egress;
    }

    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (stripped, context) = Context(options);
        var prepared = await PrepareAsync(messages, context, cancellationToken).ConfigureAwait(false);
        using var scope = context.Enter();
        var response = await base.GetResponseAsync(prepared.Messages, stripped, cancellationToken).ConfigureAwait(false);
        return prepared.Map is { } map ? PrivacyEgress.Restore(response, map) : response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (stripped, context) = Context(options);
        var prepared = await PrepareAsync(messages, context, cancellationToken).ConfigureAwait(false);
        if (prepared.Map is { } map)
        {
            ChatResponse response;
            using (context.Enter())
                response = await base.GetResponseAsync(prepared.Messages, stripped, cancellationToken).ConfigureAwait(false);
            foreach (var update in PrivacyEgress.Restore(response, map).ToChatResponseUpdates())
                yield return update;
            yield break;
        }

        // The ambient context is set while each step runs, so an HTTP handler below the provider SDK sees it.
        var updates = base.GetStreamingResponseAsync(prepared.Messages, stripped, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool more;
                using (context.Enter())
                    more = await updates.MoveNextAsync().ConfigureAwait(false);
                if (!more)
                    yield break;
                yield return updates.Current;
            }
        }
        finally
        {
            await updates.DisposeAsync().ConfigureAwait(false);
        }
    }

    // Keys first, then the ambient context of an outer layer that already stripped them.
    private static (ChatOptions? Options, ModelRequestContext Context) Context(ChatOptions? options)
    {
        var (stripped, context) = ModelRequestContext.Strip(options);
        return ReferenceEquals(stripped, options) && ModelRequestContext.Current is { } ambient ? (stripped, ambient) : (stripped, context);
    }

    private async Task<PreparedRequest> PrepareAsync(IEnumerable<ChatMessage> messages, ModelRequestContext context, CancellationToken cancellationToken)
    {
        var list = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        using var activity = Source.StartActivity("corvid.egress", ActivityKind.Internal, parentContext: default, tags:
        [
            new("gen_ai.request.model", _profile.Id),
            new("corvid.privacy", _profile.Privacy.ToString()),
            new("corvid.tainted", context.Tainted),
        ]);
        var prepared = await _egress.PrepareAsync(_profile, list, context, cancellationToken).ConfigureAwait(false);
        // Label counts only: a value never goes into a span.
        activity?.SetTag("corvid.egress.outcome", prepared.Decision.Outcome.ToString().ToLowerInvariant());
        activity?.SetTag("corvid.egress.labels", prepared.Decision.Labels.Select(pair => $"{pair.Key}={pair.Value}").ToArray());
        if (prepared.Decision.Refused)
        {
            activity?.SetStatus(ActivityStatusCode.Error, prepared.Decision.Reason);
            throw new PrivacyEgressRefusedException(_profile.Id, prepared.Decision.Reason);
        }
        return prepared;
    }
}
