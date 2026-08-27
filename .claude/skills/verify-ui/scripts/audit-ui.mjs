// Audits the running migrator UI: wiring, contrast, and layout, in both themes.
//
// The point is to check the things a screenshot cannot settle by eye. Contrast is
// measured against each element's *resolved* background — climbing ancestors until an
// opaque one is found — because the failures that matter are the ones a tinted panel
// causes, not the ones on white.
//
//   npm i -D playwright && npx playwright install chromium
//   dotnet run --project src/Migrator.App        # in another terminal
//   node .claude/skills/verify-ui/scripts/audit-ui.mjs [--url http://localhost:5099]
//                                                      [--shots <dir>]
//
// Exits non-zero when anything fails, so it can gate a change.

import { chromium } from 'playwright';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const flag = (name, fallback) => {
  const i = args.indexOf(name);
  return i >= 0 && args[i + 1] ? args[i + 1] : fallback;
};

// Resolved from this file rather than from the working directory, so the script
// behaves the same whether it is run from the repository root or from its own folder.
const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..');

const URL = flag('--url', 'http://localhost:5099');
const SHOTS = flag('--shots', null);
const APP_JS = path.join(REPO, 'src/Migrator.App/wwwroot/app.js');
const WIDTHS = [390, 768, 1280];

/** Runs in the page: every visible text node vs its real background. */
const CONTRAST = () => {
  const parse = (c) => {
    const m = c.match(/rgba?\(([\d.]+),\s*([\d.]+),\s*([\d.]+)(?:,\s*([\d.]+))?\)/);
    return m ? { r: +m[1], g: +m[2], b: +m[3], a: m[4] === undefined ? 1 : +m[4] } : null;
  };
  const luminance = ({ r, g, b }) => {
    const channel = (v) => {
      v /= 255;
      return v <= 0.03928 ? v / 12.92 : Math.pow((v + 0.055) / 1.055, 2.4);
    };
    return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
  };
  const ratio = (a, b) => {
    const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
    return (hi + 0.05) / (lo + 0.05);
  };
  const backgroundOf = (el) => {
    for (let node = el; node; node = node.parentElement) {
      const colour = parse(getComputedStyle(node).backgroundColor);
      if (colour && colour.a > 0.5) return colour;
    }
    return { r: 255, g: 255, b: 255, a: 1 };
  };

  const failures = [];
  const seen = new Set();
  for (const el of document.querySelectorAll('*')) {
    if (el.hidden || el.offsetParent === null) continue;
    const text = [...el.childNodes]
      .filter((n) => n.nodeType === Node.TEXT_NODE && n.textContent.trim())
      .map((n) => n.textContent.trim())
      .join(' ');
    if (!text) continue;

    const style = getComputedStyle(el);
    const colour = parse(style.color);
    if (!colour || colour.a < 0.5) continue;

    const size = parseFloat(style.fontSize);
    const weight = parseInt(style.fontWeight, 10) || 400;
    // WCAG "large text": >=24px, or >=18.66px when bold.
    const required = size >= 24 || (size >= 18.66 && weight >= 700) ? 3 : 4.5;
    const measured = ratio(colour, backgroundOf(el));

    const key = style.color + text.slice(0, 40);
    if (measured < required && !seen.has(key)) {
      seen.add(key);
      failures.push({
        text: text.slice(0, 50),
        colour: style.color,
        size,
        weight,
        measured: +measured.toFixed(2),
        required,
      });
    }
  }
  return failures;
};

let failed = 0;
const fail = (message) => { failed++; console.error(`  FAIL  ${message}`); };
const pass = (message) => console.log(`  ok    ${message}`);

const browser = await chromium.launch();

