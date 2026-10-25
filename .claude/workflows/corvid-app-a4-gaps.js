export const meta = {
  name: 'corvid-app-a4-gaps',
  description: 'Close the engine gaps behind the windowed Corvid app (K-2, K-3, K-6, G-2, G-6, G-7, G-9, G-12, G-14, parity), make --engine the default, verify end to end, fix',
  whenToUse: 'Phase A4 of the windowed Corvid app: the work items Corvid.App.Engine refuses after A3 (docs/plan/ui-handoff.md §1.4).',
  phases: [
    { title: 'Build', detail: 'five groups in parallel worktrees' },
    { title: 'Integrate', detail: 'merge, then make --engine the default' },
    { title: 'End to end', detail: 'every flow on the engine in temporary homes' },
    { title: 'Verify', detail: 'independent verifier, bounded fix rounds' },
  ],
}

// args: { maxConcurrent, base, shims, jobTmp }
const MAX = Math.max(1, (args && args.maxConcurrent) || 1)
let active = 0; const waiters = []
const slot = async fn => { while (active >= MAX) await new Promise(r => waiters.push(r)); active++; try { return await fn() } finally { active--; const w = waiters.shift(); if (w) w() } }
log(`maxConcurrent = ${MAX} (agent-budget said 5 at launch; held to 4 for free memory)`)

const BASE = args.base
const SHIMS = args.shims
const TMP = args.jobTmp
const SETUP = 'You start in a fresh git worktree of the repository made for you (your working directory; `pwd` shows it). You cannot work in any other worktree: the session isolates each agent to its own.'

const RULES = `Rules that apply to you:
- Memory: before every dotnet build or test run \`agent-budget level\`. If it prints critical, wait with \`agent-budget alert --from critical --timeout 300\` and check again. When it prints tight, pass -m:2 to dotnet. Use ~/.dotnet/dotnet.
- Git: never push, never force-push, never touch main or other agents' branches. Commit at every milestone (at least every 20 minutes of edits). Commit messages follow \`git log\` ("Kernel: <what> (K-3)", "Host: …", "App.Engine: … (A4 <group>)") with NO Claude attribution: no Co-Authored-By, no Claude-Session, no links, no "Generated with".
- Read first: docs/plan/ui-handoff.md (§1.4 is what A3 built, refused and left; §4 the session core), docs/plan/ui.md (§4 features, §5 flows, §8 architecture and the G-n/U-n tables), docs/plan/tui.md §9 (K-1..K-10) and the parts of docs/plan/corvid-agent.md your items touch, src/Corvid.App/README.md, and the XML docs on src/Corvid.App.Core/Sessions/*Updates.cs.
- Architecture (tools/Corvid.Architecture): Corvid.App.Engine may reference Corvid.Host, App.Core, Corvid.Mcp, the Models adapters and core projects, never Uno, Spectre.Console, Corvid.Cli, Corvid.Acp or Corvid.App; App.Core gains no references; Corvid.Host never references App projects. Logic shared by the CLI and the app lives in Corvid.Host (moved, not copied).
- The CLI is a first-class surface: a kernel or host feature you add should work for \`corvid chat\` too where the CLI has a place for it (say what you did for it), and must not break it. Keep tests/Corvid.Acp.Tests' exchanges passing unchanged unless an intended change is re-recorded (say so).
- Engine services implement the App.Core interfaces as the view models use them; change an interface only additively. Remove the refusal messages you replace; refusals that remain keep naming their work item.
- Security model (corvid-agent.md): approvals block the turn until answered and nothing is granted while SafeText.HasHidden; steering and answers are untrusted-free (they come from the person) but anything shown back from the model or tools goes through SafeText; taint, the sandbox and egress rules are unchanged unless your item is about them.
- Never touch the user's real Corvid home (~/.config/corvid, ~/.local/share/corvid, ~/.hermes, the real Vault, real chat-platform tokens). Tests use tests/Corvid.Host.Tests/Fakes.cs (TestHost) and the other fakes in temporary directories. When you run the app, use --engine with HOME, CORVID_CONFIG and XDG_* pointed at a temporary directory under ${TMP} (see ${TMP}/a3-e2e-scripts/run-app.sh and fakeoai.py for a scripted OpenAI-compatible generator).
- Untrusted text: SafeText.Clean for display, Reveal where a person approves; tests write hidden characters as \\u escapes. Engine callbacks marshal to the dispatcher; no UI-thread blocking or log writes; every task awaited or its failure shown; subscriptions disposed through AppComposition.Dispose.
- Tests: xunit.v3; TestContext.Current.CancellationToken; deterministic concurrency tests (gates, TaskCompletionSource), no sleeps where a TimeProvider exists. Each feature gets tests that fail without it.
- Looking at the app: stand-ins for Xvfb/xdotool are in ${SHIMS}. Run the harness as PATH=${SHIMS}:$PATH UI_HARNESS_STATE=<your state dir> CORVID_APP_RENDERER=software tools/ui-harness.sh <cmd> <display> ... with your own display and state dir. After launch, wait 3 s, resize, check with \`DISPLAY=:<n> xwininfo -id $(cat <state dir>/win-<n>.id)\`, resize one pixel wider and back, wait 1.5 s. Screenshots under ui-shots/ (git-ignored), viewed with Read. Never stop processes with pkill/pgrep -f. Stop your display when done (\`stop <n> --xvfb\`).
- Before you finish: whole solution \`~/.dotnet/dotnet build Corvid.slnx -c Release\` with no warnings, the checker (\`~/.dotnet/dotnet run --project tools/Corvid.Architecture -c Release --no-build -- .\`), and the tests of every project you touched plus App.Engine, App.Core, Architecture, Host, Acp, Kernel, Approvals, Cli (run the executables under tests/*/bin/Release/net10.0/ if \`dotnet test --project\` reports zero tests). Never claim a check you did not run.`

