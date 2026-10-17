# Corvid: a local-first .NET agent with a calibrated decision fabric

Version 0.3, 2026-10-16. Status: design proposal. Corvid Vault M1 is built (`vault.md`, Implementation status); C-001's initial agent projects and dependency boundary check are built ([architecture](../architecture.md)). The agent loop itself is not.

## 0. Thesis

Hermes Agent sends nearly every decision through one large generator: which tool to call, whether a command is dangerous, whether something is worth remembering, when to compact, whether a skill is relevant. That works with a frontier model and falls apart with a small local one. Hermes' own docs warn that its Hermes 4 models are not tuned for its loop, and its issue tracker is full of local tool calls leaking as raw text (§17, row 1.5).

Corvid splits the agent in two:

- **System One** is fast, calibrated and always running locally. Kev answers typed questions (yes/no, choice, score) with probabilities. GLiNER2 extracts typed spans. EmbeddingGemma 2 and e5 retrieve and route. None of these generate text, so none of them can produce a malformed tool call.
- **System Two** is the generator. It writes prose, tool arguments, code and summaries. It is the slot that can be local in-process (Qwen3.5-0.8B), a local server (a larger GGUF or vLLM model), or a cloud model, and it can be swapped at any time.

Kev's own request type is called `SystemOneRequest` upstream; the name is borrowed on purpose. The design goal is to shrink what the generator must decide until a small model can do the rest, and to know from calibrated confidence when it cannot, so the agent escalates instead of guessing.

A second goal is that every System One decision is logged with its outcome. That log tunes the thresholds, measures the system, and later becomes training data for Kev itself (§16).

## 1. Charter

**What it is.** Corvid is a single-user, local-first personal agent for Linux first, then Windows and macOS. It is a .NET 10 process with a CLI/TUI, an ACP endpoint for editors, an OpenAI-compatible endpoint, messaging gateways, scheduled jobs, self-written skills, layered memory and sandboxed tools. Models run in-process through Starling by default.

**Non-goals for v1:**

