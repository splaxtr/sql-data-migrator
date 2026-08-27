# Usage

## Before you start

**Stop writes to the source.** The copy reads table by table. A source that is still being
written to produces a target where one table reflects a later moment than another — an
invoice without its payment. This is a cutover tool.

**Create the target schema.** This tool moves data into tables that already exist. Run your
ORM's migrations, or a schema tool, against the target first. If the target has no matching
tables the run stops with "no tables to copy" rather than pretending to succeed. The
**mirror** option can build the missing tables from the source instead — see
[4 · Options](#4--options) for what it does and does not copy.

**Check the target's rights.** Suspending constraint triggers needs superuser on
PostgreSQL; creating the target database needs `CREATEDB`; creating a user per database and
handing it ownership needs superuser or `CREATEROLE`.

**Raise the lock budget for large schemas.** One transaction locks every table it loads. On
a few hundred tables this can exceed PostgreSQL's default and abort late with `out of
shared memory`. Set `max_locks_per_transaction` to 512 or more on the target.

## 1 · Connections

Add each server once. Everything is stored on your own machine (see
[SAFETY.md](SAFETY.md#stored-credentials)); nothing is sent anywhere.

| Field | Notes |
|---|---|
| Name | Whatever you will recognise later — "Prod SQL", "Staging PG" |
| Kind | SQL Server is a source, PostgreSQL is a target. The port default follows the kind |
| Host / Port | Reachable from the machine running the app, not from the database server |
| User / Password | The password is encrypted before it touches disk |

Editing a saved server and leaving the password blank keeps the stored one — you do not have
to retype it.

## 2 · Source

Pick the server, then tick the databases to move. The list is read from the server at that
moment, so it is always current, and the search box filters it as you type. **Select
visible** ticks everything the filter currently shows, which is how you take twenty
databases sharing a prefix without twenty clicks.

Ticking more than one runs them **one after another, in the order they are listed**. Each is
a separate migration with its own verification, so one failing does not stop the others —
the run continues and the summary tells you how many of each you got.

## 3 · Target

Pick the PostgreSQL server. Target names come from the **name pattern**, where `{db}` stands
for the source database name:

| Pattern | `Sales` becomes |
|---|---|
| `{db}` (default) | `Sales` |
| `{db}_pg` | `Sales_pg` |
| `new_{db}` | `new_Sales` |

Any individual name can still be typed over in the list; a name you edit by hand stops
following the pattern, so changing the pattern afterwards leaves it alone.

A target that does not exist is created with the collation you specify. One that already
exists is used as-is and its collation is verified, not changed.

**Collation** defaults to ICU `und` (the root locale). This is the safe default for most
data: it folds `I`/`i` the way applications usually expect and orders accented characters
next to their base letters. Set it to something else only if you know what you are choosing
— and note that a database's collation cannot be changed after it is created, so getting it
wrong here means recreating the database later. Leave the field empty to skip the check
entirely.

## 4 · Options

Everything here is off by default. Most of them let through something the tool would
otherwise stop on, and each is occasionally the right call:

**Mirror missing tables** — creates the tables the target does not have, from the source
schema: columns, NOT NULL, identity columns, primary keys and foreign keys. Defaults,
indexes and check constraints are **not** copied, so this is a fast way to stand up a copy
of a database, not a substitute for running your ORM's migrations against a target the
application will then own.

**Allow tables missing from target** — the source has tables the target does not. Their data
will not be migrated. Read the list before you tick this: a table you still need looks
exactly the same as one that was dropped on purpose.

**Allow schema risk** — the pre-flight found NULLs headed for NOT NULL columns, or values
longer than the target column allows. Ticking this trades an early, clear stop for a
failure partway through the copy.

**Allow collation mismatch** — the target's collation is not what you specified. The
migration will work; search and sort behaviour will differ from what you expected, silently.

**Verify only** — no copy. Compares row counts and checks foreign keys against a target
that was migrated earlier. Useful for confirming a previous run. It disables user creation,
because a verification must leave the target exactly as it found it.

**One user per database** — after a database migrates, a PostgreSQL login role is created
for it with a 24-character random password. The name comes from a pattern, `{db}_user` by
default, lower-cased and stripped to letters, digits and underscores.

The role becomes the **owner** of the database and of everything in its public schema, and
is granted privileges on top, so it can read, write, and alter its own tables. If this tool
created the database in this run, `CONNECT` is also revoked from `PUBLIC` — that database is
then reachable only by its own role. A database that already existed is left open, because
other roles may be relying on the default.

A role that already exists is reused and **its password is not rotated**; something is
probably already using it. The report says so instead of printing a password.

Creating roles and transferring ownership needs superuser, or at least `CREATEROLE`. Without
it the role is still created and granted, and the run warns that ownership could not be
transferred.

## 5 · Running

Progress streams as it happens: which database, which table, how many rows, what was
verified. The last line is the verdict, and it is honest — a success line means the rows
arrived, the counts match and no foreign key is orphaned. In a batch it also tells you how
many databases succeeded and how many did not.

## 6 · The report

When a run finishes, **Download PDF report** appears. It lists every database, its target
name, the rows moved, how long it took, and why anything that failed did — plus, when you
asked for logins, each database's username and password.

That PDF is the **only** place those passwords exist. They are generated in memory, sent to
the browser once, and never written to disk by this app — so there is nothing to steal from
the machine that ran the migration, and nothing to recover if you lose the file. If you lose
it, reset the password in PostgreSQL:

```sql
ALTER ROLE sales_user PASSWORD 'a-new-one';
```

Treat the file as the credential it is: store it somewhere safe, do not mail it around, and
delete it when the credentials have been handed over.

## When a run stops

Read the last error, not the first. The pipeline stops at the earliest problem, so the final
message is the one to act on.

| Message | What it means | What to do |
|---|---|---|
| `Kopyalanacak tablo bulunamadı` | Source and target share no tables | Wrong database selected, or the target schema was never created |
| `Kaynak tablosu 'X' hedefte yok` | The source has a table the target does not | Decide whether X still matters. If not, tick the first option |
| `hedef NOT NULL ama kaynakta N NULL var` | Source rows would violate a target constraint | Fix the data or the schema. Ticking "allow schema risk" only moves the failure later |
| `hedef varchar(N) ama kaynakta en uzun değer M karakter` | Values will not fit | Widen the target column |
| `Hedef collation 'X' — beklenen 'Y'` | Wrong collation | Recreate the target database with the right collation. This cannot be altered in place |
| `Yetim satır: A → B` | The source contains rows whose parent is missing | Clean the source, or drop the constraint deliberately. **Nothing was written** |
| `Hiç satır kopyalanmadı` | The source is empty, or the wrong one | Check the source selection |

In every one of those cases the target is unchanged. There is no half-migrated state to
clean up, and re-running after a fix is safe.
