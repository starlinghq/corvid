# Windowed UI: handoff

2026-10-25. Where the build of the windowed Corvid stands, how to pick it up on another machine, and what comes next.
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
| A0.2 Session core: update contract, fold, demo scenario, view models, markdown and diff | done, tested headless and through the Sessions page | `a143605` |
| A0.3 Sessions page: transcript, composer, approval card, dock, keyboard | done, flows F2, F3, F6 and F9 driven on the running app (§1.1) | `93d6c02` |
| A0 verify loop | done: one independent verifier, one fix round (§1.1) | `60e90fd` |
| A1 the seven other mocked screens: Review and Rewind, Board, Models and First run, Memory, Audit | done, verified on the running app by an independent verifier in two rounds (§1.2) | branch `a1/screens` |
| A2 the pages without mockups (Skills, Jobs, MCP, Gateway, Safety, Secrets, Settings), the palette, the rest of the keys, the phone screens, an accessibility pass | done, verified on the running app in three rounds, PR review fixed (§1.3) | PR #76, branch `a2/screens` |
| A3 `Corvid.App.Engine`: the real runtime behind the services | done behind `--engine`, verified end to end in three rounds, PR review fixed (§1.4) | PR #77 |
| A4 the gaps A3 left: the running turn, records, egress, hosting, parity; the engine becomes the default | done, verified end to end in three rounds (§1.5) | branch `a4/engine` |
| A5 model switching at any time, subagents with a model mix, G-15 forks mid-turn, the A4 leftovers | done, verified end to end in two rounds (§1.6) | branch `a5/engine` |
| A6 attached mode (G-1), workspaces and worktrees per session (G-4), images (K-8, F19), search across sessions (G-5), the `corvid-app` binary and its tarball (U-7, U-9), the A5 leftovers | done, verified end to end in two rounds (§1.7) | branch `a6/engine` |

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

### 1.1 The Sessions page (A0.3)

Built on this machine (Linux, no desktop session for the app) after the move, as one builder rather than the saved
workflow, against `Main.png` and `Approval.png`. It matches both at 1440x900 in light and dark, `Main-narrow.png` at
820, and keeps working at 1024 and 390. What was checked on the running app, by screenshot after each step:

- **F2.** From an idle session a message sends and its turn streams in: chips, a tool card, the answer's markdown with a
  list and a code block, "thought for 0 s"; the view follows while at the bottom; the status bar goes back to idle.
- **F3.** Enter in the composer focuses the card and sends nothing; 2 answers "this session" and the next card (a
  `patch_file` with its diff) takes over with the draft kept. 3 opens the rule preview; widening the paths to `src/` is
  refused ("src/ is not under build/; a rule can only be narrowed") and disables the button; "Write the rule and allow"
  answers, and the card reads "approved always (rule: …)". 4 denies the next card.
- **F6.** Enter while the turn runs queues a chip; Up on an empty composer takes it back (caret at its end); Shift+Enter
  breaks the line; Ctrl+Enter adds a "steer" chip; Esc interrupts ("· interrupted" on the turn header) and the queue
  goes next.
- **F9.** The amber taint row in the transcript and the "tainted" tag in the status bar.
- **Keys.** Ctrl+1 to Ctrl+7 pick dock tabs, Ctrl+B hides the dock, Shift+Tab cycles the approval mode, Esc Esc on an
  idle empty composer opens Rewind, digits answer only a focused card.
- The whole solution builds in Release with no warnings; the checker passes; App.Core 138 of 138 and architecture 267
  of 267 tests pass.

An independent verifier then checked the build, the tests, both mockups in light and dark, the four widths, the four
flows and the keys. It found no blockers, two major defects and twelve minor ones. The fix round, rechecked on the
running app:

- The status bar's mode segments now give way under 860 px, so "tainted" and "2 approvals" stay visible (the narrow
  mockups drop the segments; Shift+Tab still cycles).
- The jump pill stays away while an approval waits, and focusing a card scrolls its answers, not its top, into view.
- Tool card heads are 46 px as in `Main.png`; parsed commands stack in one column under 860 px; long target chips and
  turn heads end in an ellipsis or wrap rather than being clipped; the opening scroll position is taken twice so the
  repeater's height estimates cannot move it; "Deny and say why" puts the caret in the reason field; the answers lost
  their tooltips, which covered the reasons on keyboard focus; a thought renders its code spans.
- Each item above was looked at again on the running app after the fix: the approval scene at 820 and 390, four launches
  in a row opening at the same place, F3 with the next card's chip, the arrival of a card while scrolled up, 5 then
  typing, Ctrl+T. `WrapRowPanel` now fits each item to its line and centres it on the line; the dock's tab strip, its
  other user, draws as before.

