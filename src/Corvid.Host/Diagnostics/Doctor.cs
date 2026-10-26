using System.Net.Sockets;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Models;
using Corvid.Models.Starling;
using Corvid.Vault.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace Corvid.Host.Diagnostics;

public enum CheckStatus
{
    Ok,
    Info,
    Warning,
    Failed,
}

/// <param name="Fix">What to do about it, when something can be done.</param>
public sealed record DoctorCheck(string Area, string Name, CheckStatus Status, string Detail, string? Fix = null);

/// <summary>
/// <c>corvid doctor</c>: what Corvid needs and whether it is there, without loading a model or touching the network.
/// Configuration, the Starling model cache, bubblewrap and <c>corvid-sandbox</c>, the Vault socket, and the glibc setting
/// GLiNER2 needs. With the shell's network on (V-003b) it also runs <c>corvid-sandbox --help</c> and opens and closes one
/// Vault sandbox proxy.
/// </summary>
public sealed class Doctor(IConfiguration configuration, ModelCatalogue catalogue, IValidateOptions<CorvidOptions> validator,
    CorvidConfigFile? configFile = null)
{
    public const string MallocVariable = "MALLOC_MMAP_THRESHOLD_";
    public const string MallocValue = "65536";

    /// <param name="thorough">Hash every model file (about a second per 500 MB) instead of checking sizes.</param>
    public async Task<IReadOnlyList<DoctorCheck>> RunAsync(bool thorough = false, CancellationToken cancellationToken = default)
    {
        var checks = new List<DoctorCheck>();
        var options = configuration.GetSection(CorvidOptions.Section).Get<CorvidOptions>() ?? new CorvidOptions();

        var file = configFile?.Path ?? CorvidPaths.UserConfigFile;
        var validation = validator.Validate(null, options);
        checks.Add(validation.Failed
            ? new DoctorCheck("config", "configuration", CheckStatus.Failed, string.Join(" ", validation.Failures ?? []), $"Edit {file}.")
            : new DoctorCheck("config", "configuration", CheckStatus.Ok, File.Exists(file) ? $"valid ({file})" : $"defaults (no {file})"));
        checks.Add(DataDirectory(options));

        var configured = options.Models.EffectiveSlots()
            .SelectMany(pair => new[] { pair.Value.Model! }.Concat(pair.Value.Escalation).Concat(pair.Value.Fallbacks).Select(model => (Slot: pair.Key, Model: model)))
            .Where(pair => catalogue.TryFind(pair.Model, out _))
            .GroupBy(pair => catalogue.Find(pair.Model).Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => string.Join(", ", group.Select(pair => pair.Slot).Distinct()), StringComparer.OrdinalIgnoreCase);
        // Hashing reads gigabytes; do it off the caller's thread.
        var models = await Task.Run(() => catalogue.Models.Select(model => catalogue.Check(model.Name, thorough)).ToArray(), cancellationToken)
            .ConfigureAwait(false);
        foreach (var availability in models)
        {
            var slots = configured.GetValueOrDefault(availability.Model.Name);
            // A missing decider model is a warning: Corvid runs on the degraded tier without it (C-004b).
            var status = availability.IsReady ? CheckStatus.Ok
                : slots is null ? CheckStatus.Info
                : slots == ModelSlots.Decider ? CheckStatus.Warning
                : CheckStatus.Failed;
            var role = slots is null ? "not configured" : slots;
            checks.Add(new DoctorCheck("models", availability.Model.Name, status,
                availability.IsReady ? $"ready ({role})" : $"{availability.Readiness.ToString().ToLowerInvariant()} ({role}). {availability.Detail}",
                availability.IsReady ? null : availability.Acquire));
        }
        checks.Add(Decider(options, models));
        checks.Add(Malloc());
        checks.AddRange(Gpu());

        checks.Add(Bubblewrap());
        checks.Add(SandboxLauncher(options));
        checks.Add(await NetworkEgressAsync(options, cancellationToken).ConfigureAwait(false));
        checks.Add(Git(options));
        checks.Add(await VaultAsync(options, cancellationToken).ConfigureAwait(false));
        checks.Add(SkillsGit(options));
        checks.AddRange(Mcp(options));
        checks.Add(App(AppContext.BaseDirectory, FindOnPath));
        return checks;
    }

    /// <summary>The command that installs the windowed app, as the doctor and the README give it.</summary>
    public const string AppInstall = "curl -fsSL https://raw.githubusercontent.com/starlinghq/corvid/main/install.sh | sh -s -- --app";

    /// <summary>
    /// U-7, U-9: the windowed app (<c>corvid-app</c>), optional, so this only informs: beside this binary (the app's own
    /// directory), in the <c>app/</c> directory install.sh puts beside corvid, or on PATH; else how to install it.
    /// </summary>
    internal static DoctorCheck App(string baseDirectory, Func<string, string?> onPath)
    {
        ArgumentNullException.ThrowIfNull(baseDirectory);
        ArgumentNullException.ThrowIfNull(onPath);
        foreach (var candidate in new[] { Path.Combine(baseDirectory, "corvid-app"), Path.Combine(baseDirectory, "app", "corvid-app") })
        {
            if (File.Exists(candidate))
                return new DoctorCheck("app", "corvid-app", CheckStatus.Info, $"installed ({candidate})");
        }
        return onPath("corvid-app") is { } found
            ? new DoctorCheck("app", "corvid-app", CheckStatus.Info, $"installed ({found})")
            : new DoctorCheck("app", "corvid-app", CheckStatus.Info, "the windowed app is not installed (it is optional)",
                $"Install it beside corvid with install.sh: {AppInstall}");
    }

    private static DoctorCheck DataDirectory(CorvidOptions options)
    {
        var directory = CorvidPaths.DataDirectory(options);
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".doctor-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return new DoctorCheck("config", "data directory", CheckStatus.Ok, $"writable ({directory})");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DoctorCheck("config", "data directory", CheckStatus.Failed, $"{directory}: {exception.Message}",
                "Set Corvid:DataDirectory to a writable directory.");
        }
    }

    /// <summary>Which tier answers the fabric's questions with this configuration and cache (plan §5.5, §5.6).</summary>
    private DoctorCheck Decider(CorvidOptions options, IReadOnlyList<ModelAvailability> models)
    {
        const string degraded = "degraded tier: gates (risk, memory_op, skill_write_safe) ask the user and memory writes are staged";
        if (!options.Models.EffectiveSlots().TryGetValue(ModelSlots.Decider, out var settings) || !catalogue.TryFind(settings.Model!, out var primary))
            return new DoctorCheck("models", ModelSlots.Decider, CheckStatus.Info, $"{degraded} (no decider model is configured).");
        ModelAvailability? Find(string name) =>
            models.FirstOrDefault(item => string.Equals(item.Model.Name, name, StringComparison.OrdinalIgnoreCase));
        if (Find(primary.Name) is not { IsReady: true })
            return new DoctorCheck("models", ModelSlots.Decider, CheckStatus.Warning, $"{degraded} until {primary.Name} is downloaded.",
                $"corvid model download {primary.Name}");
        var second = settings.Escalation.FirstOrDefault();
        if (second is null)
            return new DoctorCheck("models", ModelSlots.Decider, CheckStatus.Ok,
                $"{primary.Name}, calibrated. Gates below their floor ask the user; a second tier (kev-4b) is optional: " +
                $"Corvid:Models:Slots:{ModelSlots.Decider}:Escalation.");
        return Find(second) is { IsReady: true }
            ? new DoctorCheck("models", ModelSlots.Decider, CheckStatus.Ok, $"{primary.Name}, calibrated, with {second} for gates below their floor.")
            : new DoctorCheck("models", ModelSlots.Decider, CheckStatus.Warning,
                $"{primary.Name}, calibrated; the second tier {second} is not downloaded, so gates below their floor ask the user.",
                $"corvid model download {second}");
    }

    // glibc reads its malloc tunables when the process starts, so the variable must be in the environment before then;
    // the corvid executable re-executes itself with it set (GlibcMalloc).
    private static DoctorCheck Malloc()
    {
        if (!OperatingSystem.IsLinux())
            return new DoctorCheck("models", MallocVariable, CheckStatus.Info, "not needed on this platform");
        var value = Environment.GetEnvironmentVariable(MallocVariable);
        return value == MallocValue
            ? new DoctorCheck("models", MallocVariable, CheckStatus.Ok, $"{MallocValue}, as Starling requires for GLiNER2 and Kev on Linux")
            : new DoctorCheck("models", MallocVariable, CheckStatus.Warning,
                $"{(value is null ? "not set" : $"set to {value}")} in this process; Starling requires {MallocValue} for GLiNER2 and Kev on Linux. " +
                "`corvid chat` re-executes itself with it set.",
                $"export {MallocVariable}={MallocValue} for other hosts of the extractor or the decider.");
    }

    /// <summary>
    /// C-029: the GPU, for models placed on CUDA (<c>Corvid:Models:Placement</c>). The NVIDIA driver and each GPU through
    /// NVML, whether this build's ONNX Runtime has the CUDA provider, which CUDA libraries resolve for this user, and the
    /// device-order variables. With nothing placed on CUDA every line is Info. With a model placed there, a missing
    /// prerequisite is Failed under <see cref="PlacementFallback.Fail"/> and a Warning under <see cref="PlacementFallback.Cpu"/>.
    /// Nothing is spawned and no model or CUDA context is created.
    /// </summary>
    private IEnumerable<DoctorCheck> Gpu()
    {
        var placed = new List<(string Model, string Device, int Ordinal, PlacementFallback Fallback)>();
        foreach (var (name, settings) in CorvidHost.ReadPlacement(configuration))
            if (ModelPlacement.TryParse(settings.Device, out var device, out var ordinal) && ordinal is { } cuda && catalogue.TryFind(name, out var model))
                placed.Add((model.Name, device, cuda, settings.Fallback));
        var missing = placed.Count == 0 ? CheckStatus.Info
            : placed.Exists(item => item.Fallback == PlacementFallback.Fail) ? CheckStatus.Failed
            : CheckStatus.Warning;
        var ready = placed.Count == 0 ? CheckStatus.Info : CheckStatus.Ok;
        var problems = 0;
        DoctorCheck Check(string name, bool ok, string detail, string? fix = null)
        {
            if (!ok)
                problems++;
            return new DoctorCheck("gpu", name, ok ? ready : missing, detail, ok ? null : fix);
        }

        var probe = CudaProbe.Probe();
        yield return probe.NvmlProblem is { } nvml
            ? Check("driver", false, nvml, "Install the NVIDIA driver (it provides libnvidia-ml.so.1 and libcuda.so.1), or place the models on cpu.")
            : Check("driver", true, $"NVIDIA {probe.DriverVersion ?? "unknown"}, CUDA driver API {probe.CudaDriverVersion ?? "unknown"}");
        if (probe.NvmlProblem is null && probe.Devices.IsEmpty)
            yield return Check("devices", false, "NVML lists no GPU.", "Check nvidia-smi and the driver.");
        foreach (var device in probe.Devices)
            yield return new DoctorCheck("gpu", $"GPU {device.Index}", placed.Count == 0 ? CheckStatus.Info : CheckStatus.Ok,
                $"{device.Name} ({device.Uuid}), {device.UsedBytes >> 20} of {device.TotalBytes >> 20} MiB used");

        yield return KevDecider.HasCudaProvider(out var runtimeProblem) switch
        {
            true => Check("onnx runtime", true, "CUDA build: ONNX Runtime lists CUDAExecutionProvider"),
            false => Check("onnx runtime", false, "CPU build: ONNX Runtime has no CUDAExecutionProvider, so nothing can run on a GPU",
                "Publish the CUDA variant: dotnet publish src/Corvid.Cli -c Release -r linux-x64 -p:UseCuda=true -o out-cli-cuda."),
            null => Check("onnx runtime", false, $"ONNX Runtime did not load: {runtimeProblem}", "Reinstall corvid; its native libraries are missing."),
        };

        if (OperatingSystem.IsLinux())
        {
            // cuDNN is dlopened by ONNX Runtime's CUDA provider for the kernels that use it; Kev's graph and the tiny graph of
            // the GPU tests ran without it on the RTX 3090 (C-029), so its absence is reported, never a failure.
            var required = probe.Libraries.Where(library => library.Required).ToArray();
            var absent = probe.MissingLibraries.ToArray();
            yield return absent.Length == 0
                ? Check("cuda runtime", true, $"{string.Join(", ", required.Select(library => library.Name))} resolve for this user")
                : Check("cuda runtime", false, $"not found by the loader for this user: {string.Join(", ", absent)}",
                    "Install the CUDA 13 runtime (cudart, cuBLAS, cuRAND, cuFFT) where the loader finds it: the ldconfig cache or LD_LIBRARY_PATH.");
            yield return new DoctorCheck("gpu", "cudnn", CheckStatus.Info,
                probe.Libraries.Any(library => library.Name == CudaProbe.Cudnn && library.Resolves)
                    ? $"{CudaProbe.Cudnn} resolves"
                    : $"{CudaProbe.Cudnn} does not resolve; Kev runs on CUDA without it, other CUDA graphs may need it (ONNX Runtime loads it on demand)");
        }

        var order = $"CUDA_VISIBLE_DEVICES={probe.VisibleDevices ?? "(unset)"}, CUDA_DEVICE_ORDER={probe.DeviceOrder ?? "(unset)"}";
        yield return probe.OrderAmbiguous && placed.Count > 0
            ? new DoctorCheck("gpu", "device order", CheckStatus.Warning,
                $"{order}: with {probe.Devices.Length} GPUs CUDA numbers them fastest first, so cuda:0 may not be GPU 0 above.",
                "export CUDA_DEVICE_ORDER=PCI_BUS_ID (and CUDA_VISIBLE_DEVICES=<GPU UUID> to pin one).")
            : new DoctorCheck("gpu", "device order", placed.Count == 0 ? CheckStatus.Info : CheckStatus.Ok, order);

        foreach (var (model, device, ordinal, fallback) in placed)
            yield return PlacedModel(model, device, ordinal, fallback, problems, probe.VisibleDeviceCount, probe.VisibleDevices,
                fallback == PlacementFallback.Fail ? CheckStatus.Failed : CheckStatus.Warning);
    }

    /// <summary>
    /// The per-model gpu line: Ok only when no gpu check above missed and CUDA exposes the ordinal (review of #50, finding 1:
    /// NVML lists every GPU, so <c>cuda:1</c> with one visible device passed every other line and then failed to load with
    /// "Invalid device ID"). A miss is Failed under <see cref="PlacementFallback.Fail"/>, a Warning under <see cref="PlacementFallback.Cpu"/>.
    /// </summary>
    /// <param name="problems">How many gpu checks above missed.</param>
    /// <param name="visibleCount">How many devices CUDA exposes (<see cref="CudaProbeResult.VisibleDeviceCount"/>); null when unknown.</param>
    internal static DoctorCheck PlacedModel(string model, string device, int ordinal, PlacementFallback fallback, int problems,
        int? visibleCount, string? visibleDevices, CheckStatus missing)
    {
        var absent = ordinal >= visibleCount;
        var ok = problems == 0 && !absent;
        var detail = $"{model} on {device}, fallback {fallback.ToString().ToLowerInvariant()}";
        if (absent)
            detail += $"; CUDA exposes {visibleCount} device{(visibleCount == 1 ? "" : "s")} " +
                $"(CUDA_VISIBLE_DEVICES={visibleDevices ?? "(unset)"}), so there is no {device}";
        if (!ok)
            detail += fallback == PlacementFallback.Cpu ? "; it will run on the CPU (checkpoints over 1 s)"
                : model.StartsWith("kev", StringComparison.OrdinalIgnoreCase) ? "; it will not load, so the fabric runs on the degraded tier"
                : "; it will not load";
        return new DoctorCheck("gpu", model, ok ? CheckStatus.Ok : missing, detail,
            !absent ? null
            : visibleCount == 0 ? "CUDA_VISIBLE_DEVICES hides every GPU: unset it or name a GPU index or UUID."
            : visibleCount == 1 ? $"Place {model} on cuda:0, or widen CUDA_VISIBLE_DEVICES."
            : $"Place {model} on cuda:0 to cuda:{visibleCount - 1}, or widen CUDA_VISIBLE_DEVICES.");
    }

    /// <summary>
    /// C-018: what the configured MCP servers need, without starting them (doctor uses no network and runs nothing): the
    /// sandbox for sandboxed stdio servers, Vault for servers with a secret, and a warning for each unconfined one.
    /// </summary>
    private static IEnumerable<DoctorCheck> Mcp(CorvidOptions options)
    {
        var (servers, _) = Composition.McpComposition.Definitions(options);
        foreach (var server in servers)
        {
            var http = server.Transport == Corvid.Mcp.McpTransportKind.Http;
            var kind = http ? $"http {server.Url}" : $"stdio {server.Command}";
            var trust = server.Trust.ToString().ToLowerInvariant();
            if (!http && !server.Sandboxed)
                yield return new DoctorCheck("mcp", server.Name, CheckStatus.Warning, $"{kind}, {trust}, runs unconfined",
                    "Remove Sandboxed=false unless you trust this server with everything your user can reach.");
            else if (!http && !OperatingSystem.IsLinux())
                yield return new DoctorCheck("mcp", server.Name, CheckStatus.Warning, $"{kind}: the sandbox runs on Linux only, so it will not start");
            else if (server.Secret is { } secret)
                yield return new DoctorCheck("mcp", server.Name, CheckStatus.Info, $"{kind}, {trust}, token from the Vault secret '{secret.Name}'",
                    $"corvid-vault set {secret.Name}, bound to {server.Url?.Host}");
            else
                yield return new DoctorCheck("mcp", server.Name, CheckStatus.Ok,
                    $"{kind}, {trust}{(server.Network ? ", network" : "")}; corvid mcp list starts it");
        }
    }

    // The skills directory is a git repository (C-017); without git, skills still load but no change can be recorded.
    private static DoctorCheck SkillsGit(CorvidOptions options)
    {
        if (!options.Skills.Enabled)
            return new DoctorCheck("skills", "git", CheckStatus.Info, "skills are off (Corvid:Skills:Enabled)");
        return Corvid.Skills.SkillRepository.IsGitAvailable(options.Skills.Git)
            ? new DoctorCheck("skills", "git", CheckStatus.Ok, $"{options.Skills.Git} runs; skills in {CorvidPaths.SkillsDirectory(options)}")
            : new DoctorCheck("skills", "git", CheckStatus.Warning, $"{options.Skills.Git} does not run; skills are off without it.",
                "Install git, or set Corvid:Skills:Git.");
    }

    private static DoctorCheck Bubblewrap()
    {
        if (!OperatingSystem.IsLinux())
            return new DoctorCheck("sandbox", "bubblewrap", CheckStatus.Warning, "the sandbox is Linux only; shell tools will refuse to run");
        var path = FindOnPath("bwrap") ?? (File.Exists("/usr/bin/bwrap") ? "/usr/bin/bwrap" : null);
        return path is null
            ? new DoctorCheck("sandbox", "bubblewrap", CheckStatus.Failed, "bwrap is not on PATH; sandboxed tools refuse to run without it.",
                "Install bubblewrap (apt install bubblewrap, dnf install bubblewrap, pacman -S bubblewrap).")
            : new DoctorCheck("sandbox", "bubblewrap", CheckStatus.Ok, path);
    }

    // The same resolution Corvid.Sandbox uses: configuration, then $CORVID_SANDBOX, then next to the executable.
    private static string LauncherPath(CorvidOptions options) =>
        options.Sandbox.LauncherPath is { Length: > 0 } configured ? CorvidPaths.ExpandHome(configured)
            : Environment.GetEnvironmentVariable("CORVID_SANDBOX") is { Length: > 0 } variable ? variable
            : Path.Combine(AppContext.BaseDirectory, "corvid-sandbox");

    private const string EgressFix = "Start corvid-vault 0.5 or later, or unset Corvid:Tools:ShellNetwork.";

    /// <summary>
    /// V-003b: with <c>Corvid:Tools:ShellNetwork</c> on, every shell command needs the launcher's proxy options and a Vault
    /// that opens sandbox proxies; without them a command refuses to run. Checked by the launcher's own usage text and a
    /// <c>sandbox_open</c>/<c>sandbox_close</c> round trip (no network, no command run).
    /// </summary>
    private static async Task<DoctorCheck> NetworkEgressAsync(CorvidOptions options, CancellationToken cancellationToken)
    {
        const string name = "network egress";
        if (!options.Tools.ShellNetwork)
            return new DoctorCheck("sandbox", name, CheckStatus.Info, "off: shell commands have no network (Corvid:Tools:ShellNetwork)");
        if (!OperatingSystem.IsLinux())
            return new DoctorCheck("sandbox", name, CheckStatus.Warning, "on, but the sandbox is Linux only; shell commands will refuse to run");

        var launcher = LauncherPath(options);
        if (await LauncherUsageAsync(launcher, cancellationToken).ConfigureAwait(false) is not { } usage || !usage.Contains("--proxy-socket", StringComparison.Ordinal))
            return new DoctorCheck("sandbox", name, CheckStatus.Failed,
                $"on, but {launcher} does not offer --proxy-socket; shell commands will refuse to run.",
                "Publish corvid-sandbox from this version next to corvid, or unset Corvid:Tools:ShellNetwork.");

        string socket;
        try
        {
            socket = options.Vault.SocketPath is { Length: > 0 } configured ? CorvidPaths.ExpandHome(configured) : VaultClient.DefaultSocketPath();
        }
        catch (InvalidOperationException exception)
        {
            return new DoctorCheck("sandbox", name, CheckStatus.Failed, $"on, but {exception.Message}", EgressFix);
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var client = new VaultClient($"corvid-doctor-{Environment.ProcessId}", socket);
            var open = await client.OpenSandboxAsync("shell", tainted: false, network: false, lifetimeMinutes: 1, timeout.Token).ConfigureAwait(false);
            await client.CloseSandboxAsync(open.SandboxId, timeout.Token).ConfigureAwait(false);
            return new DoctorCheck("sandbox", name, CheckStatus.Ok, $"on: HTTPS through Vault's sandbox proxy ({socket})");
        }
        catch (Exception exception) when (exception is VaultException or SocketException or IOException
            or System.Text.Json.JsonException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new DoctorCheck("sandbox", name, CheckStatus.Failed,
                $"on, but Vault at {socket} did not open a sandbox proxy ({exception.Message}); shell commands will refuse to run.", EgressFix);
        }
    }

    /// <summary>The launcher's --help text (printed on standard error), or null when it does not run.</summary>
    private static async Task<string?> LauncherUsageAsync(string launcher, CancellationToken cancellationToken)
    {
        if (!File.Exists(launcher))
            return null;
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(launcher, "--help")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null)
                return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static DoctorCheck SandboxLauncher(CorvidOptions options)
    {
        var path = LauncherPath(options);
        return File.Exists(path)
            ? new DoctorCheck("sandbox", "corvid-sandbox", CheckStatus.Ok, path)
            : new DoctorCheck("sandbox", "corvid-sandbox", CheckStatus.Failed, $"not found at {path}; sandboxed tools refuse to run without it.",
                "Publish it (dotnet publish sandbox/Corvid.Sandbox.Launcher -c Release -r linux-x64 -p:PublishAot=true) next to corvid, " +
                "or set CORVID_SANDBOX.");
    }

    // Checkpoints (C-014) run git from Corvid; without it writes still ask, but nothing can be undone.
    private static DoctorCheck Git(CorvidOptions options)
    {
        if (!options.Checkpoints.Enabled)
            return new DoctorCheck("checkpoints", "git", CheckStatus.Info, "checkpoints are off (Corvid:Checkpoints:Enabled); /undo and /rollback " +
                "move the conversation only, and refuse when a turn changed files.");
        var configured = CorvidPaths.ExpandHome(options.Checkpoints.GitPath);
        var path = configured.Contains('/') ? File.Exists(configured) ? configured : null : FindOnPath(configured);
        return path is null
            ? new DoctorCheck("checkpoints", "git", CheckStatus.Warning,
                $"{configured} was not found; changes to files are not reversible, and /undo and /rollback refuse to put files back.",
                "Install git, or set Corvid:Checkpoints:GitPath.")
            : new DoctorCheck("checkpoints", "git", CheckStatus.Ok, $"{path}; stores under {CorvidPaths.Checkpoints(options)}");
    }

    /// <summary>The GPU section of <see cref="RunAsync"/> alone (C-029), for a page that shows it beside placement.</summary>
    public IReadOnlyList<DoctorCheck> GpuChecks() => [.. Gpu()];

    /// <summary>The Vault socket check of <see cref="RunAsync"/> alone: whether something answers at the socket.</summary>
    public static Task<DoctorCheck> VaultCheckAsync(CorvidOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return VaultAsync(options, cancellationToken);
    }

    private static async Task<DoctorCheck> VaultAsync(CorvidOptions options, CancellationToken cancellationToken)
    {
        string path;
        try
        {
            path = options.Vault.SocketPath is { Length: > 0 } configured ? CorvidPaths.ExpandHome(configured) : VaultClient.DefaultSocketPath();
        }
        catch (InvalidOperationException exception)
        {
            return new DoctorCheck("vault", "socket", CheckStatus.Warning, exception.Message, "Set Corvid:Vault:SocketPath.");
        }
        if (!File.Exists(path))
            return new DoctorCheck("vault", "socket", CheckStatus.Warning,
                $"no Vault socket at {path}; tools with secret parameters are not offered without it.",
                "Start corvid-vault (vault/Corvid.Vault).");
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), timeout.Token).ConfigureAwait(false);
            return new DoctorCheck("vault", "socket", CheckStatus.Ok, $"listening at {path}");
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new DoctorCheck("vault", "socket", CheckStatus.Warning, $"{path} exists but nothing answers: {exception.Message}",
                "Start corvid-vault (vault/Corvid.Vault).");
        }
    }

    public static string? FindOnPath(string program)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, program);
            if (File.Exists(candidate) && (!OperatingSystem.IsLinux() || (File.GetUnixFileMode(candidate) & UnixFileMode.UserExecute) != 0))
                return candidate;
        }
        return null;
    }
}
