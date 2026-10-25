using System.Text.Json;
using System.Text.Json.Serialization;
using Corvid.Host.Commands;
using Corvid.Host.Composition;
using Corvid.Host.Models;
using Corvid.Host.Sessions;
using Corvid.Models;

namespace Corvid.Acp;

// A5: model switching over ACP, as the protocol's session configuration options (agentclientprotocol.com,
// protocol/session-config-options): session/new, load, resume and fork answer with the options, session/set_config_option
// sets one, and a config_option_update tells the client when a slot moved on its own (escalation, a fallback, the privacy
// gate, the turn's end) or was switched elsewhere (/model).

/// <summary>One value a select option offers.</summary>
internal sealed record ConfigOptionValue(string Value, string Name)
{
    public string? Description { get; init; }
}

/// <summary>A session configuration option (a select): the generator (<c>model</c>) and this session's decider.</summary>
internal sealed record ConfigOption(string Id, string Name, string Type, string CurrentValue, ConfigOptionValue[] Options)
{
    public string? Description { get; init; }

    public string? Category { get; init; }
}

internal sealed record SetConfigOptionRequest
{
    [JsonRequired]
    public string SessionId { get; init; } = "";

    [JsonRequired]
    public string ConfigId { get; init; } = "";

    public string? Type { get; init; }

    [JsonRequired]
    public JsonElement Value { get; init; }
}

/// <summary>session/set_config_option's answer, and session/load's and session/resume's: every option as it is now.</summary>
internal sealed record ConfigOptionsResponse
{
    public ConfigOption[]? ConfigOptions { get; init; }
}

/// <summary><c>config_option_update</c>: every option as it is now, pushed when one changed without the client asking.</summary>
internal sealed record ConfigOptionUpdate(ConfigOption[] ConfigOptions) : SessionUpdate;

internal static class AcpConfig
{
    public const string SetConfigOption = "session/set_config_option";

    /// <summary>The generator's option: the protocol's <c>model</c> category.</summary>
    public const string Model = "model";

    /// <summary>The decider's option: a custom category (underscore-prefixed, as the protocol asks).</summary>
    public const string Decider = "decider";

    /// <summary>The decider's value while nothing binds it (the degraded tier).</summary>
    public const string Degraded = "degraded";

    /// <summary>
    /// The options for a session: each slot's catalogue models that can serve it and are usable now, the one in use marked as
    /// current (offered even when it is not usable any more, so the current value is always among the options). A name that
    /// holds characters a terminal would hide is left out: it could read as another model's name.
    /// </summary>
    public static ConfigOption[] For(CorvidRuntime runtime, CorvidSession session) =>
    [
        Option(runtime, session, ModelSlots.Generator, Model, "Model", "model",
            "The generator for this session; it lasts until you change it (a running turn takes it at its next step). " +
            "`/model <name> --default` sets the default for new sessions."),
        Option(runtime, session, ModelSlots.Decider, Decider, "Decider", "_decider",
            "The decider (Kev) this session's checkpoints and approvals' risk question run on."),
    ];

    private static ConfigOption Option(CorvidRuntime runtime, CorvidSession session, string slot, string id, string name, string category, string description)
    {
        var current = session.Slots.TryCurrent(slot, out var binding) ? CatalogueName(runtime, binding.Profile.Id) : null;
        var values = new List<ConfigOptionValue>();
        foreach (var model in runtime.Catalogue.ForSlot(slot))
        {
            if (TerminalText.Visible(model.Name) != model.Name)
                continue;
            var isCurrent = string.Equals(model.Name, current, StringComparison.OrdinalIgnoreCase);
            if (!isCurrent && !runtime.Catalogue.Check(model.Name).IsReady)
                continue;
            values.Add(new ConfigOptionValue(model.Name, model.Name)
            {
                Description = model.Profile.Privacy == PrivacyClass.Cloud
                    ? "cloud: requests leave the machine through the egress gate"
                    : model.Profile.Privacy.ToString().ToLowerInvariant(),
            });
        }
        if (current is null || !values.Any(value => string.Equals(value.Value, current, StringComparison.OrdinalIgnoreCase)))
        {
            var shown = current ?? (slot == ModelSlots.Decider ? Degraded : "none");
            values.Insert(0, new ConfigOptionValue(TerminalText.Visible(shown), slot == ModelSlots.Decider && current is null ? "Degraded tier" : TerminalText.Visible(shown)));
            current = values[0].Value;
        }
        return new ConfigOption(id, name, "select", current, [.. values]) { Category = category, Description = description };
    }

