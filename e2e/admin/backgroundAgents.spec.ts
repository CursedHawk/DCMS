import type { Page } from '@playwright/test';
import { test, expect } from '../fixtures/test';
import { anthropicStream, type ScriptedTurn } from '../fixtures/aiStream';

/**
 * Background agents (ADR 0033), in a real browser.
 *
 * <p>The HrSite conversation (2026-11-12) is the bug report these answer: a site agent whose page
 * was left kept running with nothing to show it, a second loop was started on the same
 * conversation, and an orphan wrote into whichever site the editor loaded next. Site agents now
 * live in a host that outlives the page, one per site, all visible from the assistant.</p>
 *
 * <p>Navigation is in-app throughout: `page.goto` reloads the tab, and a reload ends every run by
 * design (D1).</p>
 */

const A = '11111111-1111-1111-1111-111111111111';
const B = '22222222-2222-2222-2222-222222222222';
const NAMES: Record<string, string> = { [A]: 'Acme site', [B]: 'Beta site' };

const FILES = {
  'src/App.tsx': 'export default function App() {\n  return <main>Hello</main>;\n}\n',
  'src/main.tsx': "import App from './App';\n",
};

/**
 * Model calls answered by who is calling: each site's agent names its site in its prompt, and
 * the console assistant introduces itself. A `hold` turn waits until the test releases it, so
 * a run can be kept going while the test walks elsewhere.
 */
function scriptModel(page: Page, scripts: Record<string, ScriptedTurn[]>) {
  const turns: Record<string, number> = {};
  const held: (() => void)[] = [];
  let holding = true;
  const seen: string[] = [];
  void page.route('**/api/admin/ai/messages', async (route) => {
    const body = route.request().postData() ?? '';
    const who = body.includes('You are the assistant inside DCMS')
      ? 'assistant'
      : (Object.keys(scripts).find((name) => body.includes(name)) ?? 'unknown');
    seen.push(who);
    const list = scripts[who] ?? [{ text: 'nothing scripted' }];
    const index = turns[who] ?? 0;
    turns[who] = index + 1;
    const turn = list[Math.min(index, list.length - 1)] as ScriptedTurn & { hold?: boolean };
    if (turn.hold && holding) await new Promise<void>((resolve) => held.push(resolve));
    await route.fulfill({ status: 200, contentType: 'text/event-stream', body: anthropicStream(turn) });
  });
  return {
    seen,
    release: () => {
      holding = false;
      for (const resolve of held.splice(0)) resolve();
    },
  };
}

test.beforeEach(async ({ api }) => {
  let conversation = 0;
  api
    .on('GET', '/api/admin/sites', [
      { id: A, name: NAMES[A], renderMode: 'ReactApp', environmentKey: 'production' },
      { id: B, name: NAMES[B], renderMode: 'ReactApp', environmentKey: 'production' },
    ])
    .on('GET', '/api/admin/sites/:id', ({ params }) => ({
      id: params.id,
      name: NAMES[params.id],
      renderMode: 'ReactApp',
      slug: NAMES[params.id].toLowerCase().replace(' ', '-'),
    }))
    .on('GET', '/api/admin/sites/:id/ide', {
      branch: 'main',
      baseSha: 'abc1234',
      version: 3,
      files: FILES,
      hashes: Object.fromEntries(Object.keys(FILES).map((p) => [p, `h-${p}`])),
    })
    .on('GET', '/api/admin/sites/:id/git/changes', [])
    .on('GET', '/api/admin/sites/:id/git/branches', [{ name: 'main', isDefault: true }])
    .on('GET', '/api/admin/sites/:id/git/history', [])
    .on('GET', '/api/admin/sites/:id/builds', [])
    .on('GET', '/api/admin/ai/conversations', [])
    .on('POST', '/api/admin/ai/conversations', () => ({ id: `conv-${++conversation}`, title: 'x' }))
    .on('POST', '/api/admin/ai/conversations/:id/messages', { messageCount: 2, lastSeq: 2 })
    .on('PUT', '/api/admin/ai/conversations/:id/runs/:runId', { id: 'run-1', finished: true })
    .on('PATCH', '/api/admin/sites/:id/ide/files', ({ body }) => ({
      version: 4,
      hashes: Object.fromEntries(Object.keys((body as { put: object }).put).map((p) => [p, `h2-${p}`])),
    }));
});

