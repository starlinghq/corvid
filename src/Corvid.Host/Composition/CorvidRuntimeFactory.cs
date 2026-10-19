using System.Collections.Immutable;
using Corvid.Abstractions.Extraction;
using Corvid.Approvals;
using Corvid.Fabric;
using Corvid.Host.Configuration;
using Corvid.Host.Models;
using Corvid.Host.Telemetry;
using Corvid.Kernel.Events;
using Corvid.Memory;
using Corvid.Models;
using Corvid.Tools;
using Microsoft.Extensions.Options;

namespace Corvid.Host.Composition;

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
    IEnumerable<ToolFunction> tools)
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

            var environment = ApprovalEnvironment.FromProcess(CorvidPaths.Workspace(settings));
            var rulesFile = settings.Approvals.RulesFile is { Length: > 0 } file ? CorvidPaths.ExpandHome(file) : JsonFileApprovalRuleStore.DefaultPath(environment);
            var prompters = new ApprovalRouter();
            var approvals = new ApprovalPipeline(environment, fabric, log, prompters, new JsonFileApprovalRuleStore(rulesFile),
                new ApprovalOptions { Timeout = TimeSpan.FromSeconds(settings.Approvals.TimeoutSeconds) });

            // Built-in tools (C-012) and any later toolset register providers in DI; the registry offers whatever is there.
            var registry_ = new ToolRegistry();
            foreach (var provider in toolProviders)
                registry_.Add(provider);
            registry_.AddRange(tools);

            var counter = composer.GeneratorCounter();
            var memory = settings.Memory.Enabled ? BuildMemory(settings, composer, registry, fabric, log, counter) : null;
            return new CorvidRuntime(settings, catalogue, composer, devices, metrics, feed, log, registry, fabric, approvals, prompters,
                registry_, memory, counter);
        }
        catch
        {
            composer.Dispose();
            devices.Dispose();
            throw;
        }
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
