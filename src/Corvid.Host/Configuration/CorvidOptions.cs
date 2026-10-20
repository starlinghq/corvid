using Corvid.Approvals;
using Corvid.Models;

namespace Corvid.Host.Configuration;

/// <summary>
/// Corvid's typed configuration (plan §17 row 1.2), bound from the <c>Corvid</c> section and validated when the host
/// starts (<see cref="CorvidOptionsValidator"/>). Sources, later overriding earlier: built-in defaults, the user file
/// (<c>$XDG_CONFIG_HOME/corvid/corvid.json</c>, or <c>$CORVID_CONFIG</c>), <c>CORVID__</c> environment variables
/// (<c>CORVID__Models__Slots__generator__Model</c>), then <c>--Corvid:...</c> command-line switches.
/// </summary>
public sealed class CorvidOptions
{
    public const string Section = "Corvid";

    /// <summary>Where the event log and memory store live. Default <c>$XDG_DATA_HOME/corvid</c> (<c>~/.local/share/corvid</c>).</summary>
    public string? DataDirectory { get; set; }

    /// <summary>The directory approvals treat as the workspace (paths inside it are local). Default: the current directory.</summary>
    public string? Workspace { get; set; }

    public ModelOptions Models { get; set; } = new();

    public DeviceOptions Devices { get; set; } = new();

    public ApprovalSettings Approvals { get; set; } = new();

    public TurnSettings Turn { get; set; } = new();

    public MemorySettings Memory { get; set; } = new();

    public ToolSettings Tools { get; set; } = new();

    public SchedulerSettings Scheduler { get; set; } = new();

    public TelemetrySettings Telemetry { get; set; } = new();

    public VaultSettings Vault { get; set; } = new();

    public SandboxSettings Sandbox { get; set; } = new();
}

public sealed class ModelOptions
{
    /// <summary>The Starling model cache. Default <c>$STARLING_MODELS</c>, then <c>~/.cache/starlings/models</c>.</summary>
    public string? Cache { get; set; }

    /// <summary>
    /// Per slot (plan §4.1): the model bound at session start, its escalation ladder and its fallback chain, by catalogue
    /// name (<c>corvid model</c> lists them). Slots left out keep their defaults; a slot set to an empty model is unbound.
    /// </summary>
    public Dictionary<string, SlotSettings> Slots { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The defaults: Starling's in-process models on the CPU. There is no decider model until Kev ships (C-004b).</summary>
    public static IReadOnlyDictionary<string, string> DefaultSlots { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ModelSlots.Generator] = "qwen3.5-0.8b",
        [ModelSlots.Extractor] = "gliner2-base-v1",
        [ModelSlots.TextEmbedder] = "embeddinggemma-2",
    };

    /// <summary>The configured slots merged over <see cref="DefaultSlots"/>; empty models removed.</summary>
    public IReadOnlyDictionary<string, SlotSettings> EffectiveSlots()
    {
        var slots = new Dictionary<string, SlotSettings>(StringComparer.Ordinal);
        foreach (var (slot, model) in DefaultSlots)
            slots[slot] = new SlotSettings { Model = model };
        foreach (var (slot, settings) in Slots)
        {
            if (settings is null)
                continue;
            if (string.IsNullOrWhiteSpace(settings.Model))
                slots.Remove(slot);
            else
                slots[slot] = settings;
        }
        return slots;
    }
}

public sealed class SlotSettings
{
    public string? Model { get; set; }

    /// <summary>Profile-ordered targets for escalation (plan §6).</summary>
    public List<string> Escalation { get; set; } = [];

    /// <summary>Tried in order after a provider failure.</summary>
    public List<string> Fallbacks { get; set; } = [];
}

public sealed class DeviceOptions
{
    /// <summary>Worker threads of the CPU device scheduler. One by default: Starling serializes native work anyway.</summary>
    public int CpuThreads { get; set; } = 1;
}

public sealed class ApprovalSettings
{
    public ApprovalMode Mode { get; set; } = ApprovalMode.Smart;

    /// <summary>An unanswered prompt is denied after this many seconds.</summary>
    public int TimeoutSeconds { get; set; } = 300;

    /// <summary>Where "always" rules are kept. Default <c>$XDG_CONFIG_HOME/corvid/approvals.json</c>.</summary>
    public string? RulesFile { get; set; }
}

public sealed class TurnSettings
{
    public string? SystemPrompt { get; set; }

    public int MaxSteps { get; set; } = 8;

    public int MaxOutputTokens { get; set; } = 1_024;
}

public sealed class MemorySettings
{
    /// <summary>The memory store (plan §9). It needs an extractor and a text embedder; without them it stays off.</summary>
    public bool Enabled { get; set; } = true;
}

public sealed class ToolSettings
{
    /// <summary>
    /// C-012's built-in tools: files in the workspace, the sandboxed shell, web fetch, memory and cron. Tools registered as
    /// <c>IToolProvider</c> in DI are offered either way.
    /// </summary>
    public bool Builtin { get; set; } = true;
}

public sealed class SchedulerSettings
{
    /// <summary>
    /// Run due jobs (plan §13) while this process runs. Only one process on the machine runs them (a lock beside the job
    /// store); others leave it to that one.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

public sealed class TelemetrySettings
{
    /// <summary>
    /// An OTLP endpoint (an Aspire dashboard: <c>http://localhost:4317</c>). Unset, spans are exported only when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set.
    /// </summary>
    public string? OtlpEndpoint { get; set; }

    public string ServiceName { get; set; } = "corvid";
}

public sealed class VaultSettings
{
    /// <summary>Default <c>$XDG_RUNTIME_DIR/corvid-vault/vault.sock</c>.</summary>
    public string? SocketPath { get; set; }
}

public sealed class SandboxSettings
{
    /// <summary>Default <c>$CORVID_SANDBOX</c>, then <c>corvid-sandbox</c> next to the executable.</summary>
    public string? LauncherPath { get; set; }
}