async function openAgent(page: Page) {
  await expect(page.getByRole('tab', { name: /Problems/i })).toBeVisible({ timeout: 25000 });
  await page.getByRole('button', { name: /Hide preview/i }).click();
  await page.locator('button[title="Agent"]').first().click();
}

async function ask(page: Page, text: string) {
  await page.getByPlaceholder(/Describe a change/i).fill(text);
  await page.keyboard.press('Enter');
}

test('an agent keeps working after you leave its site, beside another site\'s agent', async ({ page, api }) => {
  const model = scriptModel(page, {
    'Acme site': [
      {
        hold: true,
        tool: { id: 't1', name: 'edit_file', input: { path: 'src/App.tsx', old_text: 'Hello', new_text: 'Get started' } },
      } as ScriptedTurn,
      { text: 'Changed the heading on Acme.' },
    ],
    'Beta site': [{ text: 'Nothing to change on Beta.' }],
  });

  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/sites/${A}`);
  await openAgent(page);
  await ask(page, 'Change the heading to Get started');
  await expect.poll(() => model.seen.includes('Acme site'), { timeout: 15000 }).toBe(true);

  // Leave Acme while its agent is mid-turn, for another site, inside the app.
  await page.getByRole('link', { name: 'Back to Sites' }).click();
  await page.getByRole('link', { name: /Beta site/ }).first().click();
  await openAgent(page);
  await ask(page, 'Add a footer');
  await expect(page.getByText('Nothing to change on Beta.')).toBeVisible({ timeout: 15000 });

  // Acme's agent is still at work, and the assistant button says so from Beta's editor.
  const button = page.getByRole('button', { name: /1 agent working/ });
  await expect(button).toBeVisible();
  await button.click();
  await page.getByRole('tab', { name: /Agents/ }).click();
  const dock = page.getByRole('dialog');
  await expect(dock.getByText('Acme site')).toBeVisible();
  await expect(dock.getByText('Beta site')).toBeVisible();

  model.release();
  await expect(dock.getByText('Finished')).toHaveCount(2, { timeout: 30000 });

  // Acme's edit went to Acme's draft, and nothing of it to Beta's — the orphan bug, inverted.
  const acme = api.requestsTo('PATCH', `/api/admin/sites/${A}/ide/files`);
  expect(acme.length).toBeGreaterThan(0);
  expect(JSON.stringify(acme.map((r) => r.body))).toContain('Get started');
  expect(JSON.stringify(api.requestsTo('PATCH', `/api/admin/sites/${B}/ide/files`).map((r) => r.body))).not.toContain(
    'Get started',
  );
});

test("the assistant hands a site change to that site's agent", async ({ page }) => {
  scriptModel(page, {
    assistant: [
      { tool: { id: 'a1', name: 'request_site_change', input: { siteId: A, task: 'Make the heading say Welcome' } } },
      { text: 'Asked the Acme site agent to change the heading.' },
    ],
    'Acme site': [
      { tool: { id: 't1', name: 'edit_file', input: { path: 'src/App.tsx', old_text: 'Hello', new_text: 'Welcome' } } },
      { text: 'The heading now says Welcome.' },
    ],
  });

  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto('/sites');
  await page.getByRole('button', { name: /Open the assistant/ }).click();
  const dock = page.getByRole('dialog');
  await dock.getByRole('textbox').fill('Make the Acme heading say Welcome');
  await page.keyboard.press('Enter');

  // Handing work to an agent is a decision, so it is asked for.
  await dock.getByRole('button', { name: 'Allow', exact: true }).click({ timeout: 15000 });

  await expect(dock.getByText('Asked the Acme site agent')).toBeVisible({ timeout: 20000 });
  await dock.getByRole('tab', { name: /Agents/ }).click();
  await expect(dock.getByText('Acme site')).toBeVisible();
  await expect(dock.getByText('Asked by the assistant')).toBeVisible();
  await expect(dock.getByText('Finished')).toBeVisible({ timeout: 30000 });
});
