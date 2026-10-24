export const meta = {
  name: 'corvid-app-a1-screens',
  description: 'Build the seven other mocked screens of the windowed Corvid in five parallel groups, integrate, verify on the running app, fix',
  whenToUse: 'Phase A1 of the windowed Corvid app (docs/plan/ui-handoff.md §3): Review+Rewind, Board, Models+First run, Memory, Audit.',
  phases: [
    { title: 'Build', detail: 'one builder per group, own worktree and headless display' },
    { title: 'Integrate', detail: 'merge the groups one at a time, build and test after each' },
    { title: 'Verify', detail: 'independent verifier, bounded fix rounds' },
  ],
}

// args: { maxConcurrent, base, repo, shims, jobTmp }
//   maxConcurrent  from `agent-budget -q` at launch (heavy agents: every stage builds)
//   base           the commit of main the builders branch from
//   repo           the repository's root
//   shims          a directory with Xvfb, xdotool, xwininfo and xdpyinfo, or stand-ins for them (docs/plan/ui-handoff.md,
//                  "Harness notes"); on a machine with the real tools, a directory that holds links to them
//   jobTmp         a private directory for the harness's state
// Every agent runs in its own worktree (isolation: 'worktree'): the session isolates each agent to one worktree, so
// agents meet through named branches, never through each other's directories.
const MAX = Math.max(1, (args && args.maxConcurrent) || 1)
let active = 0; const waiters = []
const slot = async fn => { while (active >= MAX) await new Promise(r => waiters.push(r)); active++; try { return await fn() } finally { active--; const w = waiters.shift(); if (w) w() } }
log(`maxConcurrent = ${MAX} (from agent-budget at launch)`)

const REPO = args.repo
const BASE = args.base
const SHIMS = args.shims
const TMP = args.jobTmp
// The session isolates each agent to one worktree, so every agent runs in its own (isolation: 'worktree') and works on a
// named branch there; branches are shared by the repository, worktrees are not.
const SETUP = 'You start in a fresh git worktree of the repository made for you (your working directory; `pwd` shows it). You cannot work in any other worktree: the session isolates each agent to its own.'

const RULES = `Rules that apply to you:
- Memory: before every dotnet build or test run \`agent-budget level\`. If it prints critical, wait with \`agent-budget alert --from critical --timeout 300\` and check again. When it prints tight, pass -m:2 to dotnet. Use ~/.dotnet/dotnet (the /usr/bin one has no SDK).
- Git: never push, never force-push, never touch main or other agents' branches. Commit messages follow \`git log\` ("App: <what> (<step>)") and carry NO Claude attribution: no Co-Authored-By, no Claude-Session, no links, no "Generated with" footer.
- Read docs/plan/ui-build.md first and follow it (shared rules, architecture, the verification protocol), then docs/plan/ui-handoff.md and src/Corvid.App/README.md (conventions and the Uno gotchas, which cost the last builder hours). The design is docs/plan/ui.md: features §4, flows §5, keys §6, visual language §7, architecture §8.
- Areas own their files (README "Areas"): src/Corvid.App.Core/<Area>/, src/Corvid.App.Core/Composition/AppComposition.<Area>.cs, src/Corvid.App/Pages/<Area>Page.xaml(.cs) and the area's dock. Other builders work on other areas at the same time. A change to a shared file (Themes/*.xaml, Controls/, Infrastructure/, Shell/, the Sessions core) must be additive, small, and listed in your report; prefer a new file in your area. Do not change the Sessions page or the session core's public API; if your area needs session data, read it through a new interface in your area's folder.
- Looking at the app: this machine has no Xvfb or xdotool; stand-ins are in ${SHIMS}. Run the harness as
    PATH=${SHIMS}:$PATH UI_HARNESS_STATE=<your state dir> CORVID_APP_RENDERER=software tools/ui-harness.sh <cmd> <display> ...
  with your own display number and state dir (given below). After \`launch\`, Uno resizes its own window to 1024x640 a few seconds later: wait 3 s, \`resize\` to the target, check with \`DISPLAY=:<n> xwininfo -id $(cat <state dir>/win-<n>.id)\`, then resize one pixel wider and back and wait 1.5 s. A screenshot with the app drawn in the top-left 1024x640 and black elsewhere is that quirk: nudge and retake. Clicks before the size settles land outside the window. View screenshots with your Read tool; crop with \`magick in.png -crop WxH+X+Y +repage out.png\`. Save screenshots under ui-shots/ in your worktree (git-ignored). Never stop processes with pkill/pgrep -f. Stop your display when done (\`stop <n> --xvfb\`).
- Never claim a check you did not run.`

