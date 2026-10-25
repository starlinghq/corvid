export const meta = {
  name: 'corvid-app-a5-models',
  description: 'Model switching at any time and subagents with a passed model mix (delegate_task), fork mid-turn, A4 leftovers; integrate, verify end to end, fix',
  whenToUse: 'Phase A5 of the windowed Corvid app and engine: per-session model mixes, live model switching, delegate_task subagents, G-15, and the A4 leftovers named in docs/plan/ui-handoff.md §1.5.',
  phases: [
    { title: 'Foundation', detail: 'ModelMix, session-default slots, per-session decider, child sessions' },
    { title: 'Build', detail: 'switching, subagents, fork mid-turn, leftovers in parallel worktrees' },
    { title: 'Integrate', detail: 'merge the groups one at a time' },
    { title: 'End to end', detail: 'every new flow on the running app and the CLI in temporary homes' },
    { title: 'Verify', detail: 'independent verifier, bounded fix rounds' },
  ],
}

// args: { maxConcurrent, base, shims, jobTmp }
const MAX = Math.max(1, (args && args.maxConcurrent) || 1)
let active = 0; const waiters = []
const slot = async fn => { while (active >= MAX) await new Promise(r => waiters.push(r)); active++; try { return await fn() } finally { active--; const w = waiters.shift(); if (w) w() } }
log(`maxConcurrent = ${MAX} (agent-budget said 5 at launch; four builders)`)

const BASE = args.base
const SHIMS = args.shims
const TMP = args.jobTmp
const SETUP = 'You start in a fresh git worktree of the repository made for you (your working directory; `pwd` shows it). You cannot work in any other worktree: the session isolates each agent to its own.'

const DECISIONS = `Decisions the user made for this phase (follow them):
- Model switching "at any time in a conversation": a switch the person makes lasts for the session (it no longer reverts at turn end) until they change it; escalation and fallback still move off it and de-escalation returns to it, not to the registry default. A running turn takes a switch at its next step boundary (after issued tool calls, as today). "This session" and "default for new sessions" are separate choices in the app and the CLI. The decider/Kev becomes switchable per session.
- Subagents with a passed model mix: the agent picks the mix in delegate_task (any slot: generator, decider, extractor, …) from models in the catalogue; the person can also pass one (/delegate in the CLI, the app). Local and local-server models are used freely; a cloud model goes through the same privacy egress gate and taint rules as the parent (a tainted parent cannot send to cloud unless egress allows it). Spawning a child is a side effect that follows the session's approval mode (its card shows goal, toolset, budget and model mix). Optional allowlist Corvid:Agents:Models (empty = any catalogue model). Plan docs/plan/corvid-agent.md §14 and §17 row 1.7 define delegate_task: a child session with a fresh log, a goal, an explicit toolset (not inherited wholesale), a budget and an optional output schema validated on return; 10 concurrent, depth 1, a stall monitor using stuck; children cannot use ask_user/clarify, memory writes, send_message or cron; their decisions still feed calibration.
- Also in A5: G-15 (fork at a step boundary while a turn runs) and the A4 leftovers listed for the leftovers group. Attached mode, worktrees per session, images and packaging wait for A6.`

