using Corvid.Kernel.Turns;

namespace Corvid.Host.Composition;

/// <summary>
/// A5: the turn loop's question asker for every session, beside <see cref="ApprovalRouter"/>: it hands an <c>ask_user</c>
/// question to the surface attached to that session (<see cref="CorvidRuntime.AttachQuestioner"/>), which shows it and
/// returns the person's answer while the turn waits. With none attached the turn ends on the question (K-6), and the person's
/// next message answers it: a gateway chat, ACP and scheduled runs keep that path. A child session asks nothing
/// (<see cref="TurnOptions.AsksQuestions"/>), so a child's question never reaches its parent's surface.
/// </summary>
/// <remarks>
/// A5 review: several surfaces can attach to one session (the window and <c>corvid chat</c> in one process, a surface opened
/// again before the old one let go). The latest attached asks; detaching takes away only that surface's own attachment, so
/// the one attached before it asks again rather than nobody.
/// </remarks>
internal sealed class QuestionRouter : IQuestionAsker
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<Attachment>> _surfaces = new(StringComparer.Ordinal);

    public IDisposable Attach(string sessionId, IQuestionAsker surface)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(surface);
        var attachment = new Attachment(this, sessionId, surface);
        lock (_gate)
        {
            if (!_surfaces.TryGetValue(sessionId, out var attached))
                _surfaces[sessionId] = attached = [];
            attached.Add(attachment);
        }
        return attachment;
    }

    public bool CanAsk(string sessionId) => Top(sessionId) is { } surface && surface.CanAsk(sessionId);

    public ValueTask<string?> AskAsync(QuestionPrompt prompt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        // Detached since CanAsk: nobody to ask, so the turn ends on the question.
        return Top(prompt.SessionId) is { } surface
            ? surface.AskAsync(prompt, cancellationToken)
            : ValueTask.FromResult<string?>(null);
    }

    public void Expect(QuestionPrompt prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        Top(prompt.SessionId)?.Expect(prompt);
    }

    // The latest surface attached to the session and still attached.
    private IQuestionAsker? Top(string sessionId)
    {
        lock (_gate)
            return _surfaces.TryGetValue(sessionId, out var attached) && attached.Count > 0 ? attached[^1].Surface : null;
    }

    private void Remove(Attachment attachment)
    {
        lock (_gate)
        {
            if (!_surfaces.TryGetValue(attachment.SessionId, out var attached))
                return;
            attached.Remove(attachment);
            if (attached.Count == 0)
                _surfaces.Remove(attachment.SessionId);
        }
    }

    // One attachment per Attach call, so a surface attached twice detaches each one on its own.
    private sealed class Attachment(QuestionRouter router, string sessionId, IQuestionAsker surface) : IDisposable
    {
        public string SessionId { get; } = sessionId;

        public IQuestionAsker Surface { get; } = surface;

        public void Dispose() => router.Remove(this);
    }
}
