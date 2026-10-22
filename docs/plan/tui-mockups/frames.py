"""Mockup screens of the Corvid TUI design (docs/plan/tui.md), written as the bytes each renderer would emit.

Each screen is a list of styled lines. The script checks the design's invariants, writes one .ans file per screen,
and a manifest the renderer reads. Styles use the 16-colour palette plus two 256-colour diff backgrounds.
"""
import json
import os
import re
import sys

ESC = "\x1b"
RESET = ESC + "[0m"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "frames")

# ---- styles (SGR parameter strings) ----
S = {
    "": "",
    "b": "1", "dim": "2", "it": "3", "ul": "4", "inv": "7",
    "red": "31", "green": "32", "yellow": "33", "blue": "34", "magenta": "35", "cyan": "36", "white": "37",
    "grey": "90", "bred": "91", "bgreen": "92", "byellow": "93", "bblue": "94", "bmagenta": "95", "bcyan": "96",
    "bwhite": "97",
    "addbg": "48;5;22", "delbg": "48;5;52", "addword": "48;5;28", "delword": "48;5;88",
    "selbg": "48;5;237", "chip": "48;5;236", "barbg": "48;5;235",
}


def sgr(style):
    parts = [S[p] for p in style.split() if S[p]]
    return (ESC + "[" + ";".join(parts) + "m") if parts else ""


def seg(text, style=""):
    return (text, style)


class Line:
    """A styled line: runs of (text, style). Width is the character count; every glyph used here is one cell."""

    def __init__(self, *runs):
        self.runs = [r if isinstance(r, tuple) else (r, "") for r in runs]

    @property
    def width(self):
        return sum(len(t) for t, _ in self.runs)

    def pad(self, width, style=""):
        if self.width < width:
            self.runs.append((" " * (width - self.width), style))
        return self

    def ansi(self):
        out = []
        for text, style in self.runs:
            out.append(sgr(style) + text + (RESET if style else ""))
        return "".join(out) + RESET


def L(*runs):
    return Line(*runs)


def blank():
    return Line("")


def rule(width, label="", style="grey", right=""):
    if label:
        head = "─ " + label + " "
    else:
        head = ""
    tail = (" " + right + " ─") if right else ""
    fill = width - len(head) - len(tail)
    return Line(seg(head, style), seg("─" * fill, style), seg(tail, style))


def box(width, title, body_lines, style="yellow", right=""):
    """A bordered block. body_lines are Lines narrower than width - 4."""
    inner = width - 4
    top_label = "╭─ " + title + " "
    top_right = (" " + right + " ─╮") if right else "─╮"
    lines = [Line(seg(top_label, style + " b"), seg("─" * (width - len(top_label) - len(top_right)), style),
                  seg(top_right, style))]
    for bl in body_lines:
        if bl.width > inner:
            raise ValueError(f"box line too wide: {bl.width} > {inner}: {''.join(t for t, _ in bl.runs)!r}")
        lines.append(Line(seg("│ ", style), *bl.runs, seg(" " * (inner - bl.width)), seg(" │", style)))
    lines.append(Line(seg("╰" + "─" * (width - 2) + "╯", style)))
    return lines


# ---- shared pieces ----

def shell_prompt(cmd="corvid"):
    return L(seg("mark@omarchy", "bgreen b"), seg(":", "grey"), seg("~/src/corvid", "bblue b"), seg(" $ ", "grey"),
             seg(cmd))


def banner(width, model="qwen3.5-0.8b", tools=19, session="cli-7f3a2c", resumed=None):
    lines = [rule(width, "corvid", "grey", right=f"session {session}")]
    lines.append(L(seg("  generator ", "grey"), seg(model), seg(" · decider ", "grey"), seg("kev-0.8b"),
                   seg(f" · {tools} tools · ", "grey"), seg("smart", "cyan"), seg(" approvals · ", "grey"),
                   seg("/help", "bcyan"), seg(" · ", "grey"), seg("ctrl+p", "bcyan"), seg(" palette", "grey")))
    if resumed:
        lines.append(L(seg("  " + resumed, "grey")))
    return lines


def user(text):
    return L(seg("› ", "bblue b"), seg(text, "b"))


def say(text, style=""):
    return L(seg("  "), seg(text, style))


