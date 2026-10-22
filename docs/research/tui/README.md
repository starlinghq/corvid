# Research notes behind the TUI design

Eight notes gathered on 2026-10-21 for [`docs/plan/tui.md`](../../plan/tui.md). Each note follows the same shape per
question: a takeaway, cited findings with a URL per claim, inferences, and the gaps the researcher could not close.
Issue states, reaction counts and version numbers are as read on that date. Reddit was unreachable, so sentiment
comes from Hacker News, GitHub and the three 2026 developer surveys.

| Note | Covers |
| --- | --- |
| [`claude_code_tui.md`](claude_code_tui.md) | Claude Code: the three renderer eras (Ink inline, the cell-diff renderer, the alternate-screen fullscreen mode), the open and fixed failures with issue numbers, the full keybinding and feature set, and attributed sentiment |
| [`codex_cli_tui.md`](codex_cli_tui.md) | Codex CLI: the Rust rewrite and its reasons, the inline scroll-region renderer and the owned fullscreen transcript, probing and keyboard modes, failures and features, and the September 2026 default switch |
| [`ink_based_clis.md`](ink_based_clis.md) | How Ink renders and fails at scale; Gemini CLI's three rendering generations and two reverted defaults; Qwen Code; Copilot CLI's path to a cell renderer; Cline on OpenTUI; the Hermes Agent TUI and its defects |
| [`opencode_crush_pi_and_others.md`](opencode_crush_pi_and_others.md) | The scrollback-first versus alternate-screen state of October 2026; pi-tui's design and its fullscreen default; OpenCode and OpenTUI's three screen modes; Crush and Bubble Tea v2; Amp, Goose, Aider, Cursor CLI, Droid and Warp |
| [`terminal_protocols_and_compat.md`](terminal_protocols_and_compat.md) | Escape sequences and their detection (synchronized output, the kitty keyboard protocol, bracketed paste, mouse, focus, OSC 52, 8, 9, 777, 99, 7, 133, 9;4, mode 2027, images, DA1 and XTVERSION), a support matrix per terminal and multiplexer, Windows and ConPTY, resize and reflow, SSH and mosh, string width |
| [`dotnet_tui_implementation.md`](dotnet_tui_implementation.md) | .NET TUI libraries and their limits, System.Console traps on Unix and Windows from the runtime sources, measured startup and latency engineering for a single-file app, the ACP alternative and existing terminal ACP clients, ratatui and OpenTUI as native options |
| [`feature_inventory_and_sentiment.md`](feature_inventory_and_sentiment.md) | A feature matrix across thirteen products, input and output conventions, permission and mode patterns, survey data and attributed sentiment, what arrived in 2026, and the table-stakes versus differentiator split |
| [`testing_accessibility_recovery.md`](testing_accessibility_recovery.md) | How the leading TUIs test, headless emulators and PTY harnesses usable from .NET, screen-reader requirements and the Claude Code screen-reader mode as a spec, terminal-state recovery on exit, crash, suspend and hand-off, long sessions, large outputs and escape-sequence injection |

Working files the notes refer to (raw copies of READMEs, benchmark scripts and logs) stayed in the session that
produced them and are marked "not included".
