# Working on this repository with Claude Code

[CONTRIBUTING.md](CONTRIBUTING.md) covers what the project is for, how to build it, and how
pull requests are reviewed — read it first. This file only adds the things that are easy to
get wrong here and expensive to notice late.

## Build the way CI builds

```bash
dotnet build SqlDataMigrator.sln -c Release -warnaserror
```

CI uses `-warnaserror`. A plain `dotnet build` succeeds on code that CI rejects — a nullable
warning is enough — so always pass the flag before claiming a change compiles.

## Language: English in the code, Turkish on the screen

- **Code, comments, commit messages, docs, log text produced by the engine — English.**
- **Anything a user reads on the page — Turkish.** That means `wwwroot/index.html`, and the
  `TR` dictionary in `wwwroot/app.js`. The PDF report is user-facing too, so its strings are
  Turkish even though they live in C#.

## Message codes are a wire format

The engine never formats a sentence for a user. It reports an English `Text` plus a stable
`Code` and `Args`, and the browser translates by code:

- `Migrator.Core/MessageCode.cs` — codes the **engine** produces.
- `Migrator.App/AppMessageCode.cs` — codes the **application** produces around it (batching,
  reporting). A batch is not something the engine knows about; keep them apart.

Two rules follow:

1. **Adding a code means adding its Turkish line to the `TR` map in `app.js`.** A missing
   entry silently falls back to English — it looks like a translation bug, not a build error.
2. **Renaming a code silently drops its translation.** Add a new one instead of repurposing
   an old one.

## The UI has no build step

`wwwroot/` is plain HTML, CSS and JavaScript, embedded into the assembly so the published
single-file executable carries its own interface. Consequences:

- **No CDN, no external fonts, no npm.** The app is offline and localhost-only. The PDF
  report ships its own font (`Assets/DejaVuSans.ttf`) for the same reason — Turkish letters
  must render on a machine with no fonts installed.
- **In Development the physical files are served**, so editing CSS/JS and reloading is
  enough; a rebuild is only needed for C#. In Release the embedded copy is used.
- CSS is theme-tokened. Colours live in `:root` and `:root[data-theme="dark"]` in
  `style.css`; do not hardcode a colour in a rule.

## What the engine is not allowed to do

These are the reasons the tool exists, from [docs/SAFETY.md](docs/SAFETY.md):

1. **Never report success it did not earn.** Copy and verification run in one transaction and
   nothing is committed until row counts and foreign keys check out.
2. **Never leave a half-migrated target.** A failure rolls back.
3. **Never relax a check by default.** Every gate that can be opened is an explicit option
   that says what it lets through.

A change that makes a run more likely to *appear* to succeed is a change in the wrong
direction, however convenient.

## Credentials

Saved server passwords are encrypted with the machine-bound data-protection key. Generated
per-database passwords are **never written to disk** — they exist in memory and in the PDF
the browser downloads, and that is the whole design. Do not add logging, caching or a
"convenience" store for them.

## Verifying a change

Build and unit checks are not enough for this project, because most of what can go wrong is
behavioural. Two loops:

**The engine** — run a real migration against containers (see CONTRIBUTING.md for the
`docker run` lines), and check the target afterwards rather than trusting the summary.

**The UI** — `.claude/skills/verify-ui/` drives the running page with Playwright and checks
contrast, wiring and layout automatically. Use it after any change to `wwwroot/`.

## Commits and releases

Conventional Commits, because [release-please](https://github.com/googleapis/release-please)
derives the version and changelog from them. `feat:` bumps the minor, `fix:` the patch. The
release PR it opens is merged to publish binaries — do not tag by hand.
