# ADR 0032: The dcms command line and desktop agents

**Status:** accepted (2026-11-06) · kanban epic #616 · C1 shipped, C2–C7 planned · extends [0031](0031-functions-packages-and-local-development.md)

## Context

ADR 0031 gave Functions a desktop CLI: about 500 lines of Node, installed with npm from the console. Two problems followed.
- **It needed Node.** Everyone using it had to install Node 20 and npm first.
- **It did one job.** It could develop a Functions app and nothing else.

The goal now is broader. `dcms` should become the desktop alternative to the console, for people and for their own AI agents (Claude Code, Cursor, Codex). An agent should be able to:
- create and pull sites, write them and deploy them;
- set up plugins, upload media and configure Dynamic Apps;
- work across environments, or be held to one.

The agent uses its own model and subscription, not DCMS's AI endpoints. Its own settings ("ask first", "full auto") decide when it asks a person. The console and its built-in agents stay for smaller edits.

What the platform had to start from:
- **A `dcms-cli` token carries the person's full `dcms.admin` authority**, for every workspace and environment. There are no scoped tokens and no personal access tokens.
- **admin-api has no API description.** It has about 200 routes plus plugin routes, and their responses are anonymous objects.
- **What is machine-readable** is limited to:
  - the endpoint metadata: permission, audit and environment-managed markers, all readable from `EndpointDataSource`;
  - the plugin contract catalog (`/api/admin/contracts?plane=ai`), whose operations carry JSON Schemas and an `OpRisk`.

## Decision

1. **`dcms` is a .NET program, `src/Tools/Dcms.Cli`.**
   - **Build:** System.CommandLine, and published self-contained as one trimmed file for each of linux-x64, linux-arm64, osx-x64, osx-arm64 and win-x64 (about 12–14 MB). The macOS builds are ad-hoc signed by the SDK, so Apple Silicon runs them.
   - **Distribution:** the console image serves the files, with `SHA256SUMS`, `version`, and two installers:
     - Linux/macOS: `curl -fsSL https://<console>/downloads/install.sh | sh`
     - Windows: `irm https://<console>/downloads/install.ps1 | iex`

     nginx writes the console's own origin into the installers on each request. The edge keeps the public Host and sets `X-Forwarded-Proto`.
   - `dcms version` compares the CLI's version with the console's, and `dcms update` replaces the CLI with the console's build after checking its hash. The CLI is always the one its server ships.
   - **Rejected:**
     - Node single-executable apps, Deno or Bun compiled binaries: 60–110 MB each, and still JavaScript, a second language to grow in.
     - A dotnet tool: it needs .NET installed and a public feed.
     - Homebrew, winget or Scoop: one more thing to publish on every release. Later, if asked.
2. **Configuration and sign-in:**
   - **Where config lives:** in the OS's own folder (`$XDG_CONFIG_HOME/dcms`, `~/Library/Application Support/dcms`, `%APPDATA%\dcms`), or `$DCMS_CONFIG_DIR` if set. The Node CLI's `~/.config/dcms` is taken over on first use, in the same file format.
   - **Private files:** written whole, with mode 0700 for the folder and 0600 for the files on Unix.
   - **Refresh:** identity rotates refresh tokens, so refreshing happens under a machine-wide file lock.
   - **The Node CLI's safety rules are kept:**
     - no symbolic links followed in a clone;
     - the link is kept with the sign-in and tied to the clone's remote;
     - only source files are sent to the server.
3. **The plan from here (#616)**:
   - **C2: agent grants.**
     - `dcms link-agent` runs its own device approval for a named grant: one workspace, chosen environments, optionally read-only.
     - The token carries the claims `dcms_grant`, `dcms_tenant`, `dcms_env` and `dcms_ro`. One shared middleware enforces them in admin-api and on the hub; the account API refuses grant tokens.
     - Audit names the agent, and Devices lists and revokes grants.
   - **C3: an operation catalog.**
     - Routes are annotated with `.Cli(name, summary, risk)` or `.NotForCli(reason)`. A coverage test holds every route to one or the other.
     - `GET /api/admin/cli/catalog` merges those routes with the contract catalog, filtered by permission and grant.
     - `dcms ops` and `dcms op call` work from it, so a newly annotated route needs no CLI release.
     - **Rejected:** OpenAPI for admin-api. Its untyped responses would make the document weak, and it carries no risk or grant filtering. The catalog can emit OpenAPI later.
   - **C4: `dcms mcp`**, a stdio MCP server.
     - About 30 core tools, plus `dcms_find`, `dcms_describe` and `dcms_call` for the long tail.
     - Tool annotations come from risk, so the agent's own modes decide when to ask.
     - `link-agent` writes the agent's MCP configuration, and a skill served by the console.
   - **C5: workspace sync.** `pull`, `push`, `status`, `diff`, `commit` and `publish` over the IDE's draft routes with base hashes, with per-file conflict resolution. No git credentials are needed.
   - **C6: coverage waves.** One area at a time (plugins, Dynamic Apps, environments, content, people, operations) until the coverage test's exceptions list is empty.
   - **C7: hardening.** Per-grant rate limits, telemetry, docs, and a scripted MCP-client end-to-end test.

## Consequences

- **Installing needs nothing but a shell**, and the CLI cannot drift from its console. The binaries are unsigned (beyond macOS's ad-hoc signature). A browser download may meet SmartScreen or Gatekeeper; the installers avoid both. Notarization and code signing come when someone asks.
- **The admin image builds five .NET publishes**, which adds about 70 MB to it and a few minutes to its build.
- **Until C2, a CLI token is as strong as a console session.** Agents should not be linked to a workspace before C2 ships.
- **`packages/cli` is gone**, and with it the npm install line.
