using Corvid.Approvals;
using Corvid.Models;
using Corvid.Models.Privacy;

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

    public PrivacySettings Privacy { get; set; } = new();

    public SandboxSettings Sandbox { get; set; } = new();

    public CheckpointSettings Checkpoints { get; set; } = new();

    public SkillsSettings Skills { get; set; } = new();
}

public sealed class CheckpointSettings
{
    /// <summary>
    /// Checkpoints before tool calls that change files, for <c>/undo</c> and <c>/rollback</c> (plan §15). The store is a
    /// git repository per workspace under <c>DataDirectory/checkpoints</c>; the workspace's own <c>.git</c> is never used.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The git executable: a path, or a name looked up on <c>PATH</c>. Without git, nothing is reversible.</summary>
    public string GitPath { get; set; } = "git";

    /// <summary>Files larger than this are left out of checkpoints, so changing one is not reversible.</summary>
    public long MaxFileBytes { get; set; } = 32L * 1024 * 1024;

    /// <summary>A checkpoint that would add or update more files than this fails, and so does the change it was for.</summary>
    public int MaxFiles { get; set; } = 50_000;
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

    /// <summary>
    /// Remote models by catalogue name (C-015): local servers (llama.cpp, Ollama, vLLM, LM Studio) and cloud providers
    /// (Anthropic, OpenAI, OpenRouter, Gemini). Named in <see cref="Slots"/> like any other model.
    /// </summary>
    public Dictionary<string, RemoteModelSettings> Remote { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The defaults: Starling's in-process models on the CPU. The decider is Kev-0.8B (C-004b), and it is optional: while it
    /// is not downloaded the fabric runs on the degraded tier (plan §5.6) and says so. Kev-4B, the second tier for gates
    /// below their floor (§5.5), is the decider's escalation when configured (<c>Corvid:Models:Slots:decider:Escalation</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, string> DefaultSlots { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [ModelSlots.Generator] = "qwen3.5-0.8b",
        [ModelSlots.Extractor] = "gliner2-base-v1",
        [ModelSlots.TextEmbedder] = "embeddinggemma-2",
        [ModelSlots.Decider] = "kev-0.8b",
    };

