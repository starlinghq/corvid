# Agent foundation (C-001, C-002, C-004a, C-005, C-006, C-007a, C-009, C-010)

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
executable in `sandbox/Corvid.Sandbox.Launcher` (see [Sandbox](#sandbox-c-007a)). Add
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
  keyed by session and a one-based sequence. Triggers reject `UPDATE` and `DELETE`.
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

Not built yet: the session actor and its command channel, rendering a projection
into a model prompt, full-text search over raw events, branch and fork
sessions that copy a prefix, and schema migrations beyond version 1. SQLite calls
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

Not built yet: decision replay. The plan (§3.2, §16) replays logs against new
thresholds and new models. `IDecider`, the TurnState builder and versioned floors
now exist (C-004a), and every `Decision` event records its floor version and decider;
replay still needs a decision-set format and scoring (tool choice, argument match, escalation
rate). Golden sets per generator profile and fabric version, a command-line runner,
and prompt-rendering checks (C-006) also follow later.

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
projection for the new model's format, re-embedding after an `embedder.text`
swap (§4.3), restoring current bindings and pins from the log on resume (pins
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
  thread and return completed tasks.
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
  Linux process that runs GLiNER2; the host (C-013) must set it at process start.
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
conversation-level token counting before a call, the `corvid doctor` command itself
(C-013), and the device scheduler (C-011).

## Dependency rules

- `Corvid.Abstractions` and `Corvid.Vault.Contracts` have no application package or
  project dependencies.
- Production projects under `src/` are core libraries unless named
  `Corvid.Models.*` (for example, `Corvid.Models.Starling`). The base
  `Corvid.Models` project is core, not an adapter.
- Core libraries may reference other core libraries and explicitly reviewed
  infrastructure packages. They may not reference adapters or provider SDKs,
  even indirectly. The package allowlist contains
  `Microsoft.Extensions.AI.Abstractions` (used by Models, Tools and Fabric) and, for the event log,
  `Microsoft.Data.Sqlite` with its resolved dependencies
  (`Microsoft.Data.Sqlite.Core` and the `SQLitePCLRaw.*` e_sqlite3 bundle). Add infrastructure packages and their
  transitive dependencies to `CorePackages` in the checker when needed; never
  add a provider SDK there.
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

When the composition root (`Corvid.Host`) is implemented, give it an explicit
composition rule allowing adapter references. Do not relax the core library rule
to accommodate it. The current milestone does not create or exempt a host.

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
toolset bindings. Corvid.Tools contains no built-in tools yet.

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
  be passed in, but nothing wires one yet.
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
  and still read.

Not built yet: the Kev adapter and the two-tier escalation to Kev-4B (C-004b, which
waits for the package); wiring a model's token counter into the builder; GLiNER2 entities and the
embedding shortlist that feed the state and the `tool` and `skill` options (§5.4,
C-010, C-011); the `remember` durability score; the `tool` question's
below-floor variant that escalates on the 0.8B generator (the caller's choice for
now); generator logprobs as a Noul confidence (`IChatClient` has no logprobs
surface); per-question row budgets (1,000 tokens), which the Kev adapter enforces
by declining; decision replay and calibration tooling (C-021).

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
  refused before it leaves Corvid; a test keeps them in step.
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
macOS and Windows, the structural approval pipeline (C-007b), and the shell and code tools
that use the sandbox (C-012).

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

The next milestone that builds on the log is decision replay in the C-009
harness, now that C-004a records floor versions on every decision.
