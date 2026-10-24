using System.Collections.Immutable;
using Corvid.App.Core.Composition;
using Corvid.Host.Composition;
using Corvid.Host.Sessions;
using Corvid.Host.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Corvid.App.Engine;

/// <summary>
/// The engine the window runs on: the generic host (configuration, the model catalogue, the config writer, the doctor)
/// and the <see cref="CorvidRuntime"/> it starts in the background. <see cref="StartAsync"/> starts the host and checks the
/// model cache (offline, quick) before the window opens, so the head knows whether to open First run; loading the models
/// runs on the pool and every service awaits <see cref="RuntimeAsync"/>, so the window paints at once (K-4's first paint).
/// </summary>
public sealed class AppEngine : IAsyncDisposable
{
    private readonly IHost _host;
    private readonly bool _ownsHost;
    private readonly CancellationTokenSource _lifetime;
    private readonly Lazy<Task<ToolPresentation>> _tools;
    private StartupStep? _step;
    private int _disposed;

    private AppEngine(IHost host, bool ownsHost, Task<CorvidRuntime> runtime, ImmutableArray<ModelAvailability> missing, string? failure,
        CancellationTokenSource lifetime)
    {
        _host = host;
        _ownsHost = ownsHost;
        Runtime = runtime;
        MissingModels = missing;
        StartFailure = failure;
        _lifetime = lifetime;
        _tools = new Lazy<Task<ToolPresentation>>(async () => new ToolPresentation(await RuntimeAsync().ConfigureAwait(false)));
    }

    /// <summary>The host's services: <c>CorvidConfigWriter</c>, <c>ModelCatalogue</c>, <c>Doctor</c>, the options.</summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>The runtime as it starts; faulted when it cannot (<see cref="RuntimeAsync"/> says why in a sentence).</summary>
    public Task<CorvidRuntime> Runtime { get; }

    /// <summary>Configured models the cache lacks, found before anything loaded; the head opens First run when there are any.</summary>
    public ImmutableArray<ModelAvailability> MissingModels { get; }

    /// <summary>Why the runtime cannot start (invalid configuration, missing models), known before the window opens; null otherwise.</summary>
    public string? StartFailure { get; }

    /// <summary>The stage the start is at, or null once the runtime is up (or could not start).</summary>
    public StartupStep? CurrentStep => Volatile.Read(ref _step);

    /// <summary>Raised from a pool thread at each stage of the start, as it begins and as it ends.</summary>
    public event Action<StartupStep>? Progress;

    /// <summary>
    /// Starts the host (validating the configuration) and checks the model cache, then starts the runtime on the pool.
    /// A configuration the validator refuses, or models the cache lacks, leave the runtime unstarted with the reason in
    /// <see cref="StartFailure"/>, rather than failing here: the window still opens, on First run.
    /// </summary>
    /// <param name="ownsHost">Whether disposing the engine stops and disposes the host (the head's); tests keep their own.</param>
    /// <param name="progress">Told each stage of the runtime's start as it begins and ends, from a pool thread (as <see cref="Progress"/>).</param>
    public static async Task<AppEngine> StartAsync(IHost host, bool ownsHost = true, IProgress<StartupStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        var lifetime = new CancellationTokenSource();
        try
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OptionsValidationException exception)
        {
            var reason = "Corvid cannot start: the configuration is not valid. " + string.Join(" ", exception.Failures);
            return new AppEngine(host, ownsHost, Task.FromException<CorvidRuntime>(new ServiceUnavailableException(reason)), [], reason, lifetime);
        }

        var factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();
        var missing = factory.CheckModels().ToImmutableArray();
        if (!missing.IsEmpty)
        {
            var reason = Describe(new ModelsUnavailableException(missing));
            return new AppEngine(host, ownsHost, Task.FromException<CorvidRuntime>(new ModelsUnavailableException(missing)), missing, reason, lifetime);
        }

        var started = new TaskCompletionSource<CorvidRuntime>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new AppEngine(host, ownsHost, started.Task, [], null, lifetime);
        // Loading reads the models from disk for seconds; the window must not wait on it. Its outcome is the Runtime task,
        // which every service and DisposeAsync awaits, so a failure is always observed.
        _ = Task.Run(async () =>
        {
            try
            {
                started.TrySetResult(await factory.StartAsync(new Relay(step =>
                {
                    engine.Report(step);
                    progress?.Report(step);
                }), lifetime.Token).ConfigureAwait(false));
            }
            catch (Exception exception)
            {
                started.TrySetException(exception);
            }
            finally
            {
                Volatile.Write(ref engine._step, null);
            }
        }, CancellationToken.None);
        return engine;
    }

    /// <summary>An engine over a runtime already started (tests, and a head that starts the runtime itself).</summary>
    public static AppEngine ForRuntime(IHost host, CorvidRuntime runtime, bool ownsHost = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(runtime);
        return new AppEngine(host, ownsHost, Task.FromResult(runtime), [], null, new CancellationTokenSource());
    }

    /// <summary>The runtime once it is up.</summary>
    /// <exception cref="ServiceUnavailableException">It could not start; the message says why, for the page's notice.</exception>
    public async Task<CorvidRuntime> RuntimeAsync()
    {
        try
        {
            return await Runtime.ConfigureAwait(false);
        }
        catch (ModelsUnavailableException exception)
        {
            throw new ServiceUnavailableException(Describe(exception));
        }
        catch (ServiceUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            throw new ServiceUnavailableException("Corvid could not start: " + exception.Message);
        }
    }

    /// <summary>How the runtime's surfaces show its tool calls; one per runtime.</summary>
    public Task<ToolPresentation> ToolsAsync() => _tools.Value;

    /// <summary>Stops the start if it is still running, then the runtime (after background indexing), then the host it owns.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        CorvidRuntime? runtime = null;
        try
        {
            runtime = await Runtime.ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // It never started; there is nothing to stop but the host.
        }
        if (runtime is not null)
        {
            await runtime.MaintenanceIdleAsync().ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await runtime.DisposeAsync().ConfigureAwait(false);
        }
        if (_ownsHost)
        {
            await _host.StopAsync(CancellationToken.None).ConfigureAwait(false);
            _host.Dispose();
        }
        _lifetime.Dispose();
    }

    /// <summary>The missing models in a sentence, with what fetches each, as the ACP server words it.</summary>
    public static string Describe(ModelsUnavailableException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return "Corvid cannot start: " + string.Join(" ", exception.Missing.Select(item =>
            item.Detail + (item.Acquire is { } remedy ? $" Run `{remedy}`." : "")));
    }

    private void Report(StartupStep step)
    {
        Volatile.Write(ref _step, step.Elapsed is null ? step : null);
        Progress?.Invoke(step);
    }

    /// <summary>Progress reported as it happens, not posted to a captured context (there is none on the pool).</summary>
    private sealed class Relay(Action<StartupStep> report) : IProgress<StartupStep>
    {
        public void Report(StartupStep value) => report(value);
    }
}
