using Corvid.Abstractions.IO;
using Corvid.Gateway;
using Corvid.Host.Commands;
using Corvid.Host.Configuration;

namespace Corvid.Host.Composition;

/// <summary>Where the gateway runs, as a process sees it (U-8): here, in another process that holds its lock, or nowhere.</summary>
public enum GatewayHosting
{
    Off,
    Here,
    Elsewhere,
}

/// <summary>
/// The gateway's status (G-14): where it runs and why, who holds its lock since when (this process's note, or the other's),
/// and, when it runs here, each adapter's live state.
/// </summary>
/// <param name="Detail">Why it is off (no adapter enabled, a refused guest tool), or which process runs it.</param>
public sealed record GatewayStatus(GatewayHosting Hosting, string Detail)
{
    /// <summary>The lock's holder: this process when <see cref="GatewayHosting.Here"/>, the other one when elsewhere (null if it left no note).</summary>
    public LockHolder? Holder { get; init; }

    /// <summary>The adapters' live states; empty unless the gateway runs here.</summary>
    public IReadOnlyList<AdapterStatus> Adapters { get; init; } = [];

    /// <summary>The gateway's single lock, beside <c>gateway.db</c>: whichever process holds it runs the adapters.</summary>
    public static string LockPath(CorvidOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return CorvidPaths.GatewayDatabase(options) + ".lock";
    }

    /// <summary>
    /// The status from outside any gateway (<c>corvid gateway status</c>, a process that does not host it): the lock tried
    /// for an instant, and the holder's note read when it is held.
    /// </summary>
    public static GatewayStatus Probe(CorvidOptions options)
    {
        var probe = ProcessLock.Probe(LockPath(options), withoutNote: true);
        return probe.State == LockState.HeldElsewhere ? Elsewhere(probe.Holder) : new GatewayStatus(GatewayHosting.Off, "No gateway is running.");
    }

    internal static GatewayStatus Elsewhere(LockHolder? holder) =>
        new(GatewayHosting.Elsewhere, $"The gateway runs in {SessionHeldException.HolderText(holder)}.") { Holder = holder };
}

/// <summary>
/// The gateway hosted over a running <see cref="CorvidRuntime"/> under the one-lock rule (U-8, G-14), shared by
/// <c>corvid gateway</c> and the windowed app when it is the engine: <see cref="StartAsync"/> composes the gateway
/// (<see cref="CorvidGateway.Create"/>) and starts it if no other process holds <c>gateway.db.lock</c>, writing the note
/// that tells other processes who runs it; otherwise it reports who does, and a later <see cref="StartAsync"/> takes over
/// once that process has exited. <see cref="Status"/> is the status API; <see cref="Changed"/> is raised for every adapter
/// state change, pairing request and delivery given up (each is a gateway notice), and when hosting starts or stops.
/// </summary>
public sealed class HostedGateway : IAsyncDisposable
{
    private readonly CorvidRuntime _runtime;
    private readonly SlashCommandRegistry _commands;
    private readonly CorvidOptions _options;
    private readonly IReadOnlyList<GatewayAdapterRegistration> _registrations;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GatewayHost? _host;
    private LockHolder? _holder;
    private GatewayStatus _status = new(GatewayHosting.Off, "The gateway has not been started.");
    private bool _disposed;

    /// <param name="registrations">Adapters supplied in DI, which win by name over the built-in ones (tests' fakes).</param>
    public HostedGateway(CorvidRuntime runtime, SlashCommandRegistry commands, CorvidOptions options,
        IEnumerable<GatewayAdapterRegistration>? registrations = null, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(options);
        _runtime = runtime;
        _commands = commands;
        _options = options;
        _registrations = [.. registrations ?? []];
        _time = time ?? TimeProvider.System;
    }

    /// <summary>The gateway's notices (connects, failures, pairing requests), for a surface to print. Raised on the gateway's threads.</summary>
    public Action<string>? Notice { get; init; }

    /// <summary>Something in <see cref="Status"/> may have changed. Raised on the gateway's threads.</summary>
    public event Action? Changed;

    /// <summary>The running gateway, while this process hosts it.</summary>
    public GatewayHost? Gateway => Volatile.Read(ref _host);

    /// <summary>Where the gateway runs now, with the adapters' live states when here.</summary>
    public GatewayStatus Status
    {
        get
        {
            var host = Volatile.Read(ref _host);
            return host is null
                ? Volatile.Read(ref _status)
                : new GatewayStatus(GatewayHosting.Here, "The gateway runs in this process.") { Holder = _holder, Adapters = host.Adapters };
        }
    }

    /// <summary>
    /// Hosts the gateway unless it already runs here: refused (and reported) when no adapter is enabled or a guest tool is
    /// refused, or when another process holds the lock. Never throws for those; the status says why.
    /// </summary>
    public async Task<GatewayStatus> StartAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_host is not null)
                return Status;
            GatewayHost host;
            try
            {
                host = CorvidGateway.Create(_runtime, _commands, _options, _registrations, OnNotice);
            }
            catch (InvalidOperationException exception)
            {
                return Set(new GatewayStatus(GatewayHosting.Off, exception.Message));
            }
            try
            {
                await host.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (GatewayLockHeldException)
            {
                await host.DisposeAsync().ConfigureAwait(false);
                return Set(GatewayStatus.Elsewhere(ProcessLock.ReadNote(GatewayStatus.LockPath(_options))));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // L5, L8: a lock file nobody can open (a read-only or full file system) is not another gateway; the host goes.
                await host.DisposeAsync().ConfigureAwait(false);
                return Set(new GatewayStatus(GatewayHosting.Off,
                    "The gateway cannot start: " + FileLocks.Unopenable(GatewayStatus.LockPath(_options), exception) + "."));
            }
            catch
            {
                await host.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            _holder = new LockHolder(ProcessLock.ProcessName, Environment.ProcessId, _time.GetUtcNow());
            ProcessLock.WriteNote(GatewayStatus.LockPath(_options), _holder);
            Volatile.Write(ref _host, host);
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
        return Status;
    }

    /// <summary>Stops hosting: answers still being written reach the ledger first (<see cref="GatewayHost.StopAsync"/>); the lock and note go.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        GatewayHost? host;
        try
        {
            host = _host;
            if (host is null)
                return;
            // The note goes while the lock is still held, so no other process's fresh note is deleted.
            if (_holder is { } holder)
                ProcessLock.DeleteNote(GatewayStatus.LockPath(_options), holder);
            await host.DisposeAsync().ConfigureAwait(false);
            Volatile.Write(ref _host, null);
            _status = new GatewayStatus(GatewayHosting.Off, "The gateway was stopped.");
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        _disposed = true;
        _gate.Release();
    }

    private GatewayStatus Set(GatewayStatus status)
    {
        var changed = Volatile.Read(ref _status) != status;
        Volatile.Write(ref _status, status);
        if (changed)
            Changed?.Invoke();
        return status;
    }

    private void OnNotice(string message)
    {
        Notice?.Invoke(message);
        Changed?.Invoke();
    }
}
