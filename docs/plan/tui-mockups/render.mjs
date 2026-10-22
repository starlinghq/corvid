// Renders each frames/*.ans through xterm.js in headless Chromium and saves a PNG of the terminal.
import { chromium } from "playwright";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const frames = path.join(here, "frames");
const out = path.join(here, "png");
fs.mkdirSync(out, { recursive: true });
const manifest = JSON.parse(fs.readFileSync(path.join(frames, "manifest.json"), "utf8"));
const xtermJs = fs.readFileSync(path.join(here, "node_modules/@xterm/xterm/lib/xterm.js"), "utf8");
const canvasJs = fs.readFileSync(path.join(here, "node_modules/@xterm/addon-canvas/lib/addon-canvas.js"), "utf8");
const xtermCss = fs.readFileSync(path.join(here, "node_modules/@xterm/xterm/css/xterm.css"), "utf8");

// A dark terminal theme close to common defaults; the TUI itself uses the 16-colour palette.
const theme = {
  background: "#16181d", foreground: "#d6dae1", cursor: "#d6dae1", selectionBackground: "#3a3f4b",
  black: "#1f2229", red: "#e06c75", green: "#98c379", yellow: "#e5c07b", blue: "#61afef", magenta: "#c678dd",
  cyan: "#56b6c2", white: "#c8ccd4", brightBlack: "#6b7280", brightRed: "#ff7b86", brightGreen: "#b5e890",
  brightYellow: "#f0d48f", brightBlue: "#7cc4ff", brightMagenta: "#d896ec", brightCyan: "#6fd3df", brightWhite: "#f2f4f8",
};

const browser = await chromium.launch();
const page = await browser.newPage({ deviceScaleFactor: 1 });
await page.setContent(`<html><head><style>${xtermCss} body{margin:0;background:${theme.background}} #t{display:inline-block;padding:26px 30px}</style></head><body><div id="t"></div><script>${xtermJs}</script><script>${canvasJs}</script></body></html>`);

for (const m of manifest) {
  const data = fs.readFileSync(path.join(frames, m.name + ".ans"), "utf8");
  const rows = m.mode === "fullscreen" ? m.rows : m.total;
  await page.evaluate(async ({ data, cols, rows, theme }) => {
    await document.fonts.load("26px 'JetBrains Mono'");
    await document.fonts.load("bold 26px 'JetBrains Mono'");
    const host = document.getElementById("t");
    host.innerHTML = "";
    const term = new Terminal({ cols, rows, theme, fontFamily: "'JetBrains Mono', 'DejaVu Sans Mono', monospace",
      fontSize: 26, lineHeight: 1.0, cursorBlink: false, cursorStyle: "bar", allowProposedApi: true });
    term.open(host);
    term.loadAddon(new CanvasAddon.CanvasAddon());
    await new Promise((resolve) => term.write(data, resolve));
    term.write("\x1b[?25l"); // the composer draws its own caret in these frames
    await new Promise((r) => setTimeout(r, 150));
  }, { data, cols: m.cols, rows, theme });
  await page.locator("#t").screenshot({ path: path.join(out, m.name + ".png") });
  console.log("rendered", m.name, `${m.cols}x${rows}`);
}
await browser.close();
