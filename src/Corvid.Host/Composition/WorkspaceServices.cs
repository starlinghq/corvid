using System.Collections.Immutable;
using Corvid.Approvals;
using Corvid.Host.Configuration;
using Corvid.Kernel.Checkpoints;
using Corvid.Memory;
using Corvid.Sandbox;
using Corvid.Skills;
using Corvid.Tools;
using Corvid.Tools.Builtin;

namespace Corvid.Host.Composition;

/// <summary>
/// A6 (G-4): what a session's workspace decides, built once per directory and shared by every session in it: the protected
/// approval environment (what is inside the workspace, what Corvid may never touch), the approval pipeline judging paths
/// against it (a view of the runtime's, so rules, prompts and the deadline are shared), the sandboxed shell, the tools bound
/// to it (the file tools, the shell, the skill files) over the runtime's others, the checkpoint store and the project.
/// </summary>
public sealed record WorkspaceServices
{
    /// <summary>The workspace's canonical path (symbolic links resolved), as the file tools and checkpoints see it.</summary>
    public required string Root { get; init; }

    public required ApprovalEnvironment Environment { get; init; }

    public required ApprovalPipeline Approvals { get; init; }

    public required SandboxedShell Shell { get; init; }

    /// <summary>The tools a session in this workspace is offered: the runtime's, with the workspace-bound ones its own.</summary>
    public required ToolRegistry Tools { get; init; }

    /// <summary>The workspace's checkpoints (C-014); null when <c>Corvid:Checkpoints:Enabled</c> is off.</summary>
    public CheckpointService? Checkpoints { get; init; }

    /// <summary>The workspace's project (C-025); null when lessons are off.</summary>
    public ProjectIdentity? Project { get; init; }

    /// <summary>Why skills are off in this workspace (it is inside the skills directory), or null.</summary>
    public string? SkillsRefusal { get; init; }

    /// <summary>Whether this is the runtime's configured workspace (<c>Corvid:Workspace</c>, else the current directory).</summary>
    public bool IsDefault { get; init; }
}

/// <summary>
/// A6 (G-4): builds <see cref="WorkspaceServices"/> for a directory the way the runtime factory builds its configured
/// workspace: Vault's locations and Corvid's own state protected, the skills directory read-only or skills off, a sandbox
/// that hides what the approvals protect, and a checkpoint store that leaves it out.
/// </summary>
internal sealed class WorkspaceComposer(CorvidOptions settings, VaultLocations vault, string? rulesFile, string configFile,
    ISandbox sandbox, ISandboxEgress? egress, SandboxOptions sandboxOptions)
{
    /// <summary>The names of the tools bound to a workspace, which a session elsewhere gets its own of.</summary>
    public ImmutableHashSet<string> BoundToolNames { get; set; } = ImmutableHashSet<string>.Empty;

    /// <summary>The skills store, once opened (the skill file tools run scripts in the workspace's sandbox).</summary>
    public SkillStore? Skills { get; set; }

    /// <summary>The protected environment of <paramref name="workspace"/>, and the rules file it implies.</summary>
    public (ApprovalEnvironment Environment, string RulesFile) EnvironmentFor(string workspace)
    {
        var environment = vault.Protect(ApprovalEnvironment.FromProcess(workspace));
        var rules = rulesFile ?? JsonFileApprovalRuleStore.DefaultPath(environment);
        environment = CorvidRuntimeFactory.ProtectCorvidState(environment, settings, rules, configFile);
        if (settings.Skills.Enabled)
            environment = CorvidRuntimeFactory.Protect(environment, Path.Combine(CorvidPaths.SkillsDirectory(settings), ".git"));
        return (environment, rules);
    }

    /// <summary>The sandboxed shell for a workspace, with the skills directory read-only when the workspace contains it.</summary>
    public SandboxedShell ShellFor(ApprovalEnvironment environment, ImmutableArray<string> skillsReadOnly) =>
        new(sandbox, new ShellOptions
        {
            Workspace = environment.Workspace,
            Vault = vault,
            // A5: the launcher stays protected from the tools' writes but is never hidden from the sandbox it runs:
            // stage 2 re-executes it inside bubblewrap. It is read-only to commands instead.
            DeniedPaths = [.. CorvidRuntimeFactory.WithoutLauncher(environment.Protected.SafetyConfiguration, settings)],
            ReadOnlyPaths = [.. skillsReadOnly, .. CorvidRuntimeFactory.LauncherInside(settings, environment.Workspace)],
            Network = settings.Tools.ShellNetwork,
            EgressLifetime = sandboxOptions.Timeout + TimeSpan.FromMinutes(1),
        }, egress);

    /// <summary>The file tools of a workspace, closed to the skills directory when it holds it.</summary>
    public static FileTools FileToolsFor(ApprovalEnvironment environment, ImmutableArray<string> skillsReadOnly)
    {
        var workspace = Workspace.For(environment);
        return new FileTools(skillsReadOnly.IsEmpty ? workspace : new Workspace(environment.Workspace, workspace.Denied.Concat(skillsReadOnly)));
    }

    /// <summary>The checkpoint service of a workspace, or null with checkpoints off.</summary>
    public CheckpointService? CheckpointsFor(ApprovalEnvironment environment, ImmutableArray<string> skillsReadOnly) =>
        settings.Checkpoints.Enabled
            ? new CheckpointService(new ShadowStore(CorvidRuntimeFactory.CheckpointOptionsFor(settings, environment, skillsReadOnly)))
            : null;

    /// <summary>The workspace's project (C-025), or null when lessons are off.</summary>
    public ProjectIdentity? ProjectFor(string workspace, bool lessons) => lessons
        ? Kernel.Sessions.ProjectResolver.ResolveAsync(workspace, CorvidPaths.ExpandHome(settings.Checkpoints.GitPath), CorvidPaths.Home)
            .GetAwaiter().GetResult()
        : null;

    /// <summary>
    /// Everything a session in <paramref name="workspace"/> (an existing directory) works with, over the runtime's
    /// <paramref name="approvals"/> and <paramref name="shared"/> tools.
    /// </summary>
    public WorkspaceServices Build(string workspace, ApprovalPipeline approvals, ToolRegistry shared, bool lessons)
    {
        var (environment, _) = EnvironmentFor(workspace);
        var guard = CorvidRuntimeFactory.SkillsGuard(settings, environment.Workspace);
        var shell = ShellFor(environment, guard.ReadOnly);
        // The runtime's workspace-bound tools are hidden; this workspace's own take their names.
        var tools = new ToolRegistry(shared, BoundToolNames);
        if (settings.Tools.Builtin)
        {
            tools.Add(FileToolsFor(environment, guard.ReadOnly));
            tools.Add(new ShellTool(shell));
        }
        if (guard.Refusal is null && Skills is { } store)
            tools.Add(new SkillFileTools(store, shell));
        return new WorkspaceServices
        {
            Root = new Workspace(environment.Workspace).Root,
            Environment = environment,
            Approvals = approvals.ForWorkspace(environment),
            Shell = shell,
            Tools = tools,
            Checkpoints = CheckpointsFor(environment, guard.ReadOnly),
            Project = ProjectFor(environment.Workspace, lessons),
            SkillsRefusal = guard.Refusal,
        };
    }
}