const RULES = `Rules that apply to you:
- Memory: before every dotnet build or test run \`agent-budget level\`. If it prints critical, wait with \`agent-budget alert --from critical --timeout 300\` and check again. When it prints tight, pass -m:2 to dotnet. Use ~/.dotnet/dotnet.
- Git: never push, never force-push, never touch main or other agents' branches. Commit at every milestone (at least every 20 minutes of edits). Commit messages follow \`git log\` ("Kernel: <what> (A5 <group>)", "Host: …", "App.Engine: …") with NO Claude attribution: no Co-Authored-By, no Claude-Session, no links, no "Generated with".
- Read first: docs/plan/ui-handoff.md (§1.4 and §1.5: what A3/A4 built, refused and left; §4 the session core), docs/plan/corvid-agent.md (§3.3 device priorities, §6, §14 delegation, §17, the privacy egress and taint rules), docs/plan/ui.md (§4 features incl. §4.13 subagent tree, §5 flows incl. F18, §8 and the G-n table), docs/plan/tui.md (§4, §9), src/Corvid.App/README.md, and the XML docs on src/Corvid.App.Core/Sessions/*Updates.cs.
- ${'$'}{DECISIONS_PLACEHOLDER}
- Architecture (tools/Corvid.Architecture): Corvid.App.Engine may reference Corvid.Host, App.Core, Corvid.Mcp, the Models adapters and core projects; App.Core gains no references; Corvid.Host never references App projects. Logic shared by the CLI and the app lives in Corvid.Host.
- The CLI (corvid chat), ACP and the gateway are first-class surfaces: a feature works there too where they have a place for it, and nothing breaks them. Keep tests/Corvid.Acp.Tests' exchanges passing unchanged unless an intended change is re-recorded (say so).
- Security model: approvals block until answered; nothing is granted while SafeText.HasHidden; taint and egress are never weakened (a child inherits its parent's taint; cloud bindings pass the egress gate); model ids and names from configuration, catalogues or the model are untrusted text (SafeText); a model cannot raise its own limits (depth, concurrency, budget, toolset) beyond what the parent session allows.
- Never touch the user's real Corvid home (~/.config/corvid, ~/.local/share/corvid, ~/.hermes, the real Vault, real chat tokens). Tests use tests/Corvid.Host.Tests/Fakes.cs (TestHost, FakeModels, FakeGenerator) in temporary directories. When you run the app or the CLI, point HOME, CORVID_CONFIG and XDG_* at a temporary directory under ${TMP} (see ${TMP}/a3-e2e-scripts/run-app.sh and fakeoai.py, a scripted OpenAI-compatible server you can run on a free port to stand in for a second or cloud-class model). Never start, stop or reconfigure a shared model server. Never kill processes by name (no pkill/pgrep -f); kill only PIDs you started.
- Untrusted text: SafeText.Clean for display, Reveal where a person approves; tests write hidden characters as \\u escapes. Engine callbacks marshal to the dispatcher; no UI-thread blocking or log writes; every task awaited or its failure shown; subscriptions disposed through AppComposition.Dispose.
- Tests: xunit.v3; TestContext.Current.CancellationToken; deterministic concurrency tests (gates, TaskCompletionSource, pause hooks), no sleeps where a TimeProvider exists, no waits on a single update that can repeat. Each feature gets tests that fail without it.
- Looking at the app: stand-ins for Xvfb/xdotool are in ${SHIMS}. Run the harness as PATH=${SHIMS}:$PATH UI_HARNESS_STATE=<your state dir> CORVID_APP_RENDERER=software tools/ui-harness.sh <cmd> <display> ... with your own display and state dir (the engine is the default; the demo needs --demo). After launch, wait 3 s, resize, check with \`DISPLAY=:<n> xwininfo -id $(cat <state dir>/win-<n>.id)\`, resize one pixel wider and back, wait 1.5 s. Screenshots under ui-shots/ (git-ignored), viewed with Read. Stop your display when done (\`stop <n> --xvfb\`).
- Before you finish: whole solution \`~/.dotnet/dotnet build Corvid.slnx -c Release\` with no warnings, the checker (\`~/.dotnet/dotnet run --project tools/Corvid.Architecture -c Release --no-build -- .\`), and the tests of every project you touched plus App.Engine, App.Core, Architecture, Host, Acp, Kernel, Models, Approvals, Cli, Gateway, Fabric (run the executables under tests/*/bin/Release/net10.0/). If CI pins a test count (.github/workflows/ci.yml grep -q "Total: N") for a suite you change, update it. Never claim a check you did not run.`.replace('${DECISIONS_PLACEHOLDER}', DECISIONS)

const STEP = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['done', 'partial', 'blocked'] },
    branch: { type: 'string' }, worktree: { type: 'string' }, commit: { type: 'string' },
    summary: { type: 'string' },
    host_changes: { type: 'array', items: { type: 'string' }, description: 'every change to Kernel/Host/Models/core projects and the CLI/ACP, with the item' },
    shared_changes: { type: 'array', items: { type: 'string' }, description: 'every change to a file another group may also touch' },
    gaps: { type: 'array', items: { type: 'string' }, description: 'what is still refused or partial, with the reason and the message shown' },
    verified: { type: 'array', items: { type: 'string' } },
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