const STEP = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['done', 'partial', 'blocked'] },
    branch: { type: 'string' }, worktree: { type: 'string' }, commit: { type: 'string' },
    summary: { type: 'string' },
    host_changes: { type: 'array', items: { type: 'string' }, description: 'every change to Corvid.Host/Kernel/core projects and the CLI, with the work item' },
    shared_changes: { type: 'array', items: { type: 'string' }, description: 'every change to a file another group may also touch' },
    gaps: { type: 'array', items: { type: 'string' }, description: 'what is still refused or partial, with the work item and the message shown' },
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

const GROUPS = [
  { key: 'turn', areas: 'the running turn: steering, questions, output tail, progress',
    detail: `K-3 steering: a message taken by a running turn at its next step checkpoint (kernel TurnRunner/SessionActor, the host session, the update stream), with ISessionService.SteerAsync on the engine and Ctrl+Enter in the composer; the CLI's steer if tui.md gives it one. K-6 clarifying questions: a turn can ask the person a question and wait (a tool or turn result the kernel recognises), surfaced as QuestionAsked/QuestionAnswered updates with AnswerQuestionAsync, and the Board's attention item; a question never auto-answers. K-2 the output tail: streamed output of running tool calls (the sandboxed shell, web_fetch) on the stream as ToolCallChanged tails, bounded. Turn progress: TurnProgress with step n of m and the running tool. Keep the update order the contract documents; replay shows what the log can hold of these.` },
  { key: 'records', areas: 'titles, recaps, ui.db and the composer modes',
    detail: `G-6 (extends K-9): user titles and recaps as events in the log, RenameAsync on the engine, titles in the rail, header, Board and CLI session lists; recaps generated where the plan says (or on request) and shown. G-7: the ui.db conventions in Corvid.Host so the CLI and the app share drafts, pins, review points and app preferences (density, notifications, layout), replacing the "for this window only" refusals; safe concurrent access from both processes. The composer's # (stage a memory: through MemoryWriter, the review flow) and ! (run a shell command through the sandbox with the usual approval) modes that the placeholder advertises (ui.md §4.1, tui.md §4.1), in the app and, where tui.md says, the CLI.` },
  { key: 'egress', areas: 'the egress ledger and the approval assessment',
    detail: `G-9: render the redacted request of a generator call on demand (what left the machine, after redaction), recorded or reconstructable from the log, with ListEgressAsync on the engine for Audit's egress ledger (F8) and the Models privacy panel's counts; never show a secret or an unredacted value. G-2: the approval assessment on the stream for calls that ran unasked ("why it ran unasked" on every card: the rule, mode or calibrated level that allowed it), filling ApprovalRequested/ToolCall assessment fields the cards already draw; replay shows it from the log.` },
  { key: 'hosting', areas: 'gateway and scheduler hosting, the single-driver lock',
    detail: `G-14 with U-8: the app hosts the gateway and the scheduler in-process when it is the engine, under the existing one-lock rule (only one process hosts them; the CLI's corvid gateway and the app agree), with a status API; the Gateway page's live actions (pairing, revoke) and its status work, the per-person guest allowlist decided (build it in the engine if modest, otherwise keep the honest refusal); the scheduler's Since and job runs that report back. Real chat platforms are never contacted in tests or runs: use the fake adapters in tests/Corvid.Gateway.Tests. G-12: a single-driver lock per session and read-only tailing from another process: the app opening a session another process drives shows it read-only and tails it; Rewind's restore and Review's revert refuse while another process drives; the CLI honours the same lock.` },
  { key: 'parity', areas: 'live and replay parity, layout, and A3 leftovers',
    detail: `Replay matches live as far as the log allows: the generator and decisions per replayed turn, an interrupted turn or one cut by a rollback replayed as interrupted (not "failed"), the taint row, compaction markers, the status bar's generator before a turn runs. Layout with engine-length text: Models' slots table at 1440, Rewind's checkpoint ids, Audit's raw JSON, Review's "Checkpoint" label. First run restarts (or rebinds) the runtime after its downloads so Sessions works without restarting the app. Settings and Models changes that can apply live do (say which). A3's leftovers from §1.4: the interrupt race with another surface's post; First run telling when an environment variable or --set overrides a choice; a read-only config/approvals directory reported as such (not "another process is writing"); a rule write waiting on another process's lock not stalling approval checks; Memory Reject keeping its reason's focus; the Hermes import's staging session hidden from the rail; the round-3 minors (the turn strip offering refused actions, a new session's rail entry, Send with no session).` },
]

