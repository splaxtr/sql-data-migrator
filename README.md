# SQL → SQL Migrator

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

```bash
git clone https://github.com/splaxtr/mssql-to-postgres.git
cd mssql-to-postgres
dotnet run --project src/Migrator.App
```

The app starts on <http://localhost:5099> and opens your browser. Nothing is installed,
nothing runs as a service, and no data leaves your machine.

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). A
self-contained build that needs no .NET installed is on the roadmap.

## What the screen does

**Connections** — Add each server once (name, host, port, user, password). They are stored
on your own machine, not in this repository and not in any cloud. Passwords are encrypted
with the OS data-protection API. See [docs/SAFETY.md](docs/SAFETY.md#stored-credentials).

**Source** — Pick a saved SQL Server, then pick the database. The list is read live from the
server and filters as you type, so a server with two hundred databases is still usable.

**Target** — Pick a saved PostgreSQL server. The target database name defaults to the
source name, and you can either accept it, choose an existing database from the list, or
type a new name. A database that does not exist is created for you.

**Options** — Every gate that can be relaxed is off by default and has to be turned on
deliberately. They exist because a real migration sometimes needs them, not because
skipping checks is normal. Each one explains what it lets through.

**Run** — Progress streams live: which table, how many rows, what was verified. The final
line is the only thing that matters, and it is honest.

## Documentation

| Document | What it covers |
|---|---|
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | How the pipeline works and where to add a database provider |
| [docs/SAFETY.md](docs/SAFETY.md) | What is guaranteed, what is not, and why each check exists |
| [docs/USAGE.md](docs/USAGE.md) | Field-by-field walkthrough, the options, and what to do when a run stops |
| [docs/ROADMAP.md](docs/ROADMAP.md) | The SQL → SQL goal and the order things get built |

## License

MIT — see [LICENSE](LICENSE).