phase('Foundation')
const foundation = await slot(() => agent(`You build the foundation of phase A5: the model-mix and child-session core that the four builders after you use.

${SETUP} First run \`git switch -c a5/core ${BASE}\` there (or \`git switch a5/core\` if it exists: a resumed run). Display :231; state dir ${TMP}/harness-a5-core.

${RULES}

Build, small and clean (builders extend it):
1. ModelMix (Corvid.Models or Corvid.Abstractions as fits the architecture): slot name → model id, optional ladder per slot, and which slots may escalate; parse/validate against the ModelCatalogue (availability via Catalogue.Check) and Corvid:Agents:Models when set; SafeText on ids shown back; a JSON shape usable in a tool argument and in corvid.json.
2. SessionModelSlots with a per-session default: constructed from a mix (or the registry), CompleteTurnAsync / de-escalation returning to the session default, a SetSessionDefaultAsync (the sticky "this session" switch, logged as ModelSwitched with a reason such as "session"), the pin kept distinct; no behaviour change for sessions that set nothing except that the person's own /model switch is now sticky (update the tests that asserted revert-at-turn-end and say so).
3. A per-session decider: the decider slot resolved per session (a per-session DecisionFabric or a decider override in TurnServices), so a mix's decider entry or a session switch takes effect; the shared fabric stays the default; calibration data still recorded.
4. Child sessions in Corvid.Host: CorvidRuntime.OpenChildSession / RunChildAsync generalising TurnJobAgent: a fresh log session linked to its parent (new events, e.g. ChildSpawned/ChildFinished on the parent and ChildOf on the child, additive), a goal, an explicit toolset (ToolFilter or a sub-registry) with ask_user, memory writes, send and cron removed, the parent's taint inherited, the parent's approval mode as a ceiling and approvals routed to the parent's prompter, a ModelMix applied through the egress gate per binding (PolicyFor; a tainted parent cannot bind a cloud model the gate would refuse), a budget (MaxSteps, tokens, wall time) and DevicePriority.Subagent for in-process models; isTainted/ProjectionPrivacyGate seeing child sessions; the driver lock taken for the child.
5. Session-level API for builders: CorvidSession.SetModelAsync(slot, model, scope: Session|Pin|Turn) and runtime/default changes kept separate (registry default vs session default), with an update on the host session stream (a ModelChanged stream update; the engine maps it to an App.Core update — add an additive App.Core record if needed and document it in *Updates.cs).

Tests for each (kernel/host). Then build, run the checker and all listed tests, commit. Return the STEP report; notes_for_next must say exactly how builders use the mix, the session default, the per-session decider, OpenChildSession/RunChildAsync and the new events/updates, and which files are hotspots.`, { label: 'foundation', phase: 'Foundation', schema: STEP, isolation: 'worktree' }))
if (!foundation || foundation.status === 'blocked' || !foundation.commit) return { stoppedAt: 'foundation', foundation }