const GROUPS = [
  { key: 'review-rewind', areas: 'Review and Rewind', mockups: 'Review.png, Review-narrow.png, Rewind.png, Rewind-narrow.png', sources: 'Review.dc.html, Rewind.dc.html', flows: 'F4 (reviewing the changes: the side-by-side diff, comments, send them to the session) and F5 (rewind: the timeline over the checkpoint ledger, preview a restore point, put back files, conversation or both)', extra: 'They share the diff view (the core already has Diffs/ with unified and side-by-side rows, and DiffRowsView draws unified rows): build one side-by-side diff control both pages use.' },
  { key: 'board', areas: 'Board (with its attention tray, BoardDock)', mockups: 'Board.png, Board-narrow.png', sources: 'Board.dc.html', flows: 'F18 (the Board: every session as a card with state, step, model and taint; the attention tray across sessions; each item opens its full card)', extra: 'Session summaries come from ISessionService.ListAsync and the SessionHub; do not change their public API.' },
  { key: 'models-firstrun', areas: 'Models and First run', mockups: 'Models.png, Models-narrow.png, FirstRun.png, FirstRun-narrow.png', sources: 'Models.dc.html, FirstRun.dc.html', flows: 'F1 (first run: a doctor page before any model loads, downloads with progress), F14 (models: the generator ladder, slots, the decider, cloud escalation settings) and F23 (degraded states: models missing banner with download)', extra: 'Demo mode serves both: a first-run scene is reachable with --page first-run.' },
  { key: 'memory', areas: 'Memory', mockups: 'Memory.png, Memory-narrow.png', sources: 'Memory.dc.html', flows: 'F11 (memory review: staged facts from the transcript, accept, edit or drop each; lessons and their scope)', extra: '' },
  { key: 'audit', areas: 'Audit (with the egress ledger)', mockups: 'Audit.png, Audit-narrow.png', sources: 'Audit.dc.html', flows: 'F8 (escalation and the privacy gate: what left the machine), F10 (a detection match: the rule, its events, open in Audit) and F21 (Audit: filter a session\'s event log by kind, open an event, export)', extra: 'The Sessions page already links here ("what left the machine", "Open in Audit"); make those land on the right view.' },
]

const STEP = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['done', 'partial', 'blocked'] },
    branch: { type: 'string' },
    worktree: { type: 'string' },
    commit: { type: 'string' },
    summary: { type: 'string' },
    shared_changes: { type: 'array', items: { type: 'string' }, description: 'every change to a file outside the area, with why' },
    verified: { type: 'array', items: { type: 'string' }, description: 'each check run on the running app or the build, with its result' },
    deviations: { type: 'array', items: { type: 'string' } },
    screenshots: { type: 'array', items: { type: 'string' } },
    notes_for_next: { type: 'string' },
  },
  required: ['status', 'branch', 'worktree', 'commit', 'summary', 'shared_changes', 'verified', 'deviations', 'screenshots', 'notes_for_next'],
}

