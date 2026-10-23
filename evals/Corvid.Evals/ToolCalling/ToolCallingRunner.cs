using System.Buffers;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;
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
    /// <summary>
    /// Optional ablation of (c): the same bindings, but the extras are registered without their <c>mcp:</c> origin, so
    /// C-033's ingress <c>injection</c> question about MCP descriptions is never asked and never withholds them. Not production.
    /// Since C-037 the extras (trusted) are not asked about in (c) either, so the two differ only in the tools' origin.
    /// </summary>
    ShortlistKevNoDescriptions,
    /// <summary>Optional: production without Kev (<see cref="DegradedDecider"/>; embedding-margin tool pick, generator yes/no). §5.6's A/B baseline.</summary>
    Degraded,
    /// <summary>
    /// Optional, spike S4 (C-038): (c) with a transcript-sized TurnState. The same bindings; Kev's state keeps every event
    /// one-liner (<c>RecentEvents</c> 64) under <see cref="ToolCallingRunner.TranscriptStateBudget"/> instead of the last eight
    /// under the 600-token budget, and the 300-token dual-shortlist ingress budget is off. Not production.
    /// </summary>
    ShortlistKevTranscriptState,
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
        ToolCallingConfiguration.ShortlistKevNoDescriptions => "shortlist-kev-no-desc",
        ToolCallingConfiguration.Degraded => "degraded",
        ToolCallingConfiguration.ShortlistKevTranscriptState => "shortlist-kev-transcript",
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

    public static bool NeedsKev(ToolCallingConfiguration configuration) =>
        configuration is ToolCallingConfiguration.ShortlistKev or ToolCallingConfiguration.ShortlistKevNoDescriptions
            or ToolCallingConfiguration.ShortlistKevTranscriptState;
}

/// <summary>The models a run binds, loaded once. The generator's service is an <see cref="IChatClient"/> bound as the host binds it.</summary>
/// <param name="Embedder">The <c>embedder.text</c> binding; required by every configuration but (a).</param>
/// <param name="Extractor">GLiNER2, bound in every configuration when present (production's default slots); null leaves it out.</param>
/// <param name="Kev">The calibrated decider for (c).</param>
public sealed record ToolCallingBindings(ModelBinding Generator, ModelBinding? Embedder, ModelBinding? Extractor, IDecider? Kev);