def card(icon, icon_style, name, target, outcome, outcome_style, extra="", width=100):
    left = [seg("  "), seg(icon, icon_style), seg(" "), seg(name, "b"), seg("  "), seg(target)]
    right = [seg(outcome, outcome_style)]
    if extra:
        right = [seg(extra + "  ", "grey")] + right
    lw = sum(len(t) for t, _ in left)
    rw = sum(len(t) for t, _ in right)
    gap = max(2, width - 2 - lw - rw)
    return L(*left, seg(" " * gap), *right)


def status(width, items, right):
    runs = [seg(" ")]
    for i, (text, style) in enumerate(items):
        if i:
            runs.append(seg(" │ ", "grey"))
        runs.append(seg(text, style))
    lw = sum(len(t) for t, _ in runs)
    rw = sum(len(t) for t, _ in right)
    runs.append(seg(" " * max(1, width - lw - rw - 1)))
    runs.extend(right)
    runs.append(seg(" "))
    return Line(*runs).pad(width)


def gauge(used, total, cells=8):
    filled = round(cells * used / total)
    return "▮" * filled + "▯" * (cells - filled)


def composer(width, text="", hint="", placeholder="Message Corvid · @ files · ! shell · / commands"):
    lines = [Line(seg("─" * width, "grey"))]
    if text:
        lines.append(L(seg("› ", "bblue b"), seg(text), seg(" ", "inv")))
    else:
        lines.append(L(seg("› ", "bblue b"), seg(" ", "inv"), seg(placeholder, "grey")))
    if hint:
        lines.append(L(seg("  " + hint, "grey")))
    return lines


def default_status(width, ctx=(5100, 8192), mode="smart", taint=False, extra_right=None, elapsed="0:42"):
    items = [(f"qwen3.5-0.8b@cpu", ""), ("p50 410 ms", "grey"), ("kev-0.8b", "grey"),
             (f"ctx {ctx[0] / 1000:.1f}k/{ctx[1] / 1000:.1f}k {gauge(*ctx)}", "")]
    items.append((mode, "cyan"))
    if taint:
        items.append(("⚠ tainted", "byellow"))
    right = extra_right or [seg(" main", "magenta"), seg(f"  {elapsed}", "grey")]
    for drop in ("p50 410 ms", "kev-0.8b"):
        line = status(width, items, right)
        if line.width <= width:
            return line
        items = [it for it in items if it[0] != drop]
    return status(width, items, right)


# ---- screens ----

def screen_streaming(W, H):
    """Inline renderer mid-turn: committed history in native scrollback, live region at the bottom."""
    scroll = [shell_prompt(), *banner(W), blank(),
              user("why does /rollback refuse to restore src/Corvid.Cli/Program.cs?"), blank(),
              card("◇", "grey", "decide", "ingress · kev-0.8b", "intent explain 0.94 · tool search_files 0.88 → acted",
                   "grey", width=W),
              card("⌕", "cyan", "search_files", '"Program.cs" in src/', "✓ 3 matches", "green", "18 ms", width=W),
              card("▤", "blue", "read_file", "src/Corvid.Kernel/Checkpoints/CheckpointService.cs:40-120", "✓ 81 lines",
                   "green", "6 ms", width=W),
              blank(),
              say("A restore refuses a path when the file no longer holds what Corvid wrote. The ledger compares"),
              say("the current bytes with the after-checkpoint; if you edited the file since, the revert becomes a"),
              say("three-way merge, and a conflicting hunk stops the restore so your edit is never overwritten."),
              blank(),
              say("For Program.cs the after-checkpoint is c-17 and the file changed at 14:26, after the turn ended."),
              ]
    live = [
        say("List the checkpoints and the paths each one touched with:"),
        blank(),
        L(seg("  ┌", "grey")),
        L(seg("  │ ", "grey"), seg("/rollback", "bcyan")),
        L(seg("  └", "grey")),
        L(seg("  ⠹ ", "yellow"), seg("writing", "grey"), seg(" · 2.1 s · 214 tokens · ", "grey"), seg("esc", "bcyan"),
          seg(" interrupt · ", "grey"), seg("enter", "bcyan"), seg(" queue a message", "grey")),
        *composer(W, "and how do I keep my edit and take the rest"),
        default_status(W),
    ]
    return scroll, live


