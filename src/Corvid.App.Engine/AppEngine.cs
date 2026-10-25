using System.Collections.Immutable;
using Corvid.App.Core.Composition;
using Corvid.Gateway;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
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
    private readonly SemaphoreSlim _starting = new(1, 1);
    private Lazy<Task<ToolPresentation>> _tools;
    private Lazy<Task<HostedGateway?>> _gateway;
    private Task<CorvidRuntime> _runtime;
    private ImmutableArray<ModelAvailability> _missing;
    private string? _failure;
    private StartupStep? _step;
    private int _disposed;

    private AppEngine(IHost host, bool ownsHost, Task<CorvidRuntime> runtime, ImmutableArray<ModelAvailability> missing, string? failure,
        CancellationTokenSource lifetime, bool hostGateway, bool hostStarted = false)
    {
        _host = host;
        _ownsHost = ownsHost;
        _runtime = runtime;
        _missing = missing;
        _failure = failure;
        _lifetime = lifetime;
        HostsGateway = hostGateway;
        HostStarted = hostStarted;
        _tools = NewTools();
        _gateway = NewGateway();
    }

    private Lazy<Task<ToolPresentation>> NewTools() => new(async () => new ToolPresentation(await RuntimeAsync().ConfigureAwait(false)));

    private Lazy<Task<HostedGateway?>> NewGateway() => new(HostGatewayAsync);

    /// <summary>
    /// Whether this engine hosts the gateway (U-8, G-14): the app does when it is the engine, under the one-lock rule shared
    /// with <c>corvid gateway</c>. Tests choose (<see cref="ForRuntime"/>), so no adapter starts unless a test asks.
    /// </summary>
    public bool HostsGateway { get; }

    /// <summary>The host started (its configuration passed the validator), so a runtime can be started over it again.</summary>
    private bool HostStarted { get; }

    /// <summary>The host's services: <c>CorvidConfigWriter</c>, <c>ModelCatalogue</c>, <c>Doctor</c>, the options.</summary>
    public IServiceProvider Services => _host.Services;

    /// <summary>
    /// The runtime as it starts; faulted when it cannot (<see cref="RuntimeAsync"/> says why in a sentence). A runtime that
    /// could not start for want of models is started again by <see cref="StartAgainAsync"/>, which replaces this task.
    /// </summary>
    public Task<CorvidRuntime> Runtime => Volatile.Read(ref _runtime);

    /// <summary>Configured models the cache lacks, found before anything loaded; the head opens First run when there are any.</summary>
    public ImmutableArray<ModelAvailability> MissingModels => _missing;

    /// <summary>Why the runtime cannot start (invalid configuration, missing models), known before the window opens; null otherwise.</summary>
    public string? StartFailure => Volatile.Read(ref _failure);

    /// <summary>
    /// A runtime started by <see cref="StartAgainAsync"/> is up: services that subscribed to the runtime once (the
    /// scheduler's runs) subscribe again, and the window lists its sessions. Raised from a pool thread.
    /// </summary>
    public event Action<CorvidRuntime>? Started;

    /// <summary>The stage the start is at, or null once the runtime is up (or could not start).</summary>
    public StartupStep? CurrentStep => Volatile.Read(ref _step);

    /// <summary>Raised from a pool thread at each stage of the start, as it begins and as it ends.</summary>
    public event Action<StartupStep>? Progress;

    /// <summary>
    /// Memory may have changed in this process (A4V2-3): the window staged a <c>#</c> line, or a turn ended that could have
    /// written (its <c>memory_add</c> calls, its <c>remember</c> checkpoint). The memory store raises nothing itself, so the
    /// Memory page, its badge and the Board's tray read it again on this. Raised on the thread that wrote.
    /// </summary>
    public event Action? MemoryChanged;

    /// <summary>Raises <see cref="MemoryChanged"/>.</summary>
    internal void NotifyMemoryChanged() => MemoryChanged?.Invoke();

    /// <summary>
    /// Starts the host (validating the configuration) and checks the model cache, then starts the runtime on the pool.
    /// A configuration the validator refuses, a host that cannot start for another reason (a hosted service that throws, a
    /// file it cannot read), or models the cache lacks, leave the runtime unstarted with the reason in
    /// <see cref="StartFailure"/>, rather than failing here: the window still opens and says why.
    /// </summary>
    /// <param name="ownsHost">Whether disposing the engine stops and disposes the host (the head's); tests keep their own.</param>
    /// <param name="progress">Told each stage of the runtime's start as it begins and ends, from a pool thread (as <see cref="Progress"/>).</param>
    public static async Task<AppEngine> StartAsync(IHost host, bool ownsHost = true, IProgress<StartupStep>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);
        // How this process names itself in the lock notes other processes read (a session's driver, the scheduler, the gateway).
        ProcessLock.ProcessName = "the Corvid app";
        var lifetime = new CancellationTokenSource();
        try
        {
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OptionsValidationException exception)
        {
            var reason = "Corvid cannot start: the configuration is not valid. " + string.Join(" ", exception.Failures);
            return new AppEngine(host, ownsHost, Task.FromException<CorvidRuntime>(new ServiceUnavailableException(reason)), [], reason, lifetime, false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException && !cancellationToken.IsCancellationRequested)
        {
            return Failed(host, ownsHost, lifetime, exception);
        }

        CorvidRuntimeFactory factory;
        ImmutableArray<ModelAvailability> missing;
        try
        {
            factory = host.Services.GetRequiredService<CorvidRuntimeFactory>();
            missing = factory.CheckModels().ToImmutableArray();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failed(host, ownsHost, lifetime, exception);
        }
        if (!missing.IsEmpty)
        {
            var reason = Describe(new ModelsUnavailableException(missing));
            return new AppEngine(host, ownsHost, Task.FromException<CorvidRuntime>(new ModelsUnavailableException(missing)), missing, reason, lifetime,
                hostGateway: true, hostStarted: true);
        }

        var started = new TaskCompletionSource<CorvidRuntime>(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new AppEngine(host, ownsHost, started.Task, [], null, lifetime, hostGateway: true, hostStarted: true);
        engine.Load(factory, started, progress);
        return engine;
    }

    /// <summary>
    /// Loads the runtime on the pool: loading reads the models from disk for seconds, and the window must not wait on it. Its
    /// outcome is the task given, which every service and DisposeAsync await, so a failure is always observed. The gateway
    /// starts with the runtime (G-14), not when its page is first opened.
    /// </summary>
    private void Load(CorvidRuntimeFactory factory, TaskCompletionSource<CorvidRuntime> started, IProgress<StartupStep>? progress) =>
        _ = Task.Run(async () =>
        {
            try
            {
                started.TrySetResult(await factory.StartAsync(new Relay(step =>
                {
                    Report(step);
                    progress?.Report(step);
                }), _lifetime.Token).ConfigureAwait(false));
                // G-14: the gateway starts with the runtime, not when its page is first opened.
                _ = GatewayAsync();
            }
            catch (Exception exception)
            {
                started.TrySetException(exception);
            }
            finally
            {
                Volatile.Write(ref _step, null);
            }
        }, CancellationToken.None);

    /// <summary>
    /// Starts the runtime again when it could not start for want of models (First run's or Models' downloads finished): the
    /// cache is checked again and, when nothing configured is missing now, the runtime loads, so Sessions and every other
    /// area work without restarting the app. True once a runtime is up (already, or now); false, with
    /// <see cref="MissingModels"/> and <see cref="StartFailure"/> brought up to date, when models are still missing, the
    /// configuration was refused at start, or the start failed again. Never replaces a runtime that is up or loading. The
    /// configuration is the one the host read at start: what First run wrote since applies from the next start.
    /// </summary>
    public async Task<bool> StartAgainAsync(IProgress<StartupStep>? progress = null)
    {
        await _starting.WaitAsync().ConfigureAwait(false);
        TaskCompletionSource<CorvidRuntime> started;
        try
        {
            var current = Runtime;
            if (!current.IsCompleted)
            {
                await ((Task)current).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                return current.IsCompletedSuccessfully;
            }
            if (current.IsCompletedSuccessfully)
                return true;
            // A configuration the validator refused, or a host that never started, has no factory to start from.
            if (!HostStarted || Volatile.Read(ref _disposed) != 0 || current.Exception?.GetBaseException() is not ModelsUnavailableException)
                return false;
            var factory = _host.Services.GetRequiredService<CorvidRuntimeFactory>();
            var missing = await Task.Run(() => factory.CheckModels().ToImmutableArray()).ConfigureAwait(false);
            if (!missing.IsEmpty)
            {
                _missing = missing;
                Volatile.Write(ref _failure, Describe(new ModelsUnavailableException(missing)));
                Volatile.Write(ref _runtime, Task.FromException<CorvidRuntime>(new ModelsUnavailableException(missing)));
                return false;
            }
            started = new TaskCompletionSource<CorvidRuntime>(TaskCreationOptions.RunContinuationsAsynchronously);
            _missing = [];
            Volatile.Write(ref _failure, null);
            _tools = NewTools();
            // The gateway waited on the runtime that could not start: it starts with this one (Load).
            _gateway = NewGateway();
            Volatile.Write(ref _runtime, started.Task);
            Load(factory, started, progress);
        }
        finally
        {
            _starting.Release();
        }

        try
        {
            var runtime = await started.Task.ConfigureAwait(false);
            Started?.Invoke(runtime);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The failure is the Runtime task's, which RuntimeAsync words for every page.
            return false;
        }
    }

    /// <summary>An engine that could not start: the reason in a sentence, for the window to show.</summary>
    private static AppEngine Failed(IHost host, bool ownsHost, CancellationTokenSource lifetime, Exception exception)
    {
        var reason = "Corvid cannot start: " + exception.Message;
        return new AppEngine(host, ownsHost, Task.FromException<CorvidRuntime>(new ServiceUnavailableException(reason)), [], reason, lifetime, false);
    }

    /// <summary>An engine over a runtime already started (tests, and a head that starts the runtime itself).</summary>
    /// <param name="hostGateway">Whether the engine hosts the gateway (<see cref="HostsGateway"/>), with the adapters registered in DI.</param>
    public static AppEngine ForRuntime(IHost host, CorvidRuntime runtime, bool ownsHost = false, bool hostGateway = false)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(runtime);
        return new AppEngine(host, ownsHost, Task.FromResult(runtime), [], null, new CancellationTokenSource(), hostGateway);
    }

    /// <summary>
    /// The gateway this engine hosts, started on first use (G-14): null when the engine does not host one. Whether it runs
    /// here, in another process, or not at all is its <see cref="HostedGateway.Status"/>; it never faults for those.
    /// </summary>
    /// <exception cref="ServiceUnavailableException">The runtime could not start.</exception>
    public Task<HostedGateway?> GatewayAsync() => _gateway.Value;

    private async Task<HostedGateway?> HostGatewayAsync()
    {
        if (!HostsGateway)
            return null;
        var runtime = await RuntimeAsync().ConfigureAwait(false);
        var options = Services.GetRequiredService<IOptions<CorvidOptions>>().Value;
        var gateway = new HostedGateway(runtime, Services.GetRequiredService<SlashCommandRegistry>(), options,
            Services.GetServices<GatewayAdapterRegistration>());
        try
        {
            await gateway.StartAsync(_lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Closing while it started: DisposeAsync stops it.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Not the gateway's own refusals (those are its status): something broke; the page says so through the status.
            Console.Error.WriteLine("corvid app: the gateway could not start: " + exception.Message);
        }
        return gateway;
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
        if (runtime is not null && _gateway.IsValueCreated)
        {
            // Before the runtime: answers still being written reach the ledger while the sessions are there (as corvid gateway stops).
            HostedGateway? gateway = null;
            try
            {
                gateway = await _gateway.Value.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
            }
            if (gateway is not null)
                await gateway.DisposeAsync().ConfigureAwait(false);
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
