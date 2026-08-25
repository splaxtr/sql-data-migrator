# Security Policy

This tool holds credentials for production databases and is trusted to move data between
them. Both of those make it worth reporting problems in carefully.

## Supported versions

There are no tagged releases yet. The `main` branch is the only supported version, and fixes
land there. Once releases begin, this section will list which ones still receive fixes.

| Version | Supported |
|---|---|
| `main` | Yes |
| Anything else | No |

## Reporting a vulnerability

**Please do not open a public issue for a security problem.**

Report it privately through GitHub:

1. Go to the [Security tab](https://github.com/splaxtr/sql-data-migrator/security/advisories/new).
2. Choose **Report a vulnerability**.

That opens a private advisory visible only to you and the maintainer. If you would rather use
email, **ahmetsplaxtr@gmail.com** reaches the same person.

Please include:

- What an attacker can do, and what they need in order to do it — local access, a malicious
  database server, a crafted table or column name, a network position.
- The versions involved: this tool's commit, the .NET SDK, and the source and target
  database products.
- Steps to reproduce. A minimal schema that triggers it is worth more than a description.
- Whether you have already disclosed it anywhere.

## What to expect

This is a small project with a single maintainer, so the honest version rather than a
service-level promise:

- **Acknowledgement within 7 days.** If you have not heard back by then, assume the message
  was missed and send a follow-up.
- **An assessment within 30 days** — whether it is a vulnerability, how serious, and a rough
  plan.
- **Credit in the advisory and the release notes**, unless you would rather stay anonymous.
  Say which you prefer.
- **Coordinated disclosure.** A fix is published before the details are, and you will be told
  before anything is made public.

There is no bug bounty.

## In scope

Anything that lets someone read, alter or exfiltrate what this tool is trusted with:

- Recovering stored connection passwords without the rights the data-protection keys are
  supposed to require — from the stored file, from memory, or from the machine's key ring.
- Reaching the local API or the stored connections from another machine, another user
  account, or a web page in the user's browser. The app binds to localhost; anything that
  defeats that boundary is a vulnerability.
- SQL injection through a value that is not obviously code — a database name, a table or
  column name, a collation, or anything else read from a server and later put into a
  statement.
- A malicious or compromised **source** server causing writes, code execution, or damage on
  the machine running the tool or on the target database.
- Credentials, connection strings or row data appearing in logs, progress output, error
  messages or crash dumps.
- Anything that lets a migration **pass verification while the data is wrong**. Verification
  is the guarantee the tool is built around; a way to defeat it silently is treated as a
  security issue, not just a bug.
- Vulnerable dependencies, when you can show the vulnerable path is actually reachable here.

## Known, documented, and accepted

These are design decisions, not oversights. They are already written down in
[docs/SAFETY.md](docs/SAFETY.md), so a report about one will be closed with a pointer here —
unless you can show it is worse than described, in which case it is very much worth sending.

- **The local API has no authentication.** It binds to localhost, and anything running as
  your user on your machine can reach it and use the saved connections. The security boundary
  is the machine and the user account, deliberately: the app is a local tool and treating it
  as multi-user would mean pretending it is a service.
- **Saved passwords are decryptable by you, on your machine.** They are encrypted with the
  ASP.NET Core data-protection stack, keyed to the machine, which protects the file at rest
  and against accidental commits. It cannot protect against someone already logged in as you,
  because the app has to decrypt them in order to connect.
- **The tool acts with the database rights you give it.** It truncates every target table it
  is about to load and needs superuser on PostgreSQL to suspend constraint triggers. That is
  the documented cost of the guarantees it makes. Give it a target you are willing to have
  rewritten.
- **The source's data is not validated as safe.** Content is moved faithfully, including
  content that is dangerous to whatever reads it later. Moving data is not sanitising it.

## Not a security issue

- A migration that fails or stops. That is the tool working — open a normal issue.
- Wrong data in the target that was already wrong in the source. See
  [docs/SAFETY.md](docs/SAFETY.md#not-guaranteed).
- Findings from an automated scanner with no reachable path in this codebase.