/// <summary>One case run in one configuration: the turn's result (or its exception), its events and what it spent.</summary>
/// <param name="Events">The turn's own events: those after the seeded history (<see cref="SeedHead"/>), so a seeded call is
/// never scored as one the turn made.</param>
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
    ImmutableArray<SessionEvent> Events)
{
    /// <summary>The log's head after the case's history was seeded (S4, C-038); 0 for a case without history.</summary>
    public long SeedHead { get; init; }
}

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
    private readonly Corvid.Tools.ToolRegistry _toolsWithoutOrigins;
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
        _toolsWithoutOrigins = CatalogueTool.Registry(set, _results, mcpOrigins: false);
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

    /// <summary>
    /// S4's wide arm (C-038): room for every event one-liner of a 40-entry history and the turn's own (<c>recent</c> 1,800
    /// tokens), under Kev's 8,192-token state limit with margin, and a hard stop well above it so the state is answered by Kev,
    /// not the fallback. The other fields keep the production budgets.
    /// </summary>
    public static TurnStateBudget TranscriptStateBudget { get; } = new(Total: 2_000, HardStop: 4_000, Recent: 1_800);

    /// <summary>The wide arm's <c>RecentEvents</c>: more than a 40-entry history and a four-step turn render.</summary>
    public const int TranscriptRecentEvents = 64;

    /// <param name="StateBudget">TurnOptions.StateBudget; null is production's 600 tokens.</param>
    /// <param name="DualShortlistStateBudget">TurnOptions.DualShortlistStateBudget; production's 300 unless the arm turns it off.</param>
    private sealed record Setup(ToolCallingConfiguration Configuration, string LogPath, SqliteEventLog Log, ModelSlotRegistry Registry,
        DecisionFabric Fabric, ToolShortlist Shortlist, Corvid.Tools.ToolRegistry Tools, TurnStateBudget? StateBudget, int RecentEvents,
        TurnStateBudget? DualShortlistStateBudget);

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
        var transcript = configuration == ToolCallingConfiguration.ShortlistKevTranscriptState;
        return new Setup(configuration, path, log, registry, fabric, shortlist,
            configuration == ToolCallingConfiguration.ShortlistKevNoDescriptions ? _toolsWithoutOrigins : _tools,
            transcript ? TranscriptStateBudget : TurnOptions.Default.StateBudget,
            transcript ? TranscriptRecentEvents : TurnOptions.Default.RecentEvents,
            transcript ? null : TurnOptions.Default.DualShortlistStateBudget);
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
        var seedHead = Seed(setup.Log, sessionId, item.History);
        var services = new TurnServices
        {
            Log = setup.Log,
            Slots = new SessionModelSlots(setup.Registry, setup.Log, sessionId),
            Fabric = setup.Fabric,
            Tools = setup.Tools,
            Shortlist = setup.Shortlist,
            Options = TurnOptions.Default with
            {
                MaxSteps = item.Steps.Length + 1,
                // As the host sets them: the built-ins' example requests; MCP tools have none.
                ToolExamples = _examples,
                StateBudget = setup.StateBudget,
                RecentEvents = setup.RecentEvents,
                DualShortlistStateBudget = setup.DualShortlistStateBudget,
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
        var events = ReadAll(setup.Log, sessionId, seedHead);
        return new CaseRun(item, setup.Configuration, sessionId, result, error, ms, _meter.GeneratorCalls, _meter.DeciderCalls, _meter.EmbedCalls,
            [.. events.Select(stored => stored.Payload)]) { SeedHead = seedHead };
    }

    /// <summary>The prefix of seeded history call ids: <c>h-1</c>, <c>h-2</c>, in history order.</summary>
    public const string SeededCallPrefix = "h-";

    /// <summary>
    /// Writes a case's history into its fresh session before the turn (S4, C-038), as the turn loop would have logged it: a
    /// <see cref="UserMessage"/> or <see cref="AssistantMessage"/> per message, and a <see cref="ToolCallIssued"/> (call id
    /// <c>h-&lt;n&gt;</c>, no round) followed by its <see cref="ToolResult"/> per tool entry. A result's text is stored as JSON
    /// when it parses, as <see cref="CatalogueTool"/> returns canned results. Returns the head after the seed (0 without history).
    /// </summary>
    public static long Seed(IEventLog log, string sessionId, ImmutableArray<HistoryEntry> history)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (history.IsDefaultOrEmpty)
            return log.GetHead(sessionId);
        var events = new List<EventData>(history.Length * 2);
        var n = 0;
        foreach (var entry in history)
            switch (entry.Kind)
            {
                case HistoryKind.User:
                    events.Add(EventData.Create(new UserMessage(entry.Text)));
                    break;
                case HistoryKind.Assistant:
                    events.Add(EventData.Create(new AssistantMessage(entry.Text)));
                    break;
                default:
                    var id = SeededCallPrefix + (++n).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    events.Add(EventData.Create(new ToolCallIssued(id, entry.Tool!, entry.Arguments ?? EmptyObject)));
                    events.Add(EventData.Create(new ToolResult(id, ResultJson(entry.Text), entry.Error)));
                    break;
            }
        var stored = log.Append(sessionId, log.GetHead(sessionId), events);
        return stored[^1].Sequence;
    }

    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    private static JsonElement ResultJson(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
                writer.WriteStringValue(text);
            using var document = JsonDocument.Parse(buffer.WrittenMemory);
            return document.RootElement.Clone();
        }
    }

    internal static List<StoredEvent> ReadAll(IEventLog log, string sessionId, long after = 0)
    {
        var events = new List<StoredEvent>();
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
