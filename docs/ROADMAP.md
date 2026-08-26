# Roadmap

## The goal

**Any SQL database to any SQL database, from a local app you run yourself.**

SQL Server → PostgreSQL is the first pair because it is the one that was needed first, not
because the tool is about those two products. Everything in the engine that is specific to
a product is isolated (see [ARCHITECTURE.md](ARCHITECTURE.md#where-the-provider-seam-is));
everything that is about *migrating safely* is shared and stays shared.

## Order of work

**1. Promote the provider seam to interfaces.** `ISourceReader` and `ITargetWriter`, with
the current SQL Server and PostgreSQL code as the first implementations. This comes before
the second provider on purpose: an abstraction extracted from one implementation is a
guess, but an abstraction extracted *just before* the second implementation lands is
informed by knowing what actually varies. The code is already grouped along that line.

**2. A second source: MySQL / MariaDB.** Chosen next because it stresses the seam
differently from SQL Server — different `information_schema` dialect, different type names,
different identity semantics — which is exactly what the interfaces need to survive.

**3. A second target: SQL Server.** Making the tool bidirectional forces the target side to
stop assuming PostgreSQL specifics (`COPY`, `session_replication_role`, `setval`) and
express them as capabilities a provider either has or emulates.

**4. Self-contained builds — shipped.** Every tagged release publishes a single executable
per platform on the [releases page](https://github.com/splaxtr/sql-data-migrator/releases);
`dotnet publish -c Release -r <rid>` produces the same binary locally. It is meant to be
handed to whoever is doing the migration.

**5. Table selection.** Migrate a subset. The engine already reasons about a plan; the
missing piece is the safety analysis, because truncating a subset can cascade into tables
outside it — which must be refused, not discovered afterwards.

**6. Checksum verification.** Optional per-table aggregate comparisons (sums over numeric
columns) on top of row counts, defined by the user, so a migration can prove that money
totals match and not merely that the rows arrived.

## Deliberately out of scope

**Schema creation and translation.** Turning one product's DDL into another's is a large,
separate problem with its own failure modes. Conflating it with data movement makes both
harder to trust. Bring your own schema.

**Continuous replication.** This is a cutover tool. Change-data-capture pipelines are a
different product with a different operational model.

**Data transformation.** Renaming, reshaping or cleaning during a migration means a failure
cannot be distinguished from a transformation bug. Move the data faithfully, then transform
it with something that can be tested and re-run.

## Non-goals that look like goals

**A GUI framework.** The UI is one HTML page served by the app on purpose. It has no build
step, no dependency tree, and works on every platform .NET runs on. Adding a desktop UI
framework would cost more than it returns for a screen with four sections.

**Multi-user or hosted operation.** The app binds to localhost and holds credentials for
production databases. It is a local tool, and keeping it local is a security property, not
a limitation to grow out of.
