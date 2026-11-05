/**
 * What "New function" writes (ADR 0029, 0030): a TypeScript or C# function at its path, already
 * declaring when it runs and who may call it. The path under functions/ is the function's name
 * and its HTTP path, so `orders/total` is /api/{app}/fn/orders/total.
 */

export type Language = 'ts' | 'cs';
export type Start = 'manual' | 'http' | 'row' | 'message' | 'schedule' | 'every';
export type HttpAccess = 'public' | 'user' | 'role' | 'apiKey';
export type RowEvent = 'created' | 'updated' | 'deleted';
export type Method = 'get' | 'post' | 'put' | 'patch' | 'delete';
export const METHODS: Method[] = ['get', 'post', 'put', 'patch', 'delete'];
/** What an HTTP function answers when it declares nothing. */
const DEFAULT_METHODS: Method[] = ['get', 'post'];

export interface NewFunction {
  language: Language;
  /** "orders/total": folders, then the function's own name. */
  path: string;
  description: string;
  start: Start;
  access: HttpAccess[];
  methods: Method[];
  table: string;
  event: RowEvent;
  topic: string;
  cron: string;
  interval: string;
}

/** The build's rule (FunctionBuilder.FunctionName): up to four folders, 64 characters. */
export const FUNCTION_NAME = /^(?=.{1,64}$)[a-z][a-z0-9-]*(?:\/[a-z][a-z0-9-]*){0,3}$/;
const TABLE = /^[a-z0-9][a-z0-9-]{0,62}\.[A-Za-z_][A-Za-z0-9_]{0,63}$/;
const TOPIC = /^[a-z0-9][a-z0-9._-]{0,127}$/;
const INTERVAL = /^\d{1,4}(m|h|d)$/;

const pascal = (segment: string) => segment.split('-').map((w) => w[0].toUpperCase() + w.slice(1)).join('');

/** Where the file goes: functions/orders/total.ts, or functions/orders/Total.cs. */
export function fileOf(f: Pick<NewFunction, 'language' | 'path'>): string {
  if (f.language === 'ts') return `functions/${f.path}.ts`;
  const segments = f.path.split('/');
  return ['functions', ...segments.slice(0, -1), `${pascal(segments[segments.length - 1])}.cs`].join('/');
}

/** What is wrong with the form, as a message key, or null. */
export function problemOf(f: NewFunction): string | null {
  if (!FUNCTION_NAME.test(f.path)) return 'ide.newFn.badPath';
  if (f.start === 'http' && f.access.length === 0) return 'ide.newFn.noAccess';
  if (f.start === 'http' && f.methods.length === 0) return 'ide.newFn.noMethods';
  if (f.start === 'row' && !TABLE.test(f.table)) return 'ide.newFn.badTable';
  if (f.start === 'message' && !TOPIC.test(f.topic)) return 'ide.newFn.badTopic';
  if (f.start === 'schedule' && f.cron.trim().split(/\s+/).length !== 5) return 'ide.newFn.badCron';
  if (f.start === 'every' && !INTERVAL.test(f.interval)) return 'ide.newFn.badInterval';
  return null;
}

/** One line, quoted for either language: neither may break a string literal across lines. */
// eslint-disable-next-line no-control-regex -- control characters are exactly what it removes
const quote = (text: string) => JSON.stringify(text.replace(/[\u0000-\u001f\u007f\u0085\u2028\u2029]+/g, ' ').trim());

/** Declared only when they are not what an HTTP function answers anyway. */
const declaredMethods = (f: NewFunction) =>
  f.start === 'http' && !(f.methods.length === DEFAULT_METHODS.length && DEFAULT_METHODS.every((m) => f.methods.includes(m)))
    ? METHODS.filter((m) => f.methods.includes(m))
    : [];

export function starterOf(f: NewFunction): string {
  return f.language === 'ts' ? typescript(f) : csharp(f);
}