    /// <summary>The catalogue name of a binding's profile id (<c>qwen3.5-0.8b@cuda0</c> is <c>qwen3.5-0.8b</c>).</summary>
    private static string CatalogueName(CorvidRuntime runtime, string profileId)
    {
        var bare = profileId.Split('@')[0];
        return runtime.Catalogue.TryFind(bare, out var model) ? model.Name : bare;
    }
}

internal sealed partial class AcpAgent
{
    private readonly Lock _configGate = new();
    private Task _configUpdates = Task.CompletedTask;
    private CorvidRuntime? _watched;

    /// <summary>session/set_config_option: switches this session's generator or decider (its choice for the session).</summary>
    private async Task<JsonRpcResult> SetConfigOptionAsync(SetConfigOptionRequest request, CancellationToken cancellationToken)
    {
        var runtime = await RuntimeAsync().ConfigureAwait(false);
        if (!_sessions.TryGetValue(request.SessionId, out var state))
            throw new AcpException(AcpErrors.ResourceNotFound, $"Session '{request.SessionId}' is not open; create, load or resume it first.");
        var slot = request.ConfigId switch
        {
            AcpConfig.Model => ModelSlots.Generator,
            AcpConfig.Decider => ModelSlots.Decider,
            _ => throw new AcpException(AcpErrors.InvalidParams, $"Corvid has no configuration option '{TerminalText.Visible(request.ConfigId)}'."),
        };
        if (request.Value.ValueKind != JsonValueKind.String || request.Value.GetString() is not { Length: > 0 } value)
            throw new AcpException(AcpErrors.InvalidParams, $"The {request.ConfigId} option takes one of its values, as a string.");
        var session = state.Session;
        var offered = AcpConfig.For(runtime, session).Single(option => option.Id == request.ConfigId);
        if (!offered.Options.Any(option => string.Equals(option.Value, value, StringComparison.Ordinal)))
            throw new AcpException(AcpErrors.InvalidParams, $"'{TerminalText.Visible(value)}' is not one of the {request.ConfigId} option's values.");
        if (!string.Equals(offered.CurrentValue, value, StringComparison.Ordinal))
        {
            try
            {
                if (slot == ModelSlots.Decider && value == AcpConfig.Degraded)
                    await session.ClearModelChoiceAsync(slot, cancellationToken).ConfigureAwait(false);
                else if (await session.SetModelAsync(slot, value, SwitchScope.Session, cancellationToken).ConfigureAwait(false) is { Status: SwitchStatus.Refused } refused)
                    throw new AcpException(AcpErrors.InvalidParams, $"The privacy gate refused {TerminalText.Visible(value)}: {TerminalText.Visible(refused.Detail ?? "")}");
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or ModelUnavailableException)
            {
                throw new AcpException(AcpErrors.InvalidParams, exception.Message);
            }
        }
        return Result(new ConfigOptionsResponse { ConfigOptions = AcpConfig.For(runtime, session) }, AcpJson.Default.ConfigOptionsResponse);
    }

    /// <summary>The options for a session this connection opened, for the setup responses.</summary>
    private ConfigOption[] ConfigOptions(CorvidRuntime runtime, CorvidSession session)
    {
        Watch(runtime);
        return AcpConfig.For(runtime, session);
    }

    // Live: a slot of a session open here moved (any surface, any reason). The event is raised inside the session's switch
    // lock, so the update is built and sent after it, one at a time, in order.
    private void Watch(CorvidRuntime runtime)
    {
        lock (_configGate)
        {
            if (_watched is not null)
                return;
            _watched = runtime;
        }
        runtime.ModelChanged += OnModelChanged;
    }

    private void Unwatch()
    {
        CorvidRuntime? runtime;
        lock (_configGate)
            (runtime, _watched) = (_watched, null);
        if (runtime is not null)
            runtime.ModelChanged -= OnModelChanged;
    }

    private void OnModelChanged(string sessionId, ModelChanged change)
    {
        if (change.Slot is not (ModelSlots.Generator or ModelSlots.Decider) || !_sessions.TryGetValue(sessionId, out var state))
            return;
        lock (_configGate)
        {
            if (_watched is not { } runtime)
                return;
            _configUpdates = _configUpdates.ContinueWith(_ =>
            {
                try
                {
                    _context.Update(sessionId, new ConfigOptionUpdate(AcpConfig.For(runtime, state.Session)));
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    _context.Log("corvid acp: could not report a model change: " + exception.Message);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }
}
