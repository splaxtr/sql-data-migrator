---
name: verify-ui
description: Audit the running migrator web UI for contrast, wiring, focus and layout. Use after any change to src/Migrator.App/wwwroot (HTML, CSS or JS), before committing, or when asked whether a UI change is safe. Checks both light and dark themes.
---

# Verify the migrator UI

The interface is vanilla HTML/CSS/JS with no build step and no test project, so nothing
catches a UI regression except looking — and the failures that matter here are the ones
looking does not catch. A colour that misses WCAG AA by a tenth of a point, an element id
that `app.js` still reaches for after a rename, a `hidden` attribute defeated by a
`display` rule: all of them render as a page that looks fine.

This skill measures those instead.

## Running it

Playwright lives in this folder's own `package.json`, not in the solution — the shipped
application has no JavaScript dependencies and this must not change that.

```bash
cd .claude/skills/verify-ui && npm install    # once; also fetches Chromium
dotnet run --project src/Migrator.App          # in another terminal
node .claude/skills/verify-ui/scripts/audit-ui.mjs
```

The script finds the repository from its own path, so it runs the same from anywhere.
Options: `--url <address>` if the app is not on `http://localhost:5099`, and `--shots <dir>`
to also write full-page screenshots of every theme and width.

Exit code is non-zero when a check fails, so it can gate a commit.

## What it checks, in both themes

| Check | Why it is here |
|---|---|
| Every `$("id")` in `app.js` resolves to an element | A renamed id in `index.html` fails silently at runtime, not at build |
| Every input has a label or `aria-label` | The picker's target-name field loses its column heading on narrow screens |
| Contrast of every visible text node vs its **resolved** background | Status colours sit on a tinted panel, not on white, which costs about half a point |
| A visible focus ring on the first tab stop | Focus may be replaced, never removed |
| No horizontal overflow at 390 / 768 / 1280 px | A sideways-scrolling page is a layout bug, not a preference |
| No console errors | |

Log severities are painted into the log element before the contrast pass, so their colours
are measured too — they are never present on a freshly loaded page.

## Reading a contrast failure

```
FAIL  1 text element(s) below WCAG AA
      4.41:1 (needs 4.5) rgb(21, 128, 61) 12.5px/600 — "✔ 11 satır taşındı ve doğrulandı."
```

Fix it in the token, not at the call site: colours live in `:root` and
`:root[data-theme="dark"]` in `style.css`, and a one-off override in a rule is how the two
themes drift apart. Darkening the token is usually right — the same colour is read on both
the white card and the tinted log panel, and the panel is the harder of the two.
