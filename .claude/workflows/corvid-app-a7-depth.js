export const meta = {
  name: 'corvid-app-a7-depth',
  description: 'Code mode (sandboxed scripts calling Corvid tools), architect/editor turns and memory consolidation; foundation, four builders, integrate, verify end to end, fix',
  whenToUse: 'Phase A7 of Corvid: plan corvid-agent.md §15 code mode, §6 architect/editor, §9.5 sleep-time consolidation.',
  phases: [
    { title: 'Foundation', detail: 'the sandbox tools socket, the nested tool-call seam, the frozen wire format' },
    { title: 'Build', detail: 'four groups in parallel worktrees' },
    { title: 'Integrate', detail: 'merge the groups one at a time' },
    { title: 'End to end', detail: 'the new flows on the app and the CLI, beside verify round 1' },
    { title: 'Verify', detail: 'at most two rounds and one fix round' },
  ],
}

// args: { maxConcurrent, base, shims, jobTmp }
// Lean by design: as many builders as slots, incremental builds while iterating, the end-to-end pass beside the first
// verify round, and at most two verify rounds.
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
- Git: never push, never force-push, never touch main or other agents' branches. Commit at every milestone. Commit messages follow \`git log\` ("Kernel: <what> (A7 <group>)", "Tools.Builtin: …") with NO Claude attribution: no Co-Authored-By, no Claude-Session, no links, no "Generated with".
- Read first: docs/plan/ui-handoff.md (§1.4 to §1.7: what A3–A6 built, refused and left; §4), docs/plan/ui.md (§4, §5 flows incl. F19, §8 architecture, the G-n and U-n tables), docs/plan/corvid-agent.md (§3.3, §6, §9, §15, and the security model: approvals, taint, sandbox, Vault, egress), docs/architecture.md (the sandbox and approvals sections), docs/plan/tui.md §9, docs/plan/vault.md where tokens are involved, src/Corvid.App/README.md, the XML docs on src/Corvid.App.Core/Sessions/*Updates.cs.
- Architecture (tools/Corvid.Architecture): Corvid.App.Engine may reference Corvid.Host, App.Core, Corvid.Mcp, the Models adapters and core projects; App.Core gains no references; Corvid.Host never references App projects. A new project (a protocol or client library) goes in Corvid.slnx with the checker's tables updated and justified.
- The CLI, ACP and the gateway are first-class surfaces; nothing breaks them. Keep tests/Corvid.Acp.Tests' exchanges passing unchanged unless an intended change is re-recorded (say so). If CI pins a test count (.github/workflows/ci.yml grep -q "Total: N") for a suite you change, update it.
- Security: approvals block until answered; nothing is granted while SafeText.HasHidden; taint and egress are never weakened; anything arriving over a socket, from a file, an image or a model is untrusted. A listening socket binds to loopback or a Unix socket by default, requires a token, and never exposes the owner's approvals to an unpaired client.
- Never touch the user's real Corvid home (~/.config/corvid, ~/.local/share/corvid, ~/.hermes, the real Vault, real chat tokens) and never install anything system-wide. Tests use tests/Corvid.Host.Tests/Fakes.cs (TestHost, FakeModels, FakeGenerator) in temporary directories; runs point HOME, CORVID_CONFIG and XDG_* at a temporary directory under ${TMP} (see ${TMP}/a3-e2e-scripts/run-app.sh and fakeoai.py). Never start, stop or reconfigure a shared model server. Never kill processes by name (no pkill/pgrep -f); kill only PIDs you started.
- Code mode's security is the point of the phase (plan §15 and parity row 1.6, Hermes #74078): a script's tool calls must go through the turn's own tool-call path — the approval pipeline per call, checkpoints, credential scrub, taint, egress, leases — never straight to the ToolRegistry; the script runs only inside the sandbox, reaches the host only through the bind-mounted tools socket with a per-run token, and has no network; limits on time, call count and output are enforced on the host side.
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


phase('Foundation')
const foundation = await slot(() => agent(`You build the foundation of phase A7 (code mode, architect/editor, consolidation): the small, shared seams four builders after you use.

${SETUP} First run \`git switch -c a7/core ${BASE}\` there (or \`git switch a7/core\` if it exists: a resumed run). Display :271; state dir ${TMP}/harness-a7-core.

${RULES}

Build, small and clean:
1. The sandbox tools socket: SandboxPolicy gains an optional tools socket (a host path) and environment values (not just names); the launcher (sandbox/Corvid.Sandbox.Launcher: Arguments.cs, SandboxPlan.cs, Stage2.cs) bind-mounts it read-only like V-003b's proxy socket (e.g. /run/corvid-sandbox/tools.sock), refusing a missing socket; BubblewrapSandbox passes it. A confinement test (Category=Confinement) that a sandboxed process can connect to that socket and to nothing else new; update CI's pinned confinement count.
2. The nested tool-call seam: ToolCallContext gains an invoker (e.g. InvokeTool(name, JsonElement args, ct) → result) that TurnRunner sets so a sub-call runs through the same ExecuteAsync path as a model call (lease handling: the parent holds its lease; a sub-call waiting on approval is parked), with a child call id ({callId}.{n}); ToolCallIssued gains an optional ParentCallId (additive, older logs read as before); the projection and the host session stream carry it; if any sub-call's result is tainted, the parent's result is marked tainted.
3. The code-mode wire format, frozen and documented (docs/architecture.md): newline-delimited JSON over the tools socket — a per-run token, request {id, tool, args}, response {id, ok, result | error}, limits (calls, bytes) — and the tool-descriptor JSON the client generators will read (name, description, parameter JSON Schema), from the ToolRegistry intersected with the turn's offered tools and a code-mode allowlist.
Tests for each. Then the whole-solution build, checker and all tests; commit. Return the STEP report; notes_for_next must say exactly how builders use the socket option, the invoker, ParentCallId and the wire format, and which files are hotspots.`, { label: 'foundation', phase: 'Foundation', schema: STEP, isolation: 'worktree' }))
if (!foundation || foundation.status === 'blocked' || !foundation.commit) return { stoppedAt: 'foundation', foundation }

const GROUPS = [
  { key: 'codehost', areas: 'code mode, host side',
    detail: `The execute_code tool (Corvid.Tools.Builtin, beside ShellTool): takes a language (csharp, python, javascript) and source; runs it in the sandbox (WorkspaceServices' shell wiring, the session's workspace, no network, Vault egress only as the shell has it) with the foundation's tools socket served by a newline-JSON server for that one run (per-run token compared in constant time, the code-mode allowlist ∩ the turn's offered tools, a call budget, output caps; Hermes' defaults: 300 s, 50 calls, 50 KB stdout, 10 KB stderr, configurable under Corvid:Tools:Code); every request dispatched through the foundation's invoker so approvals, checkpoints, taint, scrub and egress apply per call; the script's own approval is its own analysis kind (do not let ToolAnalyzer mine paths and URLs from the source as if it were a shell command; show the source on the card); offered only when the session's generator is a cloud or local-server model (plan §15) and Corvid:Tools:Code is enabled; the result returns stdout, stderr, exit code, the call log summary, and taint. Language runners: python3 and node by absolute path; dotnet by absolute path with a per-workspace writable cache (DOTNET_CLI_HOME, artifacts) under the workspace's .corvid or the state dir, PublishAot=false, #:package/#:sdk refused, run outside the repo tree so Directory.Build.props is not picked up; doctor rows for the runtimes. Tests with each language (skip a language whose runtime is absent, saying so), with limits, a refused tool, an approval-requiring call, a tainting call, and a script that tries to reach the network or files outside the workspace.` },
  { key: 'codeclients', areas: 'code mode, clients and surfaces',
    detail: `The generated clients from the foundation's tool-descriptor JSON: a C# CorvidTools class appended after a file-based app's top-level statements, a Python module, a JavaScript module — typed wrappers per tool, the socket and token from the environment, errors as exceptions; generated per run, tested against a fake server speaking the frozen wire format (a C# test for each client's request/response handling). Surfaces: the tool card for execute_code shows the source (syntax-highlighted where the app already highlights code), the language, limits used, and its sub-calls nested under it (ParentCallId) live and on replay, in the app (transcript and Review), corvid chat (indented sub-call lines) and ACP (sub-calls as tool_call updates with a parent reference in _meta); the approval card for the script shows the full source (revealed) and that each tool it calls will be approved on its own.` },
  { key: 'architect', areas: 'architect/editor turns',
    detail: `Plan §6 "for coding, the escalated model plans and the local model applies mechanical edits", as a turn mode (not a child session): when a turn escalates on a coding task (difficulty/context/repair) and Corvid:Turn:ArchitectEditor is on (and/or the person asks with /architect), the escalated generator produces one plan (read-only tools only, or none), recorded as an event/assistant message with its generator; the turn then de-escalates to the session's own generator (or a configured editor model via TurnOptions.EditorModel or the mix) and runs the edits with the plan as a note; the privacy egress gate applies to the planner as to any cloud call; if the editor gets stuck, one re-plan is allowed. GenerateAsync takes a binding override. Surfaces: the transcript shows "planned by X · edited by Y" with the plan collapsible, live and on replay; corvid chat and ACP show the plan step. Tests with scripted generators for the plan/edit split, the re-plan, the egress gate refusing a tainted planner, and a turn that never escalates (no change).` },
  { key: 'consolidate', areas: 'memory consolidation (sleep-time jobs)',
    detail: `Plan §9.5 and §9.2 step 4, starting with the block rewrite job: a BlockConsolidator (Corvid.Memory) that asks the background generator to rewrite an over-budget or ≥90%-full core block to its budget (counted with the generator's tokenizer), keeps the meaning, refuses credentials, and stages the result as a block update with replace semantics (MemoryWriter/PendingStore; never applied without review — a core block enters every prompt); an on-demand path for the app's ConsolidateAsync (replacing the engine's refusal) and /memory consolidate in the CLI; a background loop like LessonLearner/SkillCurator on ModelComposer.CreateBackgroundSlots() at DevicePriority.Background that runs when sessions are idle (SessionActor.IsIdle/SessionHost) and a block crosses the threshold; events for /journey. Then, if time allows, fact dedup/merge (FactGraph) staged the same way. The app's Memory page offers consolidation per block with the proposed rewrite as a diff. Tests: a scripted generator rewrite staged, approve applies it, reject leaves it, credentials refused, the idle trigger, nothing applied without approval.` },
]

phase('Build')
const builderPrompt = (g, i) => `You are one of four builders of phase A7 of Corvid. Your group: ${g.areas}.

${SETUP} First run \`git switch -c a7/${g.key} ${foundation.commit}\` there (the foundation's commit), or \`git switch a7/${g.key}\` if that branch exists (a resumed run: carry on from its commits). Display :${272 + i}; state dir ${TMP}/harness-a7-${g.key}.

${RULES}

The foundation's report (read its code too):
${foundation.notes_for_next}

Build: ${g.detail}

Other groups at the same time: codehost (execute_code, the RPC server, runners, limits), codeclients (generated clients, cards and surfaces), architect (architect/editor turns), consolidate (memory consolidation). Shared hotspots — TurnRunner.cs, CorvidRuntime.cs/CorvidRuntimeFactory.cs (add your own partial file or one call site), CorvidOptions.cs, ToolPresentation.cs, the session stream, docs: keep edits there additive and small, and list them. Do not edit another group's items.

Tests for every feature that fail without it. Then drive your feature once on the running app (engine default, temporary home, a scripted generator — fakeoai.py can be extended to script tool calls) and in corvid chat; read the logs.

Return the STEP report.`

const built = await parallel(GROUPS.map((g, i) => () => slot(() =>
  agent(builderPrompt(g, i), { label: `build ${g.key}`, phase: 'Build', schema: STEP, isolation: 'worktree' }))))
const done = built.map((b, i) => ({ g: GROUPS[i], b })).filter(x => x.b && x.b.status !== 'blocked' && x.b.commit)
const missing = GROUPS.filter((g, i) => !(built[i] && built[i].status !== 'blocked' && built[i].commit)).map(g => g.key)
if (missing.length) log(`Not integrated (blocked or no commit): ${missing.join(', ')}`)
if (!done.length) return { stoppedAt: 'build', foundation, built }

phase('Integrate')
const integrated = await slot(() => agent(`You are the integrator for phase A7.

${SETUP} First run \`git switch -c a7/int ${foundation.commit}\` there (or \`git switch a7/int\` if it exists). Display :276, state dir ${TMP}/harness-a7-int.

${RULES}

Merge these branches one at a time with \`git merge --no-ff <branch>\` (codehost, codeclients, architect, consolidate where present). After each merge build the projects it touched and run their tests; after the last, the whole solution, the checker and every test project:
${done.map(x => `- a7/${x.g.key} (${x.g.areas}), commit ${x.b.commit}. Host changes: ${JSON.stringify(x.b.host_changes)}. Shared: ${JSON.stringify(x.b.shared_changes)}. Notes: ${x.b.notes_for_next}`).join('\n')}
Resolve conflicts keeping both sides' work. Add tests where the groups meet (a generated client calling the real execute_code server; an architect/editor turn whose editor uses execute_code; consolidation while a code-mode turn runs). Write docs/plan/ui-handoff.md §1.8 for A7 (built, decisions, still refused, left) and its status row; update docs/plan/corvid-agent.md (§6, §9.5, §15 status, Phase 3) and docs/architecture.md. Run the app once on the engine.

When you have committed, run \`git switch --detach\` so a7/int is free.

Return the STEP report.`, { label: 'integrate', phase: 'Integrate', schema: STEP, isolation: 'worktree' }))
if (!integrated || integrated.status === 'blocked') return { stoppedAt: 'integrate', foundation, built, integrated }

const e2ePrompt = `You run phase A7's end-to-end pass.

${SETUP} Run \`git switch a7/int\` there and work on that branch; when you have committed, run \`git switch --detach\`. Display :277, state dir ${TMP}/harness-a7-e2e.

${RULES}

In a temporary home with scripted generators (one configured as a local server, one as a cloud-class model for escalation): on the running app and in corvid chat — a generator writes a Python, a JavaScript and a C# script that call two Corvid tools; a sub-call that needs approval is asked and answered; a tainting sub-call taints the session; the call budget, timeout and output caps trip; a script trying the network or a file outside the workspace fails; code mode is not offered on the local 0.8B generator; the cards nest sub-calls live and after a restart; an architect/editor turn (planned by the cloud-class model, edited by the local one) with the plan shown, and a tainted session's planner refused by egress; consolidation of an over-budget core block from the Memory page and /memory consolidate, approved and rejected, and the idle background trigger. Fix small breakage (commit it); list the rest.

Return the STEP report: per flow, works / refused / broken.`

const verifyPrompt = (round, extra) => `You are the independent verifier for phase A7, round ${round}. Do NOT edit, commit or push.

${SETUP} Run \`git switch --detach a7/int\` there. Display :278, state dir ${TMP}/harness-a7-verify-${round}.

${RULES}

As a verifier you build once and run the full test set once; read code for everything else, and run the app only for flows you doubt.

Check: code mode's security above all — every script tool call goes through the turn's tool-call path (approval per call, checkpoints, scrub, taint, egress, leases), nothing reaches the ToolRegistry directly, the per-run token, the allowlist ∩ offered tools, the host-side limits, no network and no path outside the workspace from the script, the tools socket visible to that run only, the script's own approval showing its source, offered only on cloud/local-server generators; the generated clients match the frozen wire format; architect/editor (the planner gated by egress, the editor on the session's model, replay); consolidation (nothing applied without approval, credentials refused, budgets met, the idle trigger, background priority); the update contract and replay parity for new events; the CLI/ACP/gateway still work; no write to the real home; threading and disposal; tests meaningful.

Builders' remaining gaps (judge each): ${JSON.stringify(done.map(x => ({ group: x.g.key, gaps: x.b.gaps })))}
${extra}

Severity: blocker (crash, failing build/check/test, data loss, a write to the real home, an approval/taint/egress bypass, a script tool call that bypasses approval or the sandbox), major (a feature wrong or faked, a flow broken, the CLI/ACP broken, an unhandled exception), minor. pass is true only when no blocker or major remains. Evidence and steps for each.`

const fixPrompt = (defects, e2eReport) => `You are the fix round for phase A7. ${SETUP} Run \`git switch a7/int\` there and work on that branch; when you have committed, run \`git switch --detach\`. Display :279, state dir ${TMP}/harness-a7-fix.

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

return { foundation, built, missing, integrated, e2e, fixes, finalReport: report, rounds: round }
