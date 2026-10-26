using Corvid.Host.Models;
using Corvid.Models;
using Microsoft.Extensions.Options;

namespace Corvid.Host.Configuration;

/// <summary>
/// Validates <see cref="CorvidOptions"/> when the host starts, so a typo in a slot or model name fails before any model
/// loads rather than in the middle of a turn. Every problem is reported at once.
/// <para>
/// The options are judged on their own terms, so the same validator serves the configuration the host started with and
/// any other (the file <see cref="CorvidConfigWriter"/> is about to save): the remote models (C-015) are the options' own
/// <c>Corvid:Models:Remote</c> entries, never the ones the host started with, and the in-process models come from the
/// catalogue. So a server added and bound to a slot in one edit passes, and a broken remote entry does not block the edit
/// that fixes it.
/// </para>
/// </summary>
public sealed class CorvidOptionsValidator(ModelCatalogue catalogue) : IValidateOptions<CorvidOptions>
{
    public ValidateOptionsResult Validate(string? name, CorvidOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        var models = new Models(catalogue, options.Models.Remote);

        var slots = options.Models.EffectiveSlots();
        foreach (var slot in options.Models.Slots.Keys.Where(slot => !ModelSlots.All.Contains(slot, StringComparer.Ordinal)))
            errors.Add($"Corvid:Models:Slots: '{slot}' is not a slot; the slots are {string.Join(", ", ModelSlots.All)}.");
        foreach (var (slot, settings) in slots.Where(pair => ModelSlots.All.Contains(pair.Key, StringComparer.Ordinal)))
        {
            CheckModel(errors, models, slot, "Model", settings.Model!);
            foreach (var model in settings.Escalation)
                CheckModel(errors, models, slot, "Escalation", model);
            foreach (var model in settings.Fallbacks)
                CheckModel(errors, models, slot, "Fallbacks", model);
            if (settings.Escalation.Concat(settings.Fallbacks).Contains(settings.Model, StringComparer.OrdinalIgnoreCase))
                errors.Add($"Corvid:Models:Slots:{slot}: the default model cannot also be its own escalation or fallback.");
        }
        // The decider has two tiers at most (§5.5), and the degraded tier is its fallback: a chain would never be used.
        if (slots.TryGetValue(ModelSlots.Decider, out var decider))
        {
            if (decider.Escalation.Count > 1)
                errors.Add($"Corvid:Models:Slots:{ModelSlots.Decider}:Escalation: at most one model, the second tier for gates below their floor.");
            if (decider.Fallbacks.Count > 0)
                errors.Add($"Corvid:Models:Slots:{ModelSlots.Decider}:Fallbacks: the decider falls back to the degraded tier; remove the fallbacks.");
        }
        // The degraded decider (plan §5.6) asks the generator yes/no questions and embeds choices: both are required.
        foreach (var required in new[] { ModelSlots.Generator, ModelSlots.TextEmbedder })
            if (!slots.ContainsKey(required))
                errors.Add($"Corvid:Models:Slots:{required} must be bound: the turn loop and the degraded decider need it.");

        // Remote models (C-015) that could not be described are left out of the catalogue; say why, for these options' own
        // entries (the provider the host started with reports the same for the configuration it was built from).
        errors.AddRange(RemoteModelProvider.ProblemsOf(options.Models.Remote).Distinct(StringComparer.Ordinal));
        // The catalogue refuses two providers naming one model, so the next start would fail on it.
        foreach (var remote in options.Models.Remote.Keys.Where(models.IsLocal).Order(StringComparer.Ordinal))
            errors.Add($"Corvid:Models:Remote:{remote}: an in-process model is named '{remote}' already; give the remote model another name.");
        var privacy = options.Privacy;
        if (!Enum.IsDefined(privacy.Cloud) || !Enum.IsDefined(privacy.Tainted) || privacy.LocalServer is { } local && !Enum.IsDefined(local))
            errors.Add("Corvid:Privacy: Cloud, LocalServer and Tainted are Send, Redact or Refuse.");
        if (privacy.Threshold is < 0 or > 1 || float.IsNaN(privacy.Threshold))
            errors.Add("Corvid:Privacy:Threshold must be between 0 and 1.");
        if (privacy.Labels.Concat(privacy.ExtraLabels).Any(string.IsNullOrWhiteSpace))
            errors.Add("Corvid:Privacy: labels cannot be empty.");

        if (options.Devices.CpuThreads is < 1 or > 64)
            errors.Add("Corvid:Devices:CpuThreads must be between 1 and 64.");
        ValidatePlacement(errors, models, options.Models.Placement);
        if (options.Turn.DualShortlistStateTokens is < 100 or > 600)
            errors.Add("Corvid:Turn:DualShortlistStateTokens must be between 100 and 600 (600 turns it off).");
        if (!Enum.IsDefined(options.Turn.ReducedCheckpoints))
            errors.Add("Corvid:Turn:ReducedCheckpoints must be Auto, On or Off.");
        if (!Enum.IsDefined(options.Approvals.Mode))
            errors.Add("Corvid:Approvals:Mode must be Manual, Smart or Permissive.");
        if (options.Approvals.TimeoutSeconds is < 1 or > 86_400)
            errors.Add("Corvid:Approvals:TimeoutSeconds must be between 1 and 86400.");
        if (options.Turn.MaxSteps is < 1 or > 64)
            errors.Add("Corvid:Turn:MaxSteps must be between 1 and 64.");
        if (options.Privacy.Ledger.RetainDays is < 0 or > 36_500)
            errors.Add("Corvid:Privacy:Ledger:RetainDays must be between 0 (any age) and 36500.");
        if (options.Privacy.Ledger.MaxMegabytes is < 0 or > 1_048_576)
            errors.Add("Corvid:Privacy:Ledger:MaxMegabytes must be between 0 (no cap) and 1048576.");
        if (options.Sessions.RecapAfterMinutes is < 0 or > 10_080)
            errors.Add("Corvid:Sessions:RecapAfterMinutes must be between 0 (off) and 10080 (a week).");
        if (options.Turn.MaxOutputTokens is < 16 or > 65_536)
            errors.Add("Corvid:Turn:MaxOutputTokens must be between 16 and 65536.");
        if (options.Turn.ImageTurns is < 1 or > 64)
            errors.Add("Corvid:Turn:ImageTurns must be between 1 and 64.");
        if (options.Telemetry.OtlpEndpoint is { Length: > 0 } endpoint
            && (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            errors.Add($"Corvid:Telemetry:OtlpEndpoint must be an absolute http or https URI, not '{endpoint}'.");
        if (options.Skills.Enabled && string.IsNullOrWhiteSpace(options.Skills.Git))
            errors.Add("Corvid:Skills:Git cannot be empty while skills are enabled.");
        if (string.IsNullOrWhiteSpace(options.Telemetry.ServiceName))
            errors.Add("Corvid:Telemetry:ServiceName cannot be empty.");
        if (string.IsNullOrWhiteSpace(options.Checkpoints.GitPath)
            || options.Checkpoints.GitPath.Contains('/') && !Path.IsPathFullyQualified(CorvidPaths.ExpandHome(options.Checkpoints.GitPath)))
            errors.Add("Corvid:Checkpoints:GitPath must be a program name found on PATH or an absolute path.");
        if (options.Checkpoints.MaxFileBytes < 1)
            errors.Add("Corvid:Checkpoints:MaxFileBytes must be positive.");
        if (options.Checkpoints.MaxFiles < 1)
            errors.Add("Corvid:Checkpoints:MaxFiles must be positive.");
        foreach (var (key, path) in new[]
                 {
                     ("DataDirectory", options.DataDirectory), ("Workspace", options.Workspace), ("Models:Cache", options.Models.Cache),
                     ("Approvals:RulesFile", options.Approvals.RulesFile), ("Vault:SocketPath", options.Vault.SocketPath),
                     ("Sandbox:LauncherPath", options.Sandbox.LauncherPath), ("Skills:Directory", options.Skills.Directory),
                 })
            if (path is { Length: > 0 } && !Path.IsPathFullyQualified(CorvidPaths.ExpandHome(path)))
                errors.Add($"Corvid:{key} must be an absolute path (or start with ~/), not '{path}'.");

        // C-018: every configured MCP server, checked as it would start (names, transports, URLs, Vault secrets, no secret
        // values in a server's environment, absolute paths, timeouts and limits).
        errors.AddRange(Composition.McpComposition.Definitions(options).Problems);

        ValidateGateway(errors, models, options.Gateway, options.Mcp.Servers.Keys);
        ValidateAgents(errors, models, options.Agents);

        // The file tools and the shell are confined to it, so it must be there before anything runs.
        if (options.Workspace is { Length: > 0 } workspace && Path.IsPathFullyQualified(CorvidPaths.ExpandHome(workspace))
            && !Directory.Exists(CorvidPaths.ExpandHome(workspace)))
            errors.Add($"Corvid:Workspace: '{workspace}' is not an existing directory.");

        // corvid serve (C-019): never tools for unauthenticated remote callers, so a non-loopback address needs a token.
        var serve = options.Serve;
        if (!Uri.TryCreate(serve.Url, UriKind.Absolute, out var listen) || listen.Scheme != "http" || listen.AbsolutePath != "/"
            || listen.UserInfo.Length > 0)
            errors.Add($"Corvid:Serve:Url must be an http URL with no path, such as http://127.0.0.1:8411, not '{serve.Url}' " +
                "(for TLS, put a reverse proxy in front).");
        else if (!listen.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) && listen.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6))
            errors.Add($"Corvid:Serve:Url must name an IP address or localhost, not '{listen.Host}'.");
        else if (!ServeSettings.IsLoopback(listen) && string.IsNullOrWhiteSpace(serve.TokenSecret))
            errors.Add($"Corvid:Serve:Url {serve.Url} is not a loopback address: set Corvid:Serve:TokenSecret to a Vault secret, " +
                "so remote callers must present it.");
        if (!Enum.IsDefined(serve.Approvals))
            errors.Add("Corvid:Serve:Approvals must be Fail or Deny.");
        if (serve.MaxRequestBytes is < 1024 or > 64 * 1024 * 1024)
            errors.Add("Corvid:Serve:MaxRequestBytes must be between 1024 and 67108864.");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }

    // The gateway (C-020): token secrets are Vault names, never values, and chat overrides name real models and slots.
    private static void ValidateGateway(List<string> errors, Models models, GatewaySettings gateway, IEnumerable<string> mcpServers)
    {
        var servers = mcpServers.ToHashSet(StringComparer.Ordinal);
        if (gateway.ConnectTimeoutSeconds is < 1 or > 600)
            errors.Add("Corvid:Gateway:ConnectTimeoutSeconds must be between 1 and 600.");
        if (gateway.ApprovalTimeoutSeconds is < 1 or > 86_400)
            errors.Add("Corvid:Gateway:ApprovalTimeoutSeconds must be between 1 and 86400.");
        foreach (var (name, adapter) in gateway.Adapters)
        {
            var key = $"Corvid:Gateway:Adapters:{name}";
            if (adapter is null)
                continue;
            if (name.Contains(':', StringComparison.Ordinal) || string.IsNullOrWhiteSpace(name))
                errors.Add($"{key}: an adapter name cannot be empty or contain ':'.");
            if (adapter.Enabled && Composition.CorvidGateway.Platforms.Contains(name))
            {
                if (adapter.Secret is not { } secret || !Vault.Contracts.Placeholders.IsValidSecretName(secret))
                    errors.Add($"{key}:Secret must name the Vault secret holding the bot token (lower-case letters, digits, '.', '-'), never the token.");
                if (name.Equals("slack", StringComparison.OrdinalIgnoreCase)
                    && (adapter.AppSecret is not { } app || !Vault.Contracts.Placeholders.IsValidSecretName(app)))
                    errors.Add($"{key}:AppSecret must name the Vault secret holding Slack's app-level token (xapp-) for Socket Mode.");
            }
            if (adapter.PollSeconds is < 1 or > 300)
                errors.Add($"{key}:PollSeconds must be between 1 and 300.");
            if (adapter.Owners.Concat(adapter.Guests).Concat(adapter.Channels).Any(string.IsNullOrWhiteSpace))
                errors.Add($"{key}: user and channel ids cannot be empty.");
            // Guest tools (C-020) are an allowlist by exact name. Whether a built-in or DI tool exists is known only once the
            // runtime has its registry, so the gateway checks that when it starts (CorvidGateway.GuestToolProblems), and there
            // too refuses a tool whose risk metadata declares SideEffects.Memory (the turn loop never offers one to a guest);
            // here, the shape, and that an MCP tool names a configured server (its tools are listed only once it runs).
            foreach (var tool in adapter.GuestTools)
                if (Composition.CorvidGateway.GuestToolShapeProblem(tool, servers) is { } problem)
                    errors.Add($"{key}:GuestTools: {problem}");
            foreach (var (chat, settings) in adapter.Chats)
            {
                foreach (var (slot, model) in settings?.Slots ?? [])
                {
                    if (!ModelSlots.All.Contains(slot, StringComparer.Ordinal))
                        errors.Add($"{key}:Chats:{chat}:Slots: '{slot}' is not a slot; the slots are {string.Join(", ", ModelSlots.All)}.");
                    else if (slot == ModelSlots.Decider)
                        errors.Add($"{key}:Chats:{chat}:Slots: the decider is process-wide and cannot be overridden per chat.");
                    else
                        CheckModel(errors, models, slot, $"(gateway chat {chat})", model);
                }
            }
        }
    }

