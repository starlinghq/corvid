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

    public McpSettings Mcp { get; set; } = new();

    /// <summary><c>corvid serve</c>, the OpenAI-compatible HTTP endpoint (C-019).</summary>
    public ServeSettings Serve { get; set; } = new();

    public GatewaySettings Gateway { get; set; } = new();

    /// <summary>Detection rules over the session event log (C-023), inline at pre_execute and by <c>corvid scan</c>.</summary>
    public DetectionSettings Detections { get; set; } = new();

    /// <summary>Session titles and recaps (G-6).</summary>
    public SessionsSettings Sessions { get; set; } = new();

    /// <summary>The windowed app's own choices (U-8); the CLI's commands ignore them.</summary>
    public AppSettings App { get; set; } = new();

    /// <summary>Child sessions (A5, plan §14 <c>delegate_task</c>): limits, the model allowlist and named model mixes.</summary>
    public AgentsSettings Agents { get; set; } = new();
}

/// <summary>
/// Child sessions (A5, plan §14 and §17 row 1.7). These are ceilings: a child asks for less, never more, and neither the
/// agent nor the person passing a mix can raise them from a session.
/// </summary>
public sealed class AgentsSettings
{
    /// <summary>The models a child may be given (catalogue names); empty allows any catalogue model.</summary>
    public List<string> Models { get; set; } = [];

    /// <summary>Children of one session running at once (Hermes' 10).</summary>
    public int MaxConcurrent { get; set; } = 10;

    /// <summary>How deep children nest: 1, a child cannot delegate; 0 turns delegation off.</summary>
    public int MaxDepth { get; set; } = 1;

    /// <summary>The most generator steps a child may take (and its default), never more than <c>Corvid:Turn:MaxSteps</c>.</summary>
    public int MaxSteps { get; set; } = 8;

    /// <summary>The most tokens (prompt and completion, as providers report them) a child may use, and its default.</summary>
    public int MaxTokens { get; set; } = 64_000;

    /// <summary>The longest a child may run, in seconds, and its default.</summary>
    public int MaxSeconds { get; set; } = 600;

    /// <summary>Named model mixes, by name, each by slot (<see cref="Corvid.Models.ModelMix"/>), for <c>delegate_task</c> and <c>/delegate</c>.</summary>
    public Dictionary<string, Dictionary<string, MixSlotSettings>> Mixes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A5: named toolsets a child can be given by name (<c>delegate_task</c>'s <c>toolset</c>, <c>/delegate --toolset</c>),
    /// each a list of tool names; still cut to the parent's tools. <c>read_only</c> is built in.
    /// </summary>
    public Dictionary<string, List<string>> Toolsets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A configured mix by name, checked; null when none is so named.</summary>
    /// <exception cref="Corvid.Models.ModelMixException">The configured mix is malformed.</exception>
    public Corvid.Models.ModelMix? Mix(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Mixes.TryGetValue(name, out var slots) && slots is not null
            ? Corvid.Models.ModelMix.Create(slots.Select(pair => KeyValuePair.Create(pair.Key, pair.Value?.ToSlot() ?? new Corvid.Models.ModelMixSlot(""))))
            : null;
    }
}

/// <summary>One slot of a configured mix (<c>Corvid:Agents:Mixes:&lt;name&gt;:&lt;slot&gt;</c>).</summary>
public sealed class MixSlotSettings
{
    public string? Model { get; set; }

    /// <summary>Null keeps the session's ladder.</summary>
    public List<string>? Ladder { get; set; }

    /// <summary>Null keeps the configured fallbacks.</summary>
    public List<string>? Fallbacks { get; set; }

    public bool Escalate { get; set; } = true;

    public Corvid.Models.ModelMixSlot ToSlot() =>
        new(Model ?? "", Ladder is null ? null : [.. Ladder], Fallbacks is null ? null : [.. Fallbacks], Escalate);
}

/// <summary>
/// What the windowed app hosts when it is the engine (U-8). Both are off by default: hosting the gateway connects every
/// enabled adapter with its Vault token, and hosting the scheduler runs scheduled jobs with their tools, so the person turns
/// each on (here, or Start hosting on the Gateway and Jobs pages). The one-lock rule still decides which process runs them.
/// </summary>
public sealed class AppSettings
{
    /// <summary>The app hosts the gateway: connects the enabled adapters with their Vault tokens while it is open.</summary>
    public bool HostGateway { get; set; }

    /// <summary>The app hosts the scheduler: runs due jobs, with their tools, while it is open (<c>Corvid:Scheduler:Enabled</c> permitting).</summary>
    public bool HostScheduler { get; set; }
}

/// <summary>Session titles and recaps (G-6, extending K-9), written by the local generator only.</summary>
public sealed class SessionsSettings
{
    /// <summary>Title a session with the local generator after its first exchange. Renames work either way.</summary>
    public bool Titles { get; set; } = true;

