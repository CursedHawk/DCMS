// The only file a flow script's Deno process loads from disk (ADR 0025). The script itself
// arrives on stdin and is imported from a data: URL at run time: a module graph's statically
// analysable local imports are loaded without --allow-read, but everything a runtime-imported
// module imports is permission-checked — so the script can read no file, this one included.
//
// stdin:  {"code": "...", "ctx": {...}, "language": "ts" | "js"}
// stdout: one line, {"ok": true, "result": ...} or {"ok": false, "error": "..."}
// The script's own console output is dropped: stdout carries the result line and nothing else.

const write = console.log.bind(console);
for (const level of ["log", "info", "debug", "warn", "error", "table", "dir", "trace"]) {
  console[level] = () => {};
}

const { code, ctx, language } = JSON.parse(await new Response(Deno.stdin.readable).text());
const bytes = new TextEncoder().encode(code);
let binary = "";
for (let i = 0; i < bytes.length; i += 0x8000) {
  binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
}

try {
  const type = language === "js" ? "application/javascript" : "application/typescript";
  const module = await import(`data:${type};base64,` + btoa(binary));
  if (typeof module.default !== "function") {
    throw new Error("The script must export a default function: export default function main(ctx) { … }");
  }
  const result = await module.default(ctx);
  write(JSON.stringify({ ok: true, result: result === undefined ? null : result }));
} catch (e) {
  // A compile error names the module by its data: URL — the whole script, base64-encoded.
  const message = String(e instanceof Error ? e.message : e).replace(/data:application\/(type|java)script;base64,[A-Za-z0-9+/=]+/g, "script");
  write(JSON.stringify({ ok: false, error: message }));
}
