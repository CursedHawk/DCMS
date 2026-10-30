# ADR 0029: Functions — tenant-written TypeScript, a generated SDK, and the sandbox as the only place code runs

**Status:** accepted (2026-10-30) · in progress (kanban epic #576) · replaces the flow engine of
[0021](0021-dynamic-apps.md) · extends [0025](0025-flow-scripts.md)

## Context

Today a tenant automates only through Dynamic Apps flows:
- A flow is JSON stored in an app revision: one trigger, then a list of steps.
- The only code a tenant writes is the inline `script.run@1` step: a pure function with no I/O, a 2-second limit and no access to DCMS.

What is missing:
- an HTTP trigger;
- a way for a function to call DCMS or another API;
- tenant API keys;
- tenant messages;
- version control for the code;
- automation outside Dynamic Apps.

What tenants ask for is closer to serverless functions:
- code in a repository;
- edited in the browser, with an AI agent;
- calling the workspace's data and apps through a typed SDK;
- triggered by HTTP, by data changes, by messages, by schedules and by jobs.

## Decision

### A Functions plugin instance is one repository of many functions

A new first-party plugin, `functions`, works like this:
- Each instance owns **one git repository** in the tenant's Forgejo org, one per environment, as Mode B sites do.
- The repository holds **many functions**. A request or trigger selects one by name.
- The code is edited in the Mode B web IDE, which is generalised from "a site" to "a repository target", together with its agent harness.

A push to the `release` branch builds a **deployment**:
- The build type-checks the code against the generated SDK (`deno check`), bundles each function (esbuild, pinned in the runner's image), and records what each function declares (each bundle is imported once in the sandbox).
  - `deno bundle` is not used: it fetches esbuild from npm at run time, and the runner has no network.
  - Neither tool runs tenant code, but both read files, and a specifier is a path. So a specifier may only be `@dcms/functions` or a relative path inside the repository; it is checked before either tool starts, and esbuild's metafile is checked afterwards.
- A row in `functions.deployments` holds the result.
- The row is frozen by a trigger once it leaves `Building`.
- At most one deployment per instance and environment is `Active`. A rollback activates an older row and never rewrites one.

### Functions replace flows

This was decided by the user on 2026-10-30.
- Functions declare their own triggers: Dynamic Apps row and relation events, platform events, messages, schedules, jobs and HTTP.
- Existing flows are converted to generated functions by an automatic migration.
- After that, the step engine, `script.run@1` and the flow editor are deleted.

Dynamic Apps keeps:
- its transactional outbox;
- routing of row events to Functions through an in-process contract.

### The sandbox stays credential-free; the SDK is host calls

The runtime is ADR 0025's `script-runner` (a Deno process with no `--allow-*` flags, no network and no credentials), with two additions.
- **A session channel.** The caller (admin-api, or the jobs worker) holds a WebSocket session to the runner.
- **Host calls.** Every SDK call is a JSON line from the Deno process. The runner relays it to the caller and relays the reply back on stdin.

Everything privileged runs in .NET, in the caller, under the run's tenant and environment RLS scope:
- database CRUD;
- contract calls;
- outbound HTTP;
- secrets;
- messages;
- jobs.

The sandbox never gains a permission. Code is still imported from a `data:` URL, for the reason in 0025.

### Two gates on every call: `uses`, and what the deployer vouched for

Each function declares `uses` (tables and verbs, contracts, egress hosts, secrets, message topics). The broker refuses anything not declared there.

Deploying requires the deployer to hold every permission `uses` implies. The set is frozen on the deployment, and a call proceeds only if its permission is still in it. This is definer's rights, as Dynamic Apps flows had. It is checked for **every** function in the deployment, not only the changed ones.

Outbound HTTP:
- goes only to the declared hosts;
- goes through `PublicEgress`, which checks at connect time, so a redirect or a DNS name pointing at an internal address is refused;
- has fixed time and size caps.

What each call needs, as the broker (`Runtime/HostCalls.cs`) checks it:
- `db.<app>.<table>.<method>`: the table and verb in `uses.tables`, and the matching `plugin:dynamic-apps:data-*`. Following a lookup (`expand`, or a filter, sort or select on `company.name`) needs `read` on the target table too. It goes through `dynamic-apps.automation@1`, an in-process contract on the system plane flows used, which checks no member permission itself: the broker does.
- `plugins.<instance>.<contract>.<operation>`: the contract in `uses.contracts`, called through the contract dispatcher on the admin plane with the vouched set as the caller's permissions. Deploying vouches for every admin operation's permission of each declared contract. Platform (`dcms.*`) contracts are never callable.
- `secrets.get`: the name in `uses.secrets`, and `plugin:functions:secrets`.
- `functions.invoke`: a function of the same deployment, at most 5 deep.
- `jobs.enqueue`: a tracked job that runs the *live* deployment when it starts. At most 100 per run, and a chain of jobs enqueuing jobs stops at 10.

A run and every function it invokes share one budget (fetches, enqueues, 50 invokes). A manual run from the admin requires the caller to hold the whole vouched set: running code lends nobody the deployer's rights; only triggers do.

The generated declarations (`Build/SdkTypings.cs`) are served at `GET …/_functions/sdk.d.ts` with an ETag, and every build checks against them, so code using a table that no longer exists fails to build. Tables appear as `db.<app>.<table>` typed from the published model; contracts as `plugins.<app>.<contractName>` in camelCase (`dynamicAppsRecords`), each operation camelCased, which the broker matches loosely.

### The SDK is generated per instance and environment; only its types

`@dcms/functions` has two parts:
- **A static runtime**, shipped in the runner image, that turns calls into host calls.
- **Generated TypeScript declarations**, built from the environment's configuration:
  - Dynamic Apps tables as a typed ORM;
  - plugin contract operations;
  - the instance's own functions, topics and secrets.

The broker re-validates every call against live configuration, so stale types can never widen access.

### HTTP

Functions are called at `GET|POST /api/{instance}/fn/{function}` on the site plane.
- Each function declares who may call it: `public`, a signed-in user or role of the environment's User Authentication realm, and/or an **API key**.
- API keys are scoped to the instance, stored hashed, per environment, and shown once.
- content-api authenticates the caller and rate-limits the request, then forwards it to admin-api's internal invoke, where the broker runs. Secrets and Transit decryption stay out of the internet-facing service.

### No third-party imports in v1

A function imports only:
- its repository's own files;
- `@dcms/functions`.

All I/O goes through the SDK. The `runtime` field (`deno-ts`) leaves room for other languages; nothing beyond it is built now.

## Alternatives rejected

- **Keep flows and add a `function.run` step.** This leaves two places to define automation. The user chose one.
- **Give the sandbox network and a scoped token.**
  - Every check would move into code the tenant controls the input to.
  - A token in the process is a token the code can exfiltrate.
  - Host calls keep 0025's "nothing to steal" property.
- **Run the broker in content-api for HTTP calls.** content-api refuses plugin secrets (`SitePlaneSecrets`) by design; widening that for one plugin would undo it.
- **NATS for tenant messages.** Messages are written in the same transaction as the run's data writes and are read under RLS. The database queue Dynamic Apps already proved does both.
- **Platform-wide API keys.** Instance-scoped keys answer the need without a new identity concept.

## Consequences

- One more repository kind in Forgejo, and the IDE gains a non-site target. Site behaviour is unchanged.
- The runner becomes a shared, capacity-limited resource:
  - slots are configurable;
  - each tenant gets a concurrency cap;
  - cold starts (one Deno process per run) are the latency floor until a warm pool is worth building.
- Run logs hold tenant data. They live in the run row, under RLS and a `logs` permission, and never go to Loki.
- Once the flow migration has run everywhere, Dynamic Apps loses its automation tab, its flow AI operations and the expression language.
