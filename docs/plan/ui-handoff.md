# Windowed UI: handoff

2026-10-24. Where the build of the windowed Corvid stands, how to pick it up on another machine, and what comes next.
The design is [`ui.md`](ui.md), the approved mockups are in [`ui-mockups/`](ui-mockups/README.md), the rules every
builder follows are in [`ui-build.md`](ui-build.md), and the app's own conventions and gotchas are in
[`src/Corvid.App/README.md`](../../src/Corvid.App/README.md). Everything is on branch `claude/zen-allen-puw590`, draft
pull request starlinghq/corvid#71.

## 1. Where it stands

| Step | State | Commit |
| --- | --- | --- |
| Design (`ui.md`): features, flows, keys, architecture, work items G-1 to G-15, decisions U-1 to U-11 | done | `1f5e478` |
| Mockups (`ui-mockups/`): ten artboards, approved | done | with this handoff |
| A0.1 Scaffold: projects, checker rules, theme, fonts, icons, the shell with every area, responsive layouts | done, verified on the running app, CI green | `83d77f9` |
| A0.2 Session core: update contract, fold, demo scenario, view models, markdown and diff | built and tested headless; not yet seen through a page | `a143605` |
| A0.3 Sessions page: transcript, composer, approval card, dock, keyboard | not started: **next** | |
| A0 verify loop | not started | |
| A1 the seven other mocked screens | not started | |
| A2 the pages without mockups, the palette, the rest of the keys | not started | |
| A3 `Corvid.App.Engine`: the real runtime behind the services | not started | |

The build ran as a workflow in a cloud container: one agent per step, each building, launching the app on a virtual
display, screenshotting it and comparing it with the mockup renders, then an independent verifier. It was stopped during
A0.2 to move to another machine. A0.2's agent had written all of its code and tests but not run its own final check. Its
work was finished on stopping:

- Test calls now pass the test's cancellation token, as analyzer xUnit1051 requires here.
- `--scene` now rejects numbers such as `1`, which `Enum.TryParse` had accepted.
- Three test expectations were wrong and are fixed. The running call's checkpoint c-44 is the main scene's eighth ledger
  entry. A refused link's text joins the plain text after it. The large-rewrite diff no longer negates 0.

The whole solution then built with no warnings. The checker passed, along with 134 of 134 App.Core tests and 267 of 267
architecture tests, and the app launched with the live session list.

### What the running app shows today

The shell matches `Main.png` outside the conversation: rail, badges, sessions with their dots and times, status bar,
mode control, dock tab strip. It also matches in dark, and at 1024, 820 and 390 px wide, where the phone layout and its
bottom bar take over. Every rail item navigates. Each area is a placeholder page with its title, including the Sessions
conversation and the dock panels' bodies. The scaffold's report lists these remaining differences:

- The status bar has toggles for the rail and the dock, shown only while that panel is hidden. The mockups lack them.
  Without them a mouse user could not bring a hidden panel back.
- The phone top bar's subtitle is the workspace, not "attached to mark-ws", because this is the in-process app.
- Badge counts use Inter SemiBold where the CSS asks for bold, since only weights 400, 500 and 600 are embedded.
- Popups and tooltips follow the application theme, not a theme switched at run time.
- The status bar items are not click targets yet, and Board, Audit and First run do not have their own variants yet.
- No app or window icon yet. The assembly is `Corvid.App`, not `corvid-app` (U-7).
- The WebAssembly head has never been built, for want of the `wasm-tools` workload.

## 2. Picking it up on another machine

```sh
git fetch origin && git checkout claude/zen-allen-puw590
dotnet --version                      # 10.0.401 or a later 10.0.4xx (global.json)
dotnet build Corvid.slnx -c Release   # restores Uno.Sdk 6.7.30 from NuGet; no workload needed
dotnet src/Corvid.App/bin/Release/net10.0-desktop/Corvid.App.dll --demo
dotnet src/Corvid.App/bin/Release/net10.0-desktop/Corvid.App.dll --demo --scene approval --theme dark --page sessions
dotnet test --project tests/Corvid.App.Core.Tests/Corvid.App.Core.Tests.csproj -c Release
dotnet run --project tools/Corvid.Architecture -c Release --no-build -- .
```

