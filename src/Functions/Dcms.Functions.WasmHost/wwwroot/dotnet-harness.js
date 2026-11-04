// The harness a C# function's Deno process runs (ADR 0030), as session-harness.js is for
// TypeScript (ADR 0029): the same stdin/stdout JSON lines, the same permission-less process.
//
// stdin:  {"type":"start","mode":"run","assembly":"<base64>","pdb":"<base64>","class":"Ns.Class","input":...,"context":{...}}
//         then {"type":"reply","id":n,"ok":true,"result":...} | {"type":"reply","id":n,"ok":false,"error":"..."}
// stdout: {"type":"call","id":n,"op":"...","args":...} | {"type":"log","level":"...","message":"..."}
//         and last {"type":"done","ok":true,"result":...} | {"type":"done","ok":false,"error":"...","limit":bool}
//
// The process gets no --allow-* flag. .NET's own files reach it without one: its two modules are
// imported statically below and every .wasm through resources.js's `with { type: "bytes" }`
// imports, all part of the module graph Deno loads before running anything. The tenant's
// assembly arrives on stdin. So the function can read no file, this one included.
import { write } from "./hide-node.js";
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

const incoming = lines(Deno.stdin.readable);
const first = await incoming.next();
const start = JSON.parse(first.value);

const pending = new Map();
let nextId = 0;
(async () => {
  for await (const line of incoming) {
    const reply = JSON.parse(line);
    const waiting = pending.get(reply.id);
    if (reply.type !== "reply" || !waiting) continue;
    pending.delete(reply.id);
    if (reply.ok) waiting.resolve(JSON.stringify(reply.result === undefined ? null : reply.result));
    else waiting.reject(new Error(reply.error ?? "The host refused the call."));
  }
})();

const bytes = (base64) => Uint8Array.from(atob(base64 ?? ""), (c) => c.charCodeAt(0));

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

try {
  if (start.mode !== "run") throw new Error("A C# function is described by its build, not by running it.");
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
  setModuleImports("dcms", {
    // Every op is checked by the host; nothing here is trusted.
    call: (op, args) =>
      new Promise((resolve, reject) => {
        const id = ++nextId;
        pending.set(id, { resolve, reject });
        write({ type: "call", id, op, args: JSON.parse(args) });
      }),
    log: (level, message) => write({ type: "log", level, message }),
  });
  const { Entry } = await getAssemblyExports(getConfig().mainAssemblyName);
  const outcome = JSON.parse(await Entry.Run(
    bytes(start.assembly), bytes(start.pdb), String(start.class ?? ""),
    JSON.stringify(start.input ?? null), JSON.stringify(start.context ?? {})));
  write(outcome.ok
    ? { type: "done", ok: true, result: outcome.result ?? null }
    : { type: "done", ok: false, error: outcome.error, limit: outcome.limit === true });
} catch (e) {
  // A .NET-side stack overflow surfaces here as V8's RangeError: a limit, like running out of memory.
  write({ type: "done", ok: false, error: String(e instanceof Error ? e.message : e), limit: e instanceof RangeError });
}
// Pending host calls and timers must not keep a finished function alive.
Deno.exit(0);