    /// <summary>
    /// Minutes a person must have been away from a session before resuming it shows a recap of what happened (the CLI's
    /// <c>--session</c>, the app opening it); 0 turns recaps on return off. <c>/recap</c> works either way.
    /// </summary>
    public int RecapAfterMinutes { get; set; } = 60;
}

public sealed class DetectionSettings
{
    /// <summary>Evaluate the built-in rules at pre_execute. <c>corvid scan</c> runs them whatever this says.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Let enforce rules force an approval prompt. False records every match as detect only, which keeps scheduled and
    /// other non-interactive work from being denied by a rule (an ask with nobody to answer is a denial).
    /// </summary>
    public bool Enforce { get; set; } = true;
}

/// <summary>MCP (C-018, plan §12): servers whose tools Corvid offers, and what <c>corvid mcp serve</c> shares.</summary>
public sealed class McpSettings
{
    /// <summary>Configured servers by name; the name prefixes their tools (<c>github__create_issue</c>).</summary>
    public Dictionary<string, McpServerSettings> Servers { get; set; } = new(StringComparer.Ordinal);

    public McpServeSettings Serve { get; set; } = new();
}

/// <summary>
/// One MCP server. Nothing here is a secret: an HTTP server's token is a Vault secret named in <see cref="Secret"/>, and a
/// stdio server gets only plain settings (secret-like names are refused).
/// </summary>
public sealed class McpServerSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary><c>stdio</c> or <c>http</c>; default <c>http</c> when <see cref="Url"/> is set, else <c>stdio</c>.</summary>
    public string? Transport { get; set; }

    /// <summary><c>Untrusted</c> (the default: annotations ignored, every tool asks, every result taints) or <c>Trusted</c>.</summary>
    public Corvid.Mcp.McpTrust Trust { get; set; } = Corvid.Mcp.McpTrust.Untrusted;

    public string? Command { get; set; }

    public List<string> Arguments { get; set; } = [];

    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Run a stdio server under <c>corvid-sandbox</c> (default). False runs it unconfined: only for a server you trust.</summary>
    public bool Sandboxed { get; set; } = true;

    /// <summary>Let a sandboxed stdio server use the network.</summary>
    public bool Network { get; set; }

    /// <summary>The directory a stdio server may write. Default its own, <c>$XDG_STATE_HOME/corvid-mcp/&lt;name&gt;</c>.</summary>
    public string? Workspace { get; set; }

    public List<string> ReadOnlyPaths { get; set; } = [];

    public string? Url { get; set; }

    /// <summary>The Vault secret sent as a bearer token, through Vault's proxy.</summary>
    public string? Secret { get; set; }

    /// <summary>Only these of the server's tools (by the server's names); empty for all.</summary>
    public List<string> Tools { get; set; } = [];

    /// <summary>Tools you declare read-only (by the server's names): they run without asking.</summary>
    public List<string> ReadOnlyTools { get; set; } = [];

    public int StartTimeoutSeconds { get; set; } = 30;

    public int CallTimeoutSeconds { get; set; } = 60;

    public int TaskTimeoutSeconds { get; set; } = 600;

    public int MaxResultChars { get; set; } = 16_000;

    public int MaxTools { get; set; } = 64;
}

/// <summary>What <c>corvid mcp serve</c> offers another agent. Read-only always; each part can be turned off.</summary>
public sealed class McpServeSettings
{
    /// <summary><c>search_memory</c>: facts, never local-only ones.</summary>
    public bool Memory { get; set; } = true;

    /// <summary>Let <c>search_memory</c> also return earlier conversations, which hold tool results and outside content.</summary>
    public bool Conversations { get; set; }

    /// <summary>
    /// C-028, off by default: <c>get_memory</c>, <c>get_memory_context</c>, approved non-local-only lessons of a known project
    /// in <c>search_memory</c>, and the <c>project</c> argument. Needs <see cref="Memory"/>; lessons need Memory:Lessons.
    /// </summary>
    public bool ProjectMemory { get; set; }

    /// <summary><c>skill_list</c> and <c>skill_read</c>.</summary>
    public bool Skills { get; set; } = true;
}

/// <summary>What a request does when a tool call needs the user, who cannot be asked over HTTP.</summary>
public enum ServeApprovals
{
    /// <summary>The request fails with an <c>approval_required</c> error naming the tool; nothing that needed approval ran.</summary>
    Fail,
    /// <summary>The call is denied and the model answers without it, as in an unattended job.</summary>
    Deny,
}

