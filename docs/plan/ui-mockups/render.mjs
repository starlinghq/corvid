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
  await page.goto(`file://${path.join(html, file)}`, { waitUntil: "networkidle" }).catch(() => {});
  await page.waitForTimeout(500);
  await page.screenshot({ path: path.join(out, `${name}.png`) });
  if (!phone) {
    await page.setViewportSize({ width: 820, height: 900 });
    await page.screenshot({ path: path.join(out, `${name}-narrow.png`) });
  }
  await page.close();
  console.log(name);
}
await browser.close();
