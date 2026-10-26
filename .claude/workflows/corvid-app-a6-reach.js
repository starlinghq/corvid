export const meta = {
  name: 'corvid-app-a6-reach',
  description: 'Attached mode, workspaces and worktrees, images, packaging, search and the A5 leftovers; integrate, verify end to end, fix',
  whenToUse: 'Phase A6 of the windowed Corvid app and engine: G-1, G-4, K-8, U-7/U-9, G-5 and what docs/plan/ui-handoff.md §1.6 leaves.',
  phases: [
    { title: 'Build', detail: 'four groups in parallel worktrees' },
    { title: 'Integrate', detail: 'merge the groups one at a time' },
    { title: 'End to end', detail: 'the new flows on the app, the CLI and an attached client, beside verify round 1' },
    { title: 'Verify', detail: 'at most two rounds and one fix round' },
  ],
}

// args: { maxConcurrent, base, shims, jobTmp }
// Lean by design (the user found the A3/A4 loops far too slow): as many groups as slots, incremental builds while
// iterating, the end-to-end pass beside the first verify round, and at most two verify rounds.
const MAX = Math.max(1, (args && args.maxConcurrent) || 1)
let active = 0; const waiters = []
const slot = async fn => { while (active >= MAX) await new Promise(r => waiters.push(r)); active++; try { return await fn() } finally { active--; const w = waiters.shift(); if (w) w() } }
log(`maxConcurrent = ${MAX} (from agent-budget at launch)`)

const BASE = args.base
const SHIMS = args.shims
const TMP = args.jobTmp
const SETUP = 'You start in a fresh git worktree of the repository made for you (your working directory; `pwd` shows it). You cannot work in any other worktree: the session isolates each agent to its own.'

const RULES = `Rules that apply to you:
- Memory: before every dotnet build or test run \`agent-budget level\`. If it prints critical, wait with \`agent-budget alert --from critical --timeout 300\` and check again. When it prints tight, pass -m:2 to dotnet. Use ~/.dotnet/dotnet. Load real models only when a flow needs judgement, and stop such processes (by the PID you started) as soon as you are done.
- Build discipline (time matters): while iterating, build only the project you changed and its test project (\`~/.dotnet/dotnet build <project>.csproj -c Release --no-restore\`) and run only the affected test classes (\`<tests exe> -class "<Namespace.Class>"\`). Never rebuild just to read code. Run the whole-solution Release build, the checker and the full test set once, at the end. Take a screenshot only when it is evidence.
- Git: never push, never force-push, never touch main or other agents' branches. Commit at every milestone. Commit messages follow \`git log\` ("Host: <what> (A6 <group>)", "App.Engine: …") with NO Claude attribution: no Co-Authored-By, no Claude-Session, no links, no "Generated with".
- Read first: docs/plan/ui-handoff.md (§1.4 to §1.6: what A3–A5 built, refused and left; §4), docs/plan/ui.md (§4, §5 flows incl. F19, §8 architecture, the G-n and U-n tables), docs/plan/corvid-agent.md (the security model: approvals, taint, sandbox, Vault, egress, gateway pairing), docs/plan/tui.md §9, docs/plan/vault.md where tokens are involved, src/Corvid.App/README.md, the XML docs on src/Corvid.App.Core/Sessions/*Updates.cs.
- Architecture (tools/Corvid.Architecture): Corvid.App.Engine may reference Corvid.Host, App.Core, Corvid.Mcp, the Models adapters and core projects; App.Core gains no references; Corvid.Host never references App projects. A new project (a protocol or client library) goes in Corvid.slnx with the checker's tables updated and justified.
- The CLI, ACP and the gateway are first-class surfaces; nothing breaks them. Keep tests/Corvid.Acp.Tests' exchanges passing unchanged unless an intended change is re-recorded (say so). If CI pins a test count (.github/workflows/ci.yml grep -q "Total: N") for a suite you change, update it.
- Security: approvals block until answered; nothing is granted while SafeText.HasHidden; taint and egress are never weakened; anything arriving over a socket, from a file, an image or a model is untrusted. A listening socket binds to loopback or a Unix socket by default, requires a token, and never exposes the owner's approvals to an unpaired client.
- Never touch the user's real Corvid home (~/.config/corvid, ~/.local/share/corvid, ~/.hermes, the real Vault, real chat tokens) and never install anything system-wide. Tests use tests/Corvid.Host.Tests/Fakes.cs (TestHost, FakeModels, FakeGenerator) in temporary directories; runs point HOME, CORVID_CONFIG and XDG_* at a temporary directory under ${TMP} (see ${TMP}/a3-e2e-scripts/run-app.sh and fakeoai.py). Never start, stop or reconfigure a shared model server. Never kill processes by name (no pkill/pgrep -f); kill only PIDs you started.
- Untrusted text: SafeText.Clean for display, Reveal where a person approves; tests write hidden characters as \\u escapes. Engine callbacks marshal to the dispatcher; no UI-thread blocking; every task awaited or its failure shown; subscriptions disposed.
- Tests: xunit.v3; TestContext.Current.CancellationToken; deterministic concurrency tests (gates, TaskCompletionSource, pause hooks), no sleeps where a TimeProvider exists. Each feature gets tests that fail without it.
- Looking at the app: stand-ins for Xvfb/xdotool are in ${SHIMS}. Run the harness as PATH=${SHIMS}:$PATH UI_HARNESS_STATE=<your state dir> CORVID_APP_RENDERER=software tools/ui-harness.sh <cmd> <display> ... (the engine is the default; the demo needs --demo). After launch, wait 3 s, resize, check with xwininfo, resize one pixel wider and back, wait 1.5 s. Screenshots under ui-shots/ (git-ignored). Stop your display when done (\`stop <n> --xvfb\`).
- Never claim a check you did not run.`

