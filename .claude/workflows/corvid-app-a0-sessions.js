export const meta = {
  name: 'corvid-app-a0-sessions',
  description: 'Build the Sessions page on the session core and verify phase A0 on the running app, with a bounded fix loop',
  whenToUse: 'Next step of the windowed Corvid app build (docs/plan/ui-handoff.md): the Sessions page, then an independent verifier.',
  phases: [
    { title: 'Sessions page', detail: 'transcript, composer, approval card, dock, status bar, keyboard' },
    { title: 'Verify', detail: 'independent verifier with a bounded fix loop' },
  ],
}

// args (all optional):
//   method   'harness' (default: tools/ui-harness.sh on private Xvfb displays) or 'desktop' (computer use on this
//            machine's screen; the agents then run one at a time, which this script does anyway)
//   shots    folder for screenshots, default 'ui-shots' under the repository (git-ignored)
//   display  first Xvfb display number for the harness, default 103
const method = (args && args.method) || 'harness'
const shots = (args && args.shots) || 'ui-shots'
const display = (args && args.display) || 103

const LOOK = method === 'desktop'
  ? 'Look at the running app through computer use on this machine\'s screen, as docs/plan/ui-build.md section 6 describes for a desktop.'
  : `Look at the running app with tools/ui-harness.sh on Xvfb, as docs/plan/ui-build.md section 6 describes; your display numbers start at ${display}.`

const COMMON = `You are one agent in a workflow that builds the windowed Corvid app on Uno Platform, looking at the running app at every step.
Read docs/plan/ui-build.md first and follow it: the shared rules, the architecture and the verification protocol. Then read docs/plan/ui-handoff.md (where the build stands, including the session core's API) and src/Corvid.App/README.md (the app's conventions and gotchas).
${LOOK} Save screenshots under ${shots}/ and never commit them.
Work only in the files your step owns, keep the solution building with no warnings, and commit your work at the end. Do not push.
Before every dotnet build or test, run \`agent-budget level\`: if it prints critical, wait with \`agent-budget alert --from critical --timeout 300\`; if tight, pass -m:2. Commit messages carry no Claude attribution (no Co-Authored-By, Claude-Session or Generated-with lines).`
// The agents run one at a time, so this script needs no concurrency limiter; a workflow that fans out takes its
// maxConcurrent from agent-budget (see docs/plan/ui-build.md, "Memory").

const STEP = {
  type: 'object',
  properties: {
    status: { type: 'string', enum: ['done', 'partial', 'blocked'] },
    summary: { type: 'string' },
    commit: { type: 'string' },
    screenshots: { type: 'array', items: { type: 'string' } },
    verified: { type: 'array', items: { type: 'string' }, description: 'each check run against the running app or the build, with its result' },
    deviations: { type: 'array', items: { type: 'string' }, description: 'remaining differences from the mockups or the design' },
    notes_for_next: { type: 'string' },
  },
  required: ['status', 'summary', 'commit', 'screenshots', 'verified', 'deviations', 'notes_for_next'],
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
          id: { type: 'string' },
          severity: { type: 'string', enum: ['blocker', 'major', 'minor'] },
          area: { type: 'string' },
          what: { type: 'string' },
          expected: { type: 'string' },
          evidence: { type: 'string' },
          reproduce: { type: 'string' },
        },
        required: ['id', 'severity', 'area', 'what', 'expected', 'evidence', 'reproduce'],
      },
    },
  },
  required: ['pass', 'summary', 'screenshots', 'defects'],
}