const GROUPS = [
  { key: 'switch', areas: 'model switching at any time',
    detail: `On the foundation's session default and per-session decider: /model in corvid chat, ACP and the app switches this session (sticky) with --pin and a "default for new sessions" option (writing corvid.json through the config writer, as corvid model switch does); corvid chat accepts /model while a turn runs (it reads no input during a turn today: add a way, e.g. a key or reading lines concurrently, without breaking the CLI's rendering); ACP session/set_model (or the ACP-standard equivalent; check docs) and the available-models list; the app: the Models page and Alt+P palette offer "this session" vs "default for new sessions", the session header shows the generator and decider live, a switch mid-turn says "takes effect at the next step", a transcript row when the model changes (live and replayed from ModelSwitched), the model per step on the step update and tool cards ("generated by …"), the decider switchable per session; escalation and privacy retreat shown as such. Replay derives the model per step from ModelSwitched events.` },
  { key: 'delegate', areas: 'subagents: delegate_task with a model mix',
    detail: `The delegate_task kernel tool (like AskUserTool): goal, toolset (explicit list or named preset; never wider than the parent's), model mix (the foundation's ModelMix), budget, optional output JSON schema validated on return (one repair attempt, then a failed result); runs children through the foundation's RunChildAsync, in parallel up to 10 per session and depth 1 (a child has no delegate_task), with a stall monitor using the decider's stuck question; the result returned to the parent as the tool result (bounded, scrubbed, the child's taint propagated); spawning follows the approval mode with a card that shows goal, toolset, budget and the mix (revealed). Feed FabricConditions.GeneratorSupportsDelegation so the fabric's delegate question is asked when it applies. Surfaces: the app's Board shows children as a tree under their parent (ui.md §4.13, F18) with goal, toolset, budget, model mix, status and output, and the session header lists children; a child session opens read-only with its parent linked; /agents in corvid chat (list, show, stop a child) and /delegate to start one with a mix the person gives; ACP updates for child start/finish (as tool call updates if ACP has no better shape); the gateway refuses delegate_task for guests. Interrupting the parent stops its children.` },
  { key: 'fork', areas: 'G-15: forking at a step boundary while a turn runs',
    detail: `Fork a session at a step checkpoint while its turn is running, without waiting for the turn to end (today ForkSessionAsync waits under RunExclusiveAsync): the fork copies the log through the last completed step, its own actor and driver lock, the parent's taint, and its model slots as the parent's session default (or a mix the person picks); the running turn continues unaffected. Surfaces: the app's Aside / Fork… while a turn runs (ui.md asides mid-turn), corvid chat /fork during a turn, ACP if it has a fork method. Tests that the running turn's log is untouched and the fork replays to the copied point.` },
  { key: 'leftovers', areas: 'A4 leftovers',
    detail: `From docs/plan/ui-handoff.md §1.5 Left: (1) token use in the log (a per-generator-call usage event or field, replay shows tokens and the context gauge), (2) a question that blocks mid-turn: a question router beside the approval router so ask_user can wait inside a turn where the plan wants it (keep the current end-the-turn behaviour where it is right; say which), (3) ACP tail and question notifications, (4) Sandbox:LauncherPath naming the real corvid-sandbox breaking every shell call (state protection denying the launcher's own path; stage 2 re-exec inside bubblewrap) and the app's build output lacking corvid-sandbox, (5) GLiNER2 redaction far too eager under Privacy:LocalServer/Cloud Redact (JSON keys, "status", the system prompt replaced): restrict what is scanned (the person's and tools' free text, not structure or the system prompt) and raise thresholds; tests with realistic prompts, (6) text typed before a new session has opened is lost, (7) a session's first opening landing mid-transcript. Leave the Board's Take over and drivers/ lock files as they are.` },
]

phase('Build')
const builderPrompt = (g, i) => `You are one of four builders of phase A5 of the Corvid app and engine. Your group: ${g.areas}.

${SETUP} First run \`git switch -c a5/${g.key} ${foundation.commit}\` there (the foundation's commit), or \`git switch a5/${g.key}\` if that branch exists. Display :${232 + i}; state dir ${TMP}/harness-a5-${g.key}.

${RULES}

The foundation's report (read its code too):
${foundation.notes_for_next}
Foundation gaps and host changes: ${JSON.stringify({ gaps: foundation.gaps, host: foundation.host_changes })}

Build: ${g.detail}

Other groups at the same time: switch (model switching surfaces), delegate (delegate_task and subagent surfaces), fork (G-15), leftovers (token use, question router, ACP notifications, sandbox launcher, redaction, two app items). Shared hotspots — TurnRunner.cs, SessionActor.cs, CorvidRuntime.cs, SessionModelSlots.cs, the session stream (Corvid.Host/Sessions), SessionUpdateTranslator, EngineSessionService, CoreCommands.cs, the Board view models: keep edits there additive and small, and list them. Do not edit another group's items.

Tests for every feature that fail without it. Then run the app (engine default) in a temporary home with the scripted generator (and a second scripted server on another port standing in for another or cloud-class model), drive your features on the running app and in corvid chat, screenshot each, read the app log; confirm --demo still works.

Return the STEP report.`

