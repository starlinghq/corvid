using Corvid.App.Core.FirstRun;
using Corvid.App.Core.Models;
using Corvid.App.Core.Mvvm;
using Corvid.Host.Configuration;
using Corvid.Host.Diagnostics;
using Corvid.Host.Models;
using Corvid.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using AppCheck = Corvid.App.Core.FirstRun.DoctorCheck;
using AppStatus = Corvid.App.Core.FirstRun.CheckStatus;
using HostCheck = Corvid.Host.Diagnostics.DoctorCheck;
using HostStatus = Corvid.Host.Diagnostics.CheckStatus;

namespace Corvid.App.Engine.FirstRun;

/// <summary>
/// The first run over the engine (docs/plan/ui.md F1): <c>corvid doctor</c>'s checks (<see cref="Doctor"/>) against the
/// configuration the next start would read, the workspace the configuration names and the git repositories in the home
/// directory, the Hermes home an import would read (<see cref="HermesHome"/>), and the choices written to
/// <c>corvid.json</c> through the host's config writer. The status bar's engine line follows the runtime's start (K-4).
/// Models and downloads are the Models area's (<see cref="IModelService"/>), which the first run shares.
/// </summary>
public sealed class EngineFirstRunService : IFirstRunService, IDisposable
{
    private const int MaxSuggestions = 12;

    private readonly AppEngine _engine;
    private readonly IDispatcher _dispatcher;
    private readonly string _home;
    private readonly Func<string, string?> _environment;
    private readonly CorvidConfigWriter _writer;
    private int _disposed;

    /// <param name="home">The home directory for <c>~</c>, the repositories offered and the Hermes home; the user's by default.</param>
    /// <param name="environment">Reads an environment variable (<c>HERMES_HOME</c>); the process environment by default.</param>
    public EngineFirstRunService(AppEngine engine, IDispatcher dispatcher, string? home = null, Func<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(dispatcher);
        _engine = engine;
        _dispatcher = dispatcher;
        _home = home ?? CorvidPaths.Home;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _writer = engine.Services.GetRequiredService<CorvidConfigWriter>();
        engine.Progress += OnProgress;
        _ = engine.Runtime.ContinueWith(_ => Raise(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public event EventHandler? EngineStatusChanged;

    /// <summary>"in-process" once the runtime is up, "starting · Loading …" while it loads, "not started · …" when it cannot.</summary>
    public string? EngineStatus
    {
        get
        {
            if (_engine.StartFailure is not null)
            {
                var missing = _engine.MissingModels.Length;
                return missing > 0
                    ? $"not started · {missing} {(missing == 1 ? "model" : "models")} missing"
                    : "not started · the configuration is not valid";
            }

            var runtime = _engine.Runtime;
            if (runtime.IsCompletedSuccessfully)
                return "in-process";
            if (runtime.IsCompleted)
                return "not started · the start failed";
            return _engine.CurrentStep is { } step ? $"starting · {step.Description}" : "starting";
        }
    }

    public Task<DoctorReport> RunChecksAsync(CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        // As the next start would read the configuration: the setup's own writes count, which the host's start-up copy misses.
        var configuration = _writer.NextStart();
        var services = _engine.Services;
        var doctor = new Doctor(configuration, services.GetRequiredService<ModelCatalogue>(),
            services.GetRequiredService<IValidateOptions<CorvidOptions>>(), new CorvidConfigFile(_writer.Path));
        var checks = await doctor.RunAsync(thorough: false, cancellationToken).ConfigureAwait(false);
        var build = checks.FirstOrDefault(check => check is { Area: "gpu", Name: "onnx runtime" })?.Detail is { } runtime && runtime.Contains(':', StringComparison.Ordinal)
            ? runtime[..runtime.IndexOf(':', StringComparison.Ordinal)]
            : "";
        return new DoctorReport([.. checks.Select(Map).OfType<AppCheck>()], build);
    }, cancellationToken);

    public Task<IReadOnlyList<string>> SuggestWorkspacesAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var options = Options(_writer.NextStart());
        var found = new List<string> { Shown(CorvidPaths.Workspace(options)) };
        // Git repositories one and two levels under the home directory (~/corvid, ~/src/corvid), never inside a dot-folder
        // (where Corvid's own configuration and data live).
        var data = Path.GetFullPath(CorvidPaths.DataDirectory(options));
        foreach (var candidate in Children(_home).SelectMany(child => new[] { child }.Concat(Children(child))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (found.Count >= MaxSuggestions)
                break;
            if (Path.GetFullPath(candidate).StartsWith(data, StringComparison.Ordinal) || !Directory.Exists(Path.Combine(candidate, ".git")))
                continue;
            var shown = Shown(candidate);
            if (!found.Contains(shown, StringComparer.Ordinal))
                found.Add(shown);
        }

        return (IReadOnlyList<string>)found;
    }, cancellationToken);

    public Task<WorkspaceInfo> DescribeWorkspaceAsync(string path, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        path ??= "";
        var full = Expand(path.Trim());
        string description;
        if (full.Length == 0 || !Path.IsPathFullyQualified(full))
            description = "Not an absolute path: give one that starts with `/` or `~/`.";
        else if (!Directory.Exists(full))
            description = "There is no such folder yet: create it first, or choose another.";
        else if (InGitRepository(full))
            description = "A git repository: checkpoints and the project's lessons are on. Secrets locations in it (`.env*`, `*.pem`) stay hidden from tools.";
        else
            description = "Not a git repository: checkpoints use Corvid's own store, and there are no project lessons. Secrets locations in it (`.env*`, `*.pem`) stay hidden from tools.";
        return new WorkspaceInfo(path, description);
    }, cancellationToken);