Left for later, from that report: an inline code span wraps as a unit and can leave "(" at a line's end; after Back,
the focused answer keeps its outline; Esc Esc leaves focus (and its tooltip) on the attention bell; the demo's idle
session `cli-77b0` shows no files in Changes though its turn edited one (the demo never sends that change), the
Decisions panel lists earlier turns' groups below this turn's (`Approval.png` shows only this turn's), and with every
checkpoint's chips shown (Ctrl+Shift+D) a pre-execute group lands after its card: the last two are the fold's, in
`SessionFold`. The scripted F2 turn says "thought for 0 s". Wheel scrolling could not be driven through the XTest shim.

Decisions and differences that remain:

- A session opens scrolled to its latest message (Main.png's moment). Streaming never moves the view unless it is at
  the bottom; scrolled up during a working turn, a "Jump to bottom · N new" pill shows (`ui.md` §2).
- Aside (F7) and pop-out (more windows) are present but disabled with tooltips, as are the attach button, "Send comments
  (0)" and "Export decisions": the flows behind them come later.
- Inline code in answers has no grey background: native text runs cannot carry one (U-5). Code blocks have it.
- The Files tab shows its one-line empty text: the model has no file tree yet (it comes with the engine's listing).
- At 390 px the page drops its header (the phone top bar has the title) and the key hint, and the approval card uses
  the phone's head ("Approval 1 of 2", "4:12 left") and full-width answers. Phone.png's compact parsed rows, short
  reasons and one-line composer are A2's phone screens.
- Uno's TextBox types the line break of an Enter the page has handled, so the page cancels that one change
  (`BeforeTextChanging`). x:Bind paths on the page itself do not follow a static source, so the phone switches set the
  header's and hint's visibility in code; templates do bind to `SessionsLayout.Current`.

### 1.2 The seven other mocked screens (A1)

Built by the workflow `.claude/workflows/corvid-app-a1-screens.js`: five builders in parallel, each in its own worktree
and headless display (Review and Rewind; Board with its attention tray; Models and First run; Memory; Audit with the
egress ledger), one integrator merging their branches into `a1/screens` (no conflicts), then an independent verifier
with a fix round. Round 1 found two major defects: focused controls redrew in the dark theme's brushes under `--theme
light`, and First run's end state (an empty session, the degraded badge) was not reached. Both were fixed. Round 2
passed with ten minor ones. Each area has its core, demo service and tests in `Corvid.App.Core/<Area>/` (App.Core tests
went from 152 to 250), and each screen was compared with its render at 1440 in light and dark, at 1024, at 820 against
the narrow render, and at 390. Flows F1, F4, F5, F8, F10, F11, F14, F18, F21 and F23 were driven on the running app.
Shared additions: `SideBySideDiffView` and `PathText` (Review and Rewind), `DemoSessionService.Rewind` (a restore on the
session's stream), Ctrl+Shift+C for Review.

The pull request (#75) then had an independent code and security review in three parts, and three fix agents, one
per area, worked through it. What changed because of it:

- #73's hidden-character rule now runs through A1: memory approvals, the egress "as sent" view, raw event JSON, paths
  a person acts on, the Board's question options and download names are revealed; a memory fact or rule holding a
  hidden character cannot be approved until it is corrected; display-only text is cleaned; outside values put into
  markup are escaped. Only well-formed `[redacted:kind]` markers count, and a count that disagrees with the request's
  is flagged.
- A restore reaches the session's change lists (the fold marks the rows it put back and lowers the turn), Review's
  revert goes through the session's stream so Rewind lists it, and Review's checkpoint comes from the ledger: it reads
  c-42 where `Review.png` says c-44, a checkpoint that already held the change.
- The side-by-side diff draws at most 400 rows, rebuilds once per change and no longer leaks across visits; the
  comment field keeps its focus through rebuilds; Restore takes focus only when the preview it ended had it.
- Memory shows its failures, guards each card against double answers and picks an update target only above a score
  of 0.75 (otherwise the person picks); Audit refilters without rebuilding its table and links land on what they name.
- Areas that write the status bar give back what they replaced (`StatusBarLease`); First run follows a mode picked
  after Finish; the composition disposes the Board's ticker and the model service's timers.
- The Memory demo's persona is neutral.

Left:

- The theme chosen in Settings applies to focus and hover colours only after a restart: Uno 6.7 refuses to change
  `Application.RequestedTheme` while running (`NotSupportedException`). Settings says so.
- First run at 390 shows the session's bottom bar and subtitle (needs the shell's phone flags).
- First run's Finish leaves Sessions with no session open but the header's actions and Send look enabled.
- Rewind's preview stacks the two sides of its diff, so unchanged lines read twice; going back to before a restore
  point lists its paths but does not un-mark the rows that restore put back; a reverted file stays "reverted to c-NN"
  until it changes again.
- Board: "Open a workspace…", "New session", "Take over" and "Notify when unfocused" draw disabled (their flows come
  later); `Board.png` shows them enabled.
- After a card is removed by a click, the next card's first button draws a focus ring (the Uno focus quirk in the
  app README).
- Seams for the engine: `IMemoryService.ImportForReviewAsync` now takes the dry run's entry ids; Audit's spans and
  redactions should come as structure from the engine and the gate (G-9), and a trace's "full content" writes only the
  summary today (documented on `IAuditService.ExportAsync`).
- The Sessions page opens with the jump pill over the streaming answer (A0's behaviour, by design in `ui.md` §2).
- After a restore lowers the turn, the next prompt reuses the rolled-back turn's number, so a conversation-only
  rollback's still-live changes count in Review's "this turn". Turns need an identity beyond their number.
- The Board's revealed question option (with the raw option sent) has no test: the demo cannot inject a question.

Each builder's own list of differences from its mockup is in the workflow run's report; the verifier judged them
acceptable.

### 1.3 The pages without mockups, the keys and the phone (A2)

Built by `.claude/workflows/corvid-app-a2-reach.js`: four builders in parallel (Skills; Jobs, MCP and Gateway; Safety
and Secrets; Settings with the command palette and the rest of `ui.md` §6's keys), an integrator, one agent for the
phone layout and an accessibility pass, then an independent verifier with fix rounds. The builders had no mockups:
each page follows `ui.md` §4 and the look of the mocked pages, and A1's review findings were written into their rules
(SafeText on untrusted text, no leaks, capped lists, failures shown, the status bar lease, no writes in demo mode).
Round 1 found three major defects (a Gateway allowlist that could grant a tool whose name hid characters; the Board's
"Pair as owner" granting owner in one click; literal hidden characters in tests), round 2 three more (a status bar fix
that hid the taint tag; the palette drawing two selected rows; job prompts and commands not revealed before Resume);
all were fixed, and round 3 passed with nine minor defects. App.Core tests went from 327 to 580.

Accessibility could not be checked with a screen reader here: Uno 6.7's X11 runtime has no AT-SPI support, so on
Linux the app exposes no accessibility tree at all. The pass audited names, headings, tab order, keyboard reach, focus
visibility and colour-only meaning in the XAML and the running app. Screen-reader checks need Windows or macOS.

The PR review (agent review and security review on #76) found one high defect in each of two areas: the Board tray's
MCP "Accept" pinned a changed tool definition in one click, and a duplicate key in the raw settings editor threw from a
binding setter. Three fix agents, one per area, fixed every finding and round 3's nine minor defects. App.Core
tests are 642. Decisions taken in that round:

- The Board tray no longer accepts MCP tools or pairs guests: "Review…" and "Pair as guest…" open the item on its page,
  where the text is shown revealed. This drops `Board.png`'s Diff and Accept.
- The bell counts every attention item (5), where `Main.png` and `Phone.png` draw 3: the tray lists all of them.
- The Skills badge shows from start-up (`Main.png` draws none), since a draft is waiting.
- The phone's turn strip shows while a turn works or has a queue, since the phone has no status bar.
- "Record only" asks first, naming the enforcing rules; turning enforcement back on is one click.
- SKILL.md content uses a joiner-tolerant hidden-character check, as Memory does; names and fields stay strict.
  `SafeText` keeps a joiner or variation selector only where it does its work (between two characters outside ASCII
  or after an Indic virama; once after a base that has variants), shows the ideographic selectors always, and treats
  Mongolian's free selectors and Khmer's inherent vowels as hidden, so a run of them cannot carry a message unseen.
- Safety's demo names the detection `credential-read-then-egress` v3 as Audit's demo does; the real pack calls it
  `secret-read-then-egress` v1, so Audit's demo should change when A3 reads the engine.
- Uno drew no sideways scroll bar at 390, so wide ops tables show a "scroll it sideways" hint.

Left:

- A relative one-off job keeps its due time only in the demo service; the engine's job record needs a due-time field.
- Models' wide tables at 390 still give no sign that they scroll.
- `SafeText` still lets a little through unseen: a joiner between two characters outside ASCII and one selector after a
  base with variants carry a bit or a few each. Exact lists (emoji-variation-sequences, the standardized variants)
  would close it; Arabic's word-final joiner and the subdivision flags' tag characters are shown though ordinary.
- After a typed save that kept raw-editor edits, saving the raw file writes its older values for those keys (the
  notice warns).
- Not seen on screen, tests only: tainted plus read-only on the phone, a hidden model name under Alt+P, Check again
  after a failed Vault read, Review past MCP's 12-card cap.

### 1.4 The engine behind the services (A3)

Built by `.claude/workflows/corvid-app-a3-engine.js`: a foundation, five builders in parallel worktrees (sessions and
approvals; Rewind, Review and Audit; Models, First run and Settings; Memory, Skills and Safety; Jobs, MCP, Gateway and
Secrets), an integrator that added the Board and prompt history over the merged services, an end-to-end pass, and an
independent verifier with fix rounds (round 1 found one major defect, round 2 one major and three minor; round 3
passed with five minor ones).

- **`--engine`** runs the app on the real runtime; `--demo` stayed the default until A4, which made the engine the
  default (§1.5). `AppComposition` takes an `IAppServices`
  (`DemoAppServices`, `EngineAppServices`, or `UnavailableAppServices`, which refuses each area by name). The demo's
  Review and Rewind no longer cast the session service.
- **K-10** is in `Corvid.Host/Sessions`: `SessionStreamUpdate`, `SessionUpdateReporter` (Acp's `TurnReporter` logic
  moved to the host) and `SessionReplay`. Corvid.Acp now reports turns and replays sessions through it, with its
  recorded exchanges unchanged. The engine's `SessionUpdateTranslator` maps the stream onto App.Core's updates.
- **`CorvidConfigWriter`** (host): validated, atomic edits of corvid.json that report restart and shadowed keys;
  `corvid model switch`, Models, First run, Settings, MCP and Secrets use it.
- Host and kernel items done along the way: G-3 (restore scopes, rollback by checkpoint, one file from the ledger),
  G-10 (per-session approval mode, in-process), G-11 (narrowed always rules), K-1 (thoughts, live only), K-5 (usage,
  partly), K-7 (list and remove approval rules), G-13 (schedule preview), and `ISessionService.CreateAsync` so a fresh
  home can start a session.
- End to end in temporary homes: a scripted generator, a home with nothing downloaded, and real models (qwen3.5-0.8b,
  kev-0.8b, gliner2 and embeddinggemma on the CPU, read from the Starling cache). F1, F2, F3, F4, F5, F6, F9, F11, F13,
  F18, F21, F23 and F24 work on the engine; the WebAssembly head now builds.

The PR review (four agents, one of them on security) found one high defect, approvals.json rewritten whole by each
process so the CLI and the app lost or resurrected each other's rules, and eleven medium ones; three fix agents fixed
them all. What changed with them: approvals.json and corvid.json are edited under a file lock shared across processes
(the rules re-read on change; corvid.json edited in place, keeping comments, its mode and a symlink); `skills.db` moves
to schema 3 (one way: an older binary refuses it) so an edited draft applies only over the revision it was staged
against; First run starts from the configured approval mode and cloud posture and writes only what the person changes;
Safety searches the 50 most recent sessions.

Refused on the engine at the end of A3, each with its message on the page: the egress ledger (G-9), hosting the gateway
in the app (G-14) and the per-person guest allowlist, rename (G-6), steering (K-3), clarifying questions (K-6), the output
tail (K-2), turn progress, app preferences beyond the window (G-7, ui.db), memory consolidation (plan §9.5), opening a
terminal or the Vault helper (U-6), and the Vault policy and daily counts (V-n). A4 built all but the last three (§1.5).

Left at the end of A3 (A4 closed every item here except those §1.5 still lists):

- The `#` (stage a memory) and `!` (shell) composer modes are advertised but built nowhere; the engine sends them as text.
- Replay differs from live: no generator or decisions per replayed turn, an interrupted turn or one cut by a rollback
  replays as "failed", the taint row is lost, compaction markers show only live.
- Settings and Models changes apply at the next start (the runtime reads corvid.json once); First run does not restart
  the runtime after its downloads.
- Layout with engine-length text: Models' slots table at 1440, Rewind's checkpoint ids, Audit's raw JSON, Review's
  "Checkpoint" label.
- Memory's Reject loses its reason's focus; the Hermes import's staging session shows in the rail.
- Rewind's restore and Review's revert can write to a session another process drives (G-12). Job runs are read-only.
- The round-3 minors: the turn strip offers steering the engine refuses; a new session's rail entry stays "New
  session" while its first turn runs; Send looks enabled with no session.
- A preview taken while a turn runs is labelled as such rather than drafted on the actor; a call that crashed with no
  record of its changes still cannot be rolled back past (the kernel refuses it, now with its own reason).
- From the fix verification (low): an interrupt pending when another surface posts between the window's head read
  and its post can cancel that turn; First run writes a choice an environment variable or `--set` overrides without
  saying so; a broken approvals.json now refuses side-effecting calls mid-run (it is re-read on change); a rule write
  waiting on the CLI's lock holds every approval check for up to 10 s; a read-only config or approvals directory reads
  as "another process is writing".
- Not walked on the engine: F8, F10, F12, F14's switch and download, F15, F16, F17 with a Vault.

### 1.5 The gaps A3 left (A4)

Five groups in parallel worktrees, merged one at a time by an integrator (`a4/gaps-int`), building and running every
test project after each merge:

- **The running turn (`a4/turn`).** K-3: Ctrl+Enter steers the running turn; the kernel takes a steer at its next step
  checkpoint (`UserSteered`, a user message that starts no turn), and one the turn ends without taking goes to the head of
  the queue. K-6: `ask_user` ends a turn Clarifying with a question (`ClarificationAsked`); the queue holds until the
  person answers, the rail shows it waiting, and an answer is sent as their words (a `#` line typed then stages its fact and the question stays open; a `!` line is
  refused until the question is answered, in the app, the engine and `corvid chat`).
  K-2: a running call's output tail (shell, web_fetch), bounded, scrubbed and throttled. Turn progress: the step on the
  turn and the rail. `corvid chat` shows the step and a running call's last line, and numbers a question's choices; it
  has no steer (it reads no input while a turn runs).
- **Records (`a4/records`).** G-6: generated titles (local generator only), rename, recaps on request and after an
  absence (`SessionTitled`, `SessionRecapped`), shared by the app, `corvid chat` (`/sessions`, `/rename`, `/recap`, `/pin`)
  and ACP's `session/list`. G-7: `ui.db` for pins, drafts, review points and the app's preferences. The composer's `!`,
  `!!` and `#` lines, parsed once by the host's `ComposerLine` for the CLI and the app.
- **Egress (`a4/egress`).** G-9: the egress ledger (`egress.db` beside `events.db`, `EgressRecorded` in the session log),
  on the Audit page and in `corvid egress`. The store keeps requests for `Corvid:Privacy:Ledger:RetainDays` (90 by
  default; 0 keeps any age) and at most `MaxMegabytes` of message text (256; 0 for no cap): requests past either go, then
  the message bodies no request still uses, when a process first records into it and at most daily after (the store
  records the last prune), and at once with `corvid egress prune`, which also compacts the file. G-2: why a call that asked nobody ran or was refused (`ApprovalAssessed`), on
  its card, live and on replay.
- **Hosting (`a4/hosting`).** G-12: one driver per session across processes (a lock under the data directory); a session
  another process drives is followed read-only, its turns tailed from the log. G-14: the app hosts the gateway under the
  same one-lock rule as `corvid gateway`, with a per-person guest allowlist (`gateway.db` schema 2 on the first one) and
  `corvid gateway pair tools`; the Jobs page takes the scheduler over when its holder exits.
  After the A4 review (security M1, the user's decision) hosting is opt-in: the app hosts neither the gateway nor the
  scheduler unless `Corvid:App:HostGateway` or `Corvid:App:HostScheduler` is on (Settings › Hosting) or Start hosting on
  the Gateway or Jobs page was answered (it asks once, naming what hosting does: the enabled adapters connect with their
  Vault tokens; scheduled jobs run with their tools; it is remembered through the config writer). Stop hosting stops it
  and releases the lock. While asked for and held elsewhere, the engine's own timer (`AppEngine.TakeOverInterval`, 10 s)
  takes it over once the holder exits, with no page open (review M3). The status bar's "hosting" item, and the Gateway
  and Jobs status lines, say whenever this app hosts either.
- **Parity (`a4/parity`).** Replay carries each turn's generator, the checkpoints' decisions, taint and compaction
  markers; an interrupted turn replays as interrupted. The approval mode, approval deadline, steps and output tokens
  apply live from Settings. First run and Models start the runtime after their downloads without a restart. The A3
  layout items with engine-length text, a read-only config directory said as such, the rule-write stall, and the round-3
  minors (Send disabled with no session; a new session's title follows its first message).
- **The engine is the default.** The app opens on the engine with no arguments (First run when models are missing);
  `--demo` opts into the demo, and `--engine` is still accepted (it says the default). The browser head runs the demo.
  Mockup comparisons and the harness pass `--demo` explicitly.

Six new event types (`user_steered`, `clarification_asked`, `session_titled`, `session_recapped`, `approval_assessed`,
`egress_recorded`): a binary older than A4 cannot read a log that holds them, the same one-way step earlier event
additions took.

Still refused on the engine, each with its message: memory consolidation (plan §9.5), opening a terminal or the Vault
helper (U-6), the Vault policy, role, budget and daily counts (V-n), media memory (plan §9.4) and images in prompts (K-8).

Left (A5 closed every item here except those §1.6 still lists):

- A question that blocks mid-turn (a question router beside the approval router); ACP has no tail or question
  notifications; `corvid chat` shows `ask_user`'s call in its panel.
- A steer is reported wherever the reporter next reads the log, not at a fixed place relative to the call that finished.
- The Board's "Take over" stays disabled even when the lock is free; whether held sessions belong in the rail. (Since the
  A4 review the app lets go of a session's driver lock once it stops viewing it and nothing of it runs or waits there,
  and after a Rewind restore or a Review revert it opened: `CorvidRuntime.ReleaseDriverAsync`.)
- A job's message is still read as the user's on replay (the log does not say a tainted job wrote it), so a replay can
  show the taint cleared where the live turn kept it. A gateway guest's is not: since the A4 review `UserMessage.From`
  names the guest, the prompt quotes it as outside content, and it never answers the owner's open question.
- The lock files under `drivers/` are never removed (deleting a lock file another process may have just opened would let
  two processes hold "the" lock); they are empty and one per session ever driven.

The A4 end-to-end pass (engine by default, a scripted generator, Qwen3.5-0.8B for titles) fixed: sessions another
process starts reaching the rail and the Board without a restart (the log is read every 3 s), a replayed call's time,
Audit's session titles, a refusal's reason staying shown after its draft is kept, the Gateway page reading the store on
each visit (a terminal's `pair approve`, `revoke`, `tools`), and titles and recaps with the local generator (it refuses
sampling without a seed). Still open from that pass (A5 closed all but the last two, §1.6):

- Token use is not in the log, so a replayed turn has no "0.2k tokens" and the context gauge is empty until the next turn.
- A session's first opening after the app starts can land mid-transcript, with "Jump to bottom".
- Text typed in the composer before a new session has opened (Ctrl+N while the runtime starts) is lost.
- `Sandbox:LauncherPath` naming the real `corvid-sandbox` breaks every shell call: Corvid's state protection denies the
  launcher's own path, and its stage 2 re-executes itself inside bubblewrap ("execvp ... Permission denied"). The app's
  build output also has no `corvid-sandbox` beside it, so without the setting doctor fails it.
- `Privacy:LocalServer: Redact` (and Cloud) redaction is far too eager with GLiNER2: JSON keys, "status", "details" and
  the system prompt's words are replaced, so a redacted model sees mangled prompts and tool results.
- A gateway adapter needs Vault for its token, so nothing in a temporary home can deliver a message; pairing, tools and
  revoke were exercised through a seeded pairing code and the CLI.
- With the 0.8B generator a title can be the answer's first sentence rather than eight words.

The independent verifier ran three rounds: round 1 found a blocker (hidden characters in `ask_user`'s options reaching
the answer sent) and a major defect, round 2 one major and seven minor, all fixed but one; round 3 passed with four
minor ones.

The PR review (four agents, one on security) found no high defect. Its fixes, beside the hosting decision above:

- The lost steer was the approval card taking focus: letters typed after Enter went to the card and a space pressed its
  focused "Allow once", approving a call by accident. Space no longer answers a card, a letter returns focus to the
  composer with the typed text, and Ctrl+Enter on a card returns to the composer; Enter and 1–5 still answer.
- A refused answer goes back to the composer; a `!` turn takes no steer; a steer taken as the decider says done gets
  one more step; the kernel renames a model call id that starts with `user-shell`, and the CLI reports only its own
  `!` line's result; the output tail is published outside its lock and scrubbed before it is cut.
- The composer's `#` is a mode only before a space, and `!` only before a space or a command word, so `#123`,
  `#include` and `!important:` are messages; `\#` and `\!` send the line as text (the placeholder says so, which
  departs from `Main.png`'s wording). The egress ledger keeps 90 days and 256 MB by default (`Corvid:Privacy:Ledger`,
  `corvid egress prune`); drafts are saved in order; `corvid chat` waits at most 3 s for a title at exit; `--scene`
  implies `--demo`; `ui.db` is 0600 and the data directory 0700 when Corvid creates them.
- A guest's message is logged with its sender (`UserMessage.From`), quoted as outside content in the prompt, and only
  the owner's message closes an open question in a gateway chat; `/sessions` answers only in direct chats. The session
  list is scanned only when the log changed; Rewind, Review and closing a view release the driver lock; lock I/O errors
  are reported as such.

An independent check of those fixes found a guest's words losing their label once compacted; that and its lows are
fixed: a guest stays quoted as theirs in compaction, skill learning, titles, recaps and the decider's recent events
(never a title source), quoting turns every line break into a quoted line and shows format characters, an approval card
ignores repeated keys and, for 750 ms after it takes focus, Enter and digits, Stop hosting cannot race a page load, a
driver lock is not released while a message is being routed or the session is viewed, and every store creates the
data directory 0700.

Left from the review: lock files under the data directory's `drivers/` are not removed (deleting one another process
just opened would split the lock); a guest's own allowlist replaces the adapter's rather than narrowing it (the page
says so); a few App.Engine tests time out now and then when the machine's CPU is saturated.

### 1.6 Switching, subagents and forks (A5)

Four groups in parallel worktrees, merged one at a time by an integrator (`a5/int`: switch, delegate, fork, leftovers),
building and running every test project after each merge, then tests where the groups meet
(`tests/Corvid.Host.Tests/A5MeetingTests.cs`):

- **Model switching at any time (`a5/switch`).** A switch the person makes lasts for the session: it no longer reverts
  at the turn's end. Escalation and fallback still move off it and de-escalation returns to it, not to the registry's
  default. A running turn takes it at its next step boundary, after the calls already issued. "This session" and
  "default for new sessions" are separate choices on Models (Switch… › This session or New sessions, and Follow the
  default again), in `Alt+P`'s two groups, in `/model` (`--default`, `reset <slot>`) and in `corvid model switch`. The
  decider (Kev) is switchable per session. The session header shows the session's generator and decider (the pill gives
  way to the title when the header is narrow). `ToolCallIssued.Generator` records which generator asked for each call, so
  a switch made mid-turn sits at the right step live and on replay. A session opened again restores the person's choices
  from its log. A cloud choice is not restored: it says so and is chosen again through the privacy gate. `corvid chat`
  takes `/model` while a turn runs; approval answers still come first. ACP exposes the generator and decider as session
  config options (`session/set_config_option`, `config_option_update`).
- **Subagents with a model mix (`a5/delegate`).** `delegate_task` (plan §14, §17 row 1.7) runs up to 10 child sessions
  at once, depth 1. Each child has a fresh log, a goal, an explicit toolset (presets in `Corvid:Agents:Toolsets`,
  `read_only` built in), a budget, an optional output schema (one repair inside the same budget) and a model mix for any
  slot, picked from the catalogue by the agent or the person (`/delegate`; `/agents` lists, shows and stops). Slots the
  mix leaves out run on the parent's session choices. Spawning is a side effect under the session's approval mode, and
  its card shows the goal, toolset, budget and models on every surface. Children are a tree on the Board card and in the
  session header. A child opens read-only, linked to its parent, and can be stopped; its outside content taints the
  parent through the result.
- **G-15, fork at a step boundary (`a5/fork`).** `CorvidRuntime.ForkAsync` forks while a turn runs without waiting. It
  copies through the turn's last completed step (`TurnRunner.StepBoundary`) and the turn goes on. The fork starts with
  the source's taint and session model choices, plus an optional mix picked for it. `/fork` runs while a turn runs in
  `corvid chat`; the app has Fork… in the palette, and the aside forks mid-turn. ACP's `session/fork` does the same.
- **A4 leftovers (`a5/leftovers`).** Token use is in the log (`Usage` on `AssistantMessage` and `ToolCallIssued`), so
  replay shows a turn's tokens and the context gauge. `ask_user` waits inside the turn (a question router beside the
  approval router) where the app or `corvid chat` can answer it; `ClarificationAnswered` closes it. ACP gets the output
  tail and `_corvid/question`. `corvid chat` hides `ask_user`'s call from its panel. The configured sandbox launcher is
  protected but not hidden from its own sandbox, and `corvid-sandbox` ships beside the app. The egress gate reads only
  free text, not JSON structure or Corvid's own prompts (threshold 0.6; the A5 review put it back to 0.4). Text typed while a new session starts becomes
  its draft, and a session's first opening lands on its latest message.

Decisions taken while merging:

- `corvid chat` has one reader for lines typed during a turn: the switch group's line router. It takes the commands
  marked `SlashCommand.DuringTurn` (`/model` and `/fork`); any other line stays typed ahead for after the turn, and an
  open approval prompt or question gets every line first.
- A fork's copied session choices come back through the restore when it opens (`ForkResult.Models` reports them
  `Unchanged`). So `/fork` lets go of a fork with local choices, and keeps one open only when it holds a cloud choice.
