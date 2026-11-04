// eslint-disable-next-line @typescript-eslint/ban-ts-comment -- untyped on purpose: the flow language had no types.
// @ts-nocheck -- written for code converted from Dynamic Apps flows; edit freely.
// The flow expression language's rules (ADR 0021), so converted functions behave as their flows
// did: reading a missing field gives null, arithmetic with null gives null, text compares and
// concatenates the way the language did, and its built-in functions mean what they meant.

export const truthy = (v) =>
  v == null ? false
  : Array.isArray(v) ? v.length > 0
  : typeof v === "object" ? true
  : typeof v === "boolean" ? v
  : typeof v === "number" ? v !== 0
  : typeof v === "string" ? v.length > 0
  : true;

const isObject = (v) => v != null && typeof v === "object" && !Array.isArray(v);
const isNumber = (v) => typeof v === "number" && Number.isFinite(v);

export const get = (target, name) => (isObject(target) ? (target[name] ?? null) : null);

export const at = (target, key) =>
  Array.isArray(target) && isNumber(key) && Number.isInteger(key) && key >= 0 && key < target.length ? (target[key] ?? null)
  : isObject(target) && typeof key === "string" ? (target[key] ?? null)
  : null;

const scalar = (v) => {
  if (v != null && typeof v === "object") {
    throw new Error("Comparisons take plain values (text, numbers, true/false, null); compare a field instead");
  }
  return v;
};

export const list = (...items) => items.map((i) => (i == null ? null : scalar(i)));

export const eq = (a, b) => (a == null || b == null ? a == null && b == null : scalar(a) === scalar(b));
export const ne = (a, b) => !eq(a, b);

export const isIn = (a, b) => {
  scalar(a);
  if (Array.isArray(b)) return b.some((i) => (i == null || typeof i !== "object") && (i ?? null) === (a ?? null));
  if (isObject(b)) return typeof a === "string" && Object.hasOwn(b, a);
  return typeof b === "string" && typeof a === "string" && b.includes(a);
};

export const display = (v) =>
  v == null ? "" : typeof v === "string" ? v : typeof v === "number" || typeof v === "boolean" ? String(v) : JSON.stringify(v);

const number = (v) => {
  if (v == null) return null;
  if (isNumber(v)) return v;
  throw new Error(`${JSON.stringify(v)} is not a number`);
};

const arith = (a, b, f) => {
  const x = number(a), y = number(b);
  return x == null || y == null ? null : f(x, y);
};

export const add = (a, b) => (typeof a === "string" || typeof b === "string" ? display(a) + display(b) : arith(a, b, (x, y) => x + y));
export const sub = (a, b) => arith(a, b, (x, y) => x - y);
export const mul = (a, b) => arith(a, b, (x, y) => x * y);
export const div = (a, b) => arith(a, b, (x, y) => { if (y === 0) throw new Error("Division by zero"); return x / y; });
export const mod = (a, b) => arith(a, b, (x, y) => { if (y === 0) throw new Error("Division by zero"); return x % y; });
export const neg = (v) => { const n = number(v); return n == null ? null : -n; };

const compare = (a, b) =>
  isNumber(a) && isNumber(b) ? Math.sign(a - b)
  : typeof a === "string" && typeof b === "string" ? (a < b ? -1 : a > b ? 1 : 0)
  : null;
export const lt = (a, b) => { const o = compare(a, b); return o != null && o < 0; };
export const le = (a, b) => { const o = compare(a, b); return o != null && o <= 0; };
export const gt = (a, b) => { const o = compare(a, b); return o != null && o > 0; };
export const ge = (a, b) => { const o = compare(a, b); return o != null && o >= 0; };

// ------------------------------------------------------------------ built-in functions

const text = (v) => (typeof v === "string" ? v : null);
export const len = (v) =>
  Array.isArray(v) ? v.length : isObject(v) ? Object.keys(v).length : v == null ? 0 : typeof v === "string" ? v.length : display(v).length;