    public Task<HermesImport?> FindHermesAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var location = HermesHome.Resolve(_home, environment: _environment);
        if (location.Home is not { } home || !Directory.Exists(home))
            return null;
        var memories = Path.Combine(home, "memories");
        var entries = Directory.Exists(memories) ? HermesMemory.Read(memories).Sum(file => file.Entries.Length) : 0;
        var skills = Path.Combine(home, "skills");
        var skillCount = Directory.Exists(skills) ? Directory.EnumerateFiles(skills, "SKILL.md", SearchOption.AllDirectories).Count() : 0;
        return (HermesImport?)new HermesImport(Shown(home), entries, skillCount);
    }, cancellationToken);

    public Task SaveAsync(SetupChoices choices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(choices);
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Only what the person changed: a null mode or posture leaves the configured one (Manual, Refuse) as it is.
            var edits = new List<ConfigEdit>();
            if (choices.Mode is { } mode)
                edits.Add(ConfigEdit.Set("Corvid:Approvals:Mode", mode.ToString()));
            if (choices.Posture is { } posture)
            {
                // "local only" never asks a cloud model: the egress gate refuses every cloud request.
                edits.Add(ConfigEdit.Set("Corvid:Privacy:Cloud", posture switch
                {
                    "local only" => "Refuse",
                    "send" => "Send",
                    _ => "Redact",
                }));
            }
            if (choices.Workspace.Trim() is { Length: > 0 } workspace)
                edits.Add(ConfigEdit.Set("Corvid:Workspace", workspace));
            if (edits.Count == 0)
                return;
            var result = _writer.Apply(edits);
            if (!result.Written)
                throw new InvalidOperationException($"Nothing was saved to {result.Path}: {result.FailureText}");
        }, cancellationToken);
    }

    /// <summary>The mode and posture the next start would run with (the built-in defaults where the file says nothing).</summary>
    public Task<CurrentSetup> CurrentAsync(CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var options = Options(_writer.NextStart());
        Corvid.App.Core.Shell.ApprovalMode? mode = Enum.TryParse<Corvid.App.Core.Shell.ApprovalMode>(options.Approvals.Mode.ToString(), out var parsed) ? parsed : null;
        var posture = options.Privacy.Cloud.ToString() switch
        {
            "Refuse" => "local only",
            "Send" => "send",
            "Redact" => "redact",
            _ => null,
        };
        return new CurrentSetup(mode, posture);
    }, cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _engine.Progress -= OnProgress;
    }

    private void OnProgress(Corvid.Host.Composition.StartupStep step) => Raise();

    private void Raise()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) == 0)
                EngineStatusChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    /// <summary>
    /// A host check as the page groups it: configuration, data, checkpoints and skills; the sandbox, Vault and MCP servers;
    /// the GPU. The models and the decider are the page's Models step, which lists each from the catalogue.
    /// </summary>
    private static AppCheck? Map(HostCheck check)
    {
        var (section, name) = (check.Area, check.Name) switch
        {
            ("config", var named) => (CheckSections.Configuration, named),
            // The variable's name is in the detail; the row's name is what it is for.
            ("models", Doctor.MallocVariable) => (CheckSections.Configuration, "malloc threshold"),
            ("models", _) => (null, ""),
            ("checkpoints", var named) => (CheckSections.Configuration, $"checkpoints {named}"),
            ("skills", var named) => (CheckSections.Configuration, $"skills {named}"),
            ("sandbox", var named) => (CheckSections.Sandbox, named),
            ("vault", var named) => (CheckSections.Sandbox, $"Vault {named}"),
            ("mcp", var named) => (CheckSections.Sandbox, $"MCP {named}"),
            ("gpu", var named) => (CheckSections.Gpu, named),
            (var area, var named) => (CheckSections.Configuration, $"{area} {named}"),
        };
        if (section is null)
            return null;
        var status = check.Status switch
        {
            HostStatus.Ok => AppStatus.Ok,
            HostStatus.Info => AppStatus.Info,
            HostStatus.Warning => AppStatus.Warning,
            _ => AppStatus.Failed,
        };
        // The host's text is prose, not the page's markup: its backticks, stars and tildes (~/.cache) are text.
        return new AppCheck(section, name, status, Spans.Escape(check.Detail), check.Fix is { } fix ? Spans.Escape(fix) : null, Command(check.Fix));
    }

    /// <summary>The fix on its own when it is a command line to run ("corvid model download kev-0.8b"), for "Copy the command".</summary>
    private static string? Command(string? fix) =>
        fix is { Length: > 0 } text && new[] { "corvid ", "corvid-", "export ", "systemctl ", "dotnet ", "sudo " }.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal))
            ? text.TrimEnd('.')
            : null;

    private static CorvidOptions Options(IConfiguration configuration) =>
        configuration.GetSection(CorvidOptions.Section).Get<CorvidOptions>() ?? new CorvidOptions();

    private static IEnumerable<string> Children(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? [.. Directory.EnumerateDirectories(directory).Where(child => !Path.GetFileName(child).StartsWith('.')).Order(StringComparer.Ordinal)]
                : [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool InGitRepository(string directory)
    {
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) || File.Exists(Path.Combine(current.FullName, ".git")))
                return true;
        return false;
    }

    private string Expand(string path) =>
        path == "~" ? _home : path.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(_home, path[2..]) : path;

    /// <summary>A path under the home directory as <c>~/…</c>, as the page shows paths.</summary>
    private string Shown(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var home = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_home));
        return full == home ? "~"
            : full.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "~/" + full[(home.Length + 1)..]
            : full;
    }
}
