using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Corvid.App.Core;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.Host.Composition;
using Corvid.Host.Configuration;
using Corvid.Host.Diagnostics;
using Corvid.Host.Models;
using Corvid.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AppPlacement = Corvid.App.Core.Models.ModelPlacement;
using HostPlacement = Corvid.Models.ModelPlacement;

namespace Corvid.App.Engine.Models;

/// <summary>
/// The Models area over the engine (docs/plan/ui.md §4.8, F14, F23): the slots as the configuration the next start reads
/// them, with where each runs and how fast once the runtime is up; the catalogue (<see cref="ModelCatalogue"/>) with each
/// model's state in the cache, on its server or in Vault; the decider's tier; the doctor's GPU section and Vault check;
/// placement. Every change is written to <c>corvid.json</c> through the host's <see cref="CorvidConfigWriter"/> (validated
/// as the next start would read it), as <c>corvid model switch</c> does. A switch also applies at once: new sessions in
/// this window start on the new model, and the window's open session switches after its issued tool calls. Ladders,
/// placement and added servers apply from the next start, and the answer says so. Downloads run one at a time on the pool,
/// only when asked, with progress through <see cref="Changed"/>; a cancelled one leaves no partial file.
/// </summary>
public sealed class EngineModelService : IModelService, IDisposable
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(3);

    private readonly AppEngine _engine;
    private readonly IDispatcher _dispatcher;
    private readonly TimeProvider _time;
    private readonly Func<string?> _currentSession;
    private readonly ModelCatalogue _catalogue;
    private readonly CorvidConfigWriter _writer;
    private readonly IValidateOptions<CorvidOptions> _validator;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _gate = new();
    private readonly List<string> _queue = [];
    private readonly Dictionary<string, DownloadProgress> _progress = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServerCheck> _servers = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Task> _background = [];
    private ModelsSnapshot _base;
    private ModelsSnapshot _snapshot;
    private Running? _running;
    // Why the last built snapshot may be out of date (a refresh after a download failed); cleared by the next good build.
    private string? _problem;
    private string? _sessionId;
    private int _disposed;

    /// <param name="currentSession">The window's open session, read on the UI thread: the one a pin and a live switch apply to.</param>
    /// <param name="network">The route to local servers for their checks; tests pass a fake.</param>
    public EngineModelService(AppEngine engine, IDispatcher dispatcher, TimeProvider time, Func<string?> currentSession, HttpMessageHandler? network = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(currentSession);
        _engine = engine;
        _dispatcher = dispatcher;
        _time = time;
        _currentSession = currentSession;
        _catalogue = engine.Services.GetRequiredService<ModelCatalogue>();
        _writer = engine.Services.GetRequiredService<CorvidConfigWriter>();
        _validator = engine.Services.GetRequiredService<IValidateOptions<CorvidOptions>>();
        _http = network is null ? new HttpClient() : new HttpClient(network, disposeHandler: false);
        _http.Timeout = ServerTimeout;
        // Until the first load: nothing known, which the pages show as "checking" rather than as Vault's absence.
        _base = _snapshot = new ModelsSnapshot(_writer.Path, false, [], [], new CloudPrivacy("", false, false, 0, 0, 0),
            new DeciderStatus("", "", DeciderTier.Degraded, "", "", "", ""), [], [], "") { IsLoaded = false };
    }

    public event EventHandler? Changed;

    public ModelsSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return _snapshot;
        }
    }

    public async Task<ModelsSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        _sessionId = _currentSession();
        var snapshot = await Task.Run(() => BuildAsync(cancellationToken), cancellationToken).ConfigureAwait(false);
        return Publish(snapshot);
    }

    public Task<ConfigWrite> SwitchAsync(string slot, string model, CancellationToken cancellationToken = default)
    {
        var sessionId = _sessionId = _currentSession();
        return Task.Run(async () =>
        {
            if (!ModelSlots.All.Contains(slot, StringComparer.Ordinal))
                return Refused($"There is no slot named {E(slot)}.");
            if (!_catalogue.TryFind(model, out var descriptor))
                return Refused($"There is no model named {E(model)} in the catalogue.");
            if (!descriptor.Serves(slot))
                return Refused($"{E(descriptor.Name)} cannot serve as the {slot}; it serves {E(string.Join(", ", descriptor.Slots))}.");
            var availability = _catalogue.Check(descriptor.Name);
            if (!availability.IsReady)
                return Refused(E(availability.Detail) + (availability.Acquire is { } acquire ? $" Run `{E(acquire)}`." : ""));
            var runtime = Started();
            if (slot == ModelSlots.TextEmbedder && runtime is not null && !runtime.IsConfigured(slot, descriptor.Name))
                return Refused($"Memory embeds only with the embedder.text models configured at start. Add {E(descriptor.Name)} to " +
                    "`Corvid:Models:Slots:embedder.text:Fallbacks`, then restart.");

            var options = NextStartOptions();
            var settings = options.Models.EffectiveSlots().GetValueOrDefault(slot);
            if (settings is not null && string.Equals(settings.Model, descriptor.Name, StringComparison.OrdinalIgnoreCase))
                return Written($"The {slot} is already {E(descriptor.Name)}.");
            var edits = new List<ConfigEdit> { ConfigEdit.Set($"{CorvidOptions.Section}:Models:Slots:{slot}:Model", descriptor.Name) };
            // A model cannot be its slot's own escalation: one on the ladder leaves it as it becomes the default.
            if (settings is not null && settings.Escalation.Contains(descriptor.Name, StringComparer.OrdinalIgnoreCase))
                edits.Add(Ladder(slot, [.. settings.Escalation.Where(step => !string.Equals(step, descriptor.Name, StringComparison.OrdinalIgnoreCase))], settings));
            var result = _writer.Apply(edits);
            if (!result.Written)
                return Refused($"Not written: {E(result.FailureText)}");

            var message = $"Written to `{E(result.Path)}`: the {slot} is {E(descriptor.Name)}.";
            if (slot == ModelSlots.Decider)
            {
                message += " The decider is shared by every session, so it changes when Corvid next starts.";
            }
            else if (runtime is null)
            {
                message += " It applies when Corvid starts.";
            }
            else
            {
                // The file is written by now: a live switch that fails says so as what it is, not as a refused switch.
                try
                {
                    message += " " + await SwitchLiveAsync(runtime, slot, descriptor.Name, sessionId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    message += $" Written; the live switch failed: {E(exception.Message)} It applies when Corvid next starts.";
                }
            }

            if (!result.ShadowedKeys.IsEmpty)
                message += $" This run's environment or `--set` overrides {E(string.Join(", ", result.ShadowedKeys))}, so the next start goes on using that.";
            try
            {
                await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                message += $" Written; the page could not read the models again: {E(exception.Message)}";
            }
            return Written(message);
        }, cancellationToken);
    }

    public Task<ConfigWrite> SetLadderAsync(string slot, IReadOnlyList<string> ladder, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ladder);
        return Task.Run(async () =>
        {
            if (!ModelSlots.All.Contains(slot, StringComparer.Ordinal))
                return Refused($"There is no slot named {E(slot)}.");
            var settings = NextStartOptions().Models.EffectiveSlots().GetValueOrDefault(slot);
            if (settings is null)
                return Refused($"The {slot} is unbound, so it has no ladder.");
            if (ladder.Distinct(StringComparer.OrdinalIgnoreCase).Count() != ladder.Count)
                return Refused("A model can be on the ladder only once.");
            var names = new List<string>();
            foreach (var step in ladder)
            {
                if (string.Equals(step, settings.Model, StringComparison.OrdinalIgnoreCase))
                    return Refused($"{E(step)} is the {slot} itself; the ladder holds the models a hard step goes to.");
                if (!_catalogue.TryFind(step, out var model) || !model.Serves(slot))
                    return Refused($"{E(step)} cannot serve as the {slot}.");
                names.Add(model.Name);
            }

            var result = _writer.Apply(Ladder(slot, names, settings));
            if (!result.Written)
                return Refused($"Not written: {E(result.FailureText)}");
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            var order = names.Count == 0 ? "nowhere: no escalation" : string.Join(", then ", names.Select(E));
            return Written($"Written to `{E(result.Path)}`: a hard step on the {slot} goes to {order}. " +
                "Sessions escalate along the ladder Corvid started with, so this applies when Corvid next starts.");
        }, cancellationToken);
    }

    public Task<ConfigWrite> SetPinnedAsync(string slot, bool pinned, CancellationToken cancellationToken = default)
    {
        var sessionId = _sessionId = _currentSession();
        return Task.Run(async () =>
        {
            if (!ModelSlots.All.Contains(slot, StringComparer.Ordinal))
                return Refused($"There is no slot named {E(slot)}.");
            if (Started() is not { } runtime)
                return Refused("Corvid has not started, so there is no session to pin for.");
            if (sessionId is null)
                return Refused("Open a session first: a pin holds for one session.");
            if (!runtime.TryGetSession(sessionId, out var session))
                return Refused($"Session {E(sessionId)} is not open in this window yet: open it, then pin.");
            if (!session.Slots.TryCurrent(slot, out var binding))
                return Refused($"The {slot} is unbound in {E(sessionId)}.");
            if (pinned)
                await session.Actor.SwitchModelAsync(slot, binding, pin: true, cancellationToken).ConfigureAwait(false);
            else
                session.Slots.Unpin(slot);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return Written(pinned
                ? $"The {slot} is pinned to {E(binding.Profile.Id)} for {E(sessionId)}: escalation leaves it alone."
                : $"The {slot} is no longer pinned in {E(sessionId)}: a hard step escalates again.");
        }, cancellationToken);
    }

    public Task<ConfigWrite> SetPlacementAsync(string model, string device, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        if (!_catalogue.TryFind(model, out var descriptor))
            return Refused($"There is no model named {E(model)} in the catalogue.");
        if (!HostPlacement.TryParse(device, out var parsed, out _))
            return Refused($"{E(device)} is not a device: `cpu`, `cuda` or `cuda:N`.");
        var result = _writer.Apply(ConfigEdit.Set($"{CorvidOptions.Section}:Models:Placement:{descriptor.Name}:Device", parsed));
        if (!result.Written)
            return Refused($"Not written: {E(result.FailureText)}");
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        var fallback = NextStartOptions().Models.Placement.GetValueOrDefault(descriptor.Name)?.Fallback ?? PlacementFallback.Fail;
        var then = fallback == PlacementFallback.Cpu ? "falling back to the CPU when it cannot run there" : "with no fallback: it does not load when it cannot run there";
        return Written($"Written to `{E(result.Path)}`: {E(descriptor.Name)} on {E(parsed)}, {then}. " +
            "Models are placed as they load, so this applies when Corvid next starts.");
    }, cancellationToken);

    public Task DownloadAsync(IEnumerable<string> models, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(models);
        var names = models.ToList();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            foreach (var name in names)
            {
                // Only a local model a provider can fetch, not on disk and not on its way already.
                if (!_catalogue.TryFind(name, out var model) || model.DownloadBytes is not { } total)
                    continue;
                if (_queue.Contains(model.Name, StringComparer.OrdinalIgnoreCase) || _base.Find(model.Name) is { State: ModelState.Cached })
                    continue;
                _queue.Add(model.Name);
                _failures.Remove(model.Name);
                _progress[model.Name] = new DownloadProgress(0, total, 0);
            }

            if (_running is null && _queue.Count > 0)
                Track(PumpGuardedAsync());
        }

        Republish();
        return Task.CompletedTask;
    }

    public async Task CancelDownloadAsync(string model, CancellationToken cancellationToken = default)
    {
        Running? running;
        lock (_gate)
        {
            var name = _queue.FirstOrDefault(item => string.Equals(item, model, StringComparison.OrdinalIgnoreCase));
            if (name is null)
                return;
            running = _running is { } current && string.Equals(current.Model, name, StringComparison.OrdinalIgnoreCase) ? current : null;
            if (running is null)
            {
                _queue.Remove(name);
                _progress.Remove(name);
            }
            else
            {
                running.Cancelled = true;
            }
        }

        if (running is not null)
        {
            await running.Cancellation.CancelAsync().ConfigureAwait(false);
            // The pump removes the partial files once the download has stopped writing them.
            await running.Stopped.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        Republish();
    }

    public Task<ServerCheck> CheckServerAsync(string model, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        var remote = NextStartOptions().Models.Remote;
        var entry = remote.FirstOrDefault(pair => string.Equals(pair.Key, model, StringComparison.OrdinalIgnoreCase));
        if (entry.Value is null)
        {
            return _catalogue.TryFind(model, out _)
                ? new ServerCheck(false, $"{E(model)} is not a local server.")
                : new ServerCheck(false, $"There is no model named {E(model)}.");
        }

        if (Kind(entry.Value) != ModelKind.Server || entry.Value.Endpoint is not { Length: > 0 } endpoint || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
            return new ServerCheck(false, $"{E(entry.Key)} is not a local server.");
        var (check, _) = await ProbeAsync(entry.Key, uri, cancellationToken).ConfigureAwait(false);
        lock (_gate)
            _servers[entry.Key] = check;
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return check;
    }, cancellationToken);

    public Task<ConfigWrite> AddServerAsync(string name, string url, CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        name = (name ?? "").Trim();
        url = (url ?? "").Trim();
        if (name.Length == 0 || name.Any(char.IsWhiteSpace) || name.Contains(':', StringComparison.Ordinal))
            return Refused("Give the server a name without spaces or colons, such as qwen-coder.");
        // Nothing is stored while it holds characters that render as nothing or reorder text (the name becomes a model's).
        if (SafeText.HasHidden(name) || SafeText.HasHidden(url))
            return Refused($"The name or the address holds hidden characters ({E(SafeText.Reveal(name))}, {E(SafeText.Reveal(url))}). " +
                "Nothing was written: type them again.");
        if (_catalogue.TryFind(name, out _) || NextStartOptions().Models.Remote.ContainsKey(name))
            return Refused($"The catalogue already has a model named {E(name)}.");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return Refused("The address must be an http or https URL, such as http://127.0.0.1:11434/v1.");

        // The server names the model it serves; the configuration needs that id, so a server that does not answer is not added.
        var (check, served) = await ProbeAsync(name, uri, cancellationToken).ConfigureAwait(false);
        if (!check.Reachable)
            return Refused($"{check.Message} Nothing was written: start the server, then add it.");
        var id = served.FirstOrDefault(item => string.Equals(item, name, StringComparison.Ordinal))
            ?? (served.Count == 1 ? served[0] : null);
        if (id is null)
        {
            return Refused(served.Count == 0
                ? $"{E(name)} at `{E(uri.ToString())}` answered but lists no model. Nothing was written."
                : $"{E(name)} at `{E(uri.ToString())}` serves {E(string.Join(", ", served))}: name the server after the one to use.");
        }
        // The server's own answer is outside text: a model id with hidden characters is not written into the configuration.
        if (SafeText.HasHidden(id))
            return Refused($"{E(name)} at `{E(uri.ToString())}` names its model with hidden characters ({E(SafeText.Reveal(id))}). Nothing was written.");

        var server = new JsonObject { ["Provider"] = "openai-compatible", ["Endpoint"] = uri.ToString(), ["Model"] = id };
        var result = _writer.Apply(new ConfigEdit($"{CorvidOptions.Section}:Models:Remote:{name}", server));
        if (!result.Written)
            return Refused($"Not written: {E(result.FailureText)}");
        lock (_gate)
            _servers[name] = check;
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return Written($"Written to `{E(result.Path)}`: {E(name)} (model {E(id)}) at `{E(uri.ToString())}` answered. " +
            "It joins the catalogue when Corvid next starts; then it can serve the generator.");
    }, cancellationToken);

    public void Dispose()
    {
        Task[] background;
        lock (_gate)
        {
            if (_disposed != 0)
                return;
            _disposed = 1;
            background = [.. _background];
        }

        _lifetime.Cancel();
        _http.Dispose();
        // The pump stops at its next report; whatever it was fetching keeps its partial file for a later resume.
        _ = Task.WhenAll(background).ContinueWith(_ => _lifetime.Dispose(), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // ---- Downloads ----

    private sealed class Running(string model, CancellationTokenSource cancellation)
    {
        public string Model { get; } = model;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Cancelled { get; set; }
    }

    private async Task PumpAsync()
    {
        while (true)
        {
            Running running;
            lock (_gate)
            {
                if (_disposed != 0 || _queue.Count == 0)
                {
                    _running = null;
                    return;
                }

                running = _running = new Running(_queue[0], CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
            }

            try
            {
                await DownloadOneAsync(running).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Whatever went wrong with this one, the queue goes on: it leaves the queue with its reason on its row.
                Console.Error.WriteLine($"corvid app: downloading {running.Model} failed: {exception.Message}");
                lock (_gate)
                {
                    _queue.RemoveAll(item => string.Equals(item, running.Model, StringComparison.OrdinalIgnoreCase));
                    _progress.Remove(running.Model);
                    _failures[running.Model] = exception.Message;
                }
                Republish();
            }
            finally
            {
                running.Cancellation.Dispose();
                running.Stopped.TrySetResult();
            }
        }
    }

    /// <summary>
    /// The pump, which never leaves <see cref="_running"/> set when it stops: a later download starts a pump of its own only
    /// while none runs, so a pump gone without clearing it would leave every later download queued for good.
    /// </summary>
    private async Task PumpGuardedAsync()
    {
        try
        {
            await PumpAsync().ConfigureAwait(false);
        }
        catch
        {
            // A pump that ends normally cleared it under the lock itself (and a new one may have started since).
            lock (_gate)
                _running = null;
            throw;
        }
    }

    private async Task DownloadOneAsync(Running running)
    {
        var name = running.Model;
        var total = _catalogue.Find(name).DownloadBytes ?? 0;
        var files = new Dictionary<string, long>(StringComparer.Ordinal);
        var started = _time.GetUtcNow();
        var published = started;
        var progress = new Reporter(item =>
        {
            long bytes;
            lock (files)
            {
                files[item.File] = item.Bytes;
                bytes = files.Values.Sum();
            }

            var now = _time.GetUtcNow();
            var seconds = (now - started).TotalSeconds;
            lock (_gate)
                _progress[name] = new DownloadProgress(Math.Min(bytes, total), total, seconds > 0 ? bytes / seconds : 0);
            // At most four reports a second reach the window: a download reports every buffer.
            if (now - published >= ProgressInterval)
            {
                published = now;
                Republish();
            }
        });

        string? failure = null;
        try
        {
            var result = await _catalogue.DownloadAsync(name, progress, running.Cancellation.Token).ConfigureAwait(false);
            if (!result.IsReady)
                failure = result.Detail;
        }
        catch (OperationCanceledException) when (running.Cancellation.IsCancellationRequested)
        {
            if (running.Cancelled)
            {
                // Cancelled by the person: nothing of it stays in the cache.
                try
                {
                    _catalogue.RemovePartialDownloads(name);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failure = $"cancelled, but its partial files could not be removed: {exception.Message}";
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = exception.Message;
        }

        lock (_gate)
        {
            _queue.RemoveAll(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
            _progress.Remove(name);
            if (failure is not null)
                _failures[name] = failure;
        }

        if (failure is not null)
            Console.Error.WriteLine($"corvid app: downloading {name} failed: {failure}");
        if (Volatile.Read(ref _disposed) != 0)
            return;
        // The runtime could not start for want of models (First run): with this one in, it may start now, so Sessions works
        // without restarting the app. It loads on the pool; the status bar and the rail follow it.
        if (failure is null && !_engine.MissingModels.IsEmpty)
            _ = StartEngineAsync();
        try
        {
            await RefreshAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // corvid.json stopped parsing (or holds a value of the wrong shape) while the download ran: the page keeps the
            // last list it read, says why it is out of date, and the queue goes on.
            Console.Error.WriteLine($"corvid app: after downloading {name}, the models could not be read again: {exception.Message}");
            lock (_gate)
                _problem = $"{(failure is null ? $"{name} downloaded" : $"Downloading {name} ended")}, but the models could not be read again, " +
                    $"so this list may be out of date: {exception.Message}";
            Republish();
        }
    }

    /// <summary>Starts the runtime once nothing configured is missing; a start that fails again is written to the log, never lost.</summary>
    private async Task StartEngineAsync()
    {
        try
        {
            if (await _engine.StartAgainAsync().ConfigureAwait(false))
                Republish();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"corvid app: the runtime could not start after the downloads: {exception.Message}");
        }
    }

    /// <summary>Progress reported on the download's own thread as it happens (a <see cref="Progress{T}"/> would post it).</summary>
    private sealed class Reporter(Action<ModelDownloadProgress> report) : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => report(value);
    }

    private void Track(Task task)
    {
        lock (_background)
            _background.Add(task);
        _ = task.ContinueWith(done =>
        {
            lock (_background)
                _background.Remove(done);
            if (done.Exception is { } exception)
                Console.Error.WriteLine($"corvid app: a model download task failed: {exception.GetBaseException().Message}");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // ---- The snapshot ----

    private async Task RefreshAsync(CancellationToken cancellationToken) => Publish(await BuildAsync(cancellationToken).ConfigureAwait(false));

    private ModelsSnapshot Publish(ModelsSnapshot built)
    {
        lock (_gate)
        {
            _base = built;
            _problem = null;
        }
        return Republish();
    }

    /// <summary>The last built snapshot with the downloads in flight laid over it, announced on the UI thread.</summary>
    private ModelsSnapshot Republish()
    {
        ModelsSnapshot snapshot;
        lock (_gate)
        {
            var catalogue = _base.Catalogue.Select(model =>
            {
                if (_progress.TryGetValue(model.Name, out var progress))
                {
                    var running = _running is { } current && string.Equals(current.Model, model.Name, StringComparison.OrdinalIgnoreCase);
                    return model with { State = running ? ModelState.Downloading : ModelState.Queued, Download = progress };
                }

                return _failures.TryGetValue(model.Name, out var failure) && model.State == ModelState.Missing
                    ? model with { Fit = $"{model.Fit} · the last download failed: {SafeText.Clean(failure)}" }
                    : model;
            }).ToList();
            snapshot = _snapshot = _base with { Catalogue = catalogue, Problem = _problem };
            if (_disposed != 0)
                return snapshot;
        }

        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) == 0)
                Changed?.Invoke(this, EventArgs.Empty);
        });
        return snapshot;
    }

    private async Task<ModelsSnapshot> BuildAsync(CancellationToken cancellationToken)
    {
        var configuration = _writer.NextStart();
        var options = Options(configuration);
        var validated = Validated(options);
        var runtime = Started();
        var slots = options.Models.EffectiveSlots();
        var vault = await Doctor.VaultCheckAsync(options, cancellationToken).ConfigureAwait(false);
        var vaultPresent = vault.Status == Corvid.Host.Diagnostics.CheckStatus.Ok;
        var availability = _catalogue.Models.ToDictionary(model => model.Name, model => _catalogue.Check(model.Name), StringComparer.OrdinalIgnoreCase);
        var gpuChecks = new Doctor(configuration, _catalogue, _validator, new CorvidConfigFile(_writer.Path)).GpuChecks();

        // Servers the configuration names are asked once, briefly, the first time; Check asks again.
        var servers = options.Models.Remote.Where(pair => Kind(pair.Value) == ModelKind.Server && pair.Value.Endpoint is { Length: > 0 }).ToList();
        foreach (var (name, settings) in servers)
        {
            bool known;
            lock (_gate)
                known = _servers.ContainsKey(name);
            if (!known && Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri))
            {
                var (check, _) = await ProbeAsync(name, uri, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                    _servers.TryAdd(name, check);
            }
        }

        var session = runtime is not null && _sessionId is { } id && runtime.TryGetSession(id, out var open) ? open : null;
        var ladders = slots.ToDictionary(pair => pair.Key, pair => pair.Value.Escalation, StringComparer.Ordinal);
        var catalogue = new List<CatalogueModel>();
        foreach (var model in _catalogue.Models)
            catalogue.Add(Describe(model, availability[model.Name], options, slots, ladders, vaultPresent));
        // A server or cloud model added to the file since the start: the catalogue has it after a restart.
        foreach (var (name, settings) in options.Models.Remote.Where(pair => !_catalogue.TryFind(pair.Key, out _)).OrderBy(pair => pair.Key, StringComparer.Ordinal))
            catalogue.Add(Added(name, settings));

        return new ModelsSnapshot(
            _writer.Path,
            validated,
            [.. ModelSlots.All.Select(slot => Slot(slot, slots.GetValueOrDefault(slot), runtime, session))],
            catalogue,
            new CloudPrivacy(options.Privacy.Cloud.ToString().ToLowerInvariant(), ExtractorBound(runtime, slots, availability), vaultPresent, 0, 0, 0)
            {
                TodayUnknown = "not counted here yet: the egress ledger's counts come with Audit (G-9)",
            },
            Decider(options, slots, availability, runtime),
            Gpus(gpuChecks),
            Placements(options),
            $"{options.Devices.CpuThreads} {(options.Devices.CpuThreads == 1 ? "thread" : "threads")} for models on the CPU (Corvid:Devices:CpuThreads)");
    }

    private ModelSlot Slot(string slot, SlotSettings? settings, CorvidRuntime? runtime, CorvidSession? session)
    {
        var note = slot switch
        {
            ModelSlots.Generator => "a hard step escalates up this ladder",
            ModelSlots.Decider => "second tier for gates below their floor",
            ModelSlots.TextEmbedder => "memory embeds only with models configured at start",
            ModelSlots.MediaEmbedder => "media memory comes with plan §9.4",
            ModelSlots.Vision => "needed for images in prompts (K-8)",
            _ => null,
        };
        if (settings?.Model is not { Length: > 0 } model)
            return new ModelSlot(slot, null, Note: note, Shared: slot == ModelSlots.Decider);

        SlotPlacement? placement = null;
        if (runtime is not null && runtime.Registry.TryGet(slot, out var configured))
        {
            var profile = session is not null && session.Slots.TryCurrent(slot, out var current) ? current.Profile : configured.Default.Profile;
            var device = profile.Placement;
            int? queue = DeviceSchedulers.IsLocalDevice(device) ? runtime.Devices.QueueDepth(device) : null;
            var p50 = runtime.Metrics.TryGet(slot, profile.Id, out var latency) && latency.P50 is { } median ? (int)Math.Round(median.TotalMilliseconds) : -1;
            placement = new SlotPlacement(device, queue, p50);
        }

        return new ModelSlot(
            slot,
            model,
            IsDefault: CorvidModelDefaults(slot) is { } standard && string.Equals(standard, model, StringComparison.OrdinalIgnoreCase),
            Ladder: [.. settings.Escalation],
            Fallback: settings.Fallbacks.Count == 0 ? null : string.Join(", ", settings.Fallbacks),
            Note: note,
            Placement: placement,
            Shared: slot == ModelSlots.Decider,
            Pinned: session?.Slots.IsPinned(slot) ?? false,
            Pinnable: slot is ModelSlots.Generator or ModelSlots.Summarizer);
    }

    private static string? CorvidModelDefaults(string slot) => ModelOptions.DefaultSlots.GetValueOrDefault(slot);

    private CatalogueModel Describe(ModelDescriptor model, ModelAvailability availability, CorvidOptions options,
        IReadOnlyDictionary<string, SlotSettings> slots, IReadOnlyDictionary<string, List<string>> ladders, bool vaultPresent)
    {
        var roles = new List<string>(model.Slots);
        foreach (var (slot, ladder) in ladders)
        {
            var index = ladder.FindIndex(step => string.Equals(step, model.Name, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
                roles.Add(slot == ModelSlots.Decider ? "second tier" : $"{slot} escalation {index + 1}");
        }

        var role = ladders.GetValueOrDefault(ModelSlots.Decider)?.Contains(model.Name, StringComparer.OrdinalIgnoreCase) == true
            ? "second decider tier"
            : model.Slots.FirstOrDefault() ?? "";
        var serves = string.Join(", ", roles.Distinct(StringComparer.Ordinal));
        var canServe = model.Slots.ToList();
        var context = string.Create(CultureInfo.InvariantCulture, $"context {model.Profile.ContextWindow / 1000}k");
        var remote = options.Models.Remote.FirstOrDefault(pair => string.Equals(pair.Key, model.Name, StringComparison.OrdinalIgnoreCase)).Value;

        switch (model.Profile.Privacy)
        {
            case PrivacyClass.LocalServer:
            {
                ServerCheck? check;
                lock (_gate)
                    check = _servers.GetValueOrDefault(model.Name);
                var endpoint = remote?.Endpoint ?? "";
                return new CatalogueModel(model.Name, ModelKind.Server, serves, role, $"local server · `{E(endpoint)}`", context, 0,
                    check is { Reachable: true } ? ModelState.Reachable : ModelState.Unreachable, canServe);
            }
            case PrivacyClass.Cloud:
            {
                var secret = remote?.Secret;
                var provider = remote?.Provider ?? model.Provider;
                var where = secret is null ? E(provider) : $"{E(provider)} · key `{E(secret)}`{(vaultPresent ? " in Vault" : " · Vault absent")}";
                var egress = (remote?.Egress?.Action ?? options.Privacy.Cloud).ToString().ToLowerInvariant();
                return new CatalogueModel(model.Name, ModelKind.Cloud, $"{serves} · {E(remote?.Model ?? model.Profile.Id)}", role, where, context, 0,
                    availability.IsReady ? ModelState.KeyBound : ModelState.Unavailable, canServe, Secret: secret, Egress: egress);
            }
            default:
            {
                var bytes = model.DownloadBytes ?? 0;
                var device = options.Models.Placement.GetValueOrDefault(model.Name)?.Device ?? HostPlacement.Cpu;
                // Where it runs is said only when it is not the CPU, which every model starts on.
                var fit = device == HostPlacement.Cpu ? ModelText.Size(bytes) : $"{ModelText.Size(bytes)} · placed on {device}";
                if (availability.Readiness is ModelReadiness.Corrupt or ModelReadiness.Unreadable)
                    fit += $" · {availability.Readiness.ToString().ToLowerInvariant()} in the cache";
                var where = model.Provider == StarlingModelProvider.ProviderName ? "Starling, in-process" : $"{E(model.Provider)}, in-process";
                var configured = slots.Values.Any(settings => string.Equals(settings.Model, model.Name, StringComparison.OrdinalIgnoreCase));
                return new CatalogueModel(model.Name, ModelKind.Local, serves, role, where, fit, bytes,
                    availability.IsReady ? ModelState.Cached : ModelState.Missing, canServe,
                    InDefaultSet: configured && model.DownloadBytes is not null,
                    SetupNote: device == HostPlacement.Cpu ? "" : $"placed on {device}");
            }
        }
    }

    private CatalogueModel Added(string name, RemoteModelSettings settings)
    {
        var kind = Kind(settings);
        ServerCheck? check;
        lock (_gate)
            check = _servers.GetValueOrDefault(name);
        var where = kind == ModelKind.Server ? $"local server · `{E(settings.Endpoint)}` · added: in the catalogue after a restart" : $"{E(settings.Provider)} · added: in the catalogue after a restart";
        var state = kind == ModelKind.Server ? check is { Reachable: true } ? ModelState.Reachable : ModelState.Unreachable : ModelState.Unavailable;
        var slots = settings.Slots.Count > 0 ? settings.Slots : [ModelSlots.Generator, ModelSlots.Summarizer];
        return new CatalogueModel(name, kind, string.Join(", ", slots), slots[0], where,
            string.Create(CultureInfo.InvariantCulture, $"context {settings.ContextWindow / 1000}k"), 0, state, [.. slots], Secret: settings.Secret);
    }

    private static ModelKind Kind(RemoteModelSettings settings)
    {
        var provider = settings.Provider?.Trim().ToLowerInvariant();
        return provider == "openai-compatible" && settings.Privacy != PrivacyClass.Cloud ? ModelKind.Server : ModelKind.Cloud;
    }

    private static bool ExtractorBound(CorvidRuntime? runtime, IReadOnlyDictionary<string, SlotSettings> slots, Dictionary<string, ModelAvailability> availability) =>
        runtime is not null
            ? runtime.Registry.TryGet(ModelSlots.Extractor, out _)
            : slots.TryGetValue(ModelSlots.Extractor, out var extractor) && extractor.Model is { } model
              && availability.TryGetValue(model, out var ready) && ready.IsReady;

    /// <summary>
    /// The decider in a status bar's words, as Models words it (<c>ModelsViewModel.StatusDecider</c>), for the pages that
    /// read the running engine rather than a Models snapshot (Memory, the Board): "kev-0.8b, calibrated", "degraded until
    /// kev-0.8b is downloaded", or "degraded tier (no decider configured)" when the slot is unbound.
    /// </summary>
    public static string StatusDecider(CorvidRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        var model = runtime.Options.Models.EffectiveSlots().GetValueOrDefault(ModelSlots.Decider)?.Model;
        if (runtime.Fabric.CalibratedDecider is { } calibrated)
            return $"{(model is { Length: > 0 } ? model : calibrated)}, calibrated";
        return model is { Length: > 0 } ? $"degraded until {model} is downloaded" : "degraded tier (no decider configured)";
    }

    private DeciderStatus Decider(CorvidOptions options, IReadOnlyDictionary<string, SlotSettings> slots, Dictionary<string, ModelAvailability> availability,
        CorvidRuntime? runtime)
    {
        if (!slots.TryGetValue(ModelSlots.Decider, out var decider) || decider.Model is not { Length: > 0 } model)
        {
            return new DeciderStatus("", "no decider", DeciderTier.Degraded, "none: no decider model is configured, so every gate uses the degraded tier",
                "none", "the degraded tier (generator and embedder)", Profile(options, runtime));
        }

        var ready = availability.TryGetValue(model, out var found) && found.IsReady;
        var calibrated = runtime is not null ? runtime.Fabric.CalibratedDecider is not null : ready;
        string floors;
        if (!ready)
            floors = $"none until {model} is downloaded; every gate uses the degraded tier";
        else if (runtime is null)
            floors = "calibrated floors apply once Corvid starts";
        else
            floors = runtime.Registry.TryGet(ModelSlots.Decider, out var bound) && runtime.Metrics.TryGet(ModelSlots.Decider, bound.Default.Profile.Id, out var latency)
                && latency.P50 is { } median
                ? string.Create(CultureInfo.InvariantCulture, $"calibrated · p50 {Math.Round(median.TotalMilliseconds):0} ms on {runtime.DeciderPlacement}")
                : $"calibrated · on {runtime.DeciderPlacement ?? "cpu"}, no decisions yet";
        var second = decider.Escalation.FirstOrDefault();
        var secondTier = second is null
            ? "none: gates below their floor ask you (Corvid:Models:Slots:decider:Escalation adds one)"
            : availability.TryGetValue(second, out var tier) && tier.IsReady
                ? $"{second} for gates below their floor"
                : $"{second} for gates below their floor, when it is downloaded";
        return new DeciderStatus(model, model, calibrated ? DeciderTier.Calibrated : DeciderTier.Degraded, floors, secondTier,
            "the degraded tier (generator and embedder) for states over the hard stop", Profile(options, runtime));
    }

    private static string Profile(CorvidOptions options, CorvidRuntime? runtime) =>
        runtime is not null
            ? runtime.Fabric.Reduction is null ? "full checkpoints" : "reduced checkpoints (C-030)"
            : options.Turn.ReducedCheckpoints switch
            {
                ReducedCheckpoints.On => "reduced checkpoints (C-030)",
                ReducedCheckpoints.Off => "full checkpoints",
                _ => "full checkpoints, reduced only while Kev runs on the CPU (C-030)",
            };

    /// <summary>Each GPU the doctor lists, with the driver and CUDA runtime checks beside it; none without a driver.</summary>
    private static List<GpuDevice> Gpus(IReadOnlyList<Corvid.Host.Diagnostics.DoctorCheck> checks)
    {
        var shared = checks.Where(check => check.Name is "driver" or "cuda runtime" or "onnx runtime")
            .Select(check => new DeviceCheck(check.Name, check.Fix is null, check.Detail))
            .ToList();
        var gpus = new List<GpuDevice>();
        foreach (var check in checks.Where(check => check.Name.StartsWith("GPU ", StringComparison.Ordinal)))
        {
            var index = check.Name[4..];
            gpus.Add(new GpuDevice($"cuda:{index}", check.Detail, shared));
        }

        return gpus;
    }

    /// <summary>The models that can be placed on a GPU (Kev, C-029) and any the configuration places, with their fallback.</summary>
    private List<AppPlacement> Placements(CorvidOptions options)
    {
        var names = _catalogue.Models.Where(model => model.Provider == StarlingModelProvider.ProviderName && StarlingModelProvider.CanPlaceOnCuda(model.Name))
            .Select(model => model.Name)
            .Concat(options.Models.Placement.Keys.Where(name => _catalogue.TryFind(name, out _)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal);
        return
        [
            .. names.Select(name =>
            {
                var settings = options.Models.Placement.FirstOrDefault(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)).Value
                    ?? new ModelPlacementSettings();
                var device = settings.Device.StartsWith("cuda", StringComparison.Ordinal) ? "cuda" : settings.Device;
                return new AppPlacement(name, device, settings.Fallback.ToString().ToLowerInvariant(), NoticeOnFallback: settings.Fallback == PlacementFallback.Cpu);
            }),
        ];
    }

    // ---- Helpers ----

    /// <summary>Switches the running runtime: new sessions start on the model, and the window's open session moves to it.</summary>
    private static async Task<string> SwitchLiveAsync(CorvidRuntime runtime, string slot, string model, string? sessionId, CancellationToken cancellationToken)
    {
        ModelBinding binding;
        try
        {
            binding = runtime.Bind(slot, model);
        }
        catch (Exception exception) when (exception is ModelUnavailableException or ArgumentException or InvalidOperationException)
        {
            return $"It could not load now ({E(exception.Message)}), so it applies when Corvid next starts.";
        }

        if (runtime.Registry.TryGet(slot, out var configured))
        {
            runtime.Registry.Bind(slot, binding,
                configured.Escalation.Where(step => !string.Equals(step.Profile.Id, binding.Profile.Id, StringComparison.Ordinal)),
                configured.Fallbacks.Where(step => !string.Equals(step.Profile.Id, binding.Profile.Id, StringComparison.Ordinal)));
        }
        else
        {
            runtime.Registry.Bind(slot, binding);
        }

        if (sessionId is null || !runtime.TryGetSession(sessionId, out var session))
            return "New sessions in this window start with it.";
        var switched = await session.Actor.SwitchModelAsync(slot, binding, session.Slots.IsPinned(slot), cancellationToken).ConfigureAwait(false);
        return switched.Status switch
        {
            SwitchStatus.Switched => $"New sessions start with it, and {E(sessionId)} switches after its issued tool calls.",
            SwitchStatus.Unchanged => $"New sessions start with it; {E(sessionId)} already uses it.",
            SwitchStatus.Refused => $"New sessions start with it; the privacy gate kept {E(sessionId)} where it was: {E(switched.Detail)}",
            _ => $"New sessions start with it; {E(sessionId)} stays where it was ({E(switched.Detail)}).",
        };
    }

    /// <summary>
    /// The edit that sets a slot's ladder. An empty ladder removes the key, and in a slot the file names no model for (it
    /// runs its default) with no fallbacks it removes the slot, since a slot left with neither a model nor a ladder is unbound.
    /// </summary>
    private ConfigEdit Ladder(string slot, IReadOnlyList<string> ladder, SlotSettings settings)
    {
        var key = $"{CorvidOptions.Section}:Models:Slots:{slot}";
        if (ladder.Count > 0)
            return new ConfigEdit($"{key}:Escalation", new JsonArray([.. ladder.Select(step => (JsonNode)JsonValue.Create(step))]));
        var fileModel = _writer.NextStart()[$"{key}:Model"];
        return string.IsNullOrWhiteSpace(fileModel) && settings.Fallbacks.Count == 0 && ModelOptions.DefaultSlots.ContainsKey(slot)
            ? ConfigEdit.Remove(key)
            : ConfigEdit.Remove($"{key}:Escalation");
    }

    /// <summary>Asks a server's <c>/models</c>: whether it answered, in how long, and the model ids it lists.</summary>
    private async Task<(ServerCheck Check, IReadOnlyList<string> Models)> ProbeAsync(string name, Uri endpoint, CancellationToken cancellationToken)
    {
        var url = new Uri(endpoint.ToString().TrimEnd('/') + "/models");
        var started = _time.GetUtcNow();
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var elapsed = (int)Math.Round((_time.GetUtcNow() - started).TotalMilliseconds);
            if (!response.IsSuccessStatusCode)
                return (new ServerCheck(false, $"{E(name)} answered {(int)response.StatusCode} {E(response.ReasonPhrase)} at `{E(url.ToString())}`."), []);
            var ids = new List<string>();
            try
            {
                using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
                if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                    foreach (var item in data.EnumerateArray())
                        if (item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } text)
                            ids.Add(text);
            }
            catch (JsonException)
            {
                // It answered, but not with a model list.
            }

            var listed = ids.Count == 1 ? $"serves {E(ids[0])}" : $"serves {ids.Count} models";
            return (new ServerCheck(true, string.Create(CultureInfo.InvariantCulture, $"{E(name)} answered in {elapsed} ms · {listed}.")), ids);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var reason = exception is TaskCanceledException ? $"no answer within {ServerTimeout.TotalSeconds:0} s" : exception.Message;
            return (new ServerCheck(false, $"{E(name)} did not answer at `{E(url.ToString())}`: {E(reason)}."), []);
        }
    }

    private CorvidRuntime? Started() => _engine.Runtime.IsCompletedSuccessfully ? _engine.Runtime.Result : null;

    private CorvidOptions NextStartOptions() => Options(_writer.NextStart());

    private static CorvidOptions Options(IConfiguration configuration) =>
        configuration.GetSection(CorvidOptions.Section).Get<CorvidOptions>() ?? new CorvidOptions();

    private bool Validated(CorvidOptions options)
    {
        try
        {
            return _validator.Validate(null, options).Succeeded;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>A value from outside put into a message (markup): its marks are text.</summary>
    private static string E(string? value) => Spans.Escape(SafeText.Clean(value ?? ""));

    private static ConfigWrite Written(string message) => new(true, message);

    private static ConfigWrite Refused(string message) => new(false, message);
}
