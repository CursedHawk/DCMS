import { test, expect } from '../fixtures/test';

/**
 * The Functions workbench in the IDE (ADR 0029, 0030; epic #601): the draft's functions and what
 * is live, a run of the draft with its host calls, the run history opened from the app's Runs
 * screen, an error frame that opens its file, and a replay of a past run against the draft.
 */

const REPO = '55555555-5555-5555-5555-555555555555';
const FILES = {
  'functions/hello.ts': [
    'import { defineFunction, on } from "@dcms/functions";',
    'import { fail } from "../lib/fail.ts";',
    'export default defineFunction({',
    '  triggers: [on.row("crm.deals").updated()],',
    '  access: { http: ["apiKey"] },',
    '  uses: { tables: { "crm.deals": ["read"] } },',
    '  handler: (input: { boom?: boolean }) => (input.boom ? fail("kaput") : { ok: true }),',
    '});',
    '',
  ].join('\n'),
  'lib/fail.ts': 'export function fail(why: string): never {\n  throw new Error(why);\n}\n',
};

const SPEC = {
  name: 'hello',
  runtime: 'deno-ts',
  description: null,
  triggers: [{ kind: 'row', table: 'crm.deals', event: 'updated' }],
  http: ['apiKey'],
  uses: { tables: { 'crm.deals': ['read'] }, contracts: [], egress: [], secrets: [], emits: [] },
  file: 'functions/hello.ts',
};

const FAILED_RUN = {
  id: 'r1',
  function: 'hello',
  deployment: 3,
  trigger: 'row',
  status: 'Failed',
  attempts: 3,
  error: 'Error: kaput\n    at fail (lib/fail.ts:2:9)\n    at handler (functions/hello.ts:7:52)',
  durationMs: 41,
  createdAt: '2026-11-05T08:00:00Z',
  finishedAt: '2026-11-05T08:00:01Z',
};

test('the IDE lists, runs, traces and replays a function', async ({ page, api }) => {
  test.setTimeout(120_000);
  api
    .on('GET', '/api/admin/repos/:id/ide', {
      branch: 'main',
      baseSha: 'abc1234',
      version: 1,
      files: FILES,
      hashes: Object.fromEntries(Object.keys(FILES).map((p) => [p, `h-${p}`])),
    })
    .on('PATCH', '/api/admin/repos/:id/ide/files', { version: 2, hashes: {} })
    .on('GET', '/api/admin/repos/:id/git/changes', [])
    .on('GET', '/api/admin/repos/:id/git/branches', [{ name: 'main', isDefault: true }])
    .on('GET', '/api/admin/repos/:id/git/history', [])
    .on('GET', '/api/admin/plugins/:slug/_functions/sdk', { typings: 'export {};', csharp: '', etag: '"e"' })
    .on('POST', '/api/admin/plugins/:slug/_functions/check', { ok: true, problems: [], functions: ['hello'], permissions: [], specs: [SPEC] })
    .on('GET', '/api/admin/plugins/:slug/_functions/deployments', [
      { id: 'd3', number: 3, commitSha: 'abcdef1234567', status: 'Active', functions: ['hello'], createdAt: '2026-11-05T07:00:00Z' },
    ])
    .on('GET', '/api/admin/plugins/:slug/_functions/runs', { items: [FAILED_RUN], page: 1, more: false })
    .on('GET', '/api/admin/plugins/:slug/_functions/runs/:id', {
      run: FAILED_RUN,
      input: { event: 'deals.updated', table: 'crm.deals', id: 'x', boom: true },
      trigger: { event: 'deals.updated' },
      result: null,
      logs: '[info] looking',
      calls: 1,
      correlationId: 'c',
      depth: 1,
      trace: [{ op: 'db', args: '{"path":["crm","deals","get"]}', ok: true, ms: 4 }],
    })
    .on('POST', '/api/admin/plugins/:slug/_functions/test', {
      status: 'ok',
      result: { ok: true },
      logs: '',
      calls: 1,
      durationMs: 37,
      trace: [{ op: 'db', args: '{"path":["crm","deals","get"],"args":["x"]}', ok: true, ms: 5 }],
    })
    .on('GET', '/api/admin/ai/conversations', []);

  await page.setViewportSize({ width: 1600, height: 950 });
  // Opened from the app's Runs screen: straight onto the Runs tab.
  await page.goto(`/repos/${REPO}/code?name=Automations&slug=automations&kind=functions&runs=hello`);
  const runs = page.getByTestId('runs-view');
  // The IDE's chunks compile on first visit to the dev server: as long as the other IDE specs wait.
  await expect(runs).toBeVisible({ timeout: 60_000 });

  // A failed run in full; its frame opens the file it names.
  await runs.getByRole('button', { name: /hello/ }).click();
  await expect(runs.getByTestId('run-trace')).toContainText('crm","deals","get');
  await runs.getByRole('button', { name: 'lib/fail.ts:2' }).click();
  await expect(page.locator('.monaco-editor .view-lines')).toContainText('throw new Error(why)');

  // TypeScript coloured as VS Code's Light+ does: the language service says `fail` is a function
  // and `why` a parameter; the grammar tells `throw` (control flow) from `function`.
  const colour = (text: string) =>
    page.locator('.monaco-editor .view-lines span > span').filter({ hasText: new RegExp(`^\\s*${text}\\s*$`) }).first().evaluate((e) => getComputedStyle(e).color);
  await expect.poll(() => colour('fail'), { timeout: 30_000 }).toBe('rgb(121, 94, 38)');
  await expect.poll(() => colour('why')).toBe('rgb(0, 16, 128)');
  expect(await colour('throw')).toBe('rgb(175, 0, 219)');
  expect(await colour('function')).toBe('rgb(0, 0, 255)');
  expect(await colour('string')).toBe('rgb(38, 127, 153)');

  // Another editor theme, for every editor on the page; kept for the next visit.
  await page.getByLabel('Editor colour theme').selectOption('monokai');
  await expect.poll(() => page.locator('.monaco-editor .monaco-editor-background').first().evaluate((e) => getComputedStyle(e).backgroundColor)).toBe('rgb(39, 40, 34)');
  expect(await colour('throw')).toBe('rgb(249, 38, 114)');
  expect(await page.evaluate(() => localStorage.getItem('dcms.editorTheme'))).toBe('monokai');

  // Replay it against the draft: the Run tab, filled in, runs and shows its host calls.
  await runs.getByRole('button', { name: /Replay against the draft/ }).click();
  const run = page.getByTestId('run-view');
  await expect(run.getByLabel('Input (JSON)')).toHaveValue(/"boom": true/);
  await run.getByRole('button', { name: /^Run$/ }).click();
  await expect(run.getByTestId('run-outcome')).toContainText('ok');
  await expect(run.getByTestId('run-trace')).toContainText('"args":["x"]');
  const sent = api.requests.find((r) => r.method === 'POST' && r.path.endsWith('/_functions/test'));
  expect(sent?.body).toMatchObject({ function: 'hello', input: { boom: true }, simulate: { trigger: { kind: 'row' } } });

  // The Functions view: the draft's functions as the build reads them, and what is live.
  await page.getByRole('button', { name: 'Functions' }).first().click();
  const view = page.getByTestId('functions-view');
  await expect(view.getByTestId('function-hello')).toContainText('When crm.deals is updated');
  await expect(view.getByTestId('function-hello')).toContainText('live');
  await expect(view).toContainText('Live: deployment 3 (abcdef1)');
});
