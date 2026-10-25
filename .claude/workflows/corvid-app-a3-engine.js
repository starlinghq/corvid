export const meta = {
  name: 'corvid-app-a3-engine',
  description: 'Build Corvid.App.Engine: the in-process services over CorvidRuntime behind the windowed app, then integrate, run end to end, verify, fix',
  whenToUse: 'Phase A3 of the windowed Corvid app (docs/plan/ui-handoff.md §3): the engine project, the update mapper (K-10), and engine services for every area.',
  phases: [
    { title: 'Foundation', detail: 'Corvid.App.Engine, the composition seam, K-10 update stream, the host config writer' },
    { title: 'Build', detail: 'five service groups in parallel worktrees' },
    { title: 'Integrate', detail: 'merge the groups, then Board and prompt history over them' },
    { title: 'End to end', detail: 'the app on the engine in a temporary home, every flow' },
    { title: 'Verify', detail: 'independent verifier, bounded fix rounds' },
  ],
}

// args: { maxConcurrent, base, shims, jobTmp }
const MAX = Math.max(1, (args && args.maxConcurrent) || 1)
let active = 0; const waiters = []
const slot = async fn => { while (active >= MAX) await new Promise(r => waiters.push(r)); active++; try { return await fn() } finally { active--; const w = waiters.shift(); if (w) w() } }
log(`maxConcurrent = ${MAX} (from agent-budget at launch)`)

const BASE = args.base
const SHIMS = args.shims
const TMP = args.jobTmp
const SETUP = 'You start in a fresh git worktree of the repository made for you (your working directory; `pwd` shows it). You cannot work in any other worktree: the session isolates each agent to its own.'

const RULES = `Rules that apply to you:
- Memory: before every dotnet build or test run \`agent-budget level\`. If it prints critical, wait with \`agent-budget alert --from critical --timeout 300\` and check again. When it prints tight, pass -m:2 to dotnet. Use ~/.dotnet/dotnet (the /usr/bin one has no SDK).
- Git: never push, never force-push, never touch main or other agents' branches. Commit work in progress at every milestone (at least every 20 minutes of edits): a run can be interrupted, and only commits survive it. Commit messages follow \`git log\` ("App.Engine: <what> (A3 <group>)", "Host: <what> (K-n)") and carry NO Claude attribution: no Co-Authored-By, no Claude-Session, no links, no "Generated with" footer.
- Read first: docs/plan/ui-build.md, docs/plan/ui-handoff.md (§1 what A0–A2 built and their reviews found, §3 the plan, §4 the session core the Sessions page binds to), src/Corvid.App/README.md, docs/plan/ui.md §8 (architecture) and the work items it and docs/plan/tui.md §9 name (K-1..K-10, G-n). The XML docs on src/Corvid.App.Core/Sessions/*Updates.cs say which update needs which item.
- Architecture (tools/Corvid.Architecture, tests/Corvid.Architecture.Tests): Corvid.App.Engine is a composition project: it may reference Corvid.Host, Corvid.App.Core, Corvid.Mcp, the Corvid.Models.* adapters and the core projects; never Uno, Spectre.Console, Corvid.Cli, Corvid.Acp or Corvid.App. Only Corvid.App's desktop target references the engine. Corvid.App.Core gains no new references (no Host, Kernel, Engine). Corvid.Host never references the App projects. Logic the CLI holds that the engine needs (config writes, scans, trace export, evals) moves into Corvid.Host with the CLI calling the moved code, not copied.
- Engine services implement the existing App.Core interfaces (ISessionService, IModelService, …) exactly as the view models use them; change an interface only additively and only when the engine cannot honour it otherwise (say so). The demo services and every existing App.Core test stay as they are and keep passing; --demo stays the default.
- Where the engine lacks something an interface needs, implement the kernel/host work item (K-n, G-n) additively in Corvid.Host/Kernel with tests when it is modest; when it is large, the engine service returns a clear refusal ("comes with G-3: …") that the page already shows as a red notice or a disabled action, and you list it. Never fake engine data.
- Never touch the user's real Corvid home: no reads or writes of ~/.config/corvid, ~/.local/share/corvid or the real Vault. Tests build hosts with tests/Corvid.Host.Tests/Fakes.cs (TestHost.Create with FakeModels/FakeGenerator, linked by Compile Include as Corvid.Acp.Tests does) in temporary directories. When you run the app on the engine, set CORVID_CONFIG and the data/state directory variables that src/Corvid.Host/Configuration/CorvidPaths.cs reads to a temporary directory under ${TMP}, and say which models it had.
- Untrusted text and the UI rules from A1/A2 still hold (SafeText Clean for display, Reveal where a person approves, nothing granted while HasHidden; tests write hidden characters as \\u escapes). Engine services marshal callbacks to the dispatcher the composition gives them; no blocking waits on the UI thread; every Task is awaited or its failure reported; subscriptions to TurnFeed and events are disposed through AppComposition.Dispose.
- Tests: xunit.v3 in tests/Corvid.App.Engine.Tests (and Host/Kernel tests for host changes); pass TestContext.Current.CancellationToken; never sleep or use the real clock where a TimeProvider exists. Keep tests/Corvid.Acp.Tests' exchanges passing unchanged unless the change is intended and re-recorded (say so).
- Looking at the app: stand-ins for Xvfb/xdotool are in ${SHIMS}. Run the harness as PATH=${SHIMS}:$PATH UI_HARNESS_STATE=<your state dir> CORVID_APP_RENDERER=software tools/ui-harness.sh <cmd> <display> ... with your own display and state dir (given below). After launch, wait 3 s, resize, check with \`DISPLAY=:<n> xwininfo -id $(cat <state dir>/win-<n>.id)\`, resize one pixel wider and back, wait 1.5 s. Screenshots under ui-shots/ (git-ignored), viewed with Read. Never stop processes with pkill/pgrep -f. Stop your display when done (\`stop <n> --xvfb\`).
- Before you finish: whole solution \`~/.dotnet/dotnet build Corvid.slnx -c Release\` with no warnings, the checker (\`~/.dotnet/dotnet run --project tools/Corvid.Architecture -c Release --no-build -- .\`), and the tests of every project you touched plus App.Core, Architecture, Host and Acp (run the executables under tests/*/bin/Release/net10.0/ if \`dotnet test --project\` reports zero tests). Never claim a check you did not run.`