    // A5: child sessions' ceilings, the allowlist's names and the named mixes, each as a child would bind it.
    private static void ValidateAgents(List<string> errors, Models models, AgentsSettings agents)
    {
        if (agents.MaxConcurrent is < 1 or > 10)
            errors.Add("Corvid:Agents:MaxConcurrent must be between 1 and 10.");
        if (agents.MaxDepth is < 0 or > 1)
            errors.Add("Corvid:Agents:MaxDepth must be 0 (no children) or 1 (children cannot delegate).");
        if (agents.MaxSteps is < 1 or > 64)
            errors.Add("Corvid:Agents:MaxSteps must be between 1 and 64.");
        if (agents.MaxTokens is < 1_000 or > 10_000_000)
            errors.Add("Corvid:Agents:MaxTokens must be between 1000 and 10000000.");
        if (agents.MaxSeconds is < 10 or > 86_400)
            errors.Add("Corvid:Agents:MaxSeconds must be between 10 and 86400.");
        foreach (var model in agents.Models)
            if (!models.TryFind(model, out _))
                errors.Add($"Corvid:Agents:Models: no model is named '{ModelMix.Escape(model ?? "")}'; known models: {models.Names}.");
        foreach (var name in agents.Mixes.Keys.Order(StringComparer.Ordinal))
        {
            ModelMix mix;
            try
            {
                mix = agents.Mix(name)!;
            }
            catch (ModelMixException exception)
            {
                errors.Add($"Corvid:Agents:Mixes:{ModelMix.Escape(name)}: {exception.Message}");
                continue;
            }
            foreach (var (slot, entry) in mix.Slots)
                foreach (var model in new[] { entry.Model }.Concat(entry.Ladder ?? []).Concat(entry.Fallbacks ?? []))
                {
                    if (!models.TryFind(model, out var descriptor))
                        errors.Add($"Corvid:Agents:Mixes:{ModelMix.Escape(name)}:{slot}: no model is named '{model}'; known models: {models.Names}.");
                    else if (!descriptor.Serves(slot))
                        errors.Add($"Corvid:Agents:Mixes:{ModelMix.Escape(name)}:{slot}: '{model}' cannot serve {slot}; it serves {string.Join(", ", descriptor.Slots)}.");
                    else if (agents.Models.Count > 0 && !agents.Models.Contains(descriptor.Name, StringComparer.OrdinalIgnoreCase))
                        errors.Add($"Corvid:Agents:Mixes:{ModelMix.Escape(name)}:{slot}: '{model}' is not in Corvid:Agents:Models.");
                }
        }
        // A5: named toolsets. Whether each tool exists is known only once the registry is; a child is cut to its parent's anyway.
        foreach (var (name, tools) in agents.Toolsets.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (string.Equals(name, Composition.CorvidRuntime.ReadOnlyToolset, StringComparison.OrdinalIgnoreCase))
                errors.Add($"Corvid:Agents:Toolsets:{name}: '{Composition.CorvidRuntime.ReadOnlyToolset}' is built in and cannot be configured.");
            else if (string.IsNullOrWhiteSpace(name) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
                errors.Add($"Corvid:Agents:Toolsets: '{ModelMix.Escape(name ?? "")}' is not a toolset name (letters, digits, '_' and '-').");
            foreach (var tool in tools ?? [])
                if (string.IsNullOrWhiteSpace(tool) || !tool.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.'))
                    errors.Add($"Corvid:Agents:Toolsets:{ModelMix.Escape(name ?? "")}: '{ModelMix.Escape(tool ?? "")}' is not a tool name.");
        }
    }

    // C-029: placement applies to in-process Starling models, and in v1 only Kev can go on CUDA.
    private static void ValidatePlacement(List<string> errors, Models models, IReadOnlyDictionary<string, ModelPlacementSettings> placement)
    {
        foreach (var (model, settings) in placement)
        {
            var key = $"Corvid:Models:Placement:{model}";
            if (settings is null)
                continue;
            if (!models.TryFind(model, out var descriptor))
            {
                errors.Add($"{key}: no model is named '{model}'; known models: {models.Names}.");
                continue;
            }
            if (descriptor.Provider != StarlingModelProvider.ProviderName)
            {
                errors.Add($"{key}: '{descriptor.Name}' is not an in-process Starling model; placement applies only to those " +
                    "(a remote model runs where its server does).");
                continue;
            }
            if (!ModelPlacement.TryParse(settings.Device, out var device, out _))
                errors.Add($"{key}:Device must be cpu, cuda or cuda:0 to cuda:{ModelPlacement.MaxCudaDevice}, not '{settings.Device}'.");
            else if (ModelPlacement.IsCuda(device) && !StarlingModelProvider.CanPlaceOnCuda(descriptor.Name))
                errors.Add($"{key}: CUDA placement for {descriptor.Name} is not built yet; only kev-0.8b and kev-4b can run on CUDA " +
                    "(see plan §18 C-029, Deferred).");
            if (!Enum.IsDefined(settings.Fallback))
                errors.Add($"{key}:Fallback must be Fail or Cpu.");
        }
    }

    private static void CheckModel(List<string> errors, Models models, string slot, string field, string model)
    {
        if (!models.TryFind(model, out var descriptor))
            errors.Add($"Corvid:Models:Slots:{slot}:{field}: no model is named '{model}'; known models: {models.Names}.");
        else if (!descriptor.Serves(slot))
            errors.Add($"Corvid:Models:Slots:{slot}:{field}: '{model}' cannot serve {slot}; it serves {string.Join(", ", descriptor.Slots)}.");
    }

    /// <summary>
    /// The models the options can name: the catalogue's in-process models and the options' own remote entries (those that
    /// describe a model), never the remote models the host started with.
    /// </summary>
    private sealed class Models(ModelCatalogue catalogue, IReadOnlyDictionary<string, RemoteModelSettings> remote)
    {
        private readonly Dictionary<string, ModelDescriptor> _remote =
            RemoteModelProvider.DescriptorsOf(remote).ToDictionary(model => model.Name, StringComparer.OrdinalIgnoreCase);

        public string Names => string.Join(", ", catalogue.Models.Where(model => model.Provider != RemoteModelProvider.ProviderName)
            .Concat(_remote.Values).Select(model => model.Name).Order(StringComparer.Ordinal));

        public bool IsLocal(string name) => catalogue.TryFind(name, out var model) && model.Provider != RemoteModelProvider.ProviderName;

        public bool TryFind(string name, out ModelDescriptor model)
        {
            if (!string.IsNullOrWhiteSpace(name) && _remote.TryGetValue(name, out model!))
                return true;
            return catalogue.TryFind(name, out model) && model.Provider != RemoteModelProvider.ProviderName;
        }
    }
}
