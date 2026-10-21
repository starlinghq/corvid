using Corvid.Host.Models;
using Corvid.Models;
using Microsoft.Extensions.Options;

namespace Corvid.Host.Configuration;

/// <summary>
/// Validates <see cref="CorvidOptions"/> when the host starts, so a typo in a slot or model name fails before any model
/// loads rather than in the middle of a turn. Every problem is reported at once.
/// </summary>
public sealed class CorvidOptionsValidator(ModelCatalogue catalogue, IEnumerable<IModelProvider>? providers = null) : IValidateOptions<CorvidOptions>
{
    public ValidateOptionsResult Validate(string? name, CorvidOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();

        var slots = options.Models.EffectiveSlots();
        foreach (var slot in options.Models.Slots.Keys.Where(slot => !ModelSlots.All.Contains(slot, StringComparer.Ordinal)))
            errors.Add($"Corvid:Models:Slots: '{slot}' is not a slot; the slots are {string.Join(", ", ModelSlots.All)}.");
        foreach (var (slot, settings) in slots.Where(pair => ModelSlots.All.Contains(pair.Key, StringComparer.Ordinal)))
        {
            CheckModel(errors, slot, "Model", settings.Model!);
            foreach (var model in settings.Escalation)
                CheckModel(errors, slot, "Escalation", model);
            foreach (var model in settings.Fallbacks)
                CheckModel(errors, slot, "Fallbacks", model);
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

        // Remote models (C-015) that could not be described are left out of the catalogue; say why.
        foreach (var remote in (providers ?? []).OfType<RemoteModelProvider>())
            errors.AddRange(remote.Problems);
        var privacy = options.Privacy;
        if (!Enum.IsDefined(privacy.Cloud) || !Enum.IsDefined(privacy.Tainted) || privacy.LocalServer is { } local && !Enum.IsDefined(local))
            errors.Add("Corvid:Privacy: Cloud, LocalServer and Tainted are Send, Redact or Refuse.");
        if (privacy.Threshold is < 0 or > 1 || float.IsNaN(privacy.Threshold))
            errors.Add("Corvid:Privacy:Threshold must be between 0 and 1.");
        if (privacy.Labels.Concat(privacy.ExtraLabels).Any(string.IsNullOrWhiteSpace))
            errors.Add("Corvid:Privacy: labels cannot be empty.");

        if (options.Devices.CpuThreads is < 1 or > 64)
            errors.Add("Corvid:Devices:CpuThreads must be between 1 and 64.");
        if (!Enum.IsDefined(options.Approvals.Mode))
            errors.Add("Corvid:Approvals:Mode must be Manual, Smart or Permissive.");
        if (options.Approvals.TimeoutSeconds is < 1 or > 86_400)
            errors.Add("Corvid:Approvals:TimeoutSeconds must be between 1 and 86400.");
        if (options.Turn.MaxSteps is < 1 or > 64)
            errors.Add("Corvid:Turn:MaxSteps must be between 1 and 64.");
        if (options.Turn.MaxOutputTokens is < 16 or > 65_536)
            errors.Add("Corvid:Turn:MaxOutputTokens must be between 16 and 65536.");
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

    private void CheckModel(List<string> errors, string slot, string field, string model)
    {
        if (!catalogue.TryFind(model, out var descriptor))
            errors.Add($"Corvid:Models:Slots:{slot}:{field}: no model is named '{model}'; known models: " +
                $"{string.Join(", ", catalogue.Models.Select(item => item.Name))}.");
        else if (!descriptor.Serves(slot))
            errors.Add($"Corvid:Models:Slots:{slot}:{field}: '{model}' cannot serve {slot}; it serves {string.Join(", ", descriptor.Slots)}.");
    }
}
