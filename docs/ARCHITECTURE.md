# Architecture

## The shape of the thing

Two projects, and the split matters:

```
src/Migrator.Core/     the migration engine — no UI, no HTTP, no console
src/Migrator.App/      a local web app: minimal API + one HTML page
```

`Migrator.Core` knows nothing about how it is driven. It reports progress through
`IProgress<ProgressMessage>` rather than writing to a console, which is why the same engine
can serve a web page today, a CLI tomorrow, and a test harness in between. Anything that
writes to `Console` in the engine is a bug.

## The pipeline

A run is a fixed sequence. Every step can stop the run, and stopping is always safer than
continuing:

| Step | What it does | Why it can stop the run |
|---|---|---|
| 1. Collation check | Reads the target's collation | A wrong collation is silent — the database works, only sorting and search are quietly wrong, and fixing it later means recreating the database |
| 2. Schema read | Reads both `information_schema`s and the target's foreign keys | — |
| 3. Plan | Intersects source and target columns per table | A NOT NULL target column with no source and no safe default cannot be filled |
| 4. Source-only check | Lists source tables absent from the target | Their data would be silently left behind |
| 5. Pre-flight | Scans source data for NULLs headed into NOT NULL columns and values longer than the target allows | Better to stop before the copy than to fail halfway through it |
| 6. Copy | Truncate + binary COPY, table by table, inside one transaction | — |
| 7. Sequence fixup | `setval` on every identity sequence | Without this the first insert after migration collides |
| 8. Verify | Row counts per table, then foreign key orphans | Failure rolls the whole thing back |
| 9. Commit | Only now | — |

Steps 6 through 9 share a single transaction. That is the load-bearing decision in this
codebase: **verification runs before the commit, not after it.** Verifying afterwards would
detect problems and leave them in place, which is worse than not checking, because it
produces a failure report next to committed bad data.

## Where the provider seam is

The engine is written against two roles:

- **A source reader** — enumerate databases, read the table/column shape, stream rows out.
- **A target writer** — create the database, read its shape and constraints, bulk-load rows,
  reset sequences, verify.

Today those roles are filled by concrete SQL Server and PostgreSQL code, and the seam is
visible in the file layout rather than in interfaces:

| File | Role | Provider-specific |
|---|---|---|
| `SchemaReader.ReadSqlServerAsync`, `ListSqlServerDatabasesAsync` | source reader | SQL Server |
| `SchemaReader.ReadPostgresAsync`, `ReadForeignKeysAsync`, `ListPostgresDatabasesAsync` | target writer | PostgreSQL |
| `TargetDatabase` | target writer | PostgreSQL |
| `MigrationEngine.WriteValueAsync` | type mapping | source CLR type → target store type |
| `MigrationEngine` (the rest) | orchestration | none |

**Adding a provider** means adding the reader or writer half and the type mapping, then
letting the orchestration stay untouched. The next change to this codebase should promote
these into `ISourceReader` / `ITargetWriter` interfaces — the code is already grouped that
way, so the refactor is mechanical, and doing it before the second provider arrives keeps
the second provider from being bolted on. See [ROADMAP.md](ROADMAP.md).

## Type mapping

Mapping is driven by the **target** store type, not the source type. The engine asks the
target what a column is and converts the incoming CLR value to that. This keeps the mapping
table small: it grows with target types, not with the product of source and target types.

Two mappings encode real decisions rather than mechanics:

- **`timestamp`** — written with `DateTimeKind.Unspecified`, verbatim, with no UTC
  conversion. A wall-clock value in the source stays the same wall-clock value in the
  target. Converting here would silently shift every timestamp in the database by the
  migrating machine's offset.
- **`timestamptz`** — a `DateTimeOffset` becomes its UTC instant. A `DateTime` that is
  already marked UTC is taken as-is; one marked Local is converted; one with no kind at all
  is treated as UTC rather than being shifted by the host's timezone, because the host's
  timezone is not part of the data.

Text is never case-folded or normalised. `ABC` arrives as `ABC` and `abc` as `abc`; casing
is data, and the target's collation governs comparison, never storage.

## Stored connections

Saved servers live in a JSON file under the user's local application data directory, and
passwords are encrypted with `Microsoft.AspNetCore.DataProtection` keyed to the machine.
The file never enters the repository. This is a developer tool that talks to production
databases, so the storage is deliberately boring and local.

## What is deliberately not here

- **No schema creation.** The target's tables must already exist. Creating a schema is a
  different problem with different failure modes (types, indexes, constraints, defaults),
  and conflating the two would make both worse. Use your ORM's migrations or a schema tool,
  then move the data with this.
- **No incremental or online sync.** Every run truncates the tables it is about to fill.
  This is a cutover tool: it assumes writes to the source have stopped. Copying a moving
  source produces a target that is internally inconsistent across tables.
- **No transformation.** Column names and values pass through. A migration that also
  reshapes data is two jobs, and pretending it is one is how data gets lost.