const STEP = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['done', 'partial', 'blocked'] },
    branch: { type: 'string' }, worktree: { type: 'string' }, commit: { type: 'string' },
    summary: { type: 'string' },
    host_changes: { type: 'array', items: { type: 'string' }, description: 'every change to Corvid.Host/Kernel/other core projects, with the work item it serves' },
    shared_changes: { type: 'array', items: { type: 'string' }, description: 'every change to a file another group may also touch' },
    gaps: { type: 'array', items: { type: 'string' }, description: 'interface members the engine refuses for now, with the work item and the message shown' },
    verified: { type: 'array', items: { type: 'string' }, description: 'each check run, with its result' },
    screenshots: { type: 'array', items: { type: 'string' } },
    notes_for_next: { type: 'string' },
  },
  required: ['status', 'branch', 'worktree', 'commit', 'summary', 'host_changes', 'shared_changes', 'gaps', 'verified', 'screenshots', 'notes_for_next'],
}

const DEFECTS = {
  type: 'object',
  properties: {
    pass: { type: 'boolean', description: 'true only when no blocker or major defect remains' },
    summary: { type: 'string' },
    screenshots: { type: 'array', items: { type: 'string' } },
    defects: { type: 'array', items: { type: 'object', properties: {
      id: { type: 'string' }, severity: { type: 'string', enum: ['blocker', 'major', 'minor'] }, area: { type: 'string' },
      what: { type: 'string' }, expected: { type: 'string' }, evidence: { type: 'string' }, reproduce: { type: 'string' },
    }, required: ['id', 'severity', 'area', 'what', 'expected', 'evidence', 'reproduce'] } },
  },
  required: ['pass', 'summary', 'screenshots', 'defects'],
}

