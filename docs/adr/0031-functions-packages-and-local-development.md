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

### Packages (#611 M3)

**Any public npm or NuGet package**, declared and locked. The platform fetches exact versions, checks their integrity, never runs install scripts, and caches them.

#### Spike P0 (2026-11-05): go for both languages

Measured with the runner's own Deno 2.5.4 and esbuild 0.28.2, under the production limits for a C# run (64 MB V8 heap, 768 MB `RLIMIT_DATA`, `--liftoff-only`).

**npm** (zod 4, date-fns 4, openai 7, stripe 23):
- `deno check` resolves them offline from `node_modules` with `"nodeModulesDir": "manual"`. It must run without `--no-npm`, which turns package resolution off altogether.
- esbuild bundles them with `--platform=neutral --main-fields=module,main --conditions=worker,browser,import`.
- They run in the permission-less sandbox through the `fetch` shim. That includes the OpenAI and Stripe clients, whose `worker` builds use `fetch`.
- A Node-only package (nodemailer) fails at bundle with `Could not resolve "node:…"`. The build reports that as "needs Node.js".
- Bundles grow: zod + date-fns + openai is 1.7 MB. `MaxBundleChars` (2 M) has to become configurable.

**NuGet** (Newtonsoft.Json 13, NodaTime 3.2, Stripe.net 48):
- Package DLLs load before the function's (`Assembly.Load(bytes)` and an `AssemblyResolve` hook) and run. NodaTime runs with its time-zone database. The run peaks at about 175 MB RSS, against roughly 165 MB with no packages.
- **The 64 MB heap holds only if the start message's base64 is decoded with `Uint8Array.fromBase64`.** The harness's old `Uint8Array.from(atob(…), fn)` ran the V8 heap out with about 5 MB of packages.
- **Every assembly the runtime ships must be rooted whole.** An assembly that is shipped but trimmed has method bodies removed, and calling one crashes Mono (`transform.c: should not be reached`). That happened to Newtonsoft on any serialization. The curated set grows by about 45 assemblies (XML, `System.Linq.Expressions`, `System.Net.Http`, `netstandard`, …), and the runtime from 16 to 19 MB of wasm.
- **`HttpClient` works through the same `fetch` shim**, provided the shim is installed before .NET's modules load, and Deno runs with `--location`: the runtime probes for streaming with `new Request("")`.
- **Some packages need what browser-wasm lacks**, and fail at run time with `PlatformNotSupportedException`. Stripe.net's default HTTP client reads `ServicePointManager.SecurityProtocol`. That cannot be seen statically in general, so it is documented and not checked.

## Consequences

- The sandbox gains no permission. Everything a function reaches still goes through a host call.
- A desktop build is a fast first pass. `dcms check` (the server's build) stays authoritative.
- A release branch that was locked before this change has no merge whitelist until it is locked again. Plugin repositories are re-locked whenever an editor's access is reconciled. Site repositories get it the next time their environment's release policy is applied.
