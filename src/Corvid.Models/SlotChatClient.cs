using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
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
/// The chat client the turn loop holds for a slot. Each call resolves the slot's current binding, so a switch takes
/// effect at the next call. A provider failure falls back along the slot's chain through the logged switch procedure.
/// </summary>
internal sealed class SlotChatClient(SessionModelSlots slots, string slot, Func<Exception, bool> isFallbackError) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
        while (true)
        {
            var binding = slots.Current(slot);
            try
            {
                return await binding.GetService<IChatClient>().GetResponseAsync(history, options, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && isFallbackError(exception))
            {
                await FallBackOrThrowAsync(binding, exception, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var history = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
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
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && isFallbackError(exception))
            {
                await updates.DisposeAsync().ConfigureAwait(false);
                await FallBackOrThrowAsync(binding, exception, cancellationToken).ConfigureAwait(false);
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

    private async Task FallBackOrThrowAsync(ModelBinding failed, Exception exception, CancellationToken cancellationToken)
    {
        var result = await slots.FallBackAsync(slot, failed, cancellationToken).ConfigureAwait(false);
        if (result.Status is SwitchStatus.Switched or SwitchStatus.Unchanged)
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