const built = await parallel(GROUPS.map((g, i) => () => slot(() =>
  agent(builderPrompt(g, i), { label: `build ${g.key}`, phase: 'Build', schema: STEP, isolation: 'worktree' }))))
const done = built.map((b, i) => ({ g: GROUPS[i], b })).filter(x => x.b && x.b.status !== 'blocked' && x.b.commit)
const missing = GROUPS.filter((g, i) => !(built[i] && built[i].status !== 'blocked' && built[i].commit)).map(g => g.key)
if (missing.length) log(`Not integrated (blocked or no commit): ${missing.join(', ')}`)
if (!done.length) return { stoppedAt: 'build', foundation, built }

phase('Integrate')
const integrated = await slot(() => agent(`You are the integrator for phase A5.

${SETUP} First run \`git switch -c a5/int ${foundation.commit}\` there (or \`git switch a5/int\` if it exists). Display :237, state dir ${TMP}/harness-a5-int.

${RULES}

Merge these branches one at a time with \`git merge --no-ff <branch>\`, in this order (switch, delegate, fork, leftovers where present), building and running every test project after each:
${done.map(x => `- a5/${x.g.key} (${x.g.areas}), commit ${x.b.commit}. Host changes: ${JSON.stringify(x.b.host_changes)}. Shared: ${JSON.stringify(x.b.shared_changes)}. Notes: ${x.b.notes_for_next}`).join('\n')}
Resolve conflicts keeping both sides' work. Add tests where the groups meet (a child on a switched model; a fork of a session with a session default; a delegate inside a forked session; token use for children). Update docs/plan/ui-handoff.md with a §1.6 for A5 (what was built, decisions, still refused, left) and its status row, and docs/plan/ui.md / corvid-agent.md where a decision changes them. Run the app once on the engine and once with --demo.

When you have committed, run \`git switch --detach\` so a5/int is free.

Return the STEP report.`, { label: 'integrate', phase: 'Integrate', schema: STEP, isolation: 'worktree' }))
if (!integrated || integrated.status === 'blocked') return { stoppedAt: 'integrate', foundation, built, integrated }

// Revised after launch (the user's request: less time in the agent loops): the end-to-end pass and the first verify
// round run together, at most two verify rounds and one fix round, and every agent here builds and tests incrementally.
const FAST = `Build discipline (time matters): while iterating, build only the project you changed and the test project that covers it (\`~/.dotnet/dotnet build <project>.csproj -c Release --no-restore\`), and run only the affected test classes (\`<tests exe> -class "<Namespace.Class>"\` or \`-method\`). Do not rebuild the solution after each edit, and never rebuild just to read code. Run the whole-solution Release build, the checker and the full test set once, at the end. Keep app runs to the flows you need, and take a screenshot only when it is evidence.`

phase('End to end')
phase('Verify')
const e2ePrompt = `You run phase A5's end-to-end pass.

${SETUP} Run \`git switch a5/int\` there and work on that branch; when you have committed, run \`git switch --detach\`. Display :238, state dir ${TMP}/harness-a5-e2e.

${RULES}

${FAST}

In temporary homes (two scripted generators on different ports — one standing in for a local model, one configured as a cloud-class remote; real local models from the Starling cache read-only where judgement helps; never touch a shared model server): on the running app and in corvid chat, walk: switching the generator for this session between turns and mid-turn (takes effect at the next step; the transcript row; the header), "default for new sessions", switching the decider per session, a pinned vs a sticky switch through escalation; the agent spawning two subagents in parallel with different model mixes (one local, one cloud-class) and their results coming back; the approval card for spawning under Manual mode; a tainted parent whose child asks for the cloud model being refused by the egress gate; a child's output validated against a schema; the Board's child tree; /agents and /delegate in the CLI; interrupting the parent stops its children; a fork made mid-turn; token use shown after a restart; the question router; the sandbox launcher path; redaction on a realistic prompt. Fix small breakage (commit it); list the rest.

Return the STEP report: per flow, works / refused / broken.`

