# SQL Data Migrator

[![CI](https://github.com/splaxtr/sql-data-migrator/actions/workflows/ci.yml/badge.svg)](https://github.com/splaxtr/sql-data-migrator/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

A local app that moves data between SQL databases — **SQL Server → PostgreSQL** today.

Move data between SQL databases from a small local app you run on your own machine.
Save your servers once, pick a source database from a searchable list, pick or name the
target, press **Migrate**, watch it happen.

The migration is verified before it is committed. If row counts, or foreign key
integrity, do not line up, nothing is written — the target is left exactly as it was.

> **Currently implemented:** SQL Server → PostgreSQL.
> **What this is meant to be:** any SQL database to any SQL database. The engine is built
> around a source reader and a target writer, so adding a provider is additive work rather
> than a rewrite. See [docs/ROADMAP.md](docs/ROADMAP.md).

---

## Why this exists

Migrating a database is usually a one-off script that someone writes under time pressure,
runs once, and cannot repeat. That script tends to share three flaws:

1. **It reports success it did not earn.** Pointed at the wrong database it copies nothing
   and exits zero.
2. **It leaves damage behind.** A failure halfway through leaves the target half-loaded
   with no record of where it stopped.
3. **It moves rows without checking them.** Constraints are usually suspended during a
   bulk load and never re-validated, so inconsistencies from the source enter silently.

This tool exists because those three failures are the ones that cost real money, and all
three are avoidable. See [docs/SAFETY.md](docs/SAFETY.md) for exactly what is guaranteed.

## Running it

### As a single executable

Prebuilt binaries for Windows, Linux, and macOS are published on the
[releases page](https://github.com/splaxtr/sql-data-migrator/releases) for every tagged
version.

> **Windows note.** The binaries are not code-signed yet. SmartScreen may warn — choose
> *More info → Run anyway*. **Smart App Control** (Windows 11), which only runs signed or
> known apps, will block the exe outright with no override; on such a machine, build from
> source below or wait for a signed release. Either way, verify your download against
> `SHA256SUMS.txt` first.

To build one yourself:

```bash
dotnet publish src/Migrator.App -c Release -r win-x64
```

This produces one self-contained `Migrator.App.exe` under
`src/Migrator.App/bin/Release/net8.0/win-x64/publish/` — around 50 MB, because it carries
its own .NET runtime and the whole UI. Whoever you hand it to just runs it: it starts on
<http://localhost:5099> and opens the browser by itself, with nothing to install. Use
`-r linux-x64` or `-r osx-arm64` for the other platforms.

### From source

```bash
git clone https://github.com/splaxtr/sql-data-migrator.git
cd sql-data-migrator
dotnet run --project src/Migrator.App
```

The app starts on <http://localhost:5099>. Nothing is installed, nothing runs as a
service, and no data leaves your machine.

Building either way requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0);
the published executable itself requires nothing.

## What the screen does

**Connections** — Add each server once (name, host, port, user, password). They are stored
on your own machine, not in this repository and not in any cloud. Passwords are encrypted
with the OS data-protection API. See [docs/SAFETY.md](docs/SAFETY.md#stored-credentials).

**Source** — Pick a saved SQL Server, then tick the databases to move. The list is read live
from the server and filters as you type, so a server with two hundred databases is still
usable. Tick as many as you like: they are migrated one after another, and one failing does
not stop the rest.

**Target** — Pick a saved PostgreSQL server. Target names come from a pattern — `{db}` is
the source name, so `{db}_pg` renames the lot in one go — and any single name can still be
edited by hand, with the target server's existing databases offered as suggestions so you
can migrate into one that is already there. Leave a name empty and it takes the source's.
Each row says whether that name is already on the server or is about to be created.

**Options** — A run is one of three modes. **Migrate** prepares the target, copies, verifies
and commits. **Verify only** compares an existing target against the source and writes
nothing at all. **Create database only** creates the target database — and its login, if you
asked for one — without reading or writing a single table.

Everything else is a gate that is off by default and has to be turned on deliberately. They
exist because a real migration sometimes needs them, not because skipping checks is normal,
and each one explains what it lets through. Two do more than relax a check: **mirror**
creates the tables the target is missing from the source schema, and **one user per
database** gives each migrated database its own PostgreSQL login. Options a mode would
ignore are locked with the reason, so the page never offers work the run will not do.

**Run** — Progress streams live: which database, which table, how many rows, what was
verified. The final line is the only thing that matters, and it is honest. When the run
ends you can download a PDF report of what moved — and, if you asked for logins, the
usernames and passwords, which appear there and nowhere else.

The screen is light by default; the button in the top right switches to a dark theme and
remembers the choice. Both themes are checked against WCAG AA contrast.

## Documentation

| Document | What it covers |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | How the pipeline works and where to add a database provider |
| [docs/SAFETY.md](docs/SAFETY.md) | What is guaranteed, what is not, and why each check exists |
| [docs/USAGE.md](docs/USAGE.md) | Field-by-field walkthrough, the options, and what to do when a run stops |
| [docs/ROADMAP.md](docs/ROADMAP.md) | The SQL → SQL goal and the order things get built |

## Contributing

Pull requests are welcome, and [docs/ROADMAP.md](docs/ROADMAP.md) is the best place to see
what would actually help. Read [CONTRIBUTING.md](CONTRIBUTING.md) first — it covers how to
build and run the project, the three rules the engine is built on, and what is deliberately
out of scope, which is the part worth knowing before you write anything.

Participation is governed by the [Code of Conduct](CODE_OF_CONDUCT.md).

Found a security problem? Follow [SECURITY.md](SECURITY.md) — report it privately rather
than opening a public issue.

## License

MIT — see [LICENSE](LICENSE).
