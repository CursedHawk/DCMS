# What a function can use

A function runs in a sandbox with no permissions of its own: no files, no network, no
environment, no other processes. It computes with its language's standard library, and it
reaches everything else through the SDK (`ctx`), whose every call the platform checks against
what the function declared in `uses`. This page is the list. ADRs 0029 and 0030 explain why.

## TypeScript

- **Language**: the latest JavaScript and TypeScript (`esnext`), checked in strict mode.
- **Imports**: `@dcms/functions` and the repository's own files (`./…`, `../lib/…`). No npm
  packages, `npm:`/`jsr:`/`https:` URLs or `node:` modules yet.
- **Globals**: the web's worker APIs, which the editor and the build check against
  (`lib: esnext, webworker`):
  - `fetch`, `Request`, `Response`, `Headers`, `FormData`, `URLSearchParams`, `Blob`, `File`:
    `fetch` goes through the platform to the hosts in `uses.egress` only (see below)
  - `URL`, `TextEncoder`, `TextDecoder`, `atob`, `btoa`, `structuredClone`
  - `crypto.getRandomValues`, `crypto.randomUUID`, `crypto.subtle` (digests, HMAC, signatures,
    AES and the rest of Web Crypto)
  - streams (`ReadableStream`, `TransformStream`, `CompressionStream`, `DecompressionStream`)
  - `setTimeout`, `setInterval`, `queueMicrotask`, `AbortController`, `EventTarget`,
    `performance.now`
  - `Intl`, `WebAssembly`, and `console`, which writes to the run's log
- **Not there**: the DOM (`document`, `window`), Deno's namespace and Node's (`process`,
  `Buffer`: use `Uint8Array`). Timers and pending work stop when the function returns.

## C#

- **Language**: C# 14, nullable reference types on. Always in scope: `System`,
  `System.Collections.Generic`, `System.Linq`, `System.Threading.Tasks`,
  `System.Text.Json.Nodes`, `Dcms.Functions`.
- **Assemblies**: a function compiles against exactly these, and runs with exactly these:
  - `System.Private.CoreLib`, `System.Runtime`: the core types, `Math`, `DateTime`, `Guid`,
    `Span`, `Task`
  - `System.Collections`, `System.Collections.Concurrent`, `System.Collections.Immutable`,
    `System.Collections.NonGeneric`, `System.ObjectModel`
  - `System.Linq`, `System.Memory`, `System.Runtime.Numerics` (`BigInteger`, `Complex`)
  - `System.Text.Json`, `System.Text.RegularExpressions`, `System.Text.Encodings.Web`,
    `System.Web.HttpUtility`
  - `System.Security.Cryptography` (hashes, HMAC, AES, RSA, ECDsa)
  - `System.Reflection.Metadata`, `System.Diagnostics.StackTrace`, `System.Console`
    (`Console.WriteLine` writes to the run's log)
  - `Dcms.Functions`: the SDK
- **Not there**: `HttpClient` (use `ctx.Http.Fetch`), files, threads of your own (`async`/`await`
  works), native code (`DllImport` is refused), NuGet packages (yet).

## The SDK

| | TypeScript | C# |
| --- | --- | --- |
| Records of Dynamic Apps tables | `ctx.db.<app>.<table>` | `ctx.Db.<App>.<Table>` |
| Other apps' contracts | `ctx.plugins.<app>.<contract>.<operation>` | `ctx.Plugins.<App>…` |
| Other functions, jobs, messages | `ctx.functions.invoke`, `ctx.jobs.enqueue`, `ctx.messages.emit` | `ctx.Functions`, `ctx.Jobs`, `ctx.Messages` |
| Secrets | `ctx.secrets.get`, `ctx.secrets.ref` | `ctx.Secrets.Get`, `ctx.Secrets.Ref` |
| E-mail, notifications, content, linked environments | `ctx.email`, `ctx.notifications`, `ctx.content`, `ctx.links` | `ctx.Email`, `ctx.Notifications`, `ctx.Content`, `ctx.Links` |
| Outbound HTTP | `fetch`, `ctx.http.fetch` | `ctx.Http.Fetch` |
| The run | `ctx.request`, `ctx.user`, `ctx.trigger`, `ctx.run`, `ctx.log` | `ctx.Request`, `ctx.User`, `ctx.Trigger`, `ctx.Run`, `ctx.Log` |

Each needs its declaration in `uses` (C#: the `[Uses…]` attributes). Whoever deploys a function
must hold the permissions what it uses implies.

## Calling other services

- Declare the hosts: `uses: { egress: ["api.stripe.com", "*.example.com"] }`
  (C#: `[UsesEgress("api.stripe.com")]`). At most 20. Deploying it needs the
  *Let functions call the internet* permission (`plugin:functions:egress`).
- `fetch` (or `ctx.http.fetch`, which also sends a plain object as JSON) and `ctx.Http.Fetch`
  then reach those hosts, over http or https, with any body, text or binary.
- Redirects are followed, up to 5, each only to a declared host. Credentials do not follow a
  redirect to another host.
- A request waits 10 s unless it says otherwise (`timeoutMs`, at most 30 s). A body is at most
  5 MB each way. A run, with the functions it invokes, makes at most 50 requests.
- Private and internal addresses are refused, whatever a name resolves to.
- **Secrets without handling them**: put `ctx.secrets.ref("NAME")` where the value belongs in the
  URL or a header. The platform fills it in on the way out, so the value never enters the
  function, its log or its trace:

  ```ts
  await fetch("https://api.stripe.com/v1/charges", {
    method: "POST",
    headers: { Authorization: "Bearer " + ctx.secrets.ref("STRIPE_KEY") },
    body: new URLSearchParams({ amount: "500", currency: "eur" }),
  });
  ```

  ```csharp
  await ctx.Http.Fetch("https://api.stripe.com/v1/balance",
      headers: new Dictionary<string, string> { ["Authorization"] = $"Bearer {ctx.Secrets.Ref("STRIPE_KEY")}" });
  ```

## Limits of a run

| | |
| --- | --- |
| Wall time | 30 s over HTTP, 5 min otherwise; lower with `limits.timeoutMs` |
| Memory | 128 MB heap by default, `limits.heapMb` 32–256 |
| Host calls | 1,000 per run, 16 MB in and out together |
| Log | 64,000 characters |
| Result | 1,000,000 characters |
| Built function | 2,000,000 characters |
