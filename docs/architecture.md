# Agent foundation (C-001, C-002, C-004a, C-005, C-006, C-007a, C-007b, C-008, C-009, C-010, C-011, C-012, C-013, C-021)

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
and the `corvid` executable, `Corvid.Cli` (see [Host and CLI](#host-and-cli-c-013)). Add
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
  events stay in the log. Goals, todos and approvals are folded from their own
  events, so compaction cannot remove them. `Load(log, session, throughSequence)`
  projects any earlier position, the basis for resume, branch and undo.
- Persisted events the projection cannot apply (a compaction that splits a tool
  call from its result, an outcome for an unknown decision, duplicate IDs) are
  reported in `SessionSnapshot.Issues` rather than thrown, because an append-only
  log cannot repair them and the session must stay readable.

C-011 added the session actor and prompt rendering (see [Session actor and turn loop](#session-actor-and-turn-loop-c-011)).
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

Not built yet: compaction and expansion on a budget change (the hook only
reports the direction), per-request redaction (§10.4), re-rendering the
projection for the new model's format (the turn loop renders `IChatClient` messages for every model; Anthropic cache
breakpoints come with C-015), restoring current bindings and pins from the log on resume (pins
are not persisted), recording switch costs for `/insights`, and the
media-embedder contract.

## Starling adapters (C-010)

`Corvid.Models.Starling` is the in-process adapter for Starling's models (plan §1,
§4.1). It references `Starlings.Models.Qwen35.Extensions.AI`, `Starlings.Models.Gliner2`,
`Starlings.Models.EmbeddingGemma2.Extensions.AI`, `Starlings.Models.E5.Extensions.AI` and
`Starlings.Acquisition`, all 0.2.0 from nuget.org (D-3), and `Corvid.Models`. It is not
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
  unseeded sampling; tool calling is experimental in Starling 0.2.0. The caller owns and
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
make placement per binding; plan §4.4 puts the generator on the 3090), the Kev decider
adapter (C-004b, waiting for `Starlings.Models.Kev` on nuget.org), media embeddings
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
- `corvid evals` starts C-021's `corvid-evals` by path (found through `$CORVID_EVALS`, next to `corvid`, or on `PATH`), the
  way Corvid starts `corvid-sandbox`, so no production project references eval code.

## Tools (C-005)

`Corvid.Tools` is a core library (Microsoft.Extensions.AI.Abstractions and
`Corvid.Vault.Client`, for `SecretRef`). It holds the `[Tool]`, `[SideEffect]`,
`[Network]` and `[Destructive]` attributes, `ToolDescriptor` and `ToolRisk`,
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
parameter or return types, invalid or duplicate names, a missing description
and `[Destructive]` without `[SideEffect]` are compile errors (CVTOOL001 to
CVTOOL008).

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
side of §5.2 and §5.5, and the degraded tier of §5.6. Kev is not used:
`Starlings.Models.Kev` is not on nuget.org, so the degraded decider is the only
`IDecider` implementation, and nothing in Fabric references a Starling or provider
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
the extractor's entities. Not built yet: the Kev adapter and the two-tier escalation to Kev-4B (C-004b, which
waits for the package); the `skill` options (C-017); the `remember` durability score; the `tool` question's
below-floor variant that escalates on the 0.8B generator (the caller's choice for
now); generator logprobs as a Noul confidence (`IChatClient` has no logprobs
surface); per-question row budgets (1,000 tokens), which the Kev adapter enforces
by declining. Decision replay against new floors and calibration tooling are C-021.

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
  outside bubblewrap), and a small BPF interpreter checks the filter's decisions. CI installs
  bubblewrap, sets `kernel.apparmor_restrict_unprivileged_userns=0` (Ubuntu 24.04 limits
  unprivileged user namespaces to AppArmor-profiled programs), and the required step "Sandbox
  confinement ran and passed" fails unless all of them ran with none skipped.

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
deduplication and merging; media memory (§9.4); local-only facts and egress filtering of
retrieved memory (C-015). C-012 added the memory tools; C-013 added `/memory` (pending, approve, reject) and runs
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

