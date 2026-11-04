# ADR 0030: C# functions — IL on .NET's WebAssembly runtime, inside the same sandbox

**Status:** accepted (2026-11-04) · kanban epic #591 · extends [0029](0029-functions.md)

## Context

[ADR 0029](0029-functions.md) left a `runtime` field (`deno-ts`) as room for other languages. Tenants asked for C#:
- written in the web IDE, with real language support;
- compiled on the server when it reaches `release`;
- run as WebAssembly.

The constraint is 0029's: the sandbox holds no credentials, has no network and reaches DCMS only by host calls.

## Decision

These were decided by the user on 2026-11-04.

### IL on .NET's browser-wasm runtime, hosted by the same Deno

- Roslyn compiles the repository's C# to one IL assembly.
- A run loads it into a prebuilt **.NET `browser-wasm` runtime** (Mono, as Blazor uses). That runtime is booted by `dotnet-harness.js` inside the **same permission-less Deno process** a TypeScript function gets.
- The stdin/stdout JSON lines are identical, so the broker (`Runtime/HostCalls.cs`), budgets, limits and run history do not know which language they serve.
- Rejected: native AOT to WASI.
  - componentize-dotnet is still a preview, and the `wasi-experimental` workload has open build bugs.
  - Builds would take minutes and gigabytes on a 4-core box.

  Per-function AOT can come later without changing the contract.

Getting .NET to start in Deno with **no `--allow-*` flag** (spike C0, kanban #592):
- Deno has a Node-compatible `process` global, and dotnet.js would take the Node path and import `"process"`. So the harness's **first** import (`hide-node.js`) deletes it.
- Deno loads the module graph without read permission:
  - .NET's own modules are imported statically, so they are part of that graph;
  - every `.wasm` is a `with { type: "bytes" }` import in the generated `resources.js` (`--unstable-raw-imports` is a feature flag, not a permission), handed to .NET through `withResourceLoader`;
  - the tenant's assembly arrives on stdin.
- The escape probes stay shut:
  - files resolve against emscripten's empty in-memory FS;
  - `Environment` is emscripten's synthetic one;
  - `[JSImport]` to `Deno.*` gets NotCapable;
  - `HttpClient` is not shipped at all.

  A function could reach `globalThis` through reflection. That gives it exactly what a TypeScript function already has.

### A curated BCL, the same for compiling and running

- The whole BCL needs ~900 MB of RLIMIT_DATA and 330 MB RSS per run.
- A curated set (`Dcms.Functions.WasmHost/CuratedAssemblies.txt`: collections, LINQ, System.Text.Json, regex, cryptography, …), rooted whole and run on V8's baseline tier (`--liftoff-only`), fits a TypeScript run's 640 MB at ~126 MB RSS and ~0.5 s per cold run.
- `dcms-csc` compiles tenant code against **exactly** those assemblies. So a type outside the set is a compile error, never a load failure. `DllImport` and `HttpClient` are simply not there.

### The build: dcms-csc, confined like Deno

script-runner splits a repository's files. `.ts` files go to 0029's pipeline; `.cs` files go to `dcms-csc`.

`dcms-csc` (`Dcms.ScriptRunner.CSharp`):
- runs through `Confined` (prlimit, no environment, GC capped below the kernel limit);
- compiles in memory with fixed options and references (`FunctionCompilation.cs`);
- runs no MSBuild, no analyzers, no generators and no NuGet, so nothing of the tenant's runs at build time.

A function is a public class deriving from `Function<TIn, TOut>` and marked `[Function("name")]`:
- Its triggers, HTTP access, `uses` and limits are **attributes**.
- Being constants, they are read from the compilation and turned into the definition JSON a TypeScript describe pass produces, so `FunctionManifest.Read` validates both languages alike.
- One repository may mix languages; two functions of one name fail the build.
- The assembly and its portable PDB are stored once per deployment in `bundles` (`@dotnet.dll`, `@dotnet.pdb`). A C# function's own entry is its class name. There is no migration.

### The SDK: a static half and a generated half

- `Dcms.Functions` is the static half: the attributes, `Context`, `Table<TRow>`, `Query`/`Filter`.
- `Sdk.g.cs` is generated per environment from the same model walk as the TypeScript declarations (`SdkTypings.ModelAsync`):
  - `ctx.Db.<App>.<Table>` over typed row records, and `ctx.Plugins.<App>.<Contract>.<Op>()`;
  - as C# 14 extension properties on `Context`;
  - contract operations take and give JSON, their schema in the doc comment.
- It is served at `GET …/_functions/sdk.cs` and in `/_functions/sdk` as `csharp`.
- The build compiles a **pruned** `Sdk.g.cs`: only the tables in the repository's `[UsesTable]` and the contracts in its `[UsesContracts]` (`SdkPruning`).
  - A large workspace's SDK is megabytes (2.8 M characters for the stress tenant). Compiled whole, it overflowed the build's output and would ride along in every run.
  - The broker refuses undeclared tables anyway, so using one is now a compile error whose message says how to declare it.
  - The editor keeps the whole SDK for discovery, and is the one place it is more lenient than the build.

### The IDE: Roslyn in the browser

- `Dcms.Functions.LanguageWorker` is Roslyn (Workspaces + Features) on the same browser-wasm runtime.
- It runs in a Web Worker of the console, compiles with the same `FunctionCompilation.cs` and embeds the same references.
- It provides diagnostics (squiggles and the Problems panel), completion, hover, signature help, go to definition (into the read-only `.dcms/Sdk.g.cs`), references and formatting.
- It is served under `/csharp/` by the admin image (about 17 MB gzipped, revalidated by ETag) and loaded with the first `.cs` model.
- No server language server: per-session Roslyn would have cost 300–500 MB per open editor.

## Consequences and traps

- **A Web Worker must set `self.dotnetSidecar = true`** before importing dotnet.js. Otherwise .NET takes the worker for one of its pthreads and boot hangs after `onRuntimeInitialized`.
- **Roslyn's `SymbolFinder.FindReferencesAsync` fails in WebAssembly**: its persistent storage asks for the current process. The worker finds references by a semantic walk. The same static initializer logs one harmless unhandled exception in the background.
- **Trimming breaks Roslyn's MEF**, so the language worker ships untrimmed. The run host is trimmed with its curated set rooted. Trimming also turns off reflection-based JSON and real exception messages; the host turns both back on.
- **Mono prints no line numbers.** The host maps each frame's IL offset through the PDB (System.Reflection.Metadata), so errors read `at OrderTotal.Run in functions/OrderTotal.cs:line 12`.
- **When .NET's own allocator runs out of WebAssembly memory it aborts** instead of throwing, and the run would never answer. The harness ends it on `onAbort` as a `limit`. A managed `OutOfMemoryException` is a `limit` too.
- **The curated set costs memory in every C# run.** Re-measure the data limit when it grows.
- Cold start is ~0.5–1 s against Deno's ~0.1 s; a warm pool is still the way down (0029).
- The workbench epic (#601: run history, replay and traces in the IDE) follows.
