// The first module dotnet-harness.js imports, so it runs before any of .NET's (ADR 0030).
//
// Deno has a Node-compatible `process` global. dotnet.js takes it for Node and imports the bare
// specifier "process", which a process without npm, config or permissions cannot resolve: delete
// it, and .NET takes the plain JavaScript path, loading everything through the harness.
delete globalThis.process;

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
for (const level of ["log", "info", "debug", "warn", "error"]) {
  console[level] = (...args) => write({ type: "log", level, message: text(args) });
}
for (const level of ["table", "dir", "trace"]) {
  console[level] = (...args) => write({ type: "log", level: "log", message: text(args) });
}
