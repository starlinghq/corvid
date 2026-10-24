export const meta = {
  name: 'corvid-app-a2-reach',
  description: 'Build the windowed Corvid pages without mockups, the palette and keys, then the phone screens and accessibility, integrate, verify, fix',
  whenToUse: 'Phase A2 of the windowed Corvid app (docs/plan/ui-handoff.md §3): Skills; Jobs, MCP and Gateway; Safety and Secrets; Settings with the palette and keys; then phone and accessibility.',
  phases: [
    { title: 'Build', detail: 'one builder per group, own worktree and headless display' },
    { title: 'Integrate', detail: 'merge the groups one at a time, build and test after each' },
    { title: 'Phone and accessibility', detail: 'the phone layout for every area, and an accessibility pass' },
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

const BASE = args.base
const SHIMS = args.shims
const TMP = args.jobTmp
const SETUP = 'You start in a fresh git worktree of the repository made for you (your working directory; `pwd` shows it). You cannot work in any other worktree: the session isolates each agent to its own.'

const RULES = `Rules that apply to you:
- Memory: before every dotnet build or test run \`agent-budget level\`. If it prints critical, wait with \`agent-budget alert --from critical --timeout 300\` and check again. When it prints tight, pass -m:2 to dotnet. Use ~/.dotnet/dotnet (the /usr/bin one has no SDK).
- Git: never push, never force-push, never touch main or other agents' branches. Commit work in progress at every milestone (at least every 20 minutes of edits): a run can be interrupted, and only commits survive it. Commit messages follow \`git log\` ("App: <what> (A2 <group>)") and carry NO Claude attribution: no Co-Authored-By, no Claude-Session, no links, no "Generated with" footer.
- Read docs/plan/ui-build.md first and follow it, then docs/plan/ui-handoff.md (§1.1 and §1.2 say how A0 and A1 were built and what their reviews found) and src/Corvid.App/README.md (conventions and the Uno gotchas). The design is docs/plan/ui.md: features §4, flows §5, keys §6, visual language §7, architecture §8.
- These pages have no mockups. Design each from ui.md §4 and §7 so it looks and behaves like the mocked pages already built (Review, Rewind, Board, Models, First run, Memory, Audit): the same page head, panels, tables, tags, buttons, dock, status bar, type and colours (the corvid.css tokens in docs/plan/ui-mockups/source/corvid.css). Reuse the existing controls before adding new ones.
- Areas own their files (README "Areas"): src/Corvid.App.Core/<Area>/, Composition/AppComposition.<Area>.cs, src/Corvid.App/Pages/<Area>Page.xaml(.cs). Other builders work on other areas at the same time. A change to a shared file (Themes/, Controls/, Infrastructure/, Shell/, Sessions/, Composition/AppComposition.cs) must be additive, small, and listed in your report.
- What the review of A1 (#75) found, so do not repeat it:
  * Untrusted text (from the model, tools, MCP servers, files, configuration, the event log) is drawn through SafeText: \`SafeText.Clean\` for display, \`SafeText.Reveal\` where a person approves or acts on it in one click (a command, a path, a rule, a skill or tool to accept, a secret's name to bind, a server to trust). Text that will be stored or run must not be approvable while \`SafeText.HasHidden\` is true. Values put into markup (Spans, AuditText) are escaped first. Tests use U+202E and a tag character (U+E0041), written as \\u escapes in the source, never as literal characters.
  * No subscription from a page or control to a long-lived view model without an unsubscribe on Unloaded/OnNavigatedFrom (pages are not cached; each visit makes a new page).
  * Lists that can grow are capped or virtualised, and rebuilt once per change, not once per item.
  * Every command failure is shown (a red notice), never swallowed in AsyncRelayCommand.Error.
  * An area that writes the status bar takes it with \`StatusBarLease\` and gives it back.
  * Timers and services the area starts are disposed through AppComposition.Dispose; tests build the composition with \`ManualTimeProvider.DemoLocal()\` and \`using\`.
  * Demo mode writes no file or configuration, starts no process, opens no URL and fetches nothing.
  * Demo data names no real person, place, home directory or account.
- Looking at the app: this machine has no Xvfb or xdotool; stand-ins are in ${SHIMS}. Run the harness as
    PATH=${SHIMS}:$PATH UI_HARNESS_STATE=<your state dir> CORVID_APP_RENDERER=software tools/ui-harness.sh <cmd> <display> ...
  with your own display number and state dir (given below). After \`launch\`, Uno resizes its own window to 1024x640 a few seconds later: wait 3 s, \`resize\` to the target, check with \`DISPLAY=:<n> xwininfo -id $(cat <state dir>/win-<n>.id)\`, then resize one pixel wider and back and wait 1.5 s. A screenshot with the app drawn in the top-left 1024x640 and black elsewhere is that quirk: nudge and retake. View screenshots with your Read tool; crop with \`magick in.png -crop WxH+X+Y +repage out.png\`. Save screenshots under ui-shots/ (git-ignored). Never stop processes with pkill/pgrep -f. Stop your display when done (\`stop <n> --xvfb\`).
- Never claim a check you did not run.`

const GROUPS = [
  { key: 'skills', areas: 'Skills', sections: 'ui.md §4.9 (the skills half; Memory is already built) and flow F12 (a skill is learned: the draft, the reason, approve or edit, history, evals, pins, stale skills)', extra: 'The slash command /skills in src/Corvid.Host/Commands shows what the engine offers (list, show, pending, approve, reject, log, diff, revert, import, evals, pin, unpin, stale): mirror it.' },
  { key: 'ops', areas: 'Jobs, MCP and Gateway', sections: 'ui.md §4.10 and flows F13 (a scheduled job: create, the run history, pause, resume, remove; a run opens as a session), F15 (an MCP tool changed: the held tool, its diff against the pin, accept or keep blocked; servers, trust, the tools each added) and F16 (a pairing request: codes, pair as guest or owner, ignore; the channels and their state)', extra: 'The Board already shows held MCP tools, pairing codes and finished jobs in its attention tray, and the rail has an MCP badge: these pages are where those items lead, so the Board\'s actions should land on the right row. /cron and /mcp in src/Corvid.Host/Commands show the engine\'s operations.' },
  { key: 'safety-secrets', areas: 'Safety and Secrets', sections: 'ui.md §4.4 (the approval modes, the remembered rules with their source and scope and how to remove them, the hardline list, detection packs and their posture, taint) and §4.11 with flow F17 (secrets: names, scopes, which slot or tool waits on which name, binding a name, never showing a value)', extra: 'The Sessions approval card writes rules ("Listed in Safety → Rules, removable there"): Safety must list and remove them. Secret values are never drawn, copied or logged; only names and metadata.' },
  { key: 'settings-keys', areas: 'Settings, the command palette and the rest of the keyboard map', sections: 'flow F24 (a typed settings page over corvid.json with the validator\'s messages inline, a raw JSON editor, and what needs a restart), ui.md §6 (every key not yet bound: Ctrl+K and Ctrl+P the palette, Ctrl+N new session, Ctrl+Tab the session switcher, Ctrl+F find in the session, Ctrl+Shift+F search across sessions, Ctrl+R prompt history, Ctrl+; aside where it can open, Alt+P where it applies, F1 and ? help and shortcuts) and §4.15 (themes and keybindings)', extra: 'You own the shell-level key handling (Shell/ShellPage.xaml.cs and a new palette control). Keys that need an engine feature not built yet open their surface with a clear "comes with …" state, never do nothing. Settings in demo mode edits an in-memory copy and says it is the demo; nothing is written to disk. The Settings theme note from A1 (the theme applies to focus colours after a restart) stays true.' },
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
    deviations: { type: 'array', items: { type: 'string' }, description: 'design choices made without a mockup, and anything left undone' },
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

const builderPrompt = (g, i) => `You are one of four builders of phase A2 of the windowed Corvid app (Uno Platform), working at the same time as the others. Your group: ${g.areas}.

${SETUP} First run \`git switch -c a2/${g.key} ${BASE}\` there, or \`git switch a2/${g.key}\` if that branch already exists (a resumed run: carry on from its commits), and do all your work and commits on that branch in that directory. Your display is :${141 + i}; your harness state dir is ${TMP}/harness-a2-${g.key}.

${RULES}

Build ${g.areas} from ${g.sections}. Each area is a placeholder page with its title today. Give each area its view model, DTO records, service interface and demo service in src/Corvid.App.Core/<Area>/ (demo data consistent with the existing demo scenario: sessions cli-9f2e, cli-a410, cli-77b0, cli-c3d2, the nightly-tidy job, the 14:05 frozen clock, the Telegram pairing and the held notes__search tool the Board shows), wire it in AppComposition.<Area>.cs, and build the page (and dock, if the area needs one) with x:Bind. ${g.extra}

Drive the flows named above on the running app and add the keys from ui.md §6 that these screens own.

Tests: add tests in tests/Corvid.App.Core.Tests for the new view models and demo services (xunit.v3; pass TestContext.Current.CancellationToken to every call that takes a token; never sleep or use the real clock), including the untrusted-text cases above.

Verify with ui-build.md §6, against ui.md and the look of the mocked pages: open each screen with --demo --page <area> at 1440x900 in light, then dark, 1024x900, 820x900 and 390x844 (usable and unclipped; the phone pass comes after integration). Drive each flow and screenshot each state; read the app log for exceptions. Then build the whole solution (\`~/.dotnet/dotnet build Corvid.slnx -c Release\`) with no warnings, run the checker (\`~/.dotnet/dotnet run --project tools/Corvid.Architecture -c Release --no-build -- .\`) and the App.Core and architecture tests (if \`dotnet test --project\` reports zero tests, run the executables under tests/*/bin/Release/net10.0/), and commit on a2/${g.key}.

Return your branch, worktree path, commit, a summary, every shared-file change, the checks you ran with results, the design choices you made without a mockup and anything left undone, screenshot paths, and notes for the integrator.`

phase('Build')
const built = await parallel(GROUPS.map((g, i) => () => slot(() =>
  agent(builderPrompt(g, i), { label: `build ${g.key}`, phase: 'Build', schema: STEP, isolation: 'worktree' }))))
const done = built.map((b, i) => ({ g: GROUPS[i], b })).filter(x => x.b && x.b.status !== 'blocked' && x.b.commit)
const missing = GROUPS.filter((g, i) => !(built[i] && built[i].status !== 'blocked' && built[i].commit)).map(g => g.key)
if (missing.length) log(`Not integrated (blocked or no commit): ${missing.join(', ')}`)
if (!done.length) return { stoppedAt: 'build', built }

phase('Integrate')
const integrated = await slot(() => agent(`You are the integrator for phase A2 of the windowed Corvid app.

${SETUP} First run \`git switch -c a2/screens ${BASE}\` there (or \`git switch a2/screens\` if it exists); the builders' branches are in the same repository, so you can merge them by name.

${RULES}

Merge these builder branches into a2/screens one at a time, in this order, with \`git merge --no-ff <branch>\`:
${done.map(x => `- a2/${x.g.key} (${x.g.areas}), commit ${x.b.commit}. Its shared-file changes: ${JSON.stringify(x.b.shared_changes)}. Notes: ${x.b.notes_for_next}`).join('\n')}

After each merge: resolve conflicts (shared files were meant to be additive: keep both sides' additions, never drop one builder's work), build the whole solution in Release with no warnings, run the checker and the App.Core and architecture tests, and launch the app once (--demo, display :150, state dir ${TMP}/harness-a2-int) to confirm the merged areas open, the palette and keys work, and the Sessions page still matches Main.png. Fix integration breakage minimally and commit it. If a branch cannot be merged without rewriting it, stop merging that one, report it, and continue with the next.

When you are done and have committed, run \`git switch --detach\` so the branch a2/screens is free for the next agent (a branch can be checked out in one worktree at a time).

Return: the branch (a2/screens), worktree (\`pwd\`), final commit, which branches merged and which did not and why, the conflicts and how each was resolved, the checks with results, and notes for the next agents.`, { label: 'integrate', phase: 'Integrate', schema: STEP, isolation: 'worktree' }))
if (!integrated || integrated.status === 'blocked') return { stoppedAt: 'integrate', built, integrated }

phase('Phone and accessibility')
const phone = await slot(() => agent(`You are the phone and accessibility builder for phase A2 of the windowed Corvid app.

${SETUP} Run \`git switch a2/screens\` there and work on that branch. When you have committed, run \`git switch --detach\` so the branch is free again. Your display is :151; your harness state dir is ${TMP}/harness-a2-phone.

${RULES}

Two jobs, on the integrated branch where every area now exists:
1. The phone layout (under 720 px; docs/plan/ui-mockups/png/Phone.png and source/Phone.dc.html; ui.md §3): the session screen as Phone.png draws it (top bar, the compact approval card with full-width answers and short reasons, compact parsed rows, a one-line composer with a send button, the bottom bar), then every other area usable at 390x844: one column, no clipped controls, the bottom bar's tabs (Chat, Changes, Attention, More) leading where they say, setup (First run) without the session's bottom bar. The shell's phone flags (ShellViewModel.ShowPhoneBars, the phone title) are yours to change for this.
2. An accessibility pass over every page (ui.md §4.15): every icon-only button named, headings with levels, tab order following reading order, every action reachable by keyboard, focus visible in both themes, colour never the only carrier of meaning, live regions or names for things that change (the turn strip, countdowns, notices). A real screen reader cannot run here; if an AT-SPI tree is reachable for the app (try \`busctl --user\` / accerciser-style inspection, or note that it is not), use it, and otherwise audit the XAML and the automation names in code. Say plainly what could and could not be checked.

Verify at 390x844 and 820x900 in light and dark for every area, drive the phone session's approval and composer, and Tab through each page at 1440. Then build the whole solution with no warnings, run the checker and the tests, and commit.

Return the branch, worktree, commit, what you changed for the phone and for accessibility, the checks with results, what could not be checked, screenshot paths, and notes for the verifier.`, { label: 'phone and accessibility', phase: 'Phone and accessibility', schema: STEP, isolation: 'worktree' }))

phase('Verify')
const verifyPrompt = round => `You are the independent verifier for phase A2 of the windowed Corvid app, round ${round}. Do NOT edit, commit or push anything.

${SETUP} Run \`git switch --detach a2/screens\` there to look at the integrated work (detached: you only read). Display :152, state dir ${TMP}/harness-a2-verify-${round}.

${RULES}

The A2 pages have no mockups: judge them against ui.md (§4.4, §4.9, §4.10, §4.11, §4.15, §6, flows F12, F13, F15, F16, F17, F24) and against the look of the mocked pages (corvid.css tokens; the existing Review, Board, Models, Memory and Audit screens): consistent page heads, panels, tables, tags, buttons, spacing and type. Check Skills, Jobs, MCP, Gateway, Safety, Secrets and Settings at 1440x900 light, then dark, 1024, 820 and 390; the phone session against Phone.png; the palette and every §6 key that is bound (and that keys not bound yet show a clear state); Tab order and focus visibility on each page; untrusted text handling (A1's review rules in the rules above); that the Board's attention actions and the Sessions links land on the right rows of the new pages; that Sessions, Board, Review and Memory still match their mockups. Read the app log for exceptions. Run the whole-solution Release build, the checker and the App.Core and architecture tests.

What the builders and the phone pass said they chose or left (judge it, do not just accept it):
${done.map(x => `- ${x.g.areas}: ${JSON.stringify(x.b.deviations)}`).join('\n')}
- Phone and accessibility: ${phone ? JSON.stringify(phone.deviations) : 'did not report'}

Severity: blocker (crash, failing build/check/test, broken flow), major (clearly against the design, inconsistent with the mocked pages, a broken interaction, unreadable contrast, an unnamed control, an unreachable action, untrusted text drawn raw where it is approved), minor (polish). pass is true only when no blocker or major remains. Each defect needs concrete evidence (screenshot path, coordinates or quoted text) and steps to reproduce.`

const fixPrompt = (round, defects) => `You are fix round ${round} for phase A2 of the windowed Corvid app. ${SETUP} Run \`git switch a2/screens\` there and work on that branch. When you have committed, run \`git switch --detach\` so the branch is free again. Display :153, state dir ${TMP}/harness-a2-fix-${round}.

${RULES}

The independent verifier found these defects. Fix every blocker and major one, and the minor ones that are quick and safe. For each fix, rerun on the running app the check that showed the defect and confirm it is gone. Then build the whole solution with no warnings, run the checker and the App.Core and architecture tests, and commit.

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

return { built, missing, integrated, phone, fixes, finalReport: report, rounds: round }