for (const theme of ['light', 'dark']) {
  console.log(`\n[${theme}]`);
  const context = await browser.newContext({ viewport: { width: 1280, height: 1000 } });
  const page = await context.newPage();

  const consoleErrors = [];
  page.on('pageerror', (e) => consoleErrors.push(String(e)));
  page.on('console', (m) => m.type() === 'error' && consoleErrors.push(m.text()));

  try {
    await page.goto(URL, { waitUntil: 'networkidle' });
  } catch {
    console.error(`  FAIL  ${URL} is not answering — start the app first.`);
    process.exit(1);
  }
  if (theme === 'dark') {
    await page.click('#btnTheme');
    await page.waitForTimeout(250);
  }

  // 1 · Wiring: every element app.js reaches for has to exist.
  const ids = [...new Set([...(await readFile(APP_JS, 'utf8')).matchAll(/\$\("([^"]+)"\)/g)]
    .map((m) => m[1]))];
  const missing = await page.evaluate((list) => list.filter((id) => !document.getElementById(id)), ids);
  missing.length
    ? fail(`app.js references ${missing.length} missing element(s): ${missing.join(', ')}`)
    : pass(`${ids.length} element ids referenced by app.js all exist`);

  // 2 · Every control reachable by name, not just by position.
  const unlabelled = await page.evaluate(() =>
    [...document.querySelectorAll('input, select, textarea')]
      .filter((el) => !el.closest('label') && !el.getAttribute('aria-label') && !el.labels?.length)
      .map((el) => el.id || el.className || el.type));
  unlabelled.length
    ? fail(`unlabelled control(s): ${unlabelled.join(', ')}`)
    : pass('every form control has a label or aria-label');

  // 3 · Contrast, with one line of each log severity painted so their colours count.
  await page.evaluate(() => {
    const log = document.getElementById('log');
    if (!log) return;
    for (const kind of ['Step', 'Info', 'Success', 'Warning', 'Error']) {
      const line = document.createElement('span');
      line.className = kind;
      line.textContent = `${kind} örnek satırı — ÇĞİÖŞÜ çğıöşü\n`;
      log.appendChild(line);
    }
    document.getElementById('btnReport')?.removeAttribute('hidden');
  });
  await page.waitForTimeout(150);

  const contrast = await page.evaluate(CONTRAST);
  if (contrast.length) {
    fail(`${contrast.length} text element(s) below WCAG AA`);
    for (const c of contrast) {
      console.error(`        ${c.measured}:1 (needs ${c.required}) ${c.colour} ` +
        `${c.size}px/${c.weight} — "${c.text}"`);
    }
  } else {
    pass('all rendered text meets WCAG AA contrast');
  }

  // 4 · Focus has to be visible, not merely present.
  await page.keyboard.press('Tab');
  const outline = await page.evaluate(() => {
    const s = getComputedStyle(document.activeElement);
    return { width: parseFloat(s.outlineWidth) || 0, style: s.outlineStyle };
  });
  outline.width >= 1 && outline.style !== 'none'
    ? pass(`focus ring visible (${outline.width}px ${outline.style})`)
    : fail('first tab stop has no visible focus ring');

  // 5 · Nothing may scroll sideways at any supported width.
  for (const width of WIDTHS) {
    await page.setViewportSize({ width, height: 1000 });
    await page.waitForTimeout(120);
    const overflow = await page.evaluate(() =>
      document.documentElement.scrollWidth - document.documentElement.clientWidth);
    overflow > 0
      ? fail(`horizontal overflow of ${overflow}px at ${width}px`)
      : pass(`no horizontal overflow at ${width}px`);
    if (SHOTS) {
      await page.screenshot({ path: path.join(SHOTS, `ui-${theme}-${width}.png`), fullPage: true });
    }
  }

  consoleErrors.length
    ? fail(`console errors: ${consoleErrors.join(' | ')}`)
    : pass('no console errors');

  await context.close();
}

await browser.close();
console.log(failed ? `\n${failed} check(s) failed.` : '\nAll checks passed.');
process.exit(failed ? 1 : 0);