// ---------- Foundation ----------
phase('Foundation')
const foundation = await slot(() => agent(`You build the foundation of phase A3 of the windowed Corvid app: Corvid.App.Engine. Five builders start from your commit, so make the seams clean and small.

${SETUP} First run \`git switch -c a3/core ${BASE}\` there (or \`git switch a3/core\` if it exists: a resumed run, carry on from its commits). Display :171, state dir ${TMP}/harness-a3-core.

${RULES}

Build:
1. src/Corvid.App.Engine (net10.0 class library, in Corvid.slnx, referenced only from Corvid.App's desktop target) and tests/Corvid.App.Engine.Tests (linking tests/Corvid.Host.Tests/Fakes.cs and what it needs, as Corvid.Acp.Tests does). Satisfy the architecture checker and its tests (add the project to their tables if the rules require listing it).
2. The composition seam: AppComposition chooses demo or engine services per AppOptions. Add an \`--engine\` option to AppOptions (Demo=false; usage text updated; --demo stays the default) and a small factory interface in App.Core (e.g. IAppServices with one member per service interface, or a constructor parameter) that the head's engine path fills from Corvid.App.Engine. Remove the casts of SessionService to DemoSessionService in AppComposition.Review.cs and AppComposition.Rewind.cs (give the demo Review/Rewind what they need another way). In engine mode, an area whose engine service is not built yet shows its page with a clear "comes with A3 <group>" refusal, never demo data. The head (src/Corvid.App) builds the engine host (CorvidHost.CreateBuilder, CorvidRuntimeFactory.StartAsync with startup progress) off the UI thread; when models are missing (ModelsUnavailableException) it opens First run rather than failing.
3. K-10 (tui.md §9): a surface-neutral session update stream in Corvid.Host, extracted from Corvid.Acp's TurnReporter logic (TurnFeed observer + log events → message chunks, tool call start/update with input, output and ledger diffs, plan/decisions), and a replay of a stored session from SessionProjection into the same stream. Corvid.Acp should consume it if that is a clean refactor that keeps tests/Corvid.Acp.Tests' exchanges byte-identical; otherwise leave Acp and say so. In the engine, translate that stream to App.Core's SessionUpdate records (Sessions/*Updates.cs) — the translator is what the sessions builder will use.
4. A host config writer (Corvid.Host): the validated, atomic JSON edit that Corvid.Cli/ModelCommand.cs Switch does today, generalised to "apply these edits to corvid.json, validate with CorvidOptionsValidator, write atomically, report what needs a restart". The CLI calls it. Models, First run, Settings, MCP and Gateway will use it.
5. A minimal EngineSessionService proving the path end to end: ListAsync over the event log, OpenAsync replaying a stored session and streaming a live one, SendAsync running a turn. The sessions builder extends it; keep it in its own folder (src/Corvid.App.Engine/Sessions).

Tests: the translator (scripted FakeGenerator turns with a tool call → the expected SessionUpdate sequence), replay equals live for the same turn, the config writer (validates, atomic, refuses invalid), the seam (engine mode builds engine services, demo mode demo ones, the composition disposes both). Then run the app with --engine in a temporary home (fake or no models: it should land on First run, or open Sessions if you can configure models) and screenshot it, and with --demo to confirm nothing changed.

Return the STEP report; notes_for_next must say exactly how a builder adds an engine service (files, the factory member, how to get the runtime and the dispatcher, how tests build one).`, { label: 'foundation', phase: 'Foundation', schema: STEP, isolation: 'worktree' }))
if (!foundation || foundation.status === 'blocked' || !foundation.commit) return { stoppedAt: 'foundation', foundation }