- The session header gives the parent link and the children button their room before the models pill (`FitTitle`).
- An in-turn question could reach the window (`QuestionAsked`) before `WindowQuestioner` had registered its wait, since
  the kernel logged `ClarificationAsked` before calling the asker: an answer given then was refused, and
  `EngineRunningTurnTests.A_question_the_windows_turn_asks…` failed in 2 of 3 full-suite runs after the merges.
  `IQuestionAsker.Expect` (a default method) is now called under the turn's lock just before the question is logged.
- The models pill measured itself to decide whether it fits. Once it had given way (`MaxWidth` 0) it measured 0 wide and
  came back, a layout cycle whenever the title's room was near the line: at 1440 with the dock open the composer stopped
  showing typed text. It now measures its text.
- ACP's `available_commands` still leave out `/agents`, `/delegate` and `/fork`. Re-recording (`CORVID_RECORD_ACP=1`) after the
  leftovers merge and again at the end produced no diff.

The integrator ran the merged app once on the engine and once with `--demo`, in a temporary home with two scripted
OpenAI-compatible servers. On the engine: a spawn's approval card showed the child's goal, tools, budget and models; the
child ran on the second server, and the header's "1 child" list and the Board card showed it. `/model generator fake2`
switched this session, and the next turn ran on it and stayed on it after the turn ended. At 1920 px the header showed
the models pill and the children button side by side.