/// <summary>
/// The OpenAI-compatible endpoint (plan §12): <c>/v1/chat/completions</c> and <c>/v1/models</c>, bound to loopback by
/// default. A bearer token is a Vault secret named here, never a value in Corvid's configuration (plan §10.5).
/// </summary>
public sealed class ServeSettings
{
    /// <summary>Where to listen: an <c>http</c> URL. Anything other than a loopback address needs <see cref="TokenSecret"/>.</summary>
    public string Url { get; set; } = "http://127.0.0.1:8411";

    /// <summary>The Vault secret callers must present as <c>Authorization: Bearer</c>. Without it only loopback callers are served.</summary>
    public string? TokenSecret { get; set; }

    public ServeApprovals Approvals { get; set; } = ServeApprovals.Fail;

    /// <summary>Offer Corvid's tools to the model in served sessions. Off, every served turn is a plain conversation.</summary>
    public bool Tools { get; set; } = true;

    /// <summary>The largest request body accepted.</summary>
    public int MaxRequestBytes { get; set; } = 1024 * 1024;

    /// <summary>Whether <see cref="Url"/> names a loopback host (<c>127.0.0.0/8</c>, <c>::1</c> or <c>localhost</c>).</summary>
    public static bool IsLoopback(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.IsLoopback || url.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || System.Net.IPAddress.TryParse(url.Host.Trim('[', ']'), out var address) && System.Net.IPAddress.IsLoopback(address);
    }
}

/// <summary>
/// The messaging gateway (plan §12, C-020), run by <c>corvid gateway</c>. Adapters by name (<c>telegram</c>, <c>discord</c>,
/// <c>slack</c>); each bot token is a Vault secret named here, never a value.
/// </summary>
public sealed class GatewaySettings
{
    public Dictionary<string, GatewayAdapterSettings> Adapters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A connect that takes longer is abandoned and retried, so one platform never stalls the gateway.</summary>
    public int ConnectTimeoutSeconds { get; set; } = 30;

    /// <summary>How long an approval asked in a chat waits for the owner's <c>/approve</c>.</summary>
    public int ApprovalTimeoutSeconds { get; set; } = 300;
}

public sealed class GatewayAdapterSettings
{
    public bool Enabled { get; set; }

    /// <summary>The Vault secret holding the bot token (Telegram's needs <c>allowInUrl</c> in <c>vault.json</c>).</summary>
    public string? Secret { get; set; }

    /// <summary>Slack only: the Vault secret holding the app-level token (<c>xapp-</c>) for Socket Mode.</summary>
    public string? AppSecret { get; set; }

    /// <summary>Platform user ids that are the owner without pairing.</summary>
    public List<string> Owners { get; set; } = [];

    /// <summary>Platform user ids allowed without pairing; their messages are outside content (tainted).</summary>
    public List<string> Guests { get; set; } = [];

    /// <summary>
    /// Tools a non-owner's turn may use, by exact name (an MCP tool as <c>server__tool</c>). Empty by default: guests get
    /// answers, but no tools, skills or memory of the owner's.
    /// </summary>
    public List<string> GuestTools { get; set; } = [];

    /// <summary>Where <c>deliver: home_channel</c> sends.</summary>
    public string? HomeChat { get; set; }

    /// <summary>Per-chat persona and slot overrides, by chat id.</summary>
    public Dictionary<string, GatewayChatSettings> Chats { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Discord only: guild channel ids to watch (the bot answers there when mentioned).</summary>
    public List<string> Channels { get; set; } = [];

    /// <summary>Discord: seconds between polls of each channel. Telegram: the long-poll wait.</summary>
    public int? PollSeconds { get; set; }
}

public sealed class GatewayChatSettings
{
    /// <summary>Added to the system prompt for this chat's session.</summary>
    public string? Persona { get; set; }

