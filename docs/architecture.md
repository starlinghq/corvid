# Agent foundation (C-001, C-002, C-005, C-006, C-009, C-010)

The solution contains `Corvid.Abstractions`, `Corvid.Kernel`, and
`Corvid.Models`, alongside the working Vault projects. Kernel and Models each
reference Abstractions; Models also references
`Microsoft.Extensions.AI.Abstractions`. Models must not reference Kernel: the
kernel's turn loop will depend on the slots. C-005 added `Corvid.Tools` and its
build-time generator, `Corvid.Tools.Generators` (see [Tools](#tools-c-005)). C-010 added the first adapter,
`Corvid.Models.Starling` (see [Starling adapters](#starling-adapters-c-010)). Add
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
thresholds and new models; that needs `IDecider` and the TurnState builder (C-004),
then a decision-set format and scoring (tool choice, argument match, escalation
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
  `IEmbeddingGenerator<string, Embedding<float>>` for `embedder.text`, and
  `IExtractor` (Corvid.Abstractions, added by C-010) for `extractor`. The
  decider and media-embedder contracts are not defined yet (C-004a, and a
  Starling-typed API core cannot reference), so those slots accept any service.
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
decider/media contracts.

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
  `Microsoft.Extensions.AI.Abstractions` and, for the event log,
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

## Running the check

```sh
dotnet build Corvid.slnx -c Release
dotnet run --project tools/Corvid.Architecture -c Release --no-build -- .
dotnet test --solution Corvid.slnx -c Release --no-build
```

CI runs all three. The checker enumerates every production `.csproj` under `src/`
and `vault/`, then reads its `obj/project.assets.json`. NuGet's resolved targets
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
harness, after C-004.