// ---------- Build ----------
const GROUPS = [
  { key: 'sessions', areas: 'Sessions and approvals', interfaces: 'ISessionService (all of it) and the session side the Board watches', detail: 'Extend EngineSessionService: list with titles and state, open/replay/stream, send, the queue (engine-side over PostAsync), interrupt, steer (K-3 if modest), fork, rename (K-9/G-6), goal, compact, slash commands through SlashCommandRegistry with AvailableCommandsUpdate, approval mode (G-10: per session if modest, else runtime-wide and said so). An IApprovalPrompter attached per session (runtime.AttachPrompter) that emits ApprovalRequested with the prompt\'s assessment, awaits AnswerApprovalAsync and emits ApprovalResolved; Always answers write rules through the approvals rule store (G-11 narrowed rule if modest). Questions (K-6), thoughts (K-1), output tail (K-2), usage (K-5), taint changes and turn progress as far as TurnFeed allows; list the rest as gaps.' },
  { key: 'history', areas: 'Rewind, Review and Audit', interfaces: 'IRewindService, IReviewService, IAuditService (and IAuditSessionStatus if the engine has more than the hub)', detail: 'Rewind over CorvidSession.Checkpoints() and CheckpointService (timeline, plan, preview, restore by turn; files-only and single-file restore are G-3: implement if modest). Review: the changes since a point from the ledger diffs (as ToolPresentation.LedgerDiffsAsync does), revert a file (G-3), send comments into the session (via ISessionService.SendAsync), open in editor (refuse unless an editor is configured). Audit over the event log: the log, sessions, events, egress (re-render of a redacted request is G-9), detections via Corvid.Rules.DetectionEngine and the scan lifted from Cli/ScanCommand.cs into Host, trace export lifted from Cli/TracesCommand.cs.' },
  { key: 'setup', areas: 'Models, First run and Settings', interfaces: 'IModelService, IFirstRunService, ISettingsService', detail: 'Models over ModelCatalogue, ModelSlotRegistry and runtime.Bind: the snapshot, switch (through the foundation\'s config writer), ladders, pins, placement, downloads with progress and cancel (ModelCatalogue.DownloadAsync), server checks and adding a server (config writer). First run over Doctor.RunAsync, CheckModels, HermesHome and the config writer (SaveAsync), with K-4 load progress from StartupStep. Settings over CorvidOptions, CorvidOptionsValidator and the config writer, the typed page and the raw JSON editor writing the real file (in the temporary home when you test), saying what needs a restart.' },
  { key: 'knowledge', areas: 'Memory, Skills and Safety', interfaces: 'IMemoryService, ISkillsService, ISafetyService', detail: 'Memory over MemoryWriter and LessonWriter (approve with corrected text is G-8: implement if modest), search via the retriever, consolidation and token counts as far as Corvid.Memory exposes them. Skills over SkillStore, SkillRepository and SkillLearner (approve an edited draft; evals lifted from Cli/EvalsCommand.cs into Host if modest). Safety over the approvals rule store (list with source and scope; removal is K-7: add it to IApprovalRuleStore and its stores), the hardline list, the detection packs and enforcement setting.' },
  { key: 'ops', areas: 'Jobs, MCP, Gateway and Secrets', interfaces: 'IJobsService, IMcpService, IGatewayService, ISecretsService', detail: 'Jobs over JobStore and JobScheduler (create, runs, pause, resume, remove, schedule preview; G-13) and runtime.JobCompleted. MCP over McpClients and McpPinStore (accept with the shown hash, allow, keep blocked, forget) with trust edits and adding a server through the foundation\'s config writer. Gateway over PairingStore and the gateway host (G-14 in-process hosting and status: implement if modest, else load what the stores hold and refuse live actions); the per-user allowlist the page edits does not exist in the engine (only GuestTools): refuse or map it, and say which. Secrets over Corvid.Vault.Client (catalogue and status; never a value; V-n gaps listed); in tests use a fake Vault, never the real one.' },
]

