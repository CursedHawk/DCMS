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

### Triggers and the run queue

`functions.runs` is the queue and the history, as `apps.flow_runs` was for flows. A run is unique per (instance, trigger event, function), so a redelivered event or a second replica's tick starts nothing twice. `RunWorker` (jobs-worker) claims due runs with `FOR UPDATE SKIP LOCKED` and a 7-minute lease, in 4 slots, at most 2 per tenant. A run that threw is retried after 30 s and 2 min, 3 attempts in all; one that hit a limit is not.

Where runs come from:
- **Row and relation events.** Dynamic Apps keeps its transactional outbox. Its router hands each committed event to `dynamic-apps.event-sink@1`, an open extension point Dynamic Apps declares and Functions provides; it does not know Functions exists.
- **Platform events** (`on.event`): Functions subscribes to them itself, in the environment the event names.
- **Messages** (`messages.emit`): every function on the topic in every live deployment of the environment. No outbox: emitting queues the runs directly.
- **Schedules** (`on.schedule`, `on.every`): the worker looks every 30 s; a slot is the run's event id. A slot missed while no worker ran is not caught up. A cron firing more often than every 5 minutes is refused at build.

Cascades stop as flows' did: each run carries its chain (correlation, depth). A function's writes carry it through `AutomationCause`, an ambient value `dynamic-apps.automation@1` reads, and its messages one level deeper. A run deeper than 5, or past 20 runs set off by one original action, is recorded as terminated and not run.

Manual, job and HTTP runs run at once and are recorded in the same history. Each executed run is audited once (`plugin.functions.run.executed`); the rows themselves are not. Inputs, results and logs are shown only to holders of `plugin:functions:logs`.

### HTTP

Functions are called at `/api/{instance}/fn/{function}` on the site plane, with the methods each declares (`GET` and `POST` unless it says otherwise). A function's name is its path under `functions/`, folders included (`functions/orders/total.ts` is `orders/total`).
- Each function declares who may call it: `public`, a signed-in user or role of the environment's User Authentication realm, and/or an **API key**.
- API keys are scoped to the instance, stored hashed, per environment, and shown once.
- content-api authenticates the caller and rate-limits the request, then forwards it to admin-api's internal invoke, where the broker runs. Secrets and Transit decryption stay out of the internet-facing service.

