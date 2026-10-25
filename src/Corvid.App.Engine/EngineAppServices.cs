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
/// <param name="engine">The engine every service here runs on.</param>
/// <param name="home">
/// The home directory the services read beside Corvid's own paths (<c>~</c>, the Hermes home an import reads, the
/// repositories First run offers): <c>CorvidPaths.Home</c> for the window, a temporary directory for a test, so a test never
/// reads the person's own <c>~/.hermes</c>. Required for that reason.
/// </param>
/// <param name="environment">Reads an environment variable (<c>HERMES_HOME</c>); the process environment by default.</param>
public sealed partial class EngineAppServices(AppEngine engine, string home, Func<string, string?>? environment = null)
    : UnavailableAppServices(Unavailable.ComesWith("integration", "this area")), IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private volatile bool _disposed;

    /// <summary>The engine every service here runs on.</summary>
    public AppEngine Engine { get; } = engine ?? throw new ArgumentNullException(nameof(engine));

    /// <summary>The home directory the services read beside Corvid's own paths (the Hermes home, <c>~</c>).</summary>
    public string Home { get; } = !string.IsNullOrWhiteSpace(home) ? home : throw new ArgumentException("A home directory is required.", nameof(home));

    /// <summary>Reads an environment variable for the services (<c>HERMES_HOME</c>).</summary>
    public Func<string, string?> Environment { get; } = environment ?? System.Environment.GetEnvironmentVariable;

    /// <summary>The Hermes home an import reads, under <see cref="Home"/>; <paramref name="given"/> is the one the person named.</summary>
    private Corvid.Host.Configuration.HermesHomeLocation Hermes(string? given = null) =>
        Corvid.Host.Configuration.HermesHome.Resolve(Home, given is null ? null : ExpandHome(given), Environment);

    private string ExpandHome(string path) =>
        path == "~" ? Home : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(Home, path[2..]) : path;

    public void Dispose()
    {
        _disposed = true;
        IDisposable[] owned;
        lock (_owned)
        {
            owned = [.. _owned];
            _owned.Clear();
        }
        foreach (var item in owned)
            item.Dispose();
    }

    /// <summary>
    /// Shows the runtime's start on the status bar's engine line until it is up ("in-process · starting: Loading
    /// qwen3.5-0.8b"), then "in-process". Called once, as the composition makes its sessions.
    /// </summary>
    private void ShowStartup(AppComposition app)
    {
        void Show(string text) => app.Dispatcher.Post(() => app.StatusBar.Engine = text);
        void OnProgress(Corvid.Host.Composition.StartupStep step)
        {
            if (step.Elapsed is null)
                Show("in-process · starting: " + step.Description);
        }

        Engine.Progress += OnProgress;
        Track(new Unsubscribe(() => Engine.Progress -= OnProgress));
        if (Engine.CurrentStep is { } current)
            Show("in-process · starting: " + current.Description);
        _ = Engine.Runtime.ContinueWith(started =>
        {
            Engine.Progress -= OnProgress;
            // A start that failed is the hub's and First run's to explain (their notices and status); this line only follows a
            // start, and not once the window has let go of its services.
            if (started.IsCompletedSuccessfully && !_disposed)
                Show("in-process");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private sealed class Unsubscribe(Action action) : IDisposable
    {
        public void Dispose() => action();
    }

    /// <summary>Keeps something a factory made that must be disposed with the window (a subscription to a runtime event).</summary>
    private T Track<T>(T item) where T : IDisposable
    {
        lock (_owned)
            _owned.Add(item);
        return item;
    }
}
