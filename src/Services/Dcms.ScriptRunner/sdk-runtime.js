// @dcms/functions at run time (ADR 0029): bundled into every function in place of the import.
// Static on purpose. The generated part of the SDK is its types; this only turns calls into host
// calls, which the broker checks against the function's `uses`, the deployer's permissions and
// the live configuration. Nothing here decides what is allowed.

const RUN = Symbol.for("dcms.run");
const call = (op, args) => globalThis.__dcms.call(op, args);

/** db.<app>.<table>.<method>(...) and plugins.<instance>.<contract>.<operation>(...): a path and its arguments. */
const chain = (op, path = []) =>
  new Proxy(function () {}, {
    get(_, key) {
      // Not thenable: awaiting a namespace must not look like a call.
      if (typeof key !== "string" || key === "then") return undefined;
      return chain(op, [...path, key]);
    },
    apply(_, __, args) {
      return call(op, { path, args });
    },
  });

const context = (c) =>
  Object.freeze({
    db: chain("db"),
    plugins: chain("contract"),
    functions: Object.freeze({ invoke: (name, input) => call("functions.invoke", { name, input }) }),
    jobs: Object.freeze({ enqueue: (fn, payload, options) => call("jobs.enqueue", { function: fn, payload, options }) }),
    messages: Object.freeze({ emit: (topic, payload) => call("messages.emit", { topic, payload }) }),
    secrets: Object.freeze({ get: (name) => call("secrets.get", { name }) }),
    http: Object.freeze({
      async fetch(url, init = {}) {
        const headers = init.headers instanceof Headers ? Object.fromEntries(init.headers) : init.headers;
        const r = await call("http.fetch", {
          url: String(url),
          method: init.method ?? "GET",
          headers: headers ?? {},
          body: init.body == null ? null : typeof init.body === "string" ? init.body : JSON.stringify(init.body),
        });
        return new Response(r.body, { status: r.status, headers: r.headers });
      },
    }),
    log: console,
    request: c.request ?? null,
    user: c.user ?? null,
    trigger: c.trigger ?? null,
    run: c.run ?? null,
  });

export function defineFunction(spec) {
  if (!spec || typeof spec.handler !== "function") {
    throw new Error("defineFunction needs a handler: defineFunction({ handler(input, ctx) { ... } })");
  }
  Object.defineProperty(spec, RUN, { value: (input, c) => spec.handler(input, context(c)) });
  return spec;
}

/** An HTTP answer of the function's own; anything else it returns is sent as JSON with 200. */
export function respond(status, body, headers) {
  return { $response: { status, body: body ?? null, headers: headers ?? {} } };
}

const rowEvent = (table, event) => (options = {}) => ({ kind: "row", table, event, ...options });

/** Triggers, as data: the build reads them, the host routes on them. */
export const on = Object.freeze({
  row: (table) => ({ created: rowEvent(table, "created"), updated: rowEvent(table, "updated"), deleted: rowEvent(table, "deleted") }),
  relation: (relationship) => ({
    created: () => ({ kind: "relation", relationship, event: "created" }),
    deleted: () => ({ kind: "relation", relationship, event: "deleted" }),
  }),
  message: (topic) => ({ kind: "message", topic }),
  schedule: (cron) => ({ kind: "schedule", cron }),
  every: (interval) => ({ kind: "every", interval }),
  event: (name) => ({ kind: "event", name }),
});
