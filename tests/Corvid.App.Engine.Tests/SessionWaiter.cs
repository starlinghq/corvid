using Corvid.App.Core.Sessions;

namespace Corvid.App.Engine.Tests;

/// <summary>
/// A subscriber that can wait for an update: a turn that waits on an approval runs on until the test answers, so the test
/// waits for the card to arrive rather than for time to pass.
/// </summary>
internal sealed class SessionWaiter
{
    private readonly List<SessionUpdate> _updates = [];
    private readonly List<(Func<SessionUpdate, bool> Match, TaskCompletionSource<SessionUpdate> Done)> _waiting = [];

    public void Add(SessionUpdate update)
    {
        List<TaskCompletionSource<SessionUpdate>> done = [];
        lock (_updates)
        {
            _updates.Add(update);
            foreach (var waiter in _waiting.Where(waiter => waiter.Match(update)).ToList())
            {
                _waiting.Remove(waiter);
                done.Add(waiter.Done);
            }
        }
        foreach (var source in done)
            source.TrySetResult(update);
    }

    public IReadOnlyList<SessionUpdate> Updates
    {
        get
        {
            lock (_updates)
                return [.. _updates];
        }
    }

    /// <summary>The first update so far, or the next to come, of type <typeparamref name="T"/> that matches.</summary>
    public async Task<T> WaitAsync<T>(Func<T, bool>? match = null) where T : SessionUpdate
    {
        bool Matches(SessionUpdate update) => update is T typed && (match is null || match(typed));
        var source = new TaskCompletionSource<SessionUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_updates)
        {
            if (_updates.FirstOrDefault(Matches) is { } seen)
                return (T)seen;
            _waiting.Add((Matches, source));
        }
        // A guard, not a pace: a broken turn fails the test instead of hanging it.
        return (T)await source.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
    }
}
