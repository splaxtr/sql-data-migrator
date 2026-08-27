# Safety

What this tool guarantees, what it does not, and why each check is there. Read the "not
guaranteed" section — it is the more useful half.

## Guaranteed

**A migration never writes to the source.** Every statement a migration sends to the source
database is a `SELECT`. There is no `INSERT`, `UPDATE`, `DELETE`, or DDL anywhere in the
migration's source path. This is what makes rollback trivial: if anything goes wrong, point
your application back at the source and nothing has been lost.

> **This promise was narrowed, deliberately.** It used to read "the source is never written
> to", full stop. The app now also has a **management panel** (see
> [Server management](#server-management)) which can create, alter and drop databases and
> logins on *any* registered server — including one you also migrate from. No migration ever
> uses it, and it cannot be reached from a migration; but the app as a whole is no longer a
> program that only reads from your source, and saying otherwise would be untrue.

**What CASCADE will empty is known before the locks are taken.** The truncate is
`TRUNCATE ... RESTART IDENTITY CASCADE`, so it reaches every target table referencing one
being copied. Those outside the copy set are not refilled, and they are computed from the
target's foreign keys and listed during pre-flight — before the transaction opens — rather
than read back from PostgreSQL's notices once every table is exclusively locked.

**A failure leaves the target untouched.** The truncate, the copy, the sequence fixup and
the verification all share one transaction. If any of them fails, the transaction rolls
back and the target is byte-identical to what it was before the run.

**Success means the data actually moved.** A run only exits successfully if:

- the copy plan was non-empty (the databases really do share tables),
- at least one row was copied,
- every table's row count matches the source, and
- no foreign key has an orphaned child row.

Any of those failing is a failure, not a warning.

**Foreign keys are re-checked.** Constraint triggers are suspended during the copy so table
order does not matter. That suspension is the reason the orphan check exists: without it,
inconsistencies in the source enter the target while the constraints still claim to be
valid, and the problem surfaces months later during a restore.

**Text is preserved exactly.** Casing, accents and Turkish characters pass through
unchanged. Collation affects comparison and ordering, never what is stored.

## Not guaranteed

**A consistent snapshot of a live source.** The copy reads table by table. If the source is
still being written to, table A may reflect a later moment than table B — an invoice can
arrive without its payment. Stop writes to the source before you start. This is why the
tool describes itself as a cutover tool.

**That the target schema is right.** The tool moves data into tables that already exist. If
the target schema is missing a column, that column's data does not travel and the run still
succeeds — the column is named in the run and in the PDF report, not treated as failure,
because dropped columns are a normal outcome of a schema change. Read the report.

The reverse case is reported the same way and is the more dangerous one: a target column
that is `NOT NULL`, has no default and has no source counterpart is **given a made-up
value** — `0`, `false` or `''` — in every row. The run says which column and which value.
Afterwards an invented `0` is indistinguishable from a measured one, so that line is the
only chance to catch it.

**Anything outside `dbo`.** The source is read from the `dbo` schema only, and the target is
written in `public` only. Base tables in any other source schema are counted and named, and
the run stops unless *allow tables missing from target* is on — they are source data with no
target counterpart, which is what that option already governs. Mirroring does not help here
and is not offered as a remedy: it creates tables in `public`, so a `reporting.Invoice`
would land next to `dbo.Invoice` under the same name.

**That your data was correct to begin with.** Row counts and foreign keys are checked.
Business meaning is not. If the source contains a wrong balance, the target will contain
the same wrong balance, faithfully.

**Sub-microsecond timestamp precision.** SQL Server's `datetime2` stores 100-nanosecond
ticks; PostgreSQL's `timestamp` stores microseconds. The extra precision is truncated. This
matters only for exact-equality comparisons on timestamps, never for ordering or sums.

## The escape hatches

Four checks can be turned off. All are off by default, and each one exists because a real
migration occasionally needs it:

| Option | What it lets through | When it is legitimate |
|---|---|---|
| Allow tables missing from target | Source tables with no target counterpart | The schema genuinely dropped them. **Read the list first** — a table you still need is the same signal |
| Allow schema risk | NULLs headed for NOT NULL columns, values longer than the target allows | You have decided the copy should fail loudly on those rows instead of being blocked up front |
| Allow collation mismatch | A target collation other than the expected one | You deliberately chose a different collation and understand the search and sort consequences |
| Verify only | Skips the copy entirely | Checking an earlier migration without touching anything |

Turning one on without reading what it reported cancels the reason this tool exists.

## ORM migration history is the target's, and is kept

An ORM keeps a table of the migrations a database has had applied — `__EFMigrationsHistory`,
`django_migrations`, `schema_migrations`, `flyway_schema_history`, `__drizzle_migrations`.
Its rows describe **the target**, not the data being moved, and the answer is specific to the
provider the target runs on: a PostgreSQL branch of an application has different migration
IDs from the SQL Server branch it was ported from.

So these tables are left out of the migration entirely. They are neither truncated nor
filled, and the target's rows survive untouched.

This is not one of the escape hatches above, and it is deliberately not in that table. Those
options let questionable *data* through; this is the opposite direction. Copying the source's
rows over the target's does not merge two histories — it replaces a true statement with a
false one, and the ORM believes it. Entity Framework then finds no record of its baseline,
re-applies it, and fails on tables that already exist. That is a real failure this tool
caused, not a hypothetical.

It is the same class of loss the tool already refuses elsewhere, in the other direction: a
source table with no target counterpart stops the run because data would be left behind, and
the target's own migration history being truncated and overwritten is exactly that, reversed.

**The preservation is proven, not assumed.** Leaving a table out of the copy plan keeps it out
of the `TRUNCATE` list; it does not keep `TRUNCATE ... CASCADE` away from it, and a history
table with a foreign key into a copied table would be emptied anyway. The cascade closure is
already computed before the transaction opens, so it is checked: if a preserved table falls
inside it, the run **fails**, naming the table and the foreign-key path that reaches it. No
ORM gives that table a foreign key today, which makes it safe in practice and unverified in
principle — and a guarantee is not something to build on the second of those.

**Mirroring does not create one.** If the source has a history table the target lacks, the
mirror leaves it out and says so. Creating an empty one would produce the very crash this
protects against: the mirror has just built the schema, the ORM reads a history with nothing
in it, concludes no migration was ever applied, re-runs its baseline and hits tables that
already exist. A mirrored target has no ORM migration history and the ORM will not recognise
its schema — that is the honest outcome, and it is reported as such.

**`rowversion` is never mirrored.** SQL Server's `rowversion`/`timestamp` is a counter the
source server hands out and compares against itself; its bytes mean nothing anywhere else and
PostgreSQL cannot maintain one. Mirrored as `bytea NOT NULL` with no default it becomes a
column nothing can ever fill, so every insert an application makes fails — after the tool has
reported the migration verified. Each skipped column is named in the run.

**Copying the history anyway is an option**, for a byte-for-byte copy into a target no ORM
manages. It is off by default, it says in the interface that it overwrites rather than
permits, and every table it overwrites is reported.

## The modes that move nothing

Two of the three run modes copy no data, and none of the guarantees above about the copy
apply to them, because there is no copy.

**Verify only** writes nothing at all — no database is created, no table is truncated, no
role is provisioned. It reads both ends and compares them.

**Create database only** creates the target database, and its login when that option is on,
and stops. It reads no table from the source and writes none in the target; a target
database that already exists is left as it is. The source is still opened first, to
establish that the database the target is being named after is really there.

The honesty rule holds in both. A verification that fails is a failed run, not a warning.
And a "create database only" run that made the database but could not make the login you
asked for is reported as **failed** — the database existing is not what you asked for, and
saying otherwise would be reporting success it did not earn.

## Server management

The management panel is a different tool that happens to live in the same window, and none
of the guarantees above apply to it. They are guarantees about a migration; the panel exists
to change servers, and it does exactly what you tell it to.

**It writes to both products.** PostgreSQL and SQL Server alike, on any server saved in the
app. A server you use as a migration *source* can be administered here — that is the point
of narrowing the promise at the top of this page.

**Nothing here is transactional and nothing is reversible.** A dropped database is gone.
There is no verification step, no rollback, and no report afterwards.

What it does instead:

- **A drop says what is being lost first** — owner, size, table count, estimated rows and the
  number of open connections for a database; owned databases for a login. A confirmation
  that does not name a cost is one people learn to click through.
- **A drop only proceeds when the object's name is typed back.** The server checks this too,
  so a request that skips the browser is refused on the same terms. The name is shown next to
  the box and can be copied: the gate is there to establish that the deletion was meant, not
  to test anyone's typing.
- **System objects are refused outright.** `postgres`, `template0`, `template1`, `master`,
  `model`, `msdb`, `tempdb`, the `pg_*` roles, `sa` and the fixed server roles have no delete
  button, and the endpoint rejects them even if one is asked for directly.
- **Closing other sessions is opt-in.** A drop blocked by open connections stays blocked
  until you tick the option that forces them shut, which says that their work is rolled back.
- **A blocked delete explains itself and offers a way through.** Neither product drops a
  principal that still owns objects, so the dialog names them — which databases, how many
  objects in each — and can reassign them to another role first. The reassignment moves
  ownership and clears the privilege entries naming the old role; it drops nothing.
- **The server's own error is shown, not a summary of it.** PostgreSQL's `DETAIL` line names
  precisely what depends on a role, and it is the answer; the admin connection asks for it
  explicitly rather than letting the driver redact it. Nothing here reads rows, so that
  detail is about catalog objects and never about your data.
- **Generated passwords are shown once and stored nowhere** — same rule as the migration
  report, and the value is removed from the page when the dialog closes.

**Every identifier is quoted before it reaches a statement.** No SQL dialect accepts a bound
parameter where an identifier goes, so names from the browser are quoted for their product
(`"` doubled for PostgreSQL, `]` doubled for SQL Server) and rejected outright if they carry
a control character. The one value that cannot be quoted — a SQL Server collation name, which
sits in a keyword position — is checked against letters, digits and underscores instead.

## Stored credentials

Saved connections live in a JSON file in your local application data directory. Passwords
are encrypted with the ASP.NET Core data-protection stack, with keys scoped to your machine
— the file is not portable, and copying it to another computer yields unreadable passwords.

This protects against the file being read by something else on disk or committed by
accident. It does **not** protect against someone with your logged-in session on your
machine, because the app has to be able to decrypt them to connect. Treat the machine that
holds production connection strings accordingly.

## Generated database passwords

The password for a user created by the "one user per database" option is generated in
memory, written into the PDF report, and held only until the process exits. It is never
stored: not in the connections file, not in a log, not in a temporary file. PostgreSQL keeps
a SCRAM verifier, which cannot be reversed into the password.

The consequence is symmetric, and worth stating plainly. Nobody can recover those passwords
from the machine that ran the migration — and neither can you. If the report is lost, the
password is gone and has to be reset with `ALTER ROLE`. The PDF is a credential; handle it
as one.

## Operational prerequisites

**The target user needs enough rights.** Suspending constraint triggers
(`session_replication_role`) requires superuser on PostgreSQL. Creating the target database
requires `CREATEDB`. If either is missing the run stops with the reason rather than
half-working.

**Lock budget.** One transaction touches every table and index being loaded. On a schema of
a few hundred tables this can exceed PostgreSQL's default lock allocation and abort late
with `out of shared memory`. Raise `max_locks_per_transaction` on the target before a large
run — measured on a 507-table schema, a full load holds about 4,100 locks against a default
pool of 6,400 shared with every other connection.

**Every target table is exclusively locked until commit.** Nothing else can read them
during the run. Plan the window.
