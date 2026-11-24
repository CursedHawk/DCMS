# ADR 0032: The dcms command line and desktop agents

**Status:** accepted (2026-11-06), amended 2026-11-24 · kanban epic #616 · C1–C3 shipped, C4–C7 planned · extends [0031](0031-functions-packages-and-local-development.md)

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

## Amendment (2026-11-24): agent tokens, the assistant's limits, an allow-list

By then, #624 had given the console's assistant about 119 AI-exposed contract operations, plus 58 hand-written browser tools for the rest of the console. Those tools deliberately leave out members, roles, invitations, ownership and AI settings. The user decided three things:
- **An agent's credential is an admin-api agent token, not an identity device-flow grant.**
  - Identity's device flow cannot carry scope parameters without custom OpenIddict handlers, and identity does not know workspaces or environments; admin-api does.
  - A person mints a token with their own sign-in: `dcms agents create`, later `dcms link-agent`. The token is `dcms_agt_…` and is stored only as SHA-256 in `tenancy.agent_tokens`.
  - It covers one workspace, some environments (the ones the person holds a role in), and optionally only reads. It expires after 90 days by default.
- **Desktop agents get the console assistant's limits.** Never members, roles, invitations, ownership or AI settings, and never flipping a plugin's AI switch.
- **They respect each plugin instance's AI tools switch.** Contract calls go to `plane=ai` only.

How C2 enforces it (`src/Services/Dcms.AdminApi/Agents/AgentTokens.cs`):
- **Authentication.** A policy scheme sends a `dcms_agt_` bearer to the `AgentToken` handler and anything else to JWT.
  - The handler finds the token by hash, under platform scope, and caches the result for 30 s per process. A revoke clears the cache.
  - The principal it builds is the person (`sub`) with the agent's claims and **no role claims**, so an agent is never a SuperAdmin.
  - Permissions are still the person's own, read live.
- **Scope.** `AgentScopeMiddleware` runs before the membership check and applies `AgentScope.Refusal` (`Dcms.Shared.Security`). An agent reaches only routes marked `.Cli(...)` and not `HumanOnly`. Every other route, including any nobody has decided about yet, is an **allow-list** refusal: `403 { code: "agent_scope" }`. Within that, the token must match the workspace and environments, and a read-only token must use a route whose risk is `Read`.
- **Contracts.** The contracts proxy routes are `.Cli(..., PerOperation)`. They refuse any plane but `ai` for agents, and refuse non-Read operations for read-only tokens.
- **Tokens never manage tokens.** An agent cannot mint or revoke one.
- **Edge and audit.** The edge keeps a `dcms_agt_` bearer when there is no browser session (`BffGuard.KeepsBearer`). Audit records the person and what they acted through (`HttpCurrentActor.Key`: `agent:<name>`, or `dcms-cli`).
- **Console.** Account → Devices lists the agent tokens beside the CLI sign-ins and revokes them.

## The operation catalog (C3, 2026-11-24)

- **Marking routes.** A route offered to the command line says `.Cli(name, summary, risk, humanOnly, files)`; one deliberately not offered says `.NotForCli(reason)`. Both live in `Dcms.Shared.Security`. Plugins say `.Cli(name, summary, OpRisk)` from the SDK, which becomes `{pluginId}.{name}`.
- **The catalog.** `GET /api/admin/cli/catalog` returns, for the caller only:
  - every marked route the caller's permissions reach, with its method, path, path and query parameters, and its body's JSON Schema;
  - the contract catalog: `plane=ai` for an agent, `plane=admin` for a person.

  For agents it leaves out human-only routes, and anything but reads for read-only tokens. It is ETagged; the CLI keeps it per server, workspace and environment.
- **Wave 1** marked the console assistant's reach and beyond:
  - content and media;
  - domains and certificates;
  - API connections, jobs, audit, usage and analytics;
  - environments, with migrations;
  - sites (files, git, builds, publishing branches, search, audits, starter files), plugin repositories, and the Functions plugin's routes;
  - plugin catalog and instances, contracts, `me`, the CLI catalog itself.

  Human-only: plugin installs, updates and enables (agents use the `dcms.plugins` contract), environment links, own certificates, analytics deletion, account detachment, and agent tokens themselves. Not for the CLI: the git webhook and the Google Drive import.
- **Cross-environment routes.** Some routes act beyond the environment the request names:
  - managing environments, which names another by id (create, update, delete, locks, migrations, deploy, rollback);
  - deleting from the media library, which every environment of the workspace shares.

  These say `crossEnvironment: true`. An agent token limited to some environments is refused them and never sees them in its catalog; a token for every environment keeps them. (Found by a security review of C3: without it, a `staging` token could delete production by id.)
- **The coverage guard.** `CliCoverageTests` holds every route under a covered area to `.Cli` or `.NotForCli`. Areas join `CoveredPrefixes` wave by wave (C6).
- **The commands.** `dcms ops [search]` lists operations, `dcms op describe <name>` describes one, and `dcms op call <name> --path k=v --query k=v --body json|@file --file field=path --form k=v --instance slug --out file` calls one. Any marked route or contract operation works, with no per-operation code.

## Consequences

- **Installing needs nothing but a shell**, and the CLI cannot drift from its console. The binaries are unsigned (beyond macOS's ad-hoc signature). A browser download may meet SmartScreen or Gatekeeper; the installers avoid both. Notarization and code signing come when someone asks.
- **The admin image builds five .NET publishes**, which adds about 70 MB to it and a few minutes to its build.
- **A person's own `dcms login` stays as strong as their console session.** Agents use agent tokens, which reach only routes offered to agents; routes are opened to them wave by wave (C3, C6).
- **`packages/cli` is gone**, and with it the npm install line.
