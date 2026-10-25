# Corvid UI: features and flows

Version 0.1, 2026-10-23. Status: design exploration for a windowed Corvid built with [Uno
Platform](https://platform.uno), the step before any screen is drawn. It is the sibling of the terminal design in
[`tui.md`](tui.md) and rests on the same evidence ([`docs/research/tui/`](../research/tui/README.md), gathered
2026-10-21), plus the desktop and web agent apps and the local chat front ends read for this document (§12). Plan §18
lists a desktop UI under phase 3; this document says what it should do and how it should feel. The mockups are in
[`ui-mockups/`](ui-mockups/README.md), as `tui-mockups/` followed `tui.md`; the build's shared rules are in
[`ui-build.md`](ui-build.md) and where it stands in [`ui-handoff.md`](ui-handoff.md).

## 0. Thesis

Every chat front end of 2026 streams markdown, shows tool calls as cards and asks before side effects. The windowed ones
add what a terminal cannot carry: blocks that are typed and selectable as one unit, a diff you can comment on, several
sessions on one screen, images, drag and drop. Warp's engineers describe a terminal, from the application's side, as "a
stream of bytes in and a stream of bytes out", and their own renderer as "an ordered list of typed blocks" ([research
note](../research/tui/opencode_crush_pi_and_others.md)). The TUI design works inside that limit. This app has the typed
blocks.

Corvid's kernel already knows more than other agents show. Each turn leaves calibrated decisions, approval reasons with
codes, taint changes, escalations with their cause, redaction counts, checkpoints, staged memory, replay-tested skill
drafts, held MCP definitions and detection matches in the event log. The CLI prints some of it. The app gives each item
a place and a link to its evidence, which is where it can be plainly better than Claude Code's desktop app, Cursor,
OpenCode's clients, LM Studio, Jan or Open WebUI. Those show the conversation and the diff and little of why.

Commitments:

- **A projection of the log.** The app renders the same event log and the same session update stream the ACP server
  emits (tui.md §3.2, K-10). A resumed session and a live one draw the same screens. Anything on screen can be traced to
  an event or an update.
- **The app shows what the agent does and never acts on its own.** No view approves, redacts, decides or writes a rule.
  Approvals, taint, the privacy gate and the sandbox stay in the kernel, so the app can run attached to a Corvid on
  another machine with the same security model.
- **Trust you can see.** Approval verdicts, taint, escalation, redaction, detection matches, staged memory and held
  tools each get a row, a badge and a page, with the evidence one click away.
- **Local-first and account-free.** It works offline on the in-process models. Bytes leave the machine only through a
  cloud slot the user configured, and background processes or update checks start only when the user asks.
- **Complete from the keyboard and from a screen reader.** Every action has a key, every control has a name, streaming
  never steals focus, and motion stops when the platform asks for it.
- **Quiet.** The conversation is the interface, and panels open when there is something in them. Pets, gradients and
  animated banners are out of scope.

What rivalling the field means, measured:

| Property | Target | Who misses it today |
| --- | --- | --- |
| Window usable | under 500 ms from launch; models load afterwards with progress in the status bar (K-4) | Electron front ends; today's `corvid chat` blocks on "Loading models…" |
| Memory of the UI process over a day-long session | bounded by a window of rendered blocks, under 300 MB apart from the models | Hermes desktop (#141329: renderer memory grows about three times), OpenCode's terminal client (about 1 GB "to render text"), Claude Code (6.9 GB after 14.5 h) |
| Scroll position while streaming | never moved by the app; a "jump to bottom · N new" pill while scrolled up | Claude Code #826 (690 upvotes) |
| Idle cost | zero timers and zero redraws when nothing animates | Claude Code (3.5 to 4.6 percent CPU idle), Codex (32 ms spinner redraw) |
| What an approval shows | the parsed command, each target, the reasons with codes, reversibility, taint, the diff and the deadline | OpenCode ("Only Yes/No/Always... no Never"), Claude Code (bare `y` and `n` answered by stray keys until removed) |
| "Always" rules | previewed before they are written, listed with scope and source, removable (K-7) | OpenCode ("Always" for `python3` persists on disk) |
| What left the machine | every cloud request viewable as sent, after redaction, with its count of redacted values | nobody |
| Background processes | none unless the user turns on the scheduler host or attached mode | Codex 0.157.0 (`daemon_auto_start`, a 609-point complaint) |
| Accessibility | every control reachable and named; a linear live region for streaming; reduced motion and high contrast honoured | Codex desktop (#32375, #34339: pickers invisible to screen readers) |

## 1. What the field offers

The research notes carry the citations for the terminal agents; this digest adds the windowed products.

**Terminal agents** converged on one shape (a streaming transcript, collapsed tool cards, a `/` palette, an `@` picker,
`!` shell, a resume picker, a permission prompt with scopes) and then differentiated on rewind that restores files,
steer-versus-queue keys, side questions (`/btw`), multi-session views, remote control from a phone, scheduled tasks from
the prompt, and generated titles and recaps. Users switch tools for stability, footprint, model freedom and permission
ergonomics. They punish heavy chrome, daemons, auto-updates, nags and silent broad "Always" rules ([feature inventory
and sentiment](../research/tui/feature_inventory_and_sentiment.md)).

**Agent desktop apps.** Claude Code's desktop app runs each session in its own git worktree (under
`.claude/worktrees/`), opens a diff view where line comments are collected and sent as one message, offers a "Review
code" pass over its own diff, a side chat (`Cmd+;`) that reads the session's context without writing back, panes for
conversation, diffs, preview, terminal and editor in one draggable layout, per-session CI status, scheduled tasks, and
shares `CLAUDE.md`, MCP servers, hooks and settings with the CLI (third-party accounts:
[claudelog](https://claudelog.com/faqs/claude-code-desktop-app/),
[buildfastwithai](https://www.buildfastwithai.com/blogs/claude-code-desktop-redesign-2026)). Amp moved multi-thread
tracking out of its TUI into its native and web apps ([so long, TUI
sidebar](https://ampcode.com/news/so-long-tui-sidebar)), with server-stored threads and "handoff" in place of fork
([Hamel Husain's notes](https://hamel.dev/notes/coding-agents/amp.html)). OpenCode 2.0 became client-server with desktop
and web clients and `/pair` ([OpenCode 2.0](https://anoma.ly/notes/opencode-2-0/)). Goose's desktop app and CLI share
one session store. Warp renders agent sessions as typed blocks with a code review pane. Hermes ships a desktop app whose
renderer lags in long sessions (#138086, #141329). Codex's desktop app has the accessibility issues worth avoiding
(#32375, #34339).

**Local-first chat front ends.** LM Studio, Jan, Msty, Cherry Studio, AnythingLLM, LibreChat and Open WebUI contribute
the model manager (a catalogue with sizes and hardware fit, explicit downloads with progress, a local server switch),
conversation organisation (folders, pins, tags, search), prompt libraries, parameter controls, document knowledge bases
and provider switching
([roundup](https://www.iunera.com/kraken/enterprise-ai/top-20-tools-to-run-llms-locally-in-2026-ollama-anythingllm-open-webui-lm-studio-vllm-and-every-real-alternative-compared/),
opinion grade). They run tools without approvals or a sandbox, keep no audit trail, and store API keys in a settings
file. The Electron ones carry its footprint.

**What the app takes from each.** The converged shape as table stakes. From Claude Code's app: worktree per session,
review with comments, the side chat. From Amp: a session list and a board in place of tab strips. From the local front
ends: the model manager. From Warp: typed blocks. Corvid adds what its kernel already produces: calibrated decisions,
structural approvals, taint, Vault, checkpoints with three-way merge, staged memory, replay-tested skills, detection
rules and an append-only log.

## 2. Why Uno Platform, and the two ways to host the engine

Uno Platform builds one C# codebase, in XAML or C# markup, for `net10.0-desktop` (Windows 7 and later, macOS 10.15 and
later, Linux on X11 and the framebuffer), `net10.0-browserwasm` (Chromium, Firefox and Safari),
`net10.0-windows10.0.19041` (WinAppSDK), `net10.0-ios` and `net10.0-android`
([requirements](https://platform.uno/docs/articles/getting-started/requirements.html), fetched 2026-10-23). Uno 6.4
(2025-11-11) brought .NET 10 and Visual Studio 2026 support, display-synced Skia frame pacing and Win32 window chrome
([release post](https://platform.uno/blog/uno-platform-6-4/)); 6.5 (March 2026) opens Hot Design by default and lets an
AI agent verify app behaviour at runtime ([standup](https://platform.uno/blog/6-5-release-community-standup/),
[InfoQ](https://feed.infoq.com/uno-platform)). For Corvid that means the app lives in the same solution, under the same
analyzers and the same architecture checker, in the language the kernel is written in, with Hot Reload and Hot Design
for iterating on screens.

The engine can be hosted two ways, and both matter:

| Topology | What runs where | Used for |
| --- | --- | --- |
| **In-process** | the app hosts `CorvidRuntime` the way `corvid chat` does: models load in the app's process, and the scheduler lock and the gateway can run here | the desktop app on Linux today, and on Windows and macOS once the sandbox and Vault exist there |
| **Attached** | the app is a client of a Corvid that runs elsewhere, over the session update stream serialised as the ACP shapes with `_corvid/*` extensions (tui.md §3.2) on a local socket, a named pipe or a WebSocket | the WebAssembly build (models of these sizes do not run in a browser tab), a phone on the LAN, a second window on another machine, and the TUI plan's daemon (tui.md §4.9) once it exists |

Proposal (U-1): build in-process first and design every view against the update stream from the first day, so attached
mode is a transport change. The view models never touch `CorvidRuntime`; they talk to an `ISessionClient` with an
in-process implementation and a remote one (§8).

## 3. The shell

One window with four regions, each collapsible. The Work layout, sketched in text:

```
+------------+------------------------------------------------------------+--------------------+
| rail       | conversation                                               | dock               |
+------------+------------------------------------------------------------+--------------------+
| Sessions  2| you    the sandbox test fails on CI only, find out why     | Changes  Files     |
| Board      |                                                            | Tasks  Decisions   |
| Review     | corvid                                                     | Context  Output    |
| Memory   1 |  > intent 0.94 answer     > tool 0.91 read_file            | -----------------  |
| Skills     |  # read_file .github/workflows/ci.yml           done 0.3 s | this turn          |
| Jobs       |  # shell dotnet test Corvid.slnx           waiting 1 of 1  |  ci.yml      +4 -1 |
| Models     |  +- approval ------------------------------------------+   |  Sandbox.cs +12 -3 |
| Safety     |  | dotnet test Corvid.slnx        execute, in workspace|   |                    |
| MCP        |  | why: tainted session (web_fetch docs.example.org)   |   | session            |
| Gateway    |  | reversible: no file changes        denied in 4:12   |   |  3 files   +31 -9  |
| Secrets    |  | [1 once] [2 session] [3 always...] [4 deny] [5 why] |   |                    |
| Audit      |  +-----------------------------------------------------+   |                    |
|            |  queued  "also check the retry count"                  x   |                    |
| Settings   |  +- composer ------------------------------------------+   |                    |
|            |  | Ask Corvid...                                 send  |   |                    |
|            |  +-----------------------------------------------------+   |                    |
|            |                                                            |                    |
+----------------------------------------------------------------------------------------------+
| generator qwen3.5-0.8b cpu q0 410 ms | Kev-0.8B | ctx 38% | smart | tainted | main | 12 s    |
+----------------------------------------------------------------------------------------------+
```

- **Rail** (left): Sessions, Board, Review, Memory, Skills, Jobs, Models, Safety, MCP, Gateway, Secrets, Audit,
  Settings. Badges carry attention counts. It collapses to icons and hides in the Focus layout.
- **Conversation**: the transcript, the composer, and a turn strip above the composer with the running step, elapsed
  time, the queue and the steer field.
- **Dock** (right): tabs for Changes, Files, Tasks, Decisions, Context, Output and Session. Resizable, with reorderable
  tabs; a tab tears off into its own window where the platform allows it (checked in A0).
- **Attention tray**: one list of everything waiting on the user across sessions: approvals, clarifying questions, held
  MCP tools, pairing codes, memory and skill reviews, finished jobs, a Vault confirm in progress. Each item opens its
  full card. A bare button answers nothing.
- **Status bar**: generator slot and model with device, queue depth and median latency; decider tier; context gauge;
  approval mode; taint; goal; scheduler state; git branch; session id; elapsed time of the running turn.
- **Palette** (`Ctrl+K`, with `Ctrl+P` as the TUI's alias): every command, view and setting, fuzzy-matched, with
  argument pickers. `/` in the composer runs the same slash registry the CLI and the gateway use.

Layouts: **Focus** (conversation only, centred at reading width), **Work** (conversation and dock), **Board** (a grid of
sessions). Under 720 px of width, which is the WebAssembly build on a phone, the shell becomes one column with a bottom
bar: Chat, Changes, Attention, More. A session can be popped out into its own window. Sessions are a list in the rail
with pinned and recent groups and a live status dot per open session; v1 has no tab strip (U-11).

## 4. Features

Each feature is classed as table stakes (present across the surveyed products, or named as "the basics" when missing), a
Corvid differentiator (something the kernel enables and others cannot match), or later. "Needs" names kernel and host
work: K-n from tui.md §9, G-n from §9 here.

### 4.1 Composer

| Feature | Class | Design | Needs |
| --- | --- | --- | --- |
| Plain-text editor with soft wrap; `Enter` sends, `Shift+Enter` breaks a line (no terminal ambiguity here); readline keys as in the TUI | stakes | a native text control, so the platform's IME, spell check and dictation work | |
| Drafts per session, kept across restarts; `Ctrl+S` stashes one | stakes | `ui.db` | G-7 |
| History with `Up` at the first line and `Ctrl+R` search, per workspace | stakes | | |
| `/` palette with fuzzy matching and argument pickers for `/model`, `/rollback`, `/skills`, `/mcp`, `/cron`, `/memory` | stakes | the same registry; `CommandOutput` renders as cards and lists | |
| `@` picker: files under the file tools' `Workspace` rules (no credential files, no `.git`, links not followed out), then sessions and skills | stakes | the picker and the tools share one listing, so the model is never shown a path it may not read | |
| `!` shell line through the approval pipeline, sandbox and checkpoints; `!!` keeps the output out of the model | stakes | as tui.md §4.1 | |
| `#` stages a memory fact for review | Corvid | the gated write path | |
| Large paste as a chip (`paste #1, 120 lines`) with preview | stakes | | |
| A paste that looks like a credential is intercepted and offered to Vault under a suggested name | Corvid | | V-005 |
| Drag and drop: text files become resource links, a folder opens as a workspace, images become content | stakes | | K-8 for images |
| Image paste and screenshots as chips, shown inline | stakes | | K-8 |
| Queue while a turn runs: `Enter` queues, chips above the composer, reorder, remove, take back into the editor | stakes | the actor already reads during a turn | |
| Steer now: `Ctrl+Enter` delivers at the next step checkpoint, visible to the fabric before the generator | Corvid | | K-3 |
| Aside (`Ctrl+;`): a side question answered in a scratch fork of the session at its head, never appended to the main log; "bring this in" sends the answer as a message | stakes (Claude Code's `/btw` and side chat) | `ForkSessionAsync` between turns today; during a running turn the fork waits for the turn to end | G-15 for mid-turn |
| Token estimate of the draft against the context gauge | nice | | K-5 |
| Saved prompts and templates with placeholders | nice (the local front ends) | `ui.db` | G-7 |
| Voice push-to-talk | later (plan D-7) | | |

### 4.2 Transcript

| Feature | Class | Design | Needs |
| --- | --- | --- | --- |
| Streaming markdown rendered into native text blocks: headings, lists, tables, fenced code with syntax colours and a copy button, links opened only on click | stakes | the Markdig tree into `RichTextBlock`; the compact grammar set shared with the TUI (T-3); no WebView (U-5) | |
| Remote images in model output shown as links, never fetched; local images rendered inline | Corvid | an image fetch is network egress and taint, so the app never does it on the model's behalf | |
| Virtualised list; auto-follow paused while scrolled up; "jump to bottom · N new" | stakes | | |
| Turns grouped as prompt, collapsible steps and answer, with a header showing model, tokens, elapsed time and cost | stakes | | K-5 |
| Thought rows ("thought for 4 s"), expandable | stakes | | K-1 |
| Tool cards by kind (read, edit, delete, search, execute, fetch, other), with title, state, elapsed time and outcome (`succeeded`, `failed`, `invalid_arguments`, `denied`, `cancelled`) | stakes | `ToolPresentation` already assigns kind and title | |
| Decision chips per checkpoint: question, confidence, action, acted ("tool 0.91 → read_file", "risk 2 → ask"), toggled with `Ctrl+Shift+D`; each opens the Decisions pane | Corvid | `CheckpointDecisions` on the feed | |
| Escalation rows ("escalated to claude: step budget", "kept local: the privacy gate refused the cloud model") with the redaction count | Corvid | `ModelSwitched` and the turn result | |
| Taint rows when taint starts ("outside content in context: web_fetch example.com"), and a badge until it clears | Corvid | | |
| Compaction markers that expand into the structured record: goal, constraints, decisions, files, open questions, next step; "expanded on a larger window" when it happens | Corvid | `Compacted`, `CompactionExpanded` | |
| Memory rows ("memory staged: 2 facts") with a review button | Corvid | | |
| Detection rows in red with rule, severity, posture and the matched events | Corvid | `DetectionMatched` | |
| MCP rows ("tool held: notes__search changed") with a diff button | Corvid | `McpToolHeld`, `McpToolAccepted` | |
| Job notices with "open the run" | Corvid | `JobCompleted` | |
| Clarifying question block with options as buttons and a free-text field; the composer is focused and the question stays pinned | stakes | | K-6 |
| Rewind and fork markers | stakes | `SessionRewound`, fork metadata | |
| Per-message actions: copy source text, quote, edit and resend (a fork at that position, so the original stays), regenerate (a fork, or a conversation-only rewind), pin, "explain this decision", "open in Audit" | stakes and Corvid | forks through `ForkSessionAsync` with a position | |
| In-session find (`Ctrl+F`) with hit navigation and filters by tool and file | stakes | | |
| Export to markdown, a self-contained HTML file or JSONL; print | stakes | copies come from source text, never from rendered cells | |
| Mermaid and LaTeX | later | | |

### 4.3 Tool cards, diffs and output

- **States**: pending, awaiting approval (with the countdown), running (with a live tail, K-2), done, denied, failed,
  cancelled.
- **Why it ran unasked.** Every side-effecting card carries the approval verdict even when nobody was asked: "smart
  mode, risk 1 (0.93), in workspace, no network" or "session rule from turn 4". Today only prompted calls leave
  `ApprovalRequested`; G-2 puts the assessment on the stream for the rest.
- **Shell cards** show the structure the pipeline parsed: each command with verb, subcommand, effects and targets as
  chips. Hovering a target shows the resolved path and what the effect means.
- **Diff cards** for `write_file` and `patch_file` from the write ledger, unified inline with word-level highlights, a
  "reversible" marker when a checkpoint exists, and "open in Review".
- **Output pane** (dock): the full result with search and wrap, ANSI colours rendered as text styles after sanitization,
  and the three caps (screen, memory, model) of tui.md §6.
- **`web_fetch` cards** show host, bytes, status and the taint consequence. **MCP cards** show server and trust.
  **Memory cards** show what was searched or staged.

### 4.4 Approvals and safety

The approval card shows what the pipeline knows: the analysis text; each parsed command with its targets; the reasons
with their codes (`credential_read`, `elevated`, `tainted_network_egress`, `detection` and the rest of
`ApprovalReasons`); whether a checkpoint makes the action reversible; what tainted the session; the proposed diff side
by side; for MCP tools the server's trust; the deadline as a countdown (five minutes by default).

- **Answers** are buttons with digit keys: once, this session, always, deny, and "deny and say why", which sends the
  reason as the next message. The scopes shown are exactly `ApprovalPrompt.Offered`. `y` and `n` are never bare keys. A
  card that appears while the user is typing opens below the draft; the first `Enter` focuses it and the second answers
  it. Text typed before it appeared never answers it.
- **Always rules are visible.** Choosing "always" previews the structural rule (verb, subcommand, paths, names, scope)
  before it is written, with narrowing of its paths in A1 (G-11). The Safety page lists session and always rules with
  scope and source, and removes them (K-7).
- **Modes** are a segmented control in the status bar: Manual, Smart, Permissive, each with one sentence, and the
  hardline list applying in all of them. Per session (G-10, shared with the TUI's `Shift+Tab`).
- **Queued approvals** show "1 of 3" with the list. Approvals from other sessions wait in the attention tray and open as
  full cards.
- **Detections** that force an ask show the rule and the matched events, with a link into Audit filtered to them. The
  Safety page lists the built-in pack of twelve rules with severity, posture and the enforce switch, and every match so
  far.
- **Hardline** is shown read-only as "what Corvid never does", with its six codes.
- **Taint** has an explainer: what entered, when, what it changes (network reads ask; egress and new recipients offer
  only "once"), and when it cleared.
- **Rule suggestions** (A1): after the same verb and target have been approved several times, the app proposes the
  narrow rule it would write, and the user accepts or ignores it. A heuristic over the log in the app; nothing is
  written unasked.
- **A stale prompt runs nothing.** A prompt cancelled by the turn, the deadline or a rewind shows "denied: deadline" and
  closes.

### 4.5 Status, attention and notifications

- Status bar items as in §3, each with hover detail and a click target: the model opens Models, the gauge opens Context,
  the mode opens Safety.
- **Context gauge** (K-5): tokens in the rendered prompt over the accepted window, with the next compaction threshold
  and a breakdown in the Context pane: system prompt, memory, skills, recent results, stubs.
- **Cost** per session and per slot for cloud models when usage is reported (K-5).
- **Attention tray** as in §3; its count sits on the tray icon and the taskbar or dock badge.
- **Native notifications** (D-Bus on Linux, toasts on Windows, UNUserNotification on macOS) on turn end, a waiting
  approval, a question, a finished job and a pairing request, only while the window is unfocused unless configured. A
  click lands on the item. Sounds per event are optional and off by default.
- Taskbar progress during model downloads where the platform offers it.
- **Recap** on return after an absence: the local generator writes a few lines from the events since the user was last
  active (K-9 grows a recap).

### 4.6 Sessions, workspaces, fork and rewind

- **Session list**: title (generated locally after the first exchange, K-9; renameable, G-6), time since activity,
  workspace, branch, surface (`cli-`, `acp-`, `http-`, gateway, `job:`), turn count, taint, a preview. Search by typing;
  G-5 searches content across sessions through the episodic index. Pinned and archived are UI state. Nothing in the log
  is ever deleted, and the list says so where a user looks for delete.
- **One driver per session.** A session open in another process (a terminal running `corvid chat --session`) shows as a
  read-only live view that tails the log (G-12), with "take over" once the other side releases it.
- **Workspaces**: the app opens a folder the way an editor does. The runtime fixes its workspace at start, so each
  workspace is a runtime of its own, one per window, until workspace-per-session lands (G-4). Project binding
  (`ProjectBound`) is shown so the user knows which lessons apply. Worktree per session, the isolation model of Claude
  Code's desktop app, follows G-4.
- **Fork** powers three flows with one call: edit a prior message, branch from here, aside. The session header shows the
  fork tree.
- **Rewind** (`Esc Esc` on an empty composer, or the Rewind button): a timeline over the checkpoint ledger, one entry
  per call with tool, paths and time; a diff preview per entry; restore files and conversation, conversation only, or
  files only; the result lists restored, merged and refused paths exactly as `RestoreResult` reports them, with the
  three-way merge explained in one sentence. `/undo` and `/rollback N` keep working as commands.
- **Compact now** with a focus field (`/compact <focus>`), and the standing goal editable in the session header.

### 4.7 Review and the code panes

- **Changes**: files changed this turn, this session or since the last review, each with added and removed counts;
  side-by-side and unified diffs with word-level highlights; a comment on any line; "send 3 comments" posts them as one
  message quoting file and line; "ask about this file" starts a message with `@path`; "open in editor" uses `$EDITOR`,
  `xdg-open` or `code --goto`. Reverting a whole file from its checkpoint comes in A1, hunks later (G-3).
- **Files**: the workspace tree under the same `Workspace` rules, with paths Corvid may not read shown greyed and
  labelled; quick open; a read-only preview with highlighting.
- **Tasks**: the `TodosSet` list, live. "Ask to update" sends a message; the todos belong to the model.
- **Git**: branch and dirty count, read-only. Commits and pull requests are the agent's work through the shell tool
  under approvals (later). The app never writes to the user's repository by itself.
- **Terminal**: none embedded in v1. The `!` line runs a command under the same approvals.

### 4.8 Models

- **Slots** page: the seven slots (`generator`, `summarizer`, `decider`, `extractor`, `embedder.text`, `embedder.media`,
  `vision`) with current, default, escalation ladder, fallbacks, placement and pin state; switching mid-turn; drag to
  reorder the ladder, validated and written to `corvid.json` the way `corvid model switch` does it. The decider is
  shared across sessions and the page says so.
- **Catalogue**: local Starling models and the configured remote ones (Anthropic, OpenAI-compatible endpoints, Gemini),
  each with size, device fit from the doctor's GPU section, cached state and what it serves.
- **Downloads**: explicit, with sizes and progress, cancel, and a note that nothing downloads implicitly. The default
  set is about 10 GB and Kev-4B about 17 GB.
- **Placement**: CPU or CUDA per model with the fallback setting, and the doctor's driver and runtime checks beside it.
- **Latency and queue** per slot and model from `ModelMetrics`, as sparklines.
- **Privacy** per remote model: the egress posture (`Redact` by default), "no extractor bound: every cloud request is
  refused" when that holds, and "unavailable without Vault" with the secret name to set.
- **Local servers**: add an OpenAI-compatible endpoint (Ollama, llama.cpp, vLLM) with a connectivity check.
- **Kev** tiers: calibrated, a second tier for gates, degraded; a download button when degraded.

### 4.9 Memory, lessons and skills

- **Core blocks** (`user`, `agent`, `persona`): an editor with token budget meters (350, 550 and 500 by default), the
  local-only flag and revision history.
- **Facts**: a timeline and a search over the hybrid retriever; entity and relation views from the fact graph;
  bi-temporal fields shown (valid from, valid until, recorded at).
- **Pending writes**: a review queue with the proposal, why it was staged, the operation (add, update, invalidate, noop)
  and the nearest existing facts beside it, so the user sees what an update would replace; approve, correct then approve
  (G-8), reject.
- **Lessons** per project: pending and approved, approve with "supersedes", reject with a reason, and evidence links
  that open the session at the matched events.
- **Hermes import** with a dry run that lists what would be staged.
- **Skills** library: name, authorship (`user`, `agent`, `hub:<source>`), revision, last used, uses, pinned; `SKILL.md`
  rendered; git log, diff between revisions, revert; drafts awaiting review with the reason and the replay outcome
  (expected against actual); evals history (passed of total per revision); import from a folder, a hub or Hermes with
  the scanner's findings shown; pin and unpin; stale skills with an archive suggestion.

### 4.10 Jobs, gateway and MCP

- **Jobs**: a list with schedule, next run, state and last outcome; a form with the schedule grammar ("in 30m",
  "weekdays at 9am", cron, ISO) previewed as "runs next at", a pre-run script, no-agent jobs and where the result is
  delivered; run history; a run opens its session read-only; pause, resume, remove; the scheduler state (running here,
  held by another process, off) with the explanation.
- **Gateway**: adapters (Telegram, Discord, Slack) with state and since when; pairing requests in the attention tray,
  approved as owner or guest on this machine and never from a chat; paired users and revoke; deliveries waiting;
  per-chat persona and slot overrides; the guest tool allowlist, empty by default. The app can host the gateway when it
  is the engine and the person asks (U-8: off by default).
- **MCP**: servers with transport, state and trust; tools with pinned hash and held reason (new, changed, flagged with
  the matched text, injection); a diff of a held definition; accept, allow (a flagged tool then always asks), forget; an
  add-server form; `corvid mcp serve` with what it exposes.

### 4.11 Vault and secrets

The app never holds or shows a secret value. The Secrets page shows what Vault will tell it: status, each secret's name,
bound hosts, method policy (`auto`, `ask`, `confirm`), toolsets, role, budget use, and which features are waiting on a
secret that is not set, with the exact `corvid-vault set <name>` to run.

Entering a value is the one flow that must leave the app's process when the app is the engine. Proposal (U-6): a
separate Vault helper, also built with Uno, that talks to Vault alone; the main app launches it and learns only that a
secret now exists. Until it exists the app opens `corvid-vault set` in a terminal. The pasted-credential interception
(V-005), the out-of-band confirm (V-006, Vault's own dialog; the app only shows that one is pending) and OAuth (V-007)
plug in when they land. Audit appears as counts, through the read-only summary vault.md §10 allows.

### 4.12 Audit and insights

- **Event log viewer** per session: every typed event with its sequence, filters by type, tool and path, raw JSON, and a
  jump from any transcript row to its events and back.
- **Scan** with the detection rules over one session or the whole log, a severity filter, and the `--fail-on` presets.
- **Trace export** (Beacon JSONL) with the retention options and the local-memory switch.
- **Decisions**: confidence against outcome per question and checkpoint, the floors, ask rates, escalations and their
  causes. The calibration tooling exists; this is its view.
- **Egress ledger**: every cloud request with model, redaction count and bytes, and the request as sent after redaction,
  rendered on demand by the same gate (G-9).
- **Usage and cost** per session, slot and day (K-5). **Approvals**: asked, approved and denied per verb, feeding the
  rule suggestions.

### 4.13 Board and subagents

The Board shows every session as a card: state (idle, running, waiting on you), the current step, elapsed time, model,
taint and workspace. Peek opens a read-only live view; attach makes it the active session. New parallel sessions start
here. When plan §14's `delegate_task` lands, a session's children appear as a tree with goal, toolset, budget, status
and the validated output, and the Board groups them under their parent. Kanban boards come later, on the same substrate.

### 4.14 Remote and companion

Attached mode over loopback comes first. The WebAssembly build, served by the engine on the LAN after a pairing code
shown on the desktop, lets the owner read sessions, answer approvals and questions with the full card, and send messages
from a phone. Push notification goes through a channel Vault holds the key for (vault.md §7 names ntfy). Native iOS and
Android heads compile from the same code once the remote client has served a phone for a release.

### 4.15 Accessibility, themes and keyboard

- Every control has a name and a role; the transcript is a list a screen reader can walk; streaming text is announced at
  paragraph boundaries through a polite live region, and a finished turn, a question or an approval through an assertive
  one.
- Streaming never moves focus. An approval that appears while the user types follows the two-`Enter` rule.
- Reduced motion follows the platform setting and a toggle: no spinners, no shimmer, elapsed counters updated on state
  change.
- High contrast and daltonized themes beside light, dark and system; font scaling; a compact density.
- Verified per target in A0 with Narrator and NVDA on Windows, VoiceOver on macOS and Orca on Linux, since the research
  found desktop agent apps failing exactly here.
- Themes are JSON files that share colour roles with the TUI's themes where the roles coincide (U-11); keybindings use
  the TUI's `keybindings.json` format with app contexts added.

### 4.16 Platform integration

A tray or menu bar icon with the attention count and "new session"; a global hotkey that opens a floating composer for a
quick ask into the current or a new session; a `corvid://session/<id>` URL scheme that opens sessions and answers
nothing; a folder dropped onto the window opens as a workspace; single instance with activation; launch at login off
unless the user makes the app the scheduler host; manual "check for a release" that shows the notes and hands over to
`install.sh`.

## 5. Flows

Each flow names its trigger, the steps as the user sees them, and the edges.

**F1 First run.** The window opens on a doctor page before any model loads: configuration, data directory, each
catalogue model with size and cached state, the decider tier, bubblewrap, `corvid-sandbox`, the Vault socket, and the
GPU when one is present, each with its fix. The user picks models to download (the default set preselected, sizes
totalled), chooses a workspace, picks an approval mode with one sentence per mode, sets the cloud privacy posture, and
is offered a Hermes import. Every step is skippable. The app runs degraded with a badge that says what is missing, and
the page ends on an empty session.

**F2 A conversation in a workspace.** Open a folder. The status bar shows the project binding and the branch. Type,
send. The turn strip shows the ingress checkpoint's chips, then each step: a tool card appears pending, runs with a live
tail, finishes with its outcome; the answer streams into the last block. A title appears after the first exchange. Edge:
a step that escalates shows the escalation row before the generator's text; a refused cloud model shows "kept local"
with the reason.

**F3 An approval.** A card opens below the draft with the parsed command, targets, reasons, reversibility, taint and the
diff. The user reads and presses `2` for this session. The card collapses into the tool card as "approved for this
session (rule: `dotnet test` in workspace)". Pressing `3` first shows the rule that would be written. `4` denies; "why"
opens a field whose text becomes the next message. Edges: three queued prompts show "1 of 3"; an approval from another
session arrives in the tray with a notification; the deadline passes and the card closes as "denied: deadline"; a
detection match adds a red block with the two events.

**F4 Reviewing the changes.** After a turn the Changes tab lists two files. The user opens a side-by-side diff, comments
on a line, comments in the second file, and presses "send 2 comments". One message goes out quoting file and line. The
next turn's diff shows against the previous review point. Edge: a file Corvid may not read is listed by name with "not
readable by Corvid"; a binary or oversized file shows text only.

**F5 Rewind.** `Esc Esc` opens the timeline over the ledger. The user selects the entry before the bad edit, previews
the diff and chooses "files and conversation". The result lists two restored paths and one merged (the user had edited
it since), and the transcript shows a rewind marker; earlier events stay in the log and in Audit. A conflict refuses the
restore with the path and the reason, and nothing is overwritten.

**F6 Steering and queueing.** While a turn runs, the user types "also check the retry count" and presses `Enter`: a
queued chip. A second thought has to land now: `Ctrl+Enter` sends it at the next step checkpoint, and the fabric sees it
before the generator (K-3). `Esc` interrupts, keeps the work so far, and the queue goes next. `Up` takes the chip back
into the editor.

**F7 An aside.** `Ctrl+;` opens a panel beside the transcript. The question runs in a scratch fork at the session's
head, so it sees the context and writes nothing back. "Bring this in" sends the answer as a message; closing the panel
discards the fork. Mid-turn asides wait on G-15.

**F8 Escalation and the privacy gate.** A step's `difficulty` answer sends the step to the cloud slot. The row says
"escalated to claude: step budget", and the gauge shows the cloud window. The user clicks "what left the machine" and
sees the request as sent, with two values redacted and marked. "Keep local for this session" pins the local generator.
Edge: without Vault the cloud model is listed as unavailable with the secret name to set.

**F9 Taint.** `web_fetch` runs; a taint row appears and the badge turns amber. Hovering explains that network reads now
ask and that egress offers only "once". The next approval card names the taint source. When the kernel clears the taint,
a row says so and the badge goes.

**F10 A detection match.** A credential read followed by network egress completes a rule. The card shows the rule, its
severity, the two events that matched and the forced ask. "Open in Audit" filters the log to them. The Safety page
records the match; with `Detections:Enforce=false` the page shows "recording only".

**F11 Memory review.** The transcript shows "memory staged: 2 facts". The review queue shows each proposal with why it
was staged and the nearest existing facts. The user corrects one word, approves as an update, and rejects the other. The
core blocks page shows the `user` block near its budget and offers consolidation. The Hermes import previews every entry
before anything is staged.

**F12 A skill is learned.** After a successful task the agent drafts a skill. A card shows the draft, the reason, and
the replay's expected and actual steps with its pass or fail. Approve stores it with `agent` authorship and a revision;
reject records why. The library later shows its uses and last use, and a stale skill is offered for archiving.

**F13 A scheduled job.** "Every weekday at 9, summarise the inbox" creates a job through `cron_create`. The Jobs page
shows the schedule parsed and the next run. At 9 the run executes in its own session; a notice arrives with "open the
run", which opens that session read-only. Pause and resume are buttons; when another process holds the scheduler lock,
the page says which.

**F14 Models.** The user opens Models, sees the generator ladder (`qwen3.5-0.8b`, then `qwen-27b`, then `claude`), drags
`claude` above `qwen-27b`, and the configuration is validated and written. A download of `kev-4b` shows 17 GB and
progress; cancelling leaves the cache consistent. Placing `kev-0.8b` on CUDA shows the driver check beside it.

**F15 An MCP tool changed.** A server's tool description changed since its pin. The transcript shows "tool held", and
the MCP page shows the diff of the old and new definition with any flagged text highlighted. Accept pins it; allow, for
a flagged one, registers it as always asking; forget drops the server's pins.

**F16 A pairing request.** Someone messages the Telegram bot. A pairing code arrives in the attention tray. The user
approves as guest; the Gateway page shows the guest with an empty tool allowlist, which the user can widen.

**F17 Secrets.** The Models page says the Anthropic slot waits on `anthropic.key`. The Secrets page shows the name, its
host binding and the command to set it, and opens the Vault helper (U-6) or a terminal. When the secret exists the slot
becomes available. The app never saw the value.

**F18 The Board.** Three sessions run in two workspaces. The Board shows each with its step and state; one waits on an
approval, which the tray also lists. Peek opens it read-only; attach switches to it. A child session, once subagents
exist, shows under its parent with its budget.

**F19 Attachments and images.** A dropped screenshot becomes a chip and renders in the transcript (K-8); a dropped
source file becomes an `@` reference; a dropped folder is offered as a workspace to open.

**F20 Search and export.** `Ctrl+Shift+F` searches across sessions through the episodic index; a hit opens the session
at that turn. Export writes markdown, a self-contained HTML page or JSONL to a file the user picks.

**F21 Audit.** The user opens Audit for a session, filters to `tool_call_issued` and `approval_resolved`, reads a raw
event, runs a scan with "fail on high", and exports a trace with metadata retention.

**F22 Remote approval.** With attached mode on, the phone's browser opens the paired WebAssembly client. An approval
arrives as a notification; the card shows the same parsed command, reasons and diff, and the owner answers. A guest
device sees nothing.

**F23 Degraded states.** Models missing: a banner with the download button. Vault absent: cloud slots hidden with the
reason. Bubblewrap missing: shell calls refused, with the install hint from doctor. Decider degraded: a badge. GPU
fallback: a notice row. Scheduler held elsewhere: the Jobs page says which process. A crash: reopen; every session is in
the log, and a recap says what was running.

**F24 Settings.** A typed settings page over `corvid.json` with the validator's messages inline, a raw JSON editor,
keybindings with conflict detection, themes, accessibility, notifications and layout. Overrides given with `--set` are
shown as "set for this run".

**F25 Quick ask.** The global hotkey opens a floating composer over any app; the text goes to the current session or a
new one; the window comes forward only when the answer needs it.

## 6. Keyboard map

Defaults, all remappable in `keybindings.json` with the TUI's contexts plus `app`, `rail`, `dock`, `review` and `board`.

| Key | Action |
| --- | --- |
| `Enter` / `Shift+Enter` | send / newline |
| `Ctrl+Enter` | steer now |
| `Esc` | interrupt the turn (work kept; the queue goes next); close a card or panel |
| `Esc Esc` | with text: clear the draft to history; empty: the rewind timeline |
| `Ctrl+K`, `Ctrl+P` | command palette |
| `Ctrl+N` / `Ctrl+Shift+N` | new session / new window |
| `Ctrl+Tab` | session switcher |
| `Ctrl+;` | aside |
| `Ctrl+O` / `Ctrl+T` / `Ctrl+Shift+D` | expand tool output / thoughts / decision chips |
| `Ctrl+B` / `Ctrl+Shift+B` | toggle the dock / the rail |
| `Ctrl+1` to `Ctrl+7` | dock tabs |
| `Ctrl+F` / `Ctrl+Shift+F` | find in the session / search across sessions |
| `Ctrl+R` | prompt history search |
| `Shift+Tab` | cycle approval modes |
| `1` to `5` | answer the focused approval card |
| `Ctrl+Shift+C` | Changes |
| `Ctrl+,` | settings |
| `Alt+P` | switch the generator without leaving the composer |
| `F1`, `?` | help and shortcuts |

## 7. Visual language

- Proportional type for prose and monospace for code, commands and paths; reading width capped near 80 characters in
  Focus; comfortable and compact densities.
- One colour role per Corvid concept, and never colour alone: tool kinds carry icons; risk levels 0 to 4 use one scale;
  confidence chips say acted or unsure in words; taint is amber with its icon; detection is red; escalation is a cloud
  glyph with the model's name; staged memory has a dotted border; a reversible call has a marker.
- Motion only on state change: a caret while streaming and a spinner while something runs, both gone under reduced
  motion.
- Empty states say what to do next in one line, with the doctor's fix when something is missing.
- Light, dark, system, high contrast and daltonized themes ship; custom themes are JSON files beside `corvid.json`.

## 8. How it works underneath (a sketch)

| Project | Role | References |
| --- | --- | --- |
| `src/Corvid.App.Core` | view models and the fold from session updates to view state; no Uno types; unit-tested headless | `Corvid.Abstractions` and the update contract |
| `src/Corvid.App` | the Uno single project: desktop and WebAssembly heads first, mobile later; XAML or C# markup (U-2); themes, keybindings, platform integration | `Corvid.App.Core`, Uno packages |
| `src/Corvid.App.Engine` | the in-process `ISessionClient` over `CorvidRuntime`, plus gateway and scheduler hosting | `Corvid.Host`, like `Corvid.Acp` |
| the update contract (in `Corvid.Host` with K-10, or its own `Corvid.Protocol`) | the serialised update stream and session methods for attached mode: ACP's `session/*` plus `_corvid/*`; a Vault-held token as `corvid serve` uses; owner pairing | `Corvid.Abstractions` |

The architecture checker gets rules: only the desktop head and `Corvid.App.Engine` may reference `Corvid.Host`; the
WebAssembly head never does; Uno packages appear in `Corvid.App` only.

- **State** is a fold over the update stream per session, the same mapper replay uses, so a loaded session and a live
  one are one code path. UI-only state (layout, pins, drafts, archived sessions, review points) lives in `ui.db` beside
  the event log and never in it (G-7).
- **Rendering**: a virtualised transcript; markdown parsed with Markdig into native text blocks; code coloured with the
  grammar set the TUI plans, kept in a shared `Corvid.Markdown`; no WebView, so HTML from a model never executes and the
  accessibility tree stays native (U-5). Updates coalesce to the frame rate, and no timer runs while idle.
- **Sanitization** as in tui.md §3.7: every string from the model, a tool, an MCP server or a file name is data; links
  and images are built by the app, and remote images are never fetched.
- **Testing**: view-model tests over recorded update streams (the ACP exchange fixtures in
  `tests/Corvid.Acp.Tests/Exchanges/` are a start); UI tests on the headless Skia host and Playwright against the
  WebAssembly build; screenshot tests per theme at three widths; automation-tree assertions for every screen; budgets in
  CI for startup, idle bytes and the memory window.
- **Packaging**: a single-file desktop build beside `corvid`, installed by `install.sh --app` with a `.desktop` entry;
  Linux first, as the CLI is; Windows and macOS builds run attached to a Linux engine until the sandbox and Vault exist
  there.
- **Attached mode security**: loopback by default; a Vault-held token for anything else; approvals accepted only from a
  client the owner paired on the desktop; guests get read-only views or nothing.

## 9. Kernel and host work the app needs

K-1 to K-10 from tui.md §9 apply unchanged: thought chunks, streamed tool output, steering, load progress, usage,
structured questions, rule removal, images, titles and `SessionUpdates`. New:

| Id | Change | Needed by |
| --- | --- | --- |
| G-1 | the update stream and session methods over a local socket or WebSocket, with Vault-held tokens and owner pairing | attached mode, WebAssembly, phone |
| G-2 | the approval assessment on the stream for calls that ran unasked | "why it ran unasked" on every card |
| G-3 | restore of one file, later one hunk, from the ledger | revert in Review |
| G-4 | a workspace per session or per runtime instance, and worktree creation | several workspaces in one window, worktree isolation |
| G-5 | a search API over sessions through the episodic index | cross-session search |
| G-6 | user titles and recaps as events (extends K-9) | rename, recap |
| G-7 | the `ui.db` conventions in the host, so the CLI and the app share drafts and pins | drafts, pins, layouts |
| G-8 | correct-then-approve for staged memory | memory review |
| G-9 | render the redacted request of a generator call on demand | egress ledger |
| G-10 | a per-session approval mode | the mode control, the TUI's `Shift+Tab` |
| G-11 | narrowing an "always" rule's paths before it is written (extends K-7) | rule preview |
| G-12 | a single-driver lock per session and read-only tailing from another process | sessions open in a terminal |
| G-13 | job create and edit through a host API with schedule preview | the Jobs form |
| G-14 | gateway and scheduler hosting with a status API in-process | the Gateway page in the app |
| G-15 | forking at a step boundary while a turn runs | asides mid-turn |
| V-n | a read-only catalogue and status call from Vault (names, hosts, policy summary, audit counts) | the Secrets page |

## 10. Phasing

**A0, get a feel.** Clickable mockups of the shell, a session, an approval, Review and Rewind at three widths, checked
against §3's layout rules (`ui-mockups/`, like `tui-mockups/`). Then the prototype: the Uno desktop head on Linux and
Windows with the in-process engine; rail, session list, conversation with streaming and tool cards, the approval card,
Changes with diffs, the status bar, the palette, minimal settings; the recorded exchanges as fixtures; accessibility
checked on each platform's screen reader before the layout is frozen. K-1, K-2, K-4, K-6, K-10, G-2, G-7.

**A1, depth.** Review comments, the rewind timeline, the Memory, Skills, Jobs, Models, MCP, Gateway, Safety and Audit
pages, the attention tray and notifications, the Board, search, export, themes, keybindings, recaps, rule suggestions.
K-3, K-5, K-7, K-9, G-3, G-5, G-6, G-8 to G-14.

**A2, reach.** Attached mode and the daemon, the WebAssembly client with pairing, phone notifications through Vault,
subagent views, worktrees per session, images, voice. G-1, G-4, G-15, K-8.

## 11. Open decisions

| Id | Decision | Proposal |
| --- | --- | --- |
| U-1 | In-process first, with the update stream as the only seam between views and engine | yes; attached mode is a transport change in A2 |
| U-2 | XAML or C# markup | XAML, for Hot Design and designer hand-off; revisit in A0 if the markup API reads better for this team |
| U-3 | MVUX or MVVM | MVUX: its feeds and immutable state match a fold over events; decide after the prototype |
| U-4 | Windows head: Skia desktop or WinAppSDK | Skia desktop first, one renderer everywhere; a WinAppSDK head if notifications or accessibility need it |
| U-5 | Markdown through native text blocks or a WebView | native blocks |
| U-6 | Secret entry through a separate Vault helper process | yes; a terminal hand-off until it exists |
| U-7 | The app's name and binary | the window is "Corvid"; the binary `corvid-app`, installed by `install.sh --app` |
| U-8 | Whether the app hosts the scheduler and the gateway when it is the engine | only when the person asks: off by default (`Corvid:App:HostGateway`, `Corvid:App:HostScheduler`, or Start hosting on the Gateway and Jobs pages, which asks once and names what it does), then under the existing one-lock rule, taken over on the engine's own timer while another process holds the lock; the status bar says so whenever the app hosts either |
| U-9 | Linux packaging | tarball and `.desktop` through `install.sh`; Flatpak later, once bubblewrap inside a Flatpak sandbox is understood |
| U-10 | When the mobile heads start | after the WebAssembly client has served a phone for a release |
| U-11 | A tab strip for sessions, and shared theme and keybinding files with the TUI | no tab strip in v1; shared files |

## 12. Sources

- Repository: [`tui.md`](tui.md); the research notes in [`docs/research/tui/`](../research/tui/README.md) (feature
  inventory and sentiment, Claude Code, Codex, the Ink-based CLIs, OpenCode and others, testing and accessibility);
  [`architecture.md`](../architecture.md) (sessions, approvals, checkpoints, ACP and HTTP, gateway, MCP pinning,
  detection rules); [`vault.md`](vault.md) §6 to §11; `src/Corvid.Abstractions/Events/SessionEvent.cs`,
  `src/Corvid.Acp/TurnReporter.cs`, `src/Corvid.Approvals/ApprovalPipeline.cs`, `src/Corvid.Host/Commands/`.
- Uno Platform: [requirements and targets](https://platform.uno/docs/articles/getting-started/requirements.html)
  (fetched 2026-10-23), [Uno Platform 6.4](https://platform.uno/blog/uno-platform-6-4/), [6.5 release
  standup](https://platform.uno/blog/6-5-release-community-standup/), [InfoQ on
  6.5](https://feed.infoq.com/uno-platform).
- Agent desktop and web apps (read 2026-10-23; the Claude Code pages are third-party accounts): [claudelog on Claude
  Code for desktop](https://claudelog.com/faqs/claude-code-desktop-app/), [buildfastwithai on the desktop
  redesign](https://www.buildfastwithai.com/blogs/claude-code-desktop-redesign-2026), [Amp: so long, TUI
  sidebar](https://ampcode.com/news/so-long-tui-sidebar), [Hamel Husain on
  Amp](https://hamel.dev/notes/coding-agents/amp.html), [OpenCode 2.0](https://anoma.ly/notes/opencode-2-0/), Warp's
  block model and the Hermes and Codex desktop issues as cited in the research notes.
- Local front ends (roundups, opinion grade): [iunera's 2026
  comparison](https://www.iunera.com/kraken/enterprise-ai/top-20-tools-to-run-llms-locally-in-2026-ollama-anythingllm-open-webui-lm-studio-vllm-and-every-real-alternative-compared/),
  [promptquorum on local LLM front ends](https://www.promptquorum.com/local-llms/best-local-llm-frontends).

## Changelog

| Version | Date | Change |
| --- | --- | --- |
| 0.1 | 2026-10-23 | First draft: the feature catalogue and the flows, before mockups. |
