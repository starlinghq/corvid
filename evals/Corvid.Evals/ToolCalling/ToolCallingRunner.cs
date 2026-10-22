using System.Collections.Immutable;
using System.Diagnostics;
using Corvid.Abstractions.Events;
using Corvid.Abstractions.Fabric;
using Corvid.Abstractions.Sessions;
using Corvid.Abstractions.Tokens;
using Corvid.Fabric;
using Corvid.Kernel.Events;
using Corvid.Kernel.Turns;
using Corvid.Models;
using Microsoft.Extensions.AI;

namespace Corvid.Evals.ToolCalling;

/// <summary>The S2 configurations (plan §18). All run the real <see cref="TurnRunner"/>; they differ only in what is bound.</summary>
public enum ToolCallingConfiguration
{
    /// <summary>(a) Qwen alone: no embedder, no Kev, the abstaining degraded tier, a shortlist the size of the catalogue.</summary>
    QwenAll,
    /// <summary>(b) The embedding shortlist (size 5, the production default), the abstaining degraded tier, no Kev.</summary>
    Shortlist,
    /// <summary>(c) Shortlist plus Kev-0.8B as the calibrated decider: production with Kev.</summary>
    ShortlistKev,
    /// <summary>Optional: production without Kev (<see cref="DegradedDecider"/>; embedding-margin tool pick, generator yes/no). §5.6's A/B baseline.</summary>
    Degraded,
}

public static class ToolCallingConfigurations
{
    /// <summary>The default run: (a), (b) and (c), interleaved in that order per case.</summary>
    public static ImmutableArray<ToolCallingConfiguration> Default { get; } =
        [ToolCallingConfiguration.QwenAll, ToolCallingConfiguration.Shortlist, ToolCallingConfiguration.ShortlistKev];

    public static string Name(ToolCallingConfiguration configuration) => configuration switch
    {
        ToolCallingConfiguration.QwenAll => "qwen-all",
        ToolCallingConfiguration.Shortlist => "shortlist",
        ToolCallingConfiguration.ShortlistKev => "shortlist-kev",
        ToolCallingConfiguration.Degraded => "degraded",
        _ => throw new ArgumentOutOfRangeException(nameof(configuration)),
    };

    public static bool TryParse(string name, out ToolCallingConfiguration configuration)
    {
        foreach (var candidate in Enum.GetValues<ToolCallingConfiguration>())
            if (Name(candidate) == name)
            {
                configuration = candidate;
                return true;
            }
        configuration = default;
        return false;
    }

    public static bool NeedsEmbedder(ToolCallingConfiguration configuration) => configuration != ToolCallingConfiguration.QwenAll;

    public static bool NeedsKev(ToolCallingConfiguration configuration) => configuration == ToolCallingConfiguration.ShortlistKev;
}

/// <summary>The models a run binds, loaded once. The generator's service is an <see cref="IChatClient"/> bound as the host binds it.</summary>
/// <param name="Embedder">The <c>embedder.text</c> binding; required by every configuration but (a).</param>
/// <param name="Extractor">GLiNER2, bound in every configuration when present (production's default slots); null leaves it out.</param>
/// <param name="Kev">The calibrated decider for (c).</param>
public sealed record ToolCallingBindings(ModelBinding Generator, ModelBinding? Embedder, ModelBinding? Extractor, IDecider? Kev);

/// <summary>One case run in one configuration: the turn's result (or its exception), its events and what it spent.</summary>
public sealed record CaseRun(
    ToolCallingCase Case,
    ToolCallingConfiguration Configuration,
    string SessionId,
    TurnResult? Result,
    string? Error,
    double TurnMs,
    ImmutableArray<GeneratorCallRecord> GeneratorCalls,
    ImmutableArray<double> DeciderMs,
    ImmutableArray<double> EmbedMs,
    ImmutableArray<SessionEvent> Events);