def screen_approval(W, H):
    scroll = [*banner(W), blank(), user("clean the build output and rerun the tests"), blank(),
              card("◇", "grey", "decide", "pre-execute · kev-0.8b", "risk 2 (0.81) → ask", "grey", width=W),
              ]
    body = [
        L(seg("shell ", "grey"), seg("rm -rf build/ && dotnet test Corvid.slnx", "b")),
        blank(),
        L(seg("  rm -rf      ", "bred"), seg("delete    ", "red"), seg("→ build/      ", ""),
          seg("inside the workspace", "grey")),
        L(seg("  dotnet test ", "cyan"), seg("process   ", "cyan"), seg("→ workspace   ", ""),
          seg("sandboxed, no network", "grey")),
        blank(),
        L(seg("  • ", "yellow"), seg("deletes a directory tree "), seg("(destructive)", "grey")),
        L(seg("  • ", "yellow"), seg("session holds outside content from web_fetch learn.microsoft.com "),
          seg("(tainted)", "grey")),
        L(seg("  ↺ ", "green"), seg("reversible: checkpoint c-23 is taken before the call", "green")),
        blank(),
        L(seg(" 1 ", "inv b"), seg(" Allow once  ", "b"), seg(" 2 ", "chip"), seg(" This session  "),
          seg(" 3 ", "chip"), seg(" Always ", ""), seg("rm -rf build/", "grey"), seg("  "),
          seg(" 4 ", "chip"), seg(" Deny  "), seg(" 5 ", "chip"), seg(" Deny and say why")),
        L(seg("  ↑↓ or 1-5 choose · enter confirm · esc deny", "grey")),
    ]
    live = [*box(W, "approval needed", body, "byellow", right="denied at 14:32:10 · 1:48 left"),
            *composer(W, "", placeholder="Your draft stays here; the prompt takes the next Enter only after it opened"),
            default_status(W, mode="smart", taint=True)]
    return scroll, live


def diff_line(no_old, no_new, sign, runs):
    base = {"+": "addbg", "-": "delbg", " ": ""}[sign]
    num = f"{no_old if no_old else '':>4} {no_new if no_new else '':>4} "
    head = [seg("  "), seg(num, "grey"), seg(sign + " ", {"+": "bgreen", "-": "bred", " ": "grey"}[sign] + (" " + base if base else ""))]
    body = []
    for text, style in runs:
        st = style or base
        if style in ("addword", "delword"):
            st = style
        body.append(seg(text, st))
    return head, body, base


def screen_diff(W, H):
    scroll = [*banner(W), blank(), user("make the default turn limit 12 steps and note it in the README"), blank()]
    rows = [
        (24, 24, " ", [("", "")]),
        (25, 25, " ", [("    /// <summary>Generator responses with tool calls a turn may take before it must…", "")]),
        (26, None, "-", [("    public int MaxSteps { get; init; } = ", ""), ("8", "delword"), (";", "")]),
        (None, 26, "+", [("    public int MaxSteps { get; init; } = ", ""), ("12", "addword"), (";", "")]),
        (27, 27, " ", [("", "")]),
        (28, 28, " ", [("    /// <summary>The completion the generator may write; the prompt budget leave…", "")]),
    ]
    live_diff = [card("✎", "byellow", "patch_file", "src/Corvid.Kernel/Turns/TurnTypes.cs", "✓ +1 −1", "green",
                      "↺ c-31", width=W)]
    for no_old, no_new, sign, runs in rows:
        head, body, base = diff_line(no_old, no_new, sign, runs)
        ln = Line(*head, *body)
        ln.pad(W - 2, base)
        live_diff.append(ln)
    scroll += live_diff
    scroll += [blank(),
               card("✎", "byellow", "patch_file", "README.md", "✓ +2 −1", "green", "↺ c-32", width=W),
               L(seg("  ", ""), seg("   ⋯ 9 lines · ", "grey"), seg("ctrl+o", "bcyan"), seg(" expand", "grey")),
               blank(),
               say("Done. MaxSteps is 12 in TurnOptions, and the README's configuration paragraph says so."),
               say("Both edits are checkpointed (c-31, c-32); "),
               L(seg("  "), seg("/undo", "bcyan"), seg(" reverts them and takes this turn out of the conversation.")),
               L(seg("  · answered · 2 steps, 2 tool calls · 6.4 s", "grey")),
               ]
    live = [*composer(W), default_status(W, ctx=(3400, 8192), elapsed="0:07")]
    return scroll, live