const DEFECTS = {
  type: 'object',
  properties: {
    pass: { type: 'boolean', description: 'true only when no blocker or major defect remains' },
    summary: { type: 'string' },
    screenshots: { type: 'array', items: { type: 'string' } },
    defects: {
      type: 'array',
      items: {
        type: 'object',
        properties: {
          id: { type: 'string' }, severity: { type: 'string', enum: ['blocker', 'major', 'minor'] }, area: { type: 'string' },
          what: { type: 'string' }, expected: { type: 'string' }, evidence: { type: 'string' }, reproduce: { type: 'string' },
        },
        required: ['id', 'severity', 'area', 'what', 'expected', 'evidence', 'reproduce'],
      },
    },
  },
  required: ['pass', 'summary', 'screenshots', 'defects'],
}

const builderPrompt = (g, i) => `You are one of five builders of phase A1 of the windowed Corvid app (Uno Platform), working at the same time as the others. Your group: ${g.areas}.

${SETUP} First run \`git switch -c a1/${g.key} ${BASE}\` there, or \`git switch a1/${g.key}\` if that branch already exists (a resumed run: carry on from its commits), and do all your work and commits on that branch in that directory. Commit work in progress at every milestone (the area's core and demo data, the page drawn, each flow working), at least every 20 minutes of edits: a run can be interrupted, and only commits survive it. Your display is :${111 + i}; your harness state dir is ${TMP}/harness-a1-${g.key}.

${RULES}

Build ${g.areas} to match docs/plan/ui-mockups/png/${g.mockups} and their sources in docs/plan/ui-mockups/source/ (${g.sources}; corvid.css holds the exact sizes and colours). Each area today is a placeholder page with its title. Give each area its view model, DTO records, service interface and demo service in src/Corvid.App.Core/<Area>/ (demo data that draws the mockup's exact moment, consistent with the Sessions demo scenario where they overlap: session ids cli-9f2e, cli-a410, cli-77b0, cli-c3d2, the 14:05 frozen clock), wire it in AppComposition.<Area>.cs, and build the page (and dock, if the mockup has one) with x:Bind. ${g.extra}

Flows to make work and drive on the running app: ${g.flows}. Add the keys from ui.md §6 that these screens own.

Tests: add tests in tests/Corvid.App.Core.Tests for the new view models and demo services (xunit.v3; pass TestContext.Current.CancellationToken to every call that takes a token; never sleep or use the real clock).

Verify with ui-build.md §6: open each screen with --demo --page <area> at 1440x900 in light and compare with the mockup region by region; then dark, 1024x900, 820x900 (against the -narrow render) and 390x844 (usable and unclipped; the phone screens are phase A2). Drive each flow and screenshot each state; read the app log for exceptions. Fix what differs and look again. Then build the whole solution (\`~/.dotnet/dotnet build Corvid.slnx -c Release\`) with no warnings, run the checker (\`~/.dotnet/dotnet run --project tools/Corvid.Architecture -c Release --no-build -- .\`) and the App.Core and architecture tests, and commit on a1/${g.key}.

Return your branch, worktree path, commit, a summary, every shared-file change, the checks you ran with results, the remaining differences from the mockups, screenshot paths, and notes for the integrator.`

phase('Build')
const built = await parallel(GROUPS.map((g, i) => () => slot(() =>
  agent(builderPrompt(g, i), { label: `build ${g.key}`, phase: 'Build', schema: STEP, isolation: 'worktree' }))))
const done = built.map((b, i) => ({ g: GROUPS[i], b })).filter(x => x.b && x.b.status !== 'blocked' && x.b.commit)
const missing = GROUPS.filter((g, i) => !(built[i] && built[i].status !== 'blocked' && built[i].commit)).map(g => g.key)
if (missing.length) log(`Not integrated (blocked or no commit): ${missing.join(', ')}`)
if (!done.length) return { stoppedAt: 'build', built }

