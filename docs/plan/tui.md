# Corvid TUI: design

Version 0.1, 2026-10-22. Status: design proposal for the terminal UI of `corvid chat`, replacing the Spectre.Console
scrollback renderer of C-013. The evidence behind it is in [`docs/research/tui/`](../research/tui/README.md): eight
research notes on Claude Code, Codex CLI, the Ink-based CLIs, OpenCode and pi, terminal protocols, .NET options,
feature inventories and testing practice, each claim with a URL, gathered 2026-10-21. Plan §12 names the surface;
this document is its design.

## 0. Thesis

The terminal coding agents of 2026 converged on one shape (a streaming transcript, collapsed tool cards, a `/`
palette, an `@` file picker, `!` shell passthrough, a resume picker, a permission prompt with scopes) and then split
on one decision: whether the application owns the screen. Claude Code, Codex and pi all moved to an alternate-screen
renderer by default during 2026, and each kept the scrollback renderer as an opt-in,
because the fullscreen move trades flicker and scroll jumps for lost native scrollback, hijacked selection and
mouse-capture complaints. Gemini CLI made the same move twice and reverted it twice. The users who switch tools name
rendering stability, footprint and permission ergonomics as the reasons, not widgets.

Corvid's TUI is built on three commitments:

1. **Terminal citizenship first.** The default renderer draws into the terminal's own scrollback, never clears it,
   never captures the mouse, and keeps the live region smaller than the window. Selection, search, tmux copy mode,
   screen readers and SSH keep working because the terminal keeps owning the transcript.
2. **One component model, three renderers.** The same components render in inline mode (default), in fullscreen
   mode (opt-in, alternate screen, mouse, virtualized transcript) and in linear mode (screen readers, pipes). The
   choice is a setting and a slash command, switchable in a session, as Codex's `/tui` and Claude Code's `/tui` are.
3. **Solid before pretty.** The terminal layer is written in .NET from raw bytes: its own termios handling, its own
   input decoder, a probe with a sentinel and a deadline, a restore routine that runs on every exit path, a frame
   writer with one `write(2)` per frame, and tests that drive the real escape output through a headless terminal.
   Every failure catalogued in the research has a test or an invariant here.

What "better than anything on the market" means, measured:

| Property | Target | Who misses it today |
| --- | --- | --- |
| Full-screen clears of the main buffer | zero, in every terminal, counted by a built-in detector | Claude Code classic (about a third of sessions in Jan 2026), Gemini CLI, Hermes |
| Scroll position while streaming | never moved by the renderer; only the terminal's own scroll-on-output setting can move it | Claude Code #826 (690 upvotes, open since April 2025) |
| Native selection and search | always work in inline mode; Shift bypass in fullscreen | every alternate-screen agent |
| Key to echo, app side | under 5 ms regardless of transcript length | Hermes (about 1 s for CJK on long transcripts), Claude Code (keystroke lag issues) |
| Idle cost | zero writes, zero timers when nothing animates | Claude Code (3.5 to 4.6 percent CPU idle), Codex (32 ms spinner redraw) |
| Time to first paint | under 100 ms; models load after the prompt is usable | Corvid today ("Loading models…" blocks the prompt) |
| Memory over a day-long session | bounded by a window of rendered lines, not by transcript size | Claude Code (6.9 GB after 14.5 h), Copilot CLI (several leak fixes) |
| Terminal state after a crash, Ctrl-Z or an editor hand-off | always restored, in this order: sync update, kitty flags, mouse, paste, alt screen, cursor, termios | Codex (kitty fragments after Ctrl-C), Claude Code (alt screen left on after Ctrl-Z until 2.1.266) |
| Untrusted bytes reaching the terminal | none: every model and tool string is sanitized before rendering | Claude Code 0.2.76 (Trail of Bits), Hermes (#124205 broke the keyboard on Windows) |

## 1. What the field taught us

The notes in `docs/research/tui/` carry the citations; this section is the digest the design rests on.

**Renderer history.** Claude Code rendered inline through a fork of Ink, which clears and redraws the viewport
whenever the frame is as tall as the terminal; that produced the flicker and "scroll to top" reports that are still
its most upvoted issues. A from-scratch cell-diffing renderer (December 2025, a 16 ms frame budget, packed typed
arrays, DEC 2026 synchronized output) cut full clears to "once or twice instead of thousands", and a virtual-viewport
alternate-screen renderer (March 2026) became the default for new accounts in May 2026; the classic renderer remains
mandatory for screen-reader mode. Codex rewrote from TypeScript and Ink to Rust and ratatui in mid-2025 for
distribution and sandboxing reasons, built an inline renderer that pushes finished rows into native scrollback with
scroll regions, then made an owned fullscreen transcript the default in September 2026 and kept the inline path
behind `/tui`. pi's author built a framework-free line-diff renderer for the main screen, hit the one case it cannot
handle (a change above the viewport while the user has scrolled up) in July 2026, added an alternate-screen
renderer behind the same interface, and made it the default in pi 1.0. OpenCode runs three renderers on OpenTUI
(alternate screen, and a "split footer" mode whose scrollback bugs were filed through October 2026). Gemini CLI
shipped an alternate-buffer default in November 2025, reverted it after nine days because scrolling and selection
broke on Windows consoles and tmux, tried again in April 2026 and reverted again for performance.

**What every project ended up doing.** Cap the live region below the terminal height. Write finished turns once.
Diff lines or cells instead of erasing and rewriting. Coalesce streaming to a frame tick. Use synchronized output
only where the terminal implements it (tmux only from 3.7). Treat a resize as "repaint the live region, leave the
rest to the terminal". Decode input in a dedicated layer with a focus stack. Sanitize every string from the model
and from tools. If the alternate screen is used, budget for selection, copy, wheel speed, scrollbars, exit replay and
ConPTY repaint from day one. Instrument: full-clear counters, an overflow detector, a "no continuous render" test.

**What users praise and punish.** Praise goes to interaction design: rewind that restores files, plan mode, queueing
a message while the agent works, a visible task list, menu-driven questions, instant startup and a small footprint.
Complaints go to rendering (flicker, jumps, memory, CPU), to hijacked terminals (lost scrollback, broken copy,
cleared screen on exit), to background daemons and auto-updates, and to "Always" approvals that silently persist
broad rules.

**.NET.** No .NET library offers a cell-diff renderer with a proper input decoder under a permissive licence.
Spectre.Console is a scrollback renderer whose Live display rewrites the whole block per tick, with no raw mode and
no synchronized output. Terminal.Gui 2.5 has the driver layer (termios, kitty keyboard, an inline mode) but is a full
widget framework with heavy transitive dependencies and no synchronized output. Terminaux and Tutu are GPL. So the
terminal layer is written here. System.Console must be bypassed for input and output after start: it switches the
tty to no-echo on the first read and never switches back, re-applies its own termios on SIGCONT, dispatches every
signal but SIGINT, SIGQUIT and SIGTERM on the thread pool, and on Windows never enables virtual-terminal processing.
Measured on this machine, a self-contained single-file .NET 10 app starts in about 26 ms; ReadyToRun halves the
start of a Spectre-sized app (141 ms to 70 ms); single-file compression costs about 220 ms and must stay off.

**The protocols.** One probe batch written in one call, ended with a DA1 sentinel, answers everything a renderer
needs: DECRQM 2026 (synchronized output), DECRQM 2027 (grapheme width), the kitty keyboard query, XTVERSION, and
optionally the kitty graphics and OSC 99 queries. A DA1 reply without a feature reply means unsupported; a deadline
covers terminals that never answer. iTerm2 answers "permanently reset" for every mode including fictional ones,
Konsole never answers DECRQM, and ConPTY was observed ignoring XTVERSION, so identity (XTVERSION, `TERM_PROGRAM`,
`WT_SESSION`, `TMUX`) refines the probe rather than replacing it. The kitty protocol's flag 1 keeps unmodified Enter,
Tab and Backspace as their legacy bytes, so `reset` can still be typed after a crash, and reports modified Enter as a
CSI u sequence; Codex pushes flags 1 and 4 and gets Shift+Enter that way. Whatever is pushed must be popped on every
exit path. Inside tmux the kitty protocol does not exist (only `extended-keys`), and width follows tmux's wcwidth
rather than the outer terminal's.

## 2. Principles

- **Never emit `CSI 3J`, `CSI 2J` or a cursor-up past the live region on the main screen.** Committed transcript
  lines belong to the terminal. The only region Corvid repaints is the live region, and its height is always smaller
  than the window.
- **Everything the user sees is a component that renders lines for a width.** Lines never exceed the width, every
  line ends with a style reset, and output is cached per width. This is the invariant that makes line diffing safe.
- **Input is decoded, not read.** A byte parser with a three-way result (need more, error, event) feeds a focus stack.
  Keystrokes are echoed by the composer without waiting for the transcript. Query replies never reach the composer.
- **The agent runs tools, the UI only shows them.** The TUI consumes the same session updates the ACP server emits,
  extended with Corvid-specific events. A different process could consume them tomorrow.
- **Degrade by capability, not by hope.** Two tiers of terminal, decided by the probe, and a conservative first frame
  that upgrades when replies arrive.
- **Restore is idempotent and runs everywhere.** Normal exit, unhandled exception, SIGINT, SIGTERM, SIGHUP, SIGTSTP,
  editor hand-off and the end of every test.
- **Untrusted text is data.** Model output, tool results, MCP descriptions, file names and branch names are
  sanitized before they reach a cell. Corvid adds its own links and colours afterwards.
- **No chrome for its own sake.** No pets, no gradients, no animated banners. Spinners stop when the user asks for
  reduced motion and when nothing is running.

## 3. Architecture

### 3.1 Projects

| Project | Role | Dependencies |
| --- | --- | --- |
| `src/Corvid.Terminal` | the terminal layer: raw mode and console modes, the probe and capability model, the input decoder and focus routing, width measurement, the frame writer, the restore routine, sanitization, OSC helpers (links, clipboard, notifications, title, progress, prompt marks) | none; `LibraryImport` to libc and kernel32 only, as `FileKinds` and `GlibcMalloc` already do |
| `src/Corvid.Tui` | the chat UI: session update model, components (transcript blocks, composer, status line, pickers, overlays), the three renderers, markdown and diff rendering, themes, keybindings | `Corvid.Terminal`, `Corvid.Host` (session updates, slash commands, approvals), Markdig for markdown |
| `src/Corvid.Cli` | `corvid chat` wires the TUI to a runtime; other verbs keep Spectre.Console for plain tables | unchanged shape |
| `tests/Corvid.Terminal.Tests`, `tests/Corvid.Tui.Tests` | decoder fixtures, probe tests, emulator-driven renderer tests, component snapshots, PTY smoke tests | XTerm.NET (headless emulator), Porta.Pty, both test-only |

The architecture checker gets two rules: `Corvid.Terminal` is a core project with no packages; `Corvid.Tui` is a
surface project that may reference the composition root, like `Corvid.Acp`, and only the CLI references it.

### 3.2 The session update stream

`TurnReporter` in `Corvid.Acp` already turns the kernel's feed into ordered, typed updates: message chunks, tool calls
with kinds, titles, locations and diffs from the write ledger, permission requests with proposed diffs, available
commands. That is the right contract for a UI, and the research found that the ACP schema (v1, with its draft v2)
covers streamed chunks, thought chunks, plans, usage, notices, compaction and config options. So the reporter moves
to `Corvid.Host` as `SessionUpdates`, an in-process channel of records shaped like ACP session updates, and both the
ACP server and the TUI subscribe to it. Corvid-specific updates carry a `corvid.` prefix and would travel as
`_corvid/*` notifications over the wire:

| Update | Source | Shown as |
| --- | --- | --- |
| `agent_message_chunk`, `agent_thought_chunk` | generator stream (thought chunks need K-1 below) | streaming markdown; a collapsed "thought for 4 s" row |
| `tool_call`, `tool_call_update` with `diff`, `terminal` or `content` | `TurnFeed`, the write ledger, tool output streaming (K-2) | tool cards |
| `request_permission` | approval pipeline | the approval block |
| `plan` | `TodosSet` events | the task list |
| `usage_update` | token counter and model usage (K-5) | the context gauge and cost |
| `compaction_update` | compaction (C-016) | a marker row and a status badge |
| `notice` | job completions, MCP held tools, detection matches, decider notices | notice rows and the attention queue |
| `corvid.decision` | `CheckpointDecisions` from the fabric | decision chips (verbose toggle) |
| `corvid.model_switched` | `ModelSwitched` events | an escalation row with the reason |
| `corvid.taint` | taint changes | the taint badge |
| `corvid.checkpoint` | `CheckpointTaken`, `SessionRewound` | ledger entries for `/rewind` |
| `corvid.engine` | model loading, device queue depth, latency, scheduler lock | the status line |
| `corvid.question` | a `Clarifying` turn (K-6) | a question block that focuses the composer |

Replaying a session (`--session`, the resume picker) runs the stored events through the same mapper, so resumed and
live transcripts look identical. This is what `session/load` already does for ACP clients.

### 3.3 Threads

Three dedicated threads, none from the pool. The **input thread** blocks on `read(2)` of fd 0 (or `ReadConsoleInputW`
on Windows), runs the decoder and posts events to the UI queue. The **UI thread** owns the component tree, the focus
stack, the frame scheduler and the writer; it is the only thread that touches the terminal. The **engine threads**
(session actor, device scheduler, ONNX intra-op threads) post session updates to the UI queue and never render.
Signals arrive on the runtime's signal thread and are forwarded as events, except SIGTSTP, whose handler restores
the terminal synchronously before the process stops. The window size is re-read with `TIOCGWINSZ` at the start of
every frame rather than trusting SIGWINCH delivery, which .NET dispatches through the thread pool.

ONNX intra-op threads are capped below the core count and their nice value is lowered from inside those threads;
raising the UI thread's priority is not possible without privileges on Linux and is not attempted. The GC runs in
`SustainedLowLatency` while a turn streams.

### 3.4 Frame pipeline

A frame is requested, never ticked. Any state change (a key, a chunk, a tool update, a resize, a timer that drives a
visible animation) sets a dirty flag; the scheduler draws at most once per 16 ms and once per request,
with the trailing request always honoured. A draw asks the root component for lines (inline) or cells (fullscreen),
diffs against the back buffer, and writes one byte array wrapped in `CSI ? 2026 h` and `CSI ? 2026 l` when the
terminal supports it. Writes go through the raw stdout stream, never `Console.Out`. When nothing is dirty, nothing is
written and no timer is armed; the spinner is a 125 ms timer that exists only while a tool or generation runs and
the user has not asked for reduced motion; the elapsed counter ticks once a second under the same conditions.

### 3.5 Components

```
interface IComponent
{
    IReadOnlyList<StyledLine> Render(int width);   // never wider than width; reset at the end of each line
    bool HandleInput(InputEvent e);                // only when focused or capturing
    void Invalidate();                             // drops caches for every width
}
```

Containers stack children; a `Transcript` holds `Block`s; the `Composer` is a text editor; overlays (pickers, the
approval panel in fullscreen, `/btw`) sit on a modal stack with capture. `StyledLine` is a run list of text and
style ids with a measured width, so diffing compares ids and strings, not escape text. Blocks come in these kinds:
user message, assistant message (streaming or committed), thought, tool card (pending, running, awaiting approval,
done, denied, failed), diff card, approval, question, decision chips, notice, compaction marker, rewind marker,
system (banner, resume hint).

### 3.6 The three renderers

**Inline** (default). The screen is the terminal's own; Corvid owns a live region at the bottom whose rows are
always fewer than the window's. The live region holds the streaming tail of the current assistant message (a
bounded number of lines; the rest is committed as it stabilizes), running tool cards, the approval block when one is
open, the task list when toggled, the status line and the composer. Committed blocks are written once above the
live region and forgotten by the renderer (their source stays in the session for `/copy`, `/export` and the
fullscreen switch). Insertion uses one of two strategies chosen by the probe:

- *Scroll region* (rich tier): set `DECSTBM` to rows 1 through the row above the live region, move to that region's
  last row, emit each committed line with `\r\n` so the region scrolls and its top rows enter scrollback, reset the
  region, restore the cursor. Line feeds, not `CSI S`: xterm.js deletes rows on partial-region scroll-up, which is
  why Codex's inline mode leaves gaps in VS Code and why OpenTUI's split footer lost rows.
- *Print above* (conservative tier, ConPTY, anything that fails the emulator test): move to the live region's origin,
  clear to the end of the screen, print the committed lines, repaint the live region below them. The repaint is
  small and is wrapped in synchronized output where available.

Streaming follows Codex's two-region model: a markdown collector commits lines at newline boundaries once they can
no longer change (open code fences and tables hold back until closed), and only the tail is mutable. The live region
therefore never needs to change anything above itself, which removes the one case pi's main-screen renderer could not
handle. On resize, the renderer re-measures the width, clears from the live region's origin to the end of the
screen, and repaints only the live region; lines already in scrollback reflow under the terminal's own rules.
Nothing is ever duplicated because nothing above the live region is ever redrawn.

Limits accepted in inline mode: no mouse (the terminal keeps it), a collapsed tool card expands by printing its
expanded form as a new block or opening an overlay, and the terminal's scrollback limit is the transcript's limit.

**Fullscreen** (opt-in, phase T1). `CSI ? 1049 h`, a cell grid the size of the window, a virtualized transcript that
renders only visible blocks with an overscan, app-owned scrolling with wheel, PageUp and PageDown, Home and End,
`{` and `}` to jump between prompts, a scrollbar, a "jump to bottom · N new" pill when the user has scrolled up, and
auto-follow paused while scrolled. Mouse reporting (SGR 1006, button-event motion rather than any-motion inside
multiplexers) with `XTSHIFTESCAPE` so Shift gives native selection where the terminal supports it; in-app
drag selection with word and line granularity, copy on release through the native clipboard tool first and OSC 52
second, with a toast naming the path used. `/export` and the transcript key `[` write the whole conversation into
native scrollback. On exit the main screen is restored and the final transcript is printed, or only a resume hint
when configured. ConPTY hosts get a full-repaint option because they coalesce positioned writes badly. A crash
counter falls back to inline after two failed starts, as Claude Code does.

**Linear** (`--ax-screen-reader`, pipes, `TERM=dumb`). Append only. Labelled lines (`you:`, `corvid:`, `tool:`,
`approval:`), static status text instead of spinners, numbered menus answered with digits, the hardware cursor
parked on the composer caret, a bell when a turn ends or a prompt waits, OSC 133 marks per turn. It is the inline
renderer with the live region reduced to the composer, which is why it costs little.

### 3.7 Terminal layer details