def screen_fabric(W, H):
    """What only Corvid can show: decisions, escalation, taint, memory and job notices."""
    scroll = [*banner(W), blank(), user("which terminals handle synchronized output now? check tmux"), blank(),
              L(seg("  ◇ ingress ", "grey"), seg("kev-0.8b", "grey"), seg("   ", ""),
                seg(" intent research 0.92 ", "chip"), seg(" "), seg(" difficulty 2 0.71 ", "chip"), seg(" "),
                seg(" clarify no 0.95 ", "chip")),
              L(seg("                       "), seg(" needs_recall no 0.83 ", "chip"), seg(" "),
                seg(" tool web_fetch 0.89 → acted ", "chip")),
              card("⇣", "magenta", "web_fetch", "https://github.com/tmux/tmux/blob/master/CHANGES",
                   "✓ 41.6 KB", "green", "640 ms", width=W),
              L(seg("  ⚠ ", "byellow"), seg("outside content from github.com", "yellow"),
                seg(" · network and side-effect calls ask until you reply", "grey")),
              L(seg("  ◇ step ", "grey"), seg("kev-0.8b", "grey"), seg("      ", ""),
                seg(" stuck 0.12 ", "chip"), seg(" "), seg(" difficulty 4 0.66 → escalate ", "chip")),
              L(seg("  ↗ ", "bmagenta"), seg("escalated to ", "magenta"), seg("claude-opus-5-5", "bmagenta b"),
                seg(": difficulty 4 above the local generator's ceiling", "magenta")),
              L(seg("    privacy gate: ", "grey"), seg("2 values redacted", "cyan"),
                seg(" (an email address, a home path) before sending", "grey")),
              blank(),
              say("tmux 3.7 (June 2026) is the first release that honours an application's mode 2026:"),
              say("· it holds pane output until the frame ends, or one second passes, and answers DECRQM ?2026"),
              say("· a redraw or a resize cancels the open frame instead of blocking it"),
              say("Before 3.7 tmux used synchronized updates only towards the outer terminal, so inner apps flicker."),
              blank(),
              L(seg("  ✦ memory staged: ", "cyan"), seg("1 fact", ""),
                seg(" (tmux honours mode 2026 from 3.7) · ", "grey"), seg("/memory pending", "bcyan")),
              L(seg("  · answered · 3 steps, 1 tool call · escalated to claude-opus-5-5 · tainted · 11.8 s", "grey")),
              blank(),
              L(seg("  ◷ ", "blue"), seg("job ", "grey"), seg("nightly-tidy", "b"), seg(" ran at 02:00 · ", "grey"),
                seg("ran", "green"), seg(" · session job:nightly-tidy:41 · ", "grey"), seg("/cron", "bcyan"),
                seg(" to open it", "grey")),
              ]
    live = [*composer(W), default_status(W, ctx=(6900, 8192), taint=True,
                                         extra_right=[seg(" claude-opus-5-5 ↗", "magenta"), seg("  main", "magenta"),
                                                      seg("  0:12", "grey")])]
    return scroll, live


def screen_rewind(W, H):
    scroll = [*banner(W), blank(), user("rename TurnOptions.MaxSteps to StepBudget everywhere"), blank(),
              card("✎", "byellow", "patch_file", "src/Corvid.Kernel/Turns/TurnTypes.cs", "✓ +1 −1", "green", "↺ c-41",
                   width=W),
              card("✎", "byellow", "patch_file", "src/Corvid.Kernel/Turns/TurnRunner.cs", "✓ +6 −6", "green", "↺ c-42",
                   width=W),
              card("$", "red", "shell", "dotnet build Corvid.slnx", "✗ exit 1 · 3 errors", "red", "11.2 s", width=W),
              blank(),
              say("The build fails in the gateway: GatewayService still reads options.MaxSteps."),
              ]
    sel = "selbg"
    rows = [
        (True, "c-43", "14:41", "shell", "dotnet build Corvid.slnx", "no file changes"),
        (False, "c-42", "14:40", "patch_file", "src/Corvid.Kernel/Turns/TurnRunner.cs", "+6 −6"),
        (False, "c-41", "14:40", "patch_file", "src/Corvid.Kernel/Turns/TurnTypes.cs", "+1 −1"),
        (False, "c-37", "14:22", "write_file", "docs/plan/tui.md", "new file · 612 lines"),
        (False, "c-31", "13:58", "patch_file", "README.md", "+2 −1 · edited by you since"),
    ]
    body = [L(seg("Go back to before:", "grey"))]
    for picked, cid, t, tool_name, path, note in rows:
        style = sel if picked else ""
        ln = Line(seg(" › " if picked else "   ", ("bcyan b " + sel) if picked else ""),
                  seg(f"{cid}  {t}  ", ("grey " + style).strip()), seg(f"{tool_name:<11}", ("b " + style).strip()),
                  seg(f"{path:<40}", style), seg(f"{note:<28}",
                                                 ("yellow " + style).strip() if "you" in note else ("grey " + style).strip()))
        ln.pad(W - 4, style)
        body.append(ln)
    body += [blank(),
             L(seg("Restore  ", "grey"), seg(" files and conversation ", "inv b"), seg("  conversation only  ", ""),
               seg("  files only  ", ""), seg("  summarise from here ", "grey")),
             L(seg("README.md changed after c-31: its revert will merge three ways and stop on a conflict.", "yellow")),
             L(seg("↑↓ pick · tab option · d diff · enter restore · esc close", "grey"))]
    live = [*box(W, "rewind", body, "bcyan", right="5 of 9 checkpoints"), default_status(W, ctx=(4200, 8192))]
    return scroll, live