phase('Sessions page')
const page = await agent(`${COMMON}

Your step is A0.3, the Sessions page in src/Corvid.App, built on the session core in src/Corvid.App.Core/Sessions (SessionViewModel, the TranscriptBlock kinds, ApprovalCardBlock, ToolCardBlock, SessionPanel; read them before you start).

Build it to match docs/plan/ui-mockups/png/Main.png and Approval.png and their sources in docs/plan/ui-mockups/source/:
1. The conversation header (title, fork pill, goal pill, Aside, Rewind, Compact, pop-out) and the transcript as a virtualised list with a template per block kind: the user message card, the turn header with model, tokens, elapsed time and cost, decision chips (dashed when unsure), escalation, taint, detection and memory rows, tool cards by kind with their verdict line, the running card's live tail and Stop, the streamed markdown answer with a caret while streaming, the thought row, and the approval card exactly as Approval.png draws it (header with countdown, the command, the parsed-command boxes with targets, reasons with codes, the five answer buttons with digit hints, the rule preview with the editable paths field, the next-in-queue line). Open the app with --demo --scene approval to see that moment.
2. The turn strip (spinner, step, queued chips with remove, the steer and interrupt hints) and the composer (multi-line, placeholder, attach, draft state, token estimate, Send).
3. The dock tabs in SessionsDock: Changes (this turn and session lists, the unreadable file row, Open Review), Files, Tasks, Decisions (as Approval.png shows), Context (the gauge and breakdown), Output and Session.
4. Live status bar and rail data from the view models.
5. Keyboard: Enter sends, Shift+Enter breaks a line, Ctrl+Enter steers, Esc interrupts, Esc Esc on an empty composer goes to Rewind, digits 1 to 5 answer the focused approval card, Ctrl+B toggles the dock, Ctrl+1 to Ctrl+7 pick dock tabs, Shift+Tab cycles the approval mode; a card that opens while the user types takes one Enter to focus and a second to answer.
6. "Jump to bottom" while scrolled up during streaming; streaming never moves the scroll position or focus otherwise.

Verify with docs/plan/ui-build.md section 6: compare with Main.png and Approval.png at 1440x900 in light and dark, then the narrow widths. Run flow F2 (send a message in demo mode and watch the turn stream in), F3 (an approval: answer with 2; then with 3, check the rule preview, write the rule; then deny with 4), F6 (queue while a turn runs, take a queued message back, steer with Ctrl+Enter, interrupt with Esc) and F9 (the taint row and the amber badge). Screenshot each state. Then build the whole solution with no warnings, run the checker and the App.Core tests, and commit.

Return what you built, the commit, screenshot paths, the checks you ran with results, remaining differences, and notes for the next agents.`, { label: 'sessions page', phase: 'Sessions page', schema: STEP })

if (!page || page.status === 'blocked') return { stoppedAt: 'sessions page', page }

phase('Verify')
const verifyPrompt = (round) => `${COMMON}

You are the independent verifier for phase A0 (the shell and the Sessions page), round ${round}. Do not edit any file in the repository.

Build the app in Release and run it with --demo (and --scene approval for the approval moment). Check it against docs/plan/ui-mockups/png/Main.png, Approval.png, Main-narrow.png and Phone.png and their sources, and against docs/plan/ui.md flows F2, F3, F6 and F9 and the keyboard map in section 6 for the keys that exist so far. Look at every region at full size, then in the dark theme, then at 1024, 820 and 390 wide. Compare copy, spacing, alignment, type, colours, borders, clipping, overflow, empty states and focus visuals. Drive each flow and confirm each state change on screen. Read the XAML for AutomationProperties on icon-only buttons. Read the app's standard error for exceptions. Run the whole-solution Release build, the checker and the App.Core and architecture tests.

Severity: blocker (a crash, a failing build, check or test, a broken flow), major (clearly differs from the mockup or the design: a missing element, a wrong layout, a broken interaction, unreadable contrast), minor (polish). pass is true only when no blocker or major defect remains. Each defect needs concrete evidence and steps to reproduce it.`

const fixPrompt = (round, defects) => `${COMMON}

Notes from the Sessions page step:
${page.notes_for_next}

Your step is fix round ${round} for phase A0. The independent verifier found these defects. Fix every blocker and major one, and the minor ones that are quick and safe. For each fix, rerun the check that showed the defect on the running app and confirm it is gone. Then build the whole solution with no warnings, run the checker and the App.Core and architecture tests, and commit.

Defects:
${JSON.stringify(defects, null, 2)}

Return what you fixed, the commit, screenshot paths, the checks you ran with results, the defects you left and why, and notes for the next agents.`

let round = 1
let report = await agent(verifyPrompt(round), { label: `verify ${round}`, phase: 'Verify', schema: DEFECTS })
const fixes = []
while (report && !report.pass && round < 3) {
  const fix = await agent(fixPrompt(round, report.defects), { label: `fix ${round}`, phase: 'Verify', schema: STEP })
  fixes.push(fix)
  round++
  report = await agent(verifyPrompt(round), { label: `verify ${round}`, phase: 'Verify', schema: DEFECTS })
}
if (report && !report.pass) log(`The verifier still reports ${report.defects.filter(d => d.severity !== 'minor').length} blocker or major defects after ${round} rounds`)

return { page, fixes, finalReport: report, rounds: round }
