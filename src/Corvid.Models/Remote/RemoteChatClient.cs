using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Corvid.Models.Privacy;
using Microsoft.Extensions.AI;

namespace Corvid.Models.Remote;

/// <summary>
/// The outermost layer of a provider adapter's client: Corvid's request keys are removed before the SDK sees the options
/// (an SDK may send unknown additional properties as request fields), the request context becomes ambient for the HTTP
/// handlers under the SDK, and the SDK's failures are translated so the slot's fallback chain can act on them.
/// </summary>
/// <param name="translate">The provider SDK's own exceptions, mapped to <see cref="HttpRequestException"/> with a status; null when unknown.</param>
public sealed class RemoteChatClient(IChatClient inner, Func<Exception, Exception?> translate) : DelegatingChatClient(inner)
{
    public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var (stripped, context) = Context(options);
        using var scope = context.Enter();
        try
        {
            return await base.GetResponseAsync(messages, stripped, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw Translate(exception);
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var (stripped, context) = Context(options);
        var updates = base.GetStreamingResponseAsync(messages, stripped, cancellationToken).GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool more;
                using (context.Enter())
                {
                    try
                    {
                        more = await updates.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        throw Translate(exception);
                    }
                }
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

    private static (ChatOptions? Options, ModelRequestContext Context) Context(ChatOptions? options) =>
        ModelRequestContext.Resolve(options);

    private Exception Translate(Exception exception)
    {
        if (exception is HttpRequestException or PrivacyEgressRefusedException or NotSupportedException)
            return exception;
        var mapped = ModelHttp.Translate(exception) ?? translate(exception);
        if (mapped is null)
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
        return mapped;
    }
}