**Raw mode and console modes.** `tcgetattr` once at start (saved for restore), then a raw termios: no `ECHO`,
`ICANON`, `IEXTEN`, `ISIG` (Ctrl-C is a key), no `IXON`, `ICRNL`; `OPOST` left on. On Windows:
`ENABLE_VIRTUAL_TERMINAL_PROCESSING | DISABLE_NEWLINE_AUTO_RETURN` on stdout with the documented retry without the
second flag, input through `ReadConsoleInputW` records (which deliver Shift+Enter and Ctrl+Enter as distinct keys
on every Windows terminal, and the only path that delivers resize events), the output code page set to 65001 and
restored at exit, which .NET does not do. After the terminal layer starts, nothing calls `Console.ReadKey`,
`Console.In`, `Console.KeyAvailable` or `Console.GetCursorPosition`.

**The probe.** One write: `CSI ? 2026 $ p`, `CSI ? 2027 $ p`, `CSI ? u`, `CSI > 0 q`, `OSC 11 ; ? ST`, then `CSI c`.
A one-second deadline (three on a link the probe detects as remote). The first frame is drawn immediately in the
conservative tier; replies upgrade the capability model and the next frame uses them. Bytes received during the
probe that are not replies are replayed into the decoder. The result, with identity from `TERM`, `TERM_PROGRAM`,
`WT_SESSION`, `TMUX`, `ZELLIJ`, `SSH_TTY` and XTVERSION, decides:

| Capability | Rich tier when | Conservative when |
| --- | --- | --- |
| synchronized output | DECRPM 2026 answered 1 or 2 (tmux 3.7+, Ghostty, kitty, WezTerm, foot, Alacritty, iTerm2, VTE, Windows Terminal 1.24+, VS Code 1.109+) | no answer, 0, 4, or tmux below 3.7 |
| kitty keyboard flags 1 and 4 (never 2 or 8) | `CSI ? u` answered and not inside tmux or screen | otherwise legacy decoding with an Esc timeout |
| scroll-region insertion | the emulator-tested terminals: xterm-compatible, Ghostty, kitty, WezTerm, foot, Alacritty, iTerm2, tmux | xterm.js hosts, ConPTY, zellij (it keeps its own soft wrapping), unknown |
| OSC 8 links | adoption list by identity | plain text |
| OSC 52 copy | identity allow-list (never VTE); tmux only with `set-clipboard on` | native clipboard tools |
| notifications | OSC 99 on kitty, OSC 777 on foot and WezTerm, OSC 9 on iTerm2 and Ghostty | `BEL` |
| OSC 9;4 progress | Windows Terminal 1.6+, Ghostty 1.2+, iTerm2, ConEmu, tmux with the feature | off |
| truecolor | `COLORTERM` or identity | 256 colours, or 16 under `TERM=*-16color` |
| width model | DECRPM 2027 answered 1 or 2: grapheme clusters capped at two cells | per-codepoint wcwidth capped at two; always wcwidth inside tmux; a one-time CPR calibration with a known joined emoji corrects the model |

**The decoder.** Fed arbitrary byte chunks; returns need-more, error or an event. It understands kitty CSI-u
including colon sub-parameters and release events (Hermes dropped keystrokes on both), legacy sequences with terminfo
independent fallbacks, `modifyOtherKeys`, bracketed paste split across reads, SGR mouse, focus in and out, and the
query replies (DECRPM, kitty flags, XTVERSION DCS, OSC 10 and 11, DA1, CPR), which go to a reply channel, never to
the composer. A lone Escape resolves after 25 ms locally and 150 ms over a detected remote link. Windows console
records are translated to the same events.

**Width.** Grapheme-aware measurement with a two-cell cap, variation selectors adjusting by one, regional indicator
pairs as two, ambiguous width narrow unless the locale says otherwise; the model switches to per-codepoint wcwidth
inside tmux. Multi-codepoint emoji are kept out of width-critical cells (box corners, right-aligned columns, the
last column), and glyphs that would straddle the last two columns are replaced with an ellipsis.

**Restore.** One idempotent routine, registered before the terminal is touched, run from `ProcessExit`,
`UnhandledException`, the SIGINT, SIGTERM and SIGHUP handlers, the SIGTSTP handler and the editor hand-off scope:
end an open synchronized update; pop the kitty stack and, on process exit, also send the clear form `CSI < u` and
`CSI > 4 ; 0 m`; disable mouse (1000, 1002, 1003, 1006, 1007), focus (1004) and bracketed paste (2004); leave the
alternate screen; show the cursor and reset its style; apply the saved termios; flush. Cooked mode comes last so the
shell never sees a half-restored keyboard. The SIGTSTP path restores, then stops the process itself; the SIGCONT path
re-applies raw mode unconditionally (shells restore their own termios), re-reads the size and repaints. The kitty
flag for key-release reports (2) is never pushed, which is the source of every documented leak of key fragments
into the shell. The hand-off scope (`Ctrl+G` editor, `!` in pass-through mode, `/export` to a pager) pauses the input
thread, restores, runs the child with inherited stdio, re-applies modes, discards query replies the child provoked,
and repaints.

**Sanitization.** Every string from the model, a tool, an MCP server, a file name or a branch name passes one
function before it becomes a `StyledLine`: C0 controls other than `\n` and `\t` are caret-encoded, C1 bytes and every
ESC-initiated sequence are removed and marked, invisible Unicode (tag characters, bidi controls, zero-width
characters) is stripped with a notice when it comes from a paste, and Corvid's own SGR and OSC 8 are added only after.
Tool output is rendered as text with style tokens, never as pass-through bytes.

## 4. Features

The lists below mark each feature as table stakes (present in ten or more of the thirteen products surveyed, or
named as "the basics" when missing), a Corvid differentiator (something the kernel already enables and others
cannot match), or later. "Kernel" names the contract the feature rests on; items marked K-n need the kernel work
listed in §9.

### 4.1 Composer