- The desktop head runs on Linux (X11, or XWayland), Windows and macOS. Demo mode needs no models, no Vault and no
  sandbox, so the UI work can continue anywhere.
- Only the engine phase (A3) needs Corvid's real runtime, which is Linux-first: the models, bubblewrap and Vault.
- Everything the cloud session kept outside the repository is now inside it: the mockups and their renders, the build
  spec, the headless harness (`tools/ui-harness.sh`) and the next workflow.
- The design canvas the mockups were drawn on is a private claude.ai artifact. Its files are copied in
  `ui-mockups/source/`.

## 3. Continuing the build

### The next workflow

`.claude/workflows/corvid-app-a0-sessions.js` (workflow name `corvid-app-a0-sessions`) runs step A0.3 and the verify
loop. It builds the Sessions page against `Main.png` and `Approval.png`, drives flows F2, F3, F6 and F9 on the running
app, then has an independent verifier report defects. Up to two fix rounds follow. Its arguments:

| Argument | Values |
| --- | --- |
| `method` | `desktop` to look through computer use on this machine's screen; `harness` (the default) for Xvfb on Linux |
| `shots` | where screenshots go, `ui-shots` by default (git-ignored) |
| `display` | the first Xvfb display number for the harness, 103 by default |

With computer use, run one UI agent at a time: parallel agents would fight over one screen. Parallel screen builders
need the harness's private displays, and their own git worktrees so their builds do not collide.

### The plan after A0

Each phase is one workflow in the same shape: builders, then an independent verifier, then bounded fix rounds. Every
builder looks at the running app.

1. **A1, the seven other mocked screens**, in five groups that can run in parallel worktrees. An integrator merges the
   branches one at a time, building and checking after each. Each area owns its folders, so merges touch few shared
   files.
   - Review and Rewind together, since they share a diff view. Flows F4 and F5.
   - Board with the attention tray. Flow F18.
   - Models and First run. Flows F1, F14 and F23.
   - Memory. Flow F11.
   - Audit with the egress ledger. Flows F8, F10 and F21.
2. **A2, the pages the mockups did not draw**, from `ui.md` §4: Skills (§4.9), Jobs, MCP and Gateway (§4.10), Safety
   (§4.4), Secrets (§4.11) and Settings (F24). This phase also brings the palette (`Ctrl+K`), the rest of §6's keys, the
   phone layout's screens and an accessibility pass with a screen reader.
3. **A3, `Corvid.App.Engine`.** The in-process services over `CorvidRuntime`:
   - turn updates from `TurnFeed` and the event log, as `Corvid.Acp`'s `TurnReporter` builds them
   - approvals through an `IApprovalPrompter`, and the shared slash-command registry
   - models and slots, and `Doctor` checks feeding First run when models are missing
   - memory and lessons, rewind through the checkpoint ledger, and audit over the event log
   
   The kernel and host work items K-n and G-n come with the features that need them. The XML docs on the update
   contract say which updates need which item. The phase ends with an end-to-end run on real models, the WebAssembly
   head with `wasm-tools`, and a final walk through every flow.

## 4. The session core: what the Sessions page binds to

All of it is in `src/Corvid.App.Core/Sessions/`.

- **The seam.** `ISessionService` is the app's only seam to an engine. The demo implements it now and
  `Corvid.App.Engine` will later:
  - `ListAsync` returns `SessionSummary` records.
  - `OpenAsync(id, onUpdate)` returns an `ISessionSubscription` whose `History` is the replayed log; live updates follow
    through `onUpdate` on the UI thread.
  - Requests: `SendAsync`, `QueueAsync`, `RemoveQueuedAsync`, `MoveQueuedAsync`, `TakeBackQueuedAsync`, `SteerAsync`,
    `InterruptAsync`, `AnswerApprovalAsync`, `AnswerQuestionAsync`, `RunCommandAsync`, `SetApprovalModeAsync`,
    `SetGoalAsync`, `CompactAsync`, `ForkAsync`, `RenameAsync`.
  - `AnswerApprovalAsync` takes an `ApprovalAnswer`: Once, Session, Always (with narrowed `RulePaths`, which needs G-11)
    or Deny (with an optional reason sent as the next message).
