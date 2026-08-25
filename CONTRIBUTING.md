# Contributing

Thank you for looking. This is a small tool with a narrow purpose, and the fastest way to
have a change accepted is to know what that purpose is before you write it.

## What this project is trying to be

**Any SQL database to any SQL database, moved safely, from a local app you run yourself.**

Two words in that sentence do the work. *Safely* means the tool refuses to report success it
did not earn — see [docs/SAFETY.md](docs/SAFETY.md). *Local* means it binds to localhost and
keeps your credentials on your machine, and that is a security property rather than a stage
it will grow out of.

[docs/ROADMAP.md](docs/ROADMAP.md) lists what is planned and, just as importantly, what is
deliberately out of scope. Schema translation, continuous replication and data
transformation are not smaller versions of this tool's job — they are different jobs, and
each would make this one harder to trust. A pull request that adds one of them will be
declined no matter how good the code is, so please open an issue before building it.

## Getting set up

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Nothing else.

```bash
git clone https://github.com/splaxtr/sql-data-migrator.git
cd sql-data-migrator

dotnet build SqlDataMigrator.sln -c Release   # what CI runs
dotnet run --project src/Migrator.App          # the app, on http://localhost:5099
```

The build is clean of warnings and CI enforces that with `-warnaserror`. If your change
introduces a warning, fix the warning rather than the setting.

To exercise a real migration you need a SQL Server and a PostgreSQL instance. Containers are
enough:

```bash
docker run -d --name sdm-mssql -p 11433:1433 \
  -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='YourStrong!Pass1' \
  mcr.microsoft.com/mssql/server:2022-latest

docker run -d --name sdm-pg -p 15432:5432 \
  -e POSTGRES_PASSWORD=postgres postgres:16
```

The tool does not create schemas — see below — so you will need to create the target tables
yourself before a run does anything interesting.

## How the code is laid out

```
src/Migrator.Core/     the migration engine - no UI, no HTTP, no console
src/Migrator.App/      a local web app: minimal API + one HTML page
```

Read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) before changing either. Three rules come
out of it and they are the ones worth stating twice:

1. **`Migrator.Core` never writes to `Console` and never knows how it is being driven.** It
   reports through `IProgress<ProgressMessage>`. This is what lets the same engine serve a
   web page, a CLI, and a test harness.
2. **Verification runs inside the transaction, before the commit.** Steps 6 through 9 of the
   pipeline share one transaction on purpose. A change that moves verification after the
   commit, or splits the transaction for performance, is changing the central guarantee of
   the tool and needs to argue for itself in the pull request, not in a commit message.
3. **The source is read-only.** Every statement sent to the source is a `SELECT`. If your
   change adds anything else to the source path, it is the wrong change.

## Adding a database provider

This is the most useful contribution available, and it has a prerequisite.

The engine is written against two roles — a source reader and a target writer — but today
those roles live in file layout rather than in interfaces
([where the seam is](docs/ARCHITECTURE.md#where-the-provider-seam-is)). Roadmap item 1 is
promoting them to `ISourceReader` / `ITargetWriter`, and it is item 1 because an abstraction
extracted from a single implementation is a guess.

So: if you want to add MySQL, SQL Server as a *target*, or anything else, open an issue
first. The interface extraction and your provider should land together or in that order, and
coordinating that in an issue is much cheaper than discovering it in review.

## Tests

There is no test project yet. This is the largest gap in the repository and CI says so on
every run rather than hiding it behind a green check.

If you are adding one, the engine is the place to start — it was deliberately built with no
console and no HTTP so it can be driven from a test. Put it in `tests/` and CI will pick it
up automatically; the workflow already looks for `tests/*/*.csproj`.

Contributions that come with tests for the behaviour they change are much easier to accept,
especially anywhere near the pipeline.

## Pull requests

- **Branch from `main`** and keep one concern per pull request.
- **Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/)** —
  `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`. The existing history is the
  reference.
- **Say what you verified.** "Builds clean" and "ran a migration against a 40-table schema"
  are different claims and the difference matters here. If you did not test it against a
  real database, say that too — an untested change is reviewable, a change described as
  tested when it was not is a problem.
- **Update the docs in the same pull request.** If behaviour changes,
  [docs/SAFETY.md](docs/SAFETY.md) and [docs/USAGE.md](docs/USAGE.md) are part of the
  behaviour. A guarantee that only exists in code is not a guarantee anyone can rely on.
- **Never commit credentials, connection strings, or a dump of a real database.** Saved
  connections live outside the repository by design; keep it that way.

## Reporting bugs

Open an issue with the source and target product versions, what you expected, what happened,
and the final report line from the run. If the run stopped, the message it stopped with is
usually the whole answer — please include it verbatim.

If the bug is that the tool **reported success while data was wrong or missing**, that is the
most serious class of bug this project has. Label it clearly and it will be looked at first.

For anything with a security dimension, do not open a public issue — see
[SECURITY.md](SECURITY.md).

## Code of conduct

Participation is governed by [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