| Feature | Class | Design | Kernel |
| --- | --- | --- | --- |
| Multi-line editor with readline keys (`Ctrl+A/E/K/U/W/Y`, `Alt+B/F/D`, `Ctrl+_` undo) | table stakes | the editor is independent of the transcript; echo never waits on a frame | |
| Submit on Enter; newline on `Ctrl+J` everywhere, `Shift+Enter` where the terminal reports it (kitty flag 1, Windows console records), `\` then Enter everywhere; remappable `chat:newline` and `chat:submit` | table stakes | plus `/terminal-setup` that writes the Shift+Enter binding for VS Code, Cursor, Alacritty, Zed and tmux `extended-keys` | |
| Large paste collapsed to `[paste #1, 120 lines]`, expandable with `Ctrl+O`, cached so a recalled prompt resends it; unbracketed paste bursts detected on Windows | table stakes | | |
| `@` fuzzy file picker over the workspace, honouring the file tools' own rules (no credential files, no `.git`, links not followed out) | table stakes | the picker calls the same `Workspace` listing the tools use, so it never shows a path the model may not read | |
| `/` palette with fuzzy matching, argument hints from the registry's usage strings, completion mid-prompt | table stakes | commands stay surface-neutral; the TUI adds argument pickers for `/model`, `/rollback`, `/skills`, `/mcp` | |
| `Ctrl+P` command palette: every command and UI action, runnable without editing the prompt | table stakes (plan §12's Ctrl+P) | Amp's lesson: a palette that is always reachable beats a slash menu alone | |
| `!` shell mode | table stakes, with a difference | the command runs through Corvid's approval pipeline, sandbox and checkpoints like any shell call, with its output added to the session as a tool result; `!!` runs it without sending output to the model | |
| `#` stages a memory | differentiator | goes to the gated write path, so without Kev it lands in `/memory pending` for review | |
| History with `Up`/`Down` at the edges and `Ctrl+R` reverse search, per workspace, duplicates collapsed | table stakes | | |
| `Ctrl+G` opens the prompt in `$EDITOR` | table stakes | the hand-off scope of §3.7 | |
| `Ctrl+S` stashes and restores a draft | nice to have | | |
| Vim editing mode (`/vim`, `editorMode`) | table stakes | phase T1 | |
| Queue while a turn runs: Enter queues (shown grey), `Up` takes it back, `Esc` returns queued text to the editor | table stakes | the session actor already keeps reading during a turn | |
| Steer now: `Ctrl+Enter` (and `Ctrl+X Ctrl+S` where Ctrl+Enter is a newline) injects the message at the next step checkpoint | differentiator | the fabric can read a steering message as part of `TurnState`, so the decider sees it before the generator does | K-3 |
| Image paste `Ctrl+V` (`Alt+V` on Windows and WSL) as an `[image #1]` chip | later | needs prompt image capability in the kernel | K-8 |
| Voice | later (plan D-7) | | |

### 4.2 Transcript

| Feature | Class | Design | Kernel |
| --- | --- | --- | --- |
| Streaming markdown: headings, lists, tables (held back until closed), fenced code with syntax colours, inline code, links as OSC 8 where supported | table stakes | Markdig on the accumulated block; a compact regex grammar set for common languages loaded lazily (TextMate grammars are too heavy for the single-file publish) | |
| Tool cards: icon by kind (read, edit, delete, search, execute, fetch, memory, mcp), title from `ToolPresentation`, state, elapsed time, outcome; collapsed by default; `Ctrl+O` toggles expanded input and output for recent cards | table stakes | reuse `ToolPresentation.Kind` and `Title` from the ACP surface | |
| Tool output windows: a bounded tail while running (K-2), the full result in an overlay or pager, three caps (screen, memory, model) | table stakes | the model cap already exists in the tools; the screen and memory caps are new | K-2 |
| Diff cards for `write_file` and `patch_file`: unified, word-level highlights, line numbers, from the write ledger, with a "reversible" marker when a checkpoint exists | table stakes | `ToolPresentation.LedgerDiffsAsync` already produces them | |
| Thought rows ("thought for 4 s"), expandable with `Ctrl+T` when the generator reports reasoning | table stakes | | K-1 |
| Decision chips: the fabric's checkpoint, question, confidence and action ("tool 0.91 → read_file", "risk 2 → ask"), toggled with `Ctrl+K`, always visible in `--verbose` | differentiator | nobody else can show why the agent did what it did with a calibrated number | |
| Escalation rows: "escalated to claude: step budget", "kept local: the privacy gate refused the cloud model", with the redaction count | differentiator | `ModelSwitched` and the turn result's escalations | |
| Taint badge in the status line and a row when taint starts ("outside content in context: web_fetch example.com") and clears | differentiator | | |
| Compaction markers with the summary's goal and decisions, expandable; "expanded on a larger window" when it happens | differentiator | `Compacted`, `CompactionExpanded` | |
| Memory rows: "memory staged: 2 facts (`/memory pending`)" | differentiator | | |
| Detection rows in red with the rule id and posture, and the approval they forced | differentiator | `DetectionMatched` | |
| MCP rows: "tool held: notes__search changed (`/mcp diff`)" | differentiator | `McpToolHeld`, `McpToolAccepted` | |
| Job notices: "job nightly-tidy ran (session job:…)" with a key to open that session's transcript | differentiator | scheduler events and `/cron` (§4.6) | |
| Question block for a clarifying turn, with the composer focused and the question kept visible | table stakes | | K-6 |
| Task list (`Ctrl+T` in fullscreen, a block in inline) from `TodosSet` | nice to have | | |
| Transcript view `Ctrl+O` in fullscreen: `less`-style keys, `/` search, `n`/`N`, `{`/`}` by prompt, `[` dumps to scrollback, `v` opens in `$EDITOR` | table stakes for fullscreen | | |
| `/copy` picker for the last answer, a code block, a tool result or a turn range; `w` writes to a file over SSH | table stakes | copies come from the session's source text, never from rendered cells, so no padding or hard wraps leak in (Claude Code #18170) | |
| `/export` to markdown, HTML and JSONL | table stakes | | |
| `/focus` quiet view: last prompt, one-line tool summary, final answer | nice to have | | |
| Mermaid as ASCII, LaTeX as Unicode | later | | |

### 4.3 Approvals and safety

The approval block is where Corvid can be plainly better than the field, because the pipeline already knows more
than other agents do about what a call will do.

- **What is shown.** The analysis text; each parsed command with its verb, subcommand, effects and targets; the
  reasons with their codes; whether a checkpoint makes the action reversible; whether the session is tainted and by
  what; the proposed diff for a file edit; the deadline as a countdown; for MCP tools, the server's trust level.
- **Options as scopes**, exactly the ones the pipeline offers: once, this session, always, and deny, plus "deny and
  say why", which sends the reason as the next user message. Options are numbered and chosen with a digit or arrows
  and Enter. `y` and `n` are not accepted as bare keys, which Claude Code removed after stray keystrokes answered
  prompts. Enter is never accepted for a prompt that appeared while the user was typing: the prompt opens below the
  draft, the first Enter focuses it, the second answers it. Input typed before the prompt appeared never answers it,
  which `ConsoleChatInput` already guarantees and the decoder keeps.
- **Always rules are visible.** Choosing "always" shows the rule that will be written, with its scope, and
  `/approvals` lists and removes rules (K-7). No silent broad prefixes.
- **Modes.** `Shift+Tab` cycles the pipeline's modes (manual, smart, permissive) for the session, with the mode in the
  status line and a confirmation row; `Alt+M` as the fallback on consoles without VT input.
- **Queued approvals** stack in order; the block shows "1 of 3".
- **Detections** that force an ask are shown with the rule and the two events that matched.
- **Nothing runs on a stale prompt.** A prompt cancelled by the turn, the deadline or a rewind is shown as "denied:
  deadline" and the block closes.

### 4.4 Status line and attention

| Element | Source |
| --- | --- |
| generator slot and model, device, queue depth, median latency; decider tier (Kev-0.8B, Kev-4B escalation, degraded) | `ModelMetrics`, slots, decider notices |
| context gauge: tokens in the rendered prompt over the accepted window, with the next compaction threshold | token counter, K-5 |
| cost for cloud slots, when usage is reported | K-5 |
| approval mode, taint, goal, pending approvals | session state |
| scheduler: running here, held by another process, or off; background jobs due | `JobScheduler` |
| git branch of the workspace, session id, elapsed time of the running turn | |
| model loading progress at start ("loading qwen3.5-0.8b 2 of 4") | K-4 |

The status line is one row by default, configurable with `/statusline` (which items, in what order, and an
external command fed JSON on stdin at most every 300 ms, Claude Code's contract). The terminal title shows the
state and the project; OSC 7501 program status and OSC 9;4 progress are emitted where supported. Desktop
notifications (OSC 9, 777 or 99 by terminal, `BEL` otherwise) fire on turn end, a waiting approval or a question,
only while the terminal is unfocused (focus events) unless configured always. A hook can run a command instead.

### 4.5 Sessions, rewind and models

- `/resume` picker: title, time since activity, workspace, branch, turn count, a preview; search by typing; all
  workspaces with `Ctrl+A`. Titles are generated by the local generator after the first exchange, so they cost
  nothing and leave the machine never (K-9).
- `/fork` creates a session from a log position (`ForkSessionAsync` exists); `/branch` from the current head.
- `Esc Esc` on an empty composer opens the rewind menu over the checkpoint ledger: each entry with its tool, paths
  and time; `d` previews the diff before restoring; options restore files and conversation, conversation only, or
  files only; conflicts and merges are listed afterwards exactly as `RestoreResult` reports them. `/undo` and
  `/rollback N` keep working as commands.
- `/model` picker: slots with their current model, device and queue; the escalation ladder drawn as a chain; pin
  and unpin; switching works mid-turn because the actor reads commands during a turn.
- `/status` and `/doctor` render inline; `/context` shows what the prompt holds (system prompt, memory, skills,
  recent results, stubs) with token counts, which the renderer already knows.

### 4.6 Jobs, skills, memory, MCP

Corvid has durable scheduled jobs, a reviewed memory tier, git-versioned skills and pinned MCP tools. Each gets a
view rather than a wall of text: `/cron` (C-035: list, pause, resume, remove) lists jobs with next run and last outcome and opens a run's session;
`/memory` shows pending writes as a review list with approve, correct and reject on each row; `/skills` lists with
authorship and drafts awaiting replay; `/mcp` shows servers, trust, held tools and `diff` as a diff card. These are
slash commands today with table output; the TUI renders their `CommandOutput` as navigable lists and adds the row
actions.

### 4.7 Keybindings

Defaults, with the reserved keys never bound (`Ctrl+C`, `Ctrl+D`, `Ctrl+M`, `Ctrl+[`, `Ctrl+I`, `Ctrl+H`), tmux and
screen conflicts documented (`Ctrl+B`, `Ctrl+A`), and Windows and WSL variants where terminals reserve keys:

| Key | Action |
| --- | --- |
| `Enter` / `Ctrl+J` / `Shift+Enter` | submit / newline / newline |
| `Esc` | interrupt the turn (work so far is kept; queued messages go next); close a dialog; decline a prompt |
| `Esc Esc` | with text: clear the draft to history; empty: open the rewind menu |
| `Ctrl+C` | interrupt; then clear the prompt; then exit on a second press within 800 ms |
| `Ctrl+D` | exit on a second press when the prompt is empty |
| `Ctrl+Enter` | steer now (`Ctrl+X Ctrl+S` where Ctrl+Enter is a newline) |
| `Shift+Tab` (`Alt+M`) | cycle approval modes |
| `Ctrl+O` | expand or collapse tool output; transcript view in fullscreen |
| `Ctrl+T` | thoughts (and the task list in fullscreen) |
| `Ctrl+K` | decision chips |
| `Ctrl+P` | command palette |
| `Ctrl+R` | history search |
| `Ctrl+G` | prompt in `$EDITOR` |
| `Ctrl+L` | repaint |
| `Ctrl+Z` | suspend (Unix) |
| `Alt+P` | switch the generator model without clearing the prompt |
| `PageUp`, `PageDown`, `Ctrl+Home`, `Ctrl+End`, `{`, `}` | fullscreen transcript navigation |
| `?` | shortcut help |

`~/.config/corvid/keybindings.json`: contexts (global, chat, composer, approval, picker, transcript, rewind),
chords with a 3 s timeout, `null` to unbind, validated at load with warnings in the log, hot-reloaded.

### 4.8 Themes and accessibility

- Themes use the terminal's 16-colour palette by default so user themes carry their own contrast; truecolor
  themes (dark, light, daltonized dark and light) are opt-in or chosen by `theme: auto` from the OSC 11 probe and
  `COLORFGBG`, re-checked on focus. Custom JSON themes in the config directory, hot-reloaded. `NO_COLOR`,
  `FORCE_COLOR`, `TERM=dumb` and a redirected stdout follow the usual rules.
- `prefersReducedMotion` stops spinners, shimmer and blink and turns the elapsed counter into a static value updated
  on state changes. It is also the behaviour when the terminal is unfocused.
- `--ax-screen-reader`, `CORVID_AX_SCREEN_READER=1` or the setting selects the linear renderer. Codex's one-time
  probe (AT-SPI on Linux, VoiceOver on macOS, `SPI_GETSCREENREADER` on Windows) turns reduced motion on and prints a
  hint about the flag; it never switches renderers silently.
- `CORVID_ACCESSIBILITY=1` keeps the hardware cursor on the caret for magnifiers; the composer never draws a fake
  inverse-video cursor, which is invisible on light themes.
- Diffs in linear mode are `+` and `-` lines; tables are `Header: value` lines.

### 4.9 Later

Multi-session and agent views (plan §14 subagents come first), remote control, images in the transcript (kitty
graphics with placeholders under tmux), a daemon that keeps models loaded between sessions with the TUI attaching to
it over the session update stream (OpenCode 2.0 and Codex `/daemon` did this; Corvid's model load time makes it
worth more here), voice.

## 5. Compatibility strategy

The matrix in the research notes gives support per terminal. The design treats terminals as two tiers decided by
the probe, with identity-based exceptions, and tests both tiers through the emulator plus a manual checklist on real
terminals before each release:

| Environment | Notes |
| --- | --- |
| Ghostty, kitty, WezTerm, foot, Alacritty 0.13+, iTerm2 | rich tier; kitty keyboard; images later |
| Windows Terminal 1.24+ | rich tier through ConPTY: 2026 answered, kitty keys from 1.25, console records for input, print-above insertion, full-repaint option in fullscreen; identity by `WT_SESSION` |
| legacy conhost, Windows 10 | conservative tier; VT output only; no fullscreen |
| VS Code, Cursor and other xterm.js hosts | 2026 from xterm.js 6 (VS Code 1.109); print-above insertion (region scroll deletes rows there); `/terminal-setup` turns off GPU acceleration when text garbles |
| JetBrains terminal | conservative tier; no kitty flags (they print as `<`); Ctrl+J for newlines |
| macOS Terminal.app | conservative tier; no OSC 52, no 2026 evidence, width model 6 cells for joined emoji: avoid them |
| tmux | probe normally (DECRQM answered from 3.6 and 3.7); never push kitty flags; honour `extended-keys` for Shift+Enter; wcwidth width model; OSC 52 only with `set-clipboard on`; `allow-passthrough` for notifications; fullscreen needs `mouse on` |
| GNU screen | conservative tier; no OSC 8 (corrupts on long URLs); no 2026 |
| zellij | rich tier from 0.41 (kitty keys, 2026); alternate screen avoided for scrollback (Codex's `auto`) |
| mosh, slow SSH | probe budget three seconds; conservative tier; predictive echo accepted |
| Git Bash and mintty | need ConPTY (MSYS2 since 2022) or winpty; documented in `corvid doctor` |
| WSL | the outer terminal's capabilities apply; Alt+V for image paste when it arrives |

`corvid doctor` grows a terminal section: the probe's results, the tier, the insertion strategy, the width model
and what to change (tmux options, iTerm2 clipboard permission, VS Code settings).

## 6. Performance engineering

- **Startup.** The TUI starts before the runtime: banner, status line and composer paint within the first 100 ms;
  model loading reports progress in the status line and the first message waits for it. The publish stays
  self-contained single-file with ReadyToRun and trimming on and compression off. Extracting the ONNX natives on
  first run costs about 20 ms once and needs `HOME` (or `DOTNET_BUNDLE_EXTRACT_BASE_DIR`), which `corvid doctor`
  checks.
- **Latency.** Input to echo is measured inside the app (timestamp at `read` return, timestamp after the frame's
  write) and reported by `/status`; the budget is 5 ms. Typometer is the external check.
- **Frames.** 16 ms cadence, request-driven, coalesced; synchronized output when supported; one write per frame; the
  diff compares style ids and strings, never escape text.
- **Idle.** No timers unless something animates; no writes when nothing changed; a test asserts zero bytes over
  five seconds of idle.
- **Memory.** Committed blocks keep their source and drop rendered lines beyond a window of the last 500 lines;
  tool output keeps a bounded ring on screen and in memory, with the full result on the event log; markdown and
  highlight caches are bounded LRUs; the style and string pools are recycled per hour, which Claude Code found
  necessary.
- **Engine contention.** ONNX intra-op threads below the core count and niced; the UI thread dedicated; GC in
  `SustainedLowLatency` during a turn.
- **Large outputs.** Three caps: screen (a window of 40 lines with "expand"), memory (a 50,000-line ring), model
  (the tools' own `MaxResultChars` with head and tail kept). A 24 MB single-line JSON result wedged Gemini CLI
  permanently; here it is cut at every layer.

## 7. Solidity

- **State recovery** as in §3.7, exercised by tests that kill the process from a PTY and assert the slave's termios,
  and by a `reset`-free manual check on each terminal in §5.
- **Crash fallback.** A renderer that throws is caught at the frame boundary; the frame is skipped and the error
  logged; after three consecutive failures the session switches to the inline renderer, and from inline to linear,
  with a notice. Fullscreen failures count across starts as in Claude Code.
- **Resize robustness.** Layout is a pure function of model, width and height; the size is re-read before every
  frame; every write is bounds-checked against the current size (Codex's "index outside of buffer" panic came from a
  cached size). A property test drives random size sequences including 1 column and heights below the composer.
- **Input robustness.** Bursts of keys, pastes split across reads, pastes without an end marker (a 2 s timeout
  closes an unterminated paste), query replies interleaved with keys, and replies that arrive late from Windows
  consoles are all fixture tests.
- **Sanitization** as in §3.7, with fixtures from the published attacks (OSC 52 writes, OSC 8 spoofing, hidden text,
  `ESC [ ? 9001 h`).
- **Stderr hygiene.** Nothing in the process writes to stderr while the TUI runs; diagnostics go to the log file and
  a `/log` command shows the tail. A guard redirects stray writes.
- **Signals.** SIGINT and SIGTERM interrupt the turn, then exit with restore; a second SIGINT within a second skips
  cleanup and says so. SIGHUP restores and exits. Nothing in the UI thread blocks for longer than a frame, so a wedged
  engine never leaves the terminal in raw mode.

## 8. Testing

Three layers, run on the Arch self-hosted runner, where `/dev/ptmx` needs no root and no step has a TTY unless a
test allocates one.

1. **Components and layout.** Snapshot tests of `Render(width)` at 40, 80, 120 and 200 columns for every block
   kind and state, including CJK, joined emoji and RTL fixtures, with the width model switched both ways. Markdown
   streaming tests that feed token-sized chunks and assert the committed and tail regions at each step.
2. **The real byte stream through an emulator.** The renderer writes to a buffer that is fed to XTerm.NET (headless,
   MIT, .NET 10), and tests assert the screen grid and scrollback: committed rows reach scrollback and the live region
   is intact under both insertion strategies; a resize repaints only the live region; no frame contains `CSI 3J` or
   `CSI 2J`; frames are wrapped in 2026 exactly when the capability says so; the restore routine leaves the
   emulator's modes at their defaults; the full-clear counter is zero across a recorded day-long session replayed at
   speed. Input: the decoder is driven by byte fixtures split at every boundary (kitty CSI-u press, repeat and release
   with modifiers and lock states, legacy keys, bracketed paste, SGR mouse, focus, every reply type, lone Escape).
3. **Process-level smoke.** Porta.Pty spawns the published binary on a PTY, types a session, resizes, suspends and
   resumes, kills it, and asserts termios and the final screen; skipped when a PTY cannot be opened. The
   language-agnostic `tui-test` CLI (Microsoft, 0.1.0 as of October 2026, backends Alacritty, Ghostty, Rio, xterm.js)
   is evaluated for the terminal matrix once it stabilizes.

Guards that run in CI: idle produces zero bytes over five seconds; the "no continuous render" test; a startup
budget test; sanitization fixtures. A manual checklist covers the real terminals in §5 before a release, recorded as
asciicast files that the emulator replays in CI afterwards.

The existing `Corvid.Cli.Tests` scripted sessions keep working through the linear renderer, which is a plain writer
over the same session update stream; the fake models and scripted input are unchanged.

## 9. Kernel and host work the TUI needs

| Id | Change | Needed by |
| --- | --- | --- |
| K-1 | forward `TextReasoningContent` deltas through the feed as thought chunks (the privacy gate already handles them) | thought rows |
| K-2 | stream tool output: a bounded tail of `shell` and `web_fetch` output while the call runs, reported as `tool_call_update` content | running tool cards |
| K-3 | a steering envelope the actor delivers at the next step checkpoint, visible to the fabric in `TurnState` | steer now |
| K-4 | model load progress events from the composer and the runtime factory, so the UI starts before the models | first paint |
| K-5 | persist and report token usage per call and per session (the instrumented client already sees `UsageDetails`); a prompt token count from the renderer | context gauge, cost |
| K-6 | a structured question for `Clarifying` turns, carried as an update rather than inferred from the status | question block |
| K-7 | rule removal in `IApprovalRuleStore` and listing with scopes | `/approvals` |
| K-8 | image content in envelopes and the generator contract (ACP `promptCapabilities.image`) | image paste, later |
| K-9 | a session title and summary written as an event after the first exchange, generated by the local generator; `session/list` carries it | resume picker |
| K-10 | `SessionUpdates` in `Corvid.Host`, extracted from `Corvid.Acp.TurnReporter`, consumed by both surfaces | everything |

## 10. Phasing

**T0, foundation (replaces `ChatRenderer`).** `Corvid.Terminal` with raw mode, console modes, the probe, the
decoder, width, the writer and restore; `Corvid.Tui` with the inline and linear renderers, the composer, transcript
blocks (user, assistant, tool, diff, approval, notice, question, decision chips, escalation, taint, compaction),
the status line, the `/` palette, `Ctrl+P`, the `@` picker, the resume picker, the rewind menu, queueing, `Ctrl+O`,
`Ctrl+K`, `!` through the pipeline, `#`, history, `Ctrl+G`, themes on the 16-colour palette, reduced motion,
`--ax-screen-reader`, `/copy`, `/export`, `corvid doctor`'s terminal section, and the three test layers. K-1, K-2,
K-4, K-6, K-7, K-10.

**T1, fullscreen and polish.** The fullscreen renderer with mouse, selection, clipboard paths, search, scrollbars
and the `[` dump; `/tui` and the setting; vim mode; `keybindings.json`; `/statusline` with the external command;
notifications and progress; `/terminal-setup`; auto titles; steer now; the context gauge and cost; `/cron`,
`/memory`, `/skills` and `/mcp` as navigable lists. K-3, K-5, K-9.

**T2, reach.** The daemon and attach; images; multi-session view once subagents exist; remote control.

Each phase ends with the terminal matrix checklist and a recorded session replayed in CI.

## 11. Open decisions

| Id | Decision | Proposal |
| --- | --- | --- |
| T-1 | Default renderer: inline or fullscreen | inline, for terminal citizenship and accessibility; revisit when T1 ships with the full-clear counter at zero across the matrix and user feedback in hand |
| T-2 | Markdown parser | Markdig (MIT, fast, streaming-tolerant per block) in `Corvid.Tui`, with a checker allowlist entry for the surface project as `Corvid.Mcp` has |
| T-3 | Syntax highlighting | a compact regex grammar set covering about twenty languages, lazily loaded; TextMate grammars rejected for size |
| T-4 | Keep Spectre.Console | yes for the non-chat verbs' tables for now; the chat stops using it; remove later if the TUI's table component suffices |
| T-5 | Test emulator | XTerm.NET 3.0 (MIT, net10.0, released 2026-10-14; small community, no conformance results): adopt for layer 2, with a vttest-style fixture of our own to catch its gaps; keep the option of vendoring |
| T-6 | Where the TUI's settings live | the `Corvid:Tui` section of `corvid.json` plus `keybindings.json` and themes beside it; `/tui`, `/theme`, `/keybindings` write them |
| T-7 | Whether `!` output goes to the model by default | yes, as other agents do, with `!!` for local-only; both go through the approval pipeline |

## 12. Sources

The research notes in [`docs/research/tui/`](../research/tui/README.md) carry every citation. The sources this
design leans on most: the Claude Code fullscreen, interactive-mode, keybindings, terminal-configuration and
accessibility docs and its changelog; the Codex `codex-rs/tui` sources (`insert_history.rs`, `transcript_reflow.rs`,
`terminal_probe.rs`, `keyboard_modes.rs`, `streaming/controller.rs`, `tui.rs`), discussion #49129 and the issues
cited in the notes; the pi-tui README and changelogs; the OpenTUI renderer docs and the OpenCode split-footer
issues; Gemini CLI's rendering epic, its reverts and its flicker detector; the Hermes TUI issues; the kitty keyboard
and graphics protocol specifications; the synchronized output specification; xterm's control sequences; Microsoft's
console virtual terminal sequences and Windows Terminal release notes; the tmux changes file; the dotnet/runtime
System.Console sources; the ratatui, crossterm and Bubble Tea documentation on restoration; the Trail of Bits and
dgl.cx terminal escape injection write-ups; and the three 2026 developer surveys.