phase('Build')
const builderPrompt = (g, i) => `You are one of five builders of phase A3 of the windowed Corvid app (Corvid.App.Engine), working at the same time as the others. Your group: ${g.areas}.

${SETUP} First run \`git switch -c a3/${g.key} ${foundation.commit}\` there (the foundation's commit), or \`git switch a3/${g.key}\` if that branch exists (a resumed run). Display :${172 + i}; state dir ${TMP}/harness-a3-${g.key}.

${RULES}

The foundation's report (how to add an engine service; read its code too):
${foundation.notes_for_next}
Foundation gaps and host changes: ${JSON.stringify({ gaps: foundation.gaps, host: foundation.host_changes })}

Implement engine services for ${g.interfaces} in src/Corvid.App.Engine/<Area>/, wired through the foundation's factory seam (your areas' AppComposition.<Area>.cs files and factory members). ${g.detail}

Other groups: sessions (ISessionService, approvals), history (Rewind, Review, Audit), setup (Models, First run, Settings, which own the config writer's callers), knowledge (Memory, Skills, Safety), ops (Jobs, MCP, Gateway, Secrets). Do not edit their folders. Changes to shared files (Corvid.Host/Composition/CorvidRuntime.cs, Corvid.Host/Commands, the foundation's seam, Corvid.slnx) must be additive and listed.

Tests in tests/Corvid.App.Engine.Tests (and Host/Kernel tests for host changes) for every service member you implement, over TestHost fakes in temporary directories. Then run the app with --engine in a temporary home, drive your areas' flows (ui.md §5) as far as the fake or available models allow, screenshot each, and read the app log for exceptions; confirm --demo is unchanged.

Return the STEP report: every gap with its work item and the message the page shows.`

const built = await parallel(GROUPS.map((g, i) => () => slot(() =>
  agent(builderPrompt(g, i), { label: `build ${g.key}`, phase: 'Build', schema: STEP, isolation: 'worktree' }))))
const done = built.map((b, i) => ({ g: GROUPS[i], b })).filter(x => x.b && x.b.status !== 'blocked' && x.b.commit)
const missing = GROUPS.filter((g, i) => !(built[i] && built[i].status !== 'blocked' && built[i].commit)).map(g => g.key)
if (missing.length) log(`Not integrated (blocked or no commit): ${missing.join(', ')}`)
if (!done.length) return { stoppedAt: 'build', foundation, built }

// ---------- Integrate ----------
phase('Integrate')
const integrated = await slot(() => agent(`You are the integrator for phase A3 of the windowed Corvid app.

${SETUP} First run \`git switch -c a3/engine-int ${foundation.commit}\` there (or \`git switch a3/engine-int\` if it exists). The builders' branches are in the same repository. Display :178, state dir ${TMP}/harness-a3-int.

${RULES}

1. Merge these branches one at a time with \`git merge --no-ff <branch>\`, building and running the tests after each:
${done.map(x => `- a3/${x.g.key} (${x.g.areas}), commit ${x.b.commit}. Host changes: ${JSON.stringify(x.b.host_changes)}. Shared: ${JSON.stringify(x.b.shared_changes)}. Notes: ${x.b.notes_for_next}`).join('\n')}
Resolve conflicts keeping both sides' additions (CorvidRuntime.cs, Host/Commands, the seam, slnx). If a branch cannot be merged without rewriting it, report it and continue.
2. Then the two aggregate services over the merged ones: IBoardService (attention from sessions' approvals and questions, held MCP tools, pairing codes, finished jobs; status; the AttentionChanged event) and IPromptHistory (UserMessage events across sessions; search is G-5), with tests.
3. Run the app with --engine in a temporary home and open every area once (no demo data anywhere in engine mode; unbuilt members show their refusal), and --demo once.

When you have committed, run \`git switch --detach\` so a3/engine-int is free for the next agent.

Return the STEP report (merged and not merged, conflicts and resolutions, checks).`, { label: 'integrate', phase: 'Integrate', schema: STEP, isolation: 'worktree' }))
if (!integrated || integrated.status === 'blocked') return { stoppedAt: 'integrate', foundation, built, integrated }

