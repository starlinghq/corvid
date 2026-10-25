using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Corvid.Models.Privacy;
using Microsoft.Extensions.AI;

namespace Corvid.Models;

public static class FallbackErrors
{
    /// <summary>Rate limits, server errors and transport failures (no status code) move to the next provider.</summary>
    /// <remarks>Client errors such as 400 or 401 do not: another provider would not fix a bad request or a bad key.</remarks>
    public static bool IsProviderFailure(Exception exception) =>
        exception is HttpRequestException { StatusCode: null or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError };
}

/// <summary>
/// A5 review: the model that answered a request through a slot's client (<see cref="SessionModelSlots.GetChatClient"/>),
/// which after a fallback, a privacy retreat or a switch made during the call is not the one the call started on.
/// </summary>
public static class ModelResponses
{
    /// <summary>The <see cref="ChatResponse.AdditionalProperties"/> key holding the answering profile's id.</summary>
    public const string ProfileKey = ModelRequestKeys.Prefix + "answered_by";

    /// <summary>The id of the profile that answered (<c>qwen3.5-27b@cuda0</c>), or null for a response that did not come through a slot.</summary>
    public static string? AnsweredBy(ChatResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return response.AdditionalProperties?.TryGetValue(ProfileKey, out var value) == true ? value as string : null;
    }
}

/// <summary>
/// The chat client the turn loop holds for a slot. Each call resolves the slot's current binding, so a switch takes
/// effect at the next call. A provider failure falls back along the slot's chain through the logged switch procedure.
/// </summary>
internal sealed class SlotChatClient(SessionModelSlots slots, string slot, Func<Exception, bool> isFallbackError) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        // One list for failures and privacy refusals: a retreat must not return to a binding that just failed, nor a
        // fallback to one the gate refused, or the two could hand the request back and forth forever.
        var tried = new List<ModelBinding>();
        while (true)
        {
            var binding = slots.Current(slot);
            try
            {
                var response = await binding.GetService<IChatClient>().GetResponseAsync(history, options, cancellationToken).ConfigureAwait(false);
                // A5 review: which binding answered, set after the inner client returns so no wrapper can drop it.
                (response.AdditionalProperties ??= [])[ModelResponses.ProfileKey] = binding.Profile.Id;
                return response;
            }
            catch (PrivacyEgressRefusedException exception) when (!cancellationToken.IsCancellationRequested)
            {
                await RetreatOrThrowAsync(binding, tried, exception, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && isFallbackError(exception))
            {
                await FallBackOrThrowAsync(binding, tried, exception, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        // One list for failures and privacy refusals: a retreat must not return to a binding that just failed, nor a
        // fallback to one the gate refused, or the two could hand the request back and forth forever.
        var tried = new List<ModelBinding>();
        while (true)
        {
            var binding = slots.Current(slot);
            var updates = binding.GetService<IChatClient>().GetStreamingResponseAsync(history, options, cancellationToken)
                .GetAsyncEnumerator(cancellationToken);
            bool any;
            try
            {
                any = await updates.MoveNextAsync().ConfigureAwait(false);
            }
            catch (PrivacyEgressRefusedException exception) when (!cancellationToken.IsCancellationRequested)
            {
                await updates.DisposeAsync().ConfigureAwait(false);
                await RetreatOrThrowAsync(binding, tried, exception, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && isFallbackError(exception))
            {
                await updates.DisposeAsync().ConfigureAwait(false);
                await FallBackOrThrowAsync(binding, tried, exception, cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch
            {
                await updates.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            // Once output has reached the caller, a failure is the caller's: replaying on another model would duplicate it.
            try
            {
                if (!any)
                    yield break;
                yield return updates.Current;
                while (await updates.MoveNextAsync().ConfigureAwait(false))
                    yield return updates.Current;
                yield break;
            }
            finally
            {
                await updates.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // Remembers every binding that failed this request, so the chain never sends it back to one of them.
    private async Task FallBackOrThrowAsync(ModelBinding binding, List<ModelBinding> failed, Exception exception,
        CancellationToken cancellationToken)
    {
        failed.Add(binding);
        var result = await slots.FallBackAsync(slot, binding, failed, cancellationToken).ConfigureAwait(false);
        // Unchanged means a concurrent call already moved the slot; retry on its binding unless that one failed here too.
        if (result.Status == SwitchStatus.Switched
            || (result.Status == SwitchStatus.Unchanged && !failed.Contains(slots.Current(slot), ReferenceEqualityComparer.Instance)))
            return;
        ExceptionDispatchInfo.Capture(exception).Throw();
    }

    // The gate refused this binding's request before anything was sent: the work stays on a local model (plan §10.4).
    private async Task RetreatOrThrowAsync(ModelBinding binding, List<ModelBinding> refused, PrivacyEgressRefusedException exception,
        CancellationToken cancellationToken)
    {
        refused.Add(binding);
        var result = await slots.RetreatAsync(slot, binding, refused, cancellationToken).ConfigureAwait(false);
        if (result.Status == SwitchStatus.Switched
            || (result.Status == SwitchStatus.Unchanged && !refused.Contains(slots.Current(slot), ReferenceEqualityComparer.Instance)))
            return;
        ExceptionDispatchInfo.Capture(exception).Throw();
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        return serviceKey is null && serviceType.IsInstanceOfType(this)
            ? this
            : slots.Current(slot).GetService<IChatClient>().GetService(serviceType, serviceKey);
    }

    // The bindings belong to the registry; this view owns nothing.
    public void Dispose()
    {
    }
}
