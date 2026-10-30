// The only file a function's Deno process loads from disk (ADR 0029). Like harness.js (ADR 0025),
// the code arrives on stdin and is imported from a data: URL, so every import it makes is
// permission-checked and it can read no file, this one included. Unlike harness.js, stdin stays
// open: the function reaches DCMS only by host calls, each a line on stdout that script-runner
// relays to the caller's broker, answered by a line on stdin.
//
// stdin:  {"type":"start","mode":"run"|"describe","bundle":"...","input":...,"context":{...}}
//         then {"type":"reply","id":n,"ok":true,"result":...} | {"type":"reply","id":n,"ok":false,"error":"..."}
// stdout: {"type":"call","id":n,"op":"...","args":...} | {"type":"log","level":"...","message":"..."}
//         and last {"type":"done","ok":true,"result":...} | {"type":"done","ok":false,"error":"..."}
//
// The function itself could write to stdout too; that changes nothing it could not do anyway:
// a forged call is checked like any other, a forged result is its own.

const encoder = new TextEncoder();
const stdout = Deno.stdout;
const write = (message) => {
  const bytes = encoder.encode(JSON.stringify(message) + "\n");
  let offset = 0;
  while (offset < bytes.length) {
    offset += stdout.writeSync(bytes.subarray(offset));
  }
};

const format = (args) =>
  args.map((a) => (typeof a === "string" ? a : (() => { try { return JSON.stringify(a); } catch { return String(a); } })())).join(" ");
for (const level of ["log", "info", "debug", "warn", "error"]) {
  console[level] = (...args) => write({ type: "log", level, message: format(args) });
}
for (const level of ["table", "dir", "trace"]) {
  console[level] = (...args) => write({ type: "log", level: "log", message: format(args) });
}

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
    if (reply.ok) waiting.resolve(reply.result === undefined ? null : reply.result);
    else waiting.reject(new Error(reply.error ?? "The host refused the call."));
  }
})();

// What @dcms/functions' runtime calls. Every op is checked by the host; nothing here is trusted.
globalThis.__dcms = {
  call(op, args) {
    const id = ++nextId;
    return new Promise((resolve, reject) => {
      pending.set(id, { resolve, reject });
      write({ type: "call", id, op, args: args === undefined ? null : args });
    });
  },
};

const RUN = Symbol.for("dcms.run");
const scrub = (e) =>
  // A compile error names the module by its data: URL — the whole bundle, base64-encoded.
  String(e instanceof Error ? e.message : e).replace(/data:application\/javascript;base64,[A-Za-z0-9+/=]+/g, "function");

try {
  const bytes = encoder.encode(start.bundle);
  let binary = "";
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }
  const module = await import("data:application/javascript;base64," + btoa(binary));
  const definition = module.default;
  if (!definition || typeof definition[RUN] !== "function") {
    throw new Error("A function's file must `export default defineFunction({ ... })` from \"@dcms/functions\".");
  }
  if (start.mode === "describe") {
    // Everything but the handler: what it is triggered by, who may call it, what it uses.
    write({ type: "done", ok: true, result: JSON.parse(JSON.stringify(definition)) });
  } else {
    const result = await definition[RUN](start.input ?? null, start.context ?? {});
    write({ type: "done", ok: true, result: result === undefined ? null : result });
  }
} catch (e) {
  write({ type: "done", ok: false, error: scrub(e) });
}
// Pending host calls and timers must not keep a finished function alive.
Deno.exit(0);
