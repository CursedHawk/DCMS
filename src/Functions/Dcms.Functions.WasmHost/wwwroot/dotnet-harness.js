// The harness a C# function's Deno process runs (ADR 0030), as session-harness.js is for
// TypeScript (ADR 0029): the same stdin/stdout JSON lines, the same permission-less process.
//
// stdin:  {"type":"start","mode":"run","assembly":"<base64>","pdb":"<base64>","class":"Ns.Class","input":...,"context":{...},"serve":bool}
//         then {"type":"reply","id":n,"ok":true,"result":...} | {"type":"reply","id":n,"ok":false,"error":"..."}
// stdout: {"type":"call","id":n,"op":"...","args":...} | {"type":"log","level":"...","message":"..."}
//         and last {"type":"done","ok":true,"result":...} | {"type":"done","ok":false,"error":"...","limit":bool}
//
// The process gets no --allow-* flag. .NET's own files reach it without one: its two modules are
// imported statically below and every .wasm through resources.js's `with { type: "bytes" }`
// imports, all part of the module graph Deno loads before running anything. The tenant's
// assembly arrives on stdin. So the function can read no file, this one included.
import { host, quiet, write } from "./hide-node.js";
import { files } from "./resources.js";
import "./_framework/dotnet.runtime.js";
import "./_framework/dotnet.native.js";
import { dotnet } from "./_framework/dotnet.js";

async function* lines(readable) {
  let buffered = "";
  for await (const chunk of readable.pipeThrough(new TextDecoderStream())) {
    buffered += chunk;
    let newline;
    while ((newline = buffered.indexOf("\n")) >= 0) {
      const line = buffered.slice(0, newline);
      buffered = buffered.slice(newline + 1);
      if (line.trim() !== "") yield line;
    }
  }
}

// Serving (#607): a start message with "serve": true leaves the process up after its done line,
// for the next start of the same deployment's code, which script-runner sends only to a process
// it keeps for that deployment. A later start may leave out the assembly: the loaded one runs.
const pending = new Map();
let nextId = 0;
let entry;
// Between a run's done line and the next start, a serving process has no run: what code left
// running gets no host calls and no log (script-runner also retires a process busy while idle).
let running = false;
// fromBase64, not atob: a start message with packages is megabytes, and decoding it through a
// string ran the 64 MB heap out (ADR 0031, spike P0).
const bytes = (base64) => Uint8Array.fromBase64(base64 ?? "");

// The reason rarely says why (Mono logs its own allocator's failure as a separate line); in
// practice an abort is running out of WebAssembly memory, so it counts as a limit, not retried.
const end = (reason) => {
  write({
    type: "done",
    ok: false,
    error: `The .NET runtime stopped, most likely out of memory (${reason.slice(0, 200)}).`,
    limit: true,
  });
  Deno.exit(0);
};

async function boot() {
  const { getAssemblyExports, getConfig, setModuleImports } = await dotnet
    .withResourceLoader((_type, name) => {
      const file = files[name.split("/").pop()];
      return file ? Promise.resolve(new Response(file)) : undefined;
    })
    // When .NET's own native allocator runs out of WebAssembly memory, the runtime aborts instead
    // of throwing OutOfMemoryException, and the run's promise never settles: end it here.
    .withModuleConfig({
      onAbort: (reason) => end(String(reason ?? "")),
      onExit: (code) => end(`exit ${code}`),
    })
    .create();
  // Every op is checked by the host; nothing here is trusted.
  const call = (op, args) =>
    new Promise((resolve, reject) => {
      if (!running) return reject(new Error("The run is over."));
      const id = ++nextId;
      pending.set(id, { resolve, reject });
      write({ type: "call", id, op, args });
    });
  // fetch (hide-node.js) and so HttpClient: the reply as an object.
  host.call = async (op, args) => JSON.parse(await call(op, args));
  setModuleImports("dcms", {
    call: (op, args) => call(op, JSON.parse(args)),
    log: (level, message) => running && write({ type: "log", level, message }),
  });
  return (await getAssemblyExports(getConfig().mainAssemblyName)).Entry;
}

async function run(start) {
  running = true;
  quiet.now = false;
  try {
    if (start.mode !== "run") throw new Error("A C# function is described by its build, not by running it.");
    entry ??= await boot();
    // NuGet packages' assemblies (ADR 0031), before the function's, which references them.
    for (const p of start.packages ?? []) entry.Load(bytes(p));
    const outcome = JSON.parse(await entry.Run(
      bytes(start.assembly), bytes(start.pdb), String(start.class ?? ""),
      JSON.stringify(start.input ?? null), JSON.stringify(start.context ?? {})));
    write(outcome.ok
      ? { type: "done", ok: true, result: outcome.result ?? null }
      : { type: "done", ok: false, error: outcome.error, limit: outcome.limit === true });
  } catch (e) {
    // A .NET-side stack overflow surfaces here as V8's RangeError: a limit, like running out of memory.
    // So does .NET's own OutOfMemoryException when it escapes Entry.Run (thrown again while reporting).
    const error = String(e instanceof Error ? e.message : e);
    write({ type: "done", ok: false, error, limit: e instanceof RangeError || /OutOfMemoryException/.test(error) });
  }
  running = false;
  quiet.now = true;
  // Calls still waiting belong to the run that ended: refuse them rather than answer them later.
  for (const [id, waiting] of pending) {
    pending.delete(id);
    waiting.reject(new Error("The run is over."));
  }
  // Pending host calls and timers must not keep a finished function alive.
  if (!start.serve) Deno.exit(0);
}

for await (const line of lines(Deno.stdin.readable)) {
  const message = JSON.parse(line);
  if (message.type === "start") {
    // Not awaited: its host calls' replies arrive on this same stream.
    void run(message);
    continue;
  }
  const waiting = pending.get(message.id);
  if (message.type !== "reply" || !waiting) continue;
  pending.delete(message.id);
  if (message.ok) waiting.resolve(JSON.stringify(message.result === undefined ? null : message.result));
  else waiting.reject(new Error(message.error ?? "The host refused the call."));
}
// The caller closed stdin: nothing more will come.
Deno.exit(0);
