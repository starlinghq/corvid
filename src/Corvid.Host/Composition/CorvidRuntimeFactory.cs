using System.Collections.Immutable;
using Corvid.Abstractions.Extraction;
using Corvid.Abstractions.Fabric;
using Corvid.Approvals;
using Corvid.Fabric;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Host.Telemetry;
using Corvid.Kernel.Checkpoints;
using Corvid.Kernel.Events;
using Corvid.Kernel.Jobs;
using Corvid.Mcp;
using Corvid.Memory;
using Corvid.Models;
using Corvid.Rules;
using Corvid.Sandbox;
using Corvid.Skills;
using Corvid.Tools;
using Corvid.Tools.Builtin;
using Microsoft.Extensions.Options;

namespace Corvid.Host.Composition;

/// <summary>What C-012's tools and scheduler share: the protected approval environment, the sandboxed shell and the job store.</summary>
/// <param name="Web">The web fetch tool's client, owned by the runtime; null when the built-in tools are off.</param>
public sealed record BuiltinServices(ApprovalEnvironment Environment, SandboxedShell Shell, JobStore Jobs, WebFetchTool? Web);

/// <summary>
/// The handler the built-in <c>web_fetch</c> tool sends its requests through, when one is registered in DI; without one
/// the tool uses its own handler, which connects only to public addresses. The prompt-injection suite (C-024) registers a
/// recording sink here, so the real tool, with its URL checks, taint and approval path, fetches from the sink; Vault's
/// egress proxy (V-003) is the production use. A handler given here replaces the resolved-address check (the tool's own
/// check of literal addresses still applies), so it must not follow redirects and must apply its own address policy.
/// </summary>
public sealed record WebFetchTransport(HttpMessageHandler Handler);

/// <summary>
/// Skills (C-017): the store, the loop's database, the learning loop and the curator. <paramref name="Store"/> is null when
/// skills are off or the repository could not be opened; <paramref name="State"/> says which.
/// </summary>
public sealed record SkillServices(SkillStore? Store, SkillsDatabase? Database, SkillLearner? Learner, SkillCurator? Curator, string State);

/// <summary>The sandbox is Linux only (C-007a): elsewhere every command is refused, never run unconfined.</summary>
internal sealed class NoSandbox : ISandbox
{
    public Task<SandboxResult> RunAsync(SandboxPolicy policy, SandboxCommand command, CancellationToken cancellationToken = default) =>
        throw new SandboxRefusedException("The sandbox runs on Linux only; commands are not run on this platform.");
}

/// <summary>Configured models that are not usable offline. Corvid never downloads implicitly.</summary>
public sealed class ModelsUnavailableException(ImmutableArray<ModelAvailability> missing)
    : InvalidOperationException(string.Join(Environment.NewLine, missing.Select(item => item.Detail)))
{
    public ImmutableArray<ModelAvailability> Missing { get; } = missing;
}

