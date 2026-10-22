# TUI mockup screens

Ten screens of the design in [`../tui.md`](../tui.md), made before the TUI exists. `frames.py` writes each screen as the
bytes the design's renderer would emit (inline, fullscreen or linear) and refuses any screen that breaks the design's
rules: a line wider than the terminal, `CSI 2J` or `CSI 3J` in an inline frame, an inline live region as tall as the
window, a fullscreen frame that does not fill the window. `render.mjs` draws each frame with xterm.js (canvas renderer,
JetBrains Mono) in headless Chromium and saves the PNGs in `png/`. The sessions shown are examples.

```sh
cd docs/plan/tui-mockups
npm install                 # xterm.js, its canvas addon and Playwright
python3 frames.py           # writes frames/*.ans and checks the invariants
node render.mjs             # writes png/*.png
```

When the TUI is built, these screens become fixtures for its layer-2 tests (§8): the real renderer's output for the
same scripted sessions should draw the same screens.
