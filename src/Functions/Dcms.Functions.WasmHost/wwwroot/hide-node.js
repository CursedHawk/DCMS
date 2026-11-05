// The first module dotnet-harness.js imports, so it runs before any of .NET's (ADR 0030).
//
// Deno has a Node-compatible `process` global. dotnet.js takes it for Node and imports the bare
// specifier "process", which a process without npm, config or permissions cannot resolve: delete
// it, and .NET takes the plain JavaScript path, loading everything through the harness.
delete globalThis.process;

// fetch, through the broker (ADR 0031): the http.fetch host call ctx.Http.Fetch makes, so only the
// hosts in uses.egress answer. HttpClient runs on it (.NET's browser handler calls fetch), which
// is why it is here: .NET's modules take fetch when they load. The same as session-harness.js's.
// dotnet-harness.js sets host.call; a fetch with no run going is refused there.
export const host = { call: undefined };
const NULL_BODY = new Set([101, 103, 204, 205, 304]);
globalThis.fetch = async (input, init) => {
  const request = new Request(input, init);
  const bytes = request.body === null ? null : new Uint8Array(await request.arrayBuffer());
  const timeoutMs = init?.timeoutMs;
  const r = await host.call("http.fetch", {
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
  Object.defineProperty(response, "url", { value: r.url ?? "" });
  return response;
};

// Every console line becomes a log line of the run. Emscripten binds console.log when .NET's
// modules load, so this has to be in place first; Console.WriteLine in a function lands here.
const encoder = new TextEncoder();
export const write = (message) => {
  const bytes = encoder.encode(JSON.stringify(message) + "\n");
  let offset = 0;
  while (offset < bytes.length) {
    offset += Deno.stdout.writeSync(bytes.subarray(offset));
  }
};
const text = (args) => args.map((a) => (typeof a === "string" ? a : String(a))).join(" ");
// Set by dotnet-harness.js between runs of a serving process: no run, no log.
export const quiet = { now: false };
for (const level of ["log", "info", "debug", "warn", "error"]) {
  console[level] = (...args) => quiet.now || write({ type: "log", level, message: text(args) });
}
for (const level of ["table", "dir", "trace"]) {
  console[level] = (...args) => quiet.now || write({ type: "log", level: "log", message: text(args) });
}
