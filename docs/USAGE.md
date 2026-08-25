# Usage

## Before you start

**Stop writes to the source.** The copy reads table by table. A source that is still being
written to produces a target where one table reflects a later moment than another — an
invoice without its payment. This is a cutover tool.

**Create the target schema.** This tool moves data into tables that already exist. Run your
ORM's migrations, or a schema tool, against the target first. If the target has no matching
tables the run stops with "no tables to copy" rather than pretending to succeed.

**Check the target's rights.** Suspending constraint triggers needs superuser on
PostgreSQL; creating the target database needs `CREATEDB`.

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

Pick the server, then the database. The list is read from the server at that moment, so it
is always current, and the field filters as you type. Two hundred databases stay usable.

## 3 · Target

Pick the PostgreSQL server. The database name is pre-filled with the source name, which is
almost always what you want. From there:

- **Accept it** — if it does not exist, it is created with the collation you specify.
- **Pick an existing one** from the list — it is used as-is and its collation is verified,
  not changed.
- **Type anything else** — same as accepting, with your name.

**Collation** defaults to ICU `und` (the root locale). This is the safe default for most
data: it folds `I`/`i` the way applications usually expect and orders accented characters
next to their base letters. Set it to something else only if you know what you are choosing
— and note that a database's collation cannot be changed after it is created, so getting it
wrong here means recreating the database later. Leave the field empty to skip the check
entirely.

## 4 · Options

Everything here is off by default. Each one lets through something the tool would otherwise
stop on, and each is occasionally the right call:

**Allow tables missing from target** — the source has tables the target does not. Their data
will not be migrated. Read the list before you tick this: a table you still need looks
exactly the same as one that was dropped on purpose.

**Allow schema risk** — the pre-flight found NULLs headed for NOT NULL columns, or values
longer than the target column allows. Ticking this trades an early, clear stop for a
failure partway through the copy.

**Allow collation mismatch** — the target's collation is not what you specified. The
migration will work; search and sort behaviour will differ from what you expected, silently.

**Verify only** — no copy. Compares row counts and checks foreign keys against a target
that was migrated earlier. Useful for confirming a previous run.

## 5 · Running

Progress streams as it happens: which table, how many rows, what was verified. The last
line is the verdict, and it is honest — a success line means the rows arrived, the counts
match and no foreign key is orphaned.

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