/// <summary>
/// Runs golden cases through <see cref="TurnRunner"/> in each configuration (C-034). Per case the configurations run
/// interleaved, in the order given, after one untimed warm-up case (S5's fairness rule); each case and configuration gets a
/// fresh session, and each configuration one SQLite event log (<c>events-&lt;config&gt;.db</c>) in the output directory.
/// Options are production's but <c>MaxSteps</c> (expected steps + 1, to bound runtime). The generator slot escalates to
/// <see cref="EscalationStub"/>, so an escalation is recorded with its reason. Approvals are not configured: every
/// catalogue tool is read-only, so none is asked.
/// </summary>
public sealed class ToolCallingRunner
{
    private readonly ToolCallingSet _set;
    private readonly ImmutableArray<Setup> _setups;
    private readonly CannedResults _results = new();
    private readonly TurnMeter _meter = new();
    private readonly Corvid.Tools.ToolRegistry _tools;
    private readonly IReadOnlyDictionary<string, ImmutableArray<string>> _examples;

    public ToolCallingRunner(ToolCallingSet set, ToolCallingBindings bindings, IEnumerable<ToolCallingConfiguration> configurations,
        string outputDirectory)
    {
        ArgumentNullException.ThrowIfNull(set);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(configurations);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        if (bindings.Generator.Service is not IChatClient)
            throw new ArgumentException("The generator binding must be a chat client.", nameof(bindings));
        _set = set;
        _tools = CatalogueTool.Registry(set, _results);
        _examples = set.Catalogue.Where(tool => !tool.Examples.IsEmpty).ToDictionary(tool => tool.Name, tool => tool.Examples, StringComparer.Ordinal);
        Directory.CreateDirectory(outputDirectory);
        _setups = [.. configurations.Distinct().Select(configuration => Configure(configuration, bindings, outputDirectory))];
        if (_setups.IsEmpty)
            throw new ArgumentException("No configuration to run.", nameof(configurations));
    }

    public ImmutableArray<ToolCallingConfiguration> Configurations => [.. _setups.Select(setup => setup.Configuration)];

    /// <summary>The event log of one configuration.</summary>
    public IEventLog Log(ToolCallingConfiguration configuration) => _setups.Single(setup => setup.Configuration == configuration).Log;

    public string LogPath(ToolCallingConfiguration configuration) => _setups.Single(setup => setup.Configuration == configuration).LogPath;

    /// <summary>Policy floors in force (the fabric's), for the report header.</summary>
    public static string FloorsVersion => DecisionPolicy.Default.Floors.Version;

    private sealed record Setup(ToolCallingConfiguration Configuration, string LogPath, SqliteEventLog Log, ModelSlotRegistry Registry,
        DecisionFabric Fabric, ToolShortlist Shortlist);

