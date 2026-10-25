using Corvid.App.Core.Shell;

namespace Corvid.App.Core.FirstRun;

/// <summary>How a doctor check came out (the engine's <c>Corvid.Host.Diagnostics.CheckStatus</c>).</summary>
public enum CheckStatus
{
    Ok,
    Info,
    Warning,
    Failed,
}

/// <summary>The doctor's sections the first run groups its checks into, in the page's order.</summary>
public static class CheckSections
{
    public const string Configuration = "Configuration and data";
    public const string Sandbox = "Sandbox and secrets";
    public const string Gpu = "GPU";

    public static IReadOnlyList<string> All { get; } = [Configuration, Sandbox, Gpu];
}

/// <summary>
/// One of <c>corvid doctor</c>'s checks (<c>Corvid.Host.Diagnostics.DoctorCheck</c>): what was checked, how it came out,
/// what was found, and the fix when something can be done. Text may mark paths and commands with backticks.
/// </summary>
/// <param name="FixCommand">The fix's command line on its own, for "Copy the command".</param>
public sealed record DoctorCheck(string Section, string Name, CheckStatus Status, string Detail, string? Fix = null, string? FixCommand = null);

/// <summary>The doctor's report: every check, and the build the GPU section describes ("CUDA build").</summary>
public sealed record DoctorReport(IReadOnlyList<DoctorCheck> Checks, string Build);

/// <summary>A workspace as the first run describes it: a git repository or not, and what follows from that.</summary>
public sealed record WorkspaceInfo(string Path, string Description);

/// <summary>A Hermes installation found on this machine, with what it would import.</summary>
public sealed record HermesImport(string Path, int Memories, int Skills);

/// <summary>
/// What the first run settles: the workspace, the approval mode and the cloud privacy posture ("redact", "local only").
/// A null mode or posture leaves the configuration's as it is: the first run writes only what the person changed, so a
/// setup opened on a configured machine (a model went missing) never loosens a mode or a posture chosen before.
/// </summary>
public sealed record SetupChoices(string Workspace, ApprovalMode? Mode, string? Posture);

/// <summary>
/// The approval mode and the cloud posture the configuration has now, which the first run starts from; null where it says
/// nothing (the built-in default applies). The posture is "redact", "local only" or "send" (<c>Corvid:Privacy:Cloud</c>
/// Redact, Refuse or Send).
/// </summary>
public sealed record CurrentSetup(ApprovalMode? Mode, string? Posture)
{
    public static CurrentSetup None { get; } = new(null, null);
}

/// <summary>
/// The first run's seam to the engine (docs/plan/ui.md F1): the doctor's checks before any model loads, the workspace,
/// the Hermes import and saving what was chosen. Models and downloads go through the Models area's
/// <see cref="Models.IModelService"/>, which the first run shares. The demo implements it now; <c>Corvid.App.Engine</c>
/// will over <c>Doctor</c> and the configuration writer.
/// </summary>
public interface IFirstRunService
{
    Task<DoctorReport> RunChecksAsync(CancellationToken cancellationToken = default);

    /// <summary>Folders offered by Choose…: recent workspaces and git repositories under the home directory.</summary>
    Task<IReadOnlyList<string>> SuggestWorkspacesAsync(CancellationToken cancellationToken = default);

    Task<WorkspaceInfo> DescribeWorkspaceAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>A Hermes installation to import from; null when there is none.</summary>
    Task<HermesImport?> FindHermesAsync(CancellationToken cancellationToken = default);

    /// <summary>Writes the choices to <c>corvid.json</c> (creating it) when the setup finishes or is skipped.</summary>
    Task SaveAsync(SetupChoices choices, CancellationToken cancellationToken = default);

    /// <summary>The mode and posture configured now, which the setup starts on; nothing configured by default (the demo's fresh machine).</summary>
    Task<CurrentSetup> CurrentAsync(CancellationToken cancellationToken = default) => Task.FromResult(CurrentSetup.None);

    /// <summary>
    /// The engine's start as the status bar's engine line says it while the setup shows ("starting · Loading kev-0.8b",
    /// "not started · 2 models missing"), from K-4's load progress; null when there is nothing to report (the demo, whose
    /// line is the mockup's).
    /// </summary>
    string? EngineStatus => null;

    /// <summary>Raised on the UI thread when <see cref="EngineStatus"/> changes.</summary>
    event EventHandler? EngineStatusChanged { add { } remove { } }
}