`corvid evals` (C-013) runs `corvid-evals` with `--log` defaulting to the user's event log. Not built yet: Kev-0.8B against Kev-4B agreement (C-004b); fitting the degraded
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
spike S3; calibrated `risk` (C-004b); reversibility from checkpoint state (C-014; `ApprovalContext.Reversible` is passed through);
restoring session rules on resume (they are not in the log); removing rules (C-013's `/approvals` lists them, and the
host keeps always-rules in `Corvid:Approvals:RulesFile`, by default `$XDG_CONFIG_HOME/corvid/approvals.json`); control flow, subshells and other syntax
outside the subset, which ask rather than run; unwrapping further launchers (`strace`, `nsenter`, `unshare`,
`runuser`, `systemd-run`, `script -c`, `at`, `parallel`, ...), which today are programs that run code Corvid
cannot see, so they ask unless a calibrated `risk` answer allows them, and the hardline list does not see the
command they launch; symlinks and `/proc/<pid>/cwd` paths, which are resolved lexically and left to the sandbox.

## Session actor and turn loop (C-011)

Plan §3.2, §3.3, §5.2, §5.4, §6, §7, §8 levers 1–2 (lever 2 has nothing to drop until skills exist) and §10.3. The code is
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
  token per turn, linked to the actor's lifetime, passed to every model, tool, approval and log call.
- A turn (`TurnRunner`): append `UserMessage`; extract entities from the user message when an extractor is bound
  (labels person, organization, place, date, time, project, file, url); rank the tool shortlist; ask the ingress
  checkpoint as one `DecisionFabric.DecideAsync` (intent, difficulty, clarify, `needs_recall` when a retriever is bound,
  and `tool` over the shortlist plus `none`) on one TurnState. The TurnState is built per checkpoint with the generator
  binding's own `ITokenCounter` when it offers one, the log's recent entries as one-liners, the goal, the entities, the
  step and the last tool result. `clarify` (or `intent: ask_clarifying_question`) makes the answer one question;
  `needs_recall` runs hybrid retrieval into the system prompt (below its floor, the top three facts only); `difficulty`
  above its floor escalates (never in the degraded tier, which only logs a heuristic).
- Generation (§7): a confident `tool` pick offers exactly that schema, `none` offers no tools, below the floor the
  shortlist is offered (`intent: answer_directly` above its floor also offers none). The prompt is the projection rendered
  by `PromptRenderer`: system prompt (with the frozen core blocks, local-only ones left out for cloud generators, and any
  recalled memory), user and assistant text, calls as `FunctionCallContent` (consecutive calls in one assistant message),
  results as `FunctionResultContent`, compaction summaries as notes. Each call is validated before it is logged: the
  name must be one offered, the arguments must fit the generator's JSON Schema subset (`required`, `type` lists with null,
  `enum`, array `items`; unknown names are errors, because the binder would drop them), and, once per turn, slot
  pre-fill: the tool's free-text parameters become extractor labels (their descriptions), and a span at least 0.8
  confident that the argument omits or contradicts is shown beside it. An invalid call is logged as `ToolCallIssued` and
  an error `ToolResult` in one append (details, the expected schema and the extracted values: the repair message), so a
  concurrent switch never reads the call without its result. A `ToolArgumentException` from the
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
  stubbed, sparing the latest round, then all but the newest. Still over: escalate, or fail the turn. Compaction (§8
  lever 3) is C-016.