- Multi-tenant hosting. Hermes users ask for it (#34352); Corvid stays one user with many profiles.
- Matching Hermes' surface area. That means about 28 platforms, about 100 slash commands and about 45 provider plugins. Hermes is a map of where we want to go, not a checklist (§17 marks every row v1, later or never).
- RL training environments. Hermes removed its own on 2026-05-15 (§17, row 1.17).
- A Python sidecar of any kind.

**Honesty clause.** With only Qwen3.5-0.8B as the generator, Corvid is a working tier, not Hermes parity. The fabric makes the small model useful for routine work: answering from memory, running known skills, simple tool calls, scheduling and filing. Long coding sessions, multi-step research and summarizing long contexts need a bigger generator. That can be a larger local model behind a local server, or a cloud model. §6 defines when Corvid escalates and how it says so.

**Dependencies on Starling, and their maturity today.**

| Model | Role in Corvid | Starling package | Maturity (2026-10-16) |
| --- | --- | --- | --- |
| Kev-0.8B, Kev-4B | Decider (all typed decisions) | `Starlings.Models.Kev` | **Experimental, unmerged.** Branch `kev-onnx` at `19af3e6`. This is the critical-path risk. |
| GLiNER2 base | Extractor (entities, slots, command targets, PII) | `Starlings.Models.Gliner2` | Stable CPU and CUDA (0.2.0) |
| EmbeddingGemma 2 | Embedder for text, image and video memory; skill and tool routing | `Starlings.Models.EmbeddingGemma2` (+ `.Extensions.AI`, imaging, video) | Text and image Stable; video Experimental |
| multilingual-e5-small | Cheap fallback text embedder, 100 languages | `Starlings.Models.E5` | Stable |
| Qwen3.5-0.8B | Default local generator | `Starlings.Models.Qwen35.Extensions.AI` (`IChatClient`, tool calling in 0.2.0) | Stable fp32x CPU and CUDA, fp16x CUDA |

## 2. Measured constraints that shape the design

These numbers are binding. Every threshold and budget below follows from them, and phase 0 (§18) remeasures them on the target hardware.

| Fact | Value | Source | Consequence |
| --- | --- | --- | --- |
| Kev-0.8B CPU, short states | about 0.37 s per question (764 questions in 4.7 min on 4 weak vCPUs, two threads) | `starling/model-pipeline/admissions/kev.md`, Spike results, step 5 | Put every question for a checkpoint into **one** request on one state. Never call Kev once per decision. |
| Kev-0.8B CPU, 2k-token state | 12.5 s state pass; 7.5k state is 51–53 s; 32k ran out of memory at 15 GB | same | The fabric state is a rendered **turn-state** of a few hundred tokens, never the transcript (§5.1). |
| Kev on CUDA | **not measured** | none | Spike S1 measures it. Until then §5 assumes CPU numbers and treats GPU as headroom. |
| Kev accuracy | decision-v7 dev 0.827, transfer-v4 dev 0.648 | same, step 2 | Kev is not an oracle off-distribution. Calibrated confidence is what makes it usable: act above a threshold, escalate or ask below it. |
| Kev state limits | validated to 8,192 tokens; refuses above the limit, never truncates | admission, Supported behaviour table | The state builder must budget, and it handles the refusal as "fabric unavailable for this checkpoint". |
| Kev state-prefix reuse | Experimental | admission | Within a checkpoint, one state pass serves all questions. Reuse across checkpoints is a later optimisation. |
| Qwen3.5-0.8B accepted context | about 8,200 tokens (evidence ends there; longer is Unsupported) | `Qwen35Capabilities.cs` | Local context budget is 8k minus the completion. Compaction thresholds are set per generator (§8). |
| Qwen conversation state reuse | **Unsupported.** Each request prefills the whole conversation (the template drops earlier thinking, so turn n is not a prefix of turn n+1) | `Qwen35ChatModel.cs` remarks | Every local turn pays a full prefill. Keep local prompts small and local tool loops short. Request it upstream (U-3). |
| Qwen prefill on CPU | one-call 1,666-token prefill about 12 s on the workstation | `Qwen35ChatClientOptions.cs` | A local loop on CPU is too slow beyond a few steps. The local generator belongs on CUDA. |
| Qwen decode on CUDA (fp32x) | about 24 ms per step | `ai-dotnet/README.md` Qwen35 evidence table | Interactive speed on GPU is fine. |
| Qwen structured output | `ResponseFormat` JSON throws; no grammar or llguidance hook | `Qwen35ChatRequestMapper.cs` | v1 tool calling must work **without** constrained decoding (§7). Request it upstream (U-2). |
| Hardware | RTX 3090 and RTX 4060 Ti; the 4060 Ti is often nearly full | memory note from the Roost kickoff | Default placement: generator on the 3090; Kev, GLiNER2 and embedders on CPU or the 3090. Placement is configuration (§4.4, D-6). |

## 3. Architecture

### 3.1 Layers

```
 Surfaces     CLI/TUI · ACP (editors) · OpenAI-compatible HTTP · MCP server · Gateway adapters (Telegram, Discord, Slack, Signal, email, webhook)
                 │  all speak one Envelope type over Channel<T>
 Kernel       Session actor ─ Turn loop ─ Event log (append-only, SQLite) ─ Scheduler ─ Background jobs
                 │
 System One   Fabric: TurnState builder → Kev (batched questions) · GLiNER2 · Embedders · Policy (thresholds, escalation)
 System Two   Generator slot (IChatClient pipeline: redaction → budget → tool shortlist → model) · Summarizer slot · Vision slot
                 │
 Capabilities Tools (source-generated AIFunctions) · MCP client · Skills · Memory (core, episodic, semantic graph, procedural) · Sandbox
                 │
 Platform     Starling models (in-process ONNX) · local servers (OpenAI-compatible) · cloud providers · OpenTelemetry
```

### 3.2 The kernel is an event log, not a message list

Hermes keeps a message list and a SQLite mirror. Compaction rewrites the list. Session ids rotate, so a `/goal` can be lost after compaction (#33618, #45059) and context is lost permanently (#12131).

Corvid stores every session as an append-only log of typed events, in the style of OpenHands. The event types are `UserMessage`, `AssistantMessage`, `ToolCallIssued`, `ToolResult`, `Decision` (every System One answer), `ApprovalRequested` and `ApprovalResolved`, `ModelSwitched`, `Compacted`, `MemoryWritten`, `SkillChanged`, `GoalSet` and `CheckpointTaken`. The prompt for any model is a **projection** of the log:

- Compaction appends a `Compacted` event whose summary replaces a range *in the projection only*. The raw events stay and remain searchable.
- Branching, forking, `/undo`, `/retry` and resume all reduce to choosing a log position.
- Goals, todos and pending approvals are projections of their own events. They live outside the transcript, so compaction cannot delete them.
- A model switch is just an event, and the next projection is rendered for the new model.
- Replay is free. The eval harness (§16) replays logs against new thresholds or new models.

### 3.3 Concurrency

- One **session actor** per session: a single-reader `Channel<SessionCommand>`, so there are no locks in the turn loop.
- Surfaces post `Envelope`s into a bounded channel with backpressure.
- Tools run as `Task`s with per-tool concurrency limits. Read-only tools run in parallel. Side-effecting tools serialize by default.
- Model calls go through a **device scheduler** per GPU and per CPU pool. Starling serializes native CUDA work by default, so the scheduler queues by priority instead of letting requests contend:
  1. interactive generator
  2. fabric
  3. subagents
  4. background consolidation
- Starling's model calls are synchronous native work (`KevDecisionModel.Decide`, GLiNER2 and embeddings). Adapters run them on the device scheduler's threads, never on the session actor, and return `Task`s.
- Cancellation is a `CancellationToken` end to end. Starling's clients check it between prefill chunks and decode steps.

These are what Hermes cannot do cheaply. It uses a background thread per API call and a thread pool for tools (#11431), and an eager-import gateway that ran to about 8 GB in an hour (#18438).

### 3.4 Solution layout

```
src/
  Corvid.Abstractions        Envelope, events, IToolset, ISkillStore, IMemory, ModelSlot, policies (no dependencies)
  Corvid.Kernel              session actor, turn loop, event log, projections, scheduler
  Corvid.Fabric              TurnState, question catalogue, Kev/GLiNER2/embedding adapters, calibration store
  Corvid.Models              slot registry, profiles, IChatClient middleware (budget, redaction, shortlist, repair)
  Corvid.Models.Starling     in-process adapters (Starlings.* packages)
  Corvid.Models.OpenAICompat local servers (llama.cpp, Ollama, vLLM, LM Studio) and OpenRouter
  Corvid.Models.Anthropic    native Messages API with prompt caching
  Corvid.Memory              SQLite store: FTS5, sqlite-vec, temporal graph, core blocks
  Corvid.Skills              agentskills.io loader, selector, learning loop, curator
  Corvid.Tools               built-in tools + Corvid.Tools.Generators (Roslyn source generator)
  Corvid.Sandbox             Linux: bubblewrap + Landlock + seccomp; container backend; SSH backend.
                             The policy is applied by a small shared launcher, corvid-sandbox, that Corvid Vault also uses
  Corvid.Mcp                 client and server (ModelContextProtocol C# SDK)
  Corvid.Acp                 Agent Client Protocol over stdio
  Corvid.Gateway             adapter host, delivery ledger, pairing; Corvid.Gateway.<Platform>
  Corvid.Cli                 Spectre.Console scrollback UI, slash commands
  Corvid.Host                generic host, configuration, DI, OpenTelemetry, single-file publish
  Corvid.Vault.Client        socket client, VaultHandler HttpClient handler, SecretRef type (no secret values ever)
vault/
  corvid-vault               SEPARATE executable and service (Native AOT, no models, no Corvid.* dependencies except the wire contract):
                             encrypted store, egress proxy, ssh-agent, scrub service, policy, audit; see vault.md
evals/                       golden trajectories, decision sets, replay harness
```

Libraries take only `Microsoft.Extensions.AI.Abstractions` plus their own needs. Provider SDKs live only in the `Corvid.Models.*` adapter packages. This mirrors Starling's boundary rule and keeps the core free of provider lock-in.

## 4. Model slots and switching

### 4.1 Slots

"Switch model and type of model at any time" means **per-role slots**, each independently swappable at runtime:

| Slot | Default | Alternatives | Contract |
| --- | --- | --- | --- |
| `generator` | Qwen3.5-0.8B in-process (CUDA) | local server (e.g. a 9B–35B GGUF), cloud (Anthropic, OpenAI, OpenRouter, Gemini) | `IChatClient` |
| `summarizer` | same as generator | a larger-context model; required when the context exceeds the local window (§8) | `IChatClient` |
| `decider` | Kev-0.8B | Kev-4B (escalation decider), a cloud model through a Kev-shaped adapter, or none (degraded tier, §5.6) | `IDecider` (Kev request in, Kev answers out) |
| `extractor` | GLiNER2 | a cloud model with a JSON schema | `IExtractor` (text + labels → spans) |
| `embedder.text` | EmbeddingGemma 2 | e5-small (fast and multilingual), cloud embeddings | `IEmbeddingGenerator<string, Embedding<float>>` |
| `embedder.media` | EmbeddingGemma 2 image and video | none | typed Starling API |
| `vision` | cloud multimodal model, on request | a local VLM server | `IChatClient` with image content |

Each slot binding carries a **model profile**: context window, accepted context, tool-call support (native, template-rendered or none), structured-output support, prompt-caching style, cost per token, privacy class (`local`, `local-server` or `cloud`), and device placement. Policies read the profile and never assume a model name.

### 4.2 Switching semantics, including mid-turn

The history is provider-neutral: Microsoft.Extensions.AI `ChatMessage` content in the event log. A `/model` command or an automatic escalation runs these steps:

1. **Let issued work finish.** Tool calls already issued complete and their results are logged. Nothing in flight is cancelled unless the user cancels.
2. **Keep pending approvals.** They are events, so they survive the switch.
3. **Append `ModelSwitched`** with the old and new profiles and the reason (user, escalation or fallback).
4. **Re-run budget policy against the new profile.** Moving from 8k local to 200k cloud undoes clearing and expands compacted ranges back out of the log where the budget allows. Moving from 200k cloud to 8k local compacts before the next call (§8).
5. **Re-render the projection** for the new model's format: native tool calls, Qwen's template, or Anthropic content blocks with cache breakpoints.
6. **Re-check privacy.** Moving from local to cloud passes the redaction gate (§10.4) before the first cloud request.
7. **Continue the turn.** The next model call uses the new slot.

Fallback chains (cloud 429/5xx → next provider → local) use the same path. Prompt caches are lost on a switch, as in any harness. Corvid at least records the cost in the decision log, so frequent switching shows up in `/insights`.

### 4.3 Embedder swaps

Changing `embedder.text` invalidates every stored vector. Corvid keeps vectors in a table keyed by `(item, embedder_id)`:

- A swap starts a background re-embed job, the same kind of idle-time job as consolidation (§9.5).
- Until the job finishes, retrieval uses the old index for vectors and FTS5 for everything.
- Rows switch over atomically per item.

### 4.4 Placement

Placement is configuration. The defaults are:

- Generator on the 3090 under CUDA.
- Kev-0.8B on CPU until spike S1, then wherever S1 says.
- GLiNER2 and e5 on CPU (they are small and fast).
- EmbeddingGemma 2 on CPU for text and on GPU for video.

The device scheduler (§3.3) owns placement at runtime. `/status` shows a live table of slot, device, queue depth, p50 latency and memory.

## 5. The decision fabric

### 5.1 TurnState: what Kev sees

Kev must never see the transcript. The fabric builds a **TurnState**, a small JSON object that is rendered once per checkpoint and passed as `KevContent.Json(...)`. Kev renders JSON objects as `key: value` lines, so field names are part of the prompt and are chosen to read well.

```json
{
  "user_message": "can you move tomorrow's dentist reminder to friday and tell sam",
  "surface": "telegram",
  "recent": ["user asked about dentist reminder (cron job 14)", "assistant listed 3 reminders"],
  "goal": null,
  "entities": {"date": ["tomorrow", "friday"], "person": ["sam"], "task": ["dentist reminder"]},
  "step": 0,
  "last_tool_result": null,
  "generator": "qwen3.5-0.8b local, 8k"
}
```

Budget: 600 tokens by default, with a hard stop at 1,500. Each field has a token budget and a deterministic truncation rule. `recent` is a list of event one-liners drawn from the log, not message text. `entities` come from GLiNER2.

**State and instructions are separate places.** Kev's contract runs the shared `state` once and then gives each question its own row with its own `instructions` (which also accept `KevContent.Json`). Content that only one question needs goes in that question's instructions or options, not in the state:
- Candidate tools and skills (from §5.4) are the **options** of `tool` and `skill`, rendered as `name: description`. Kev was trained to read the description at each option's closing token.
- The old tool results for `clear_results` and the retrieved neighbours for `memory_op` go in those questions' instructions.

Rows in one pass are right-padded to the longest, and each row is bounded by `KevOptions.MaxRowTokens` (8,192). The 0.37 s figure in §2 was measured with rows up to 853 tokens, so the row budget is 1,000 tokens per question.

When the turn-state would exceed the hard stop, or Kev refuses it, the checkpoint falls back to the degraded tier (§5.6) and logs the fact.

### 5.2 Checkpoints and batching

Each turn has up to four kinds of fabric checkpoint. Each one is a **single** `KevRequest` with several questions on one state:

| Checkpoint | When | Typical questions |
| --- | --- | --- |
| `ingress` | user message arrives | intent, tool need, difficulty, memory recall need, clarify need, injection check on attachments |
| `step` | after each tool result, before the next generator call | done/continue, next tool, stuck, escalate, result-clearing |
| `pre-execute` | after the generator emits a side-effecting call, before it runs; also before a memory or skill write | `risk` over the parsed action, `memory_op`, `skill_write_safe`. Runs only for tools with side-effect metadata, so read-only calls cost nothing. |
| `egress` | before the answer is delivered | worth remembering, skill opportunity, follow-up scheduling, delivery channel |

In code this follows the Kev API on the `kev-onnx` branch:

```csharp
var questions = new Dictionary<string, KevQuestion>
{
    ["intent"] = KevQuestion.Choice("What does the user need right now?",
        "answer_directly", "use_tools", "recall_memory", "run_skill", "ask_clarifying_question"),
    ["needs_tools"] = KevQuestion.Noul("Does completing this require running a tool?"),
    ["difficulty"] = KevQuestion.Score("How hard is this for a small local model?",
        "trivial", "easy", "moderate", "hard", "needs a frontier model"),
    // Options carry their descriptions: Kev reads "name: description" at each option's closing token.
    ["tool"] = KevQuestion.Choice("Which tool should run first?",
        shortlist.Select(t => KeyValuePair.Create(t.Name, (KevContent?)t.Description))
                 .Append(KeyValuePair.Create("none", (KevContent?)"no tool is needed"))),
};
KevDecision d = kev.Decide(new KevRequest(KevContent.Json(turnState.ToJsonElement()), questions), ct);
// d["intent"].Choice, d["intent"].Confidence, d["needs_tools"].Yes, d["difficulty"].ExpectedScore, d["tool"].Probabilities
```

Every answer is appended as a `Decision` event with these fields: question id, the answer's probabilities, the confidence, the threshold applied, the action taken, and later the **outcome**. The outcome is one of: the tool succeeded, the user approved or denied, the user corrected the agent, the answer was accepted, or the user retried.

### 5.3 The question catalogue

Every point where Hermes uses the generator, a regex or a flag becomes a typed question. A **floor** is the confidence below which Corvid does not act on the answer. Floors start at the values below and are retuned from the decision log (§5.5). Kev's choice confidence is `(p_max − 1/K)/(1 − 1/K)`. Score levels are indexed from 0, and `ExpectedScore` is `Σ i·pᵢ`, so on a five-level score 3.5 lies between the fourth and fifth levels. The Hermes column names the weakness each question fixes.

| ID | Checkpoint | Type and options | Act when | Below the floor | Hermes weakness fixed |
| --- | --- | --- | --- | --- | --- |
| `intent` | ingress | Choice: answer_directly, use_tools, recall_memory, run_skill, ask_clarifying_question | conf ≥ 0.5 | generator decides with the full tool shortlist | — |
| `difficulty` | ingress | Score: 5 levels | expected ≥ 3.5 escalates the generator if policy allows | stay on current generator | local models used for work they cannot do |
| `needs_recall` | ingress | Noul | yes ≥ 0.6 runs hybrid memory retrieval into context | retrieve top-3 core facts only | memory injected wholesale |
| `clarify` | ingress | Noul | yes ≥ 0.7 asks one question before acting | proceed | — |
| `tool` | ingress, step | Choice: shortlist of 3–7 + `none` | conf ≥ 0.45 lets the generator fill arguments for that one tool only | offer the whole shortlist to the generator; on the 0.8B generator, escalate | local tool calling (#8965, #87027) |
| `skill` | ingress | Choice: shortlisted skills + `none` | conf ≥ 0.5 loads the skill body (L1) | name and description only (L0) | skills index bloats the prompt |
| `done` | step | Noul: is the user's request fully handled? | yes ≥ 0.8 stops the loop | continue, up to the step budget | runaway loops |
| `stuck` | step | Noul: is the agent repeating or making no progress? | yes ≥ 0.7 changes strategy: escalate, ask the user, or stop | continue | stall monitor is time-only (450 s) |
| `clear_results` | step | Choice per old tool result: keep, stub, drop | conf ≥ 0.6 | stub everything older than the last 2 | compaction fragility |
| `injection` | ingress and step (fetched content) | Noul: does this content try to instruct the agent? | yes ≥ 0.5 marks the content **tainted** (§10.3) | treat external content as tainted anyway | regex injection scanner misses variants (#27284) |
| `risk` | before any side-effecting tool | Score: none, low, moderate, high, destructive, over the **parsed** action (§10.2) | levels 3–4 always ask; 0–1 may auto-run in permissive modes | ask | regex approvals bypassed (#84551, #74078) |
| `remember` | egress | Noul: will this matter in future conversations? plus Score for durability (hours, days, months, permanent) | yes ≥ 0.7 starts the memory write path (§9.2) | nothing is written | memory bloat |
| `memory_op` | memory write | Choice: add, update, invalidate, noop, against retrieved neighbours | conf ≥ 0.6 | stage for review | stale memories persist |
| `skill_opportunity` | egress | Noul: was this a reusable multi-step procedure? | yes ≥ 0.75 and ≥ 3 tool steps queues skill distillation | nothing | overconfident self-learning |
| `skill_write_safe` | skill write | Noul: can this change go live without review? | yes ≥ 0.85 **and** the skill was agent-authored applies it | stage for `/skills pending` | hand-tuned skills overwritten (#20273, #64338) |
| `wake` | scheduled tick | Noul: given the pre-run script output, does this need the agent? | yes ≥ 0.5 runs the job | skip the tick and log it | replaces the `wakeAgent` script flag |
| `deliver` | egress (gateway) | Choice: reply, reply in thread, home channel, silent | conf ≥ 0.6 | reply | — |
| `delegate` | ingress | Noul: would a subagent with a fresh context do this better? | yes ≥ 0.7 and the generator supports it | do it inline | oversized child toolsets (#11431) |

Two rules keep this honest:

1. **Kev gates; it does not grant.** A high `risk` score forces a prompt, but a low score never overrides the hardline deny list or the sandbox (§10). A high `skill_write_safe` score never lets a hub-installed or user-edited skill change silently.
2. **The generator never sees a tool it was not offered.** If `tool` picks `cronjob.update`, the generator receives exactly that tool's schema. Its only job is to fill the arguments.

### 5.4 Retrieval feeding the fabric

Candidate tools and skills come from embeddings, not from the model reading every description:

- Each tool and skill is embedded **multi-vector**: its description plus 5–10 synthetic example requests, generated once by the generator or a cloud model and stored. RAG-MCP reports 43% versus 14% tool-selection accuracy from retrieval alone.
- The shortlist is the top-k by max-similarity, boosted by recency of use in this session. Hard filters run first: toolset enabled, platform allowed, `check_fn` available.
- GLiNER2 slots narrow it further. A `date` entity boosts scheduling tools; a `url` entity boosts web tools.

### 5.5 Calibration

Kev's temperature is fitted on its own training distribution. Ours is different, so Corvid calibrates per question:

- The decision log records the outcome of every acted-on answer. User corrections, denials, retries and tool failures count as negative outcomes.
- `corvid evals calibrate` fits a per-question floor that holds a target precision (default 0.9 for gating questions, 0.8 for routing ones). It reports reliability diagrams, escalation rate and the agreement between Kev-0.8B and Kev-4B.
- Floors are versioned configuration. Replays against the log (§3.2) show what a new floor would have changed before it ships.
- **Two-tier decider.** When Kev-0.8B is below the floor on a gating question (`risk`, `skill_write_safe`, `memory_op`), the same state goes to Kev-4B before falling back to the user or the generator. Spike S1 decides whether Kev-4B is fast enough on the 3090 to be the default for those questions.

### 5.6 Degraded tier: no Kev

Kev is unmerged and Experimental. Corvid must work without it:

| Question shape | Without Kev |
| --- | --- |
| `tool`, `skill`, `deliver` (Choice over named items) | embedding similarity between the turn-state and each option's description; top-1 if the margin is above 0.05, otherwise the whole shortlist goes to the generator |
| Noul questions | the generator answers "yes" or "no". Its logprobs could serve as a confidence if a slot exposed them, but `IChatClient` has no logprobs surface and Starling's client does not expose them today. So the conservative default applies whenever the answer gates anything. |
| `risk`, `skill_write_safe`, `memory_op` (gates) | **no calibrated confidence, so the gate defaults to asking the user** |
| `difficulty` | token-length and tool-count heuristics; escalation becomes manual (`/model`) |

The degraded tier is also the A/B baseline for spike S2: the same golden set run with and without Kev.

## 6. Generator policy and escalation

- **Escalation triggers:**
  - `difficulty` above the floor
  - two failed argument repairs (§7)
  - `stuck` above the floor
  - the context does not fit the local window after compaction
  - the user asks for it
- **What escalation targets:** a profile-ordered list, for example local 0.8B → local-server 27B → cloud. Each step must be allowed by the privacy policy for the current content (§10.4).
- **Escalation is visible.** The CLI shows "escalating to <model>: <reason>", the gateway appends a short footnote, and `/insights` reports the escalation rate as a headline metric.
- **De-escalation.** After an escalated turn finishes, the slot returns to the default unless the user pinned the model.
- **Architect/editor split** (from Aider): for coding, the escalated model plans and the local model applies mechanical edits. Phase 3.

## 7. Tool calling that works with small models

Hermes removed its client-side tool-call parsers along with Atropos on 2026-05-15. Local models there depend on the server emitting OpenAI-format `tool_calls`, which is the source of #8965, #84158, #87027 and #32660.

Starling already renders the Qwen template and parses tool calls in-process (`Qwen35ToolCallParser`). That is the first "better in .NET" that is true today. On top of it, each turn runs this pipeline:

1. The shortlist comes from §5.4, and `tool` from the fabric. With a confident pick, the generator sees **one** tool schema.
2. The generator emits a call. Starling's parser returns `FunctionCallContent` with arguments cast against the schema.
3. **Validate:** JSON Schema validation, plus argument-level checks generated with the tool (enum membership, path inside the workspace, date parse).
4. **Slot pre-fill:** GLiNER2 has already extracted entities with labels taken from the tool's parameter descriptions. If the generator's argument disagrees with a high-confidence extracted span, or omits it, the repair step shows both.
5. **Repair once.** The error and the expected shape go back to the generator in a short message. A second failure escalates (§6).
6. **No-op detection.** If the generator narrates a tool result instead of calling the tool (#87027), the fabric's `done` question will be low and `tool` will be high. Corvid re-prompts with tool mode set to require that tool.

**Constrained decoding (later, upstream U-2).** ONNX Runtime GenAI integrates llguidance. When Starling exposes it, Corvid builds a Lark grammar per turn: either free text, or the union of `{name: const, arguments: schema}` for the offered tools. Invalid names and malformed arguments then become impossible, and step 5 becomes rare.

**Tool registry.** Tools are C# methods with attributes. A Roslyn incremental generator emits the `AIFunction` wrapper, the JSON Schema (`AIJsonUtilities` at compile time), the validator, the risk metadata (`[SideEffect(Filesystem)]`, `[Network]`, `[Destructive]`) and the embedding examples file. Parameters of type `SecretRef` are rendered as an enum of Vault catalogue names, so a model can only name a secret, never supply one (§10.5). That means no reflection and Native AOT compatibility. Toolsets, platform presets and `check_fn` availability work as in Hermes. MCP tools arrive as `McpClientTool`, which is already an `AIFunction`, and get the same treatment: embedded, shortlisted and risk-scored.

**Tool search.** When a cloud generator is active and the shortlist is too small, Corvid exposes `tool_search` and `tool_call`, as Hermes and Anthropic do. Local generators never see the search tool, because the fabric does the searching.

## 8. Context management

Budgets come from the generator profile: accepted context minus completion minus a reserve. The levers apply in cost order:

1. **Clear old tool results** (`clear_results`): replace them with stubs such as `[cleared: web_extract of example.com, 14 KB; re-run or see event 812]`.
2. **Drop the skill body** once the skill has completed.
3. **Compact.** Summarize a range into a **structured record**: goal, constraints, decisions, files, open questions, next step. Corvid keeps Hermes' template but stores it as JSON in the `Compacted` event, so later compactions update fields instead of rewriting prose.
4. **Lineage.** Compaction never deletes. The raw range stays in the log and in FTS5.

**The summarizer problem.** Hermes admits that when the auxiliary model's window is smaller than the main model's, the middle is dropped without a summary. Corvid inherits the same risk: a 0.8B summarizer cannot read a 100k cloud transcript. The rule is:

- If the range fits the summarizer, summarize it in one call.
- Otherwise, map-reduce: split the range into chunks that fit, summarize each into the structured record, then merge the records. This works because merging JSON records is easy for a small model.
- If the merged record still does not fit, escalate the summarizer, or keep the range uncompacted and clear more results. It never silently drops content.

**Goals and todos.** `/goal`, `/subgoal` and the todo list are projections of their own events (§3.2). They are re-injected after every compaction, so they cannot be lost (#33618, #45059).

**Prompt caching.** For Anthropic, Corvid uses the Hermes layout: a frozen system prompt plus breakpoints on the last messages. Like Hermes, it delivers changes to memory and skills as one-shot notes to keep the prefix stable. For local Qwen there is no cross-request cache (§2), so the local system prompt is kept short: under 1,000 tokens, with skills and memory retrieved per turn rather than injected wholesale.

## 9. Memory

### 9.1 Tiers

| Tier | What | Store | In the prompt |
| --- | --- | --- | --- |
| Core blocks | `user` (who the user is), `agent` (standing notes), `persona` (SOUL) | SQLite rows with a size budget per block | Always, as a frozen snapshot per session (Hermes' cache-friendly design, kept) |
| Episodic | every event of every session | event log + FTS5 (with a CJK tokenizer) + sqlite-vec | On demand: `session_search`, or `needs_recall` |
| Semantic | entities, relations and facts with time | bi-temporal graph in SQLite tables | On demand: top facts about the entities in the turn-state |
| Procedural | skills | agentskills.io folders, git-versioned | L0 index retrieved per turn, L1/L2 on demand |
| Media | images, screenshots, video frames shared with the agent | EmbeddingGemma 2 vectors + thumbnails | On demand; text queries find images ("the whiteboard photo from Tuesday") |

### 9.2 Write path (replaces the monolithic `memory` tool)

At egress, `remember` (§5.3) decides whether anything in the turn is worth keeping. If so:

1. GLiNER2 extracts entities and relations with labels from the profile's schema (person, organization, place, project, preference, date, commitment) plus free labels.
2. Hybrid retrieval finds neighbouring facts about the same entities.
3. `memory_op` chooses add, update, invalidate or noop against those neighbours, using Mem0's operation set. An invalidation sets `valid_to`; it does not delete, as in Graphiti, so "what did I believe in March" stays answerable.
4. Core-block edits (`user` and `agent`) go through the same gate. When a block passes its budget, a consolidation job proposes a rewrite instead of returning an error as Hermes does.
5. Low confidence stages the write for `/memory pending`, which is Hermes' `write_approval`, now applied only where it is needed.

The generator can still call `memory.add` explicitly. Those calls pass the same gate.

### 9.3 Read path

Hybrid retrieval runs BM25 (FTS5) and vector search (sqlite-vec) and merges them with reciprocal rank fusion. It then boosts for recency, expands one hop across the graph from the turn's entities, and optionally reranks with Kev: a Choice over the top 8 snippets, "which of these answers the question?". The result is capped by the generator's budget.

### 9.4 Media memory

When an image or video arrives (gateway or CLI), Corvid embeds it with EmbeddingGemma 2 in the same space as text and stores the vector, a thumbnail and the caption context. Qwen3.5-0.8B is text-only, so local "what is in this image" questions go to retrieval plus the `vision` slot when allowed. Finding the image only needs the embedding, which runs locally.

### 9.5 Sleep-time jobs

Background jobs run on the device scheduler at the lowest priority, when the user is idle:

- Deduplicate and merge facts.
- Rewrite core blocks to their budget.
- Re-embed after an embedder swap.
- Distil skills from queued `skill_opportunity` turns.
- The skill curator: mark stale skills, archive, and propose merges. Pinned skills are never touched.
- Recalibrate fabric floors from new outcomes.
- Compact old sessions into episode summaries.

Every job writes events and is reviewable with `/journey` (Hermes' timeline view).

## 10. Safety

### 10.1 Two independent policies

Codex CLI's model is the floor: the **sandbox policy** (what the process *can* do) is separate from the **approval policy** (what Corvid *asks* about).

- **Sandbox.** On Linux, shell and code tools run under bubblewrap with Landlock filesystem rules (workspace read-write, home read-only, `~/.ssh`, `~/.gnupg` and `.env` denied) and seccomp. Network is off by default and on per toolset. Container and SSH backends are alternatives. Modal-style cloud backends come later.
- **Approval.** Applies to every side-effecting tool, not only the shell: write, patch, send_message, cron changes and MCP tools with side-effect metadata. Hermes covers only the shell (#35357).

### 10.2 Approval on parsed structure, not regex

Hermes' regex patterns are bypassed by `timeout`, `bash -c` and numeric PIDs (#84551, #74078). Corvid works on structure instead:

1. **Parse.** Shell commands go through a real bash parser (tree-sitter-bash through a .NET binding, decision D-5). Wrappers such as `timeout`, `env`, `nice`, `xargs`, `sh -c` and `bash -c` are unwrapped recursively, and substitutions are found.
2. **Extract targets.** Static rules pull paths, PIDs, hosts and URLs from the AST. GLiNER2 handles free-form arguments (SQL strings, `curl` data, `kill` targets), with labels: file path, process, host, database table, credential.
3. **Hardline deny.** Structural, non-model and not overridable even in yolo mode: `mkfs`, raw device writes, recursive deletes outside the workspace, writes to credential stores, and changes to Corvid's own safety configuration.
4. **Score risk.** `risk` scores the **parsed** action (verb, resolved targets, whether each is inside the workspace, whether it is reversible given the checkpoint state, and whether the turn is tainted).
5. **Decide.** Policy maps the score and mode (manual, smart, permissive) to auto, ask or deny. Approvals offer once, session or always (stored as a structural rule, not a string). Unanswered approvals deny after a timeout, as in Hermes.

Changing approval or sandbox settings is itself a destructive action that needs approval (#59293).

### 10.3 Taint tracking for prompt injection

Content from outside the user is **tainted**: web pages, fetched files, email bodies, MCP results from untrusted servers, and messages from other gateway users.

- A turn whose context holds tainted content runs with a reduced capability set. Network egress tools and `send_message` to new recipients require approval, whatever `risk` says. This is the CaMeL / dual-LLM idea at the granularity Corvid can afford.
- `injection` (§5.3) adds detection on top, but taint applies whether or not detection fires.
- Taint is cleared only by an explicit user message.

### 10.4 Privacy egress

Before any request to a slot with privacy class `cloud`:

- GLiNER2 runs PII labels (person, email, phone, address, account number, credential, plus user-defined labels) over the outgoing projection.
- The profile's policy decides: send as is, redact to stable placeholders (`<PERSON_1>`), mapped back in the response, or refuse and stay local.
- Core memory blocks are marked `local-only` by default.
- Cloud provider API keys never live in Corvid. Model slots authenticate through Vault's egress proxy (§10.5).

Hermes has no equivalent. Hermes even sent web search to a third party without opt-in (#45058).

### 10.5 Secrets: Corvid Vault

Secrets live in **Corvid Vault** (`corvid-vault`), a separate, model-free process that Corvid cannot read. In the strong tier it runs under a different OS user. The full design is in [`vault.md`](vault.md). In short:

- Corvid and every model see only secret names and per-session placeholders.
- Vault substitutes real values at the network edge, only for hosts the secret is bound to. It also acts as ssh-agent and gpg-agent, and as a last resort runs a command with the value injected.
- Vault scrubs every byte it relays. The kernel also passes every tool result through Vault's scrub service before the event log.
- Each secret has its own policy (hosts, methods, paths, budgets, taint). High-value uses need an out-of-band confirmation that Corvid cannot forge.
- Credentials pasted into chat are intercepted at ingress and moved into Vault.
- Corvid never reads secrets from environment variables. When Vault is absent, secret-bearing tools are unavailable.
- The hardline deny list (§10.2) and the sandbox's denial of secret paths stay in place as defence in depth.

### 10.6 Supply chain

- **Skills and plugins from hubs.** Scanned like Hermes' scanner, plus `injection` over the SKILL.md body. Scripts in skills always run sandboxed.
- **Plugins.** .NET assemblies load into a collectible `AssemblyLoadContext` and declare their capabilities in a manifest. A tool from a plugin gets no permissions its manifest did not declare.

## 11. Skills

- **Format.** agentskills.io `SKILL.md` folders, compatible with Hermes and Claude Code, so existing skill libraries import as is. Hermes' metadata (`requires_toolsets`, `fallback_for_tools`, `platforms`) is read and honoured.
- **Selection.** The L0 index is not injected whole. The embedding shortlist (§5.4) plus `skill` selects the skill, and the L1 body loads on a confident pick. A cloud generator may also see the top-10 index.
- **Versioning.** The skills directory is a git repository. Every change is a commit authored by `user`, `agent` or `hub:<source>`. `/skills diff` and `/skills revert` use git. The curator and learning loop may only modify `agent`-authored files. That rule is the structural fix for #20273 and #64338.
- **Learning loop.**
  1. `skill_opportunity` queues a turn.
  2. A sleep-time job distils a draft skill. It uses the escalated generator when allowed, because skill authoring is System Two work.
  3. The draft must **pass its own test**: Corvid replays the source turn with the draft loaded, against the event log, and checks that the tool sequence and outcome match.
  4. `skill_write_safe` gates applying it.

  Hermes' self-evaluation is the documented weak point. A replay test and a calibrated gate replace "the model thinks it went well".
- **Skill evals.** A skill can carry `evals/` cases (prompt → expected tool sequence). The curator runs them after any change and on model swaps.

## 12. Surfaces and the gateway

- **CLI/TUI.** Spectre.Console with a scrollback-first renderer (the Claude Code and pi style): streaming output, a live tool panel, approvals inline, and Ctrl+P. Slash commands are a registry shared with the gateway, so each one is defined once. The v1 set is about 35 commands (§17).
- **ACP** over stdio so the agent runs inside Zed, JetBrains and VS Code. It provides diffs, approvals, session load and resume, and fork, which maps to a log position.
- **OpenAI-compatible HTTP** (`/v1/chat/completions` and `/v1/models`), so Open WebUI and other front ends work. **MCP server** mode exposes Corvid's memory and skills to other agents.
- **Gateway.**
  - One hosted service with adapter plugins.
  - v1 adapters: Telegram, Discord, Slack, Signal, email (IMAP/SMTP) and webhook.
  - The delivery ledger is a table of the event log with at-least-once redelivery.
  - Pairing codes and allowlists work as in Hermes.
  - Per-chat sessions; per-channel slot and persona overrides.
  - Adapters load lazily, each in its own scope, so a hanging Telegram connect cannot stall the host (#67498, #72589).
- **Voice.** STT through whisper.cpp bindings or a Starling Whisper profile when one exists (decision D-7). TTS is a slot with local and cloud bindings.

## 13. Scheduler, goals and background work

- **Durable jobs.** Jobs live in SQLite and use Hermes' schedule grammar ("in 30m", "weekdays at 9am", cron, ISO). Every run is a fresh session linked to the job.
- **Pre-run scripts.** A script's output feeds `wake`, so "check the inbox, wake the agent only if something needs me" costs one Kev request, not one generator call. `no_agent` jobs skip models entirely.
- **`/goal`.** A standing goal is an event. Each turn's `done` question is asked against the goal as well as the request, and the goal persists across compactions and restarts.
- **Heartbeat.** An optional periodic tick (OpenClaw's idea) runs the egress checkpoint over recent events and offers follow-ups. It is off by default.

## 14. Subagents

- `delegate_task` starts a child session actor with a fresh log, a goal, an explicit toolset (not inherited wholesale: #11431), a budget and an optional output schema that is validated on return.
- Children run in parallel when their slots are cloud or local-server. In-process GPU children queue behind the interactive turn on the device scheduler, so there is no 10-way contention on one GPU.
- Children cannot use `clarify`, `memory` writes, `send_message` or `cronjob` (Hermes' rule, kept). Their decisions and outcomes still feed calibration.
- Kanban-style durable multi-agent boards come later. The event log and jobs table already provide the substrate.

## 15. Code execution and checkpoints

- **Code mode.** This is a cloud- and local-server-generator feature; 0.8B models write poor code. The script runs sandboxed and calls Corvid tools over JSON-RPC on a Unix socket, as in Hermes. Languages:
  - .NET 10 file-based apps (`dotnet run script.cs`) with a generated `CorvidTools` client
  - Python and JavaScript through the same RPC stub
  - Hermes' limits are kept: timeout, call count and output size.
- **Checkpoints.** A shadow git store per project. A checkpoint is taken before any write, patch or destructive shell action, and recorded as a `CheckpointTaken` event, so `/rollback N` and `/undo` share one model. A write ledger preserves the user's own edits during restore.

## 16. Observability, evals and the learning flywheel

- **OpenTelemetry GenAI spans:** `invoke_agent` → `chat {model}` → `execute_tool {tool}`, plus `corvid.decide` spans with question ids and confidences. An Aspire dashboard for local development.
- **Event log as dataset.** `corvid export` writes:
  - trajectories in Hermes' ShareGPT format (for anyone fine-tuning generators)
  - **Kev training records**: turn-state, question, options, the acted answer and the outcome
  
  Kev's training code is Apache-2.0 and the models are LoRA on Qwen3.5 bases. A personal Kev adapter fine-tuned on one user's own decisions is the long-term payoff. That is a later phase, and it depends on Starling supporting adapter swaps (U-5).
- **Golden sets.** Per-capability eval suites (tool choice, argument exact match, task success, escalation rate, approval precision). They run in CI per generator profile and per fabric version, the same way Starling gates models.

## 17. Hermes parity

Rows follow the numbering of the Hermes feature report (16 Oct 2026, v0.22.2). Phase: **v1**, **later** or **never**.

| # | Hermes | Documented issue | Corvid | Models | Phase |
| --- | --- | --- | --- | --- | --- |
| 1.1 | CLI, Ink TUI, desktop app, web dashboard, OpenAI-compatible API, ACP, Python library | slow eager imports (#72589) | Spectre CLI, ACP, OpenAI-compatible API in v1; NuGet library (`Corvid.Kernel`) for embedding; desktop and web later | — | v1 / later |
| 1.2 | about 25 CLI subcommands | setup-wizard loops | `corvid` with chat, model, config, gateway, skills, memory, cron, doctor, export, evals; typed config validated at startup | — | v1 |
| 1.3 | about 100 slash commands | surface sprawl | about 35 in v1 from one registry (session, steering, `/goal`, `/model`, `/approvals`, `/rollback`, `/skills`, `/memory`, `/insights`, `/status`); mascots never | — | v1 |
| 1.4 | about 28 messaging platforms, pairing, delivery ledger, voice, profiles, Bot Mode | memory leak (#18438), Telegram hangs | 6 adapters in v1, lazily loaded and isolated; ledger in the event log; `deliver` decides routing | Kev | v1 / later |
| 1.5 | 70+ tools, 28 toolsets, 7 terminal backends, browser backends, tool search | tool calls leak as text on local models (#8965, #84158, #87027, #32660) | source-generated tools; embedding shortlist + `tool` + one-schema generation + validate/repair; local, container and SSH backends; browser through Playwright for .NET; tool search for cloud generators | Kev, embedders, GLiNER2 | v1 |
| 1.6 | `execute_code` Python over UDS RPC, persistent kernels | different block list from the shell (#74078) | same RPC design, one structural policy for all execution paths, .NET file-based apps plus Python and JS | generator (cloud or local server) | later |
| 1.7 | `delegate_task`, 10 concurrent, depth 1, stall monitor | oversized child toolsets (#11431) | child session actors, explicit toolsets, device-scheduled GPU work, `stuck` replaces time-only stalls | Kev | v1 |
| 1.8 | agentskills.io skills, self-written, curator, hubs, scanner | skills overwritten (#20273, #64338, #78004); overconfident self-evaluation | git-versioned skills with authorship; replay-tested drafts; `skill_write_safe` gate; embedding selection | Kev, embedders, generator | v1 |
| 1.9 | MEMORY.md and USER.md with character caps; FTS5 session search; external providers (Honcho and others) | caps error out; stale memories persist | core blocks with budgets and consolidation; bi-temporal graph from GLiNER2; Mem0 operations; hybrid retrieval; media memory | GLiNER2, Kev, embedders | v1 |
| 1.10 | cron with schedule grammar, pre-run scripts, delivery targets | — | durable jobs, same grammar, `wake` gate | Kev | v1 |
| 1.11 | about 45 providers, routing, fallbacks, credential pools, managed llama.cpp | Codex null-output bugs (#32892) | per-role slots; in-process Starling, OpenAI-compatible local servers, Anthropic, OpenAI, OpenRouter, Gemini in v1; fallbacks as switch events; managed llama.cpp later | all | v1 |
| 1.12 | two-layer compression, structured summary, tool-result placeholders, Anthropic caching | summarizer smaller than window drops context; lost goals (#33618); orphaned queue (#99882) | event-log projection; `clear_results`; map-reduce summaries; goals as events; never deletes | Kev, summarizer slot | v1 |
| 1.13 | shadow-git checkpoints, `/rollback` | — | same, unified with log positions | — | v1 |
| 1.14 | approval modes, regex dangerous patterns, hardline list, container boundary | bypasses (#84551, #74078); non-shell tools ungated (#35357); config unguarded (#59293); scanner misses (#27284); unrequested third-party egress (#45058) | sandbox policy separate from approval policy; parsed-structure risk; approvals for all side effects; taint tracking; PII egress gate | Kev, GLiNER2 | v1 |
| 1.14b | credential vault (1Password, Bitwarden, command), env scrubbing for subprocesses, pattern-based output redaction, writes to `~/.ssh` and `.env` blocked | the agent process holds the keys; redaction is by pattern | Corvid Vault: separate model-free process (separate uid in the strong tier); placeholders, edge substitution bound to hosts, ssh-agent, exact-value scrubbing, per-secret access policy, out-of-band confirm, provider keys through Vault | GLiNER2 (paste interception) | v1 |
| 1.15 | MCP client (stdio, HTTP, OAuth, sampling), ACP | — | MCP C# SDK client and server; sampling not relied on (deprecated in MCP 2026-07-28); tasks extension for long calls; ACP | — | v1 |
| 1.16 | profiles, SOUL.md, plugins with 27 hooks | — | profiles as directories with their own store; persona block; plugins in collectible load contexts with declared capabilities; hooks as typed events on the log | — | v1 / later |
| 1.17 | batch runner, trajectory compressor; Atropos removed | — | replay harness, golden sets, ShareGPT export, Kev decision export | — | later |
| — | Nous Portal tool gateway, Spotify, Google Meet, pets | — | none | — | never |

## 18. Phased plan

### Phase 0: prove the fabric (about 2 weeks)

| Spike | Question | Pass condition |
| --- | --- | --- |
| **S1** | Kev-0.8B and Kev-4B latency on the 3090 (through `tools/gpu-lease`) and on the workstation CPU, for turn-states of 300, 600 and 1,500 tokens with 4–8 questions and rows up to 1,000 tokens; plus the per-call cost of a one-question `pre-execute` checkpoint | an ingress checkpoint at p50 ≤ 300 ms on GPU with Kev-0.8B. If it only fits on CPU at ≥ 1 s, reduce checkpoints to ingress + egress and drop `step` questions to two. |
| **S2** | Does the fabric make Qwen3.5-0.8B a usable tool caller? Build a 200-case golden set of personal-assistant tasks (reminders, notes, files, search, messages) and compare (a) Qwen alone with all tools, (b) the embedding shortlist, (c) the shortlist plus Kev `tool` plus one schema plus repair | (c) beats (a) by ≥ 25 points on tool choice and argument exact match; escalation rate below 30% |
| **S3** | GLiNER2 on shell-argument targets and PII labels | recall ≥ 0.95 on paths, hosts and PIDs in a 300-command corpus that includes Hermes' published bypasses |
| **S4** | TurnState design: does Kev's answer quality hold at 600 tokens against the full transcript? | `tool` and `done` accuracy within 3 points |
| **S6, S7** | Vault: no secret value in any Corvid or sandbox core dump, log, span or cloud request; prompt-injection exfiltration suite (`vault.md` §13) | no match anywhere; every exfiltration attempt refused or scrubbed |
| **S5** | Starling's Qwen client with an llguidance grammar (prototype on a Starling branch) | 0 invalid tool names, with no added latency over 10% |

### Phase 1: kernel and local loop (v0.1)

Event log, session actor, slots, Starling adapters, the fabric with the catalogue from §5.3, tools (files, shell, web, memory, cron), sandbox on Linux, structural approvals, CLI, memory tiers 1–3, and OpenTelemetry.

### Phase 2: reach (v0.2)

ACP, OpenAI-compatible API, MCP client and server, gateway with Telegram, Discord and Slack, cloud slots with privacy egress, compaction with map-reduce, skills with the learning loop and replay tests, calibration tooling.

### Phase 3: depth

Subagents, code mode, media memory, remaining gateway adapters, voice, two-tier decider, architect/editor, desktop UI, decision-data export, and personal Kev adapters if U-5 lands.

### Backlog (first items)

| ID | Item | Depends on |
| --- | --- | --- |
| C-001 | **Built:** repository, initial core solution layout, resolved dependency boundary check (no provider SDK outside `Corvid.Models.*`); see [architecture](../architecture.md) | — |
| C-002 | Event model and SQLite log with projections | C-001 |
| C-003 | Spikes S1–S5 and their write-ups | Kev branch, GPU lease |
| C-004 | `IDecider` with Kev and degraded implementations; TurnState builder with budgets | C-003 |
| C-005 | Tool source generator and registry | C-001 |
| C-006 | Slot registry, profiles and switch semantics (§4.2) with tests that switch mid-turn | C-002 |
| C-007 | Sandbox (bubblewrap, Landlock, seccomp) and the structural approval pipeline. Must deny every tool access to Vault's data and config directories: until then, a shell tool can decrypt the default-tier store (`vault.md`, Implementation status). | S3 |
| C-008 | Memory store: FTS5, sqlite-vec, graph tables, write and read paths | C-004 |
| C-009 | Golden set and replay harness | C-002 |

## 19. Upstream asks for Starling

These belong in Starling's own plan, not here. They are the changes that make Corvid's local tier strong.

| ID | Ask | Why |
| --- | --- | --- |
| U-1 | Merge Kev (`kev-onnx`) and qualify a CUDA profile | Corvid's critical path (§1). |
| U-2 | Constrained decoding in `Starlings.Models.Qwen35`: `ResponseFormat` JSON Schema and grammar through ONNX Runtime GenAI's llguidance | Removes most tool-argument repair (§7). |
| U-3 | Conversation-state reuse for Qwen3.5 (KV and DeltaNet state carried between turns when the rendering is a prefix, e.g. with thinking disabled) | Every local turn currently pays a full prefill (§2). |
| U-4 | Kev state-prefix reuse to Supported, and a long-state chunked prefill | Cheaper step checkpoints; states above 8k. |
| U-5 | Adapter (LoRA) swapping for Kev profiles | Personal decider adapters (§16). |
| U-6 | A larger Qwen3.5 generator profile (4B or 9B), CUDA | A stronger in-process generator below the cloud. |
| U-7 | A local speech-to-text model | Voice without the cloud (§12). |

## 20. Open decisions for Mark

| ID | Decision | Default proposal |
| --- | --- | --- |
| D-1 | Product name and repository home | **Decided by Mark, 2026-10-16:** the name is Corvid; the repo is `starlinghq/corvid`, private. |
| D-2 | Is Corvid Starling's named .NET consumer for Kev (P-028 in the Starling plan)? | yes. It is the demand evidence Kev's admission lacks. |
| D-3 | Depend on Starling packages from nuget.org only, or on project references during phase 0 | project references until Kev ships |
| D-4 | Licence | MIT OR Apache-2.0, like Starling |
| D-5 | Shell parser: tree-sitter-bash through a .NET binding, or a hand-written parser for the subset Corvid permits | tree-sitter |
| D-6 | GPU placement defaults and whether Corvid uses Roost when it exists | §4.4 defaults; Roost as a local-server slot later |
| D-7 | Voice in v1 or later | later |
| D-8 | Which cloud providers ship in v1 | Anthropic, OpenAI-compatible (covers OpenRouter and local servers), Gemini |
| D-9 | Hermes compatibility: import `~/.hermes` skills, memories and sessions (Hermes has `hermes claw migrate` for OpenClaw) | import skills and memories in v1; sessions later |
| D-10 – D-12 | Vault: strong tier required or optional; Windows and macOS isolation; own store or front a password manager | see `vault.md` §14 |

## 21. Sources

Hermes issue numbers, version facts and the MCP 2026-07-28 details are as reported by research passes on 2026-10-16 (H1, R12). They were not each verified against the primary page.

| ID | Source | Used for |
| --- | --- | --- |
| H1 | https://github.com/NousResearch/hermes-agent (v0.22.2, 2026-10-15) and https://hermes-agent.nousresearch.com/docs/ | feature catalogue, architecture, issues cited by number |
| H2 | Hermes commit 5af672c (PR #26106, 2026-05-15) | removal of Atropos and the client-side tool-call parsers |
| S1 | `starling/model-pipeline/admissions/kev.md` | Kev contract, limits, latency, accuracy |
| S2 | `starling` branch `kev-onnx` at `19af3e6`, `ai-dotnet/src/Starlings.Models.Kev` | the Kev C# API |
| S3 | `starling/ai-dotnet/src/Starlings.Models.Qwen35*` and `ai-dotnet/README.md` | context limit, prefill and decode numbers, tool calling, no structured output, no state reuse |
| S4 | https://github.com/jaredpalmer/kev | System One request shape |
| R1 | https://www.anthropic.com/engineering/effective-context-engineering-for-ai-agents | context levers |
| R2 | https://www.anthropic.com/engineering/advanced-tool-use | tool search, programmatic tool calling |
| R3 | https://arxiv.org/abs/2505.03275 (RAG-MCP) | retrieval-based tool selection |
| R4 | https://arxiv.org/abs/2506.02153 | small models as the default in agent systems |
| R5 | https://arxiv.org/abs/2501.13956 and https://github.com/getzep/graphiti | bi-temporal memory graph |
| R6 | https://arxiv.org/abs/2504.19413 (Mem0) | memory update operations |
| R7 | https://www.letta.com/blog/sleep-time-compute | background consolidation |
| R8 | https://github.com/guidance-ai/llguidance and https://github.com/microsoft/onnxruntime-genai | constrained decoding route |
| R9 | https://github.com/openai/codex | sandbox policy separate from approval policy |
| R10 | https://github.com/All-Hands-AI/OpenHands | event-stream architecture |
| R11 | https://agentclientprotocol.com | ACP |
| R12 | https://blog.modelcontextprotocol.io/posts/2026-07-28-release-candidate/ | MCP 2026-07-28 (sampling deprecated, tasks extension) |
| R13 | https://github.com/modelcontextprotocol/csharp-sdk | MCP C# SDK |
| R14 | https://learn.microsoft.com/dotnet/ai/microsoft-extensions-ai | `IChatClient` middleware |
| R15 | https://opentelemetry.io/docs/specs/semconv/gen-ai/ | tracing conventions |
| R16 | https://aider.chat/docs/repomap.html | architect/editor split, repo map |
| V1 | `vault.md` §15 | prior art for credential-injecting proxies (Fly.io Tokenizer, Infisical Agent Vault, varlock, Gondolin, Novita, Cloak) |
| R17 | https://agentskills.io | skill format |

## Changelog

| Version | Date | Change |
| --- | --- | --- |
| 0.1 | 2026-10-16 | First draft. |
| 0.2 | 2026-10-16 | Corvid Vault (§10.5, `vault.md`): secrets in a separate model-free process; provider keys through Vault; parity row 1.14b; spikes S6–S7; decisions D-10 to D-12. |
| 0.3 | 2026-10-16 | D-1 decided (name Corvid, `starlinghq/corvid` private); status line notes Vault M1 is built. |
