# `.claude/`

Configuration for [Claude Code](https://claude.com/claude-code), checked in so that everyone
working on this repository — human or agent — gets the same conventions and the same
verification tools.

Nothing in here is required to build or run the application. Delete the folder and the
project still works; you only lose the guardrails.

| Path | What it is |
|---|---|
| [`../CLAUDE.md`](../CLAUDE.md) | The project rules an agent has to know: build flags, the English-code/Turkish-UI split, message codes as a wire format, what the engine is never allowed to do |
| `settings.json` | Shared, non-secret settings — an allowlist of the commands this project runs constantly, so they stop prompting |
| `skills/verify-ui/` | Audits the running web UI for contrast, wiring, focus and layout, in both themes |

## Secrets

**There are none in this folder, and none may be added.** It is a public repository.

- `settings.local.json` is personal and git-ignored. Put machine-specific paths and
  permissions there.
- API keys, connection strings and tokens belong in environment variables, never in a file
  under version control.
- The application's own credentials are not stored here either — saved server passwords are
  encrypted per-machine outside the repository, and generated database passwords are never
  written to disk at all. See [docs/SAFETY.md](../docs/SAFETY.md).

## MCP servers

**This project needs none.** Everything the code requires is reachable through the .NET SDK,
`docker` and `gh`, so there is deliberately no `.mcp.json` here — an empty or speculative one
would be clutter that later gets copied somewhere it does not belong.

If you do wire one up (a database server is the obvious candidate for this project), the
project-scoped file is `.mcp.json` at the repository root, and the rule is that it holds
*references* to credentials, never credentials:

```jsonc
{
  "mcpServers": {
    "example": {
      "command": "npx",
      "args": ["-y", "<the-server-package>"],
      // Expanded from your shell at launch. The literal value never enters the repository.
      "env": { "EXAMPLE_URL": "${EXAMPLE_URL}" }
    }
  }
}
```

`.mcp.json` is git-ignored here on purpose: an MCP setup tends to be specific to one
machine's tooling, and the cost of a mistaken commit is a leaked credential.

## Where the design came from

The interface palette — `#88BDBC`, `#254E58`, `#112D32`, `#4F4A41`, `#6E6658` — was chosen by
the maintainer and then mapped to interface roles by contrast measurement rather than by eye.
The reasoning is recorded where it is needed, in the token block at the top of
[`style.css`](../src/Migrator.App/wwwroot/style.css): which colour may carry text, which may
only be a surface, and why the status colours sit outside the palette.

The general direction (a Swiss/minimal treatment for a professional tool, 4.5:1 contrast,
visible focus, `prefers-reduced-motion`) came from a design-system skill run locally during
the redesign. Its conclusions live in the CSS and in `skills/verify-ui/`, which enforces
them on every subsequent change — that is the durable part, and it is why the skill is
committed and the one-off design session is not.
