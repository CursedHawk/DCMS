# ADR 0025: Flow scripts run in a separate, credential-free Deno sandbox

**Status:** accepted (2026-10-16) · in progress (kanban epic #313)

## Context

Dynamic Apps flows (ADR 0021) are steps that call catalog actions. The data between them is
shaped by the expression language, which is deliberately closed: no loops, no I/O, a fixed set
of pure functions. That covers conditions and templates, but not a computation that needs a
loop, a map over a list, or a few lines of logic. Authors want to write such a step in
TypeScript, with editor IntelliSense from the app's own model.

Nothing on the server executes JavaScript today. The closest thing is the Mode B build
sandbox, which runs one throwaway container per build. That is far too slow for a step that
fires on every row event.

## Decision

### The contract: a pure function

A script step is the catalog action `script.run@1`, with this input:

```json
{ "language": "ts", "code": "export default function main(ctx) { … }", "args": { "total": "{{ row.amount }}" } }
```

- **Shape:** the module's default export is `main(ctx)`, sync or async.
- **`ctx`:** the flow scope (`event`, `row`, `previous`, `changedFields`, `input`, `steps`,
  `run`) plus `args`, the step's rendered templates.
- **Result:** the JSON value `main` returns is the step's output, read by later steps as
  `steps.<id>`.
- **Purity:** a script has no I/O at all. It cannot fetch, read, write, spawn, or see the
  environment. Anything with a side effect stays a catalog action, so idempotency keys, write
  limits and permissions keep working unchanged.
- **Risk:** because it is pure, `script.run@1` is `OpRisk.Read`. The executor's reread loop may
  run it again.

### `code` is literal, never a template

`IFlowAction.LiteralInputs` (default empty, like `HtmlInputs`) names input keys that
`Expressions.Render` copies verbatim and `ConfigValidator` does not scan for `{{ }}`. For
`script.run@1` that key is `code`. A `"{{"` inside a script's string or regex therefore stays
exactly what the author wrote. Values flow into a script only through `args` or `ctx`.

### Where it runs: `script-runner`

A small .NET service with one endpoint, `POST /run`. Each execution is a fresh Deno process.
**Deno** is pinned by image digest. It is deny-by-default, it runs TypeScript by stripping
types (no compile step, and no client-supplied compiled JS to trust), and it needs no native
addons.

Each execution is launched like this:

```
deno run --no-prompt --no-remote --no-npm --no-config --no-lock
         --v8-flags=--max-old-space-size=64 /app/harness.js
```

- **Permissions:** zero `--allow-*` flags.
- **Environment:** cleared (`clearEnv`), apart from `DENO_DIR` and `NO_COLOR`.
- **Cache:** `DENO_DIR` is a fresh temporary directory per execution, deleted afterwards, so
  no two runs share even a transpile cache.
- **I/O:** `{code, ctx}` arrives on stdin, and one JSON line leaves on stdout.

**The user's code is never a file.** The harness loads it with a runtime
`import("data:application/typescript;base64,…")`. This is load-bearing: the spike showed that
Deno resolves a module graph's *statically analysable* local imports **without** checking
`--allow-read`. With the script as the entry file, `import x from "/etc/passwd"` was read, and
its contents surfaced in the parse error. A module loaded through a runtime `data:` import has
every further local import permission-checked. All of these were refused:

- `import "file:///etc/passwd"`
- literal and computed `import()`
- `new Worker("file://…")`
- `fetch("file://…")`

The harness module itself is already loaded, so importing it again reads nothing; it is public
anyway.

### Isolation layers, all at once

| Layer | What it denies |
| --- | --- |
| Deno permissions | net, read, write, env, run, ffi, sys |
| `data:` loading | local-file module loads that bypass `--allow-read` |
| V8 heap cap 64 MB | heap exhaustion: the process aborts (exit 133) |
| `RLIMIT_DATA` 640 MB | memory outside the heap (ArrayBuffers, Wasm), which the heap cap does not count. Deno's own startup reserves ~600 MB of it, so a script gets roughly 40–80 MB more |
| `RLIMIT_CORE` 0, `oom_score_adj` 1000 | a crash filling the shared tmpfs with core dumps; the kernel's OOM killer picking the host instead of a script |
| Wall-clock deadline | loops: the host kills the process tree |
| Output cap 64 KB | an oversized result: the host stops reading and kills |
| Container: `read_only`, `cap_drop: ALL`, `no-new-privileges`, non-root, `pids_limit`, `mem_limit` | anything that escapes Deno |
| Container holds **no credentials** | `*otel-env` only, never `*service-env`: no Vault token, no database, no NATS, no MinIO |
| Network: one internal network with the flow executor | egress; nothing but its caller can reach it |

An escape from Deno lands in a container that has nothing to steal and nowhere to send it.

### Limits

- `code` is at most 64 KB, enforced by `ConfigValidator` and again by the runner.
- The result is at most 64 KB.
- A run gets 2 s of wall-clock time, a 64 MB heap and the `RLIMIT_DATA` above. Two run at once.
- A runner timeout, memory abort or oversized output ends the run **terminated**
  (`FlowFatalException(limit: true)`).
- A thrown error is a step failure without retry: the same input fails the same way.
- An unreachable runner is retryable, like any other passing trouble.

### Redaction

The source lives in the published revision. A run step's recorded input replaces `code` with
`{ sha256, chars }`, so the run inspector, `GetRunAsync` (and through it the assistant) and the
run history never repeat the source in every run.

### Spike numbers (2026-10-16, 1 CPU, `denoland/deno:distroless-2.5.4`)

Per execution, including process start, the `data:` import and TS stripping: **p50 84 ms,
p90 94 ms, max 104 ms** with a fresh `DENO_DIR` each run.

## Alternatives rejected

- **Jint, or any engine, in the admin-api process.** Tenant code would share a process with
  the database connection, Vault token and every tenant's data. One interpreter bug would be a
  full compromise. A fast in-process engine is not worth that blast radius. Jint also cannot
  run TypeScript.
- **Node with `isolated-vm`.** It is a native addon to build and pin, the isolate is the only
  boundary, and TypeScript would still need a transpile step.
- **One container per execution (the Mode B sandbox pattern).** It is the strongest boundary,
  but costs hundreds of milliseconds per step and needs Docker access in the caller. The
  credential-free container already gives the container boundary without that cost.
- **Banning `import` in the source text.** Text matching is brittle. The `data:` loading closes
  the actual hole in the runtime instead.

## Consequences

- **A new image and service.** `script-runner` is built, rolled and watched like the others; it
  uses `*otel-env` so it still has telemetry.
- **Purity is the boundary.** A script that needs data calls nothing. Its flow reads first
  (`records.lookup`, `records.query`) and passes the result in.
- **Editor types are a guide, not a type check.** IntelliSense comes from server-generated
  typings (`FlowTypings`), and type errors show in the browser. The server only strips types,
  so a type error does not stop a run.
- **Deno upgrades are security upgrades.** The image is pinned by digest, and the permission
  probes from the spike are integration tests that must keep passing on every bump.
