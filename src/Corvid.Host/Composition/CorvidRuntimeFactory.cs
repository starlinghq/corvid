using System.Collections.Immutable;
using Corvid.Abstractions.Extraction;
using Corvid.Approvals;
using Corvid.Fabric;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Host.Telemetry;
using Corvid.Kernel.Events;
using Corvid.Kernel.Jobs;
using Corvid.Memory;
using Corvid.Models;
using Corvid.Sandbox;
using Corvid.Tools;
using Corvid.Tools.Builtin;
using Microsoft.Extensions.Options;

namespace Corvid.Host.Composition;

/// <summary>What C-012's tools and scheduler share: the protected approval environment, the sandboxed shell and the job store.</summary>
/// <param name="Web">The web fetch tool's client, owned by the runtime; null when the built-in tools are off.</param>
public sealed record BuiltinServices(ApprovalEnvironment Environment, SandboxedShell Shell, JobStore Jobs, WebFetchTool? Web);

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
    CorvidConfigFile? configFile = null)
{
    /// <summary>Configured models that are missing or corrupt, checked offline by size.</summary>
    public IReadOnlyList<ModelAvailability> CheckModels()
    {
        using var devices = new DeviceSchedulers();
        using var composer = new ModelComposer(catalogue, options.Value, devices, metrics, feed);
        return composer.CheckConfigured();
    }

    /// <exception cref="ModelsUnavailableException">A configured model is not usable offline; nothing was loaded.</exception>
    public async Task<CorvidRuntime> StartAsync(CancellationToken cancellationToken = default)
    {
        var missing = CheckModels();
        if (missing.Count > 0)
            throw new ModelsUnavailableException([.. missing]);
        // Loading reads models from disk synchronously (seconds for Qwen); keep it off the caller's thread.
        return await Task.Run(Build, cancellationToken).ConfigureAwait(false);
    }

    private CorvidRuntime Build()
    {
        var settings = options.Value;
        var devices = new DeviceSchedulers(settings.Devices.CpuThreads);
        var composer = new ModelComposer(catalogue, settings, devices, metrics, feed);
        try
        {
            var registry = composer.Compose();
            var log = new SqliteEventLog(CorvidPaths.EventLog(settings));
            var fabric = new DecisionFabric(DecisionPolicy.Default, composer.CreateDegradedDecider(), log);

            // Vault's locations are protected twice (C-012): the hardline list refuses them, and the shell's sandbox hides them.
            var vault = VaultLocationsFor(settings);
            var environment = vault.Protect(ApprovalEnvironment.FromProcess(CorvidPaths.Workspace(settings)));
            var rulesFile = settings.Approvals.RulesFile is { Length: > 0 } file ? CorvidPaths.ExpandHome(file) : JsonFileApprovalRuleStore.DefaultPath(environment);
            environment = ProtectCorvidState(environment, settings, rulesFile, configFile?.Path ?? CorvidPaths.UserConfigFile);
            var prompters = new ApprovalRouter();
            var approvals = new ApprovalPipeline(environment, fabric, log, prompters, new JsonFileApprovalRuleStore(rulesFile),
                new ApprovalOptions
                {
                    Timeout = TimeSpan.FromSeconds(settings.Approvals.TimeoutSeconds),
                    // Without the mapping the shell tool never runs: it requires the approval of its command line.
                    ShellTools = BuiltinTools.ShellToolMapping,
                });

            var counter = composer.GeneratorCounter();
            var memory = settings.Memory.Enabled ? BuildMemory(settings, composer, registry, fabric, log, counter) : null;

            var launcher = settings.Sandbox.LauncherPath is { Length: > 0 } path ? CorvidPaths.ExpandHome(path) : null;
            ISandbox sandbox = OperatingSystem.IsLinux()
                ? new BubblewrapSandbox(new SandboxOptions { LauncherPath = launcher })
                : new NoSandbox();
            // The sandbox leaves everything outside the workspace read-only, but a workspace can contain Corvid's own state.
            var shell = new SandboxedShell(sandbox, new ShellOptions
            {
                Workspace = environment.Workspace,
                Vault = vault,
                DeniedPaths = environment.Protected.SafetyConfiguration,
            });
            var jobs = new JobStore(CorvidPaths.JobStore(settings));
            var builtin = new BuiltinServices(environment, shell, jobs, settings.Tools.Builtin ? new WebFetchTool() : null);

            // Tools registered in DI (any later toolset) are offered alongside the built-in ones.
            var toolRegistry = new ToolRegistry();
            if (settings.Tools.Builtin)
            {
                toolRegistry.Add(new FileTools(Workspace.For(environment)));
                toolRegistry.Add(new ShellTool(shell));
                toolRegistry.Add(builtin.Web!);
                toolRegistry.Add(new CronTools(jobs));
                if (memory?.Writer is { } writer)
                    toolRegistry.Add(new MemoryTools(writer, memory.Retriever));
            }
            foreach (var provider in toolProviders)
                toolRegistry.Add(provider);
            toolRegistry.AddRange(tools);

            return new CorvidRuntime(settings, catalogue, composer, devices, metrics, feed, log, registry, fabric, approvals, prompters,
                toolRegistry, memory, counter, builtin);
        }
        catch
        {
            composer.Dispose();
            devices.Dispose();
            throw;
        }
    }

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
        var retriever = new HybridRetriever(episodes, facts, vectors, counter);
        // The write path extracts entities first (plan §9.2), so it exists only with an extractor bound.
        var writer = registry.TryGet(ModelSlots.Extractor, out var extractor)
            ? new MemoryWriter(database, facts, blocks, retriever, extractor.Default.GetService<IExtractor>(), fabric, log, vectors)
            : null;
        return new MemoryServices(database, blocks, episodes, vectors, retriever, writer);
    }
}