export const lower = (v) => text(v)?.toLowerCase() ?? null;
export const upper = (v) => text(v)?.toUpperCase() ?? null;
export const trim = (v) => text(v)?.trim() ?? null;
export const contains = (s, p) => text(s) != null && text(p) != null && s.toLowerCase().includes(p.toLowerCase());
export const startsWith = (s, p) => text(s) != null && text(p) != null && s.toLowerCase().startsWith(p.toLowerCase());
export const endsWith = (s, p) => text(s) != null && text(p) != null && s.toLowerCase().endsWith(p.toLowerCase());
export const coalesce = (...args) => args.find((a) => a != null) ?? null;
export const str = (v) => (v == null ? null : display(v));
export const num = (v) => {
  if (isNumber(v)) return v;
  if (typeof v === "string" && /^\s*[+-]?(\d{1,3}(,\d{3})+|\d*)(\.\d+)?\s*$/.test(v) && /\d/.test(v)) return Number(v.replaceAll(",", ""));
  return null;
};
export const round = (v, digits) => {
  const n = number(v);
  if (n == null) return null;
  const d = digits == null ? 0 : Math.min(10, Math.max(0, Math.trunc(number(digits) ?? 0)));
  const f = 10 ** d;
  return (Math.sign(n) * Math.round(Math.abs(n) * f)) / f;
};
export const now = () => new Date().toISOString();
export const today = () => new Date().toISOString().slice(0, 10);
const moved = (v, ms, name) => {
  const t = text(v);
  if (t != null && /^\d{4}-\d{2}-\d{2}$/.test(t)) {
    if (name === "addHours") throw new Error("addHours() needs a date and time, not a date");
    return new Date(Date.parse(t + "T00:00:00Z") + Math.trunc(ms / 86_400_000) * 86_400_000).toISOString().slice(0, 10);
  }
  const at = t == null ? NaN : Date.parse(t);
  return Number.isNaN(at) ? null : new Date(at + ms).toISOString();
};
export const addDays = (v, days) => moved(v, (number(days) ?? 0) * 86_400_000, "addDays");
export const addHours = (v, hours) => moved(v, (number(hours) ?? 0) * 3_600_000, "addHours");
export const join = (items, separator) => (Array.isArray(items) ? items.map(display).join(text(separator) ?? ", ") : null);

// ------------------------------------------------------------------ templates

const piece = (v) => (v == null ? "" : typeof v === "string" ? v : JSON.stringify(v));
const html = (s) => s.replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;").replaceAll('"', "&quot;").replaceAll("'", "&#39;");

/** A text template: each hole's value as text. */
export const t = (parts, ...values) => parts.reduce((out, part, i) => out + part + (i < values.length ? piece(values[i]) : ""), "");

/** An HTML template: the template's own markup kept, each hole's value escaped. */
export const h = (parts, ...values) => parts.reduce((out, part, i) => out + part + (i < values.length ? html(piece(values[i])) : ""), "");

// ------------------------------------------------------------------ what a flow's run saw

/** The names a flow's expressions read, from what the function was started with. */
export const scope = (kind, eventName, input, ctx) => {
  const payload = isObject(input) ? input : {};
  const fromRow = (kind === "row" || kind === "relation") && typeof payload.event === "string";
  const event = { eventName: fromRow ? payload.event : eventName, payload: kind === "manual" ? { input } : payload };
  return {
    event,
    row: payload.record ?? null,
    previous: payload.previous ?? null,
    changedFields: Array.isArray(payload.changedFields) ? payload.changedFields : [],
    input: kind === "manual" ? (input ?? null) : (payload.input ?? null),
    run: { id: null, flow: ctx.run?.function ?? null, depth: ctx.run?.depth ?? 0 },
  };
};

// ------------------------------------------------------------------ actions with no one-line equivalent

export const first = (page) => page?.items?.[0] ?? null;
export const page = (p) => ({ items: p.items, total: p.total, totalCapped: p.totalCapped });

/** user-auth.invite@1: someone who already has an account is left as they are. */
export const invite = async (directory, input) => {
  const email = String(input.email ?? "").trim();
  const found = await findUser(directory, email);
  if (found) return found;
  const groups = [];
  for (const name of input.groups ?? []) groups.push(await groupId(directory, name));
  return directory.invite({ email, displayName: input.displayName ?? null, groups });
};

/** user-auth.add-to-group@1 and remove-from-group@1: by email, and a group's id or name. */
export const membership = async (directory, add, input) => {
  const email = String(input.email ?? "").trim();
  const user = await findUser(directory, email);
  if (!user) throw new Error(`nobody with the email ${email} can sign in here.`);
  const membership = { userId: user.id, groupId: await groupId(directory, input.group ?? "") };
  await (add ? directory.addToGroup(membership) : directory.removeFromGroup(membership));
  return findUser(directory, email);
};

const findUser = async (directory, email) =>
  (await directory.listUsers({ search: email, page: 1, pageSize: 100 })).items.find((u) => u.email?.toLowerCase() === email.toLowerCase()) ?? null;

const groupId = async (directory, group) => {
  const { groups } = await directory.listGroups();
  const found = groups.find((g) => g.id === group) ?? groups.find((g) => g.name.toLowerCase() === String(group).trim().toLowerCase());
  if (!found) throw new Error(`there is no group '${group}'.`);
  return found.id;
};
