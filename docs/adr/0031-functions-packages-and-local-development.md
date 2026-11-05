# ADR 0031: Functions: external APIs, desktop development, and packages

**Status:** accepted (2026-11-05) · kanban epic #611 · extends [0029](0029-functions.md) and [0030](0030-csharp-functions.md)

## Context

The user raised four needs:
- a clear standard library;
- functions that call outside APIs;
- packages;
- developing a functions repository from a desktop editor over git.

At the time:
- The sandbox had working web globals, but the type libraries disagreed with the runtime.
- `ctx.http.fetch` existed but could not carry binary bodies or follow redirects. Declaring egress needed no permission.
- Nothing let a user clone a functions repository, sign in from a terminal, or get the SDK outside the browser.

## Decision

These were decided by the user on 2026-11-05.

### Standard library and external calls (#611 M1)

- **One set of type libraries everywhere**: `deno check`, the type-schemas pass and Monaco all use `lib: ["esnext", "webworker"]`.
- **The global `fetch` goes through the broker** (`http.fetch`), under the same egress rules. The harness deletes `Deno` and `process`.
- **Secrets stay out of the sandbox.** `ctx.secrets.ref(name)` is a placeholder that the broker fills only in the headers and URL of a fetch to a declared host. What the function reads back names the secret by its ref.
- **Redirects:**
  - Each redirect hop is re-checked against `uses.egress`.
  - Credentials and secrets are dropped when the origin (scheme, host, port) changes.
- **Deploying a function that declares `uses.egress` needs `plugin:functions:egress`.** Live deployments were vouched for it by migration.
- **What a function can use** is one page (`Docs/functions-runtime.md`), served to the IDE and the CLI. A unit test keeps its C# list equal to `CuratedAssemblies.txt`.

### Desktop development (#611 M2)

- **Repository access**:
  - The app's *edit* permission grants Forgejo write on its repository, and *read* grants read.
  - `release` takes pushes and pull-request merges from the platform's bot only.
  - A desktop user releases through the server, which merges `main` into `release` and needs *deploy*. That is how the console does it, so publishing stays the server's decision.
- **Sign-in**:
  - Identity has the OAuth device authorization grant, for a public client `dcms-cli`.
  - The person approves the code at `/connect/verify`, in a browser where they are signed in.
  - The edge passes a `dcms-cli` bearer through on the admin API routes only when no browser session is present. Every other bearer is still stripped there (ADR 0014).
  - Account settings list each signed-in command line and sign it out, which revokes its refresh tokens. Its access token lives at most 10 minutes more.
- **The `dcms` CLI** (`packages/cli`):
  - It is Node with no dependencies. It runs `login`, `link`, `sdk`, `check`, `run`, `runs`, `logs`, `secrets` and `deploy`, all over the endpoints the web IDE already uses.
  - `link` reads the git remote, and `GET /api/admin/me/repos?remote=` names the workspace, environment and app.
- **The C# SDK package is made by the server, not published by CI.**
  - `/_functions/sdk.nupkg` builds `Dcms.Functions.Sdk` from the running server's `Dcms.Functions.dll`. Its version is the content hash.
  - The package adds dcms-csc's usings, C# 14 and nullable, compiles `.dcms/Sdk.g.cs`, and removes every framework reference outside `CuratedAssemblies.txt`. That is the same filter dcms-csc applies to the same targeting pack.
  - `dcms sdk` saves it into the clone's `.dcms/packages`, a local NuGet source.
  - **Rejected:** a CI-published package on the GitLab registry. It needs a token for a private project, and it can drift from the server a clone talks to.

### Packages (#611 M3, to come)

**Any public npm or NuGet package**, declared and locked. The platform fetches exact versions, checks their integrity, never runs install scripts, and caches them. The details, and the spike's results, will be recorded here when that milestone lands.

## Consequences

- The sandbox gains no permission. Everything a function reaches still goes through a host call.
- A desktop build is a fast first pass. `dcms check` (the server's build) stays authoritative.
- A release branch that was locked before this change has no merge whitelist until it is locked again. Plugin repositories are re-locked whenever an editor's access is reconciled. Site repositories get it the next time their environment's release policy is applied.