    /// <summary>Slot overrides by catalogue model name, pinned for the chat's session (<c>generator</c>: <c>claude</c>).</summary>
    public Dictionary<string, string> Slots { get; set; } = new(StringComparer.Ordinal);
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
    /// Where in-process Starling models run, by catalogue name (C-029), for example
    /// <c>{ "kev-0.8b": { "Device": "cuda", "Fallback": "Cpu" } }</c>. Models left out run on the CPU. In v1 only Kev-0.8B
    /// and Kev-4B can be placed on CUDA, and only a <c>-p:UseCuda=true</c> build has the CUDA natives.
    /// </summary>
    public Dictionary<string, ModelPlacementSettings> Placement { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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

/// <summary>What happens when a model placed on a GPU cannot run there (C-029). Nothing falls back silently.</summary>
public enum PlacementFallback
{
    /// <summary>
    /// The load fails. For the decider that means the degraded tier with a notice (the decider never stops a start, C-004b)
    /// and <c>corvid doctor</c> reporting Failed; it does not mean Kev on the CPU.
    /// </summary>
    Fail,

    /// <summary>Load on the CPU instead, with a notice at start (CPU checkpoints take over 1 s, spike S1).</summary>
    Cpu,
}

public sealed class ModelPlacementSettings
{
    /// <summary><c>cpu</c> (the default), <c>cuda</c> (device 0) or <c>cuda:N</c>, N as CUDA numbers devices.</summary>
    public string Device { get; set; } = ModelPlacement.Cpu;

    public PlacementFallback Fallback { get; set; } = PlacementFallback.Fail;
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

    /// <summary>
    /// The TurnState size, in the generator's tokens, for an ingress checkpoint that asks both <c>tool</c> and <c>skill</c>
    /// (C-029; spike S1: 212 ms at 300 tokens against 301 ms at 600 on the GPU). 100 to 600; 600 turns it off.
    /// </summary>
    public int DualShortlistStateTokens { get; set; } = Kernel.Turns.TurnOptions.DefaultDualShortlistStateTokens;

    /// <summary>
    /// Fewer decider questions per turn (C-030, spike S1 decision 2): step checkpoints ask at most two questions (done, stuck,
    /// tool in that order) and pre_execute skips <c>risk</c> where the outcome is ask whatever it says. <see cref="ReducedCheckpoints.Auto"/>
    /// reduces only when Kev runs on the CPU.
    /// </summary>
    public ReducedCheckpoints ReducedCheckpoints { get; set; } = ReducedCheckpoints.Auto;
}

/// <summary>Whether the fabric runs the reduced checkpoint profile (C-030).</summary>
public enum ReducedCheckpoints
{
    /// <summary>Reduced only when a calibrated decider is bound and its effective placement is <c>cpu</c>; CUDA, a remote decider and the degraded tier stay full.</summary>
    Auto,

    /// <summary>Always reduced, the degraded tier included.</summary>
    On,

    /// <summary>Never reduced: every checkpoint is asked whole.</summary>
    Off,
}

public sealed class MemorySettings
{
    /// <summary>The memory store (plan §9). It needs an extractor and a text embedder; without them it stays off.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The lessons tier (C-025); it needs memory on.</summary>
    public LessonsSettings Lessons { get; set; } = new();
}

public sealed class LessonsSettings
{
    /// <summary>
    /// Lessons per project (C-025): <c>lesson_opportunity</c> queues turns, a background job drafts lessons on the background
    /// generator, and approved lessons of the workspace's project join recall. <c>/memory lessons</c> reviews them.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

public sealed class ToolSettings
{
    /// <summary>
    /// C-012's built-in tools: files in the workspace, the sandboxed shell, web fetch, memory and cron. Tools registered as
    /// <c>IToolProvider</c> in DI are offered either way.
    /// </summary>
    public bool Builtin { get; set; } = true;

    /// <summary>
    /// V-003b: give shell commands HTTPS through Corvid Vault's per-sandbox proxy, the only route out (never the host
    /// network). Off by default. In a tainted turn a command gets the route only when a person approved it as network
    /// egress; scheduled scripts from tainted jobs never do. With it on, a shell command does not run when Vault is absent.
    /// </summary>
    public bool ShellNetwork { get; set; }

    /// <summary>
    /// A5: offer <c>delegate_task</c> (plan §14), which starts child sessions within <c>Corvid:Agents</c>. On by default;
    /// <c>Corvid:Agents:MaxDepth</c> 0 turns it off too.
    /// </summary>
    public bool Delegate { get; set; } = true;
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

    /// <summary>How long the egress ledger (G-9, <c>egress.db</c>) keeps the requests it recorded.</summary>
    public EgressLedgerSettings Ledger { get; set; } = new();
}

/// <summary>
/// The egress ledger's retention (<c>Corvid:Privacy:Ledger</c>): requests older than <see cref="RetainDays"/> are pruned,
/// then the oldest until the text kept is at most <see cref="MaxMegabytes"/>, then the message bodies no request still
/// uses. Pruned when a process first records into the store and at most daily after, and by <c>corvid egress prune</c>.
/// </summary>
public sealed class EgressLedgerSettings
{
    /// <summary>Days a request is kept; 0 keeps requests whatever their age.</summary>
    public int RetainDays { get; set; } = 90;

    /// <summary>The most message text kept, in MB (1 MB = 1,048,576 bytes); 0 sets no size cap.</summary>
    public int MaxMegabytes { get; set; } = 256;

    /// <summary>The retention these settings describe.</summary>
    public Audit.EgressRetention Retention() => new(RetainDays > 0 ? TimeSpan.FromDays(RetainDays) : null,
        MaxMegabytes > 0 ? MaxMegabytes * 1024L * 1024 : null);
}

/// <summary>One model's egress policy; anything left unset comes from <see cref="PrivacySettings"/>.</summary>
public sealed class EgressSettings
{
    public EgressAction? Action { get; set; }

    public EgressAction? Tainted { get; set; }

    public List<string> ExtraLabels { get; set; } = [];

    public float? Threshold { get; set; }
}
