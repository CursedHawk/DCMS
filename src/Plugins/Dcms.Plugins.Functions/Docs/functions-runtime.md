# What a function can use

A function runs in a sandbox with no permissions of its own: no files, no network, no
environment, no other processes. It computes with its language's standard library, and it
reaches everything else through the SDK (`ctx`), whose every call the platform checks against
what the function declared in `uses`. This page is the list. ADRs 0029 and 0030 explain why.

## TypeScript

- **Language**: the latest JavaScript and TypeScript (`esnext`), checked in strict mode.
- **Imports**: `@dcms/functions`, the repository's own files (`./…`, `../lib/…`), and the npm
  packages `package.json` depends on (`zod`, `date-fns/format`), locked by `package-lock.json`.
  - The build fetches each locked version from the npm registry, checks it against the lock's
    integrity hash, and never runs an install script. A missing or stale lock fails the build.
  - Packages bundle as their `worker`/`browser` build. One that needs Node.js (`fs`, `net`, …)
    fails the build and says so. One that talks HTTP through `fetch` (OpenAI's, Stripe's clients)
    goes through the same egress rules as `ctx.http.fetch`.
  - No `npm:`/`jsr:`/`https:` specifiers and no `node:` modules.
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
  - `System.Net.Http` (`HttpClient`, through the same egress rules as `ctx.Http.Fetch`),
    `System.Net.Primitives`, `System.Net.Requests`, `System.Net.ServicePoint`,
    `System.Net.WebHeaderCollection`, `System.Net.Security`, `System.Private.Uri`
  - `System.Xml.ReaderWriter`, `System.Private.Xml`, `System.Xml.Linq`, `System.Xml.XDocument`,
    `System.Private.Xml.Linq`, `System.Xml.XPath`, `System.Xml.XPath.XDocument`,
    `System.Xml.XmlSerializer`
  - `System.Linq.Expressions`, `System.Reflection.Emit`, `System.Reflection.Emit.ILGeneration`,
    `System.Reflection.Emit.Lightweight`, `System.Reflection.Primitives`, `System.Runtime.Loader`
  - `System.Threading`, `System.Threading.Channels`, `System.Threading.Thread`,
    `System.Threading.ThreadPool`, `System.Threading.Overlapped`
  - `System.IO.Compression`, `System.IO.Pipelines`, `System.IO.MemoryMappedFiles`,
    `System.Formats.Asn1`
  - `System.ComponentModel`, `System.ComponentModel.Primitives`, `System.ComponentModel.Annotations`,
    `System.ComponentModel.TypeConverter`, `System.Collections.Specialized`, `System.Data.Common`
  - `System.Runtime.Serialization.Primitives`, `System.Runtime.Serialization.Formatters`,
    `System.Runtime.Serialization.Json`, `System.Runtime.Serialization.Xml`
  - `System.Diagnostics.DiagnosticSource`, `System.Diagnostics.Tracing`,
    `System.Diagnostics.TraceSource`, `System.Diagnostics.Contracts`
  - `System.Runtime.InteropServices`, `System.Runtime.Intrinsics`, `System.Numerics.Vectors`,
    `System.Text.Encoding.Extensions`, `System.Resources.Writer`, `System.Transactions.Local`,
    `System.Security.Claims`, `System.Drawing`, `System.Drawing.Primitives`,
    `Microsoft.Win32.Primitives`
  - `System`, `netstandard`: facades, so packages built for .NET Standard load
  - `Dcms.Functions`: the SDK
- **NuGet packages**: the `PackageReference`s in `Functions.csproj`, locked by
  `packages.lock.json` (`<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>`,
  then `dotnet restore`).
  - The build fetches each locked version from nuget.org, checks its content hash, and uses its
    best build for .NET (newest .NET first, then .NET Standard).
  - A package that references an assembly outside the list above, or declares native code, fails
    the build and is named. Its assemblies go with the deployment, at most 16 MB in all.
- **Not there**: files, threads of your own (`async`/`await` works; `Thread` and the thread pool
  are present for what packages expect, but the sandbox runs one thread), native code
  (`DllImport` is refused), JavaScript interop. A package that needs what .NET in the browser
  lacks fails when it reaches it, with `PlatformNotSupportedException`: Stripe.net's built-in HTTP
  client is one.

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