const STEP = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['done', 'partial', 'blocked'] },
    branch: { type: 'string' }, worktree: { type: 'string' }, commit: { type: 'string' },
    summary: { type: 'string' },
    host_changes: { type: 'array', items: { type: 'string' } },
    shared_changes: { type: 'array', items: { type: 'string' } },
    gaps: { type: 'array', items: { type: 'string' } },
    verified: { type: 'array', items: { type: 'string' } },
    screenshots: { type: 'array', items: { type: 'string' } },
    notes_for_next: { type: 'string' },
  },
  required: ['status', 'branch', 'worktree', 'commit', 'summary', 'host_changes', 'shared_changes', 'gaps', 'verified', 'screenshots', 'notes_for_next'],
}

const DEFECTS = {
  type: 'object',
  properties: {
    pass: { type: 'boolean' },
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
  { key: 'attached', areas: 'attached mode (G-1)',
    detail: `The session update stream and session methods over a local socket or WebSocket (ui.md §8 "the update contract … for attached mode: ACP's session/* plus _corvid/*; a Vault-held token as corvid serve uses; owner pairing"): a host-side server in Corvid.Host (or a small protocol project) that serves SessionStreamUpdate and the session methods the app uses, a client implementing the App.Core service interfaces over it (Corvid.App.Engine or a new client project the desktop and WebAssembly heads can both use), \`corvid serve\` hosting it (reusing ACP's shapes where they fit), and the app's \`--attach <address>\` (or a First run choice) running against it with the same pages. Tokens: generated and held as the plan says (Vault when present, a 0600 file otherwise; say which), owner pairing for a second device, refusal without a token, loopback/Unix socket by default. The WebAssembly head attaches to it in a browser (build it; run it if wasm-tools is installed, else say so). Approvals and questions answered from an attached client reach the engine's turn; a disconnect never leaves a turn waiting forever (it falls back to the deadline).` },
  { key: 'workspaces', areas: 'workspaces per session and worktrees (G-4)',
    detail: `A workspace per session (the runtime today has one workspace per window/runtime instance): a session records its workspace, the sandbox, checkpoints, file tools and Review/Rewind use it, and the app shows several workspaces in one window (the rail groups sessions by workspace; a new session picks its workspace). Worktree creation: create a git worktree for a session (and optionally for a subagent from delegate_task: a "worktree" isolation option, so children that write do not collide), list and remove them, with the person's approval since it writes outside the workspace; never touch the person's main checkout's branch state. CLI: /workspace and a --workspace for corvid chat. Tests with real git in temporary directories.` },
  { key: 'media', areas: 'images (K-8, F19)',
    detail: `Image content in envelopes and the generator contract: a message can carry images (paste, drop, a path), sent to a generator whose slot or model supports vision (the vision slot or a vision-capable generator; others refuse with a clear message or describe through the vision slot, per the plan), recorded in the log (content-addressed, sized, never inlined huge; the event stays readable by older binaries if possible or the one-way step is documented), shown in the transcript as chips and inline images (bounded), replayed. Images are untrusted content: they taint like outside content when they come from outside, are never sent to a cloud model unless egress allows images, and are stripped of metadata (EXIF location) before leaving the machine. App: paste/drop/attach button; CLI: an /image <path> or @path syntax; ACP: image content blocks. Tests with small fixture images.` },
  { key: 'ship', areas: 'packaging, search and the A5 leftovers',
    detail: `U-7/U-9: the app's binary named corvid-app, install.sh --app installing it (to the user's prefix the script already uses, never system-wide in tests) with a .desktop file and an icon (a simple Corvid mark; also the window icon), a tarball target, and the doctor/README saying how to install; test install.sh into a temporary prefix. G-5: search across sessions through the episodic index (the palette's Ctrl+Shift+F, /search in the CLI, results opening the session at the matched turn), replacing the "comes with G-5" refusal in prompt history. A5 leftovers from ui-handoff.md §1.6: plain-word passwords in prose ("my wifi password is sunshinerainbow") — teach Corvid.Secrets' credential detector a labelled-prose rule (a credential noun followed by is/:/= and a single token) with tests that it does not over-match ordinary prose; a reset on a pinned slot logged with its pin; an abandoned child keeping its concurrency place until its tool stops; the tainted child's answer enclosed (start and end markers that the child's text cannot forge) rather than prefixed; and any other §1.6 "left" item that is small.` },
]

phase('Build')
const builderPrompt = (g, i) => `You are one of four builders of phase A6 of the Corvid app and engine. Your group: ${g.areas}.

${SETUP} First run \`git switch -c a6/${g.key} ${BASE}\` there, or \`git switch a6/${g.key}\` if that branch exists (a resumed run). Display :${251 + i}; state dir ${TMP}/harness-a6-${g.key}.

${RULES}

Build: ${g.detail}

Other groups at the same time: attached (G-1), workspaces (G-4), media (K-8), ship (packaging, G-5 search, A5 leftovers). Shared hotspots — CorvidRuntime.cs, TurnRunner.cs, SessionEvent.cs, the session stream (Corvid.Host/Sessions), SessionUpdateTranslator, EngineSessionService, the composer, Corvid.slnx: keep edits there additive and small, and list them. Do not edit another group's items.

Tests for every feature that fail without it. Then drive your features once on the running app (engine default, temporary home, the scripted generator) and in corvid chat; read the logs; confirm --demo still opens.

Return the STEP report.`

const built = await parallel(GROUPS.map((g, i) => () => slot(() =>
  agent(builderPrompt(g, i), { label: `build ${g.key}`, phase: 'Build', schema: STEP, isolation: 'worktree' }))))
const done = built.map((b, i) => ({ g: GROUPS[i], b })).filter(x => x.b && x.b.status !== 'blocked' && x.b.commit)
const missing = GROUPS.filter((g, i) => !(built[i] && built[i].status !== 'blocked' && built[i].commit)).map(g => g.key)
if (missing.length) log(`Not integrated (blocked or no commit): ${missing.join(', ')}`)
if (!done.length) return { stoppedAt: 'build', built }

phase('Integrate')
const integrated = await slot(() => agent(`You are the integrator for phase A6.

${SETUP} First run \`git switch -c a6/int ${BASE}\` there (or \`git switch a6/int\` if it exists). Display :256, state dir ${TMP}/harness-a6-int.

${RULES}

Merge these branches one at a time with \`git merge --no-ff <branch>\` (attached, workspaces, media, ship where present). After each merge build the projects the merge touched and run their tests; after the last, the whole solution, the checker and every test project:
${done.map(x => `- a6/${x.g.key} (${x.g.areas}), commit ${x.b.commit}. Host changes: ${JSON.stringify(x.b.host_changes)}. Shared: ${JSON.stringify(x.b.shared_changes)}. Notes: ${x.b.notes_for_next}`).join('\n')}
Resolve conflicts keeping both sides' work. Add tests where the groups meet (an attached client sending an image; a session in a second workspace over attached mode; search finding a session in another workspace; a subagent in its own worktree). Write docs/plan/ui-handoff.md §1.7 for A6 (built, decisions, still refused, left) and its status row, and update docs/plan/ui.md's G-n/U-n rows that are now done. Run the app once on the engine and once with --demo.

When you have committed, run \`git switch --detach\` so a6/int is free.

Return the STEP report.`, { label: 'integrate', phase: 'Integrate', schema: STEP, isolation: 'worktree' }))
if (!integrated || integrated.status === 'blocked') return { stoppedAt: 'integrate', built, integrated }

const e2ePrompt = `You run phase A6's end-to-end pass.

${SETUP} Run \`git switch a6/int\` there and work on that branch; when you have committed, run \`git switch --detach\`. Display :257, state dir ${TMP}/harness-a6-e2e.

${RULES}

In temporary homes (the scripted generator, a vision-capable scripted server for images; real models only where judgement helps): \`corvid serve\` with a token and the app attached to it (a turn, an approval answered from the attached app, a question, a disconnect mid-approval, a refused tokenless client, owner pairing of a second client), the WebAssembly head attached if wasm-tools is installed; two workspaces in one window, a session in each, a worktree created for a session and for a subagent and removed; an image pasted in the app and given in the CLI, sent to the vision-capable model, refused for a model without vision, shown on replay, refused to a cloud model by egress when tainted; install.sh --app into a temporary prefix and the installed corvid-app launching; search across sessions from the palette and /search; the plain-word password redacted; the enclosed tainted child answer. Fix small breakage (commit it); list the rest.

Return the STEP report: per flow, works / refused / broken.`

const verifyPrompt = (round, extra) => `You are the independent verifier for phase A6, round ${round}. Do NOT edit, commit or push.

${SETUP} Run \`git switch --detach a6/int\` there. Display :258, state dir ${TMP}/harness-a6-verify-${round}.

${RULES}

As a verifier you build once and run the full test set once; read code for everything else, and run the app only for flows you doubt.

Check: attached mode's security (tokens required, loopback/Unix socket by default, pairing needs the owner, an attached client cannot approve for a session it does not own, no secret or token in logs, a disconnect never leaves a turn waiting), its parity with in-process mode on every page, and that the CLI/ACP/gateway still work; workspaces (every tool, the sandbox, checkpoints, Review/Rewind use the session's workspace; nothing writes into another workspace; worktree creation and removal never touch the person's main checkout's branch or uncommitted work); images (bounded, content-addressed, metadata stripped, taint and egress applied, refusal for models without vision, replay); packaging (install.sh --app into a prefix, no system-wide writes, the .desktop file valid); search (results sanitized, opening at the right turn); the A5 leftovers fixed; the update contract and replay parity for new events; no write to the real home; threading and disposal; tests meaningful.

Builders' remaining gaps (judge each): ${JSON.stringify(done.map(x => ({ group: x.g.key, gaps: x.b.gaps })))}
${extra}

Severity: blocker (crash, failing build/check/test, data loss, a write to the real home, an approval/taint/egress bypass, an unauthenticated socket), major (a feature wrong or faked, a flow broken, the CLI/ACP broken, an unhandled exception), minor. pass is true only when no blocker or major remains. Evidence and steps for each.`

const fixPrompt = (defects, e2eReport) => `You are the fix round for phase A6. ${SETUP} Run \`git switch a6/int\` there and work on that branch; when you have committed, run \`git switch --detach\`. Display :259, state dir ${TMP}/harness-a6-fix.

${RULES}

Fix every blocker and major defect below and what the end-to-end pass reported broken, and minor ones that are quick and safe. Rerun the narrowest check that showed each, then once the whole-solution build, checker and tests, and commit.

Verifier's defects:
${JSON.stringify(defects, null, 2)}

End-to-end pass:
${e2eReport}

Return the STEP report (fixed by id, left and why).`

phase('End to end')
phase('Verify')
const [e2e, first] = await parallel([
  () => slot(() => agent(e2ePrompt, { label: 'end to end', phase: 'End to end', schema: STEP, isolation: 'worktree' })),
  () => slot(() => agent(verifyPrompt(1, 'The end-to-end pass runs at the same time as you; do not wait for it.'), { label: 'verify 1', phase: 'Verify', schema: DEFECTS, isolation: 'worktree' })),
])
const e2eReport = e2e ? JSON.stringify({ summary: e2e.summary, gaps: e2e.gaps }) : 'did not report'
let report = first
let round = 1
const fixes = []
if (report && (!report.pass || (e2e && e2e.gaps.length))) {
  const fix = await slot(() => agent(fixPrompt(report.defects, e2eReport), { label: 'fix', phase: 'Verify', schema: STEP, isolation: 'worktree' }))
  fixes.push(fix)
  round = 2
  report = await slot(() => agent(verifyPrompt(2, `Round 1's defects and the fix round's report: ${JSON.stringify({ defects: first.defects, fix: fix && { summary: fix.summary, gaps: fix.gaps } })}`), { label: 'verify 2', phase: 'Verify', schema: DEFECTS, isolation: 'worktree' }))
}
if (report && !report.pass) log(`Verifier still reports ${report.defects.filter(d => d.severity !== 'minor').length} blocker or major defects after ${round} rounds; they go to the PR review`)

return { built, missing, integrated, e2e, fixes, finalReport: report, rounds: round }
