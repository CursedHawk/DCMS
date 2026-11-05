/**
 * What "New function" writes (ADR 0029, 0030): a TypeScript or C# function at its path, already
 * declaring when it runs and who may call it. The path under functions/ is the function's name
 * and its HTTP path, so `orders/total` is /api/{app}/fn/orders/total.
 */

export type Language = 'ts' | 'cs';
export type Start = 'manual' | 'http' | 'row' | 'message' | 'schedule' | 'every';
export type HttpAccess = 'public' | 'user' | 'role' | 'apiKey';
export type RowEvent = 'created' | 'updated' | 'deleted';

export interface NewFunction {
  language: Language;
  /** "orders/total": folders, then the function's own name. */
  path: string;
  description: string;
  start: Start;
  access: HttpAccess[];
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
  if (f.start === 'row' && !TABLE.test(f.table)) return 'ide.newFn.badTable';
  if (f.start === 'message' && !TOPIC.test(f.topic)) return 'ide.newFn.badTopic';
  if (f.start === 'schedule' && f.cron.trim().split(/\s+/).length !== 5) return 'ide.newFn.badCron';
  if (f.start === 'every' && !INTERVAL.test(f.interval)) return 'ide.newFn.badInterval';
  return null;
}

/** One line, quoted for either language: neither may break a string literal across lines. */
// eslint-disable-next-line no-control-regex -- control characters are exactly what it removes
const quote = (text: string) => JSON.stringify(text.replace(/[\u0000-\u001f\u007f\u0085\u2028\u2029]+/g, ' ').trim());

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
    ...(f.start === 'http' ? [`/** GET or POST /api/{this app's address}/fn/${f.path} */`] : []),
    'export default defineFunction({',
    ...(f.description.trim() ? [`  description: ${quote(f.description)},`] : []),
    ...(trigger ? [`  triggers: [${trigger}],`] : []),
    ...(f.start === 'http' ? [`  access: { http: [${f.access.map((a) => `"${a}"`).join(', ')}] },`] : []),
    ...(f.start === 'row' ? [`  uses: { tables: { ${quote(f.table)}: ["read"] } },`] : []),
    '  async handler(input, ctx) {',
    '    ctx.log.info("called with", input);',
    '    return { ok: true };',
    '  },',
    '});',
    '',
  ];
  return lines.join('\n');
}

const ACCESS = { public: 'Access.Public', user: 'Access.User', role: 'Access.Role', apiKey: 'Access.ApiKey' } as const;

function csharp(f: NewFunction): string {
  const segments = f.path.split('/');
  const own = segments[segments.length - 1];
  const space = ['Functions', ...segments.slice(0, -1).map(pascal)].join('.');
  const event = f.event[0].toUpperCase() + f.event.slice(1);
  const attributes = [
    `[Function("${own}"${f.description.trim() ? `, Description = ${quote(f.description)}` : ''})]`,
    ...(f.start === 'http' ? [`[Http(${f.access.map((a) => ACCESS[a]).join(', ')})]`] : []),
    ...(f.start === 'row' ? [`[OnRow(${quote(f.table)}, RowEvent.${event})]`, `[UsesTable(${quote(f.table)}, "read")]`] : []),
    ...(f.start === 'message' ? [`[OnMessage(${quote(f.topic)})]`] : []),
    ...(f.start === 'schedule' ? [`[OnSchedule(${quote(f.cron.trim())})]`] : []),
    ...(f.start === 'every' ? [`[OnEvery(${quote(f.interval)})]`] : []),
  ];
  return [
    `namespace ${space};`,
    '',
    ...(f.start === 'http' ? [`/// <summary>GET or POST /api/{this app's address}/fn/${f.path}</summary>`] : []),
    ...attributes,
    `public sealed class ${pascal(own)} : Function<JsonNode, object>`,
    '{',
    '    public override async Task<object> Run(JsonNode input, Context ctx)',
    '    {',
    '        await Task.CompletedTask;',
    '        ctx.Log.Info("called with", input);',
    '        return new { ok = true };',
    '    }',
    '}',
    '',
  ].join('\n');
}
