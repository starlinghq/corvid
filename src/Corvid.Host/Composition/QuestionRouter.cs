using System.Collections.Concurrent;
using Corvid.Kernel.Turns;

namespace Corvid.Host.Composition;

/// <summary>
/// A5: the turn loop's question asker for every session, beside <see cref="ApprovalRouter"/>: it hands an <c>ask_user</c>
/// question to the surface attached to that session (<see cref="CorvidRuntime.AttachQuestioner"/>), which shows it and
/// returns the person's answer while the turn waits. With none attached the turn ends on the question (K-6), and the person's
/// next message answers it: a gateway chat, ACP and scheduled runs keep that path. A child session asks nothing
/// (<see cref="TurnOptions.AsksQuestions"/>), so a child's question never reaches its parent's surface.
/// </summary>
internal sealed class QuestionRouter : IQuestionAsker
{
    private readonly ConcurrentDictionary<string, IQuestionAsker> _surfaces = new(StringComparer.Ordinal);

    public IDisposable Attach(string sessionId, IQuestionAsker surface)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(surface);
        _surfaces[sessionId] = surface;
        return new Detach(this, sessionId, surface);
    }

    public bool CanAsk(string sessionId) => _surfaces.TryGetValue(sessionId, out var surface) && surface.CanAsk(sessionId);

    public ValueTask<string?> AskAsync(QuestionPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        // Detached since CanAsk: nobody to ask, so the turn ends on the question.
        return _surfaces.TryGetValue(prompt.SessionId, out var surface)
            ? surface.AskAsync(prompt, cancellationToken)
            : ValueTask.FromResult<string?>(null);
    }

    public void Expect(QuestionPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (_surfaces.TryGetValue(prompt.SessionId, out var surface))
            surface.Expect(prompt);
    }

    private sealed class Detach(QuestionRouter router, string sessionId, IQuestionAsker surface) : IDisposable
    {
        public void Dispose() => router._surfaces.TryRemove(KeyValuePair.Create(sessionId, surface));
    }
}
