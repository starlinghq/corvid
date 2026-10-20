using System.Net.Sockets;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Models;
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
/// GLiNER2 needs.
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
            var status = availability.IsReady ? CheckStatus.Ok : slots is null ? CheckStatus.Info : CheckStatus.Failed;
            var role = slots is null ? "not configured" : slots;
            checks.Add(new DoctorCheck("models", availability.Model.Name, status,
                availability.IsReady ? $"ready ({role})" : $"{availability.Readiness.ToString().ToLowerInvariant()} ({role}). {availability.Detail}",
                availability.IsReady ? null : availability.Acquire));
        }
        checks.Add(new DoctorCheck("models", ModelSlots.Decider, CheckStatus.Info,
            "degraded tier: gates ask the user and memory writes are staged until Starlings.Models.Kev is published on nuget.org (C-004b)."));
        checks.Add(Malloc());

        checks.Add(Bubblewrap());
        checks.Add(SandboxLauncher(options));
        checks.Add(await VaultAsync(options, cancellationToken).ConfigureAwait(false));
        return checks;
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

    // glibc reads its malloc tunables when the process starts, so the variable must be in the environment before then;
    // the corvid executable re-executes itself with it set (GlibcMalloc).
    private static DoctorCheck Malloc()
    {
        if (!OperatingSystem.IsLinux())
            return new DoctorCheck("models", MallocVariable, CheckStatus.Info, "not needed on this platform");
        var value = Environment.GetEnvironmentVariable(MallocVariable);
        return value == MallocValue
            ? new DoctorCheck("models", MallocVariable, CheckStatus.Ok, $"{MallocValue}, as Starling requires for GLiNER2 on Linux")
            : new DoctorCheck("models", MallocVariable, CheckStatus.Warning,
                $"{(value is null ? "not set" : $"set to {value}")} in this process; Starling requires {MallocValue} for GLiNER2 on Linux. " +
                "`corvid chat` re-executes itself with it set.",
                $"export {MallocVariable}={MallocValue} for other hosts of the extractor.");
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

    private static DoctorCheck SandboxLauncher(CorvidOptions options)
    {
        // The same resolution Corvid.Sandbox uses: configuration, then $CORVID_SANDBOX, then next to the executable.
        var path = options.Sandbox.LauncherPath is { Length: > 0 } configured ? CorvidPaths.ExpandHome(configured)
            : Environment.GetEnvironmentVariable("CORVID_SANDBOX") is { Length: > 0 } variable ? variable
            : Path.Combine(AppContext.BaseDirectory, "corvid-sandbox");
        return File.Exists(path)
            ? new DoctorCheck("sandbox", "corvid-sandbox", CheckStatus.Ok, path)
            : new DoctorCheck("sandbox", "corvid-sandbox", CheckStatus.Failed, $"not found at {path}; sandboxed tools refuse to run without it.",
                "Publish it (dotnet publish sandbox/Corvid.Sandbox.Launcher -c Release -r linux-x64 -p:PublishAot=true) next to corvid, " +
                "or set CORVID_SANDBOX.");
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