def screen_resume(W, H):
    rows = [
        ("Rollback refuses Program.cs", "2 min", "~/src/corvid", "main", "14 turns", True),
        ("TUI design research and plan", "1 h", "~/src/corvid", "claude/adoring…", "41 turns", False),
        ("Gateway pairing for Signal", "yesterday", "~/src/corvid", "signal-adapter", "22 turns", False),
        ("Nightly tidy job (scheduled)", "02:00", "~/notes", "—", "job · 3 turns", False),
        ("Kev latency spike, S1 numbers", "Tue", "~/src/corvid", "spike/s1", "9 turns", False),
        ("Draft reply to the Vault review", "Mon", "~/src/corvid", "main", "6 turns", False),
    ]
    lines = [shell_prompt("corvid chat --resume"), blank(),
             rule(W, "resume a session", "bcyan", right="6 of 48 · this workspace"),
             L(seg(" ⌕ ", "grey"), seg("roll", "b"), seg(" ", "inv"), seg("   type to search titles and prompts", "grey"))]
    lines.append(blank())
    for title, when, ws, br, turns, picked in rows:
        st = "selbg" if picked else ""
        ln = Line(seg(" › " if picked else "   ", ("bcyan b " + st).strip()),
                  seg(f"{title:<36}", ("b " + st).strip() if picked else st), seg(f"{when:>10}  ", ("grey " + st).strip()),
                  seg(f"{ws:<15}", ("grey " + st).strip()), seg(f"{br:<17}", ("magenta " + st).strip()),
                  seg(f"{turns:>14}", ("grey " + st).strip()))
        ln.pad(W, st)
        lines.append(ln)
    lines += [blank(),
              L(seg("   preview ", "grey"), seg("› why does /rollback refuse to restore src/Corvid.Cli/Program.cs?")),
              L(seg("           ", ""), seg("A restore refuses a path when the file no longer holds what Corvid wrote…",
                                            "grey")),
              blank(),
              L(seg(" ↑↓ move · enter resume · space preview · ctrl+a all workspaces · ctrl+r rename · esc cancel",
                    "grey"))]
    return lines, []


def screen_linear(W, H):
    """--ax-screen-reader: append-only labelled lines, no boxes, no spinners, cursor on the caret."""
    lines = [shell_prompt("corvid chat --ax-screen-reader"),
             L(seg("[Screen reader mode: on via flag]")),
             L(seg("corvid: session cli-7f3a2c, generator qwen3.5-0.8b, decider kev-0.8b, approvals smart.")),
             L(seg("you: clean the build output and rerun the tests")),
             L(seg("decision: risk level 2, confidence 0.81, asking.")),
             L(seg("approval: shell wants to run: rm -rf build/ && dotnet test Corvid.slnx")),
             L(seg("approval: rm -rf deletes build/ inside the workspace. dotnet test runs a process, sandboxed.")),
             L(seg("approval: reasons: deletes a directory tree. The session holds outside content.")),
             L(seg("approval: reversible, checkpoint c-23 is taken first. Denied automatically at 14:32:10.")),
             L(seg("approval: 1 allow once, 2 this session, 3 always for rm -rf build/, 4 deny, 5 deny and say why.")),
             L(seg("Select with numbers: 1")),
             L(seg("approval: allowed once.")),
             L(seg("tool: shell started.")),
             L(seg("tool: shell finished, exit 0, 128 tests passed, 41 seconds.")),
             L(seg("corvid: The build directory is gone and all 128 tests pass. The deletion is checkpointed as")),
             L(seg("corvid: c-23, so /undo puts build/ back if you need it.")),
             L(seg("status: answered, 2 steps, 1 tool call.")),
             L(seg("you: ")),
             ]
    lines[-1].runs.append((" ", "inv"))
    return lines, []