phase('Build')
const builderPrompt = (g, i) => `You are one of five builders of phase A4 of the windowed Corvid app: closing the gaps Corvid.App.Engine still refuses. Your group: ${g.areas}.

${SETUP} First run \`git switch -c a4/${g.key} ${BASE}\` there, or \`git switch a4/${g.key}\` if that branch exists (a resumed run). Display :${201 + i}; state dir ${TMP}/harness-a4-${g.key}.

${RULES}

Build: ${g.detail}

Other groups at the same time: turn (K-3, K-6, K-2, progress: kernel turn loop, session stream), records (G-6, G-7, composer modes), egress (G-9, G-2), hosting (G-14, G-12), parity (replay, layout, A3 leftovers). The session stream (src/Corvid.Host/Sessions), the translator and EngineSessionService are shared hotspots: keep your edits there additive and small, and list them. Do not edit another group's items.

Tests for every feature (kernel/host tests for the engine side, App.Engine tests for the services, App.Core tests if a view model changes). Then run the app with --engine in a temporary home with the scripted generator (and real models from the Starling cache read-only if a flow needs them, as A3's end-to-end did), drive your features on the running app, screenshot each, and read the app log; confirm --demo still works.

Return the STEP report.`

const built = await parallel(GROUPS.map((g, i) => () => slot(() =>
  agent(builderPrompt(g, i), { label: `build ${g.key}`, phase: 'Build', schema: STEP, isolation: 'worktree' }))))
const done = built.map((b, i) => ({ g: GROUPS[i], b })).filter(x => x.b && x.b.status !== 'blocked' && x.b.commit)
const missing = GROUPS.filter((g, i) => !(built[i] && built[i].status !== 'blocked' && built[i].commit)).map(g => g.key)
if (missing.length) log(`Not integrated (blocked or no commit): ${missing.join(', ')}`)
if (!done.length) return { stoppedAt: 'build', built }

