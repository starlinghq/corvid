using System.Text.Json.Nodes;
using Corvid.App.Core;
using Corvid.App.Core.Mvvm;
using Corvid.App.Core.Shell;
using Corvid.App.Engine.Ops;
using Corvid.Host.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Corvid.App.Engine.Hosting;

/// <summary>
/// U-8 over the engine: the app hosts the gateway and the scheduler only when the person asks (both off by default). Start
/// and Stop hosting act at once (<see cref="AppEngine.SetHostingAsync"/>) and are remembered as <c>Corvid:App:HostGateway</c>
/// and <c>Corvid:App:HostScheduler</c> through the config writer, so the next start does the same; a <c>corvid.json</c> that
/// cannot be written still lets this run host, and says so. <see cref="Changed"/> follows the engine's hosting (a take-over
/// on its timer, the gateway's start and stop), posted to the UI thread.
/// </summary>
public sealed class EngineHostingService : IHostingService, IDisposable
{
    private readonly AppEngine _engine;
    private readonly ChangeSignal _changed;

    public EngineHostingService(AppEngine engine, IDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _engine = engine;
        _changed = new ChangeSignal(dispatcher, () => Changed?.Invoke(this, EventArgs.Empty));
        _engine.HostingChanged += OnHostingChanged;
    }

    public string? Unavailable => null;

    public HostingState State => _engine.HostingState;

    public event EventHandler? Changed;

    /// <summary>The setting that remembers a part's hosting.</summary>
    public static string SettingKey(HostedPart part) =>
        part == HostedPart.Gateway ? "Corvid:App:HostGateway" : "Corvid:App:HostScheduler";

    public async Task<string> SetAsync(HostedPart part, bool on, CancellationToken cancellationToken)
    {
        await _engine.SetHostingAsync(part, on).ConfigureAwait(false);
        var key = SettingKey(part);
        var saved = await Task.Run(() =>
        {
            var writer = _engine.Services.GetRequiredService<CorvidConfigWriter>();
            var result = writer.Apply(new ConfigEdit(key, JsonValue.Create(on)));
            return result.Written
                ? (result.ShadowedKeys.Length > 0 ? $" {key} is saved, but this run's environment or --set overrides it at the next start." : $" Saved as {key} for the next start.")
                : $" {key} could not be saved ({SafeText.Clean(string.Join(" ", result.Failures))}), so the next start does not remember it.";
        }, CancellationToken.None).ConfigureAwait(false);
        _changed.Raise();
        return Said(part, on) + saved;
    }

    private string Said(HostedPart part, bool on)
    {
        var state = _engine.HostingState;
        if (!on)
            return part == HostedPart.Gateway
                ? "This app stopped hosting the gateway: its adapters are disconnected and its lock released."
                : "This app stopped hosting the scheduler: no job runs here, and its lock is released.";
        if (state.Here(part))
            return part == HostedPart.Gateway ? "This app hosts the gateway now." : "This app hosts the scheduler now.";
        return part == HostedPart.Gateway
            ? "This app is to host the gateway, but it is not running here yet: the Host panel says why (another process, or no adapter enabled)."
            : "This app is to host the scheduler, but it is not running here yet: the Scheduler panel says why (another process holds it, or Corvid:Scheduler:Enabled is off).";
    }

    private void OnHostingChanged() => _changed.Raise();

    public void Dispose()
    {
        _engine.HostingChanged -= OnHostingChanged;
        _changed.Dispose();
    }
}