- Taint (§10.3): an envelope from the user clears it, an `external` one sets it, and any result of a tool with
  `[Network]` sets it (a page, and a webhook's response alike). It holds across turns until the user speaks and is passed
  to every approval, so network egress and new recipients ask whatever `risk` says. That includes network reads: a
  read-only `[Network]` tool goes through the pipeline in a tainted session (C-007b gives it `tainted_network_egress`),
  because injected text could carry data out in a URL. Untainted network reads run unasked. `injection` adds a decision per
  network result; without Kev it takes `treat_as_tainted`.
- Egress: the answer is appended as `AssistantMessage`, then `remember` (when a memory writer is bound) and `deliver`
  (when `AskDeliver`, for gateways) are asked together. `remember` above its floor sends the user message into
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
built yet: compaction (C-016), so a session past its budget fails after escalation rather than compacting; cloud
slots, privacy egress and prompt-cache breakpoints (C-015); subagents and the subagent queue's users; skills and the `skill`
question (C-017); the `delegate` and `skill_opportunity` questions (`wake` is asked by the scheduler, C-012); GLiNER2 entity boosts in the shortlist (§5.4's
third bullet); per-tool concurrency limits beyond read-only versus side-effecting; an incremental projection in the actor
(each checkpoint and generator call reloads the session from the log: about 2.7 ms for 300 events, linear in the session's
length); persisting taint, which a resumed actor starts without; outcomes for `done`, `stuck`, `intent` and the other
routing questions (user corrections and retries need a surface); `RequireAny` for no-op detection without a pick;
recording why a turn failed beyond its answer text; and memory per device in `/status` (C-013 shows slot, device, queue
depth and median latency).

## Host and CLI (C-013)

Plan §3.4, §12 (CLI), §16 (spans), §17 rows 1.2 and 1.3, and §4.4's `/status`. `Corvid.Host` is the composition root,
`Corvid.Cli` the `corvid` executable over it. Everything runs on the degraded decider; Starling packages come from
nuget.org through `Corvid.Models.Starling` only.

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
  prints as a notice; delivering results to chats is C-020's.
- Slash commands (`SlashCommandRegistry`, `CoreCommands`): surface-neutral, returning `CommandOutput` (title, lines, a
  table, a tone, exit) that the CLI renders with Spectre and a gateway can send as `ToPlainText()`. `/model` lists slots or
  switches one through `SessionActor.SwitchModelAsync` (`/model <model>`, `/model <slot> <model> [--pin]`,
  `/model unpin <slot>`); it refuses a model missing from the cache (with the download command) and an `embedder.text`
  model memory was not built with. `/status`: per slot the model, device, queue depth (all priorities), median latency of
  the last 128 calls and call count, plus the degraded decider's row, the session's head, pending approvals, taint and goal.
  `/goal [text|clear]` appends `GoalSet`. `/approvals` lists pending approvals and session and always rules. `/memory`
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
  catalogue model (`Check`, by size; `--thorough` hashes), marked failed only when configured, the decider (degraded until
  Kev), `MALLOC_MMAP_THRESHOLD_` in this process, bubblewrap on `PATH`, `corvid-sandbox` where `Corvid.Sandbox` looks for it,
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
  before any tool was offered to the generator: a fabric policy matter for C-004a/C-004b, not changed here.

Not built yet: Ctrl+P; typing while a turn runs (the chat reads the next line after the delivery, so `/model` mid-turn is
available to the gateway and a later CLI, not this one); removing approval rules (`IApprovalRuleStore` has no removal);
`/memory` block edits; a `/cron` command; the rest of the ~35-command v1 set (`/rollback`, `/skills`, `/insights` and
others arrive with their items); the `config`, `gateway`, `skills`, `memory`, `cron` and `export` verbs; a scheduler
that runs without a chat open (a daemon, or the gateway, C-020); delivering job results to chats (C-020); metrics and logs export; an Aspire
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
  `[SideEffect(Filesystem)]`; `write_file` is also `[Destructive]`, because until checkpoints exist (C-014) a replaced file
  cannot be restored. Every path parameter is named `path`, which is how C-007b finds the target. `Workspace` resolves a
  path against the workspace and follows every symbolic link in it (realpath, missing components kept), so a link inside
  cannot lead out; it refuses paths at or under denied paths (`Workspace.For` takes the approval environment's Vault,
  credential-store and safety-configuration paths) and credential names (the sandbox's `.env` rule, examples included,
  and private keys). Listings and searches leave out credential names and denied paths, never descend into `.git`,
  `node_modules` or a link (searches skip linked files too), and stop at limits (`FileToolOptions`); files with a NUL in
  their first 8 KiB are binary and not read.
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
`/cron` command; delivering job results to surfaces
(C-020; `JobScheduler.Completed` is the hook); checkpoints before writes (C-014); network for the shell per toolset and
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

`corvid chat` (C-013) runs the loop and the built-in tools outside tests.
