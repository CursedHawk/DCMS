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

// What the harness needs of Deno, kept before the function can see it: afterwards there is no
// Deno (and no Node `process`) at all. Nothing in them would work without a permission anyway;
// gone, a function sees what its types say it has (webworker + ES), not a namespace that throws.
const { stdout, stdin, exit } = Deno;
delete globalThis.Deno;
delete globalThis.process;

const encoder = new TextEncoder();
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

const incoming = lines(stdin.readable);
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

// fetch, as the web has it, through the broker: the same http.fetch host call ctx.http.fetch
// makes, so only the hosts in uses.egress answer. A library written against fetch works as is.
// Request does the work of reading every kind of body (text, bytes, Blob, URLSearchParams,
// FormData) and its content type.
const NULL_BODY = new Set([101, 103, 204, 205, 304]);
globalThis.fetch = async (input, init) => {
  const request = new Request(input, init);
  const bytes = request.body === null ? null : new Uint8Array(await request.arrayBuffer());
  const timeoutMs = init?.timeoutMs;
  const r = await globalThis.__dcms.call("http.fetch", {
    url: request.url,
    method: request.method,
    headers: Object.fromEntries(request.headers),
    bodyBase64: bytes === null ? null : bytes.toBase64(),
    redirect: request.redirect,
    timeoutMs: typeof timeoutMs === "number" ? timeoutMs : null,
    raw: true,
  });
  const body = NULL_BODY.has(r.status) ? null : Uint8Array.fromBase64(r.bodyBase64 ?? "");
  const response = new Response(body, { status: r.status, headers: r.headers });
  // Where it ended up after redirects, as a fetch says; a secret in it reads as its ref.
  Object.defineProperty(response, "url", { value: r.url ?? "" });
  return response;
};

const RUN = Symbol.for("dcms.run");
// The error and the function's own frames: "function:12:5" is a line and column of its bundle,
// which the caller maps back to the repository's file (workbench W3). A frame anywhere else
// (this file, Deno's internals) says nothing about the function and is left out.
const scrub = (e) => {
  // A compile error names the module by its data: URL — the whole bundle, base64-encoded. A
  // stack frame names it too, shortened by Deno with "......" in the middle.
  const text = String(e instanceof Error ? (e.stack ?? e.message) : e)
    .replace(/data:application\/javascript;base64,[A-Za-z0-9+/=.]+/g, "function");
  const [first, ...rest] = text.split("\n");
  return [first, ...rest.filter((line) => line.includes("function:")).slice(0, 10)].join("\n");
};

try {
  // toBase64, not a binary string: with packages a bundle is megabytes (ADR 0031).
  const module = await import("data:application/javascript;base64," + encoder.encode(start.bundle).toBase64());
  const definition = module.default;
  if (!definition || typeof definition[RUN] !== "function") {
    throw new Error("A function's file must `export default defineFunction({ ... })` from \"@dcms/functions\". Every .ts in functions/ is a function: shared code goes outside it, in lib/ for example.");
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
exit(0);