phase('Integrate')
const integrated = await slot(() => agent(`You are the integrator for phase A4 of the windowed Corvid app.

${SETUP} First run \`git switch -c a4/gaps-int ${BASE}\` there (or \`git switch a4/gaps-int\` if it exists). Display :208, state dir ${TMP}/harness-a4-int.

${RULES}

1. Merge these branches one at a time with \`git merge --no-ff <branch>\`, building and running the tests after each:
${done.map(x => `- a4/${x.g.key} (${x.g.areas}), commit ${x.b.commit}. Host changes: ${JSON.stringify(x.b.host_changes)}. Shared: ${JSON.stringify(x.b.shared_changes)}. Notes: ${x.b.notes_for_next}`).join('\n')}
Resolve conflicts keeping both sides' work (the session stream, the translator, EngineSessionService, CorvidRuntime, the seam). If a branch cannot be merged without rewriting it, report it and continue.
2. Make --engine the default: AppOptions.Demo defaults to false, --demo opts in, the usage text, README and docs/plan/ui-handoff.md's commands updated; tools/ui-harness.sh and the mockup comparisons that need the demo pass --demo explicitly (check every script and doc that launches the app). App.Core tests that relied on the default pass --demo or set it.
3. Run the app with no arguments in a temporary home (it opens on the engine; First run when models are missing) and with --demo once.

When you have committed, run \`git switch --detach\` so a4/gaps-int is free.

Return the STEP report.`, { label: 'integrate', phase: 'Integrate', schema: STEP, isolation: 'worktree' }))
if (!integrated || integrated.status === 'blocked') return { stoppedAt: 'integrate', built, integrated }

phase('End to end')
const e2e = await slot(() => agent(`You run phase A4's end-to-end pass of the windowed Corvid app, now on the engine by default.

${SETUP} Run \`git switch a4/gaps-int\` there and work on that branch; when you have committed, run \`git switch --detach\`. Display :209, state dir ${TMP}/harness-a4-e2e.

${RULES}

In temporary homes (the scripted generator; real models from the Starling cache read-only for the flows that need judgement; never start, stop or reconfigure a shared model server; fake gateway adapters only; no real Vault): walk every flow of ui.md §5 on the running app, with the new features first: steering a running turn, a clarifying question answered from the card and from the Board, a running shell call's output tail and the step count, renaming a session and its recap, a draft and a pin surviving an app restart and seen by the CLI, # and ! in the composer, the egress ledger and "why it ran unasked" on a card, the gateway hosted in the app with a fake adapter (pair, revoke, status) and a scheduled job run reporting back, a session driven by \`corvid chat\` shown read-only and tailed in the app, replay equal to live after a restart, First run's downloads leading straight into a working session. Screenshot each, read the app log. Fix small breakage (commit it); list the rest.

Return the STEP report: per flow, works / refused with work item / broken.`, { label: 'end to end', phase: 'End to end', schema: STEP, isolation: 'worktree' }))

phase('Verify')
const verifyPrompt = round => `You are the independent verifier for phase A4 of the windowed Corvid app, round ${round}. Do NOT edit, commit or push.

${SETUP} Run \`git switch --detach a4/gaps-int\` there. Display :210, state dir ${TMP}/harness-a4-verify-${round}.

${RULES}

Check: the new kernel/host features are correct and safe (steering cannot inject between a call and its approval or bypass taint; a question cannot be answered by the model or auto-answered; output tails are bounded and sanitized; titles and recaps are untrusted where the model writes them; ui.db is safe with the CLI and the app at once; # and ! go through memory review and the sandbox with approval; the egress ledger never shows a secret or an unredacted value; the assessment shown matches what allowed the call; one process hosts the gateway and the scheduler; the single-driver lock holds across processes and read-only tailing cannot write); the update contract's order and replay parity; the CLI still works (\`corvid chat\` with the scripted generator in a temporary home); --engine is the default and --demo still opens the scripted scenario; no write to the real home; threading and disposal; tests meaningful. Run the app on the engine and with --demo; run the whole-solution build, checker and all tests.

Builders' and end-to-end's remaining gaps (judge each refusal):
${JSON.stringify(done.map(x => ({ group: x.g.key, gaps: x.b.gaps })))}
End to end: ${e2e ? JSON.stringify({ summary: e2e.summary, gaps: e2e.gaps }) : 'did not report'}

Severity: blocker (crash, failing build/check/test, data loss, a write to the real home, an approval or taint bypass, a secret shown), major (a feature wrong or faked, a flow broken on the engine, the CLI broken, an undisposed subscription, an unhandled exception), minor. pass is true only when no blocker or major remains. Evidence and steps for each.`

const fixPrompt = (round, defects) => `You are fix round ${round} for phase A4 of the windowed Corvid app. ${SETUP} Run \`git switch a4/gaps-int\` there and work on that branch; when you have committed, run \`git switch --detach\`. Display :211, state dir ${TMP}/harness-a4-fix-${round}.

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

return { built, missing, integrated, e2e, fixes, finalReport: report, rounds: round }