    /// <summary>
    /// The configured slots merged over <see cref="DefaultSlots"/>. A slot without a model but with an escalation or
    /// fallbacks keeps its default model (so <c>decider:Escalation</c> alone adds Kev-4B to Kev-0.8B); a slot without a
    /// model and without either is unbound. Empty and missing models are treated alike, because configuration providers
    /// do not agree on which an empty value is.
    /// </summary>
    public IReadOnlyDictionary<string, SlotSettings> EffectiveSlots()
    {
        var slots = new Dictionary<string, SlotSettings>(StringComparer.Ordinal);
        foreach (var (slot, model) in DefaultSlots)
            slots[slot] = new SlotSettings { Model = model };
        foreach (var (slot, settings) in Slots)
        {
            if (settings is null)
                continue;
            if (string.IsNullOrWhiteSpace(settings.Model) && (settings.Escalation.Count > 0 || settings.Fallbacks.Count > 0)
                && DefaultSlots.TryGetValue(slot, out var model))
                slots[slot] = new SlotSettings { Model = model, Escalation = settings.Escalation, Fallbacks = settings.Fallbacks };
            else if (string.IsNullOrWhiteSpace(settings.Model))
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

    /// <summary>
    /// Profile-ordered targets for escalation (plan §6). For the decider, at most one: the second tier (Kev-4B) asked about
    /// gates the default answered below their floor (§5.5).
    /// </summary>
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

public sealed class SkillsSettings
{
    /// <summary>Skills (plan §11): the SKILL.md store, the <c>skill</c> question, the skill tools and <c>/skills</c>.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The skills directory, a git repository. Default <c>$XDG_DATA_HOME/corvid-skills</c>: outside the data directory, which
    /// the sandbox hides, so skill scripts are readable inside it.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>The learning loop: <c>skill_opportunity</c> queues turns, and a background job drafts and tests skills.</summary>
    public bool Learning { get; set; } = true;

    /// <summary>The git executable. It runs with a cleared environment and none of the user's git configuration.</summary>
    public string Git { get; set; } = "git";
}

public sealed class SandboxSettings
{
    /// <summary>Default <c>$CORVID_SANDBOX</c>, then <c>corvid-sandbox</c> next to the executable.</summary>
    public string? LauncherPath { get; set; }
}

/// <summary>
/// One remote model (plan §4.1). A cloud model's key is a Vault secret, named here and never stored in Corvid (plan §10.5):
/// without Vault the model is unavailable.
/// </summary>
public sealed class RemoteModelSettings
{
    /// <summary><c>anthropic</c>, <c>openai</c>, <c>openrouter</c>, <c>gemini</c> or <c>openai-compatible</c>.</summary>
    public string? Provider { get; set; }

    /// <summary>The provider's model id, for example <c>claude-opus-5-5</c>.</summary>
    public string? Model { get; set; }

    /// <summary>The API base. Required for <c>openai-compatible</c>; the others default to the provider's public endpoint.</summary>
    public string? Endpoint { get; set; }

    /// <summary>The Vault secret holding the key. Required for cloud models; a local server that needs no key leaves it out.</summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Only for <c>openai-compatible</c>: <c>LocalServer</c> (the default, which needs a loopback or private endpoint) or
    /// <c>Cloud</c>. Every other provider is cloud.
    /// </summary>
    public PrivacyClass? Privacy { get; set; }

    /// <summary>Chat slots it may serve. Default generator and summarizer.</summary>
    public List<string> Slots { get; set; } = [];

    public int ContextWindow { get; set; } = 128_000;

    /// <summary>How much of the window Corvid plans to use; default the whole window.</summary>
    public int? AcceptedContext { get; set; }

    public ToolCallSupport ToolCalls { get; set; } = ToolCallSupport.Native;

    public StructuredOutputSupport StructuredOutput { get; set; } = StructuredOutputSupport.None;

    /// <summary>Default: breakpoints for Anthropic, automatic for the others.</summary>
    public PromptCachingStyle? Caching { get; set; }

    /// <summary>Price per million tokens, for the decision log and <c>/insights</c>.</summary>
    public decimal InputCost { get; set; }

    public decimal OutputCost { get; set; }

    public decimal CachedInputCost { get; set; }

    /// <summary>
    /// Whether the model honours a required tool choice. Default false for Anthropic (current models reject it) and true
    /// for the others; when false the turn loop asks for the tool in words.
    /// </summary>
    public bool? ForcedToolChoice { get; set; }

    /// <summary>This model's egress policy, over <see cref="CorvidOptions.Privacy"/>.</summary>
    public EgressSettings? Egress { get; set; }
}

/// <summary>The privacy egress gate (plan §10.4) for every model this process binds.</summary>
public sealed class PrivacySettings
{
    /// <summary>What a cloud model receives when the request holds PII: <c>Redact</c> (default), <c>Send</c> or <c>Refuse</c>.</summary>
    public EgressAction Cloud { get; set; } = EgressAction.Redact;

    /// <summary>Local servers are not gated unless this is set.</summary>
    public EgressAction? LocalServer { get; set; }

    /// <summary>The least the gate does for a tainted request (plan §10.3); it only ever tightens.</summary>
    public EgressAction Tainted { get; set; } = EgressAction.Redact;

    /// <summary>The PII labels; default person, email, phone number, address, account number, credential.</summary>
    public List<string> Labels { get; set; } = [];

    /// <summary>User-defined labels scanned as well, for example "medical condition".</summary>
    public List<string> ExtraLabels { get; set; } = [];

    /// <summary>Minimum span confidence; lower is stricter.</summary>
    public float Threshold { get; set; } = EgressPolicy.Default.Threshold;
}

/// <summary>One model's egress policy; anything left unset comes from <see cref="PrivacySettings"/>.</summary>
public sealed class EgressSettings
{
    public EgressAction? Action { get; set; }

    public EgressAction? Tainted { get; set; }

    public List<string> ExtraLabels { get; set; } = [];

    public float? Threshold { get; set; }
}
