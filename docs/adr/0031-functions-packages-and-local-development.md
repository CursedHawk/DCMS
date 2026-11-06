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

- **The web IDE and a desktop clone side by side (D5, M4):**
  - A plugin repository has the live channel sites have: the site hub takes `repoId`, for those who hold the binding's *read*.
  - Its commits, merges and draft writes are announced there, and so is every push the webhook reports, a desktop one included.
  - An open editor hears "main moved", with the reload banner.
  - A commit onto a branch that moved resolves each conflicting file on its own (`resolutions`: mine, the branch's, or a hand merge in `ConflictResolver`), as a merge already did. The resolutions must cover exactly the conflicts there are when it commits, or it is a 409 again.

### Packages (#611 M3)

**Any public npm or NuGet package**, declared and locked. The platform fetches exact versions, checks their integrity, never runs install scripts, and caches them.

#### npm, as built (P1–P3)

- **The lock is the whole truth.**
  - `package.json`'s `dependencies` must equal what `package-lock.json` (v2/v3) records for the root, or the build fails and names the packages.
  - Every non-dev entry must come from `registry.npmjs.org` and carry a sha256/384/512 integrity. Platform-specific optional binaries are skipped.
  - At most 300 packages, 25 MB each and 60 MB per build.
- **admin-api fetches; script-runner stays offline.**
  - `PackageStore` fetches each tarball by its exact URL through `PublicEgress`, checks the integrity before anything else, and caches it in the `dcms-packages` bucket under its hash, shared by every tenant.
  - The build request carries the archives. script-runner unpacks them into `node_modules/`: regular files only, no links, nothing outside the package folder, at most 300 MB and 30,000 files.
- **The build:**
  - A bare import must name a dependency or a file of one.
  - `deno.json` gets `"nodeModulesDir": "manual"`, and `deno check`/`deno info` run without `--no-npm`.
  - A package's `node:` imports are left for esbuild to judge. esbuild bundles the package's `worker`/`browser`/ES-module build, and a package that still reaches Node.js is reported once, naming the package and the modules it needs.

#### NuGet, as built (P4)

- **Declaring:** `Functions.csproj`'s `PackageReference`s, locked by `packages.lock.json`. The direct entries must match the project, or the build fails and names them. `Dcms.Functions.Sdk` is the server's own and is never fetched.
- **Fetching:** each locked package comes from nuget.org's flat container by id and version, through the same `PackageStore`, checked by its `contentHash` (sha512).
- **Building:**
  - script-runner takes each package's best `lib/` folder: net10.0 down to netstandard1.0, never .NET Framework.
  - dcms-csc compiles against those DLLs and reads their metadata without loading them. Any assembly reference outside the curated set, the SDK and the packages is refused, and so is any P/Invoke.
  - The assemblies go with the deployment as `@nuget/{name}.dll` bundles, at most 16 MB: the run's start message carries them, and the function's V8 heap parses it. The start message may be 80 MB.
- **Running:** the harness loads them (`Entry.Load`, plus an `AssemblyResolve` hook) before the function's assembly, and a kept process loads them once.

#### The IDE's package manager (P5)

- **Endpoints:**
  - The Functions view's **Packages** section searches npm or NuGet (`/_functions/packages/search`, proxied).
  - Adding or removing a package (`POST /_functions/packages`, *edit*) returns the manifest and lock to write into the caller's draft. It is committed like any other edit, and audited as `package.changed`.
- **`PackageResolver` writes what a desktop `npm install` / `dotnet restore` accepts:**
  - **npm:** the highest version in range, the `latest` tag first. A package goes at the top of `node_modules` unless a different version is already there, then under the package that needs it. Peer and optional dependencies are not installed.
  - **NuGet:** the latest stable version unless one is given. Dependencies take the lowest version their range allows, and the highest of those when two ask. The `.nupkg` is hashed into the lock. Packages the framework provides (the curated BCL) are pruned, as .NET 10's restore does.
- **The editor sees the packages:**
  - `/_functions/packages/types` returns the locked npm packages' `.d.ts` files and `package.json` files, mounted under `file:///node_modules/` beside the SDK.
  - `/_functions/packages/assemblies` returns the locked NuGet packages' assemblies (the build's `lib/` choice) to the C# language worker's `Packages` export.
  - Both are fetched again when a lock changes.

#### Policy (P6)

- **"Functions may use packages"**, the instance setting `packages`, is on by default. Turned off:
  - a build whose lockfiles install any package fails, saying so;
  - adding a package is refused. Removing one stays allowed, so a repository can get back inside the setting.
- **Every deployment records what it installed** (`functions.deployments.Packages`: `npm:name@version`, `nuget:Id@version`, transitive ones included). The record is frozen with the build, and the Deployments screen lists it.

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