def screen_fullscreen(W, H):
    """Opt-in fullscreen renderer: alternate screen, virtualized transcript, scrollbar, pinned composer."""
    body = [
        user("walk me through how the gateway retries a delivery"),
        blank(),
        card("⌕", "cyan", "search_files", '"DeliveryQueued" in src/', "✓ 7 matches", "green", "21 ms", width=W - 2),
        card("▤", "blue", "read_file", "src/Corvid.Gateway/DeliveryLedger.cs", "✓ 214 lines", "green", "4 ms",
             width=W - 2),
        blank(),
        say("Every answer goes through the ledger before it is sent:"),
        blank(),
        say("1. DeliveryQueued is appended to the gateway's own session in the event log, from any process."),
        say("2. The adapter's sender drains its chat in order and appends DeliveryResolved with the outcome."),
        say("3. A failure that may pass is retried with a backoff and stays unresolved meanwhile."),
        say("4. A permanent refusal from the platform is given up and resolved as such."),
        say("5. After a restart, whatever was never resolved is sent again, from a low-water mark."),
        blank(),
        say("So delivery is at least once: a crash between a send and its DeliveryResolved event"),
        say("repeats that one message on the next start. Jobs created in a chat report there through"),
        say("the same ledger, whichever process ran them."),
        L(seg("  · answered · 2 steps, 2 tool calls · 5.2 s", "grey")),
        blank(),
        user("and what happens when the owner is offline for a day?"),
    ]
    top = [L(seg(" corvid ", "inv b"), seg("  cli-7f3a2c · ~/src/corvid · main", "grey")).pad(W, ""),]
    view_h = H - 5
    view = body[:view_h]
    while len(view) < view_h:
        view.append(blank())
    # scrollbar in the last column: thumb near the middle (user scrolled up)
    thumb_start, thumb_len = 7, 4
    lines = top[:]
    for i, ln in enumerate(view):
        ln.pad(W - 1)
        bar = "┃" if thumb_start <= i < thumb_start + thumb_len else "│"
        ln.runs.append((bar, "bcyan" if bar == "┃" else "grey"))
        lines.append(ln)
    pill_text = " ↓ jump to latest · 3 new "
    pill = Line(seg(" " * (W - len(pill_text) - 3)), seg(pill_text, "inv bcyan b"), seg("   "))
    lines.append(pill)
    lines += composer(W, "", placeholder="Message Corvid · wheel or PgUp/PgDn scroll · shift+drag selects natively")
    lines.append(default_status(W, ctx=(5800, 8192),
                                extra_right=[seg(" fullscreen", "cyan"), seg("  main", "magenta"), seg("  1:03", "grey")]))
    return lines, []


def screen_narrow(W, H):
    """The streaming screen at 60 columns: same components, re-wrapped for the width."""
    scroll = [shell_prompt(), rule(W, "corvid", "grey", right="cli-7f3a2c"),
              L(seg("  qwen3.5-0.8b · kev-0.8b · smart", "grey")), blank(),
              user("why does /rollback refuse Program.cs?"), blank(),
              card("⌕", "cyan", "search_files", '"Program.cs"', "✓ 3", "green", width=W),
              card("▤", "blue", "read_file", "…/CheckpointService.cs", "✓ 81 lines", "green", width=W),
              blank(),
              say("A restore refuses a path when the file no longer"),
              say("holds what Corvid wrote. If you edited it since, the"),
              say("revert becomes a three-way merge, and a conflicting"),
              say("hunk stops the restore so your edit is kept."),
              ]
    live = [L(seg("  ⠹ ", "yellow"), seg("writing · 2.1 s · ", "grey"), seg("esc", "bcyan"),
              seg(" interrupt", "grey")),
            *composer(W, "and how do I keep my edit"),
            status(W, [("qwen3.5-0.8b", ""), (f"ctx {gauge(5100, 8192)}", ""), ("smart", "cyan")],
                   [seg("0:42", "grey")])]
    return scroll, live


