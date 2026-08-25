# Safety

What this tool guarantees, what it does not, and why each check is there. Read the "not
guaranteed" section — it is the more useful half.

## Guaranteed

**The source is never written to.** Every statement sent to the source database is a
`SELECT`. There is no `INSERT`, `UPDATE`, `DELETE`, or DDL anywhere in the source path.
This is what makes rollback trivial: if anything goes wrong, point your application back at
the source and nothing has been lost.

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
succeeds — the missing column is reported, not treated as failure, because dropped columns
are a normal outcome of a schema change. Read the report.

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

## Stored credentials

Saved connections live in a JSON file in your local application data directory. Passwords
are encrypted with the ASP.NET Core data-protection stack, with keys scoped to your machine
— the file is not portable, and copying it to another computer yields unreadable passwords.

This protects against the file being read by something else on disk or committed by
accident. It does **not** protect against someone with your logged-in session on your
machine, because the app has to be able to decrypt them to connect. Treat the machine that
holds production connection strings accordingly.

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
