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

**The name box offers the target server's existing databases.** Migrating into a database
that is already there is a normal thing to want, and picking it from the list beats
recalling its spelling — a name typed one letter off is a new, empty database rather than
an error. **Leave a row's box empty and it takes the source database's name**, which is the
commonest answer and now needs no typing.

Each row says what its name is on the target right now:

| Badge | Meaning |
|---|---|
| `var` | The database is already on the server. It is used as-is; nothing is created |
| `oluşturulacak` | Not there yet. It will be created with the collation above |
| `yok` | Not there, and this run will not create it — only **verify only** shows this |

A target that does not exist is created with the collation you specify. One that already
exists is used as-is and its collation is verified, not changed.

**Collation** defaults to ICU `und` (the root locale). This is the safe default for most
data: it folds `I`/`i` the way applications usually expect and orders accented characters
next to their base letters. Set it to something else only if you know what you are choosing
— and note that a database's collation cannot be changed after it is created, so getting it
wrong here means recreating the database later. Leave the field empty to skip the check
entirely.

## 4 · Options

Everything here is off by default. Three things happen on **every migration** regardless of
what you tick, and they are worth knowing before the options make sense. The other two run
modes below move no data at all, so none of the three applies to them:

- **Every table being copied is emptied first**, with `TRUNCATE … RESTART IDENTITY
  CASCADE`. `CASCADE` also empties tables that depend on those and have no source
  counterpart, and those stay empty — the run lists which ones it emptied.
- **The truncate, the copy, the sequence fixup and the verification share one
  transaction.** If the verification fails nothing is written and the target is exactly
  what it was.
- **Constraint triggers are suspended for the duration of the copy** — which needs
  superuser on the target — and every foreign key is re-checked before the commit.

The options below are grouped on the page the same way they are grouped here.

### Run mode

Three exclusive choices. Everything under them applies to whichever one is selected, and an
option the chosen mode would ignore is locked on the page with the reason — the engine was
always going to ignore it, and showing it as available promised work that never happened.

**Migrate and verify** (the default) — the full pipeline described above.

**Verify only** — no copy. Nothing is truncated, no target database is created, and no role
is provisioned. It compares row counts and checks foreign keys against a target that was
migrated earlier, which is how you confirm a previous run.

Because a verification has to leave the target exactly as it found it, this disables three
options the engine would ignore anyway: **mirror missing tables** and **one user per
database**, both of which write to the target, and **allow schema risk**, whose pre-flight
only runs when there is a copy to gate. The collation check still runs.

**Create database only** — creates the target database and stops. No table is read from the
source and none is written in the target; nothing is truncated. A database that is already
there is left exactly as it is.

The source database is still opened before anything is created. It is the only thing the
source is used for in this mode, and it is worth the round trip: a target named after a
source that is not there is a typo, not a plan.

**One user per database** applies here too, and this is usually the point of the mode —
standing up an empty database with its own login, ready for an ORM's migrations to run
against it. If you asked for a login and it could not be created, the database is reported
as **failed** even though it exists, because half of what you asked for did not happen. The
password still appears only in the PDF.

This mode locks everything about tables — mirror, the source-only permission and the schema
risk gate — because no table is compared. It also locks **allow collation mismatch**: there
is no existing collation to compare against. The collation field in step 3 still matters,
though, because it is what the new database is created with.

### Missing tables

**Mirror missing tables** — creates the tables the target does not have, from the source
schema: columns, NOT NULL, identity columns, primary keys and foreign keys. Defaults,
indexes and check constraints are **not** copied, so this is a fast way to stand up a copy
of a database, not a substitute for running your ORM's migrations against a target the
application will then own. A source column whose type has no PostgreSQL mapping stops the
run before it starts, with the column named.

**Allow tables missing from target** — the source has tables the target does not, or holds
base tables in a schema other than `dbo`, which this tool does not read at all. Their data
will not be migrated. Read the list before you tick this: a table you still need looks
exactly the same as one that was dropped on purpose. With mirroring on, this permission has
nothing left to permit — the missing tables get created — and the page says so.

### ORM migration history

An ORM's migration-history table — `__EFMigrationsHistory` and the equivalents for Django,
Rails, Flyway and Drizzle — is **left out of the migration entirely**, by default. It is
neither emptied nor filled, and the target's own rows stay as they are.

Those rows say which migrations *this* database has had applied, and that answer belongs to
the target's provider. Copying the source's over them makes the ORM think its baseline was
never applied; it re-runs it and fails on tables that already exist. The run reports which
history tables it found and what it did with each.

**Copy ORM migration history** turns that off. It is not one of the relaxed checks below — it
does not let anything through, it overwrites correct target state — so it has its own heading
here and its own label in the interface. Use it for a byte-for-byte copy into a target that no
ORM manages.

Two related behaviours, both automatic:

- **Mirroring never creates a history table.** A mirrored target has no ORM migration history,
  the ORM will not recognise its schema, and the run says so rather than leaving a convincing
  empty table behind.
- **`rowversion` is never mirrored.** It is generated by the source server and cannot be
  maintained in PostgreSQL; mirrored as `bytea NOT NULL` it would be a column no application
  could ever fill.

### Relaxed checks

Both of these are marked **kapı açar** on the page: they let through something the tool
would otherwise stop on.

**Allow schema risk** — the pre-flight found NULLs headed for NOT NULL columns, or values
longer than the target column allows. Ticking this trades an early, clear stop for a
failure partway through the copy. The transaction still rolls back, so what you give up is
the clean stop, not the data.

**Allow collation mismatch** — the target's collation is not what you specified. The
migration will work; search and sort behaviour will differ from what you expected, silently.
Leaving the collation field in step 3 empty skips the check altogether, which leaves this
option with nothing to relax — the page disables it and says why.

### Target users

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

### What the run tells you about the schema

Three things a row count cannot show. All three appear in the log and in the PDF report:

- **Invented values.** A target column that is `NOT NULL`, has no default and is absent from
  the source is filled with `0`, `false` or `''` in every row. The run names the table, the
  column, the type and the exact value. Nothing about the data afterwards will tell you
  which zeros were real.
- **Columns that did not travel.** A source column with no target counterpart is named per
  table. This is normal after a schema change and is not treated as a failure.
- **Tables CASCADE will empty.** The truncate reaches every target table referencing one
  being copied. Those outside the copy set are listed before the transaction opens, because
  afterwards is not a decision.

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

## Management

The **Yönetim** tab is a second screen on the same servers, and a different job: it creates,
alters and drops databases and logins. Nothing on it runs in a transaction and nothing on it
can be undone — see [SAFETY.md](SAFETY.md#server-management) before the first drop.

Pick any saved server, PostgreSQL or SQL Server. The panel adapts to which one it is rather
than showing controls the product does not have.

### Databases

The table lists every database with its owner, collation, size and open connection count.
System databases are hidden behind the **Sistem** switch and can never be dropped.

| Action | What it does |
|---|---|
| **Yeni veritabanı** | Creates one with a collation and an owner. PostgreSQL wants an ICU locale (`und`); SQL Server wants a collation name |
| **Yetkiler** | Who can do what on this database — see below |
| **Sahip** | Hands the database, and everything in its `public` schema, to another role |
| **Sil** | Drops it, after showing what is in it and asking for the name |

### Privileges

Four levels, because the four are what an operator reaches for and a panel that can express
every `GRANT` is a worse tool than `psql`:

| Level | PostgreSQL | SQL Server |
|---|---|---|
| **yetki yok** | Every grant this tool made, revoked | The database user is dropped |
| **bağlanabilir** | `CONNECT` on the database | A user exists, in no role |
| **okur ve yazar** | Plus every privilege on the `public` schema, its tables and sequences, and on ones created later | Plus `db_datareader` and `db_datawriter` |
| **sahibi** | Owns the database and its objects | Owns the database — which in SQL Server means being `dbo` in it |

Superusers are left out of the list. They hold every privilege implicitly, so listing them
would put every superuser on every database and say nothing about what anybody granted.

PostgreSQL grants `CONNECT` to `PUBLIC` by default, which means a role with no grant at all
can still connect. The **PUBLIC bağlanabilsin** switch is that grant; turning it off leaves
only the roles you named. SQL Server has no equivalent, so the switch is not shown there.

### Users and roles

PostgreSQL has no separate user object: a role that can log in **is** a user, and one that
cannot is a group. Both are in the list, and the **Tür** column says which is which. On SQL
Server the same column separates logins from fixed server roles.

| Action | What it does |
|---|---|
| **Yeni** | Creates a login. Leave the password empty and a 24-character one is generated and shown **once** — it is stored nowhere |
| **Düzenle** | Server-wide powers, group membership, and a password reset |
| **Sil** | Drops it, after listing what it owns — and offering to hand that over first |

Neither product will drop a principal that still owns something, and the roles this tool
creates own a great deal: a per-database login owns its database and every table in it. So
the delete dialog names what is in the way — which databases, and how many objects in each —
and offers to reassign it all to another role before dropping.

**The hand-over destroys nothing.** It changes owners and then clears the privilege entries
that named the old role, which is the other half of what blocks the drop. Your tables, and
the rows in them, are untouched.

If you decline the hand-over the drop is still attempted, and the server's own refusal is
shown verbatim — including the `DETAIL` line naming exactly what depends on the role.

The power switches mean the same thing in different mechanisms. PostgreSQL has per-role
flags (`CREATEDB`, `CREATEROLE`, `SUPERUSER`); SQL Server has fixed server roles
(`dbcreator`, `securityadmin`, `sysadmin`). The panel labels each with the name that server
uses, and does not offer the three SQL Server ones twice by listing them under membership as
well.

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