def screen_startup(W, H):
    """First paint before the models load: the composer is usable while the status line reports progress."""
    lines = [shell_prompt(), *banner(W), L(seg("  Resumed: 14 turns in this session's log.", "grey")), blank(),
             user("why does /rollback refuse to restore src/Corvid.Cli/Program.cs?"),
             L(seg("  · answered · 3 steps, 2 tool calls · 9.4 s", "grey")), blank()]
    live = [*composer(W, "/model generator qwen-27b", hint="/model <slot> <model> [--pin]   switch a slot for this session"),
            status(W, [("loading models", "yellow"), ("embedder ✓", "green"), ("gliner2 ✓", "green"),
                       ("qwen3.5-0.8b 62%", "yellow"), ("kev-0.8b queued", "grey")],
                   [seg("first paint 48 ms", "grey")])]
    return lines, live


SCREENS = [
    ("01-streaming", "Inline renderer mid-turn", screen_streaming, 100, 30, "inline"),
    ("02-approval", "Approval with parsed commands, reasons and reversibility", screen_approval, 100, 30, "inline"),
    ("03-diff", "Diff cards from the write ledger", screen_diff, 100, 30, "inline"),
    ("04-fabric", "Decisions, taint, escalation, memory and jobs", screen_fabric, 100, 30, "inline"),
    ("05-rewind", "Esc Esc rewind over the checkpoint ledger", screen_rewind, 100, 30, "inline"),
    ("06-resume", "Resume picker", screen_resume, 100, 20, "inline"),
    ("07-startup", "First paint while models load", screen_startup, 100, 16, "inline"),
    ("08-fullscreen", "Fullscreen renderer, scrolled up", screen_fullscreen, 100, 30, "fullscreen"),
    ("09-linear", "Screen-reader (linear) renderer", screen_linear, 100, 21, "linear"),
    ("10-narrow", "The streaming screen at 60 columns", screen_narrow, 60, 24, "inline"),
]


def check(name, lines, live, W, H, mode):
    for i, ln in enumerate(lines + live):
        if ln.width > W:
            text = "".join(t for t, _ in ln.runs)
            raise SystemExit(f"{name}: line {i} is {ln.width} cells, wider than {W}: {text!r}")
    if mode == "inline" and live and len(live) >= H:
        raise SystemExit(f"{name}: live region {len(live)} rows is not shorter than the {H}-row window")
    if mode == "fullscreen" and len(lines) != H:
        raise SystemExit(f"{name}: fullscreen frame has {len(lines)} rows, window has {H}")


def emit(lines, live, mode, W):
    """The bytes: inline and linear append lines; fullscreen enters the alternate screen and paints rows."""
    out = []
    if mode == "fullscreen":
        out.append(ESC + "[?1049h" + ESC + "[H")
        for i, ln in enumerate(lines):
            out.append(ESC + f"[{i + 1};1H" + ln.ansi())
        return "".join(out)
    allx = lines + live
    out.append("\r\n".join(ln.ansi() for ln in allx))
    return ESC + "[?2026h" + "".join(out) + ESC + "[?2026l"


def main():
    os.makedirs(OUT, exist_ok=True)
    manifest = []
    for name, caption, fn, W, H, mode in SCREENS:
        scroll, live = fn(W, H)
        check(name, scroll, live, W, H, mode)
        data = emit(scroll, live, mode, W)
        if mode != "fullscreen" and re.search(r"\x1b\[[23]J", data):
            raise SystemExit(f"{name}: an inline frame clears the screen or scrollback")
        with open(os.path.join(OUT, name + ".ans"), "w", encoding="utf-8") as f:
            f.write(data)
        manifest.append({"name": name, "caption": caption, "cols": W, "rows": H, "mode": mode,
                         "live": len(live), "total": len(scroll) + len(live)})
    with open(os.path.join(OUT, "manifest.json"), "w") as f:
        json.dump(manifest, f, indent=1)
    for m in manifest:
        print(f"{m['name']:<14} {m['cols']}x{m['rows']:<3} {m['mode']:<10} lines {m['total']:<3} live {m['live']}")
    print("all invariants hold")


if __name__ == "__main__":
    main()