// ---------- End to end ----------
phase('End to end')
const e2e = await slot(() => agent(`You run phase A3's end-to-end pass of the windowed Corvid app on the engine.

${SETUP} Run \`git switch a3/engine-int\` there and work on that branch; when you have committed, run \`git switch --detach\`. Display :179, state dir ${TMP}/harness-a3-e2e.

${RULES}

In a temporary Corvid home: first with fake models if the head can be pointed at them (or an engine test that drives the composition end to end), then with real models if any are reachable without touching the user's real home (a configured local OpenAI-compatible server on this machine, if one answers; never start, stop or reconfigure a shared model server, and keep load light). Walk every flow of ui.md §5 that the engine supports (F1 first run, F2/F3 a turn with a tool call and its approval, F4 review, F5 rewind, F6 queue, F8/F10/F21 audit, F9, F11 memory, F12 skills, F13 jobs, F14 models, F15 MCP, F16 gateway as far as possible, F17 secrets with a fake vault only, F18 board, F23, F24 settings) on the running app, screenshot each, and read the app log. Fix small breakage you find (commit it); list the rest. Also try the WebAssembly head build if the wasm-tools workload is installed (\`~/.dotnet/dotnet workload list\`); do not install workloads, just report.

Return the STEP report: per flow, works / refused with work item / broken.`, { label: 'end to end', phase: 'End to end', schema: STEP, isolation: 'worktree' }))

// ---------- Verify ----------
phase('Verify')
const verifyPrompt = round => `You are the independent verifier for phase A3 of the windowed Corvid app (Corvid.App.Engine), round ${round}. Do NOT edit, commit or push.

${SETUP} Run \`git switch --detach a3/engine-int\` there. Display :180, state dir ${TMP}/harness-a3-verify-${round}.

${RULES}

Check: the architecture rules hold; the engine services honour the App.Core interfaces as the view models use them (read the view models); every update the translator emits matches the contract's XML docs; replay equals live; approvals block the turn until answered and never grant while hidden characters are present; the config writer validates and writes atomically and never touches the real home; no demo data in engine mode; failures surface as notices; subscriptions and the runtime are disposed; threading (dispatcher marshalling, no UI-thread blocking, no unobserved tasks); untrusted text from the engine goes through SafeText; tests are meaningful (not mocks of the thing under test). Run the app with --engine in a temporary home and with --demo; run the whole-solution build, checker and all tests.

The builders' and end-to-end pass's reported gaps (judge whether each refusal is honest and shown):
${JSON.stringify(done.map(x => ({ group: x.g.key, gaps: x.b.gaps })))}
End to end: ${e2e ? JSON.stringify({ summary: e2e.summary, gaps: e2e.gaps }) : 'did not report'}

Severity: blocker (crash, failing build/check/test, data loss, a write to the real home, an approval bypass), major (an interface member wrong or faked, a flow broken in engine mode, an undisposed subscription, an unhandled exception), minor. pass is true only when no blocker or major remains. Each defect needs evidence and steps.`

const fixPrompt = (round, defects) => `You are fix round ${round} for phase A3 of the windowed Corvid app. ${SETUP} Run \`git switch a3/engine-int\` there and work on that branch; when you have committed, run \`git switch --detach\`. Display :181, state dir ${TMP}/harness-a3-fix-${round}.

${RULES}

Fix every blocker and major defect below, and minor ones that are quick and safe. Rerun the check that showed each, then the whole-solution build, checker and tests, and commit.

Defects:
${JSON.stringify(defects, null, 2)}

Return the STEP report (fixed by id, left and why).`

let round = 1
let report = await slot(() => agent(verifyPrompt(round), { label: `verify ${round}`, phase: 'Verify', schema: DEFECTS, isolation: 'worktree' }))
const fixes = []
while (report && !report.pass && round < 3) {
  const fix = await slot(() => agent(fixPrompt(round, report.defects), { label: `fix ${round}`, phase: 'Verify', schema: STEP, isolation: 'worktree' }))
  fixes.push(fix)
  round++
  report = await slot(() => agent(verifyPrompt(round), { label: `verify ${round}`, phase: 'Verify', schema: DEFECTS, isolation: 'worktree' }))
}
if (report && !report.pass) log(`Verifier still reports ${report.defects.filter(d => d.severity !== 'minor').length} blocker or major defects after ${round} rounds`)

return { foundation, built, missing, integrated, e2e, fixes, finalReport: report, rounds: round }