/// <summary>
/// Starts a <see cref="CorvidRuntime"/> from configuration. Nothing loads until <see cref="StartAsync"/>, so commands that
/// only inspect (<c>corvid doctor</c>, <c>corvid model</c>) work without any model present.
/// </summary>
public sealed class CorvidRuntimeFactory(
    IOptions<CorvidOptions> options,
    ModelCatalogue catalogue,
    ModelMetrics metrics,
    TurnFeed feed,
    IEnumerable<IToolProvider> toolProviders,
    IEnumerable<ToolFunction> tools,
    CorvidConfigFile? configFile = null,
    IMcpTransports? mcpTransports = null,
    WebFetchTransport? webTransport = null)
{
    /// <summary>
    /// Configured models that stop the runtime from starting, checked offline (by size for Starling's): every missing or
    /// corrupt model, and a remote model that is unavailable (no Vault) when it is some slot's default. An unavailable
    /// escalation or fallback target is left out instead (<see cref="CheckOptionalModels"/>), so the rest still runs.
    /// </summary>
    public IReadOnlyList<ModelAvailability> CheckModels() => Check().Required;

    /// <summary>Unavailable escalation and fallback targets the runtime will start without (plan §10.5: cloud needs Vault).</summary>
    public IReadOnlyList<ModelAvailability> CheckOptionalModels() => Check().Optional;

    /// <exception cref="ModelsUnavailableException">A configured model is not usable offline; nothing was loaded.</exception>
    public async Task<CorvidRuntime> StartAsync(CancellationToken cancellationToken = default)
    {
        var (required, optional) = Check();
        if (required.Count > 0)
            throw new ModelsUnavailableException([.. required]);
        // Loading reads models from disk synchronously (seconds for Qwen); keep it off the caller's thread.
        return await Task.Run(() => Build(optional), cancellationToken).ConfigureAwait(false);
    }

    private (IReadOnlyList<ModelAvailability> Required, IReadOnlyList<ModelAvailability> Optional) Check()
    {
        using var devices = new DeviceSchedulers();
        using var composer = new ModelComposer(catalogue, options.Value, devices, metrics, feed);
        var unready = composer.CheckConfigured();
        var optional = unready.Where(item => item.Readiness == ModelReadiness.Unavailable && !composer.IsDefault(item.Model.Name)).ToList();
        return ([.. unready.Except(optional)], optional);
    }

    private CorvidRuntime Build(IReadOnlyList<ModelAvailability> unavailable)
    {
        var settings = options.Value;
        var devices = new DeviceSchedulers(settings.Devices.CpuThreads);
        var composer = new ModelComposer(catalogue, settings, devices, metrics, feed);
        try
        {
            var registry = composer.Compose(unavailable.Select(item => item.Model.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
            var log = new SqliteEventLog(CorvidPaths.EventLog(settings));
            // Kev when the decider slot is bound (C-004b), with Kev-4B as the second tier when configured; the degraded tier
            // always, for states over the hard stop and requests Kev refuses.
            IDecider? calibrated = null, escalation = null;
            string? placement = null;
            if (registry.TryGet(ModelSlots.Decider, out var decider))
            {
                calibrated = decider.Default.GetService<IDecider>();
                escalation = decider.Escalation.FirstOrDefault()?.GetService<IDecider>();
                placement = decider.Default.Profile.Placement;
            }
            // C-030: resolved once, as placement is fixed per runtime and /model refuses decider switches.
            var fabric = new DecisionFabric(DecisionPolicy.Default, composer.CreateDegradedDecider(DecisionPolicy.Default), log,
                calibrated, escalation, ReductionFor(settings.Turn.ReducedCheckpoints, calibrated, placement));

            // Vault's locations are protected twice (C-012): the hardline list refuses them, and the shell's sandbox hides them.
            var vault = VaultLocationsFor(settings);
            var environment = vault.Protect(ApprovalEnvironment.FromProcess(CorvidPaths.Workspace(settings)));
            var rulesFile = settings.Approvals.RulesFile is { Length: > 0 } file ? CorvidPaths.ExpandHome(file) : JsonFileApprovalRuleStore.DefaultPath(environment);
            environment = ProtectCorvidState(environment, settings, rulesFile, configFile?.Path ?? CorvidPaths.UserConfigFile);
            // The skills repository's history is how authorship is known (C-017), so no tool may write it; the skill files
            // themselves stay readable in the sandbox, where skill scripts run.
            if (settings.Skills.Enabled)
                environment = Protect(environment, Path.Combine(CorvidPaths.SkillsDirectory(settings), ".git"));
            // A workspace that contains the skills directory (the home directory, say) would let the file tools and the shell
            // edit skills, and an edit on disk is committed as the user's: the agent could rewrite a user or hub skill, or turn
            // hub content into trusted content. The skills directory is read-only to commands and closed to the file tools.
            var skillsGuard = SkillsGuard(settings, environment.Workspace);
            var prompters = new ApprovalRouter();
            var approvals = new ApprovalPipeline(environment, fabric, log, prompters, new JsonFileApprovalRuleStore(rulesFile),
                new ApprovalOptions
                {
                    Timeout = TimeSpan.FromSeconds(settings.Approvals.TimeoutSeconds),
                    // Without the mapping the shell tool never runs: it requires the approval of its command line.
                    ShellTools = BuiltinTools.ShellToolMapping,
                    // C-023: the built-in rule pack after the hardline list; enforce rules force a prompt, never more.
                    Detections = settings.Detections.Enabled
                        ? new DetectionEngine(RulePack.BuiltIn(), BuiltinTools.ShellToolMapping, settings.Detections.Enforce)
                        : null,
                });

            var counter = composer.GeneratorCounter();
            var memory = settings.Memory.Enabled ? BuildMemory(settings, composer, registry, fabric, log, counter) : null;

            var launcher = settings.Sandbox.LauncherPath is { Length: > 0 } path ? CorvidPaths.ExpandHome(path) : null;
            var sandboxOptions = new SandboxOptions { LauncherPath = launcher };
            ISandbox sandbox = OperatingSystem.IsLinux()
                ? new BubblewrapSandbox(sandboxOptions)
                : new NoSandbox();
            // V-003b: with the shell's network on, commands reach HTTPS through Vault's per-sandbox proxy only; without a
            // Vault socket the egress is absent and such a command refuses rather than run another way.
            var egress = settings.Tools.ShellNetwork && vault.SocketPath is { } vaultSocket ? new VaultSandboxEgress(vaultSocket) : null;
            // The sandbox leaves everything outside the workspace read-only, but a workspace can contain Corvid's own state.
            var shell = new SandboxedShell(sandbox, new ShellOptions
            {
                Workspace = environment.Workspace,
                Vault = vault,
                DeniedPaths = environment.Protected.SafetyConfiguration,
                ReadOnlyPaths = skillsGuard.ReadOnly,
                Network = settings.Tools.ShellNetwork,
                EgressLifetime = sandboxOptions.Timeout + TimeSpan.FromMinutes(1),
            }, egress);
            var jobs = new JobStore(CorvidPaths.JobStore(settings));
            var builtin = new BuiltinServices(environment, shell, jobs, settings.Tools.Builtin ? new WebFetchTool(webTransport?.Handler) : null);

            // Tools registered in DI (any later toolset) are offered alongside the built-in ones.
            var toolRegistry = new ToolRegistry();
            if (settings.Tools.Builtin)
            {
                var workspace = Workspace.For(environment);
                toolRegistry.Add(new FileTools(skillsGuard.ReadOnly.IsEmpty ? workspace
                    : new Workspace(environment.Workspace, workspace.Denied.Concat(skillsGuard.ReadOnly))));
                toolRegistry.Add(new ShellTool(shell));
                toolRegistry.Add(builtin.Web!);
                toolRegistry.Add(new CronTools(jobs));
                if (memory?.Writer is { } writer)
                    toolRegistry.Add(new MemoryTools(writer, memory.Retriever));
            }
            var skills = skillsGuard.Refusal is { } refusal ? new SkillServices(null, null, null, null, refusal) : OpenSkills(settings, log);
            if (skills.Store is { } store)
                toolRegistry.Add(new SkillFileTools(store, shell));
            foreach (var provider in toolProviders)
                toolRegistry.Add(provider);
            toolRegistry.AddRange(tools);
            if (skills.Store is { } skillStore && skills.Database is { } skillsDatabase)
                skills = AddLearning(skills, settings, skillStore, skillsDatabase, composer, log, fabric, toolRegistry);

            var checkpoints = settings.Checkpoints.Enabled
                ? new CheckpointService(new ShadowStore(CheckpointOptionsFor(settings, environment, skillsGuard.ReadOnly)))
                : null;

            // C-018: configured MCP servers start in the background and add their tools as they list them; last, so built-in
            // and DI tools keep their names. A sandboxed server is denied what the shell is denied. C-022: held and accepted
            // definitions are recorded in the log's mcp-pins session. Vault's sandbox directory is made to exist first, so it is
            // denied to a server started before Vault's first sandbox_open too (V-003b).
            vault.EnsureSandboxDirectory();
            var mcp = McpComposition.Start(settings, McpDeniedPaths(vault, environment), toolRegistry, log, transports: mcpTransports);

            // C-025: the workspace's project, once, with the checkpoints' git and the user's home as discovery's ceiling.
            ProjectIdentity? project = memory?.Lessons is null ? null
                : Kernel.Sessions.ProjectResolver.ResolveAsync(environment.Workspace,
                    CorvidPaths.ExpandHome(settings.Checkpoints.GitPath), CorvidPaths.Home).GetAwaiter().GetResult();

            return new CorvidRuntime(settings, catalogue, composer, devices, metrics, feed, log, registry, fabric, approvals, prompters,
                toolRegistry, memory, counter, builtin, checkpoints, skills, mcp) { UnavailableModels = [.. unavailable], Project = project };
        }
        catch
        {
            composer.Dispose();
            devices.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The checkpoint profile (C-030). Auto reduces only when a calibrated decider is bound and its effective placement is
    /// exactly <c>cpu</c> (a configured CUDA Kev that fell back included); CUDA, a remote decider and the degraded tier
    /// (placement null) stay full.
    /// </summary>
    internal static CheckpointReduction? ReductionFor(ReducedCheckpoints setting, IDecider? calibrated, string? placement) => setting switch
    {
        ReducedCheckpoints.On => new CheckpointReduction("configured"),
        ReducedCheckpoints.Off => null,
        _ when calibrated is not null && placement == ModelPlacement.Cpu => new CheckpointReduction($"{calibrated.Name} on cpu"),
        _ => null,
    };

    /// <summary>
    /// Corvid's own state wherever configuration puts it: the data directory (the event log, the job store that feeds
    /// unattended runs, memory), the configuration file that sets the approval mode, the approval rules and the sandbox
    /// launcher. Approvals' safety configuration covers only <c>$XDG_CONFIG_HOME/corvid</c>; without these a workspace
    /// that contains them (the home directory, say) would let a tool rewrite its own rules or schedule jobs. They become
    /// safety configuration, so the hardline list refuses writes, the file tools refuse access and the shell's sandbox
    /// hides them. A path that is the workspace or contains it is left out: denying it would deny the whole workspace.
    /// </summary>
    internal static ApprovalEnvironment ProtectCorvidState(ApprovalEnvironment environment, CorvidOptions settings, string rulesFile,
        string configFile)
    {
        string?[] candidates =
        [
            CorvidPaths.DataDirectory(settings),
            rulesFile,
            configFile,
            settings.Sandbox.LauncherPath is { Length: > 0 } launcher ? CorvidPaths.ExpandHome(launcher) : null,
        ];
        var workspace = environment.Workspace;
        var added = candidates
            .Where(path => path is { Length: > 0 } && Path.IsPathFullyQualified(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path!)))
            .Where(path => !(workspace == path || workspace.StartsWith(path + "/", StringComparison.Ordinal)));
        var protectedPaths = environment.Protected with
        {
            SafetyConfiguration = [.. environment.Protected.SafetyConfiguration.Concat(added).Distinct(StringComparer.Ordinal)],
        };
        return new ApprovalEnvironment(environment.Workspace, environment.Home, protectedPaths, environment.Variables);
    }

    /// <summary>
    /// What a sandboxed MCP server (C-018) is denied besides the launcher's own denials: Vault's configured locations and
    /// Corvid's state (data directory, rules file, configuration file, launcher), as for the shell.
    /// </summary>
    internal static IEnumerable<string> McpDeniedPaths(CorvidOptions settings, string configFile)
    {
        var vault = VaultLocationsFor(settings);
        var environment = vault.Protect(ApprovalEnvironment.FromProcess(CorvidPaths.Workspace(settings)));
        var rulesFile = settings.Approvals.RulesFile is { Length: > 0 } file ? CorvidPaths.ExpandHome(file) : JsonFileApprovalRuleStore.DefaultPath(environment);
        return McpDeniedPaths(vault, ProtectCorvidState(environment, settings, rulesFile, configFile));
    }

    private static IEnumerable<string> McpDeniedPaths(VaultLocations vault, ApprovalEnvironment environment) =>
        vault.Paths.Concat(environment.Protected.SafetyConfiguration).Distinct(StringComparer.Ordinal);

    private static ApprovalEnvironment Protect(ApprovalEnvironment environment, string path)
    {
        var protectedPaths = environment.Protected with
        {
            SafetyConfiguration = [.. environment.Protected.SafetyConfiguration.Append(Path.GetFullPath(path)).Distinct(StringComparer.Ordinal)],
        };
        return new ApprovalEnvironment(environment.Workspace, environment.Home, protectedPaths, environment.Variables);
    }

    /// <summary>
    /// How the skills directory is kept from the tools: read-only (and closed to the file tools) when the workspace contains
    /// it; skills are off when the workspace is the skills directory or inside it, since every tool write would land there.
    /// </summary>
    internal static (ImmutableArray<string> ReadOnly, string? Refusal) SkillsGuard(CorvidOptions settings, string workspace)
    {
        if (!settings.Skills.Enabled)
            return ([], null);
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(CorvidPaths.SkillsDirectory(settings)));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if (root == directory || root.StartsWith(directory + "/", StringComparison.Ordinal))
            return ([], $"off: the workspace {root} is inside the skills directory {directory}, where the tools could rewrite skills");
        if (!directory.StartsWith(root + "/", StringComparison.Ordinal))
            return ([], null);
        // The sandbox launcher binds only paths that exist.
        Directory.CreateDirectory(directory);
        return ([directory], null);
    }

    /// <summary>
    /// C-014: the store leaves out what the environment protects (Vault, credential stores, Corvid's own state, credential
    /// file names), so it never holds a copy of anything Corvid may not read. A skills directory inside the workspace is left
    /// out too: it has its own history, and the learning loop writes it in the background, so a restore across a tool call
    /// could otherwise revert an agent skill, and the skill store would then commit that as the user's edit.
    /// </summary>
    internal static CheckpointOptions CheckpointOptionsFor(CorvidOptions settings, ApprovalEnvironment environment,
        ImmutableArray<string> skillsReadOnly)
    {
        var options = CheckpointOptions.For(environment, CorvidPaths.Checkpoints(settings), CorvidPaths.ExpandHome(settings.Checkpoints.GitPath));
        return options with
        {
            ExcludedPaths = [.. options.ExcludedPaths, .. skillsReadOnly],
            MaxFileBytes = settings.Checkpoints.MaxFileBytes,
            MaxFiles = settings.Checkpoints.MaxFiles,
        };
    }

    /// <summary>The skill store. Without git, or with a repository Corvid will not use, skills are off and the reason is kept.</summary>
    private static SkillServices OpenSkills(CorvidOptions settings, SqliteEventLog log)
    {
        if (!settings.Skills.Enabled)
            return new SkillServices(null, null, null, null, "off (Corvid:Skills:Enabled)");
        var directory = CorvidPaths.SkillsDirectory(settings);
        try
        {
            var database = new SkillsDatabase(CorvidPaths.SkillsDatabase(settings));
            var store = SkillStore.OpenAsync(new SkillStoreOptions
            {
                Root = directory,
                GitExecutable = settings.Skills.Git,
                Database = database,
                Log = log,
            }).GetAwaiter().GetResult();
            return new SkillServices(store, database, null, null, $"{store.Skills.Count} skill(s) in {directory}");
        }
        catch (Exception exception) when (exception is GitException or System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException
                                              or InvalidDataException or Microsoft.Data.Sqlite.SqliteException)
        {
            return new SkillServices(null, null, null, null, $"off: {exception.Message}");
        }
    }

    /// <summary>
    /// The learning loop and the curator (plan §11): drafts and replay turns run on the default generator at background
    /// priority, with a degraded-decider fabric over each replay's own log, and stand-ins for every registered tool.
    /// </summary>
    private static SkillServices AddLearning(SkillServices skills, CorvidOptions settings, SkillStore store, SkillsDatabase database,
        ModelComposer composer, SqliteEventLog log, DecisionFabric fabric, ToolRegistry tools)
    {
        var background = composer.CreateBackgroundSlots();
        var chat = (Microsoft.Extensions.AI.IChatClient)background.Get(ModelSlots.Generator).Default.Service;
        var embedder = (Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>)background.Get(ModelSlots.TextEmbedder).Default.Service;
        // Replay checkpoints are background work too, so their degraded answers queue behind the user's own.
        var decider = new DegradedDecider(embedder, chat, counter: chat.GetService(typeof(Abstractions.Tokens.ITokenCounter)) as Abstractions.Tokens.ITokenCounter);
        var replayer = new SkillReplayer(new SkillReplayServices
        {
            Registry = background,
            Fabric = replayLog => new DecisionFabric(DecisionPolicy.Default, decider, replayLog),
            Tools = tools,
            Turn = new Kernel.Turns.TurnOptions
            {
                MaxSteps = settings.Turn.MaxSteps,
                MaxOutputTokens = settings.Turn.MaxOutputTokens,
                SystemPrompt = settings.Turn.SystemPrompt is { Length: > 0 } prompt ? prompt : Kernel.Turns.TurnOptions.DefaultSystemPrompt,
                ToolExamples = BuiltinTools.Examples,
            },
        });
        var curator = new SkillCurator(store, database, replayer);
        var generator = background.Get(ModelSlots.Generator).Default;
        var learner = settings.Skills.Learning
            ? new SkillLearner(store, database, log, fabric, replayer, (Microsoft.Extensions.AI.IChatClient)generator.Service, curator,
                new SkillLearnerOptions { Generator = generator.Profile.Id, MaxOutputTokens = settings.Turn.MaxOutputTokens })
            : null;
        return skills with { Learner = learner, Curator = curator };
    }

    /// <summary>Vault's own defaults for this user, with the socket from configuration when it is set.</summary>
    private static VaultLocations VaultLocationsFor(CorvidOptions settings)
    {
        var defaults = VaultLocations.Default(CorvidPaths.Home, Environment.GetEnvironmentVariable);
        return settings.Vault.SocketPath is { Length: > 0 } socket
            ? new VaultLocations(defaults.DataDirectory, defaults.ConfigPath, CorvidPaths.ExpandHome(socket))
            : defaults;
    }

    private static MemoryServices BuildMemory(CorvidOptions settings, ModelComposer composer, ModelSlotRegistry registry,
        DecisionFabric fabric, SqliteEventLog log, Abstractions.Tokens.ITokenCounter? counter)
    {
        var database = new MemoryDatabase(CorvidPaths.MemoryDatabase(settings));
        var facts = new FactGraph(database);
        var blocks = new CoreBlockStore(database, counter);
        var episodes = new EpisodicIndex(database);
        var vectors = new MemoryVectors(database, new SqliteVectorIndex(database), composer.CreateMemoryEmbedders());
        // C-025: lessons need no extractor; they are gated on memory_op like facts and drafted on the background generator.
        var lessonStore = settings.Memory.Lessons.Enabled ? new LessonStore(database) : null;
        var retriever = new HybridRetriever(episodes, facts, vectors, counter, lessons: lessonStore);
        // The write path extracts entities first (plan §9.2), so it exists only with an extractor bound.
        var writer = registry.TryGet(ModelSlots.Extractor, out var extractor)
            ? new MemoryWriter(database, facts, blocks, retriever, extractor.Default.GetService<IExtractor>(), fabric, log, vectors)
            : null;
        var lessons = lessonStore is null ? null : new LessonWriter(lessonStore, fabric, log);
        Kernel.Lessons.LessonLearner? learner = null;
        if (lessons is not null)
        {
            var generator = composer.CreateBackgroundSlots().Get(ModelSlots.Generator).Default;
            learner = new Kernel.Lessons.LessonLearner(lessons, log, (Microsoft.Extensions.AI.IChatClient)generator.Service,
                new Kernel.Lessons.LessonLearnerOptions { Generator = generator.Profile.Id });
        }
        return new MemoryServices(database, blocks, episodes, vectors, retriever, writer) { Lessons = lessons, LessonLearner = learner };
    }
}