- **The update contract.** `SessionUpdates.cs`, `ToolUpdates.cs`, `ApprovalUpdates.cs` and `CorvidUpdates.cs` define it.
  `SessionFold` turns updates into `SessionState` (`SessionFold.Replay`), and `SessionHub` fans them out.
- **The view model.** `SessionViewModel` has these parts:
  - `Blocks`, an `ObservableCollection<TranscriptBlock>`.
  - The composer: `Draft`, `Queue` (`QueuedChip`s with remove, take-back and move commands), `SubmitCommand`,
    `SteerCommand`, `InterruptCommand`, `TakeBackLastCommand`, the turn strip's text and hint.
  - The approvals: `CurrentApproval` and `HasApproval`.
  - The dock panels, in `SessionPanel`: turn and session files, the context gauge and breakdown, tasks, decision groups,
    output and session facts.
  - The status strings, and the events `StateChanged` and `FocusRequested`.
- **The block kinds.** `UserMessageBlock`, `TurnHeaderBlock`, `DecisionsBlock` (`DecisionChip`), `SwitchBlock`,
  `TaintBlock`, `ToolCardBlock` (`FileDiffView`), `ApprovalCardBlock`, `AnswerBlock`, `ThoughtBlock`, `NoticeBlock`,
  `DetectionBlock`, `CompactionBlock`, `RewindBlock`, `QuestionBlock` and `CommandBlock`. `ApprovalCardBlock` carries
  `ParsedCommandView`, `ReasonView`, `ApprovalOptionView`, `RulePreviewView`, `RuleRowView` and `DetectionView`.
- **The demo.** `DemoSessionService` has two scenes. `--scene main` is the default, `Main.png`'s moment: turn 12
  running. `--scene approval` is `Approval.png`'s: turn 13 waiting on a-32, with a-33 behind it. The sessions are
  `cli-9f2e` (the main one), `cli-a410`, `cli-77b0`, `cli-c3d2` (held by a terminal, read-only) and
  `job:nightly-tidy:2026-10-24`. Sending a message runs a scripted turn.
- **Markdown and diff.** `Markdown/` turns text into Markdig blocks with inline runs; links keep only web and mail
  addresses, and images are never fetched. `Diffs/` holds a line diff with word highlights, in unified and side-by-side
  rows.

## 5. Decisions taken while building

- **U-3:** MVVM on hand-written basics, not MVUX, so the core stays free of Uno types.
- **U-2:** XAML.
- **U-4:** the Skia desktop head on every OS.
- **U-5:** markdown in native text blocks from a Markdig model, with no WebView.
- **The WebAssembly head** is opt-in until CI can install `wasm-tools`.
- **Hot Design and Uno's in-app MCP endpoint are off.** They bring CommunityToolkit, Uno Toolkit and Uno Themes, which
  the checker refuses.
- **The run-time theme** is the root page's `RequestedTheme`, because Uno sets `Application.RequestedTheme` only before
  start-up.
- **Under Xvfb, the software renderer.** With OpenGL, Uno sometimes keeps a stale frame after a shrinking resize.

U-1 (in-process first, the update stream as the only seam) is followed. U-6 to U-11 stand as `ui.md` proposes.

## 6. Risks to watch

- The session core has only been exercised by tests and the rail's session list. A0.3 is its first real consumer; expect
  changes to its API.
- Uno quirks found so far are in the app README's gotchas: theme brushes on `Run`s, line-height maths, the order of
  `Navigated` and `OnNavigatedTo`, stroke widths rounded to whole pixels. Read them before writing XAML.
- CI builds the desktop head on a self-hosted runner with no display. Nothing in the app's build may need one.