    private Setup Configure(ToolCallingConfiguration configuration, ToolCallingBindings bindings, string outputDirectory)
    {
        var name = ToolCallingConfigurations.Name(configuration);
        var path = Path.Combine(outputDirectory, $"events-{name}.db");
        foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            File.Delete(file);
        var log = new SqliteEventLog(path);
        var generator = (IChatClient)bindings.Generator.Service;
        var registry = new ModelSlotRegistry().Bind(ModelSlots.Generator,
            new ModelBinding(bindings.Generator.Profile, new TimedChatClient(generator, _meter, bindings.Generator.Profile.Id)),
            [EscalationStub.Binding()]);
        IEmbeddingGenerator<string, Embedding<float>>? embedder = null;
        if (ToolCallingConfigurations.NeedsEmbedder(configuration))
        {
            var binding = bindings.Embedder ?? throw new ArgumentException($"{name} needs an embedder.", nameof(bindings));
            embedder = (IEmbeddingGenerator<string, Embedding<float>>)binding.Service;
            registry.Bind(ModelSlots.TextEmbedder, new ModelBinding(binding.Profile, new TimedEmbedder(embedder, _meter)));
        }
        if (bindings.Extractor is { } extractor)
            registry.Bind(ModelSlots.Extractor, extractor);

        var policy = DecisionPolicy.Default;
        IDecider degraded = configuration == ToolCallingConfiguration.Degraded
            // As the host builds it: the default generator and embedder, the generator's own token counter.
            ? new DegradedDecider(embedder!, generator, policy.Catalogue, generator.GetService(typeof(ITokenCounter)) as ITokenCounter,
                askTurnBlocking: policy.Floors.DegradedTurnBlocking)
            : new AbstainingDecider();
        IDecider? calibrated = null;
        if (ToolCallingConfigurations.NeedsKev(configuration))
            calibrated = new TimedDecider(bindings.Kev ?? throw new ArgumentException($"{name} needs Kev.", nameof(bindings)), _meter);
        var fabric = new DecisionFabric(policy, new TimedDecider(degraded, _meter), log, calibrated);
        // (a) offers every tool: without an embedder the shortlist is the filtered catalogue, capped alphabetically at Size.
        var shortlist = configuration == ToolCallingConfiguration.QwenAll
            ? new ToolShortlist(new ShortlistOptions(Size: _set.Catalogue.Length))
            : new ToolShortlist();
        return new Setup(configuration, path, log, registry, fabric, shortlist);
    }

    /// <param name="warmup">Run once in every configuration before the first case, untimed and unscored (S5's warm-up).</param>
    /// <param name="completed">Called after each case and configuration, for progress and incremental output.</param>
    public async Task<ImmutableArray<CaseRun>> RunAsync(IReadOnlyList<ToolCallingCase> cases, ToolCallingCase? warmup = null,
        Action<CaseRun>? completed = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cases);
        if (warmup is not null)
            foreach (var setup in _setups)
                await RunOneAsync(setup, warmup, "warmup-" + warmup.Id, cancellationToken).ConfigureAwait(false);
        var runs = ImmutableArray.CreateBuilder<CaseRun>();
        foreach (var item in cases)
            foreach (var setup in _setups)
            {
                var run = await RunOneAsync(setup, item, item.Id, cancellationToken).ConfigureAwait(false);
                runs.Add(run);
                completed?.Invoke(run);
            }
        return runs.ToImmutable();
    }

    private async Task<CaseRun> RunOneAsync(Setup setup, ToolCallingCase item, string sessionId, CancellationToken cancellationToken)
    {
        _results.Begin(item);
        _meter.Reset();
        var services = new TurnServices
        {
            Log = setup.Log,
            Slots = new SessionModelSlots(setup.Registry, setup.Log, sessionId),
            Fabric = setup.Fabric,
            Tools = _tools,
            Shortlist = setup.Shortlist,
            Options = TurnOptions.Default with
            {
                MaxSteps = item.Steps.Length + 1,
                // As the host sets them: the built-ins' example requests; MCP tools have none.
                ToolExamples = _examples,
            },
        };
        TurnResult? result = null;
        string? error = null;
        var watch = Stopwatch.StartNew();
        try
        {
            result = await new TurnRunner(sessionId, services).RunAsync(new Envelope(sessionId, "cli", item.Prompt), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            error = exception.GetType().Name + ": " + exception.Message;
        }
        var ms = watch.Elapsed.TotalMilliseconds;
        var events = ReadAll(setup.Log, sessionId);
        return new CaseRun(item, setup.Configuration, sessionId, result, error, ms, _meter.GeneratorCalls, _meter.DeciderCalls, _meter.EmbedCalls,
            [.. events.Select(stored => stored.Payload)]);
    }

    internal static List<StoredEvent> ReadAll(IEventLog log, string sessionId)
    {
        var events = new List<StoredEvent>();
        long after = 0;
        while (true)
        {
            var page = log.Read(sessionId, after, limit: 4096);
            events.AddRange(page);
            if (page.Count < 4096)
                return events;
            after = page[^1].Sequence;
        }
    }
}