const verifyPrompt = (round, extra) => `You are the independent verifier for phase A5, round ${round}. Do NOT edit, commit or push.

${SETUP} Run \`git switch --detach a5/int\` there. Display :239, state dir ${TMP}/harness-a5-verify-${round}.

${RULES}

${FAST} As a verifier you build once and run the full test set once; read code for everything else.

Check: the decisions above are implemented as stated; model switching semantics (sticky session default, pin, escalation, de-escalation, mid-turn at the next step, the decider per session, defaults for new sessions) in the kernel and on every surface; delegate_task's limits (10, depth 1, budget, toolset never wider than the parent's, removed tools, stall monitor) cannot be exceeded by the model; children inherit taint; a cloud binding in a mix always passes the egress gate and a tainted parent's child cannot reach cloud unless egress allows it; the spawn follows the approval mode; output schemas validated; results bounded and scrubbed; interrupting stops children; G-15 forks leave the running turn untouched; token use, the question router, ACP notifications, the sandbox launcher and redaction fixes; the update contract and replay parity for the new events; the CLI, ACP and gateway still work; no write to the real home; threading and disposal; tests meaningful. Run the app on the engine once for the flows you doubt.

Builders' remaining gaps (judge each):
${JSON.stringify(done.map(x => ({ group: x.g.key, gaps: x.b.gaps })))}
${extra}

Severity: blocker (crash, failing build/check/test, data loss, a write to the real home, an approval/taint/egress bypass, a child exceeding its limits), major (a feature wrong or faked, a flow broken, the CLI/ACP broken, an unhandled exception, an undisposed subscription), minor. pass is true only when no blocker or major remains. Evidence and steps for each.`

const fixPrompt = (round, defects, e2eBroken) => `You are fix round ${round} for phase A5. ${SETUP} Run \`git switch a5/int\` there and work on that branch; when you have committed, run \`git switch --detach\`. Display :240, state dir ${TMP}/harness-a5-fix-${round}.

${RULES}

${FAST}

Fix every blocker and major defect below and what the end-to-end pass reported broken, and minor ones that are quick and safe. Rerun the check that showed each (the narrowest test or flow), then once the whole-solution build, checker and tests, and commit.

Verifier's defects:
${JSON.stringify(defects, null, 2)}

End-to-end pass (its broken items and gaps):
${e2eBroken}

Return the STEP report (fixed by id, left and why).`

const [e2e, first] = await parallel([
  () => slot(() => agent(e2ePrompt, { label: 'end to end', phase: 'End to end', schema: STEP, isolation: 'worktree' })),
  () => slot(() => agent(verifyPrompt(1, 'The end-to-end pass runs at the same time as you; do not wait for it.'), { label: 'verify 1', phase: 'Verify', schema: DEFECTS, isolation: 'worktree' })),
])
const e2eBroken = e2e ? JSON.stringify({ summary: e2e.summary, gaps: e2e.gaps }) : 'did not report'
let report = first
let round = 1
const fixes = []
if (report && (!report.pass || (e2e && e2e.gaps.length))) {
  const fix = await slot(() => agent(fixPrompt(1, report.defects, e2eBroken), { label: 'fix 1', phase: 'Verify', schema: STEP, isolation: 'worktree' }))
  fixes.push(fix)
  round = 2
  report = await slot(() => agent(verifyPrompt(2, `Round 1's defects and the fix round's report: ${JSON.stringify({ defects: first.defects, fix: fix && { summary: fix.summary, gaps: fix.gaps } })}`), { label: 'verify 2', phase: 'Verify', schema: DEFECTS, isolation: 'worktree' }))
}
if (report && !report.pass) log(`Verifier still reports ${report.defects.filter(d => d.severity !== 'minor').length} blocker or major defects after ${round} rounds; they go to the PR review`)

return { foundation, built, missing, integrated, e2e, fixes, finalReport: report, rounds: round }