function typescript(f: NewFunction): string {
  const trigger = {
    manual: null,
    http: null,
    row: `on.row(${quote(f.table)}).${f.event}()`,
    message: `on.message(${quote(f.topic)})`,
    schedule: `on.schedule(${quote(f.cron.trim())})`,
    every: `on.every(${quote(f.interval)})`,
  }[f.start];
  const lines = [
    `import { defineFunction${trigger ? ', on' : ''} } from "@dcms/functions";`,
    '',
    ...(f.start === 'http' ? [`/** ${METHODS.filter((m) => f.methods.includes(m)).map((m) => m.toUpperCase()).join(', ')} /api/{this app's address}/fn/${f.path} */`] : []),
    'export default defineFunction({',
    ...(f.description.trim() ? [`  description: ${quote(f.description)},`] : []),
    ...(trigger ? [`  triggers: [${trigger}],`] : []),
    ...(f.start === 'http'
      ? [`  access: { http: [${f.access.map((a) => `"${a}"`).join(', ')}]${declaredMethods(f).length ? `, methods: [${declaredMethods(f).map((m) => `"${m}"`).join(', ')}]` : ''} },`]
      : []),
    ...(f.start === 'row' ? [`  uses: { tables: { ${quote(f.table)}: ["read"] } },`] : []),
    // Over HTTP its types are its API: the site's OpenAPI document and client say them, and a call that does not fit gets 400.
    ...(f.start === 'http'
      ? [
          '  async handler(input: { name?: string }, ctx): Promise<{ message: string }> {',
          '    ctx.log.info("called with", input);',
          '    return { message: `Hello, ${input.name ?? "world"}!` };',
        ]
      : ['  async handler(input, ctx) {', '    ctx.log.info("called with", input);', '    return { ok: true };']),
    '  },',
    '});',
    '',
  ];
  return lines.join('\n');
}

const ACCESS = { public: 'Access.Public', user: 'Access.User', role: 'Access.Role', apiKey: 'Access.ApiKey' } as const;
const METHOD = { get: 'HttpMethod.Get', post: 'HttpMethod.Post', put: 'HttpMethod.Put', patch: 'HttpMethod.Patch', delete: 'HttpMethod.Delete' } as const;

function csharp(f: NewFunction): string {
  const segments = f.path.split('/');
  const own = segments[segments.length - 1];
  const space = ['Functions', ...segments.slice(0, -1).map(pascal)].join('.');
  const event = f.event[0].toUpperCase() + f.event.slice(1);
  const attributes = [
    `[Function("${own}"${f.description.trim() ? `, Description = ${quote(f.description)}` : ''})]`,
    ...(f.start === 'http'
      ? [`[Http(${f.access.map((a) => ACCESS[a]).join(', ')}${declaredMethods(f).length ? `, Methods = [${declaredMethods(f).map((m) => METHOD[m]).join(', ')}]` : ''})]`]
      : []),
    ...(f.start === 'row' ? [`[OnRow(${quote(f.table)}, RowEvent.${event})]`, `[UsesTable(${quote(f.table)}, "read")]`] : []),
    ...(f.start === 'message' ? [`[OnMessage(${quote(f.topic)})]`] : []),
    ...(f.start === 'schedule' ? [`[OnSchedule(${quote(f.cron.trim())})]`] : []),
    ...(f.start === 'every' ? [`[OnEvery(${quote(f.interval)})]`] : []),
  ];
  const type = pascal(own);
  // Over HTTP its types are its API: the site's OpenAPI document and client say them, and a call that does not fit gets 400.
  const http = f.start === 'http';
  const [input, output] = http ? [`${type}Input`, `${type}Output`] : ['JsonNode', 'object'];
  return [
    `namespace ${space};`,
    '',
    ...(http ? [`public sealed record ${input}(string? Name);`, `public sealed record ${output}(string Message);`, ''] : []),
    ...(http ? [`/// <summary>${METHODS.filter((m) => f.methods.includes(m)).map((m) => m.toUpperCase()).join(', ')} /api/{this app's address}/fn/${f.path}</summary>`] : []),
    ...attributes,
    `public sealed class ${type} : Function<${input}, ${output}>`,
    '{',
    `    public override async Task<${output}> Run(${input} input, Context ctx)`,
    '    {',
    '        await Task.CompletedTask;',
    '        ctx.Log.Info("called with", input);',
    http ? '        return new($"Hello, {input.Name ?? "world"}!");' : '        return new { ok = true };',
    '    }',
    '}',
    '',
  ].join('\n');
}
