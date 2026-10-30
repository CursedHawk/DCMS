import { api } from '../../../lib/api';
import type { ToolSpec } from '../../agent/contracts';
import { useVfs } from '../../site-source';
import type { TenantToolContext } from './tenantTools';

/**
 * The functions agent's own tools (ADR 0029): the server's build of the draft, a real run of a
 * function from it, and the SDK as generated for this environment. `base` is the Functions app's
 * admin path, `/admin/plugins/{slug}/_functions`.
 */

interface Problem {
  message: string;
  file?: string | null;
  line?: number | null;
}

interface Check {
  ok: boolean;
  problems: Problem[];
  functions: string[];
  permissions: string[];
}

const draft = () => useVfs.getState().files;

export async function checkDraft(base: string): Promise<{ ok: boolean; report: string }> {
  const check = await api.post<Check>(`${base}/check`, { files: draft() });
  if (check.ok) {
    return {
      ok: true,
      report: `Builds. Functions: ${check.functions.join(', ') || 'none'}. Deploying it takes: ${check.permissions.join(', ')}.`,
    };
  }
  const lines = check.problems.slice(0, 25).map((p) => (p.file ? `${p.file}${p.line ? `:${p.line}` : ''}: ${p.message}` : p.message));
  return { ok: false, report: `${check.problems.length} problem(s):\n${lines.join('\n')}` };
}

export function functionsTools(base: string): ToolSpec<TenantToolContext>[] {
  return [
    {
      name: 'functions_check',
      description:
        'Type-check and build the whole draft on the server exactly as a deployment is built: every problem with its file and line, which functions it defines and what deploying it would take. Run it after every change; the compiler is never wrong about whether it builds.',
      input_schema: { type: 'object', properties: {}, additionalProperties: false },
      describe: () => 'check functions',
      run: async () => {
        const { ok, report } = await checkDraft(base);
        return { content: report, isError: !ok };
      },
    },
    {
      name: 'functions_test_run',
      description:
        'Run one function of the draft now with an input, built on the spot. It runs FOR REAL with the member\'s permissions: database writes, messages and HTTP calls happen. Returns its status, result, error and log.',
      input_schema: {
        type: 'object',
        properties: {
          function: { type: 'string', description: 'The function: functions/{name}.ts is {name}.' },
          input: { description: 'What the function receives as its input.' },
        },
        required: ['function'],
        additionalProperties: false,
      },
      risk: 'dangerous',
      describe: (input) => `run ${String(input.function ?? '')}`,
      run: async (input) => {
        const result = await api.post<{ status: string; result?: unknown; error?: string | null; logs: string }>(`${base}/test`, {
          files: draft(),
          function: String(input.function ?? ''),
          input: input.input ?? null,
        });
        const body = [
          `Status: ${result.status}`,
          result.error ? `Error: ${result.error}` : `Result: ${JSON.stringify(result.result ?? null)}`,
          result.logs ? `Log:\n${result.logs}` : 'Log: (empty)',
        ].join('\n');
        return { content: body, isError: result.status !== 'ok' };
      },
    },
    {
      name: 'functions_sdk',
      description:
        'The TypeScript declarations of "@dcms/functions" for this environment: defineFunction, the triggers, `uses` (the capability grant), and the context with ctx.db.<app>.<table> typed from the published tables. Read it before writing a function.',
      input_schema: { type: 'object', properties: {}, additionalProperties: false },
      describe: () => 'read the SDK',
      run: async () => {
        const { typings } = await api.get<{ typings: string }>(`${base}/sdk`);
        return { content: typings.length > 60_000 ? `${typings.slice(0, 60_000)}\n… (truncated)` : typings };
      },
    },
  ];
}