New on the wire: `clarification_answered`, plus the optional `generator` and `usage` fields on `tool_call_issued` and
`usage` on `assistant_message`. A binary older than A5 cannot read a log that holds the new event (the same one-way step
as before); it ignores the new fields.

Still refused on the engine, each with its message: memory consolidation (plan §9.5), opening a terminal or the Vault
helper (U-6), the Vault policy, role, budget and daily counts (V-n) and media memory (plan §9.4). (Images in prompts,
K-8, attached mode, worktrees per session and packaging came in A6: §1.7.)

Left:

- From §1.5: a steer's position in the report relative to the call that finished; the Board's Take over; the lock files
  under `drivers/`; a job's message read as the user's on replay; a gateway adapter needs Vault for its token.
- Delegation: the calls a turn issues in the same round are authorized and run one after another (approved
  `delegate_task` calls do not run together across calls, only the tasks of one call); a finished child's tail timer keeps ticking until its channel goes; `--demo` has no children.
  (`Children(parentId)` now reads only what the parent's log added since its last list; A5 review.)
- Switching: the live step's model is inferred until `ITurnListener.StepStarted` carries the generator (replay is exact).
- A person's message clears taint for its turn (plan §10.3), so a child spawned in a tainted fork after the person writes
  is not tainted unless the turn reads outside content again. This is the intended model; it is noted because a fork's
  inherited taint lasts only until then.

The end-to-end pass walked the A5 flows in the app and in `corvid chat` (three scripted servers: a local server, one gated
as a cloud model with `Egress: { Action: Redact, Tainted: Refuse }`, and an escalation step; Kev-0.8B and Kev-4B as deciders).
A real cloud model needs https and a Vault key, so "cloud-class" here is a gated local server. It fixed:

- A child's fixed system prompt was redacted for a gated child model ("You are a `<PERSON_1>` working on one goal"): it is
  now one of the egress gate's trusted texts. The output schema after it is still read.
- `Alt+P` filtered dropped the "This session" and "Default for new sessions" headers, so a model listed in both did not say
  which choice a row made.
- A this-session switch to a model not loaded yet (Kev-4B, seconds on the CPU) gave no sign until it was made; a message
  sent meanwhile ran on the old decider. Models' picker now says it is loading and which model the session stays on.
- The turn strip said "generating" while the turn waited on an in-turn question.
- Choosing with Enter in the palette (Fork…) typed a line break into the composer the focus went back to.
- Coming back from a fork, the header's title stayed cut to a word: a pill that collapses raises no `SizeChanged`.

Found and left:

- Pin semantics differ between layers. `SessionModelSlots` (and its test) lets escalation move a pinned slot and keeps it
  there after the turn; `/model`'s help ("--pin to keep it past escalation"), `/model unpin` ("escalation moves off it
  again") and `IModelService.SetPinnedAsync` ("escalation leaves it alone") say a pin is not escalated off. Seen in
  `corvid chat`: pinned on fake, two failed repairs escalated to fakebig, and the next turn stayed on fakebig. Plan §6
  ("returns to the session's default unless the user pinned the model") and C-006 back the model layer, so the fix is
  the surfaces' words, unless the person wants a pin to stop escalation. (The A5 review fixed the words: below.)
- After "default for new sessions", the degraded decider tier (`/status`) and the session titler still use the default
  the runtime started with.
- The egress gate redacts some non-PII: "hello two" sent as "hello `<PERSON_1>`", and a tool's own schema default (`200`) in
  an invalid-arguments result as an account number.
- A fork's copied turns read "0 s".
- The spawn card offers Allow once when every child would be refused; the refusal shows on the card and the call fails.
- `corvid chat` without `corvid-sandbox` beside it refuses sandboxed tools (correctly); the message names
  `Corvid:Sandbox:LauncherPath` now (it named only `SandboxOptions.LauncherPath`). With that key set to the app's
  launcher, the shell runs and the launcher is visible and read-only inside its sandbox.

The A5 verifier's fix round then fixed:

- An interrupt that reached the app's question asker between setting its cancelling flag and cancelling the turn's token
  got a null answer, so the turn ended cancelled with its question still open on every replay. The asker now waits for
  the token, an answer meanwhile is refused, and the kernel withdraws the question (`ClarificationAnswered` with "")
  whenever the token is cancelled as the asker returns.
- The output schema's repair turn had a fresh step budget (up to twice what the card showed). `TurnRunner.StepLimit` gives
  it what the first turn left, and with nothing left there is no repair.
- A schema-checked `output` reached the parent uncapped; past `MaxChildTextChars` it goes as the start of its JSON text
  in `answer`, and `detail` says so.
- The gateway's `/approve` message shows the spawn's plan (`IGatewayAgent.CardLinesAsync`, the host's `DelegationCard`).
- The spawn card's preview loaded the models a mix named while the approval waited; it now reads the catalogue's profiles
  (the gate checks them the same way) and binds nothing, and the approved call loads.
- A child opened but not run yet (another task of the call still planning) could not be stopped: its usage is made at
  open, and a stop before it runs means it never starts ("stopped by the person").
- Egress: a lowercase JSON key was taken as structure, and every key's words then suppressed the same word anywhere in
  the request, so `{"jsmith": …}` went unredacted. A key is structure only when it is made of Corvid's fixed structure
  words (`PrivacyEgress.StructureWords`: "status", "exit_code", "toolCallId"); any other key is scanned and rewritten as a
  value, and only those words make a lowercase span structure (the trusted prompts' own words did too, until the A5 review).

Verification: the end-to-end pass and the first verify round ran side by side (the workflow was revised mid-run to
build incrementally and to stop after two verify rounds, since A3 and A4 spent most of their hours in serial
verify-and-fix loops). Round 1 found seven defects, all fixed; round 2 passed. Still open from it: `/model`'s help and
its unpin reply say a pin holds a model against escalation, while a pin only stops de-escalation (plan §6, C-006); the
review round below fixed the words.

The A5 review's fix round, switching, forks, questions and privacy (`a5fix/switch`):

- A choice lasts exactly as made. Choosing the model a slot already uses is logged (a `session` `ModelSwitched` from the
  model to itself), and so is a reset that leaves the slot where it is (pinned, or off the choice): before, both came back
  wrong when the session was opened again. Pins are restored too: every `session` switch logs its pin, `/model unpin` and
  the Models page's unpin log an `unpin` (`SessionModelSlots.UnpinAsync`), and `CorvidRuntime.ModelChoiceStates` reads
  choices with their pins (paged now: it read only the log's first 256 events). The words of `/model`'s help, its unpin
  reply and the Models page's pin say what a pin does: a turn's end leaves the slot where it is; escalation still moves it.
- A fork starts on the choices logged through its position (the copied log), not on the source's current ones; a cloud
  choice from then is chosen again through the gate with the fork's taint.
- An in-turn `ask_user` call is parked while it waits, so a switch then (the app, ACP's `set_config_option`) is made instead
  of failing as "neither tracked nor parked". A call names the generator that answered (`SlotChatClient` stamps
  `corvid.answered_by` on the response), so a fallback, a privacy retreat or a switch during the call tags the right one.
- ACP offers `default` ("Follow the default") for a slot the session chose, which forgets the choice (the decider back to
  the degraded tier). The Models page's "default for new sessions" writes through `ModelDefaults.Write`, as the other surfaces do.
- The question router keeps every surface attached to a session; the latest asks, and detaching takes away only its own.
- A `!` line that is a session's first message has the step boundary 0 (it was read as "no turn", and a fork then copied
  the line without its call); a fork then says there is nothing before it yet.
- Privacy: a `credential` span is structure only when made of credential nouns ("API key", "wifi password", "handle");
  any other is a value ("sunshinerainbow"). The trusted prompts' words are no longer structure where the person or a tool
  writes them (a name can be one). A value of letters is replaced only as a whole word when it has three letters or fewer;
  a longer one wherever it appears ("Smith" in "SmithJ"; "Mark" in "Marketing" too, restored in the response). The
  threshold is 0.4 again, for every request: the false positives behind 0.6 were all structure, which the shape and
  structure rules drop at any threshold, and nothing measured what 0.6 missed; a tainted-only 0.4 would leave the person's
  own PII less well found. The realistic turns of `EgressModelTests` (GLiNER2) still pass unmangled at 0.4.
- New on the wire: `ModelSwitched`'s optional `pinned` and `reset`, and the reason `unpin`. Older binaries ignore the fields.

Left from it:

- With the real GLiNER2, "my wifi password is sunshinerainbow" labels nothing, and the credential detector's prose gate
  (C-032, `Corvid.Secrets`) wants a digit or a symbol, so the word is sent; "SmithJ" goes only when the extractor finds
  "Smith" itself ("John Smith" alone does not redact it). Both need the detector or a surname rule, not the redaction map.
- A per-session decider on another device than the shared one keeps the shared fabric's checkpoint reduction (C-030): the
  reduction is resolved once from the runtime's decider placement.

The PR review (two agents and a security review) found one high defect: a multi-task `delegate_task` leaked the
children it had opened when a later task's planning threw. Fixed with the rest; decisions taken in that round: an agent's
mix may not name a child's decider (a child judges with its parent's decider unless the person or a configured
`Corvid:Agents:Mixes` entry names one), a tainted child's answer gets the injection checkpoint and is labelled outside
content, `Corvid:Agents:Models` covers inherited models in the slots a remote model can serve (generator, summarizer,
vision), and the egress threshold is back to 0.4. Still open: a plain-word password in prose ("my wifi password is
sunshinerainbow") reaches a cloud model when GLiNER2 labels nothing, since the credential detector's prose rule wants a
digit or symbol (a `Corvid.Secrets` change). From the fix verification, also left: an abandoned child frees its
concurrency place while a tool that ignores cancellation still runs (its driver lock holds until it is idle); a reset on
a pinned slot is logged without the pin, so replay shows it unpinned; a tainted child's answer is labelled with a
prefix, not enclosed, so the injection checkpoint is the defence against text after it.

### 1.7 Attached mode, workspaces, images, search and packaging (A6)

Four groups in parallel worktrees, merged one at a time by an integrator (`a6/int`: attached, workspaces, media, ship),
building the projects each merge touched and running their tests, then the whole solution, the checker and every test
project, and tests where the groups meet (`tests/Corvid.Host.Tests/A6MeetingTests.cs`,
`tests/Corvid.App.Engine.Tests/EngineAttachedMeetingTests.cs`, `tests/Corvid.App.Core.Tests/AttachedLimitTests.cs` and
`AttachedForwardingTests.cs`):

- **Attached mode, G-1 (`a6/attached`).** `corvid serve --attach` serves the session seam (`ISessionService`, the engine's
  own `EngineSessionService`) as JSON-RPC over a WebSocket on a Unix socket (0600) and, with `--listen`, loopback TCP
  (Host and Origin checked, the WebAssembly build served beside it with `--web`); `--no-openai` leaves out the OpenAI
  endpoint. Every connection authenticates first with the owner token (a Vault secret, or one generated in
  `$XDG_STATE_HOME/corvid/attach/owner.token`, 0600) or a paired device's token (`corvid serve pair`, `devices`,
  `revoke`); there is no guest. The app attaches with `--attach` and `--pair`; the WebAssembly head attaches to the Corvid
  that serves it and pairs from `#pair=`. Approvals and questions are the engine's own cards. A client that drops leaves
  the engine as it was (approvals fall back to their deadline, in-turn questions are put aside once no client is left).
  `corvid serve` now stops on SIGTERM and deletes only a socket it bound itself. Attached windows skip `ui.db`.
- **Workspaces per session, G-4 (`a6/workspaces`).** A session records its workspace (`WorkspaceBound`) and its approvals,
  sandbox, file tools, checkpoints, Review and Rewind follow it; the rail groups sessions by workspace; the Board's Open a
  workspace starts one elsewhere or in a new git worktree (under `$XDG_STATE_HOME/corvid-worktrees`), the worktree only
  after the person approves it on the asking session's card, also outside a turn. `/workspace`, `corvid chat
  --workspace` and `delegate_task`'s `isolation: worktree` (a child in a worktree of its own) do the same elsewhere.
- **Images, K-8 and F19 (`a6/media`).** Paste, drop and the attach box make chips; the engine checks them before queueing,
  the turn strips their metadata and stores them content-addressed (`<data>/media/sha256/`), the log keeps references,
  the transcript draws bounded thumbnails live and on replay. A generator that reads images gets them; otherwise the
  vision slot describes them, or the message is refused. A gated model gets no image unless egress allows images.
  `corvid chat` has `/image` and `@path`; ACP takes image blocks and advertises `promptCapabilities.image` (its two
  exchanges were re-recorded for that field alone).
- **Search, packaging and the A5 leftovers (`a6/ship`).** G-5: `CorvidRuntime.SearchSessions` over the episodic index,
  `/search`, and `Ctrl+Shift+F` in the app, which opens the session at the matched turn. U-7 and U-9: the app's binary is
  `corvid-app` with the Corvid mark as its window icon, a tarball target (`CorvidAppTarball`), `install.sh --app` with a
  desktop entry, the release workflow publishing it, and an Info-only app check in `corvid doctor`. The A5 leftovers: an
  abandoned child keeps its concurrency place until its tool returns; a tainted child's answer is enclosed between
  markers its text cannot forge (`EncloseOutside`); the credential detector turns away a plain-word password in labelled
  prose; a reset on a pinned slot logs its pin.

Decisions taken while merging:

- **The CLI references `Corvid.App.Engine`** (and through it `Corvid.App.Core` and Markdig), recorded in the checker's
  tables with the reason: `corvid serve --attach` hosts the engine's own session service, so the remote seam is
  `ISessionService` itself and the approval card, queue and questions are the window's code, never a second copy.
  `Corvid.Http` and `Corvid.Acp` still reference neither; `Corvid.Host` references no App project.
- **Images cross the attached wire.** `session/prompt` takes `images` (base64, at most 8, 40 MB together) and
  `_corvid/read_image` returns a thumbnail's bytes by digest. The server reads requests of up to 256 KB until a connection
  authenticates with the owner's or a paired device's token, and up to 64 MB after (`IMessageChannel.AllowMessagesUpTo`);
  a pairing connection never gets more. A file dropped on an attached window is read on that device by its full path
  (`~` is that device's home; a relative path is refused), never on the engine's machine: a paired device reads none of
  the engine's files this way, and the engine checks the bytes as it checks a paste.
- **Workspaces cross the attached wire.** `session/new` takes `workspace`, `worktree`, `worktreeName` and `askIn`; the
  path is the engine machine's and the engine checks it. The capabilities carry `canSendImages` and `canChooseWorkspace`.
- **Every member of the seam forwards.** Running the attached app showed images refused ("come with the engine") and
  thumbnails "not stored": the head's `RemoteAppServices` wraps the connection in a view that forwarded only the members
  it was written with, so the A6 default members answered for the window. It forwards them now, and
  `AttachedForwardingTests` fails if any `ISessionService` member of the remote seam is left to its default.
- **A session's named images are its workspace's.** `@shot.png`, a relative `/image` path and a drop in a session that
  works elsewhere resolve in that session's workspace (`ImageAttachments.EnvironmentFor`), in the app and in `corvid
  chat`; before, they read the configured workspace.
- **A close sends its answer and reason before reading stops.** `AttachEndpointTests.TheUnixSocketIsTheOwnersAndStillNeedsTheToken`
  failed in one full Cli run and in 6 of 6 runs under parallel load: the writer finishing cancelled the reading, and
  cancelling a WebSocket's read aborts the socket, which dropped the refusal's answer and the close with its reason. The
  peer now sends the channel's close, then waits up to 2 s for the other end to close back before it stops reading, and
  an end the other closed closes back. A peer that never closes back costs a disposal up to 2 s.
- ACP's `available_commands` leave out `/workspace` and `/search` (one filter), so the recorded exchanges are unchanged
  but for media's `promptCapabilities.image`.

The integrator ran the merged app on the engine in a temporary home with one scripted vision server: an image attached
through the attach box reached the generator stripped (the server saw `image/png`, metadata gone) and showed as a
thumbnail; `Ctrl+Shift+F` found the message and the answer. Attached over the Unix socket to `corvid serve --attach`
from the same home, the earlier thumbnail loaded by digest, a 352 KB image (above the 256 KB read before authentication)
crossed the socket and reached the generator, and Open a workspace on the Board started a session in a second folder,
which the rail grouped under its own workspace. `corvid serve` stopped on SIGTERM and removed its socket. `--demo` drew
the main scene. Screenshots are in `ui-shots/a6int-*.png` (git-ignored).

The end-to-end pass ran the merged branch in a temporary home with scripted servers (a vision-capable generator, a
text-only model and a gated "cloud" model with `Egress: { Action: Send, Tainted: Refuse, Images: true }`) and GLiNER2 for
the gate. It fixed three things: the WebAssembly head did not publish (the linker config still named the assembly
`Corvid.App`, and an attached image prompt used the trim-unsafe `JsonArray.Add<T>`); a copied image or copied files did
not paste on X11 (Uno raises no `Paste` for a clipboard without text, and lists a copied PNG as `image/png`, not as the
Bitmap format: Ctrl+V now takes them, `ImageSignature.ClipboardImageFormat`); and `corvid serve`'s pairing hint named
`Corvid.App`. Seen working: `corvid serve --attach --listen` with the app attached over the Unix socket (a turn, an
approval and a question answered there, an approval still pending at the engine after the window dropped and answered
after it attached again), a wrong token, a request before authenticating and an oversized first request refused and
closed, a tokenless initialize refused and the connection left to the authentication window (any next request closes it), a
foreign Host (421) and Origin (403) refused, a used pairing code refused, the app paired over loopback TCP and the
published WebAssembly head paired from `#pair=`; two workspaces in one window with a session and a file write in each; a
session's worktree from the Board and a child's from `isolation: worktree`, both removed with `/workspace remove` in
`corvid chat`; an image pasted in the app (also attached) and given with `/image` and `@path` in the CLI, sent stripped to
the vision model, refused for the text-only one with the draft kept, shown on replay, sent to the gated model untainted
and refused to it once a `web_fetch` tainted the turn (the turn stayed local); `install.sh --app` into a temporary prefix,
the installed `corvid-app` running on the engine and `corvid doctor` finding it; `Ctrl+Shift+F` and `/search` across
sessions; the plain-word password reaching the gated model as `<CREDENTIAL_1>`; a tainted child's answer enclosed between
`outside-content` markers. Screenshots are in `ui-shots/a6e2e-*.png` (git-ignored).

The fix round after the verifier's pass (A6V1-01 to 11) and the end-to-end pass:

- **A scheduled job runs where it was made (A6V1-01).** A job's runs work in the workspace of the session that created
  it (`ScheduledJob.OriginSession`; `CorvidRuntime.Jobs.cs`): the pre-run script in that workspace's sandbox under its
  approvals, the agent turn with its tools, and the run's session records the workspace. When that workspace is gone (a
  removed worktree), the script is refused and the run fails with why; nothing runs in the configured workspace instead.
  A job made in a chat, on the Jobs page or with no origin runs in the configured workspace, as before.
- **Removing a worktree from the app (e2e).** The window tells the runtime which sessions it views
  (`CorvidRuntime.AddViewer`), and `RemoveWorktreeAsync` first lets go of an idle session held there that no surface
  views (the one the Board made); a viewed or running one still refuses. Review's per-workspace copy is replaced when the
  runtime's services for that directory are new (A6V1-09), and an approval answered outside any call (a worktree's) stays
  in the transcript with its verdict (A6V1-09). The chooser opens with empty boxes, and `/workspace` says how to remove.
- **Attached mode (A6V1-04 to 06).** Wrong tokens and codes are counted per peer (10 a minute) and for the network as a
  whole (100), never against the Unix socket, so the LAN cannot lock the owner out; pairing writes the devices file
  outside the authenticator's gate. A revoked device's live connections close at their next request or within 5 s
  (`IAttachedAuthenticator.IsCurrentAsync`, `AttachedProtocol.RevocationCheck`). `corvid serve` warns that `ws://` past
  loopback is cleartext, and the README says so; there is no TLS (U-12). The browser head sends a `#pair=` code only to
  the server that served the page. Attached mode's scope in A6 is recorded as U-12: Sessions and the Board (A6V1-02).
- **Smaller ones.** `install.sh` without `--app` keeps an app of the same release and removes a stale app's link, desktop
  entry and icon with a note (A6V1-03). A slot left pinned after its choice was reset is pinned again on reopen
  (A6V1-07, `SessionModelSlots.RestorePin`). A tainted child's schema-checked output is enclosed as outside content
  (A6V1-08). A chat notice starts on its own line (A6V1-10). The approval-deadline test runs on a test clock
  (`CorvidRuntimeFactory` takes a registered `TimeProvider` for the approvals), and the Secrets tests' absent socket is
  short whatever `TMPDIR` is (A6V1-11).

New on the wire: `workspace_bound`; the optional `images` on `user_message`; attached mode's protocol (version 1). A
binary older than A6 cannot read a log that holds `workspace_bound` (the same one-way step as before) and reads a message
with images as its text alone.

Still refused on the engine, each with its message: memory consolidation (plan §9.5), opening a terminal or the Vault
helper (U-6), the Vault policy, role, budget and daily counts (V-n) and media memory (plan §9.4). An attached window's
areas other than Sessions and the Board say they run on the engine's machine.

Left:

- Attached mode: an owner UI in the app for `CreatePairingCodeAsync` (pairing codes come from `corvid serve pair`); a
  pairing box in the browser instead of `#pair=`; reconnecting after a drop (opening the window again attaches); TLS on
  the listener (U-12); the other areas over the wire (U-12); an attached badge in the status bar (the Board's status bar says "engine unreachable" when attached). A paired device
  may hold up to 16 requests of 64 MB at once (it is trusted with the engine already).
- Images: a browser drop gives no path, so a browser attaches by paste only. A drop on the desktop head was not driven
  (the harness has no drag source).
- Seen in the end-to-end pass and not fixed: Open a workspace offers worktrees already removed (from the sessions
  recorded there); the cron_create card does not say which workspace the job will run in; an approval card that arrives outside a turn at the end of a long transcript is cut off at the
  bottom and the wheel cannot scroll to its buttons (Enter from the composer brings it into view); a child that answered
  from one model call shows "0 steps"; on replay a model switch between turns is folded into the turn before it ("the
  running turn takes it at its next step"); the user's own message keeps a pasted password in the log and the search
  index (the answer echoing it is scrubbed), since nothing moves it into Vault without one.
- Packaging: Flatpak (U-9) not started.
- From §1.6, still: a steer's position in the report; the Board's Take over; the lock files under `drivers/`; a job's
  message read as the user's on replay; a gateway adapter needs Vault for its token; delegation's same-round calls run one
  after another; the live step's model is inferred until `StepStarted` carries it.
- Real models: the integration pass used scripted servers only; the GPU tests (`Category=Gpu`) were not run here.

### What the running app shows today



The shell matches `Main.png` outside the conversation: rail, badges, sessions with their dots and times, status bar,
mode control, dock tab strip. It also matches in dark, and at 1024, 820 and 390 px wide, where the phone layout and its
bottom bar take over. Every rail item navigates. The Sessions area is built (§1.1); every other area is a placeholder
page with its title. The scaffold's report lists these remaining differences:

- The status bar has toggles for the rail and the dock, shown only while that panel is hidden. The mockups lack them.
  Without them a mouse user could not bring a hidden panel back.
- The phone top bar's subtitle is the workspace, not "attached to mark-ws", because this is the in-process app.
- Badge counts use Inter SemiBold where the CSS asks for bold, since only weights 400, 500 and 600 are embedded.
- Popups and tooltips follow the application theme, not a theme switched at run time.
- The status bar items are not click targets yet, and Board, Audit and First run do not have their own variants yet.
- The window carries the Corvid mark as its icon and the binary is `corvid-app` (U-7, A6).
- The WebAssembly head builds with `-p:CorvidAppWasm=true` and attaches to the Corvid that serves it; the end-to-end pass published it and ran it paired in headless Chromium (A6, §1.7).

## 2. Picking it up on another machine

```sh
git fetch origin && git checkout claude/zen-allen-puw590
dotnet --version                      # 10.0.401 or a later 10.0.4xx (global.json)
dotnet build Corvid.slnx -c Release   # restores Uno.Sdk 6.7.30 from NuGet; no workload needed
dotnet src/Corvid.App/bin/Release/net10.0-desktop/corvid-app.dll --demo
dotnet src/Corvid.App/bin/Release/net10.0-desktop/corvid-app.dll --demo --scene approval --theme dark --page sessions
# The engine is the default: with no --demo the app runs on this machine's Corvid home (corvid.json, models, event log).
# Point HOME, CORVID_CONFIG and the XDG directories at a temporary home to try it without touching yours.
dotnet src/Corvid.App/bin/Release/net10.0-desktop/corvid-app.dll
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

A0.3 and its verifier have since been done by hand (§1.1), so this workflow is spent; it stays as the template for A1's.

### Harness notes from a machine without Xvfb

- TigerVNC's `Xvnc` is a headless X server too: `Xvnc :n -geometry 2560x1600 -depth 24 -SecurityTypes None -rfbport 0
  -rfbunixpath <socket> -nolisten tcp` stands in for Xvfb. It refuses to start with no listening endpoint at all, hence
  the Unix socket. Without `xdotool`, a few lines of python-xlib with XTest cover the harness's `search`, `windowsize`,
  `mousemove`, `click`, `type` and `key`.
- Uno sets its window to 1024x640 a few seconds after start-up, which can undo the harness's resize. Wait about 3 s,
  resize, check the size with `xwininfo`, then resize one pixel wider and back: Uno sometimes keeps drawing at its first
  size until the window changes again. A screenshot with the app in the top-left 1024x640 and black elsewhere is this,
  not a defect.

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

- The session core's API held for the Sessions page with one wording fix ("about 1 token"). It has only ever been fed
  by the demo service; `Corvid.App.Engine` is its first real producer.
- Uno quirks found so far are in the app README's gotchas: theme brushes on `Run`s, line-height maths, the order of
  `Navigated` and `OnNavigatedTo`, stroke widths rounded to whole pixels. Read them before writing XAML.
- CI builds the desktop head on a self-hosted runner with no display. Nothing in the app's build may need one.
- Left from the code and security review of #71 (the rest was fixed in its follow-up):
  - `SessionFold.OnRewound` keeps the decisions (and, when it stops a running turn, the turn header and step) of rewound
    turns; a new turn with the same number would show the old turn's chips. Key decision groups by turn header id.
  - `SessionHub.Show` selects a session the rail does not list, which the next rail rebuild loses. It has no caller
    yet; the Board's peek will be the first.
  - The WebAssembly target's boundary rules are checked only against a synthetic graph: CI never restores with
    `-p:CorvidAppWasm=true`. The checker's Uno rule matches names starting `Uno.` only.
  - `MarkdownView` rebuilds a streaming answer whole on every update (quadratic in its length; a selection inside it
    is lost). Rebuild only the last block when the earlier ones are unchanged.
  - Templates bind to the static `SessionsLayout.Current`; whether Uno's x:Bind lets go of recycled elements' handlers
    is unmeasured. `Bindings.StopTracking()` in `OnNavigatedFrom` is the cheap guard if it does not.
  - `docs/plan/ui-mockups/source/Audit.dc.html` and `png/Audit.png` show a real home directory in sample output;
    replace it and render again (rendering needs the network for the fonts).
  - `AsyncRelayCommand` and `DispatcherExtensions.InvokeAsync` have no callers outside tests yet.