As built (F7):
- `access.http` takes `public`, `user` (anyone signed in to the realm), `role` and `apiKey`. `role` follows Dynamic Apps' manual flows: Functions offers each such function to the roles screen as resource `fn:{name}`, action `invoke` (`users.resources@1`). content-api asks `users.access@1` whether the user holds it.
- content-api throttles (120 calls a minute per address, 3,000 per app), caps the body at 1 MiB, and passes on only safe headers. It forwards the caller as it found them, the raw `X-Api-Key` included, to `/api/internal/functions/invoke`. That route accepts only a client-credentials token with the `dcms.functions` scope (`ServicePrincipalGuard`). The access decision and the key check happen there, against the live deployment.
- Keys are `dfk_` plus 32 random bytes. They are found by a 12-character prefix and compared by SHA-256 in constant time, and may be limited to named functions.
- A function's input is the query string for `GET` and `DELETE`, the JSON body otherwise. It answers with any JSON (200), or with `respond(status, body, headers)`, whose headers are limited to an allowlist (no cookies or CORS). A function that fails answers 500 with no detail. Every HTTP run is recorded (user, 2026-11-05). So that a public endpoint cannot flood the history, the hourly sweep keeps only a function's newest 1,000 successful HTTP runs; failed ones stay for the usual 30 days.
- **Methods, types and validation (#610, user, 2026-11-05).**
  - `access.methods` (C#: `[Http(..., Methods = [HttpMethod.Post])]`) lists the methods it answers among get, post, put, patch and delete; any other gets 405. None declared: GET and POST.
  - The build reads what each function takes and returns as JSON Schema: TypeScript from the handler's parameter and return types, by the TypeScript compiler (pinned in the runner image, run by `type-schemas.js` with read access to the build only, never running the code); C# from `TIn`/`TOut` in dcms-csc, as System.Text.Json's web defaults serialize them (camelCase, enums as numbers). The SDK's `Json`, `object`, `JsonNode` and a `respond(...)` answer are `{}`: any JSON. Doc comments become descriptions.
  - Bounds: 8 levels, 1,000 nodes and 32,000 characters per schema, recursion cut to `{}`; admin-api keeps a schema only if it uses the keywords it writes (`FunctionManifest.Schema`). Reading types failing is not a build failure: the functions are then described and called as taking any JSON.
  - admin-api checks an HTTP call's input against the schema before a sandbox starts: 400 with up to ten "path: what was expected" problems. Query values are read as the types their properties say (`?limit=5` is a number). Other triggers and `functions.invoke` are not checked.
  - The OpenAPI document has each declared method: `GET`/`DELETE` with the input's properties as query parameters, the others with it as the request body, and the output as the 200 response; so the generated client is typed.
- `?async=1` enqueues the run as a tracked job and answers 202 with its id.
- At most 4 HTTP runs per tenant at once on a replica; past that, 503.
- The live deployment's HTTP functions are in the tenant's OpenAPI document and generated client (F13): each at its methods, `/fn/{name}`, client `api.{instance}.fn.{…name's segments}.{method}`, with who may call it and the `X-Api-Key` header where keys are accepted. content-api cannot read deployments, so it asks admin-api's `/api/internal/functions/live` (same `dcms.functions` scope) and remembers the answer for 30 s; the live deployment's number is the instance's API version, so a deploy rebuilds cached documents.

### AI

- **In the editor.** A Functions app's repository opens in the console's IDE (`/repos/{id}/code`, F8). Its agent is the site agent's loop with the `functions` prompt: the file, type and git tools, plus `functions_check` (the server's build of the draft, also the run's closing gate), `functions_test_run` (a real run of a draft function under the member's permissions, approved like any dangerous tool) and `functions_sdk`. The agent edits the draft; committing, shipping and deploying stay the member's.
- **From anywhere.** `functions.code@1` (Admin and Ai) gives the console's assistant the same path: overview, read files, read the SDK, write files to a working branch (never `release`), check, test, ship (merge into `release`, which builds) and deploy. Each step needs its own permission; deploying still needs everything the functions use. `dcms.repos@1` gained `CommitAsync` and `MergeAsync` for it. A merge into `release` is refused where the environment leaves the instance to deployments, as in the IDE.
- **On a Dynamic Apps app's page.** A Functions tab lists the functions its tables trigger or use, opens their code, and offers "Automate with AI": the assistant with a prompt naming the app and its tables, the Functions app's AI tools switched on when needed.

### Environments

- The `functions` configuration kind (order 110, after Dynamic Apps, whose tables a build type-checks against) moves what is **live**, by instance slug: the live commit and the permissions it was vouched for. Code merged into `release` but not deployed does not move.
- Applying it puts that commit's files on the receiving environment's `release` as one new commit (`dcms.repos@1` `AdoptAsync`: fetched from the same instance's repository in another environment, a fast-forward a locked release accepts), and also on `main` when the repository is new there. It builds them against the receiving environment's SDK and makes the build live.
- The commit is per environment, so `Live` is self-merged, as a site's `Release` is.
- Definer's rights hold across environments. The deployer must hold, in the receiving environment, everything the build uses there. If they do not, the build stays `Ready` and the deployment fails naming what is missing. A new environment's copy has no person behind it, so it vouches for everything.
- Secrets, API keys and run history stay with each environment.
- Where an environment leaves a Functions app to deployments (policy kind `functions`), its `release` is locked at the git server and refused in the IDE and to `MergeAsync`. Activating or rolling back by hand is refused too (`RepoInfo.ReleaseManaged`). A plugin's repository answers to the plugin's own kind (its id), not to `plugins`.

### Retiring flows (F11, F12)

- **The SDK covers what flows could do.**
  - `email.send` (`uses.email`).
  - `notifications.raise` (`uses.notifications`): the permissions of whom to tell, which the deployer must hold.
  - `content.get`/`content.list` (`uses.content`, needs `content:read`).
  - Other environments' apps through their links: `links.call`, `links.records`, with `uses.links: ["environment/app"]`.
  - An app's publishes: `on.app("crm").published()`, which needs `model-read`.

  Within a run, mail and notifications de-duplicate on the run and their place in it.
- **Flows were moved, once, on dev.** A converter wrote each published flow as a function of an `automations` Functions app: same trigger, condition and steps, expressions through a `lib/flow.ts` keeping the flow language's rules. It was deployed vouched for what the flows could already do, and the flows were switched off. The last app, test1's CRM, was finished by hand on vps1 (2026-11-04). The converted code is the tenants' own now, in their `automations` repositories.
- **The migration is gone** (F12): the converter, the expression language, the migration worker, and `dcms.instances@1`, which only it used. Nothing converts flows any more. An environment that still had flows would keep them as inert data in its revisions, to be rewritten as functions by hand (production included, if it has any).
- **One difference stays.** A flow retried from the step that failed; a function is retried whole. A record a failed run created is created again on the retry.

### The flow engine is gone (F12)

Deleted:
- the executor, router, action catalog, flow composer and typings;
- the platform-event bridge and the run slots;
- the `apps.flow_runs`, `flow_run_steps` and `flow_schedules` tables;
- the flow routes, the flow operations of `dynamic-apps.config@1` and the `flows-run` permission;
- `automation.actions@1` and the actions User Authentication and Visitor accounts offered through it;
- the flow editor, with script-runner's `/run`.

Old flows stay in revisions as data. Change sets refuse to create or update one, and the
validator no longer looks at them. Dynamic Apps' worker now only hands an app's row,
relation and publish events to the functions on them, and keeps its indexes.

### No third-party imports in v1

A function imports only:
- its repository's own files;
- `@dcms/functions`.

All I/O goes through the SDK. The `runtime` field (`deno-ts`) leaves room for other languages: C# (`dotnet`) came next, in [0030](0030-csharp-functions.md).

### The workbench (epic #601, 2026-11-05)

A Functions app's repository in the IDE is where its functions are written, run and debugged.
- **Functions view:** every function of the draft as the server's build reads it (runtime, file, triggers, access, uses) and what is live, with deploy.
- **Run tab:** runs one function now, from the draft (`/_functions/test`) or as deployed (`/_functions/run`).
  - It may be told it was started by an HTTP call (with a signed-in user), a row event, a message or a schedule (`RunSimulation`).
  - Only the function's context changes: the run is still a manual one, under the caller's permissions, and the broker trusts none of it.
  - Inputs are kept as `test-inputs/<function>/*.json` in the repository. Not under `.dcms/`: builds refuse hidden paths.
- **Runs tab:** the history, one run in full, and **replay against the draft**. The app's Runs screen opens it (`/repos/{id}/code?runs=<function>`).
- **Traces:** `FunctionRunner` records every host call of a run (op, arguments, ok or error, ms) into `functions.runs.trace` and the test/run answers.
  - Never results.
  - Arguments are kept only after redaction:
    - every value the run got from `secrets.get`, wherever it was passed on;
    - an HTTP call's URL beyond scheme and host;
    - its body;
    - every header but a few harmless ones.
  - At most 200 calls of 300 characters each.
  - Shown, like logs, only to holders of `plugin:functions:logs`.
- **Errors name the repository's lines:**
  - C# maps IL offsets through the PDB (ADR 0030).
  - TypeScript: the harness passes the function's own frames. Deno shortens a `data:` URL with `......`, so the scrub matches dots too. `FunctionRunner` maps `function:L:C` through esbuild's source map, kept as `bundles["<fn>.map"]`.
- **Breakpoint debugging is out of scope** by decision. Traces and replay are the debugger.

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
