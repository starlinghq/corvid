using Corvid.App.Core.Composition;

namespace Corvid.App.Engine;

/// <summary>
/// Engine mode's services: each area's service over the in-process <see cref="AppEngine"/>. The class is partial with one
/// file per area (<c>EngineAppServices.&lt;Area&gt;.cs</c>), each overriding that area's factory member, so the builders of
/// different areas never edit the same file. An area whose engine service is not built yet overrides its member with one of
/// <see cref="Unavailable"/>'s services and the group that brings it (<see cref="Unavailable.ComesWith"/>); the base's
/// members are only a fallback and never show demo data.
/// <para>
/// A factory gets the UI thread as <c>app.Dispatcher</c> and the clock as <c>app.Time</c> from the composition it is
/// given, and the runtime by awaiting <see cref="AppEngine.RuntimeAsync"/> inside its service (never at construction,
/// which runs on the UI thread while the models may still be loading). The composition disposes each service it made,
/// then this, which disposes what <see cref="Track{T}"/> was given.
/// </para>
/// </summary>
public sealed partial class EngineAppServices(AppEngine engine) : UnavailableAppServices(Unavailable.ComesWith("integration", "this area")), IDisposable
{
    private readonly List<IDisposable> _owned = [];

    /// <summary>The engine every service here runs on.</summary>
    public AppEngine Engine { get; } = engine ?? throw new ArgumentNullException(nameof(engine));

    public void Dispose()
    {
        IDisposable[] owned;
        lock (_owned)
        {
            owned = [.. _owned];
            _owned.Clear();
        }
        foreach (var item in owned)
            item.Dispose();
    }

    /// <summary>Keeps something a factory made that must be disposed with the window (a subscription to a runtime event).</summary>
    private T Track<T>(T item) where T : IDisposable
    {
        lock (_owned)
            _owned.Add(item);
        return item;
    }
}