phase('Integrate')
const integrated = await slot(() => agent(`You are the integrator for phase A1 of the windowed Corvid app.

${SETUP} First run \`git switch -c a1/screens ${BASE}\` there; the builders' branches are in the same repository, so you can merge them by name.

${RULES}

Merge these builder branches into a1/screens one at a time, in this order, with \`git merge --no-ff <branch>\`:
${done.map(x => `- a1/${x.g.key} (${x.g.areas}), commit ${x.b.commit}. Its shared-file changes: ${JSON.stringify(x.b.shared_changes)}. Notes: ${x.b.notes_for_next}`).join('\n')}

After each merge: resolve conflicts (shared files were meant to be additive: keep both sides' additions, never drop one builder's work), build the whole solution in Release with no warnings, run the checker and the App.Core and architecture tests, and launch the app once (--demo, display :120, state dir ${TMP}/harness-a1-int) to confirm the merged areas still open and the Sessions page still matches Main.png. Fix integration breakage minimally and commit it. If a branch cannot be merged without rewriting it, stop merging that one, report it, and continue with the next.

When you are done and have committed, run \`git switch --detach\` so the branch a1/screens is free for the next agent (a branch can be checked out in one worktree at a time).

Return: the branch (a1/screens), worktree (\`pwd\`), final commit, which branches merged and which did not and why, the conflicts and how each was resolved, the checks with results, and notes for the verifier.`, { label: 'integrate', phase: 'Integrate', schema: STEP, isolation: 'worktree' }))
if (!integrated || integrated.status === 'blocked') return { stoppedAt: 'integrate', built, integrated }

phase('Verify')
const verifyPrompt = round => `You are the independent verifier for phase A1 of the windowed Corvid app, round ${round}. Do NOT edit, commit or push anything.

${SETUP} Run \`git switch --detach a1/screens\` there to look at the integrated work (detached: you only read). Display :121, state dir ${TMP}/harness-a1-verify-${round}.

${RULES}

Check each A1 screen against its mockup render and source: Review, Rewind, Board, Models, First run, Memory, Audit (docs/plan/ui-mockups/png/<Artboard>.png and -narrow.png; sources in source/). Look at every region at 1440x900 light, then dark, then 1024, 820 (against -narrow) and 390 (usable, unclipped; phone screens are phase A2). Compare copy, spacing, alignment, type, colours, borders, clipping, overflow, empty states and focus visuals. Drive flows F1, F4, F5, F8, F10, F11, F14, F18, F21 and F23 from ui.md §5 as far as the screens reach, and the keys §6 assigns to them, confirming each state change on screen. Check the Sessions page still matches Main.png and its links to Review, Rewind and Audit land. Read the XAML for AutomationProperties.Name on icon-only buttons. Read the app log for exceptions. Run the whole-solution Release build, the checker and the App.Core and architecture tests.

Builders' own reports of what remains different (judge them, do not just accept them):
${done.map(x => `- ${x.g.areas}: ${JSON.stringify(x.b.deviations)}`).join('\n')}

Severity: blocker (crash, failing build/check/test, broken flow), major (clearly differs from the mockup or design: missing element, wrong layout, broken interaction, unreadable contrast), minor (polish). pass is true only when no blocker or major remains. Each defect needs concrete evidence (screenshot path, coordinates or quoted text) and steps to reproduce.`

const fixPrompt = (round, defects) => `You are fix round ${round} for phase A1 of the windowed Corvid app. ${SETUP} Run \`git switch a1/screens\` there and work on that branch. When you have committed, run \`git switch --detach\` so the branch is free again. Display :122, state dir ${TMP}/harness-a1-fix-${round}.

${RULES}

The independent verifier found these defects. Fix every blocker and major one, and the minor ones that are quick and safe. For each fix, rerun on the running app the check that showed the defect and confirm it is gone. Then build the whole solution with no warnings, run the checker and the tests, and commit.

Defects:
${JSON.stringify(defects, null, 2)}

Return what you fixed (by defect id), the commit, the checks with results, the defects you left and why, and screenshot paths.`

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

return { built, missing, integrated, fixes, finalReport: report, rounds: round }
