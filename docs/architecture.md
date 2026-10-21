# Agent foundation (C-001, C-002, C-004a, C-004b, C-005, C-006, C-007a, C-007b, C-008, C-009, C-010, C-011, C-012, C-013, C-014, C-015, C-016, C-017, C-018, C-019, C-020, C-021)

The solution contains `Corvid.Abstractions`, `Corvid.Kernel`, and
`Corvid.Models`, alongside the working Vault projects. Kernel and Models each
reference Abstractions; Models also references
`Microsoft.Extensions.AI.Abstractions`. Models must not reference Kernel: the
kernel's turn loop will depend on the slots. C-005 added `Corvid.Tools` and its
build-time generator, `Corvid.Tools.Generators` (see [Tools](#tools-c-005)). C-010 added the first adapter,
`Corvid.Models.Starling` (see [Starling adapters](#starling-adapters-c-010)).
C-004a added `Corvid.Fabric`, which references Abstractions and
`Microsoft.Extensions.AI.Abstractions` (see [Decision fabric](#decision-fabric-c-004a)).
C-007a added `Corvid.Sandbox` (core, no dependencies) and the `corvid-sandbox` launcher
executable in `sandbox/Corvid.Sandbox.Launcher` (see [Sandbox](#sandbox-c-007a)).
C-008 added `Corvid.Memory`, which references Abstractions, Fabric, `Microsoft.Data.Sqlite` and
`Microsoft.Extensions.AI.Abstractions` (see [Memory store](#memory-store-c-008)). C-021 added calibration and
decision export to `evals/Corvid.Evals`, and the `corvid-evals` console in `evals/Corvid.Evals.Calibrate` (see
[Calibration and decision export](#calibration-and-decision-export-c-021)).
C-007b added `Corvid.Approvals`, which references Abstractions, Fabric, Tools and Sandbox and no
package of its own (see [Approvals](#approvals-c-007b)). C-011 made `Corvid.Kernel` the integration point: it now references
Approvals, Fabric, Memory, Models and Tools as well, for the session actor, the turn loop and the device scheduler (see
[Session actor and turn loop](#session-actor-and-turn-loop-c-011)). C-012 added `Corvid.Tools.Builtin`, the built-in
tools, which references Approvals, Kernel, Memory, Sandbox and Tools and no package, and the scheduler in Kernel (see
[Built-in tools and scheduler](#built-in-tools-and-scheduler-c-012)). C-013 added the composition root, `Corvid.Host`,
and the `corvid` executable, `Corvid.Cli` (see [Host and CLI](#host-and-cli-c-013)). C-004b added the Kev decider to
`Corvid.Models.Starling` and the two-tier escalation to `Corvid.Fabric` (see [Kev decider](#kev-decider-c-004b)). C-016
added compaction to the kernel (see [Compaction](#compaction-c-016)). C-014 added checkpoints to
`Corvid.Kernel` (`Checkpoints`), with no new project or package (see [Checkpoints](#checkpoints-c-014)). C-017 added `Corvid.Skills`, which
references Abstractions, Approvals, Fabric, Kernel, Models, Tools and Tools.Builtin, `Microsoft.Data.Sqlite` and
`Microsoft.Extensions.AI.Abstractions` (see [Skills](#skills-c-017)). C-015 added the privacy egress gate
and the Vault-only HTTP pipeline to `Corvid.Models` (which now references `Corvid.Vault.Client`), and two adapters,
`Corvid.Models.Anthropic` and `Corvid.Models.OpenAICompat` (see [Cloud slots and privacy egress](#cloud-slots-and-privacy-egress-c-015)). C-018
added `Corvid.Mcp`, the first surface project, which references Sandbox, Tools and Vault.Client and the ModelContextProtocol
SDK, and nothing else references it but the host (see [MCP client and server](#mcp-client-and-server-c-018)). C-020
added `Corvid.Gateway`, which references Abstractions, Approvals, `Corvid.Vault.Client` and `Microsoft.Data.Sqlite`, and the
platform adapters `Corvid.Gateway.Telegram`, `.Discord` and `.Slack`, which reference only the gateway (see
[Gateway](#gateway-c-020)). Add
the other projects from the agent plan as their implementations start, rather
than creating empty projects for every future surface.

## Session event log (C-002)

- `Corvid.Abstractions/Events` holds the event contracts: `SessionEvent` and its
  types, and `IEventLog`. The JSON discriminators declared on `SessionEvent`
  (`user_message`, `decision`, `compacted`, ...) are persisted wire names; renaming
  one requires a payload version bump and a migration.
- `Corvid.Kernel/Events` holds `SqliteEventLog`, the source-generated JSON
  context (snake_case, payload version 1) and `SessionProjection`. Abstractions and
  Kernel set `IsAotCompatible`, so the trim and AOT analyzers run on them.
- The log is one SQLite file (WAL, `synchronous=FULL`) with one row per event,
  keyed by session and a one-based sequence. Connections are pooled (C-011): with one connection per operation and no
  pool, each append closed the file's last connection and SQLite checkpointed the WAL on that close, so an append cost
  6.8 ms against 2.3 ms pooled on the development machine (a head read 0.35 ms against 0.03 ms). Commits still fsync. Triggers reject `UPDATE` and `DELETE`.
  `Append` takes the expected head, writes a whole batch or nothing, and throws
  `EventLogConflictException` when another writer got there first. A session
  actor is expected to be the single writer; the check keeps a second writer
  (for example a recovering process) from silently interleaving.
- `SessionProjection` replays a complete prefix into a snapshot: transcript, goal,
  todos, pending approvals, model profiles per slot, decisions with outcomes, and
  checkpoints. `Compacted` replaces its range in the transcript only; the raw
  events stay in the log. `CompactionExpanded` (C-016) shows a compacted range again, one layer at a time. Goals, todos
  and approvals are folded from their own
  events, so compaction cannot remove them. `Load(log, session, throughSequence)`
  projects any earlier position, the basis for resume, branch and undo.
- Persisted events the projection cannot apply (a compaction that splits a tool
  call from its result, an outcome for an unknown decision, duplicate IDs) are
  reported in `SessionSnapshot.Issues` rather than thrown, because an append-only
  log cannot repair them and the session must stay readable.

C-011 added the session actor and prompt rendering (see [Session actor and turn loop](#session-actor-and-turn-loop-c-011)).
C-014 added `SessionRewound`, which shows the transcript as it was at an earlier position, and `CheckpointTaken`'s ledger
fields (see [Checkpoints](#checkpoints-c-014)).
Not built yet: branch and fork sessions that copy a prefix, and schema migrations beyond version 1. SQLite calls
are synchronous; callers that must not block should use a storage worker.

## Golden trajectories and replay (C-009)

- `evals/golden/*.json` are golden trajectories: format `corvid-golden-trajectory`,
  `format_version` 1. Each file holds an `id` (the file name), a description, the
  session's `events` in the event log's persisted form (`payload_version` 1, the same
  snake_case JSON and discriminators the log stores), and `checks`: a log position
  (`through`), an optional `expand_compactions`, and the `expected` projection.
  Unknown fields, versions and event types are rejected rather than ignored.
- `evals/Corvid.Evals` is the harness. `ReplaySession` appends a trajectory to a fresh
  `SqliteEventLog` (under the temporary directory, removed on dispose) or to an empty
  session in a caller's log, and projects any position with `SessionProjection.Load`.
  `ReplayRunner.Verify` compares each check with `JsonDiff` and reports differences by
  JSON path. It also projects every position from 0 to the head, compacted and
  expanded, and requires the log round trip to match `SessionProjection.Build` over
  the in-memory events.
- `SnapshotJson` is the comparable form of a snapshot: head, goal, todos, transcript
  entries with their event, pending approvals, model profiles per slot, decisions with
  outcomes, checkpoints and issues. Dictionaries are written as arrays sorted by key;
  the session ID, event IDs and timestamps are left out. Issue reasons are compared,
  so rewording one means re-recording.
- `ReplayRunner.Record` fills `expected` from the current projection, for authoring.
  `CORVID_RECORD_GOLDENS=1` makes the golden-set test rewrite the committed fixtures
  instead of asserting; review that diff, since recording accepts whatever the
  projection does today. `tests/Corvid.Evals.Tests` also states the seed set's key
  behaviours directly, so a recorded regression does not pass silently.
- The seed set covers a tool call round trip with a decision outcome, nested
  compaction and its expanded view, goal, todos and an approval across compaction,
  model switches per slot with a checkpoint, and inconsistent history reported as
  issues.
- Corvid.Kernel grants `InternalsVisibleTo` to Corvid.Evals so fixtures use the
  persisted serializer itself rather than a copy of the format.

Replaying decisions against new floors is C-021 (see [Calibration and decision
export](#calibration-and-decision-export-c-021)). Not built yet: replaying decisions against new models (re-asking a
decider over recorded states) and scoring golden sets (tool choice, argument match, escalation rate), golden sets per
generator profile and fabric version, a command-line runner for the golden set, and prompt-rendering checks (C-006).

## Model slots and switching (C-006)

`Corvid.Models` (core, `IsAotCompatible`) implements plan §4.1, §4.2 and the
de-escalation rule of §6. It depends only on Abstractions and
`Microsoft.Extensions.AI.Abstractions`; provider adapters come later in
`Corvid.Models.*`.

- `ModelProfile`: context window, accepted context, tool-call support (none,
  template-rendered, native), structured output, prompt-caching style, cost per
  million tokens, privacy class (`local`, `local-server`, `cloud`) and placement.
  `ToSnapshot()` serializes it through a source-generated context (snake_case,
  string enums); that JSON is persisted in `ModelSwitched` events, so renaming a
  property or enum value needs a migration.
- `ModelSlots` names the seven slots (`generator`, `summarizer`, `decider`,
  `extractor`, `embedder.text`, `embedder.media`, `vision`) and their contracts:
  `IChatClient` for generator, summarizer and vision,
  `IEmbeddingGenerator<string, Embedding<float>>` for `embedder.text`,
  `IExtractor` (`Corvid.Abstractions.Extraction`, added by C-010) for `extractor`, and
  `IDecider` (`Corvid.Abstractions.Fabric`, added by C-004a) for `decider`. The
  media-embedder contract is not defined yet (a Starling-typed API core cannot
  reference), so that slot accepts any service.
- `ModelSlotRegistry` is the process-wide catalogue: per slot a default binding,
  an escalation ladder and a fallback chain. `SessionModelSlots` holds one
  session's current bindings, captured from the registry at creation, so
  rebinding the registry never silently changes a running session. Starting
  bindings are configuration, not events, so a projection's `ModelProfiles`
  lists only slots that have switched.
- `SessionModelSlots.SwitchAsync` runs the switch procedure, serialized per
  session: wait for tracked tool calls (`TrackToolCall`), outside the lock, and
  verify from the log that every issued call has a result or is parked at an
  approval prompt (`ParkToolCall`; parked calls are reported, not awaited, so
  `/model` works while an approval is pending); ask the privacy gate when the target is
  `cloud`; append `ModelSwitched` with old and new profile snapshots and the
  reason (`user`, `escalation`, `fallback`, `de_escalation`) at the expected
  head, re-checking on a concurrent append; then swap the binding and run the
  budget hook. Pending approvals are left untouched and reported in the result.
  The privacy gate runs before the event, not after as listed in §4.2, so a
  refused move is never recorded as having happened.
- `GetChatClient(slot)` returns an `IChatClient` that resolves the current
  binding on every call, so a switch takes effect at the next call while calls
  already in flight finish on the old model. A 429, 5xx or transport failure
  moves along the fallback chain (forward only from a listed hop, from the top
  when the default fails, skipping targets the privacy gate refuses and bindings
  that already failed this request), one `fallback` switch event per hop. A
  fallback does not wait for tracked tool calls, so a tool whose own model call
  fails cannot deadlock on itself; streaming falls back only
  before the first update. `EscalateAsync` climbs the ladder;
  `CompleteTurnAsync` returns every unpinned slot to its default. Only a user
  switch pins or unpins.
- Hooks: `IBudgetPolicy` (default `AcceptedContextBudgetPolicy`, which only
  reports whether to compact or expand) and `IPrivacyGate` (default
  `PrivacyGates.DenyCloud`, so cloud slots are refused until a gate is
  configured).

The turn loop acts on the hook's direction (C-016): compaction before a call that does not fit, and expansion after a move
to a larger window (see [Compaction](#compaction-c-016)); the hook itself stays a reporter. C-015 implemented the privacy
hook (`ProjectionPrivacyGate`), per-request redaction on each cloud binding, the `privacy` switch reason and `RetreatAsync`
(see [Cloud slots and privacy egress](#cloud-slots-and-privacy-egress-c-015)). Not built yet: re-rendering the projection
for the new model's format (the turn loop renders `IChatClient` messages for every model; the Anthropic adapter adds cache
breakpoints itself, C-015), restoring current bindings and pins from the log on resume (pins
are not persisted), recording switch costs for `/insights`, and the
media-embedder contract.

## Starling adapters (C-010)

`Corvid.Models.Starling` is the in-process adapter for Starling's models (plan §1,
§4.1). It references `Starlings.Models.Qwen35.Extensions.AI`, `Starlings.Models.Gliner2`,
`Starlings.Models.EmbeddingGemma2.Extensions.AI`, `Starlings.Models.E5.Extensions.AI` and
`Starlings.Acquisition` and, from C-004b, `Starlings.Models.Kev`, all 0.3.0 from nuget.org (D-3; 0.3.0 changed nothing
these adapters use: batching options are opt-in and off), and `Corvid.Models`. It is not
`IsAotCompatible`: Starling is not AOT-qualified as a set, and warnings are errors.

- Natives: the project references exactly one native package, `Microsoft.ML.OnnxRuntimeGenAI`
  0.17.1 (CPU). It brings `Microsoft.ML.OnnxRuntime` 1.30.0, the CPU ONNX Runtime that
  GLiNER2, e5 and EmbeddingGemma 2 load through `Starlings.Runtime.Onnx`. Every service
  runs on the CPU, and the factories refuse CUDA session options with
  `NotSupportedException` instead of failing later at load.
- `StarlingProfiles`: `qwen3.5-0.8b@cpu` has context window and accepted context 8,192
  (`Qwen35ChatModel.MaxContextTokens`; Starling rejects prompt plus completion above it and
  never truncates, and the model's own 262,144 is not reachable), template-rendered tool
  calls, no structured output, no prompt caching (each request prefills the whole
  conversation). Prompts above 1,666 tokens are outside Starling's Stable contract. The
  encoders have no tool calls or structured output: `gliner2-base-v1@cpu` 512 tokens
  including the label schema, `embeddinggemma-2@cpu` 8,192, `multilingual-e5-small@cpu`
  512 (its tokenizer truncates on the right, as the reference does; the others throw).
  All are `local`, free, placement `cpu`.
- `StarlingBindings` loads a model from resolved files and returns a `ModelBinding`:
  Qwen as an `IChatClient` for `generator` or `summarizer` (it also answers
  `GetService(typeof(ITokenCounter))`), GLiNER2 as `IExtractor`, EmbeddingGemma 2 (the
  default `embedder.text`) and e5 (the alternative) as `IEmbeddingGenerator`. The Qwen
  client rejects `RequireAny`/`RequireSpecific` tool modes, JSON response formats and
  unseeded sampling; tool calling is experimental in Starling 0.3.0. The caller owns and
  disposes the services.
- `Gliner2Extractor` implements `IExtractor` (Corvid.Abstractions/Extraction: text and
  labels in, labelled spans with UTF-16 offsets and confidences out; over-long input
  throws `ArgumentException`, nothing is truncated). Extraction is blocking native work,
  so it runs on a `TaskScheduler` given at creation, the thread pool by default; the
  device scheduler (C-011) will supply its own. Starling's embedders run on the calling
  thread and return completed tasks. C-011's `DeviceScheduler.GetTaskScheduler(priority)` is the scheduler to give the
  extractor; the embedders and the Qwen client are wrapped with `DeviceScheduling.Schedule` instead.
- `Qwen35TokenCounter` implements `ITokenCounter` (Corvid.Abstractions/Tokens) with
  Qwen's own tokenizer, loaded from the model's `tokenizer.json` without the ONNX graph.
  It counts text; counting a rendered conversation before a call is not built, and the
  exact prompt size is the response's `Usage.InputTokenCount`.
- `StarlingModelStore` is acquisition for a future `corvid doctor`: `StarlingModels`
  lists the four models with their asset locks (repository, commit, file sizes and
  SHA-256). `Check`/`CheckAll` resolve offline and report `Ready`, `Missing` (with the
  missing paths), `Corrupt` (file, sizes, digests) or `Unreadable`, never throwing for
  those. `OpenQwen35()` and the other `Open*` methods are for startup: offline, size-only
  verification by default (hashing about 5 GB every start takes seconds; downloads are
  always hashed and `Check` hashes by default), and a `StarlingModelUnavailableException`
  that names the model, slot, missing files, download size and the `STARLING_MODELS`
  variable. `DownloadAsync` is the only call that uses the network. The cache root is
  the constructor's directory, else `$STARLING_MODELS`, else `~/.cache/starlings/models`.
- Deployment: Starling requires `MALLOC_MMAP_THRESHOLD_=65536` in the environment of a
  Linux process that runs GLiNER2. glibc reads it before `Main`, so `corvid chat` re-executes itself with it set (C-013,
  `GlibcMalloc`); any other process that hosts the extractor must start with it.
- Tests: `tests/Corvid.Models.Starling.Tests` checks profiles, the catalogue, offline
  resolution against a temporary cache (missing, wrong size, same-size corruption under
  SHA-256 using sparse files), extractor mapping, scheduling and validation, slot
  binding and CUDA refusal without loading a model. Model-backed tests run only with
  `CORVID_STARLING_MODELS` set to a model cache, skip any model that cache lacks, and
  never download; CI does not set it.

Not built yet: CUDA placement (switch the native package to
`Microsoft.ML.OnnxRuntimeGenAI.Cuda`, which brings `Microsoft.ML.OnnxRuntime.Gpu`, and
make placement per binding; plan §4.4 puts the generator on the 3090), media embeddings
(`embedder.media`), the fp16x Qwen variant, batch extraction, label descriptions,
and conversation-level token counting before a call. C-013 built `corvid doctor` on `StarlingModelStore` and wires
C-011's device scheduler to these bindings.

## Dependency rules

- `Corvid.Abstractions` and `Corvid.Vault.Contracts` have no application package or
  project dependencies.
- Production projects under `src/` are core libraries unless named
  `Corvid.Models.*` (for example, `Corvid.Models.Starling`). The base
  `Corvid.Models` project is core, not an adapter.
- Core libraries may reference other core libraries and explicitly reviewed
  infrastructure packages. They may not reference adapters or provider SDKs,
  even indirectly. The package allowlist contains
  `Microsoft.Extensions.AI.Abstractions` (used by Models, Tools, Fabric and Memory) and, for the event log
  and the memory store, `Microsoft.Data.Sqlite` with its resolved dependencies
  (`Microsoft.Data.Sqlite.Core` and the `SQLitePCLRaw.*` e_sqlite3 bundle). Add infrastructure packages and their
  transitive dependencies to `CorePackages` in the checker when needed; never
  add a provider SDK there.
- `Corvid.Memory` references `Corvid.Fabric`, because `memory_op` is a fabric gate. Fabric must not
  reference Memory (the compiler rejects the cycle). Memory reads the event log through `IEventLog`
  and does not reference Kernel.
- `Corvid.Approvals` references `Corvid.Fabric` (`risk` is a fabric gate), `Corvid.Tools` (`ToolRisk`) and
  `Corvid.Sandbox` (`BuiltInDenials`, so the hardline list and the sandbox protect the same paths). It parses
  bash itself (decision D-5); the boundary tests keep tree-sitter bindings out of it.
- `Corvid.Kernel` composes the other core libraries for the turn loop (C-011): Approvals, Fabric, Memory, Models and
  Tools. None of them may reference Kernel (the compiler rejects the cycle); Models and Memory read the log through
  `IEventLog`. Kernel is still a core library: adapters reach it only through the contracts the host binds to slots.
- `Corvid.Tools.Builtin` (C-012) references Approvals, Kernel (the job store and the scheduler's script-runner contract),
  Memory, Sandbox and Tools. Nothing references it but the host and tests, so Kernel never sees the tools it runs.
- `Corvid.Skills` (C-017) references Kernel (the turn-loop contract `ISkillSource`/`ISkillLearning` and the turn runner its
  replay test drives), Fabric (`skill_write_safe`), Tools.Builtin (the sandboxed shell for skill scripts) and their
  dependencies, and no package beyond the allowlist. Nothing references it but the host and tests, so the kernel never sees
  the skill store.
- Provider SDKs and Starling model packages belong in `Corvid.Models.*` adapters.
  Adapters may reference core projects. ONNX Runtime and ONNX Runtime GenAI natives
  (and their managed packages) come only through adapters; a core project that
  resolved one would fail the allowlist. In any project, the checker fails a target
  that resolves two flavours of one native runtime (for example
  `Microsoft.ML.OnnxRuntime` with `Microsoft.ML.OnnxRuntime.Gpu.Linux`, or GenAI CPU
  with GenAI CUDA): both ship the native library under the same file name.
- The Vault executable may reference only `Corvid.Vault.Contracts`. Agent
  projects, including adapters, may not reference the Vault executable.
- The sandbox launcher (`sandbox/Corvid.Sandbox.Launcher`, the `corvid-sandbox` executable;
  `LauncherProjects` in the checker) may have no package or project dependencies at all, so
  Vault can start it without taking on any of Corvid's code. No project may reference it,
  including Vault and adapters: Corvid.Sandbox and Vault start it by path. The test project
  references it (tests are not scanned) to reach its internals and to build it next to the tests.
- `Microsoft.NET.ILLink.Tasks` is allowed everywhere as SDK trim/AOT build
  tooling, not an application dependency.
- Build-tool projects run inside the compiler, not in Corvid. The only one is
  `Corvid.Tools.Generators` (listed in `BuildToolProjects` in the checker). It
  may use only Roslyn and the netstandard2.0 packages Roslyn brings
  (`BuildToolPackages`), and no projects. Other projects consume it as an
  analyzer: `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`. Such a
  reference is not in NuGet's restored graph, so the checker reads analyzer
  project references from each production `.csproj` and fails any that names a
  project outside `BuildToolProjects`. A build-tool project in another
  project's restored graph (an ordinary reference) fails the check.
- Test, eval and development tool projects (`tests/`, `evals/`, `tools/`) are outside
  these production rules, and the checker does not scan them. They can reference
  implementations to exercise them, including Vault. Production projects should not
  reference them; the checker does not yet enforce that.

- The composition root and the CLI (C-013; `CompositionProjects` in the checker) are not core. `Corvid.Host` may
  reference core projects and adapters, and resolve the packages it reviews (`CompositionPackages`: the generic host's
  `Microsoft.Extensions.*` configuration, DI, logging and options packages, and OpenTelemetry with its OTLP exporter) plus
  any package reachable from an adapter project in the restored graph (Starling, ONNX Runtime), followed through each
  library's `dependencies`. `Corvid.Cli` may also resolve `Spectre.Console` and `Spectre.Console.Ansi` (`CliPackages`), so
  the host stays free of the terminal UI and the gateway (C-020) can share its command registry. A package neither listed
  nor brought by an adapter fails, as in core: a provider SDK the host took on directly is refused. Only the CLI may
  reference the host, and nothing may reference the CLI. The core rule is unchanged: core projects still fail on adapters,
  hosting, OpenTelemetry and Spectre packages. The kernel and fabric emit spans through `System.Diagnostics.ActivitySource`,
  which is in the shared framework, so they take no package for telemetry.
- Provider SDKs (C-015): `Anthropic` only in `Corvid.Models.Anthropic`; `OpenAI` and `Microsoft.Extensions.AI.OpenAI` (with
  `System.ClientModel`) only in `Corvid.Models.OpenAICompat`. The host resolves them through those adapters. `Corvid.Models`
  references `Corvid.Vault.Client` (core, no package) for the placeholder and Vault handlers; no core allowlist entry was added.
- Surface projects (C-018; `SurfacePackages` in the checker) speak a protocol through its SDK. Each may reference core
  projects and resolve the core allowlist plus its own reviewed packages (for `Corvid.Mcp`: `ModelContextProtocol`,
  `ModelContextProtocol.Core`, `ModelContextProtocol.Extensions.Tasks` and the `Microsoft.Extensions.*` abstractions the tasks
  extension's hosting dependency brings: caching, configuration, DI, diagnostics, file providers, hosting, logging, options and
  primitives). It may not reference an adapter, the host, Vault, the launcher, a build tool or another surface. Only the
  composition root may reference a surface, and it then resolves what the surface brings, as it does for adapters; a core
  project that reached a surface or its SDK fails. Nothing was added to `CorePackages`.
- Front ends (C-019; `FrontEndProjects` in the checker): `Corvid.Acp` and `Corvid.Http` sit on the composition root like the
  CLI. Each may reference `Corvid.Host` (and resolve what it resolves), only the CLI may reference a front end, and neither takes
  a package. Shared frameworks are checked too (`FrameworkReferences`): `Microsoft.AspNetCore.App` is allowed only in
  `Corvid.Http` and in the CLI, which inherits it; any other project, and any shared framework other than
  `Microsoft.NETCore.App`, fails. See [ACP and OpenAI-compatible HTTP](#acp-and-openai-compatible-http-c-019).
- The gateway (C-020): `Corvid.Gateway` and its platform adapters are core libraries under the checker. They use no
  messaging SDK, only HTTP and WebSockets from the shared framework, so bot tokens stay in Vault, and they need no rule of
  their own. A future adapter that needs an SDK would take an explicit, documented rule like the provider SDKs above.
  Only the host references the adapters; the gateway reaches the runtime through `IGatewayAgent` and never references
  the host.
- `corvid evals` starts C-021's `corvid-evals` by path (found through `$CORVID_EVALS`, next to `corvid`, or on `PATH`), the
  way Corvid starts `corvid-sandbox`, so no production project references eval code.

## Tools (C-005)

`Corvid.Tools` is a core library (Microsoft.Extensions.AI.Abstractions and
`Corvid.Vault.Client`, for `SecretRef`). It holds the `[Tool]`, `[SideEffect]`,
`[Network]`, `[Destructive]` and `[Probe]` attributes, `ToolDescriptor` and `ToolRisk`,
the `ToolFunction` base class (an `AIFunction`), argument binding
(`ToolArguments`) and `ToolRegistry`.

`Corvid.Tools.Generators` is an incremental source generator. For each `[Tool]`
method in a top-level partial class it emits a nested `ToolFunction` that binds
arguments with explicit per-type conversions and calls the method directly, the
JSON Schema as a string written at compile time, and the risk metadata. The
class gains `CreateTools()` (and implements `IToolProvider` unless static).
Generated code uses no reflection; the fixture project builds it with the trim
and AOT analyzers on. The schema is computed by the generator, because the
compiler cannot run `AIJsonUtilities`; a test compares it with
`AIJsonUtilities` for every fixture tool. Two differences are deliberate:
`SecretRef` is a string, and array elements are non-null. Unsupported
parameter or return types, invalid or duplicate names, a missing description,
`[Destructive]` without `[SideEffect]`, and `[Probe]` (a tool slot pre-fill may
try with a substitute argument, its result discarded) on a tool with a
`[SideEffect]` or `[Network]` are compile errors (CVTOOL001 to CVTOOL009).

`ToolRegistry.List()` returns descriptors; `GetFunctions(secretNames)` returns
the tools to offer a model. A `SecretRef` parameter is offered as an enum of
the given Vault catalogue names, and a call naming another secret is refused
before the method runs. With no catalogue, secret-bearing tools are left out
(plan §10.5).

Supported parameter types: `string`, `bool`, `int`, `long`, `float`, `double`,
`decimal`, `Guid`, `DateTime`, `DateTimeOffset`, `Uri`, non-flags enums (by
name), `SecretRef`, nullable forms of these, arrays and lists of them (not of
`SecretRef`), and `CancellationToken`. Return values are limited to types the
chat client can serialize without caller-supplied type information.

Deferred: argument validators beyond type binding (paths inside the
workspace, date ranges), the embedding-examples file, object-typed parameters
and return values (which need a `JsonSerializerContext`), toolsets, platform
presets and `check_fn` availability, and restricting secrets by the catalogue's
toolset bindings. The built-in tools are in `Corvid.Tools.Builtin` (C-012).

C-012 added `ToolCallContext`: a parameter of that type is bound from `AIFunctionArguments.Context` (the turn's session,
call id, taint and approval), never from the model, and is left out of the schema and the descriptor's parameters; a
non-nullable one refuses a call made outside a turn. The schema parity test leaves it out on the `AIJsonUtilities` side
too.

## Decision fabric (C-004a)

`Corvid.Fabric` (core, `IsAotCompatible`) implements plan §5.1, §5.3, the policy
side of §5.2 and §5.5, and the degraded tier of §5.6. The degraded decider is its own `IDecider`; Kev's lives in
`Corvid.Models.Starling` (C-004b), and nothing in Fabric references a Starling or provider
package. It needs no new infrastructure package, so the checker's allowlist is
unchanged.

- Contracts (`Corvid.Abstractions/Fabric`): `IDecider` takes a `DecisionRequest`,
  one shared state (`JsonElement`) plus named `DecisionQuestion`s, each Noul
  (options `yes`, `no`), Choice (options with optional descriptions, rendered
  `name: description`) or Score (ordered levels), with per-question instructions
  as text or JSON (`DecisionContent`, after `KevContent`). It returns
  `DeciderAnswers` or `DeciderUnavailable` (`StateOverBudget`, `Refused`,
  `Failed`), which makes the caller fall back. A `DecisionAnswer` holds
  probabilities in option order, `Confidence` (Kev's `(p_max − 1/K)/(1 − 1/K)`),
  `ExpectedScore` (Σ i·pᵢ for scores), `Calibrated` and, for embedding answers,
  the top-1 `Margin`. The contract lives in Abstractions so `Corvid.Models` can
  type the slot without referencing Fabric. The `extractor` slot's `IExtractor`
  is C-010's (`Corvid.Abstractions.Extraction`).
- `QuestionCatalogue.Default` is the §5.3 table as data: id, checkpoints
  (`ingress`, `step`, `pre_execute`, `egress`, `tick`), kind and fixed options (or
  caller-supplied for `tool` and `skill`), role (gate or routing), what the floor
  measures (choice confidence, P(yes), expected score, or the `risk` level rule),
  the act and below-floor actions, the degraded rule, and named caller conditions
  (`FabricConditions`, for example `agent_authored` for `skill_write_safe`). Gates
  are `risk`, `skill_write_safe` and `memory_op`. A request key may carry an
  instance suffix (`clear_results/call-7`).
- Floors are versioned configuration: `floors.json`, embedded in the assembly and
  read through a source-generated context that rejects unknown properties.
  `FloorConfiguration` holds the version, the degraded choice margin (0.05) and one
  floor per catalogue question, validated on load. Starting values are the §5.3
  ones; `risk`, which the table gives no number, starts at confidence 0.9 (the
  §5.5 gating precision target).
- `TurnStateBuilder` renders the §5.1 fields in a fixed order with per-field
  token budgets (total 600, hard stop 1,500). Strings keep their longest prefix
  that fits, ending in `…`, without splitting surrogate pairs; `recent` keeps the
  newest items; entities keep ordinal label order. If the state is still over
  the total, `recent`, `last_tool_result`, `entities`, `goal` and `user_message`
  are cut further in that order. Fixed fields still over the hard stop set
  `ExceedsHardStop`. Counting goes through `ITokenCounter`
  (`Corvid.Abstractions.Tokens`, shared with C-010); the default is characters /
  4, rounded up. A model's own counter, such as the Qwen generator binding's, can
  be passed in; the turn loop (C-011) passes the generator's (`GetService(typeof(ITokenCounter))`).
- `DegradedDecider` (§5.6): choices by cosine similarity between the rendered state
  (`key: value` lines, as Kev reads JSON) and each option, in one embedding batch
  per request; Noul questions by asking the generator for `yes` or `no` (leading
  word, any case; anything else is no answer); `difficulty` by the user message's
  token length and the tool steps taken. Gates, `injection` and `clear_results`
  get no answer, and no model is called for them. Every answer is uncalibrated.
- `DecisionPolicy` applies floors. An uncalibrated answer never passes a gate. A
  degraded choice acts only when its margin is above 0.05. A heuristic
  `difficulty` is logged but never escalates, so escalation is manual (`/model`).
  A missing or malformed answer, or a missing required condition, takes the
  below-floor action. For `risk` above its floor, levels 3–4 return `ask`, 0–1
  return `auto_run` in permissive mode, and other levels return `approval_mode`,
  left to the approval pipeline (C-007b).
- `DecisionFabric.DecideAsync` runs one checkpoint. Questions must belong to the
  catalogue and the checkpoint, have the catalogue's kind and, for rows with fixed
  options (`risk`, `difficulty`, `memory_op` and others), exactly its options in
  order, because the policy reads score levels by index. It sends one request to the calibrated decider
  when one is configured and the state is within the hard stop, and falls back
  to the degraded tier when there is none, the state is over the hard stop, or
  the decider declines or throws. It then appends one `Decision` per question in
  a single batch, retrying at the new head after a concurrent append. Each event
  holds the probabilities, confidence, the threshold actually compared (floor or
  margin), the action, `catalogue_version` (the floor version) and `decider`, for
  example `degraded after kev-0.8b: state_over_budget`. `RecordOutcome` appends
  `DecisionOutcomeRecorded`.
- `Decision` gained two optional fields, `catalogue_version` and `decider`. They
  are omitted when null, so events written before C-004a keep their persisted form
  and still read. C-021 added three more, `state`, `question` and `calibrated`, the same way.

C-011 added `ToolShortlist` (see [Session actor and turn loop](#session-actor-and-turn-loop-c-011)) and feeds the state
the extractor's entities. C-004b added the two-tier escalation, the record of declined questions and the rule that an
uncalibrated answer never diverts the turn (see [Kev decider](#kev-decider-c-004b)). Not built yet: the `remember` durability score; the `tool` question's below-floor variant that escalates on the 0.8B generator
(the caller's choice for now); generator logprobs as a Noul confidence (`IChatClient` has no logprobs surface). Decision
replay against new floors and calibration tooling are C-021.

## Sandbox (C-007a)

Plan §10.1 and §3.4, and `vault.md` §3.3 and "Implementation status". Linux only.

- `sandbox/Corvid.Sandbox.Launcher` builds `corvid-sandbox`, a Native AOT executable
  (`IsAotCompatible`, published and smoke-run in CI's aot job) with no package or project
  dependencies. Its command line is the contract (`corvid-sandbox --help`): `--workspace`,
  `--chdir`, `--ro`, `--deny`, `--env NAME`, `--network`, `--best-effort-layers`, `--bwrap`,
  `--status-file`, then `-- COMMAND`. Exit status is the command's, or 125 when the sandbox
  was not set up and nothing ran. The status file (JSON: `started`, `exitCode`, `layers`,
  `error`) tells a refusal from a command that exited 125. It is opened before anything runs,
  with `O_NOFOLLOW`, and refused if it resolves inside the workspace, because the launcher
  itself runs unconfined and a command could otherwise redirect that write.
- Stage 1 (the supervisor) resolves the policy with every path canonical and runs bubblewrap:
  `--unshare-all --unshare-user --disable-userns` (new user, mount, PID, network, IPC, UTS and,
  where possible, cgroup namespaces; no nested user namespaces), `--share-net` only with
  `--network`, `--die-with-parent`, `--new-session`, `--clearenv`. `/` is bound read-only, the
  workspace read-write, `--ro` paths read-only (parents before children), and `/tmp`, `/run`
  fresh. Then the masks: directories behind a mode-0000 tmpfs, files behind `/dev/null`.
  Masked: `~/.ssh`, `~/.gnupg`, `~/.aws`, `~/.config/gh` (and `$XDG_CONFIG_HOME/gh`), Vault's
  data and config directories (`$XDG_DATA_HOME/corvid-vault`, `$XDG_CONFIG_HOME/corvid-vault`
  and the `~/.local/share`, `~/.config` defaults), `$XDG_RUNTIME_DIR`, `.env` and `.env.*` files
  at the top of home and anywhere in the workspace, and `--deny` paths. Both `$HOME` and the
  password-database home are covered. Vault's directories for the current `$HOME` and XDG
  variables are created (0700) when missing, so a store created while a command runs is still
  hidden; other denied paths that do not exist are skipped. The supervisor stays the parent, and kills bubblewrap
  when its own parent exits or on SIGINT, SIGTERM, SIGHUP or SIGQUIT.
- Stage 2 is the launcher again, run by bubblewrap inside the namespaces. It checks that it is
  in none of the supervisor's namespaces (which also catches a stand-in for bubblewrap), sets
  `no_new_privs`, applies Landlock (read and execute anywhere; write only beneath the
  workspace, the sandbox's `/tmp` and `/dev/shm`, plus existing devices; from ABI 6, abstract
  Unix socket and signal scoping) and a seccomp deny list (EPERM for mount and namespace calls,
  `clone` with namespace flags, `ptrace`, `process_vm_*`, `bpf`, `perf_event_open`,
  `userfaultfd`, `io_uring_*`, `keyctl`, module and kexec loading, `TIOCSTI` and others; ENOSYS
  for `clone3`; another architecture's calls kill the process; tables for x86_64 and arm64).
  It reports the layers on a pipe, closes it, and `execve`s the command with exactly the
  allowlisted environment. Both layers bind the calling thread and survive `execve`, so all
  of stage 2 runs on the main thread. Without Landlock or a seccomp table it refuses unless
  `--best-effort-layers` is given; bubblewrap is never optional.
- Refusals (exit 125, nothing run): bubblewrap missing or failing, a namespace missing, the
  launcher or bubblewrap inside the workspace (the command could replace it), the workspace or
  working directory inside a denied path, a secret-like `--env` name (tokens, keys, passwords,
  agent sockets, cloud and VCS prefixes), a status file inside the workspace or behind a
  symlink, and a workspace with more than 100,000 entries to scan for `.env` files.
- `src/Corvid.Sandbox` (core, `IsAotCompatible`, no dependencies): `SandboxPolicy` (workspace,
  read-only and denied paths, network, environment names; default environment `PATH`, `HOME`,
  `USER`, `LOGNAME`, `LANG`, `LC_ALL`, `LC_CTYPE`, `TERM`, `TZ`), `SandboxCommand`,
  `SandboxResult` (exit code, output, truncation flags, `TimedOut`, layers), `ISandbox` and
  `BubblewrapSandbox`. It finds the launcher through `SandboxOptions.LauncherPath`,
  `$CORVID_SANDBOX`, or next to the application, starts it with a cleared environment (the
  policy's names plus what the launcher needs: `PATH`, `HOME`, the XDG directories,
  `DOTNET_ROOT`), and enforces a timeout (kills the whole tree), a per-stream output limit
  (the rest is drained) and cancellation. A refusal throws `SandboxRefusedException`; it never
  falls back to running unsandboxed. Its secret-name rule repeats the launcher's so a value is
  refused before it leaves Corvid; a test keeps them in step. `BuiltInDenials` repeats the launcher's
  built-in denials (secret directories, Vault's directories for home and the XDG variables, the runtime
  directory, the `.env` name rule) for the approval pipeline, kept in step by another test.
- Tests: `tests/Corvid.Sandbox.Tests`. The confinement tests (trait `Category=Confinement`)
  run real commands under bubblewrap against a temporary HOME and XDG layout kept outside
  `/tmp`, each with a positive control, and prove that a command cannot read or list Vault's
  directories, `~/.ssh`, `~/.gnupg`, `~/.aws`, `~/.config/gh` or `.env` files, cannot write
  outside the workspace, has no network by default, gets only the allowlisted environment,
  cannot create namespaces, and dies on timeout, cancellation and launcher death; and that
  the refusals above run nothing. Landlock and seccomp are also tested on their own (stage 2
  outside bubblewrap), and a small BPF interpreter checks the filter's decisions. CI runs on
  the org's self-hosted runner (an Arch Linux host with bubblewrap and unprivileged user
  namespaces; the runner has no root), first checks that bubblewrap and `unshare -U` work,
  and the required step "Sandbox confinement ran and passed" fails unless all of them ran
  with none skipped.

Limits: masking is by path, so the same files reached another way (a hard link, another
mount of the same file system, a snapshot directory) stay readable; Landlock grants only and
cannot hide a path inside a readable tree. The `.env` scan runs before the command, to a depth
of 12, skipping `.git` and `node_modules`.

Not built yet: `*.pem` masking and `.env` files below the top of home, Landlock deny-read,
graceful termination (SIGTERM to the launcher ends the sandbox with SIGKILL), extra writable
paths such as tool caches in home, the HTTPS proxy route for sandboxed scripts (V-003), the
`corvid-exec` user and value injection for Vault's exec path, container and SSH backends,
macOS and Windows, hiding the other credential stores the approval pipeline protects (`~/.kube`,
`~/.netrc`, `~/.docker/config.json` and others) and Corvid's own configuration (`~/.config/corvid`; Landlock
already blocks writes to it), and the code tool (§15). The shell tool (C-012) runs only through the sandbox and passes
Vault's configured locations as denied paths (see [Built-in tools and scheduler](#built-in-tools-and-scheduler-c-012)).

## Memory store (C-008)

`Corvid.Memory` (core, `IsAotCompatible`) implements plan §9.1 (tiers 1–3), §9.2, §9.3 without
reranking, and the memory side of §4.3. It needs no new package: `Microsoft.Data.Sqlite` and
its e_sqlite3 bundle (SQLite 3.53, with FTS5 and the trigram tokenizer) are already on the
allowlist. No Starling or provider package is referenced; tests use fake embedders, extractors
and deciders.

- `MemoryDatabase` is one SQLite file (WAL, `synchronous=FULL`, schema version 1), separate from
  the event log, with a connection per operation as in `SqliteEventLog`. Times are UTC ticks.
- Core blocks: `CoreBlockStore` holds `user`, `agent` and `persona` rows, each with a token budget
  (350, 550 and 500 by default, about Hermes' character limits at chars/4, counted with an
  `ITokenCounter`), a local-only flag and a revision. `Set` refuses content over the budget
  (`OverBudget`) or against a stale revision (`Conflict`) and changes nothing. `Snapshot()` is the
  frozen per-session copy; `Render(includeLocalOnly)` leaves local-only blocks out for non-local
  models. Nothing chooses that flag from a model profile yet.
- Episodic: `EpisodicIndex.CatchUp(log, session)` copies the text of user and assistant messages,
  tool calls and results (JSON values, not raw JSON, because the log escapes non-ASCII), approval
  requests, compaction summaries, goals and todos into `episodes`, a page per transaction, behind a
  per-session high-water mark, and returns the episodes it added so the caller can embed them
  (`MemoryVectors.EmbedAsync`; the re-embed job also finds any item without a vector). Decisions, outcomes, switches and memory events are not indexed.
  Rows mirror the append-only log, and triggers reject updates and deletes. `Search` ranks by FTS5
  BM25 with the trigram tokenizer, so text without word breaks (Chinese, Japanese) is found by
  substring; query terms of three or more characters are OR-ed phrases. A query whose terms are all
  shorter (`東京`, `Al`) cannot use a trigram index and falls back to LIKE, newest first.
- Vectors: `IVectorIndex` keys vectors by `(item, embedder_id)`; one embedder id has one dimension
  count. `SqliteVectorIndex` stores normalized float32 BLOBs (native byte order) and scans every
  vector of the embedder per query: exact, and linear in the number of items. Items are `event:<id>`
  episodes and `fact:<id>` facts.
- Embedder swaps (§4.3): `MemoryVectors` tracks the active and pending embedder.
  `BeginSwap(id)` makes the first embedder active at once on an empty store, and otherwise records
  it as pending. From then on `EmbedAsync` writes new items with both embedders.
  `ReembedAsync(batch)` is the re-embed job: it embeds items missing a vector for the pending
  embedder, each row in its own transaction, so it can stop and resume anywhere. Until none is
  missing, retrieval embeds queries with the old embedder and searches the old index, and FTS
  covers everything. Then one update makes the pending embedder active, and the old vectors are
  deleted. A run reports `Completed` only after that switch; an item written without its new
  vector during the run's last batch leaves the swap pending for the next run. If the active embedder is not loaded in the process, retrieval uses FTS and the graph only.
  With no swap running, the same job backfills items whose embedding failed. Invalidated facts are not re-embedded.
- Fact graph: `FactGraph` stores entities (matched by label and normalized name: NFKC, collapsed
  whitespace, lower case), facts with optional relations (subject, predicate, object), and links
  from facts to the entities they mention. Each fact has valid time (`valid_from`, `valid_to`) and
  transaction time (`recorded_at`, `invalidated_at`). `Invalidate` and `Supersede` set `valid_to`
  once; the schema's triggers reject deleting a fact and every other update. `AsOf(validAt, knownAt)`
  answers both "what was true in March" and "what did Corvid believe then", because an invalidation
  recorded after `knownAt` was not yet known. `Expand` returns current facts about the seed
  entities (hop 0) and about entities those facts mention (hop 1).
- Retrieval: `HybridRetriever` builds three ranked lists: BM25 (facts and episodes interleaved by
  rank, since their BM25 scores are not comparable), vector search (over-fetched, then filtered to
  current facts and the requested session), and the one-hop graph list from the turn's entities.
  It fuses them with reciprocal rank fusion (k = 60), multiplies by
  `1 + 0.5 · 0.5^(age / 30 days)`, cuts each snippet to 200 tokens and fills the token budget
  greedily, skipping items that do not fit. An embedder failure drops the vector list and the
  result reports no embedder. Only current facts are returned.
- Write path: `MemoryWriter.WriteAsync` extracts entities with the §9.2 labels, retrieves up to
  five neighbouring facts, and asks `memory_op` at the `pre_execute` checkpoint through
  `DecisionFabric`, with the candidate and neighbours as JSON instructions and the entities in the
  TurnState. Because `memory_op` is a gate, only a calibrated answer above its floor (0.6) writes:
  `add` adds the fact, `update` supersedes the best-ranked neighbour, `invalidate` ends it, and
  `noop` writes nothing. Everything else is staged in `pending_writes` with the decider's proposal
  and the policy's reason. The degraded decider never answers a gate, so without Kev every write is
  staged and nothing unreviewed is written. `ApproveAsync` applies a staged write (the
  reviewer may change the operation or target; a write the graph or block store then refuses stays
  pending) and `Reject` discards it; both record the decision's
  outcome (`approved`, `corrected` or `rejected`) for calibration. `EditBlockAsync` sends `user` and
  `agent` edits through the same gate; an edit over the budget is staged as `over_budget` without
  asking, and `persona` is edited only by the user. Every write appends `MemoryWritten` with the
  fact, block (`block:user`) or pending id and the operation; staging records `staged`.
  The memory row is written before the event: the two files are not one transaction, so a crash
  can leave a row without its event, never an event without a row.

Not built yet: sqlite-vec (no first-party .NET package; it would replace the scan behind
`IVectorIndex` and needs a checker rule for its native library); Kev reranking of the top 8
(§9.3); the consolidation job that rewrites an over-budget block (§9.5); relation extraction
(`IExtractor` returns spans, so relations come only from callers) and entity aliasing; fact
deduplication and merging; media memory (§9.4). C-015 added local-only facts (schema version 2) and core blocks that are
local-only by default, and the turn loop leaves both out of a cloud model's prompt. C-012 added the memory tools; C-013 added `/memory` (pending, approve, reject) and runs
`EpisodicIndex.CatchUp` after each turn. C-011 starts the re-embed job when
`embedder.text` switches and the write path when `remember` acts at egress.

## Calibration and decision export (C-021)

Plan §5.5, §16 and the replay half of §3.2. The code is in `evals/Corvid.Evals/Calibration` (namespace
`Corvid.Evals.Calibration`), which now references `Corvid.Fabric` for the catalogue, floors and the policy's measure. The
entry point is `corvid-evals` (`evals/Corvid.Evals.Calibrate`, a console app with no logic of its own; `CalibrationCli` in
the library parses the command line so tests drive it). `corvid evals calibrate` (C-013) runs it by path.
Neither project is production code, so the checker does not scan them and no package was added.

```sh
corvid-evals calibrate --log events.db [--log more.db] --version 2026-11-01.1 --out floors.json [--floors current.json] [--report-json report.json]
corvid-evals report --log events.db [--json report.json]
corvid-evals replay --log events.db --candidate floors.json [--json replay.json]
corvid-evals export-decisions --log events.db --out decisions.jsonl [--labelled-only] [--calibrated-only]
corvid-evals export-trajectories --log events.db --out trajectories.jsonl
```

- Reading: `DecisionSet.Load` reads every session of each log (`SqliteEventLog.ListSessions`, added for this) and joins
  each `Decision` with its `DecisionOutcomeRecorded` events. A path that does not exist is an error, because opening a log
  creates it. The stored `question_id` is the request key, split on the first `/` into the catalogue id and an instance
  (`clear_results/call-7`); keys the catalogue does not know are counted and ignored. The first decision keeps an ID, as in
  the projection; outcomes for unknown decisions are counted. The last outcome labels a decision.
- Outcomes are free text in the log. `OutcomeClassifier.Default` lists the ones Corvid writes today: positive `accepted`,
  `approved`, `succeeded`, `task_succeeded`, `tool_succeeded`, `user_accepted`, `user_approved`; negative `corrected`,
  `denied`, `failed`, `rejected`, `retried`, `task_failed`, `tool_failed` and the `user_` forms (§5.5: corrections,
  denials, retries and tool failures are negative). Anything else is unlabelled, reported, and never used for fitting;
  `--positive` and `--negative` extend the lists.
- Tier: fails closed. `degraded` and `degraded after …` deciders are the degraded tier whatever else is recorded, as is
  an answer recorded with `calibrated: false`. An answer is calibrated only when its event records `calibrated: true`;
  a missing decider (before C-004a) or an answer without the flag (before C-021) is unknown, so a decider's name alone
  never makes its answers fitting evidence. A decision with no answer keeps its decider's tier. Only calibrated decisions are fitting evidence, for gates and
  routing questions alike, since floors apply to a calibrated decider's confidence. The degraded and unknown tiers are
  reported in their own section. With no Kev every decision is degraded, so on today's logs `calibrate` fits nothing and
  writes the current floors under the new version.
- Measure: the answer is rebuilt from the stored probabilities with the options in the order asked (from the recorded
  question, else the catalogue's fixed options; the stored map is unordered, so score levels never come from it), and
  measured by `DecisionPolicy.MeasureOf`, the policy's own definition: choice confidence, P(yes) or expected score.
  "Acted" is derived, not read from the action name alone: for `risk`, `ask` is both the act for levels 3–4 and the
  below-floor action, so a risk decision acted when its measure reached the recorded threshold. For every other row the
  act differs from the below-floor action, which also covers a missing condition. An empty probability map (a degraded
  gate) has no answer.
- Fitting (`FloorFitter`): per catalogue question, the floor is the lowest observed measure at which the calibrated,
  labelled decisions at or above it number at least `--min-support` (20) and have a positive share at or above the target
  (`--gate-precision` 0.9, `--routing-precision` 0.8). Labelled decisions count whether or not they were acted on: a
  staged memory write that the reviewer approves is evidence for a lower floor. This assumes an outcome judges the
  decider's answer whichever action followed, which holds for today's outcomes (C-008's review) but not, for example, for
  an "accepted" after a `done` that fell below its floor and continued. Below the minimum support the floor is kept
  (`insufficient_data`); when no floor reaches the target it is kept and reported (`target_unreachable`) rather than
  raised automatically. The output keeps the degraded margin and every other floor, must have a new version, and is
  parsed back through `FloorConfiguration.Parse` before it is written.
- Report (`CalibrationReport`, text and JSON, format `corvid-calibration-report` 1): decisions per tier, decider and floor
  version; per question the acted rate, the escalation rate (the share that took the below-floor action: handed to the
  generator, the user or review), outcome coverage, precision when acted and, for calibrated decisions, ten reliability
  bins over the measure (an expected score divided by its top level) with the observed positive rate; unknown questions,
  unlabelled outcomes, and the fit per question with precision and coverage at the current and the new floor.
- Replay (`FloorReplay`, text and JSON, format `corvid-floor-replay` 1): each answered decision's measure is compared with
  the threshold it was held to (recorded on the event) and with the candidate floor, and decisions whose action would
  change are listed with their outcomes. The policy is not re-run, because the caller's conditions are not in the log and
  a missing one would read as failed. A decision held back by a condition stays held back; a decision that now acts on a
  question with required conditions is flagged `conditions_unverified`; a risk level of 0–1 is reported as
  `approval_mode` because the permissive mode is not recorded. Degraded gates, heuristics and conservative defaults never
  change. Degraded generator yes/no answers are compared with the floor as the policy does. Degraded embedding choices
  cannot be replayed against a new margin (the margin itself is not recorded) and are counted as not replayable.
- `Decision` events now also carry `state`, the TurnState JSON the decider saw, `question`, the question as asked
  (`kind`, `instructions` as a JSON string or object, `options` with descriptions, in order), and `calibrated`, the
  answer's `DecisionAnswer.Calibrated` (null when there was no answer). All are optional and
  omitted when null, as `catalogue_version` and `decider` were in C-004a, so older events keep their persisted form. The
  state is repeated on each decision of a checkpoint (up to about 600 tokens each by default). Instructions are whatever
  the caller sent, so `memory_op` decisions now hold the candidate fact and its retrieved neighbours.
- Kev training records (`KevTrainingExport`, JSON lines, format `corvid-kev-decision` 1): one per answered decision, with
  the source, session and sequence, question and instance, kind and role, the state (null for decisions recorded before
  C-021), the instructions and options (the catalogue's when the question was not recorded), each option's probability,
  the top answer, the measure, whether it acted and the acted answer (`yes` for a Noul question that acted), the action,
  threshold, floor version, decider, tier, outcome and label. Unanswered decisions are skipped; degraded answers are
  included and marked unless `--calibrated-only`.
- Trajectories (`ShareGptExport`, JSON lines): one per session with a conversation, `{"id", "source", "conversations":
  [{"from", "value"}]}` in the ShareGPT form Hermes uses. User messages are `human`; assistant text and tool calls are
  `gpt`, each call as `<tool_call>{"name", "arguments"}</tool_call>`; tool results are `tool`, as
  `<tool_response>{"tool_call_id", "name", "content"}</tool_response>`. Consecutive turns from one speaker are joined
  with a newline. Compactions are expanded, so the raw history is exported rather than summaries.
- Tests: `tests/Corvid.Evals.Tests` builds synthetic logs (`SyntheticLog`) and covers outcome joining and key splitting,
  tiers, measures per kind with option order, the risk rule, unlabelled outcomes, fitting (lowering, raising a gate,
  degraded data excluded, insufficient data, an unreachable target, score floors), the emitted file, the report, replay
  in both directions with held and unverified conditions and the margin case, both exports, the command line, and a
  round trip through `DecisionFabric` with a calibrated test decider.

`corvid evals` (C-013) runs `corvid-evals` with `--log` defaulting to the user's event log. Not built yet: Kev-0.8B against Kev-4B agreement (C-004b records which tier answered an escalated gate, not the answer it replaced); fitting the degraded
margin (the margin is not recorded); recording the caller's conditions and the permissive mode, which would make replay
exact; outcomes for questions other than `tool`, `risk` and `memory_op` (C-011 records `tool_succeeded` and
`tool_failed` on the `tool` decision that picked the tool), and with them an acted-only fit or per-question outcome
semantics; opening a live or read-only copied log without the log's write-mode setup (`SqliteEventLog` switches
the file to WAL on open); the system prompt and tool schemas in exported trajectories; and replaying recorded states
against a new decider model.

## Approvals (C-007b)

`Corvid.Approvals` (core, `IsAotCompatible`) implements plan §10.2, the approval half of §10.1, the taint
inputs of §10.3 and the `risk` row of §5.3. It references Abstractions, Fabric, Tools and Sandbox and no
package. The turn loop (C-011) calls it for every side-effecting tool. C-012 added `ApprovalOptions.ShellTools`: a call to
a shell tool is assessed as the command line it runs (see [Built-in tools and scheduler](#built-in-tools-and-scheduler-c-012)).

- Parser (decision D-5): `ShellParser` is hand-written for the subset Corvid permits: lists (`;`, newline,
  `&&`, `||`, `&`), pipelines, simple commands with prefix assignments, single, double and `$'...'` quotes,
  escapes, `$NAME` and `${NAME}`, `$(...)` and backquotes, a leading `~`, redirections, here-strings and
  here-documents (bodies parsed for substitutions unless the delimiter is quoted; only an exact delimiter line
  ends one). Everything else returns `ShellUnsupported` with a reason: control flow, functions, subshells and
  groups, arithmetic, brace and process expansion, parameter operators, `~user`, arrays, unterminated quotes,
  NUL, input over 64 KiB or nesting deeper than 16. Unsupported text is never guessed at: it asks, and the
  hardline backstop still scans it for Vault's directory name and disk formatters.
  `TreeSitter.DotNet` 1.3.0 was evaluated and rejected; the reasons are recorded in plan §20, D-5.
- Analysis: `ShellAnalyzer` expands words statically (tilde, `$HOME` and the XDG variables, assignments made
  earlier in the line, field splitting; anything else is dynamic), follows `cd` (both directories after `;`
  or `||`), and unwraps wrappers recursively: `timeout`, `env` (including `-C`), `nice`, `nohup`, `stdbuf`,
  `setsid`, `ionice`, `time`, `command`, `builtin`, `exec`, `eval`, `xargs` (its input is dynamic), `sh`,
  `bash` and `dash` with `-c` or a literal here-string or here-document on stdin, `sudo`, `doas`, `su -c`,
  `pkexec` (elevated), `watch` (joined and run by `sh -c`), `flock`, `taskset`, `chrt`, `busybox` and
  `find -exec`. Wrapper options are parsed strictly: an unknown or computed option refuses. Other programs'
  options are read tolerantly, with abbreviated long options (`rm --rec`) read as every option they could
  stand for. Substitutions are analysed as commands of their own. Each command becomes an `AnalyzedCommand`: verb and subcommand, effects
  (read, write, delete, recursive, execute, network, signal, elevated, system, configuration, ...), targets
  (paths resolved against the workspace as inside, workspace root, outside or unresolved; globs by their static
  directory; PIDs, process names, hosts, URLs) and unresolved reasons. Known programs are described by kind
  (readers, searchers, writers, archivers, network clients, git, package managers, system tools); an
  unknown one runs code Corvid cannot see (`execute`). After `source`, or with `PATH`, `LD_PRELOAD` and other
  code-loading variables set, and for a program named by a path outside the system directories, a command is
  unresolved but still described as named, so `./rm -rf /` and `env PATH=x rm -rf /` meet the hardline list.
  Interpreters' inline code (`python3 -c`, `node -e`) is recorded for the backstop; other shells' scripts
  (`zsh`, `ksh`, `fish`, ...) are outside the subset and ask. Readers lose their read-only standing when an
  option writes or runs something: `git log`/`diff`/`show --output`, `git format-patch -o`, `git grep -O`,
  `rg --pre`, `ag --pager`, `yq -i`, `sort --compress-program`, `sdiff -o`, `less -o`, and the output operand of
  `uniq` and `xxd`. A computed argument to `git`, or a computed pattern for `rg` or `ag`, may be such an option,
  so it is unresolved.
- Launchers (`Launchers.cs`) are unwrapped too, with their option syntax parsed strictly (a value-taking option
  consumes its value; an unknown option refuses), so the command they run meets the hardline list and target
  extraction as if it ran alone. Unwrapping never lowers risk. Tracers add their own targets: `strace` and
  `ltrace` (`-o` writes a file, or pipes to a shell command that is analysed; `-p` attaches to PIDs; `-u` elevates;
  `strace -E` sets the environment; fault injection is unresolved), `valgrind` (`--log-file` and the profilers' output
  files, with `%p` as a pattern; it also reads `./.valgrindrc`, `~/.valgrindrc` and `$VALGRIND_OPTS`, which can
  redirect `--log-file` or set `--trace-children`, so it always asks) and `perf stat`, `record` (`perf.data`) and
  `trace`. `gdb` (`getopt_long_only`: one- or two-dash long names, exact only) always asks, since a debugger runs
  whatever it is told and reads `.gdbinit`; `--args PROGRAM ARGS` and a bare `PROGRAM` are analysed as the command it
  would run (in its `-cd` directory), `-p`/`--pid` and a numeric second operand are process targets, core and symbol
  files are reads, `-x`/`-ix` scripts are reads plus unresolved, and `-ex`/`-iex` commands are unresolved and kept for
  the hardline backstop. `runuser` (permuted util-linux options) elevates: `-u USER [--] COMMAND` is unwrapped like
  `sudo`, `-c`/`--session-command` strings go through the subset parser (with `-l` or `-` the working directory is
  unknown; a shell other than `sh`/`bash`/`dash`, or the user's login shell, is unresolved), and `-u` with `-c`, `-l`,
  `-s` or `-f` refuses as util-linux does. `at` and `batch` defer the job to atd, so they always ask (`Schedule`); the
  time spec is consumed, a job from a static here-string, here-document or a pipe from `echo` of static words (no
  option other than `-n`, no backslash, glob or expansion) is parsed as a `/bin/sh` script in the current directory,
  `-f FILE` is a read target and unresolved, input Corvid cannot see refuses, `at -l` is `atq` (read-only) and
  `at -r`/`-d` is `atrm`. Only `at` and `batch` read a piped `echo`; a shell reading one still asks. Launchers that change namespaces, mounts or the user mark the launcher and everything
  it runs unresolved, so they always ask: `nsenter`, `unshare`, `firejail` (a short list of options), `bwrap`
  (writable binds are write targets, read-only binds recursive reads; the working directory is unknown) and
  `systemd-run` (elevated for the system manager; a service runs in `/` or the home directory with the manager's
  environment, which expands `$VAR` in its arguments; resource properties only; `--host`, `--machine` and `--shell`
  refuse). Shell strings go through the subset parser: `script -c` (permuted options; the typescript is a write
  target; a `$SHELL` other than `sh`/`bash`/`dash` is unresolved) and GNU `parallel`, whose static `:::` inputs are
  substituted job by job into the replacement strings (`{}`, `{.}`, `{/}`, `{//}`, `{/.}`, `{#}`, `{N}`; perl
  expressions refuse), while input from stdin or files stands in as a dynamic value.
- Tools: `ToolAnalyzer` describes a non-shell call from C-005's `ToolRisk` (filesystem write, destructive
  delete, network, messaging, schedule, memory, configuration, remote) and the targets in its arguments
  (parameters named like paths or addresses, or targets the caller passes). Recipients are targets too. A
  filesystem tool with no path found, or a messaging tool without `Recipients`, is unresolved, so it asks
  once and no remembered rule matches it.
- Hardline (`Hardline`, rule names in `HardlineRules`): disk formatters; writes to devices other than the inert
  ones (`/dev/null`, `/dev/tty`, ...) and `/dev/shm`; recursive deletes outside the workspace; writes to
  credential stores; any access to Vault's directories (by path, by glob, by a recursive change, move or
  deletion of an ancestor, or by name in unparsed text); writes to Corvid's safety configuration (`$XDG_CONFIG_HOME/corvid`,
  which holds the always-rules file). `ProtectedPaths` is built from `Corvid.Sandbox.BuiltInDenials`: Vault's
  directories at the defaults and the XDG locations, the sandbox's secret directories and the runtime
  directory, plus stores the sandbox does not hide (`~/.kube`, `~/.netrc`, `/etc/shadow`, ...) and credential
  file names (`.env` and `.env.*` other than examples, private keys). Hardline denials are not overridable by
  mode, rule or score, and ask nobody.
- Forced prompts: anything unparsed or unresolved, reading a credential store or a device or another
  process's `/proc` entry, a recursive read over Vault's directory, elevation, signals, configuration
  changes, and in a tainted turn any network egress and any send to a recipient not in
  `ApprovalContext.KnownRecipients`. These always ask and offer only "once".
- Decision: read-only actions run without asking. Then remembered rules (a denial first), then `risk` at the
  `pre_execute` checkpoint through `DecisionFabric`, with the parsed action (commands, effects, targets,
  taint, reversibility) as the question's JSON instructions. `risk` is a gate, so the degraded decider never
  answers it and every side effect asks until a calibrated decider exists (C-004b). Modes: `Manual` asks
  for every side effect without asking the fabric; `Smart` runs a calibrated level 0–1 only when every path
  is inside the workspace and there is no network, elevation, system change or destructive tool;
  `Permissive` follows the fabric (0–1 run, 2–4 ask).
- Asking: `AuthorizeAsync` appends `ApprovalRequested` (the action as JSON, the reasons and, since C-011, the
  `ApprovalContext.CallId` of the tool call waiting on it: an optional `call_id`, omitted when null, so older events keep
  their persisted form), asks the
  `IApprovalPrompter` with a deadline (`ApprovalOptions.Timeout`, five minutes) and appends
  `ApprovalResolved`: approved, denied, timed out, failed or cancelled, all but approval deny. The user's
  answer is recorded as the `risk` decision's outcome for calibration. With no prompter, or
  `Interactive = false`, an ask is denied without events.
- Rules: "session" and "always" answers become `ApprovalRule`s per side-effecting command: verb,
  subcommand, effects, elevation, the paths (a target must be at or under one) or patterns (a target must be
  the same pattern or a path it matches) and the names (hosts, PIDs, process names, recipients) approved, so
  approving `rm -rf build` never covers `rm -rf src` or `sudo rm`, and approving `rm *.o` never covers
  `rm main.c`.
  Session rules live in the pipeline; always rules in `IApprovalRuleStore`, by default in memory, or
  `JsonFileApprovalRuleStore` (format `corvid-approval-rules` version 1, unknown fields rejected, written
  atomically). Forced prompts offer no rule.
- Tests: `tests/Corvid.Approvals.Tests`, against the real event log. The bypass suite runs every shape in
  every mode with the degraded tier and with a fake calibrated decider that scores it level 0: wrappers
  (`timeout`, `env`, `nice`, `nohup`, `xargs`, `sh -c`, `bash -c`, `watch`, `flock`, `sudo`, ...) around
  `rm -rf`, quoting tricks, `$'...'`, here-strings and here-documents into a shell, substitutions, `find`,
  reads of Vault's directories, raw devices, credential stores and Corvid's configuration are denied as
  hardline; computed targets, `kill` with numeric PIDs, unparsed syntax, credential reads and code-loading
  variables ask. None runs automatically.

Not built yet: GLiNER2 extraction of free-form targets (SQL, `curl` data, `kill` targets), which waits for
spike S3; calibrated `risk` (C-004b); reversibility now comes from checkpoint state (C-014 sets `ApprovalContext.Reversible`);
restoring session rules on resume (they are not in the log); removing rules (C-013's `/approvals` lists them, and the
host keeps always-rules in `Corvid:Approvals:RulesFile`, by default `$XDG_CONFIG_HOME/corvid/approvals.json`); control flow, subshells and other syntax
outside the subset, which ask rather than run; unwrapping launchers not yet read (`perf` subcommands other than `stat`, `record` and `trace`, `lldb`, moreutils
`parallel`, which is read as GNU's), which remain programs that
run code Corvid cannot see; symlinks and `/proc/<pid>/cwd` paths, which are resolved lexically and left to the sandbox.

## Session actor and turn loop (C-011)

Plan §3.2, §3.3, §5.2, §5.4, §6, §7, §8 lever 1 and §10.3 (levers 2 and 3 came with C-016). The code is
in `Corvid.Kernel` (`Sessions`, `Turns`, `Scheduling`), `Corvid.Fabric` (`ToolShortlist`) and
`Corvid.Abstractions/Sessions` (`Envelope`). Kernel now references Approvals, Fabric, Memory, Models and Tools; none of
them references Kernel, and no package was added, so the checker's allowlist is unchanged. Everything runs on `IDecider`
with the degraded tier; nothing here references a Starling or provider package. C-013's `Corvid.Host` composes it as
described below (see [Host and CLI](#host-and-cli-c-013)).

- Envelopes and actors: surfaces post an `Envelope` (session, surface, text, origin `user` or `external`, optional message
  id) to `SessionHost.PostAsync`, a bounded channel (default 64) that waits when full. One router hands each envelope to
  its session's `SessionActor`, created on first use, and waits while that session already holds
  `MaxPendingMessages` (16): the backpressure surfaces see. A session that stops draining holds up routing for all. An
  envelope whose actor cannot be built (or was disposed) goes to `SessionHostOptions.RoutingFailed`, and routing goes on.
  Each actor reads a single-reader `Channel<SessionCommand>`: submit, cancel the running turn, switch a model, turn
  finished. The loop owns the queue of messages and the running turn; it starts each turn as a task and keeps reading, so
  a cancel or a `/model` switch is handled while a turn waits on a model or an approval prompt. Turns run one at a time in
  arrival order, and each finished turn (or the exception that ended it) goes to an `IDeliverySink`. Cancellation is one
  token per turn, linked to the actor's lifetime, passed to every model, tool, approval and log call. `CancelTurn` targets
  one turn, fixed at the call. If a turn is running, its token is cancelled before `CancelTurn` returns (callbacks run on
  the thread pool), so a caller that cancels and then unblocks the turn finds it already cancelled; the posted command
  names that turn, and the loop ignores it if the turn has finished and the next one started meanwhile. If none is
  running, the posted command cancels the turn running when the loop reads it: a message submitted before the call whose
  turn had not started yet. A message submitted after the call is never cancelled, and with nothing queued the cancel does
  nothing.
- A turn (`TurnRunner`): append `UserMessage`; extract entities from the user message when an extractor is bound
  (labels person, organization, place, date, time, project, file, url); rank the tool shortlist; ask the ingress
  checkpoint as one `DecisionFabric.DecideAsync` (intent, difficulty, clarify, `needs_recall` when a retriever is bound,
  and `tool` over the shortlist plus `none`) on one TurnState. The TurnState is built per checkpoint with the generator
  binding's own `ITokenCounter` when it offers one, the log's recent entries as one-liners, the goal, the entities, the
  step and the last tool result. `clarify` (or a calibrated `intent: ask_clarifying_question`; C-004b) makes the answer one question;
  `needs_recall` runs hybrid retrieval into the system prompt (below its floor, the top three facts only); `difficulty`
  above its floor escalates (never in the degraded tier, which only logs a heuristic).
- Generation (§7): a confident `tool` pick offers exactly that schema, `none` offers no tools, below the floor the
  shortlist is offered (`intent: answer_directly` above its floor also offers none). The prompt is the projection rendered
  by `PromptRenderer`: system prompt (with the frozen core blocks, local-only ones left out for cloud generators, and any
  recalled memory), user and assistant text, calls as `FunctionCallContent` (consecutive calls in one assistant message),
  results as `FunctionResultContent`, compaction summaries as notes. Each call is validated before it is logged: the
  name must be one offered, the arguments must fit the generator's JSON Schema subset (`required`, `type` lists with null,
  `enum`, array `items`; unknown names are errors, because the binder would drop them). Slot pre-fill makes the tool's
  free-text parameters extractor labels (their descriptions); a span at least 0.8 confident that the argument omits or
  contradicts is a hint, never a veto. A call whose arguments pass the schema and the binding runs as written. The span
  is shown, once per turn, only beside an argument the schema named in an error (missing or wrong) or the binding refused
  (a `ToolArgumentException` names its parameter), and only when the span itself passes: substituted into the arguments
  it must fit the schema and, for a tool that declares `[Probe]` (`list_files` and `read_file`), survive a probe run of
  the tool (its result discarded), so `list_files` is never steered to a directory that does not exist. The probe is
  opt-in, never inferred from `ReadOnly`: an MCP tool is read-only on its server's or the user's word, and a probe runs
  unlogged and unasked with the user's words. It runs under a call id of its own (`probe-` and a GUID), never the failed
  call's, so nothing keyed by call id (events, approvals, checkpoints, telemetry, a tool's own records) can take the
  discarded trial for the real call. Other tools get the schema check only. An invalid call is logged as
  `ToolCallIssued` and an error `ToolResult` in one append (details, the expected schema and any hints: the repair
  message), so a concurrent switch never reads the call without its result. A `ToolArgumentException` from the
  tool's own binding counts the same. A second failed response escalates the generator through C-006
  (`EscalateAsync`); with nowhere to go, or with `TurnOptions.EscalationAllowed` off (which every trigger respects), the
  turn fails with a message. No-op detection: when the first response with tools
  on offer calls none, a step checkpoint asks `done` and `tool`; if `done` is below its floor and `tool` picks a tool, the
  generator is asked again for that tool only (the narration is not logged), with `ChatToolMode.RequireSpecific` on a
  `native` tool-call profile and an instruction otherwise (Starling's template-rendered Qwen rejects required modes).
- Execution: calls that need no approval run in parallel, the rest one at a time in order (§3.3). Each
  valid call takes its lease before its `ToolCallIssued` is appended, so no issued call ever waits without one (a
  response's calls may therefore alternate with their results in the log). A
  read-only call is tracked with `TrackToolCall`. A side-effecting call, and a read-only network call in a tainted
  session (taint as of the response that made it), is parked (`ParkToolCall`) and goes through
  `ApprovalPipeline.AuthorizeAsync`, the pre-execute checkpoint, with the turn's taint, known recipients and its call id;
  a denial, timeout or refusal is logged as an error result while still parked, and an approval moves it to tracked with
  the new `SessionModelSlots.RunParkedToolCall`, so a concurrent switch never sees it as neither. Without an approval
  pipeline side effects are refused, never run. Every result is appended before the lease is released, and a turn that
  is cancelled or fails with an exception appends an error result for each open call before it ends, so a later switch
  finds no orphan. The `tool` decision that picked a
  tool gets `tool_succeeded` or `tool_failed` (C-021's vocabulary) when that tool's call finishes.
- Step checkpoint after each round of results: `done`, `stuck`, `tool`, `clear_results/<call>` for each full result
  older than the last two (at most eight per step, each with the old result as its instructions) and `injection/<call>`
  for each network tool's result this round. `done` above its floor makes the next call tool-free; `stuck` escalates, or
  finishes tool-free with status `stuck`; `tool` sets the next offer. The step budget (8 responses with calls) ends the
  turn with a tool-free answer.
- Result clearing (§8 lever 1): `PromptRenderer` stubs results from the log alone, so a resumed session renders the same
  prompt. A `clear_results` decision of `apply:stub` or its below-floor `stub_older_than_last_2` stubs the result
  (`[cleared: list_notes result, 1.2 KB; re-run or see event 14]`, citing the event sequence that holds it); `apply:drop`
  leaves a shorter stub; clearing only narrows. Then, while the prompt (with the offered tools' schemas) is over the
  generator profile's accepted context minus the completion (1,024) and a reserve (256), the oldest full results are
  stubbed, sparing the latest round, then all but the newest. Still over: C-016's levers 2 and 3 (see
  [Compaction](#compaction-c-016)), then escalate, or fail the turn.
- Taint (§10.3): an envelope from the user clears it, an `external` one sets it, and any result of a tool with
  `[Network]` sets it (a page, and a webhook's response alike). It holds across turns until the user speaks and is passed
  to every approval, so network egress and new recipients ask whatever `risk` says. That includes network reads: a
  read-only `[Network]` tool goes through the pipeline in a tainted session (C-007b gives it `tainted_network_egress`),
  because injected text could carry data out in a URL. Untainted network reads run unasked. `injection` adds a decision per
  network result; without Kev it takes `treat_as_tainted`.
- Egress: the answer is appended as `AssistantMessage`, then `remember` (when a memory writer is bound) and `deliver`
  (when `AskDeliver`, for gateways) are asked together; `deliver` acts only on a calibrated answer (C-020). `remember` above its floor sends the user message into
  `MemoryWriter.WriteAsync`, which without Kev stages it (`MemoryWritten` `staged`). Last, `CompleteTurnAsync`
  de-escalates unpinned slots. Every answer at every checkpoint is a `Decision` event through the fabric.
- `ToolShortlist` (Fabric, over plain `ShortlistItem`s so Fabric does not reference Tools): hard filters first (the
  caller's predicate: toolsets, platform, availability), then each item embedded once per embedder instance as
  `name: description` plus one vector per example request (multi-vector), scored by the best cosine with the query, plus a
  recency boost of 0.1 halving every 3 uses since its last call in the session, top 5. An embedder failure returns the
  filtered items unranked. Vectors are cached per embedder instance, so switching `embedder.text` starts a new cache. C-005
  has no examples file yet, so `TurnOptions.ToolExamples` supplies them.
- Device scheduler (§3.3): `DeviceScheduler` per device (`cpu`, `cuda:0`) with dedicated threads (one by default) and
  four FIFO queues, interactive generator, fabric, subagent, background; the highest non-empty queue runs next, and
  running work is not pre-empted. `RunAsync` completes with continuations forced off the device;
  `GetTaskScheduler(priority)` is what `Gliner2Extractor` takes (a test runs it there), and tasks completed on it do not
  inline awaiters on the device thread. `DeviceScheduling.Schedule` wraps an `IChatClient` (each streaming step too), an
  embedder, a decider or an extractor so its calls start on the device at one priority; the host wraps the generator at
  interactive for the turn and at fabric for the degraded decider's yes/no questions, and memory's embedders at
  background. Work must not wait synchronously for other work on the same device.
- Model switches and memory: `SessionActor.SwitchModelAsync` runs C-006's switch beside the turn (it waits for running
  calls, not parked ones). A switch of `embedder.text` starts `EmbedderSwapJob`: `MemoryVectors.BeginSwap(profile id)`,
  then `ReembedAsync` batches until the swap completes or a batch makes no progress. `MemoryVectors` must be built with
  every `embedder.text` binding the registry lists, keyed by profile id.
- Events: `ApprovalRequested` gained an optional `call_id` (see [Approvals](#approvals-c-007b)). Nothing else changed in
  the persisted format.
- Tests: `tests/Corvid.Kernel.Tests` drive real turns on the SQLite log with a scripted fake generator (which also
  answers the degraded decider's yes/no questions), a keyword embedder, fake extractors and prompters, C-005's generated
  fixture tools and hand-written tools: confident and unconfident picks and `none`, repair, escalation and de-escalation,
  failure with nowhere to escalate, binding failures, approval ask, deny and timeout, refusal without a pipeline, taint
  from fetched content and from an external envelope forcing approval under a calibrated level-0 `risk` in permissive
  mode, a network read asking under taint, an exception after a call is issued closing it, escalation disabled by policy,
  routing past a session whose actor cannot be built, no-op detection on template and native profiles, slot pre-fill, entities in the state, the token counter,
  clarify, the step budget, stuck, result clearing, cancellation with a switch afterwards, parallel read-only calls, the
  egress write path, actor ordering, a switch while a call is parked at a prompt, cancellation mid-turn, backpressure,
  the re-embed job, the device scheduler, rendering, validation and value conversion. `evals/golden/turn-read-only-tool`
  and `turn-repair-then-approval` were recorded from the loop; `GoldenTurnTests` checks the loop still produces them (ids
  and the hash order of probability maps aside), and `tests/Corvid.Evals.Tests` replays them like the seed set.

C-012 added the built-in tools, `TurnOptions.Interactive` for unattended turns and the `ToolCallContext` each call
carries (see [Built-in tools and scheduler](#built-in-tools-and-scheduler-c-012)); C-013 composes them in the host. Not
built yet (compaction came with C-016; C-015 added cloud slots, the request context and the frozen system prompt for
breakpoint caching): subagents and the subagent queue's users; the `delegate`
question (`wake` is asked by the scheduler, C-012; `skill` and `skill_opportunity` by C-017); GLiNER2 entity boosts in the shortlist (§5.4's
third bullet); per-tool concurrency limits beyond read-only versus side-effecting; an incremental projection in the actor
(each checkpoint and generator call reloads the session from the log: about 2.7 ms for 300 events, linear in the session's
length); persisting taint, which a resumed actor starts without; outcomes for `done`, `stuck`, `intent` and the other
routing questions (user corrections and retries need a surface); `RequireAny` for no-op detection without a pick;
recording why a turn failed beyond its answer text; and memory per device in `/status` (C-013 shows slot, device, queue
depth and median latency).

## Compaction (C-016)

Plan §8, §3.2 (`Compacted`), §4.2 step 4 and §13 (goals survive compaction). The code is in `Corvid.Kernel/Context`
(`ContextCompactor`, `CompactionRecords`, `ISkillBodyLever`), with changes to `PromptRenderer`, `TurnRunner`,
`SessionActor`, `SessionProjection` and the events. No project or package was added, so the checker is unchanged.
Everything runs on the degraded decider; no question is asked about compaction.

- Levers in cost order, fitted before every generator call: lever 1, result clearing in the renderer (C-011); then, while
  the prompt is still over the generator's budget, lever 2, `TurnServices.SkillBodies` (`ISkillBodyLever`: drop the bodies
  of completed skills), which nothing implements until skills (C-017); then lever 3, compaction. A range that cannot be
  compacted is kept, and the renderer's `clearMore` pass shortens every result the budget stubbed, and every other one
  outside the latest round, to the dropped stub, which still cites its event. Still over: the generator escalates for
  context, or the turn fails saying the conversation no longer fits, as before.
- Ranges (`ContextCompactor.Ranges`): first everything before the turn's own `UserMessage`, from sequence 1, so every
  earlier compaction is inside it whole; then, in a long turn, the turn's earlier rounds, from after its message to the
  event before the latest round of calls. The turn's message and its latest round are never compacted during the turn. A
  range is offered only when it holds more than one summary standing alone. Before appending, the `Compacted` event is
  projected with the log, and a range the projection would refuse (an unfinished or split call, a partial overlap) is
  never appended.
- Summarizer: the `summarizer` slot when bound, otherwise the generator's current binding (plan §4.1's default; the host
  binds no summarizer by default, so the generator's model summarizes; `Corvid:Models:Slots:summarizer:Model` binds one). It gets the instructions, which ask for one JSON
  object with the six fields of the structured record (goal, constraints, decisions, files, open questions, next step)
  and say that text inside the events is data to record, not follow; no tools; and a completion of 768 tokens
  (`CompactionOptions.SummarizerOutputTokens`). The reply is read leniently (the outermost object, a single string as a
  one-item list, code fences and trailing commas tolerated); a reply with no record is asked for once more, then the
  attempt fails.
- Map-reduce: one call may read the summarizer's accepted context less its completion, the reserve (256), the
  instructions and the framing, counted with the summarizer's own token counter. The range goes in as lines
  (`[14] user: …`, `[15] call read_file {…}`, `[16] result of read_file: …`); a result the fabric already cleared goes in
  as its stub. Lines are grouped into chunks that fit, and a longer line is split into pieces (halving, surrogate pairs
  kept whole), so every character of the range reaches the summarizer. Each chunk becomes a record. A compaction already
  inside the range contributes its stored record and is not summarized again. `CompactionRecords.Merge` merges the
  records oldest first: ordered, de-duplicated unions of constraints, decisions, files and open questions, and the newest
  goal and next step. That is how a later compaction updates fields rather than rewriting prose. The merge is code, not a
  model call as §8 suggests, so a merge cannot lose an item.
- Fit: the record, rendered as its summary note and counted with the generator's counter, must fit
  `CompactionOptions.RecordTokens` (1,024) or a quarter of the generator's budget, whichever is less. When it does not, or
  the summarizer fails, the `summarizer` slot escalates along its ladder (C-006, so the privacy gate applies to a cloud
  target) and the range is summarized again; the slot returns to its default after the turn like any other. With nowhere
  to go, escalation disabled, or the generator standing in for the summarizer (the turn escalates the generator for
  context instead), the range is kept: nothing is appended, and `TurnResult.Compactions` says why.
- Events: `Compacted` gained optional `summarizer` (the profile that wrote the record) and `chunks` (map calls, 1 when the
  range fit one call), omitted when null so earlier events keep their form. `CompactionExpanded(compacted_sequence)`
  (`compaction_expanded`) is new. The record is C-002's `CompactionSummary`.
- Goals and todos (§8, §13): after the last summary note, every render adds `[still in force after the summary]` with the
  standing goal and the todo list, read from their own events rather than from any record.
- Expansion (§4.2 step 4): the projection keeps every valid compaction and shows the outermost ones not expanded, so
  expanding an outer summary shows the inner one and the raw events after it. A new compaction is checked against the
  shown ones, so nesting stays whole after an expansion; an expansion naming no valid compaction, or one already expanded,
  is an issue. C-006's `IBudgetPolicy` stays a reporter: it runs under the session's switch lock, and a summarizer call
  there that fell back would wait for that same lock. The turn loop acts on the `Budget` of the `SwitchResult`
  (`TurnRunner.NoteBudgetChange`, called after its own escalations, after de-escalation at the end of a turn and by
  `SessionActor.SwitchModelAsync` for `/model`): when the generator moves to a larger window, before its next call the
  shown compactions are expanded newest first while the prompt stays within `ExpansionFill` (75%) of the budget, one
  `CompactionExpanded` event each, so a resumed session renders the same prompt. A smaller window needs no step of its
  own: every call is fitted to the current window and compacts when it does not fit, so compaction happens before the
  first call on the smaller model.
- Re-rendering for the new model's format needed nothing new: the renderer produces provider-neutral `IChatClient`
  messages for every profile; Anthropic cache breakpoints are C-015's.
- Content and taint: the summarizer is offered no tools, its instructions treat event text as data, and the record is
  rendered as a labelled summary note, so a summary carries no more authority than the raw range it replaces.
  Compaction neither sets nor clears taint (C-011's rules hold). The raw events stay in the log and the episodic index.
- Telemetry: a `corvid.compact` span per range (from, through, status, chunks, summarizer). The host's generator layer
  does not stream text under it (`HostTelemetry.StreamingSessionOf`), so when the generator's binding summarizes, a
  watching surface never shows the record as answer text.
- Tests: `tests/Corvid.Kernel.Tests/CompactionTests` drive real turns on the SQLite log with fake summarizers: one call;
  map-reduce with every character reaching the summarizer; a nested compaction updating the earlier record; a record over
  its budget escalating the summarizer and de-escalating after the turn; no ladder (nothing appended, the turn fails
  saying so); a summarizer that never writes a record (asked twice); the generator standing in; goal and todos after
  every compaction; a long turn's earlier rounds compacted; a `/model` switch to a smaller window compacting before the
  next call; a switch to a larger one through the actor expanding, then back compacting again; expansion within budget;
  ranges, chunking, lenient reading and merging. `SessionProjectionTests` cover expansion layers and refusals;
  `CompositionTests` checks a watched session sees only the answer and that a configured `summarizer` slot writes the records. `evals/golden/turn-compaction` was recorded from the
  loop (`GoldenTurnTests`), and `compaction-expanded` is a seed trajectory for the projection.

Not built yet: a summarizer that revises earlier items (the merged record only grows, so an open question answered later
stays listed); undoing
`clear_results` decisions on a larger window (stubs from the budget come back, stubs the fabric chose stay); expansion
after a fallback inside `SlotChatClient` moves to a larger model (fallbacks do not report a budget direction); compacting
the turn's own message or latest round; keeping a pending expansion across a restart (it is held in the turn runner, so
after a `/model` to a larger window and a restart the next turn renders compacted, which is safe but not expanded); host
configuration of `CompactionOptions` (defaults only); summarizer calls in parallel; telling summarizer calls apart from
answers when the generator's binding serves both (they are `chat {model}` spans tagged `corvid.slot=generator` under
`corvid.compact`, and count toward the generator's `/status` latency); a `/compact` command; and an implementation of
`ISkillBodyLever` (C-017).

## Host and CLI (C-013)

Plan §3.4, §12 (CLI), §16 (spans), §17 rows 1.2 and 1.3, and §4.4's `/status`. `Corvid.Host` is the composition root,
`Corvid.Cli` the `corvid` executable over it. The fabric runs on Kev-0.8B when it is downloaded and on the degraded
decider otherwise (C-004b, see [Kev decider](#kev-decider-c-004b)); Starling packages come from nuget.org through
`Corvid.Models.Starling` only.

- Configuration (`CorvidOptions`, section `Corvid`): data directory, workspace, per-slot model, escalation ladder and
  fallbacks by catalogue name (defaults `qwen3.5-0.8b`, `gliner2-base-v1`, `embeddinggemma-2`; no decider model),
  the model cache, CPU threads, approval mode, timeout and rules file, turn limits and system prompt, memory on or off, an
  OTLP endpoint, the Vault socket and the `corvid-sandbox` path. Sources, later winning: the user file
  (`$XDG_CONFIG_HOME/corvid/corvid.json`, or `$CORVID_CONFIG`, or `--config`), environment variables
  (`CORVID__Models__Cache`), then `--set key=value`. `CorvidOptionsValidator` runs at host start (`ValidateOnStart`) and
  reports every problem at once: unknown slots or models, a model that cannot serve its slot (in the default, escalation or
  fallbacks), a default repeated in its own ladder, a missing generator or text embedder (the degraded decider needs both),
  thread, timeout and turn limits, a non-HTTP endpoint and relative paths (`~/` is expanded).
- Models: `IModelProvider` (check offline, load, download explicitly) and `ModelCatalogue` (names unique across
  providers). `StarlingModelProvider`, registered by default, wraps C-010's `StarlingModelStore`; Qwen serves `generator`
  and `summarizer`. `ModelComposer` loads each configured model once and wraps it per use, outermost first: a span and
  latency layer (`InstrumentedChatClient`, which also streams for the turn's generator), then C-011's device scheduling at
  interactive priority for the slot bindings, fabric priority for the degraded decider (the default generator and embedder,
  even when a session has escalated), and background priority for memory's embedders (every configured `embedder.text`
  model, keyed by profile id). GLiNER2 takes the device's `TaskScheduler` at load. Bindings are cached per slot and model,
  because a binding's identity is its reference. Only `cpu` and `cuda*` placements are device-scheduled.
- Runtime (`CorvidRuntimeFactory.StartAsync`): first checks every configured model offline by size and, if any is
  missing or corrupt, throws `ModelsUnavailableException` listing all of them with the command that fetches each, before
  loading anything. Then the SQLite event log (`events.db`), `DecisionFabric` on the degraded decider, the approval
  pipeline with a per-session prompter router (a session with no surface attached fails the prompt, which the pipeline
  records and denies, never answering for the user), memory (`memory.db`: core blocks counted with the generator's
  tokenizer, episodic index, vectors, hybrid retrieval, and the write path when an extractor is bound), and C-012's
  built-in tools. When the configured text embedder differs from the store's active one, C-011's `EmbedderSwapJob` starts.
  Sessions are C-011's actors, one `SessionModelSlots` and `TurnRunner` each, sharing the rest; after each delivery the
  episodic index catches up with the log and embeds the new episodes in the background, queued before the sender hears back.
- Built-in tools and scheduler (C-012, wired here): the approval environment is the configured workspace (it must exist)
  with Vault's locations for this user (`VaultLocations.Default`, the socket from `Corvid:Vault:SocketPath`) added by
  `VaultLocations.Protect`. Corvid's own state wherever configuration puts it (the data directory with `events.db`,
  `jobs.db` and `memory.db`, the configuration file read, the rules file and a configured sandbox launcher) is added to
  the environment's safety configuration, beside `$XDG_CONFIG_HOME/corvid`, so a workspace that contains it (the home
  directory, say) cannot be used to rewrite the approval rules or the job store: the hardline list refuses writes, the
  file tools refuse the paths and the shell's sandbox hides them. A path that is or contains the workspace is left out.
  The pipeline gets the rules file, the timeout and `BuiltinTools.ShellToolMapping`, so the
  `shell` tool is assessed as its command line. The file tools work in `Workspace.For` that environment; the shell runs
  through `BubblewrapSandbox` (the launcher from `Corvid:Sandbox:LauncherPath`, `$CORVID_SANDBOX` or next to `corvid`;
  on other platforms every command is refused) with the same Vault locations and safety configuration hidden; `web_fetch` uses its default handler;
  the memory tools need the write path; the cron tools use `jobs.db`. Turns get `BuiltinTools.Examples` for the
  shortlist. `Corvid:Tools:Builtin` turns them off; `IToolProvider`s and `ToolFunction`s registered in DI are added
  either way. With `Corvid:Scheduler:Enabled` (default on) the runtime runs C-012's `JobScheduler` with the
  `SandboxedScriptRunner` and a `TurnJobAgent` whose turns are not interactive. `JobScheduler.RunAsync` marks runs other
  processes left running as interrupted, so only the process holding an exclusive lock beside the job store
  (`jobs.db.lock`) runs it; `/status` says which. Completed runs raise `CorvidRuntime.JobCompleted`, which `corvid chat`
  prints as a notice. C-020 queues a job's result to the gateway chat it was created in (see [Gateway](#gateway-c-020)).
- Slash commands (`SlashCommandRegistry`, `CoreCommands`): surface-neutral, returning `CommandOutput` (title, lines, a
  table, a tone, exit) that the CLI renders with Spectre and a gateway can send as `ToPlainText()`. `/model` lists slots or
  switches one through `SessionActor.SwitchModelAsync` (`/model <model>`, `/model <slot> <model> [--pin]`,
  `/model unpin <slot>`); it refuses a model missing from the cache (with the download command) and an `embedder.text`
  model memory was not built with. `/status`: per slot the model, device, queue depth (all priorities), median latency of
  the last 128 calls and call count, plus the degraded decider's row, the session's head, pending approvals, taint and goal.
  `/goal [text|clear]` appends `GoalSet`. `/undo` and `/rollback [N]` came with C-014 (see [Checkpoints](#checkpoints-c-014)).
  `/approvals` lists pending approvals and session and always rules. `/memory`
  lists staged writes, `approve <id> [operation]` and `reject <id>` resolve them. `/tools`, `/todos`, `/cancel`, `/help`,
  `/exit` (`/quit`). A line is a command only when its first word is `/name`, so a pasted path is a message.
- Telemetry (plan §16): the kernel (`KernelTelemetry`, source `Corvid.Kernel`) starts `invoke_agent corvid` per turn
  (status, steps, tool calls, taint, escalations) and `execute_tool {tool}` per call, covering its approval wait (call id,
  approval, outcome: succeeded, failed, invalid_arguments, denied, cancelled; a call rejected before running gets one too).
  The fabric (`FabricTelemetry`, `Corvid.Fabric`) starts `corvid.decide` per checkpoint with the checkpoint, decider and
  fallback, arrays of question ids, confidences, actions and whether each acted, and one `corvid.decision` event per
  question. The host (`Corvid.Host`) starts `chat {model}` client spans around every model call with GenAI request and
  response model, token usage and finish reason; the degraded decider's yes/no calls appear under `corvid.decide`. Tags
  are given at span creation so listeners see them on start. `AddCorvid` exports all three sources over OTLP when
  `Corvid:Telemetry:OtlpEndpoint` or `OTEL_EXPORTER_OTLP_ENDPOINT` is set (an Aspire dashboard listens on
  `http://localhost:4317`); otherwise nothing is exported.
- `TurnFeed` turns those spans into per-session events for surfaces (tool started and finished, checkpoint decisions) and
  carries streamed text: when a surface watches a session, the generator's span layer asks the model for a stream, forwards
  each text delta and reassembles the response the turn loop asked for (Starling's Qwen client reassembles to the
  non-streamed contents, tool calls included). Without a watcher it does not stream. Its listener also makes sure the spans
  exist when nothing exports them.
- `corvid doctor` (`Doctor`): configuration (validated, with the file in use), the data directory (writable), every
  catalogue model (`Check`, by size; `--thorough` hashes), marked failed only when configured (a missing decider model is a
  warning), the decider (Kev with or without its second tier, or the degraded tier and the command that fetches Kev),
  `MALLOC_MMAP_THRESHOLD_` in this process, bubblewrap on `PATH`, `corvid-sandbox` where `Corvid.Sandbox` looks for it,
  and the Vault socket (exists and answers). Nothing loads and nothing uses the network. Exit 1 when a check failed.
- glibc: Starling requires `MALLOC_MMAP_THRESHOLD_=65536` for GLiNER2 on Linux, and glibc reads it before `Main`.
  `GlibcMalloc.EnsureMmapThreshold`, called first by `corvid chat`, re-executes the same binary (`Environment.ProcessPath`)
  with the same argv (from `/proc/self/cmdline`) and the variable added; `CORVID_NO_REEXEC=1` turns it off.
- `corvid` (`Corvid.Cli`): `chat` (the default; `--session <id>` resumes, `--verbose` prints checkpoint decisions),
  `model [list]`, `model switch <slot> <model>` (validates the whole effective configuration with the change, then writes
  the user file atomically; new sessions use it), `model download <model>... | --all` (the only network use), `doctor`
  (`--thorough`), and `evals <command>` (see the dependency rules). Exit codes: 0, 1 failure, 2 usage or invalid
  configuration, 3 a configured model is missing (each missing model, why, `corvid model download <name>`, and
  `STARLING_MODELS`). `dotnet publish src/Corvid.Cli -c Release -r linux-x64` makes a self-contained single file with the
  ONNX Runtime natives inside, extracted on first run (about 65 MB).
- Chat UI (`ChatRenderer`, the one writer): scrollback-first. Streamed text goes straight into the scrollback; text that
  was not streamed (a fixed message, or an answer given while tools were on offer and the model was not streaming) is
  printed when the turn is delivered, never twice. On an interactive terminal a live panel lists the round's tools with
  elapsed time and outcome, and is closed (left as it last looked) before text, a prompt or command output; elsewhere each
  tool start and finish is a line. Approvals (`CliApprovalPrompter`) are a panel with the action, its commands, effects and
  targets, the reasons and the deadline, answered on the next line (`y` once, `s` session, `a` always, `n` deny, as
  offered); only a line typed after the prompt counts (`ConsoleChatInput` keeps typed-ahead lines for the chat), and the
  deadline or the end of input denies. Ctrl-C cancels a running turn and leaves at the prompt. Input is read on one
  background loop into a channel so cancelled reads never swallow a line; piped input is echoed.
- Tests: `tests/Corvid.Host.Tests` (configuration validation and binding, composition with fake providers, missing models
  reported before loading, DI tools, a scripted turn, resume, approval routing, the episodic catch-up, streaming and the
  tool feed, the span tree and its attributes, every core command, the doctor; the built-in tools registered, the shell
  mapped to its command, Vault protected, a file tool turn in the configured workspace, a due job run by the one process
  holding the scheduler lock) and `tests/Corvid.Cli.Tests` (scripted chat
  sessions on fake slots through `CorvidCli.RunAsync` and Spectre's `TestConsole`: streaming without duplication, the tool
  panel, inline approvals approved, denied and unanswered, resume, invalid configuration; the real Starling provider over
  an empty cache for `chat`, `model` and `doctor`; `model switch` and `download`; `evals` running the real `corvid-evals`;
  argument parsing; the live panel). They share `Fakes.cs`. The checker's tests cover the composition rule.
- Checked by hand on the development machine with the Starling models in the cache: `corvid chat` (from `dotnet run` and
  from the single-file publish) answered through GLiNER2, the degraded decider and Qwen3.5-0.8B, with `/status` showing
  latencies, the 15 built-in tools listed and the scheduler running, and the re-executed process carrying
  `MALLOC_MMAP_THRESHOLD_=65536`. With Qwen as the degraded decider's yes/no answerer, `clarify` acted on all three
  prompts tried (one of them a request to read a file in the workspace), so those turns ended with a clarifying question
  before any tool was offered to the generator. C-004b changed the policy: an uncalibrated answer no longer diverts the turn.

Not built yet: Ctrl+P; typing while a turn runs (the chat reads the next line after the delivery, so `/model` mid-turn is
available to the gateway and a later CLI, not this one); removing approval rules (`IApprovalRuleStore` has no removal);
`/memory` block edits; a `/cron` command; the rest of the ~35-command v1 set (`/insights` and
others arrive with their items; `/skills` came with C-017); the `config`, `skills`, `memory`, `cron` and `export` verbs (`gateway`
came with C-020, whose gateway also runs the scheduler and delivers job results to chats); a scheduler daemon; metrics and logs export; an Aspire
dashboard recipe; shipping `corvid-sandbox` and `corvid-evals` inside the publish (doctor reports where it looked);
per-device memory in `/status`; CUDA placement (C-010's); Vault-backed secret names for tools (`TurnOptions.SecretNames`
is not set, so secret-bearing tools are not offered); and persisting pins and taint across a resume.

## Built-in tools and scheduler (C-012)

Plan §7, §9, §10.1–§10.3 and §13. The tools are in `src/Corvid.Tools.Builtin` (core, `IsAotCompatible`), C-005 generated
tools: it references Approvals, Kernel, Memory, Sandbox and Tools, and the generator as an analyzer. The durable scheduler
is in `Corvid.Kernel/Jobs`, as plan §3.4 places it. No package was added, so the checker's allowlist is unchanged; nothing
references Builtin except tests and the host (C-013). Everything runs on the degraded decider.

- Contract changes. `ToolCallContext` (Corvid.Tools): the session, the call id, taint, whether anyone can be asked, and
  the approval pipeline's result for the call. The turn loop attaches it to every call's `AIFunctionArguments.Context`; a
  `[Tool]` method receives it by declaring a parameter of that type, which the generator binds from there (a nullable one
  accepts a call made outside a turn; a non-nullable one refuses it) and leaves out of the schema and the descriptor's
  parameters. `ApprovalOptions.ShellTools` maps a tool name to the string parameter holding a command line: the pipeline
  assesses such a call as that `ShellAction`, parsed and unwrapped, so the hardline list, rules and `risk` see the commands
  and a read-only command runs unasked; a call without its command stays a tool call that asks once.
  `TurnOptions.Interactive` (default true) reaches `ApprovalContext.Interactive` and the call context, so an unattended
  turn's asks are denied. `JsonValues` now writes `JsonNode` results as JSON (a `JsonObject` was written as an array of
  pairs).
- Files (`FileTools`): `read_file` (numbered lines, continued with `start_line`), `list_files` (optionally recursive),
  `search_files` (plain text or a non-backtracking regular expression), `write_file` (atomic replace, creating directories)
  and `patch_file` (replace text that must occur exactly once unless `replace_all`; LF text matches a CRLF file). Writes are
  `[SideEffect(Filesystem)]`; `write_file` is also `[Destructive]`: C-014's checkpoints can usually restore a replaced file,
  but not when they are off, git is missing or the file is too large, which the approval's per-call `reversible` says. Every path parameter is named `path`, which is how C-007b finds the target. `Workspace` resolves a
  path against the workspace and follows every symbolic link in it (realpath, missing components kept), so a link inside
  cannot lead out; it refuses paths at or under denied paths (`Workspace.For` takes the approval environment's Vault,
  credential-store and safety-configuration paths) and credential names (the sandbox's `.env` rule, examples included,
  and private keys). Listings and searches leave out credential names and denied paths, never descend into `.git`,
  `node_modules` or a link (searches skip linked files too), and stop at limits (`FileToolOptions`); files with a NUL in
  their first 8 KiB are binary and not read, and a file the user may not read (EACCES) is a "permission denied" tool
  error for `read_file` and `patch_file` (not reported as binary) and skipped by a search. Only regular files are opened: .NET reports a FIFO, a socket or a device as a
  file, and reading a FIFO nobody writes to blocks the turn for ever (a device may never end). `FileKinds.Special` asks
  Linux's `statx` (one layout on every architecture), links followed as `Workspace.Resolve` already did, so `read_file`,
  `patch_file`, `write_file` (which would otherwise replace the pipe) and a `search_files` naming one refuse it with a
  tool error, a search walking past one skips it, and a listing shows its kind instead of a size. Elsewhere the kind is
  not told and these files are treated as before. The path check gives the error its wording; the read itself
  (`FileKinds.ReadRegular`) opens with `O_NONBLOCK | O_NOFOLLOW` and checks the kind of the opened descriptor, so a
  path swapped for a FIFO (or a final link) after the check is refused, not read. `write_file` replaces by rename, so a
  swap there cannot block it.
- Shell (`ShellTool`, `shell`, `[SideEffect(Process | Filesystem)]`, `[Destructive]`): a command line run by `/bin/bash -c`
  (bash, because the pipeline parses the bash subset, D-5) through `SandboxedShell`, which builds one `SandboxPolicy`: the
  workspace, no network, the default environment (names that change what bash runs, such as `BASH_ENV`, `SHELLOPTS` or
  `LD_PRELOAD`, are refused), and as denied paths Vault's configured locations (`VaultLocations`:
  data directory, policy file and socket, from Vault's own defaults or from configuration) plus any extra paths.
  `VaultLocations.Protect` adds the same locations to the approval environment's Vault paths, so the hardline list denies
  them too. The tool runs only when its call context carries an allowed `ApprovalResult` whose analysis is the shell
  command it was given, so a call outside a turn, with no approval, an approval of a different command, or an approval
  of the call as an opaque tool call (no `ShellTools` mapping) is refused without running. Output is trimmed to
  `MaxResultChars` per stream, keeping the start and most of the end.
- Web fetch (`WebFetchTool`, `web_fetch`, `[Network]`, read-only): one `HttpClient` over an injectable `HttpMessageHandler`
  (Vault's egress proxy can sit in front later; the request carries `VaultHandler.Tainted` and `Toolset` options). The
  default handler follows no redirects, ignores ambient proxies, and connects only to public addresses after resolving
  the name (an IPv6 address that carries an IPv4 one, IPv4-compatible, NAT64 or 6to4, is judged by that address).
  Redirects are followed by the tool, at most five, only to http or https and never to a local address; URLs
  with credentials, other schemes and literal local or private addresses are refused before any request. The body is
  read up to `MaxBytes` while streaming, HTML is reduced to its visible text by a single forward pass (scripts, styles,
  the head and comments dropped, blocks on their own lines), non-text content is described but not shown, and every result
  says `"tainted": true`. As a `[Network]` tool its result taints the session (C-011), and in a tainted session the call
  itself asks.
- Memory (`MemoryTools`): `memory_search` (hybrid retrieval, §9.3), `memory_add` (`[SideEffect(Memory)]`: the C-008 write
  path in the calling session, so without Kev the fact is staged), `memory_pending`, `memory_approve` and `memory_reject`
  (`[Destructive]`: the proposal is gone). Approve and reject are side effects, so the turn loop asks the user before
  either runs; that prompt is the review.
- Scheduler (§13). `JobSchedule` parses Hermes' grammar: delays (`in 30m`, `2h`, `in 1h30m`), intervals (`every 30m`, at
  least a minute), days and times (`at 9am`, `tomorrow at 9am`, `daily at 9am`, `weekdays at 9am`, `mondays at 17:30`,
  `on mon, wed and fri at 8pm`), five-field cron with names and the `@daily` macros (Vixie day semantics), and ISO 8601
  times (with an offset, or wall-clock in the store's zone). Wall-clock times are in a time zone: a time in a daylight-saving
  gap does not occur and a repeated one runs once. It stores a canonical form (`once …`, `every 1800s`, `cron …`). `JobStore`
  is its own SQLite file (jobs change, so not the event log): jobs with their schedule as written and canonical, zone,
  optional pre-run script, `no_agent`, origin session, whether they were created in a tainted session, next run and state;
  runs with their own session id (`job:<job>:<run>`), status, `wake` decision, script exit code, output and detail.
  `ClaimDue` moves each due job on (missed ticks collapse into one) or completes a one-shot and records the run as running
  in one transaction, so a crash never runs a tick twice; `InterruptStale` marks what a stopped process left running.
  `JobScheduler.RunDueAsync` runs each claimed job: the pre-run script through `IJobScriptRunner`, then for a `no_agent`
  job nothing more (`script_only`); otherwise, when there was a script, `wake` at the tick checkpoint with the output in its
  instructions (below its floor: `skipped`, logged with the decision in the run's session), then the agent through
  `IJobAgent` in the run's session (`ran`). A refused script is `script_refused`; failures are `failed`. `RunAsync` loops on
  `TimeProvider`. `TurnJobAgent` runs one turn with services that must be unattended (`TurnOptions.Interactive` false), as
  an external envelope when the run is tainted (script output, or a job created in a tainted session). The built-in
  `SandboxedScriptRunner` authorizes a script through the approval pipeline non-interactively and runs it in the shell's
  sandbox, so only a read-only script or one an always-rule allows runs; without Kev every side-effecting script is
  refused. A job created in a tainted session has its script assessed as tainted, so its network egress asks (and is
  refused) whatever an always-rule says. The cron tools (`cron_create` and `cron_remove`, `[SideEffect(Schedule)]`, remove also `[Destructive]`, and
  `cron_list`) work on the store; a bad schedule is an argument error the generator can repair.
- `BuiltinTools.Examples` holds five to seven example requests per tool for the multi-vector shortlist
  (`TurnOptions.ToolExamples`), written by hand because C-005 has no examples file. `BuiltinTools.ShellToolMapping` is the
  `ApprovalOptions.ShellTools` entry the shell needs.
- Tests: `tests/Corvid.Tools.Builtin.Tests` covers each tool's metadata and behaviour (workspace escapes by `..`, absolute
  paths, `~` and symbolic links; protected and credential paths; listing, search and patch rules; the shell's refusals;
  redirects, schemes, local addresses, size caps and HTML; staged, approved and rejected memory writes; cron create, list
  and remove). Its `Category=Confinement` tests run the shell tool and the script runner under the real launcher and
  bubblewrap: a script the pipeline cannot see into cannot read Vault's non-default locations or `~/.ssh`, or write
  outside the workspace; there is no network; hardline commands never reach the sandbox; unattended side-effecting and
  Vault-touching scripts are refused; timeouts are reported. They skip only where bubblewrap cannot run, and CI's step
  "Shell tool confinement ran and passed" fails unless all of them ran. `tests/Corvid.Kernel.Tests` drives the shell tool
  through the real turn loop (read-only runs unasked, a side effect asks, hardline never prompts, an unattended turn denies,
  no mapping means no run) and covers the grammar (including daylight-saving changes), the store and the scheduler.

C-013 wires all of this in the host (see [Host and CLI](#host-and-cli-c-013)) and added `/memory`. Not built yet: a
`/cron` command; delivering job results to surfaces other than gateway chats (C-020 delivers to the chat a job was created
in); checkpoints before a scheduled pre-run script (C-014 checkpoints the turn
loop's calls only); network for the shell per toolset and
Vault's proxy route for sandboxed commands (V-003); the code tool and code mode (§15); container and SSH backends;
argument validators generated with the tool and the generated examples file (C-005; paths are checked by the tools at run
time, as argument errors the generator repairs); editing, pausing or resuming jobs, per-job toolsets, delivery targets and
pruning run history; a calibrated `wake` (C-004b; the degraded tier asks the generator yes or no); pre-run scripts with
side effects or network when unattended; for web fetch, the Vault handler itself, address checks after resolution when a
caller supplies its own handler (only literal addresses are checked then), charsets declared only in a page's markup,
PDFs and rendering JavaScript; `.gitignore`-aware search and globs; editing core blocks or a staged write's text through
a tool; and the subagent restrictions of §14 (children may not write memory or schedule jobs). Resolving a path and
opening it are separate calls, so a concurrent process that swaps a directory for a link in between could redirect one
access.

## Kev decider (C-004b)

Plan §2 (Kev's limits), §4.4, §5 (all), §5.5's two-tier decider and §5.6's fallback. Every `Starlings.*` package moved to
0.3.0 together; `Starlings.Models.Kev` 0.3.0 is the new one, in `Corvid.Models.Starling` only, with no new native package
(it loads through the CPU `Microsoft.ML.OnnxRuntime` 1.30.0 the adapter already brings), so the checker is unchanged.

- `KevDecider` (`IDecider`) over `KevDecisionModel`, CPU only (CUDA session options are refused), always with the head's
  calibrated temperature (any `KevOptions.Temperature` is refused, because answers are recorded as calibrated and C-021
  fits floors only on those). One `KevRequest` per checkpoint: the TurnState as `KevContent.Json` (Kev renders it as
  `key: value` lines), every question as its own row in ordinal key order, instructions as text or JSON, Choice options
  as `name: description`, Score levels as their text. Answers come back in the question's option order (Kev's yes/no is
  `no, yes`, so it is swapped), normalised in double precision, as `DecisionAnswer`s with `Calibrated = true`, so
  `Confidence` and `ExpectedScore` are the fabric's own definitions. Names: `kev-0.8b`, `kev-4b`.
- Limits. A state over Kev's 8,192 tokens (Kev's `KevInputTooLongException` without a question index) returns
  `DeciderUnavailable(StateOverBudget)`, so the fabric falls back to the degraded tier for that checkpoint and records
  `degraded after kev-0.8b: state_over_budget`. The fabric's own hard stop (1,500 by the generator's counter) usually
  falls back first. Each question's row (instructions and options after the state) is held to 1,000 tokens (§5.1: the
  0.37 s per question was measured with rows up to 853): Kev encodes once, rows over the budget are dropped from the
  encoding without encoding again, and the question is declined: no answer (its below-floor action) and
  `DeciderAnswers.Declined` gives the reason, recorded as the decision's decider, `kev-0.8b declined: row_over_budget`.
  A row over Kev's own 8,192 is declined the same way after one more encode; more than 255 options is
  `unsupported_question`. Every question declined is `Refused`, so the checkpoint falls back.
- Threading. `DecideAsync` runs Kev synchronously and returns a completed task. The host binds it to the `decider` slot
  through `ModelComposer`, which wraps every `IDecider` with `DeviceScheduling.Schedule` at fabric priority and a latency
  layer, so Kev runs on the CPU device's thread, behind the interactive generator and never on a session actor.
- Two tiers (`DecisionFabric`, §5.5). With a second decider, after the first tier answers, every calibrated answer to a
  gate (`risk`, `skill_write_safe`, `memory_op`) whose measure is below its floor (`DecisionPolicy.ClearsFloor`, without
  the caller's conditions) goes to the second tier in one request on the same state. Its answers replace the first's and
  the decision records `kev-4b after kev-0.8b: below_floor`; if it declines, fails or throws, the first answer stands as
  `kev-0.8b after kev-4b: failed` (or `refused`, `no_answer`), and either way the policy then decides, so below the floor
  on both tiers the user is asked. Routing questions, gates above their floor and checkpoints that fell back to the
  degraded tier never reach it. `FabricVerdict.Decider`, the span's `corvid.decider.escalated` and each
  `corvid.decision` event's `corvid.decider` show which tier answered.
- Degraded turn blocking. A catalogue row now says whether acting diverts the turn (`BlocksTurn`: `clarify` and
  `stuck`). The policy never lets an uncalibrated answer do that unless `degraded_turn_blocking` is set in the floors file
  (default false; floors version `2026-10-19.1`; older files load as false and C-021's fitter carries the setting over);
  the degraded decider does not even ask the generator about those rows, saving a prefill per question; and the turn loop
  takes `intent: ask_clarifying_question` only from a calibrated answer. C-013 had found Qwen3.5-0.8B saying yes to
  `clarify` on every real prompt. Other degraded yes/no answers (`done`, `needs_recall`, `remember`, `wake`) still act.
  The two recorded golden turns were re-recorded for the new floor version and the unasked rows.
- Taint and memory (§10.3). With Kev, a calibrated `memory_op` above its floor writes memory without review, which C-008
  never did without Kev. So `MemoryWriteRequest.Tainted` (and `EditBlockAsync`'s `tainted`) stage the write for review
  without asking the gate, recorded as `MemoryWriter.TaintedReason`: `memory_add` passes its call's taint and the turn
  loop's egress `remember` passes the session's, so text from outside the user (a fetched page, another sender's
  message) never reaches memory unreviewed. The `memory_approve` tool now takes the call's context and refuses in a
  tainted session (and without a turn's context), so the generator cannot approve such a write either; the user approves
  it with `/memory approve`.
- Host. `kev-0.8b` is the default decider model and `kev-4b` is in the catalogue (slot `decider`, profiles
  `kev-0.8b@cpu` and `kev-4b@cpu`, accepted context 8,192, locks from the package naming `jaredpalmer/kev-*`, files from
  `Starlinghq/kev-0.8b-onnx` and `Starlinghq/kev-4b-onnx` at their pinned commits). The decider is optional: its models are
  left out of the start's missing-model check, and `ModelComposer` binds the slot only with what is ready and loads,
  keeping the reason otherwise (missing, or any load failure, ONNX Runtime's included) in `DeciderNotices` (the model,
  what that means, and `corvid model download ...`), which
  `corvid chat` prints at start; `CorvidRuntime.DeciderState` is on `/status` and `/model`. A second tier is
  `Corvid:Models:Slots:decider:Escalation` (at most one; fallbacks are refused because the degraded tier is the fallback;
  a slot with only an escalation keeps its default model, so `decider:Escalation:0=kev-4b` is enough). The fabric takes
  the slot's default as its first tier and its escalation as the second. The fabric, approvals and memory share one
  decider, so `/model` refuses to switch the decider per session and points at `corvid model switch decider`. `/status`
  lists Kev, the second tier and the degraded fallback with their latencies. `corvid model download kev-0.8b` (3.1 GB)
  and `kev-4b` (16.9 GB) use the generic acquisition path; `corvid doctor` checks both and reports the tier.
- Tests. `tests/Corvid.Models.Starling.Tests/KevDeciderTests` (no model): the request shape, option rendering and JSON
  instructions, answer mapping and the yes/no swap, row budget declines without re-encoding, Kev's own row and state
  refusals, unsupported questions, malformed runs, the refusal of CUDA and raw temperatures, a run through
  `DecisionFabric`, and Kev starting on a device thread. `tests/Corvid.Fabric.Tests/TwoTierTests`: escalation of a gate
  below its floor on the same state, both tiers below the floor, routing questions and gates above the floor never
  escalated, a failing or declining second tier, no escalation after a fallback, declined questions recorded, the
  turn-blocking rule with and without the setting, and old floors files. `tests/Corvid.Kernel.Tests`: a generator that
  says yes to everything no longer stops a degraded turn, an embedding pick of `ask_clarifying_question` does not
  divert it, calibrated `clarify` and `stuck` still do, and with Kev `remember` writes the user's message but stages a
  message from outside; `WriterTests` and `MemoryToolsTests`: a tainted write or block edit is staged even when Kev would
  write it, and `memory_approve` refuses in a tainted session. `tests/Corvid.Host.Tests/DeciderTests` and a CLI chat test: Kev bound and answering on the device, a missing
  Kev or second tier, or a Kev that throws while loading, leaving the degraded tier with a notice, `/model` and
  `/status`, and the doctor's warning. Model-backed (`KevModelTests`, opt-in through
  `CORVID_STARLING_MODELS`; Kev-4B also needs 22 GB available): a calibrated ingress checkpoint on Kev-0.8B and Kev-4B,
  a real row-budget decline and a state over Kev's limit.
- Checked by hand on the development machine (CPU, bundles hashed against the locks): Kev-0.8B answered a four-question
  ingress checkpoint in about 1.3 s and Kev-4B in about 8 to 11 s. On "list the files in my workspace" over a five-tool
  shortlist, Kev-0.8B chose `none` at confidence 0.36 (below the 0.45 floor, so the shortlist goes to the generator) and
  Kev-4B chose `list_files` at 0.76; in `corvid chat`'s six-tool shortlist Kev-0.8B gave `none` 0.36 and `list_files`
  0.33 (confidence 0.17). `corvid chat` with Qwen3.5-0.8B, GLiNER2, EmbeddingGemma 2 and Kev-0.8B: "list my
  files" ran `list_files` and streamed the answer; Kev's `done` at P(yes) 0.82 stopped the loop. "what is in the notes
  folder?" streamed a response carrying narration and a `list_files` call through Starling's Qwen client after a failed
  `read_file` of the directory. "list the files in my workspace" failed: Qwen's `list_files {"path": "."}` was right, but
  C-011's slot pre-fill took GLiNER2's `workspace` span as a contradiction, and the repaired call followed it into a
  path that does not exist (a C-011 matter, not changed here).

Not built yet: Kev on CUDA (the adapter ships CPU natives only, as C-010 does); Kev-0.8B against Kev-4B agreement in
C-021's report (the replaced first-tier answer is not recorded); switching the decider per session (the fabric is
process-wide); asking the degraded tier about the questions Kev declined while keeping Kev's other answers; Kev state
prefix reuse across checkpoints (Unsupported upstream, U-4); spikes S1, S2 and S4 (C-003) on the published package; and
fitted floors, which wait for Kev decisions with outcomes in real logs.

## Checkpoints (C-014)

Plan §15 (checkpoints), §3.2 (undo and rollback as log positions), §10.2 step 4 (reversibility) and §17 row 1.13. The code
is in `Corvid.Kernel/Checkpoints`; the host wires it and adds `/undo` and `/rollback`. No project or package was added:
git is the CLI, started through `System.Diagnostics.Process`, so the checker's allowlist is unchanged.

- Store (`ShadowStore`): one git repository per workspace at `DataDirectory/checkpoints/<sha256 of the workspace's real
  path, 16 hex>`, created owner-only (0700) with no templates, the workspace's path written beside it (a store that names
  another workspace is refused). Every git command runs with `GIT_DIR` set to the store and `GIT_WORK_TREE` to the
  workspace, so the workspace's own `.git` is never read or written, from a cleared environment (`PATH` of the system
  directories, `HOME` and `XDG_CONFIG_HOME` a private directory in the store, `GIT_CONFIG_NOSYSTEM`, `GIT_CONFIG_GLOBAL=/dev/null`,
  `GIT_TERMINAL_PROMPT=0`, a fixed author), and with `-c` settings on every call that switch off hooks (`core.hooksPath=/dev/null`),
  fsmonitor, the untracked cache, credential helpers, pagers, editors, gpg signing, automatic gc and every transport
  protocol. The store's `info/attributes` (`* -text -filter -ident -diff !eol !working-tree-encoding`) outranks any
  `.gitattributes` in the workspace, so files are stored and restored byte for byte, and no filter configured anywhere
  runs. `info/exclude` lists the protected directories inside the workspace so listing never walks them. Both files are
  rewritten when the store is first used in a process. A workspace configured through a symbolic link is keyed by its real
  path, and targets named through either path are recognised. Operations on one store are serialized in the process; a second
  process using it at the same moment fails on git's index lock, which fails closed.
- A snapshot (`SnapshotAsync`) lists what changed since the last one (`ls-files --modified --deleted --others
  --exclude-standard`, cheap after the first thanks to the index's stat data), plus the call's own file targets even when a
  `.gitignore` leaves them out, and updates the index with `update-index` before `write-tree`, `commit-tree` on one chain
  and `update-ref`. Left out, and removed from the index if they were in it: any path with a `.git` component, credential
  names (the sandbox's `.env` rule and the approval pipeline's key names, `CheckpointOptions.For`), anything at or under
  the approval environment's Vault, credential-store and safety-configuration paths (which include Corvid's data
  directory, and so the store itself, when the workspace contains them), nested repositories (an untracked directory that
  is itself a repository; their contents are not checkpointed), and files over `MaxFileBytes` (32 MiB). A snapshot that
  would add more than `MaxFiles` (50,000) files fails.
- The write ledger: for a call that changes files (a tool with `[SideEffect(Filesystem)]` whose parsed action is not
  read-only: `write_file`, `patch_file`, and `shell` unless the command only reads), the turn loop takes a checkpoint after
  the approval and before the call runs, and another once it has finished, failed or been cancelled, before its result.
  Both are `CheckpointTaken` events with the call's id: `kind: "before"`, with `paths` naming target files that existed but
  could not be captured; and `kind: "after"`, with `paths` naming what changed between the two (`diff-tree`). A store that
  is available but cannot take the before checkpoint stops the call with an error result ("no checkpoint could be taken
  before this change, so it was not made"): the approval was given knowing the change was reversible. An after checkpoint
  that fails leaves the entry without its after state, and a restore across it is refused rather than guessed. With no
  store (git missing), calls run as before C-014, without checkpoints.
- Reversibility: before the approval pipeline runs, the turn loop analyses the action and sets
  `ApprovalContext.Reversible`, which reaches the `risk` state and the `ApprovalRequested` action. It is true only when the
  store works and the action only writes or deletes files at resolved paths inside the workspace, none excluded, none an
  existing file over the size limit, and no directory, pattern or recursive target ignored by the workspace's
  `.gitignore`; network, signals, elevation, system, configuration, messaging, schedule, memory, remote and running
  unseen code (`Execute`) are irreversible. Without checkpoints it stays null.
- Undo and rollback are one operation (`CheckpointService.RestoreAsync`) on a `RestorePlan`: a log position and the
  ledger entries after it. `/undo` goes to just before the last user message still in the transcript; `/rollback N` to just
  before the call of the Nth latest checkpoint still in the transcript (1 is the latest), where no call is open. The
  restore snapshots the workspace first (a `restore` checkpoint, kept so a later redo could use it), then reverts the
  entries newest first, path by path: a path that still holds what Corvid wrote goes back to what it was before the call;
  a path already back is left; a regular file the user has changed since is merged three ways (`git merge-file`: the
  change from Corvid's content back to the earlier one, applied to the current content), so the user's edits elsewhere in
  it stay; anything else (an overlapping edit, a binary, a link, a file deleted or recreated since, a target that was never
  captured) is a conflict. Any conflict refuses the whole restore and nothing is written. Before writing, every path is
  checked again: not excluded, not reached through a symbolic link or a file, and nothing on disk the snapshot did not
  capture (a directory, an oversized or ignored file) would be replaced. Deletions come first, then files are written
  through a temporary file and a rename, links as links, with git's mode (644 or 755). Paths the user changed that no
  ledger entry touched are never looked at. `CheckpointTaken(kind: "restore")` is appended before the first write and
  `SessionRewound` after the last, so a restore that a disk or permission error stops part way leaves the conversation
  where it was, reports the path it stopped at, and names the restore checkpoint that holds the workspace as it was.
  A store that cannot be created (an unwritable data directory) is a checkpoint that cannot be taken: the call is not run.
- A plan also lists calls after its position that changed files without a ledger entry (checkpoints off or git missing):
  the host passes the test (the tool's risk and the parsed action; a tool no longer registered counts as one that did), and a
  restore across any of them is refused, so the
  transcript never moves back over a change that stays on disk. With checkpoints off, a turn without such calls can still
  be undone (the conversation only, `CheckpointService.Rewind`).
- `SessionRewound(through_sequence, reason, restored, checkpoint_id)` (`session_rewound`, a new discriminator) hides the
  events between its position and itself from the transcript, and drops summaries and C-016 expansions appended in that
  range for good (a later compaction or expansion does not bring them back); a rewind that
  would leave a call without its result is reported in `Issues` and ignored. Goals, todos, approvals, decisions, model
  switches and checkpoints are not rewound. `SessionSnapshot` gained `Hidden`, `IssuedCallIds` (every call id ever issued,
  which the turn loop now uses so a model reusing `call_0` after an undo gets `call_0-2`) and `CheckpointEvents`.
  `CheckpointTaken` gained `call_id`, `kind` and `paths`, omitted when null, so older events keep their form.
- The session actor gained `RunExclusiveAsync`: work that runs when no turn is running and before the next one starts.
  `CorvidSession.UndoAsync` and `RollbackAsync` run there, so a restore never races a turn's writes or appends.
- Host: `Corvid:Checkpoints` (`Enabled`, default on; `GitPath`, default `git` on `PATH`; `MaxFileBytes`; `MaxFiles`),
  validated at start; the store lives under `CorvidPaths.Checkpoints`. `/undo` and `/rollback [N]` are in the core command
  registry: they report the files put back, the files merged with the user's edits, where the conversation now ends and the
  restore checkpoint, or, when refused, why and each conflicting path; `/rollback` alone lists the checkpoints newest
  first. `corvid doctor` checks for git (a warning when missing: writes still ask, but nothing can be undone).
- Tests: `tests/Corvid.Kernel.Tests` (`CheckpointTests`) run the real git: byte-exact binary, CRLF, executable and link
  storage; secrets, protected paths, nested repositories and oversized files left out; a file that grows past the limit
  leaving the checkpoint; a workspace that is itself a git repository, with `.gitattributes` that would convert line
  endings and hooks and an fsmonitor that would leave a mark, whose `.git` is byte-identical afterwards; git missing
  (unavailable with the reason, nothing created, writes marked irreversible, restore refused); reversibility per action;
  and through the real turn loop with the file tools and the shell: the before and after events around a write, undo and
  repeated undo, rollback N, the user's edits in the same file merged and in another file untouched, an overlapping edit
  refused with nothing changed, an edit made between two of Corvid's writes kept, binaries restored exactly and never
  merged, an oversized target refused, a failing checkpoint stopping the write, a destructive shell command checkpointed
  and undone while a read-only one is not, and undo queued behind a running turn with call ids kept unique. The
  projection tests cover rewinds, nested rewinds and refused positions; `tests/Corvid.Host.Tests` the commands, the
  store's location and exclusions, checkpoints off, the doctor and the settings' validation.

An undo or rollback reverts files only: other side effects of the turns it takes out of the conversation (a job created,
a fact written or staged, a page fetched) stay, as do the events that record them. A call cancelled while its approval
prompt was open is now closed with `error: "not run"` (it was `cancelled`), so a plan knows it changed nothing.

Not built yet: redo; checkpoints before scheduled pre-run scripts (they run only when read-only or allowed by an always
rule); restoring files a shell command writes that the workspace ignores, or ignored and oversized files inside a directory
it deletes (the target is checked, not every file under it); the contents of nested repositories; pruning or garbage
collecting the store, which grows with every checkpoint; rewinding goals, todos and taint with the transcript (taint stays
until the user speaks, which errs closed); hiding rewound turns from episodic memory, which indexes the raw log; running git
inside the sandbox (it runs as Corvid, with the protections above); a cross-process lock on a store (git's index lock makes
a concurrent second process fail closed); and an approval prompt for restores, which the user starts explicitly. A user
edit made in the moment between a call's before checkpoint and its after checkpoint is counted as Corvid's. Through the
gateway (C-020), `/undo` and `/rollback`, like every command, are the paired owner's alone.

## Skills (C-017)

Plan §11 entirely, the `skill`, `skill_opportunity` and `skill_write_safe` rows of §5.3, the curator of §9.5, §10.6's skill
half and D-9's skills. The code is in `src/Corvid.Skills` (core, `IsAotCompatible`), the turn-loop contract in
`Corvid.Kernel/Turns/Skills.cs`, and the host wiring and `/skills` in `Corvid.Host`. No package was added; the checker's
allowlist is unchanged. The skill questions go through the fabric like any other: Kev (C-004b) when it is downloaded, the
degraded tier otherwise; `skill_write_safe` is a gate, so Kev-4B is its second tier when configured.

- Format (`SkillDocument`, `Frontmatter`): agentskills.io `SKILL.md` folders as Hermes and Claude Code write them: a `---`
  frontmatter block with `name` (1–64 lowercase letters, digits, `-` or `_`) and `description` (up to 1,024 characters), then
  the Markdown body; SKILL.md is capped at 256 KiB. The frontmatter is read by a hand-written parser for the YAML subset these
  files use (block maps and lists, plain and quoted scalars, flow lists, `|` and `>` blocks, comments). Anchors, aliases, tags,
  flow maps, tabs, maps in list items and duplicate keys are refused, so a skill whose metadata cannot be read exactly is
  reported and not loaded; no YAML package enters a core project. Unknown keys (`license`, `allowed-tools`, `compatibility`)
  are kept and ignored. Hermes' activation fields are read from `metadata.hermes` (or the top level), `platforms` from the
  top level, and example requests for the shortlist from `metadata.corvid.examples`.
- Activation (`SkillAvailability`): `platforms` (`linux`, `macos`, `windows`), `requires_toolsets` and `requires_tools`
  (hidden unless all are present), `fallback_for_toolsets` and `fallback_for_tools` (hidden while any is present). Corvid has
  no toolsets (C-005), so Hermes toolsets map to Corvid tools (`terminal` → `shell`, `file` → the file tools, `web` →
  `web_fetch`, `memory`, `cronjob` → the cron tools, `skills` → `skill_read`; available when any member is registered), and
  some Hermes tool names to Corvid's (`terminal`, `patch`, `web_extract`, `skill_view`). Anything unknown is unavailable.
- Store (`SkillStore`, `SkillRepository`): the skills directory, `Corvid:Skills:Directory`, default
  `$XDG_DATA_HOME/corvid-skills` (beside the data directory, not in it: the sandbox hides the data directory, and skill
  scripts must be readable there). Skills are found at any depth up to four (Hermes' category folders), never through links
  or hidden folders (`.git`, `.archive`, `.hub`); a duplicate name is reported. The directory is a git repository: every
  change is a commit whose author is `user`, `agent` or `hub:<source>` (committer `corvid`). git runs with a cleared
  environment, no system or global configuration, `core.hooksPath=/dev/null`, no fsmonitor, pager, editor, askpass, ssh
  command, signing, credential helper or external diff, `core.symlinks=false`, the repository named explicitly and
  `GIT_CEILING_DIRECTORIES` set, so the user's git setup never runs. The repository's own `.git/config` is checked on open:
  any key beyond what `git init` writes (a hooks path, a filter or diff driver) refuses the repository. `.git` is added to
  the approval environment's safety configuration, so no tool writes it and the sandbox hides it. When the workspace contains
  the skills directory (the home directory, say), the directory is also bound read-only in the shell's sandbox and denied to
  the file tools, so no tool edit can reach a skill and then be committed as the user's; a workspace at or inside the skills
  directory turns skills off, with the reason in `/status`. Checkpoints (C-014) leave such a skills directory out, so a
restore never reverts what the learning loop wrote there (which the store would then commit as the user's); the skills
directory has its own history and `/skills revert`. Before every change (and
  at each refresh) whatever changed on disk is committed as the user's; a file's author is the author of the last commit
  that touched it. A skill is agent-owned when every file in it was last changed by the agent; it is trusted when no file
  came from a hub. `WriteAgentSkillAsync` and `ArchiveAsync` check ownership under the repository lock, after recording
  outside edits, and refuse anything else (a new skill's folder must still not exist then): the curator and the learning loop can change only agent-authored skills (the
  structural fix for Hermes #20273 and #64338). Each change made in a session appends `SkillChanged` (skill, revision,
  author). The kernel reads a snapshot taken at open, after each change and on `/skills`, never waiting on git.
- Levels: L0 is `ISkillSource.Available` (name, description, examples, trusted) after the activation rules; L1 is
  `Load` (the body, and the L2 file list), which records a use for the curator; L2 is `ReadResource`, confined to the
  folder (no absolute paths, `..`, hidden entries, links or binary files).
- Turn loop (C-011's `TurnRunner`): with `TurnServices.Skills` bound, the available skills are ranked by
  `TurnServices.SkillShortlist` (C-011's multi-vector `ToolShortlist`, top three) and `skill` joins the ingress checkpoint
  with them as `name: description` options plus `none`. A confident pick (or `TurnOptions.PinnedSkill`, used by replays and
  evals) puts the L1 body in the system prompt (after the history, with recalled memory, for a model that caches on
  breakpoints, C-015); below the floor the shortlist's L0 lines are shown, naming `skill_read`
  when it is registered; a confident `none` shows nothing. A body that is not trusted (a hub skill) taints the session
  (§10.3: outside content) and an `injection/skill:<name>` decision is recorded (without Kev, `treat_as_tainted`). With
  `TurnServices.SkillLearning` bound, an answered turn with at least three tool calls asks `skill_opportunity` at egress with
  the `tool_steps_at_least_3` condition (shorter turns are not asked: in the degraded tier each ask is a generator call);
  acting queues `SkillOpportunity` (the user message's and the answer's sequences, the decision id, the request).
  `TurnResult` gained `Skill` and `SkillQueued`. `ToolCallContext.MarkResultTainted` lets a tool that returns outside
  content without the network taint the session after its call.
- Learning loop (`SkillLearner`, `SkillsDatabase`): `Queue` writes the opportunity to `skills.db` (in the data directory:
  the queue, staged drafts, usage, pins and eval results) and wakes `RunAsync`, which the runtime runs in the background;
  leftovers from an earlier run go first. For each: the generator (the host's default generator wrapped at background
  priority on the device scheduler) drafts a SKILL.md from the turn's events (request, calls, results cut to 400
  characters, answer); the draft must parse, pass the scanner, and not name a skill the agent does not own; then it must
  pass its replay test; then `skill_write_safe` is asked at the pre-execute checkpoint in the source session with the
  `agent_authored` condition and the draft and replay in its instructions. A calibrated answer above 0.85 commits it as the
  agent's; anything else stages it (`/skills pending`), so without Kev every draft is staged. A turn that was tainted when it
  answered (§10.3) queues its opportunity marked tainted, and its draft is staged whatever the gate says: a skill written
  from outside content would otherwise load as trusted. A turn taken back with `/undo` or `/rollback` (C-014) before its
opportunity is processed is not distilled: the opportunity fails with that reason. `/skills approve` commits a
  staged draft as `agent` (ownership checked again: a skill the user wrote meanwhile refuses it), runs its evals and records
  the decision's outcome `approved`; `/skills reject` records `rejected`. Interactive sessions only: unattended job turns
  are never distilled.
- Replay test (`SkillReplayer`): the source session's events before the turn are copied into a fresh SQLite log under the
  temporary directory (as C-009's `ReplaySession` does; removed afterwards), and the turn is run again by a real
  `TurnRunner` with the draft pinned, not interactive and without escalation, over a degraded-decider fabric on that log.
  Every registered tool is replaced by a read-only stand-in with the same name and schema that returns the result recorded
  for that tool's next call, so a replay changes nothing, asks nobody and touches no network; a call the source never made
  fails. It passes when the successful calls are the source's, in order, with no failed call, and the turn answered.
- Evals: `evals/*.json` in a skill folder, each `{"prompt": ..., "expected_tools": [...]}` (unknown fields refused). The
  curator (`SkillCurator.RunEvalsAsync`) runs each case on an empty log with stand-ins returning a placeholder, and a case
  passes when the successful calls begin with the expected tools and the turn answered; results go to `skills.db` and
  `/skills list`. They run after the learning loop applies or a draft is approved, and on `/skills evals <name>`.
- Curator: `StaleAsync`/`ArchiveStaleAsync` find agent-owned, unpinned skills unused for the idle limit (or, never used,
  unchanged since their last commit) and move them to `.archive/` as the agent (`/skills stale [days] [--archive]`, default
  90 days). Pinned skills (`/skills pin`) and anything the user or a hub wrote are never touched.
- Supply chain (§10.6, `SkillScanner`, `ImportAsync`): an import copies the skills under a folder (one skill or a tree such as
  `~/.hermes/skills`, or `$HERMES_HOME/skills`; D-9) as `hub:<source>` in one commit. Each skill is all or nothing: a link,
  a special file, a `.git` entry or git attributes, an unreadable SKILL.md, a name already in use, more than 5 MB or 500
  files, or a scanner finding refuses it and the rest import. Hidden files are not copied and nothing runs. The scanner
  flags instruction overrides, hiding things from the user, downloads piped to a shell, credential-store and Vault paths,
  environment dumps, reverse shells, recursive deletes of home or root, encoded execution and invisible characters, over
  SKILL.md and every text file; a file over 4 MiB, or binary content in a text or script file, is itself a finding, so
  nothing imports unread (other binary files, such as images, are skipped); drafts are scanned too. It is a screen: hub skills are tainted whatever it says.
- Tools (`SkillFileTools`, C-005 generated): `skill_read` (read-only; a skill's body or one file) and `skill_run_script`
  (`[SideEffect(Process | Filesystem)]`, `[Destructive]`): a `.sh` (bash) or `.py` (python3) file under the skill's
  `scripts/`, run by C-012's `SandboxedShell` (workspace writable, no network, Vault and Corvid's state hidden) with its
  path and arguments single-quoted, and only when the call's context carries an allowed `ApprovalResult`; as a side effect
  the turn loop always asks first, and without Kev nothing runs unasked. Both mark a hub skill's result as tainted.
- Host: `Corvid:Skills` (`Enabled`, `Directory`, `Learning`, `Git`). The runtime opens the store (skills are off, with the
  reason in `/status`, when git is missing or the repository is refused), registers the skill tools, builds the replayer on
  `ModelComposer.CreateBackgroundSlots` (the default generator and embedder at background priority, with a degraded
  decider on them), the curator and the learner, and runs the learner in the background. `/skills` (list, show, pending,
  approve, reject, log, diff, revert, import `<folder> [source]` or `hermes`, evals, pin, unpin, stale); `/skills revert`
  reverts a commit as the user, so the files it touched become the user's. `corvid doctor` checks that git runs.
- Tests: `tests/Corvid.Skills.Tests` (frontmatter subset and refusals, Hermes and Claude Code files, activation rules, the
  scanner; real git repositories under TMPDIR: discovery, outside edits recorded as the user's, agent writes refused on user
  and hub skills, history, diff and revert, hooks in the repository and a global configuration never running, a repository
  configuration that could run code refused, resource confinement; imports from a Hermes tree and refusals; the learning
  loop staged without Kev, approved, rejected, applied under a calibrated yes, a replay that diverges, malformed and flagged
  drafts, an approval after a user edit, a turn undone before its draft; evals and the curator; the turn loop's `skill` question, L0, L1, `none`, pinned
  skills, hub taint forcing approval, `skill_opportunity`; the skill tools). Host and CLI tests cover `/skills` and the
  doctor check, and point the skills directory at temporary folders; a composition test checks that checkpoints leave the
skills directory out.

Not built yet: curator merge proposals and running evals on model swaps; dropping a skill body once it has completed
(§8 lever 2) and showing a cloud generator the top-10 index; drafting on the escalated generator (drafts use the default
local generator); a stale scan on a schedule (the curator runs on `/skills stale`); refreshing the snapshot when files
change on disk between `/skills` calls; the `corvid skills` verb; installing from hubs over the network and plugins in a
collectible `AssemblyLoadContext` (§10.6); importing Hermes memories and sessions (D-9); approvals that show a script's
content (the prompt shows the tool call); toolsets (C-005), which would replace the Hermes map; using an existing clone as
the skills directory (its `remote.*` and `branch.*` keys are outside the configuration allowlist, so it is refused with the
key named). Known limits: a hub skill's description (L0, up to 1,024 characters, scanned at import) reaches the `skill`
options and the L0 lines without tainting the session; only loading its body or files taints. The drafting prompt reads at
most 4,096 events of the source turn.
## Cloud slots and privacy egress (C-015)

Plan §4.1, §4.2 step 6, §6 (escalation targets), §8 (prompt caching), §10.4, §10.5, `vault.md` §9 and §11, decision D-8.
Everything was verified against fake HTTP handlers and a stand-in Vault on a Unix socket: no network and no real keys.

- Privacy egress (`Corvid.Models/Privacy`, core, `IsAotCompatible`). `PrivacyEgress` is one per process. For a target profile
  it takes an `EgressPolicy`: action `Send`, `Redact` or `Refuse`; labels person, email, phone number, address, account
  number and credential, plus user-defined ones; a confidence threshold (0.4); and `Tainted`, the least it does for a tainted
  request (`Redact` by default), which only tightens. The default gates every `cloud` profile with `Redact` and nothing
  else. Before a request it scans every string the request carries (text, tool-call arguments and tool results at their
  JSON leaves, numbers included) with the `IExtractor`, in chunks of 1,200 characters with 100 of overlap, cut at whitespace;
  a chunk the extractor refuses as too long (GLiNER2 takes 512 tokens with the labels) is halved down to 48 characters.
  Results are cached by chunk digest, so a growing conversation is scanned only where it changed. `Refuse` with PII refuses.
  `Redact` replaces each value, longest first, with a stable per-session placeholder (`<PERSON_1>`, `<ACCOUNT_NUMBER_1>`) from
  a `RedactionMap`; values seen earlier in the session are replaced even when the extractor misses them this time, so the
  prompt prefix stays stable for caching. Placeholder-shaped text already in the content (a page quoting `<EMAIL_1>`) is
  rewritten to `‹EMAIL_1›`, so outside content cannot name a placeholder and have a real value restored in its place. JSON
  object keys are scanned and redacted like values. It fails closed: no extractor, an extractor exception, or content it
  cannot read under `Redact` or `Refuse` refuses the request. Unreadable means images and files, and also any tool argument
  or result that is not a string, number, boolean or JSON (an adapter would serialize such an object, which the scan cannot
  see). A request marked local-only (below) is refused by every cloud model, whatever its policy. Under `Send` the scan still runs and the same extractor failures refuse, because without it the gate
  cannot apply the taint rule. Maps live in memory, for at most 256 sessions (the least recently used is dropped).
- `PrivacyEgressChatClient` wraps each gated binding. It reads the session and taint from `ChatOptions.AdditionalProperties`
  (`corvid.session_id`, `corvid.tainted`, which `TurnRunner` sets on every generator call) or the ambient
  `ModelRequestContext`, strips every `corvid.` key, moves `ChatOptions.Instructions` into a leading system message so it is
  scanned too, prepares the request, throws `PrivacyEgressRefusedException` before
  anything is sent, and maps placeholders back in the response's text and tool-call arguments. A redacted request is not
  streamed: it is answered whole, restored, then replayed as updates (a placeholder could split across deltas, and Vault M1
  buffers bodies anyway). A `corvid.egress` span records the outcome and label counts, never values.
- Refuse and stay local: `SlotChatClient` catches the refusal and calls `SessionModelSlots.RetreatAsync`, which moves the slot
  to the first listed binding that is not cloud (local first: default, fallbacks, escalation), appends `ModelSwitched` with
  the new reason `privacy`, and retries there. Like a fallback it does not wait for tool calls and keeps a pin, so a pinned
  cloud slot stays local until the user switches back. With no local binding the refusal reaches the turn. Within one
  request, fallback and retreat share one list of bindings already tried, so a failing local server and a refusing cloud
  fallback end the request instead of handing it back and forth. The turn result
  lists the retreat among its escalations (`<model>: privacy`), and the CLI's footer says the turn was kept local.
- Switches: `ProjectionPrivacyGate` (Kernel) is C-006's `IPrivacyGate`. Before a move to a gated model it renders the
  projection at the switch's head and asks `PrivacyEgress.AssessAsync` with the session's taint. It refuses a move that every
  request would refuse (policy `Refuse` and the conversation holds PII, or no extractor), so `EscalateAsync` passes over that
  target and the chain local → local-server → cloud honours the policy; `/model` reports the refusal. The host gives it to
  every session's and every scheduled run's slots, in place of `DenyCloud`.
- Memory: core blocks are local-only by default, and memory schema version 2 marks every existing block local-only (nothing
  could reach a cloud model before, so none was shared on purpose); `CoreBlockStore.SetLocalOnly` shares one. Facts gain
  `local_only` (`FactInput.LocalOnly`, `MemoryWriteRequest.LocalOnly`, carried on staged proposals; `FactGraph.MarkLocalOnly`;
  a superseding fact inherits it; a trigger lets it change from 0 to 1 only). `RetrievedItem.LocalOnly` lets the turn loop leave
  local-only facts out of a cloud model's recalled memory. Local and local-server models see everything (a local server's
  endpoint must be the user's own, below). Retrieved memory that remains still passes the gate. A prompt rendered for a
  local or local-server model that holds local-only blocks or facts carries `corvid.local_only`, so when C-006's fallback
  chain hands that same request to a cloud model, the gate refuses it rather than send local-only memory, which is not PII
  the scan would find.
- Requests without the turn's options: `TurnRunner` makes its session and taint the ambient `ModelRequestContext` around
  fabric checkpoints, the memory write and compaction (C-016's summarizer may be a cloud binding), the learning loop (C-017)
  does the same with the source turn's session and taint around its draft and replay, and the scheduler marks a job's
  `wake` question tainted (script output is outside content), so a cloud generator answering degraded-decider questions,
  summarizing or drafting is gated with the right taint and Vault is told. `ModelRequestContext.Resolve` makes an ambient
  context's taint and local-only mark a floor under explicit request keys, so a replay's clean session cannot lower them.
- Prompt caching (§8): for a profile with `Breakpoints` caching the turn loop keeps the system prompt frozen (core blocks only)
  and sends recalled memory and the turn's skill note (C-017's L0 lines or L1 body) as a one-shot user note after the history. `AnthropicCacheBreakpoints` marks the system prompt
  and the last content of each of the last three messages with `cache_control` (Hermes' layout: four breakpoints, the API's
  maximum; the system breakpoint covers the tool definitions), on copies, through the SDK's `WithCacheControl`.
- Vault-only keys (`Corvid.Models/Remote`, `Corvid.Vault.Client`). `ModelHttp.Create` builds a remote binding's pipeline: a
  handler that tells Vault the taint and the toolset `model-provider`; `VaultSecretHandler`, which removes every credential
  header the SDK set and puts a placeholder minted by `VaultPlaceholders` (one Vault session per process, renewed a minute
  before expiry, minted again and retried once on `bad_placeholder`) into the provider's header; `VaultHandler`, which sends
  it through Vault's egress proxy; and below that, for a cloud model, a handler that refuses, so a request without a
  placeholder can never leave unproxied. A cloud model without a secret, or without Vault, cannot be built. A keyless local
  server is reached directly (no proxy, no redirects) with every credential header removed. The SDKs are given only a
  sentinel key, and the Anthropic client's environment and profile credential resolution is switched off.
  `RemoteChatClient` strips Corvid's keys, makes the context ambient for the handlers, and translates failures so C-006's
  fallback chain works: API errors keep their status (429 and 5xx fall back, 4xx does not), an unreachable Vault or server
  falls back, and Vault's `rate_limited` and `budget_exceeded` become 429, `upstream_error` 502 and its other refusals 403.
  SDK retries are off; the chain decides. Vault's default `tainted` rule is `ask`, so a provider key without
  `"tainted": "auto"` in `vault.json` is refused for a tainted turn (`approval_required`, a 403): tainted sessions stay off
  that key unless the user allows it, which is Vault becoming stricter, as `vault.md` §6 intends.
- `Corvid.Models.Anthropic` (package `Anthropic` 12.58.0, the official SDK, through its `IChatClient`): the native Messages API
  at `https://api.anthropic.com`, key in `x-api-key`. A required tool choice is refused with `NotSupportedException` before
  sending unless the model is configured to accept it (current models return 400), so the turn loop asks in words.
- `Corvid.Models.OpenAICompat` (packages `OpenAI` 2.14.0 and `Microsoft.Extensions.AI.OpenAI` 10.10.1): Chat Completions for
  local servers (llama.cpp, Ollama, vLLM, LM Studio), OpenAI (`https://api.openai.com/v1`), OpenRouter
  (`https://openrouter.ai/api/v1`), Gemini through Google's OpenAI-compatible endpoint
  (`https://generativelanguage.googleapis.com/v1beta/openai/`) and any other hosted compatible API; the key is a bearer token.
- Host (`RemoteModelProvider`, `Corvid:Models:Remote`): each entry names a provider (`anthropic`, `openai`, `openrouter`,
  `gemini`, `openai-compatible`), the model id, the endpoint (required for `openai-compatible`), the Vault secret (required
  for cloud), privacy (only for `openai-compatible`: `LocalServer`, the default, or `Cloud`), chat slots (generator and
  summarizer by default), context window and accepted context, tool-call and structured-output support, caching (breakpoints
  for Anthropic, automatic otherwise), costs per million tokens, forced tool choice, and its own `Egress` over
  `Corvid:Privacy` (`Cloud`, `LocalServer`, `Tainted`, `Labels`, `ExtraLabels`, `Threshold`). Profiles are
  `<name>@<provider>` with the endpoint's authority as placement. Refused at validation: a cloud model without a secret, an
  `http` endpoint with a secret (Vault sends keys only over verified TLS), and a `LocalServer` endpoint that is not loopback,
  private, link-local or CGNAT, a single-label name or a `.local`, `.lan`, `.internal` or `.home.arpa` name (local servers
  are not gated by default, so the class must be true). `Check` asks Vault over its local socket: no socket, no answer, no
  such secret, or a secret not bound to the endpoint's host makes the model `Unavailable` (a new readiness) with the
  `corvid-vault set` step; nothing is downloaded. At start an unavailable model that is some slot's default stops the
  runtime; an unavailable escalation or fallback target is left out and listed by `/status`. `ModelComposer` wraps every
  gated chat binding in the egress gate beneath the span layer (each cloud binding; a local server only when
  `Corvid:Privacy:LocalServer` or the model's `Egress` says so) and every other one in `ModelRequestScopeChatClient`, which
  strips the keys; the gate's extractor is the configured `extractor` model. `/status` shows the cloud policy and warns when
  no extractor is bound.
- Tests: `tests/Corvid.Models.Tests` (redaction round trip in text, arguments and results; stable placeholders per session;
  refusal; no extractor or a failing one refusing under every action; taint raising send to redact; chunking and halving;
  neutralized placeholders; opaque content and opaque tool results; JSON keys; instructions; local-only requests; streaming;
  the privacy retreat with its event, and a failing local server with a refusing cloud fallback), `tests/Corvid.Kernel.Tests`
  (`EgressTurnTests`: what a cloud generator sees, the request context and the local-only mark, taint on fabric questions,
  escalation passing over a refusing target),
  `tests/Corvid.Memory.Tests` (`LocalOnlyTests`, including the version 1 migration), `tests/Corvid.Models.Remote.Tests`
  (against `FakeVault`: request shape, the placeholder key, no environment key, cache breakpoints, taint and toolset, error
  mapping, re-minting, Vault absent, forced tool choice, the gate in front of the Anthropic client; local servers direct and
  keyless, hosted endpoints through Vault) and `tests/Corvid.Host.Tests` (`RemoteModelTests`: validation, local endpoints,
  unavailable targets skipped or stopping the start, Vault readiness checks, a turn on a composed cloud model through Vault,
  the sources of the egress policy).

Not built yet: a native Gemini adapter (`Google.GenAI` has an `IChatClient`, but it brings `Google.Apis.Auth`, whose
default credentials come from the environment and files, which Corvid does not read; Google's OpenAI-compatible endpoint
gives Gemini an `IChatClient` through the same Vault handler, without Gemini's own context caching); incremental streaming
from cloud models (Vault M1 buffers response bodies; every request under `Redact` is answered whole, even when nothing was
replaced); telling the turn loop up front that a model refuses a required tool choice (the no-op re-prompt's required mode
is refused before sending, which `/status` and its span record as one failed call before the retry in words); a validation message for a
remote model named like a Starling model (the catalogue refuses the duplicate when the host is built); persisting redaction
maps (a restart renumbers placeholders, costing one cache miss); replaying thinking blocks or exposing effort and thinking
settings (the log keeps text and tool calls only); cache hints for Anthropic models behind OpenRouter; images and other
non-text content to a gated model except under `Send`; the system prompt in the switch-time check (requests are checked in
full); `/memory` commands and a tool to mark facts local-only or share a core block (API only today); Vault's
`role = "model-provider"` policy key (requests carry the toolset `model-provider` instead); switch costs for `/insights`;
cloud embedders, extractors and vision; `tool_search` for cloud generators (§7); Bedrock, Vertex and Foundry; spike S3's
recall measurement of GLiNER2 on the PII labels; and a run against the real services with real keys.

## MCP client and server (C-018)

Plan §3.1 (the MCP client capability and the MCP server surface), §7 ("MCP tools arrive as `McpClientTool`"), §10.1, §10.3,
§10.5, §10.6, §12 and §17 row 1.15, against MCP 2026-07-28 (sampling deprecated and not relied on; the tasks extension for long
calls). The code is in `src/Corvid.Mcp`, the only project that references the official SDK (`ModelContextProtocol.Core` and
`ModelContextProtocol.Extensions.Tasks` 2.2.0; 2.3.0 was published the day this was built and is not taken yet). It is a
surface project with its own checker rule (see [Dependency rules](#dependency-rules)), not `IsAotCompatible` (the tasks
extension is not AOT-qualified). The host composes it; `Corvid.Cli` adds `corvid mcp`.

- Definitions (`McpServerDefinition`, from `Corvid:Mcp:Servers:<name>` by `McpComposition.Definitions`, checked by
  `CorvidOptionsValidator` with everything else): a name (lower-case letters, digits, `-`, `_`, at most 24) that prefixes the
  server's tools; transport `stdio` or `http` (default `http` when `Url` is set); trust `Untrusted` (the default) or
  `Trusted`; for stdio a command, arguments, plain environment settings (invalid or secret-like names refused, the sandbox's
  own rule), `Sandboxed` (default true), `Network` (default false), a workspace and read-only paths (absolute, `~/` expanded);
  for HTTP a URL (no user info) and an optional Vault `Secret` (then https is required: Vault sends credentials only over
  verified TLS); `Tools` (only these, by the server's names), `ReadOnlyTools` (the user vouches for them), start, call and task
  timeouts (30 s, 60 s, 10 min), `MaxResultChars` (16,000) and `MaxTools` (64). `Enabled: false` skips a server unchecked.
- Transports (`McpTransports`): a sandboxed stdio server is started through `BubblewrapSandbox.Prepare` (new in
  `Corvid.Sandbox`: the launcher's command line and complete environment for a long-lived process whose streams the caller
  owns, with the same policy checks as `RunAsync`, refusing a missing launcher): the server's workspace (by default
  `$XDG_STATE_HOME/corvid-mcp/<name>`, outside the data directory, which is denied) read-write, everything else read-only, the
  configured read-only paths, Vault's configured locations and Corvid's state (the data directory, the configuration and rules
  files, the launcher; `McpDeniedPaths`, as for the shell) denied on top of the launcher's own denials, no network unless
  `Network`, and the default environment plus the server's own settings, nothing inherited. Off Linux a sandboxed server does
  not start. `Sandboxed: false` runs the command directly in its workspace (created if missing) with the SDK's minimal
  environment and the server's settings, and `corvid doctor` warns about it. An HTTP server uses streamable HTTP only (no SSE fallback, no standalone GET stream, no
  reconnection): with a secret, through `VaultSecretHandler` (a placeholder in `Authorization: Bearer`) and `VaultHandler` (Vault's
  egress proxy), above a handler that refuses, so no request leaves unproxied, and without Vault the server does not start;
  without one, directly (no proxy, no redirects) with credential headers removed. Every request tells Vault the toolset
  `mcp:<name>` and whether the calling session is tainted (`McpRequestScope`, an async-local set around each call; connecting
  and listing are untainted), so a secret's taint rule applies. No token is ever in configuration or in Corvid.
- Connections (`McpServerConnection`): connected on first use under a gate and kept; the handshake declares only the tasks
  extension (no sampling, elicitation or roots, so nothing a server asks reaches the user or a model except through a tool
  result). Listing is bounded by the start timeout; a call by the call timeout; a failure records the reason with the last 20
  lines of standard error (each cut to 300 characters) and makes later calls fail at once for 30 s (`RetryDelay`) before the
  next attempt (a start cancelled with its turn or the host is not a failure: the next call starts the server again); a server
  that exited or dropped its connection is started again for the next call. `McpClients.Start` runs every
  server's first start in the background (`Started` completes when each is ready or failed, never faulting) and registers
  each server's tools as soon as it has listed them; the runtime starts it after the built-in and DI tools, so they keep their
  names, and stops every server after the sessions on dispose. A server that never answers costs only its own tools; turns read
  the registry, so tools appear in the next turn after their server lists them.
- Tasks: when the server's capabilities include the tasks extension, a call is made with `CallToolAsTaskAsync`; a created
  task is polled with `tasks/get` at the server's interval (clamped to 0.1–5 s) until it completes, fails or is cancelled, within
  `TaskTimeout`. A turn that is cancelled or a task that times out cancels the task on the server; a task that asks for input
  is cancelled too, since Corvid gives servers no input. Without the extension a call is an ordinary `tools/call`.
- Tools (`McpTool`, `McpToolMapping`): each listed tool becomes a C-005 `ToolFunction` named `server__tool` (characters outside
  `[A-Za-z0-9_-]` replaced; a name over 64 characters cut with a hash of the full name), with the server's input schema (object
  schemas only, at most 16 KiB) and its description (bidirectional, zero-width and control characters removed, at most 1,024
  characters). So MCP tools are embedded in the shortlist by name and description, offered, validated against their schema and
  repaired like built-in ones. A tool listed twice, refused, over `MaxTools` or named like an existing tool is left out and
  reported in `/mcp`. The result is `{server, content, structured?, truncated?, tainted?}`: text blocks joined, images, audio
  and binary resources described by type and size, structured content only when there is no text, cut at `MaxResultChars`. An
  error result is thrown as `McpToolException`, so the turn logs an error result with the server's text.
- Risk and taint (§10.1, §10.3, §10.6): an untrusted server's annotations are ignored (a claim of read-only would let a tool run
  unasked), so each tool is `SideEffects.Remote` and destructive, asks on every call unless a remembered approval rule covers it,
  and its result or error taints the session (`ToolCallContext.MarkResultTainted`, set before the call). A trusted server's
  `readOnlyHint`, `destructiveHint` and `openWorldHint` are honoured, with MCP's defaults when a hint is absent (not read-only,
  destructive, open world). `ReadOnlyTools` makes a tool read-only whatever the server says; an untrusted server's results
  still taint. A tool is `[Network]` when what it is sent can leave the machine (an HTTP server, or a stdio one with network or
  unconfined; for a trusted server only when open world), so in a tainted session even a read-only one asks, and its result
  taints. Calls made outside a turn tell Vault the session is tainted.
- Server mode (`CorvidMcpServer`, `corvid mcp serve`): an MCP server on standard input and output for another agent (§12),
  opened without loading any model. It accepts read-only tools only (a tool with any side effect is refused at construction),
  answers `tools/list` and `tools/call` with read-only, non-destructive annotations, runs each call as unattended and tainted
  (an agent's request is outside content, so anything that would ask refuses), returns a tool's argument errors as error results,
  and cuts results at 32,000 characters. The tools: `search_memory` (`SharedMemoryTools` in `Corvid.Tools.Builtin`: hybrid
  retrieval over FTS and the fact graph, never local-only facts and without a count of them, earlier conversations only with
  `Corvid:Mcp:Serve:Conversations`), and `skill_list` (`SkillCatalogTools` in `Corvid.Skills`: name, description and whether the
  skill is trusted) with `skill_read`. `Corvid:Mcp:Serve:Memory` and `:Skills` turn each part off. Standard output is the
  protocol; notes and errors go to standard error.
- Host and CLI: `CorvidRuntime.Mcp`; `/mcp` lists each server's transport (and whether it offers tasks), trust, state, tools and
  detail, with what was left out, warning when one failed; `corvid mcp [list]` starts each configured server into a registry of
  its own, prints the same with each tool's risk, and exits 1 when a server is not ready; `corvid mcp serve`; `corvid doctor`
  reports each server without starting it (an unconfined one, or a sandboxed one off Linux, is a warning; a secret names the
  `corvid-vault set` step). A missing Vault runtime directory makes only the servers with a secret fail.
- Tests: `tests/Corvid.Mcp.Tests`, against an in-process SDK server on pipes (`FakeMcpServer`, shared with the host tests):
  prefixed tools with the server's schema, untrusted annotations ignored, taint from untrusted results and errors and none from
  a trusted read-only tool, cut results and described non-text content, a hanging call timing out, a server that never answers
  not stalling the start, a failed server failing fast until its retry time, a start cancelled with its turn not counted as a
  failure, a server that exits started again, filtering,
  limits and name clashes, name sanitizing, description cleaning, definition validation; tasks (`McpTaskTests`, the SDK's task
  store on a hosted server: a long call polled to its result, a task over its timeout cancelled on the server); HTTP
  (`McpHttpTests`, with C-015's stand-in Vault: every request through Vault with the placeholder, the toolset and the caller's
  taint, none direct; no Vault, no start; a keyless server reached directly without credentials); server mode (side-effecting
  tools refused, memory search over the protocol without local-only facts, argument errors as error results, and the real
  `corvid mcp serve` as a stdio server for Corvid's own client, unconfined (also in a workspace that does not exist yet) and
  under the launcher, which CI requires to run).
  `tests/Corvid.Host.Tests` (`McpHostTests`: validation, denied paths and private workspaces, a runtime whose server cannot start
  answering turns at once with `/mcp` and doctor reporting it, and real turns where an untrusted tool asks and taints while a
  trusted read-only one runs unasked), `tests/Corvid.Cli.Tests` (`corvid mcp` with nothing configured, a failing server and a
  usage error), `tests/Corvid.Sandbox.Tests` (`Prepare`) and the checker's tests (the surface rule).

Not built yet: OAuth authorization flows (a token is a static bearer secret in Vault); writes from server mode through the gated
paths (a staged memory proposal needs the extractor and the event log in the serve process, so server mode is read-only only);
resources, prompts and completions in either direction; `notifications/tools/list_changed` (a server's first listing is kept
until Corvid restarts) and retrying a server that failed its first start without a restart (`corvid mcp list` diagnoses it);
example requests for MCP tools in the shortlist; Vault's exec path for stdio servers that need a secret (a secret-like
environment name is refused instead); address checks for keyless HTTP servers (the URL is the user's own configuration); the
privacy egress gate over MCP arguments (an untrusted server's calls are each approved with their arguments shown, and a
trusted read-only tool's are not); tainting a turn for an untrusted server's tool descriptions, which reach the prompt cleaned and
capped but untainted, like a hub skill's L0 description; stdio servers where the sandbox does not run; server mode over HTTP
(C-019's endpoint); and SDK 2.3.0.

## ACP and OpenAI-compatible HTTP (C-019)

Plan §12 (ACP; OpenAI-compatible HTTP), §17 row 1.1, R11 (agentclientprotocol.com). Two front ends on the composition root,
`src/Corvid.Acp` (`corvid acp`) and `src/Corvid.Http` (`corvid serve`). Both open ordinary Corvid sessions through
`CorvidRuntime`, so turns, tools, approvals, taint, the sandbox, checkpoints and privacy egress are the ones `corvid chat` uses.

### ACP over stdio (`corvid acp`)

- No maintained .NET ACP library: the protocol's organisation publishes SDKs for other languages, and the NuGet packages
  (`AgentClientProtocol`, `LibAcp`, `AcpSdk` and others) are young community ports, one of them on StreamJsonRpc. A front end
  takes no package of its own, so `Corvid.Acp` implements JSON-RPC 2.0 directly: newline-delimited messages (16 MB cap),
  System.Text.Json source generation (`AcpJson`), requests handled concurrently (a prompt runs for minutes while its cancel
  arrives), every outgoing message through one ordered writer, agent-to-client requests (`session/request_permission`) with
  their own ids, and `$/cancel_request`. Message shapes follow schema version 1 (`schema.json`); `session/list`,
  `session/resume`, `session/fork` and `session/close` come from `schema.unstable.json` and are advertised under
  `sessionCapabilities`.
- Standard output carries protocol messages only: under `acp` the CLI's own console writes to standard error. The runtime
  starts in the background so `initialize` answers at once; a session request waits for it, and missing models fail that
  request with the `corvid model download` remedy instead of ending the process.
- `initialize`: protocol version 1, `loadSession`, text and resource links only (no image, audio or embedded context), no MCP
  transports, no authentication methods (the agent runs as the user who started it).
- Sessions: `session/new` (surface `acp`, ids `acp-…`); `session/load` (replays the projected transcript, compactions
  expanded and rewound turns left out, as user and agent message chunks and tool calls with their results); `session/resume`
  (no replay); `session/fork` (`CorvidRuntime.ForkSessionAsync`, below; a position comes in `_meta.corvid.throughSequence`,
  the head otherwise); `session/list` (the user's `acp-` and `cli-` conversations, newest first, titled by their first
  message, paged by an offset cursor; scheduled runs and HTTP sessions are left out); `session/close`. Each opened session is
  sent the slash commands as `available_commands_update` (all but `/exit`), and a prompt that starts with `/` runs the shared
  registry.
- The workspace is fixed when the runtime starts (approvals, the sandbox, checkpoints and the hardline list protect it), so a
  session whose `cwd` is another directory is refused with the remedy, never redirected. A client's MCP servers are logged to
  standard error and not connected: the servers Corvid connects are the ones in its own `Corvid:Mcp:Servers` (C-018), whose
  tools sessions here are offered under the same approvals and taint as in `corvid chat`.
- A prompt is one turn (`TurnReporter`, an `ITurnObserver` on the runtime's feed): streamed text as `agent_message_chunk`;
  each call as `tool_call` (kind from the tool's risk metadata: execute, search, fetch, delete, edit, read or other; a title
  with its command, path or URL; locations only for paths the file tools may open) and `tool_call_update` with its input,
  result and, for a call that changed files, a `diff` per path from its write-ledger checkpoints (C-014). Notifications go
  out on one ordered chain, drained before the prompt's response; answer text that was not streamed is sent last. Stop
  reasons: `end_turn`, `cancelled`, `max_turn_requests` (step budget). One prompt at a time per session.
- Approvals (`PermissionPrompter`, attached with `AttachPrompter`): a C-007b prompt becomes `session/request_permission`
  naming the call (`ApprovalPrompt.CallId`, added for this), with the pipeline's reasons, its deadline and, for `write_file`
  and `patch_file`, the proposed diff (the current text read only through the file tools' `Workspace` rules, so a refused
  path is never read). The options are the scopes the pipeline offers (allow once, for this session, always) and deny. A
  cancelled request, an unknown or unoffered option, a client error, the deadline and a prompt outside a running prompt all
  deny; nothing is approved on the user's behalf.
- Cancellation: `session/cancel` (and `$/cancel_request` on the prompt) cancels the turn and its open permission request; the
  prompt still answers, with `cancelled`. The kernel turn is cancelled first, so a permission request that ends as cancelled
  resumes a turn that already sees the cancel and makes no further model call. A prompt that received a cancel answers
  `cancelled` even if its turn reached its end first (ACP requires it); a cancel between prompts does not touch the next one.

### Forking at a log position

`SessionFork` (Kernel, no package) copies a source session's events through a sequence into a new, empty session, in order
and at the same sequences, so positions inside payloads (a compaction's range, a rewind's position) keep their meaning; event
ids are new. A position that leaves a tool call or an approval open is refused, as is a target that has events; the source
never changes. `CorvidRuntime.ForkSessionAsync` takes the copy between turns (`RunExclusiveAsync`) when the source is open in
this process.

### OpenAI-compatible HTTP (`corvid serve`)

- ASP.NET Core minimal APIs on a slim builder (no configuration files, no `ASPNETCORE_URLS`, no console logging, no server
  header), Kestrel on `Corvid:Serve:Url` (default `http://127.0.0.1:8411`), JSON through a source-generated context.
- `GET /v1/models` lists `corvid`. `POST /v1/chat/completions` runs one turn per request and answers as a `chat.completion`
  or, with `stream: true`, as server-sent `chat.completion.chunk` events ending in `data: [DONE]`; a failure after the headers
  travels as an error event. `finish_reason` is `stop`, or `length` when the step budget ran out.
- Conversations onto sessions: a client that resends the whole history keeps one session. After each answer the endpoint
  remembers a SHA-256 digest of the user and assistant messages through that answer (in memory, 4,096 conversations); a
  request whose history matches continues that session with only its last message. Otherwise a new `http-` session is seeded
  with the client's history (`CorvidSession.SeedHistory`) and the last message is sent. `X-Corvid-Session` names the session
  in every response and may name one in a request, but only a session this endpoint started (an `http-` id in the log): a
  terminal or editor session has a user who answers its approvals. A client's system or developer messages reach the model as
  quoted instructions in the user's message, never as Corvid's system prompt. The history a client sends is the
  authenticated user's own conversation, so it is not tainted.
- Refused with OpenAI-shaped errors: client-defined `tools`, `functions`, `tool_choice` and `tool` messages (Corvid runs its
  own tools under its own approvals), `n` > 1, non-text content parts, a model other than `corvid`, and bodies that are not
  `application/json`.
- Who may call; tools are never exposed to an unauthenticated remote caller. Without `Corvid:Serve:TokenSecret` the
  validator accepts only a loopback address and the endpoint serves only loopback peers. A loopback endpoint also refuses a
  `Host` that is not a loopback name (421, against DNS rebinding), and the JSON-only rule means a web page cannot post to it
  without a CORS preflight, which is never answered. With a token secret every request needs `Authorization: Bearer`, and any
  IP address (or `localhost`) may be configured; TLS is a reverse proxy's job.
- The token is a Vault secret; Corvid never holds its value (§10.5). Vault has no compare operation, so `VaultTokenVerifier`
  sends the presented token to Vault's scrub service: it is the secret exactly when the whole token comes back as the one
  marker `[secret:<name>]`. Tokens under 24 bytes (the scrubber also matches a secret's first 12) or holding `[` or `]` are
  refused before Vault is asked; an encoding the scrubber recognises (hex, base64) is accepted, which is the same knowledge.
  Accepted tokens are remembered for five minutes by digest, and more than 20 failures a minute stop reaching Vault, so a
  flood cannot spend Vault's per-user scrub budget (it does refuse new clients for that minute). `corvid serve` checks that
  Vault holds the secret before it listens; a request while Vault is down gets 503.
- Approvals cannot be answered over HTTP, so served sessions are not interactive (`SessionSettings(Interactive: false)`):
  an ask is denied without a prompt. Under `Corvid:Serve:Approvals=Fail` (the default) the first denied call cancels the
  turn and the request fails with 403 `approval_required`, naming the tool and `corvid chat --session <id>` to continue where
  it can be approved; under `Deny` the model answers without the call, as in an unattended job. `Corvid:Serve:Tools=false`
  offers the model no tools at all. Hardline denials, the sandbox and privacy egress apply as everywhere.
- A client that disconnects cancels its turn; one request at a time per session. `SessionGates` keeps a session's gate only
  while a request holds or waits for it (a count under one lock), so the endpoint holds no state for idle sessions.

### Dependency rules for the front ends

`FrontEndProjects` in the checker: `Corvid.Acp` and `Corvid.Http` may reference `Corvid.Host` (their packages are judged as
the composition root's, including what C-018's `Corvid.Mcp` brings through it), only `Corvid.Cli` may reference a front end,
and a front end never references the CLI. They differ from C-018's SDK surfaces (`SurfacePackages`), which sit under the host
and may not reference it; an SDK surface may not reference a front end either. Neither takes a
NuGet package. ASP.NET Core is a shared-framework reference, which the package rules never see, so the checker now also reads
framework references (a project's own and those its references bring) against `FrameworkReferences`:
`Microsoft.AspNetCore.App` only in `Corvid.Http` and in the CLI, which inherits it; any other shared framework anywhere fails.
The core rule is unchanged.

Verified with recorded ACP exchanges (`tests/Corvid.Acp.Tests/Exchanges/*.jsonl`, with placeholders for ids and paths)
driving the server over in-memory streams on the host's fake models, plus scenario tests (permission with diffs, an edit of
an existing file, cancellation, load and resume, fork, list, a refused workspace, malformed requests, missing models); HTTP
tests against Kestrel on a loopback port (streaming and not, session mapping, refusals, approvals under Fail and Deny, tools
off); and the token verifier against a stand-in Vault scrubber.

Deferred: ACP `authenticate`, `logout`, `session/set_mode`, `session/set_config_option` and `session/delete`; image, audio
and embedded-context prompt blocks (resource links reach the model as Markdown links and are not fetched); the client's MCP
servers (only Corvid's configured ones are connected); MCP server mode over HTTP (C-018 left it for this endpoint; it stays
stdio only); the client's file-system and terminal capabilities (Corvid's own tools and sandbox are used); `plan` updates
from todos; a workspace per session; HTTP approvals surfaced to another channel (C-020); client-defined tools;
`/v1/completions`, `/v1/embeddings` and usage numbers; conversation mapping that survives a restart (a known history then
seeds a new session); HTTPS in Corvid; a Vault compare operation in place of the scrub check.

## Gateway (C-020)

Plan §12 (the gateway), §5.3 (`deliver`), §10.3 (other users' messages are tainted), §10.5 (tokens only through Vault),
§13 (job results) and §17 row 1.4. Everything was verified with fake adapters, fake platforms behind a stand-in Vault on a
Unix socket and the real runtime over fake models: no network and no real tokens.

- Projects. `Corvid.Gateway` (core, `IsAotCompatible`) references Abstractions, Approvals (`IApprovalPrompter`) and
  `Corvid.Vault.Client`, and `Microsoft.Data.Sqlite` (already allowed). `Corvid.Gateway.Telegram`, `.Discord` and `.Slack`
  reference only `Corvid.Gateway`: each speaks its platform over plain HTTP (`System.Net.Http`, `System.Text.Json.Nodes`)
  and, for Slack, `ClientWebSocket`, all in the shared framework. No messaging SDK is used, so the adapters are core
  libraries under the checker and need no rule of their own. The maintained SDKs hold the token and open their own
  connections, which would bypass Vault. Only `Corvid.Host` references them, and the gateway reaches the runtime through
  `IGatewayAgent`, so it never references the host.
- `GatewayHost`: one host for every enabled adapter. An adapter is a `GatewayAdapterRegistration` (name and factory).
  Nothing is created until `StartAsync`. Each adapter then gets a `Supervisor` with its own instance, linked cancellation,
  connection task and delivery task. The factory and `ConnectAsync` run off the loop under `ConnectTimeout` (30 s). A
  constructor or connect that hangs, even one that ignores cancellation, is left behind and disposed whenever it ends, and
  the adapter is retried with a doubling backoff (2 s up to 5 min). A receive failure disposes the adapter, creates a fresh
  one and reconnects. Adapter state (`Starting`, `Connecting`, `Connected`, `Retrying`, `Stopped`) and notices go to the
  surface. A lock beside `gateway.db` lets only one gateway run against a data directory. `StopAsync` stops receiving and
  gives answers still being written `StopTimeout` (10 s) to reach the ledger and be sent. Then it denies waiting approvals,
  stops the adapters, each under its own timeout, and saves the ledger's low-water mark.
- Inbound. Unaddressed group messages are dropped. A group message is addressed when it mentions the bot, replies to it or,
  on Telegram, is a command; DMs are always addressed. `PairingStore.RoleOf` decides the sender's role: configuration first
  (`Owners`, then `Guests`), then pairings. An unpaired sender gets the pairing flow and nothing else. Their text is not
  stored and reaches no session or model. A paired sender's message goes to the chat's own session,
  `gateway:{adapter}:{chat}`, opened once per process with the chat's settings and an approval prompter. The owner's message
  is posted as `EnvelopeOrigin.User`. A guest's is posted as `EnvelopeOrigin.External`, which taints the session (C-011)
  until the owner speaks. Messages are queued in order per adapter, and answers are awaited off the receive loop
  (`CorvidRuntime.PostAsync` returns once queued), so a long turn never holds up the next message.
- Guest tools. Taint asks only before network reads and sends, so on its own it would let a guest's turn (a bot set to
  `tainted: auto` so other people get answers) run `read_file`, `list_files` or `search_files` on the owner's workspace
  and see the result. Anyone but the owner therefore posts with a `GuestScope` on the envelope (`Envelope.Guest`, posted by
  `CorvidSession.PostAsGuestAsync`): an allowlist by exact tool name, `Corvid:Gateway:Adapters:<name>:GuestTools`, empty
  by default. `TurnRunner` builds the turn's catalogue from it, so a tool not named is neither offered nor callable (a call
  to it is refused as not offered), and an MCP tool (`server__tool`), a skill's tool or any tool registered later stays
  out unless named. A guest's turn also gets none of the owner's own context: no skills (no shortlist, no body, no
  distillation), no `needs_recall` and no core blocks in the system prompt. Nor does it see the owner's earlier work in
  the chat: `PromptRenderer.Render(spokenOnlyBefore:)` renders the transcript before the guest's message as its user and
  assistant messages only, without tool calls and results, compaction summaries (which restate tool output) or the
  standing goal and todos, so a file the owner's turn read in a group is not there for a guest to ask about; only the
  answers stay, as the chat saw them. (An owner answer sent `silent` or to `home_channel`, or a turn the owner ran on the
  chat's session from another surface, is still in that history as text.) Sessions are per chat, so nothing from the
  owner's DM reaches a group. `cron_create` cannot be a guest tool: a job's run is a turn of its own with the whole
  toolset that reports to the chat. Nor does a guest's turn reach the owner's memory write path: it is not asked
  `remember` at egress, which would stage the guest's message (tainted, so always staged) in the owner's review queue,
  a proposal worded by someone else and waiting to be approved; and a tool that declares `[SideEffect(Memory)]`
  (`memory_add`, `memory_approve`, `memory_reject`) is left out of a guest's catalogue even when `GuestTools` names it,
  and which `CorvidGateway.Create` refuses anyway (below). Two other residuals stay as they are: an approval asked during a guest's turn is
  posted in that chat, where only the owner can answer it, and an owner answer sent `silent` or to `home_channel`
  stays in the chat's history as text (above). The owner's own turns are unchanged and keep
  the whole toolset, including the turn right after a guest's in the same chat: as before, the owner's message clears
  taint (C-011), and a guest's earlier text stays in the transcript as a fetched page would, with the injection and
  approval checks that apply to any outside content. The validator refuses a guest tool that is not a tool name or names
  an MCP server that is not configured, and `CorvidGateway.Create` refuses one that is in neither the runtime's registry
  nor an MCP server's namespace (an MCP server's tools are listed only once it runs), so a typo is not silently a tool
  nobody gets, or whose risk metadata declares `SideEffects.Memory`: by the flag the turn loop filters on, not by name,
  so a memory-writing tool added later is refused at start rather than silently left out of a guest's catalogue.
- Pairing (`PairingStore`, `gateway.db`, its own SQLite file with WAL and full sync). A code is 8 characters from an
  unambiguous alphabet, shown `ABCD-EFGH`. It is bound to the adapter and sender, expires after an hour, and is repeated to
  that sender at most every 10 minutes. At most 10 codes wait per adapter; senders after that are ignored. Codes are
  approved only on the machine: `corvid gateway pair approve CODE --owner|--guest`, with no default, so approving a
  stranger out of habit cannot make them the owner. `pair revoke` removes a pairing. The same file keeps adapters' polling
  cursors and the ledger's mark.
- Commands and approvals. Slash commands come from C-013's `SlashCommandRegistry` (`SlashCommandRegistry.IsCommand`, so a
  pasted path is not a command), run in the chat's session and are sent back as `ToPlainText()`. They run off the receive
  loop, because `/undo` waits for a running turn that may itself be waiting for an `/approve`. Only the owner runs
  commands, which settles C-014's question of who may `/undo` and `/rollback` from a chat; guests are told so. An approval
  the pipeline asks in a gateway session is posted to the chat with its reasons and the scopes on offer. Only the owner
  answers, with `/approve [once|session|always]` or `/deny`, within `ApprovalTimeout` (5 min, capped by the prompt's own
  deadline). No answer denies, as does a second prompt while one waits. The confirmation is queued before the turn
  resumes, so the chat reads it before the answer.
- `deliver` (§5.3). Gateway sessions set `SessionSettings.AskDeliver`, so `deliver` is asked at egress with `remember`.
  `TurnRunner` now acts on it only for a calibrated answer (or when `DegradedTurnBlocking` lets the degraded tier divert
  turns), so without Kev every answer is a reply and the degraded tier never silences one or moves it. `reply` answers the
  message. `reply_in_thread` answers in its thread: a Slack `thread_ts`, a Discord thread started on the message, or a
  Telegram reply. `home_channel` sends to the adapter's `HomeChat`. A tainted turn's answer never goes there, since that
  would be a new recipient (§10.3), and without a home chat the answer is a reply. `silent` sends nothing. A failed turn
  always replies with a short notice and keeps the details in the log.
- Delivery ledger (`DeliveryLedger`). It is a table of the event log, kept as the session `gateway-ledger` so no chat's
  transcript carries it. Two new events: `DeliveryQueued` (`delivery_queued`: id, adapter, chat, text, kind (`reply`,
  `thread`, `home`, `job` or `notice`), thread, reply-to, source session and sequence, taint) is appended before anything
  is sent. `DeliveryResolved` (`delivery_resolved`: `delivered` with the platform's message id, or `failed` with why) is
  appended after. Any process sharing the log may queue (`DeliveryLedger.Queue(log, …)`, retrying on head conflicts). The
  gateway holds only open deliveries, tails the session every 2 s and on every queue, and sends each adapter's open
  deliveries in ledger order. A chat whose earlier message is waiting out a retry keeps its later ones back, so a chat
  never sees answers out of order. Transient failures are retried with a doubling backoff (2 s up to 5 min, or the
  platform's `retry_after`), up to 8 attempts. Permanent ones are given up at once and recorded `failed`: an unknown
  chat, a bot removed, or a Vault policy refusal. A delivery for an adapter this gateway does not run is given up the
  same way. The mark saved in `gateway.db` is the position before the oldest open entry and never past what this
  process has read, so a restart reads only the open end and sends what was queued and never resolved. Delivery is
  at least once: a crash between a send and its resolution sends that message again.
- Scheduled jobs (§13). A job created in a gateway chat records that chat's session as its origin. When a run completes,
  `CorvidRuntime.QueueJobResult` queues its output (or why it did not complete) to that chat, kind `job`, tainted when the
  job was created tainted, ran a script, or its run's turn ended tainted (a page fetched during the run; `JobAgentResult`
  carries the turn's taint and the runtime remembers it by run session until the result is queued). Skipped ticks report nothing; jobs created elsewhere stay local. Because the
  ledger is in the shared log, a result is delivered even when another process (a CLI holding the scheduler lock) ran the
  job. The gateway's own runtime runs the scheduler when it holds the lock, so a gateway is the "scheduler without a chat
  open" C-013 left open.
- Per-chat settings. `Corvid:Gateway:Adapters:<name>:Chats:<chat>` sets `Persona`, which is appended to the system prompt
  for that chat's session only (C-019's `SessionSettings`, given to `OpenSession` and applied when the session is first opened), and `Slots`, catalogue models switched
  in with `pin`. Slots are not restored from the log on resume (C-006), so each start switches them again. The decider
  is process-wide and refused, and models are checked against the catalogue at validation.
- Vault (§10.5, `vault.md` §9). `GatewayHttp` builds every adapter's pipeline. A placeholder goes where the token goes:
  `VaultSecretHandler` for `Authorization: Bot …` (Discord) and `Bearer …` (Slack), and the new
  `VaultUrlSecretHandler` (`Corvid.Vault.Client`) for Telegram's `/bot<token>/` path. The URL handler replaces a marker
  with the placeholder, strips credential headers, refuses a request without the marker, and re-mints once on
  `bad_placeholder`; Vault substitutes in a URL only for a secret with `allowInUrl`. `VaultHandler` sends the request
  through Vault's proxy with the toolset `gateway` and the answer's taint. Below that sits a handler that refuses, so
  nothing leaves unproxied. One Vault session serves the gateway process. Vault refusals of a send are classified:
  `rate_limited`, `budget_exceeded`, `upstream_error` and `bad_placeholder` are retried, and policy refusals are given up
  with Vault's reason. Under the default `tainted: ask`, Vault refuses answers from a tainted session, so guests get
  answers only when the user sets `tainted: auto` on the token. Sends are `POST`s with no approval attached, so the
  token's `methods` must allow `POST` as `auto`.
- Telegram (`TelegramAdapter`). `getMe` connects and `getUpdates` long-polls (25 s, `message` updates only). An update is
  confirmed (by the next poll's offset) only after the gateway has taken it, so a restart receives it again rather than
  losing it. `/cmd@OtherBot` is ignored. `sendMessage` is used in 4,096-character pieces with `reply_parameters` and
  forum topics. 400, 403 and 404 are permanent, and `retry_after` is honoured.
- Discord (`DiscordAdapter`). It uses the REST API, polling each configured guild channel and a DM channel opened with
  each known user (owners and guests from configuration, and pairings, picked up each round) every 5 s. A per-channel
  cursor in `gateway.db` means messages written while the gateway was down are read on start, and a channel seen for the
  first time starts at its newest message. Discord's realtime Gateway WebSocket needs the token in its first frame
  (`IDENTIFY`), which Vault cannot substitute yet. Polling cannot see a DM from someone unknown, so a new person asks to
  pair by mentioning the bot in a watched channel. Reading message content needs the Message Content intent. Answers come
  in 2,000-character pieces with an empty `allowed_mentions`, so model text cannot ping `@everyone`. A thread reply starts
  a thread on the message. On a resend, a thread already created (`160004`) is reused by the message's id, and where no
  thread can be made (a DM) the answer is a plain reply.
- Slack (`SlackAdapter`, Socket Mode). `auth.test` uses the bot token and `apps.connections.open` the app-level token,
  both as Vault placeholders. The one-time `wss://` URL is opened directly, because Vault does not relay WebSockets. It
  carries a single-use ticket, not a token, and only `wss` is accepted. Envelopes are acknowledged before they are
  handled, and `disconnect` reconnects. DMs (`message` with `channel_type` `im`) and `app_mention` reach the gateway, while
  bot messages and subtypes do not. `chat.postMessage` takes `thread_ts`, and text is escaped (`&`, `<`, `>`) so model
  text cannot ping a channel or a person. Errors such as `channel_not_found` or `not_in_channel` are permanent.
- Host and CLI. `Corvid:Gateway` (`Adapters` by name with `Enabled`, `Secret`, `AppSecret` for Slack, `Owners`, `Guests`,
  `GuestTools`, `HomeChat`, `Chats`, `Channels` for Discord, `PollSeconds`; `ConnectTimeoutSeconds`; `ApprovalTimeoutSeconds`) is
  validated at start. Secrets must be Vault secret names, never tokens, and Slack needs both. `CorvidGateway.Create`
  composes the gateway over a running `CorvidRuntime`. Registrations in DI replace a built-in adapter of the same name,
  which is how tests and future adapters plug in. `AddCorvidGateway` adds `GatewayService`, the one hosted service: it
  starts the runtime, then the gateway, and stops them in reverse order. `corvid gateway [run]` loads models like `chat`
  and runs until Ctrl-C. `status` lists the adapters, whether a gateway runs, waiting deliveries and pairings. `pair
  [list]`, `pair approve <code> --owner|--guest` and `pair revoke <adapter> <user>` manage pairing.
- Tests: `tests/Corvid.Gateway.Tests` covers the host over a fake agent and fake adapters (pairing only for strangers,
  unaddressed group messages, owner and guest taint, a guest's tool scope from its adapter, a chat's session opened again after a failed open, owner-only commands and approvals, an unanswered approval denied,
  every `deliver` route, silent answers, redelivery after a crash-free restart exactly once, a delivery queued by
  another process, retries in order and permanent failures, a hanging connect and a hanging constructor isolated from
  the other adapter, reconnect after a receive failure, and one gateway per data directory), the ledger (only open
  deliveries held, resolved once, the mark never past an unread entry), pairing, message splitting, and each adapter
  against a fake platform behind `FakeVault`. The adapter tests check the placeholder in the header or URL path, the
  toolset and taint flags, cursors, mentions, threads, refusal classification, Vault refusals, and no route without Vault.
  `tests/Corvid.Host.Tests` (`GatewayTests`) covers the real runtime over fake models: the chat's own session, a guest
  tainting it until the owner speaks, a guest offered no file tool while the owner's same request in the same chat lists
  the files, an MCP-style tool kept from a guest who has other tools, an allowlisted tool working for a guest and the only
  one offered, unknown guest tools refused at start, the shared command registry, a side effect approved in the chat, a persona and
  slot override for one chat only, a job's result reaching its chat (tainted when its run ended tainted), the hosted
  service, and validation (memory write tools refused as guest tools). `tests/Corvid.Kernel.Tests` (`EgressTurnTests`)
  checks that a guest's turn sees no core block and no recalled fact, and `TurnLoopTests` that it is not asked
  `remember`, stages nothing and cannot call a memory write tool its scope names, while the owner's next turn still
  stages its message.

Not built yet: Signal, email (IMAP/SMTP) and webhook adapters (§12); Discord's realtime Gateway and Slack's WebSocket
through Vault (both wait on Vault's WebSocket first-frame substitution, `vault.md` §9); DMs on Discord from people not yet
known; Telegram webhooks; non-text content in either direction (images, files, voice, and §12's STT and TTS); streaming
partial answers or typing indicators into chats; message edits and deletions; Vault's out-of-band `confirm` for tainted
sends (refused or allowed per token today); pairing from a chat; a cap on the ledger session's size (resolved entries
stay in the log); taint that follows content rather than turns (by C-011's rule the owner's next message clears it,
though a guest's earlier text in a group chat stays in the projection, as a fetched page does); and a run against the real
platforms with real tokens.

## Trace export and Hermes memory import (C-027)

Plan §18 row C-027 and D-9. The export adapts the telemetry schema of Beacon (Asymptote-Labs/agent-beacon, MIT): field
names, event identity and the redaction patterns were read from its `docs/telemetry-schema/{event-schema,normalization,fields}.mdx`
and `pkg/asymptoteobserve` (`event.go`, `identity.go`, `dedupe.go`, `content.go`, `metadata_only.go`, `privacy.go`) and its
Hermes session mapper, and reimplemented; no Beacon code is used. The Hermes formats were read from Hermes Agent's source
(NousResearch/hermes-agent at 1ab79836, `tools/memory_tool_store.py`, `tools/memory_tool.py`, `hermes_state_common.py`).

- `corvid traces export [--session <id> | --all] [--retention full|metadata] [--include-local-memory] [--out <file>]`
  (`Corvid.Cli/TracesCommand`). It refuses when the event log does not exist (opening `SqliteEventLog` would create one),
  reads each session in pages of 4096 up to a head captured first, and writes one line per event: raw UTF-8 bytes to
  standard output (never through Spectre), or to `--out`, created `CreateNew` with mode 0600, so an existing file is never
  overwritten. An unknown session is an error. `session.working_directory` is the configured workspace, because Corvid
  records no per-session directory: older sessions are labelled with today's workspace.
- `BeaconExporter` (`Corvid.Kernel/Traces`, BCL only: `System.Text.Json.Nodes`, SHA-1, SHA-256, `[GeneratedRegex]`) is a
  pure function over `StoredEvent`s, which is what lets the golden test use fixed times (`SqliteEventLog` stamps the clock).
  Each line is built as a `JsonObject` in the field order of Beacon's `Event` struct: `timestamp` (`recorded_at` in UTC with
  a fixed nine-digit fraction, so timestamps compare as strings; it is the time the event was appended), `sequence` (the
  event's log position, stable per session), `vendor` `beacon`, `product` `endpoint-agent`, `schema_version` `1.0`,
  `event{id,kind:agent_runtime,action,category,fidelity:observed}`, `severity` (`high` for `tool.failed`), `endpoint{os}`,
  `harness{name:corvid,version,collection_method:poll}` (what Beacon's own session-file readers use), `origin` `local`,
  `error`, `session{id,working_directory}`, then `tool`, `file`, `command`, `mcp`, `approval`, `prompt`, `content`,
  `gen_ai`, a fixed per-action `message` and `field_truncated`. `vendor` and `product` are the schema's identifiers, which
  Beacon's `Event.Validate` requires, not a claim that Beacon wrote the line; `harness.name` names the producer.
- Mapping, one pass with two lookups (call id to `ToolCallIssued`, approval id to call id), as Beacon's Hermes mapper does:
  `UserMessage` to `prompt.submitted` (`prompt.text`); `AssistantMessage` to `agent.message` (`gen_ai.output.messages` as
  one assistant message with one text part); `ToolCallIssued` to `tool.invoked` (`tool{name,command,path}`,
  `gen_ai.operation.name` `execute_tool`, `gen_ai.tool{call{arguments,id},name}`); `ToolResult` by its call: `shell` to
  `command.executed` (`command{command,exit_code,output}`, stdout then stderr; a nonzero exit sets `error.type`
  `command_failed`), `read_file`, `list_files` and `search_files` to `file.read`, `write_file` and `patch_file` to
  `file.modified` (`file.operation` `create` when `write_file` created the file, else `modify`), a `server__tool` name to
  `mcp.tool_invoked` (`mcp{server,tool}`), anything else to `tool.completed`, and `IsError` to `tool.failed` (`error.type`
  `tool_error`), each with `gen_ai.tool.call.result`. Tool paths are resolved against the working directory.
  `ApprovalRequested` becomes `approval.requested` (`approval{required,reason}`, no decision, as Beacon writes it) and
  `ApprovalResolved` `approval.allowed` or `approval.denied` (`approval.decision`), both with the call's `tool` and, for a
  shell call, `command.command`, which Beacon's approval rules match on. Every other kind is skipped and no
  `session.started` or `session.ended` is synthesized.
- `event.id` (`BeaconEventId`) is Beacon's `EventIDForLine`: a version 5 UUID under Beacon's fixed namespace. A tool,
  command, file or MCP event with a call id is named from session, lowercased action, Beacon's `dedupeTarget` (its
  `canonicalTarget` quirks included: `command:<command.command>|<tool.command>`, `tool:<name>|<path>|<message>`,
  `tool.completed:<message>`, `mcp:<server>|<tool>`, `file:<path>`) and call id, so it equals what Beacon derives for the
  same action; other events are named from their own line without `event.id`, `endpoint`, `harness.version` and
  `session.working_directory` (`BeaconEventId.Identity`), so a prompt, message or approval keeps its id across Corvid runs,
  rebuilds (the version carries the commit), hosts and workspaces, though it is not equal to a digest of Beacon's byte
  layout; a tool event whose path is resolved against the workspace names a different file from another workspace, and
  rightly a different id. The id is computed once, on the full-retention line after redaction and the
  per-string limit, with memory content withheld, so it is the same in both retentions, with or without
  `--include-local-memory`, and under `--session` or `--all`. SHA-1 is what RFC 4122 version 5 specifies.
- Content (`BeaconContent`). Each retained string (prompt and assistant text, tool arguments and result, command output and
  line, approval reason) is redacted, hashed (SHA-256 of the redacted text before truncation) and cut to Beacon's 4096-byte
  limit on a character boundary with Beacon's `...[truncated]` marker. One `content{retention,included,redacted,truncated,hash,bytes}`
  marker per event describes its primary string (the prompt, the assistant text, the arguments of an invocation, the
  command output, otherwise the result, or the approval reason); `bytes` is the original UTF-8 length, and `redacted` and
  `truncated` are set when any string of the event was. The hash differs from Beacon's `RetainedContent`, which hashes the
  original: a hash of the original would let anyone holding the export confirm a guess at a redacted secret offline.
  A line over 64 KiB (reachable only through escaping) drops the tool result, then the arguments, command output, prompt
  text, assistant text and approval reason, setting `field_truncated` and `content.truncated`; Beacon only flags such a
  line.
- `--retention metadata` follows Beacon's `StripToMetadata`: the message, `prompt.text`, `command.output`, `approval.reason`,
  `gen_ai` input and output and tool call arguments and result go, emptied objects are dropped, and the marker becomes
  `{retention:metadata,included:false,redacted:true}` with its hash and size. Ids, names, paths, the command line, exit
  codes and decisions stay. A hash of short text such as a prompt still lets someone confirm a guess.
- Local-only memory. The arguments and results of every `memory_*` tool and of the shared `search_memory` are written in
  metadata form even in a full export, unless `--include-local-memory` is given: memory holds local-only facts (§10.4), and
  the tools' results do not mark which items are, so the rule is by tool name and conservative. A tool that returns memory
  text under another name is not covered.
- `SecretRedactor` replaces with `[REDACTED]`, keeping a credential's public type prefix as Beacon does: private-key
  blocks, `Bearer` credentials, `key=value`, `key: value` and quoted JSON or shell assignments whose label names an API
  key, access key, secret, token, password, passwd, private key or client secret (an Authorization scheme or JSON literal
  is left), AWS access key ids, GitHub `gh[pousr]_` and `github_pat_` tokens, Slack `xox?-` tokens, `sk-` keys and
  JWT-shaped strings. It is a screen, not a proof; Corvid keeps real secrets behind Vault placeholders, but tool output can
  hold one.
- `/memory import hermes [<hermes home>] [--dry-run]` (`CoreCommands`), next to `/skills import hermes`, so it works from
  chat, ACP and the gateway (the owner's alone). `HermesMemory` (`Corvid.Memory`) reads `$HERMES_HOME/memories/MEMORY.md`
  (the agent's notes) and `USER.md` (the user profile), an explicit argument winning, as Hermes' `get_hermes_home` finds
  the home: `$HERMES_HOME` trimmed, with `$VAR` and `${VAR}` (and `%VAR%` on Windows) and a leading `~` expanded as
  `expandvars` and `expanduser` do, else `~/.hermes` (`%LOCALAPPDATA%\hermes` on Windows) with `$HERMES_DATA_DIR_SUFFIX`
  appended. A `HERMES_HOME` still relative after expansion is reported and nothing is imported, rather than falling back
  to the default profile. The files are read as Hermes' `MemoryStore` reads them: strict UTF-8 (a file with an invalid byte is refused whole, as Hermes refuses to
  read it), a leading BOM stripped (`utf-8-sig`), CRLF read as LF (Python's universal newlines), entries split on the
  whole `"\n§\n"` delimiter so a bare `§` survives, each stripped, blanks dropped and repeats dropped keeping the first. A
  missing file has no entries; an entry shaped like Hermes' `[BLOCKED: …]` placeholder is skipped and counted, though
  Hermes puts that placeholder only in its system-prompt snapshot, so the files hold the raw entries. Each entry is
  screened by Corvid's `SkillScanner` (not Hermes' `scan_for_threats` rules), and a flagged entry is refused. The rest
  go to `MemoryWriter.StageImportAsync`, which runs `WriteAsync`'s first two steps (extractor entities, neighbour
  retrieval; factored into one helper) and stages a local-only fact proposal with reason `import:hermes MEMORY.md` or
  `import:hermes USER.md`, without asking the gate. It never writes and never overwrites. An entry identical (case and
  spacing aside) to a pending proposal or a current fact is skipped, so a second import stages nothing new; one the
  extractor refuses as too long is reported, never cut. The report counts per file what was staged, skipped as a
  duplicate, refused and flagged. Review is `/memory pending`, `approve` and `reject` as for any staged write; an approved
  import keeps its entities and is embedded as usual.
- Tests. `tests/Corvid.Kernel.Tests` (`BeaconExportTests`): a fixture session of hand-built stored events (a prompt, a
  shell call with an approval and a failing exit code, `write_file`, `read_file`, an MCP `weather__lookup`, `memory_search`,
  a tool error, a denied approval, a skipped `GoalSet` and a 5 KB fetch result with embedded credentials) compared line by
  line with `Fixtures/beacon-export.full.jsonl` and `beacon-export.metadata.jsonl` (`CORVID_RECORD_GOLDENS=1` rewrites
  them); every line passes a C# copy of `Event.Validate` plus the timestamp and UUID formats; ids are equal across
  retentions, with local memory included and across harness version, OS and (for events naming no path) workspace; a
  call id matches a UUID computed independently with Python's `uuid5`;
  memory content is withheld and present with the flag; redaction precedes truncation; a cut is flagged even when the
  marker restores the UTF-16 length; the event cap; and the redactor's
  patterns. `tests/Corvid.Cli.Tests` (`TracesCommandTests`): `--session` on stdout, `--all` with ids equal to a
  single-session export, metadata retention, `--out` owner-only and never overwritten, an unknown session, a missing log
  (not created) and bad arguments. `tests/Corvid.Memory.Tests` (`HermesMemoryTests`): the parser (a bare `§`, blanks,
  duplicates, `[BLOCKED:`, BOM, CRLF, invalid UTF-8, a missing file, directory resolution with `HERMES_HOME` expansion,
  the suffix and an unusable value) and `StageImportAsync` (staged
  local-only with its reason and no decision, applied only on approval, pending and known duplicates skipped, an extractor
  refusal staging nothing). `tests/Corvid.Host.Tests` (`MemoryImportCommandTests`): the command against a temporary
  Hermes home, with a dry run, a scanner refusal, re-import idempotency before and after an approval, a missing home and
  bad usage.

Not built yet: Hermes session import (D-9 "sessions later"; the format is verified: `~/.hermes/state.db`, schema version
31, `sessions` with id, source, model, cwd, title, parent session, `started_at` and `ended_at` as REAL seconds and token
and cost columns, `messages` with role, content, `tool_call_id`, `tool_calls` JSON, `tool_name`, `timestamp`, `active`
and `compacted`; it waits on a way to append events with past times, a read-only snapshot of a live WAL database, and a
mapping of Hermes tools and of active and compacted messages); token usage and the model in exports (Corvid persists no
usage, only OpenTelemetry spans; a `token.usage` event with `gen_ai.usage.input_tokens` as uncached input needs usage
recorded per turn); the skipped event kinds (decisions, compaction, model switches, memory and skill changes, goals,
todos, per-path file events from checkpoints, rewinds, deliveries) and session start, end and handoff events; a
per-session working directory; importing Hermes memory into the core blocks (`USER.md` to `user`, `MEMORY.md` to
`agent`), which waits for block editing (C-013) because an over-budget block edit cannot be approved; `SOUL.md` to the
persona block, Hermes profiles and memory provider plugins; a `corvid memory` verb; `traces` tail, follow, date ranges
and pushing to a Beacon endpoint or SIEM; host, user and repository enrichment and the `redacted` retention level; and a
per-item local-only filter for memory tool results.

## Running the check

```sh
dotnet build Corvid.slnx -c Release
dotnet run --project tools/Corvid.Architecture -c Release --no-build -- .
dotnet test --solution Corvid.slnx -c Release --no-build
```

CI runs all three. The checker enumerates every production `.csproj` under `src/`,
`vault/` and `sandbox/`, then reads its `obj/project.assets.json`. NuGet's resolved targets
include transitive packages and projects; all restored frameworks and runtime
targets are checked. Project identity comes from the referenced `.csproj` path,
not its configurable assembly name. Unknown packages fail closed in core projects.

Run a fresh build/restore before checking: the checker validates the restored
graph, not edits made after restore. Missing assets fail the check, including for
a new production project omitted from the solution. The repository uses the
standard `obj/` path; custom intermediate paths are not supported. This is an
architecture regression check for NuGet/project references, not a security
sandbox or an IL analyzer for manually supplied binaries.

`corvid chat` (C-013) runs the loop and the built-in tools outside tests; `corvid acp` and `corvid serve` (C-019) run the
same sessions for an editor and for HTTP clients.
