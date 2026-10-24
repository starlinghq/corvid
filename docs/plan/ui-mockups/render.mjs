// Renders each html/*.html page (written by to-html.py) in headless Chromium: 1440x900 and 820x900 for the desktop
// artboards, 390x844 for the phone. Fonts come from Google Fonts, so rendering needs the network.
import { chromium } from "playwright";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const html = path.join(here, "html");
const out = path.join(here, "png");
fs.mkdirSync(out, { recursive: true });

const browser = await chromium.launch();
for (const file of fs.readdirSync(html).filter(name => name.endsWith(".html"))) {
  const name = file.replace(".html", "");
  const phone = name === "Phone";
  const page = await browser.newPage({ viewport: phone ? { width: 390, height: 844 } : { width: 1440, height: 900 } });
  // The wait for an idle network may time out on a page that loaded; what must not happen is a render with fallback
  // fonts overwriting the references the app is compared against, so the fonts are checked before any screenshot.
  await page.goto(`file://${path.join(html, file)}`, { waitUntil: "networkidle" }).catch(() => {});
  await page.waitForTimeout(500);
  // document.fonts.check() is true when no face of the family exists at all, so ask for the faces and count them.
  const fonts = await page.evaluate(async () => {
    await document.fonts.ready;
    const inter = await document.fonts.load('13px Inter');
    const mono = await document.fonts.load('13px "JetBrains Mono"');
    return inter.length > 0 && mono.length > 0;
  });
  if (!fonts)
    throw new Error(`${file}: Inter or JetBrains Mono did not load (offline?); no PNG was written`);
  await page.screenshot({ path: path.join(out, `${name}.png`) });
  if (!phone) {
    await page.setViewportSize({ width: 820, height: 900 });
    await page.screenshot({ path: path.join(out, `${name}-narrow.png`) });
  }
  await page.close();
  console.log(name);
}
await browser.close();
