import { test, expect } from '../fixtures/test';
import { anthropicStream, type ScriptedTurn } from '../fixtures/aiStream';
import { HOME, doc, open, saved } from '../fixtures/visualSite';

/**
 * The Mode D agent (P5), end to end: a model turn calls a structured tool, the tool edits the
 * page document through the run transaction, and the author sees it on the canvas and in the
 * saved file. A refused placement must come back to the model as an error, not a broken page.
 */

const HOME_PAGE = {
  'dcms/app.json': doc({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }] }),
  [HOME]: doc({
    schemaVersion: 1,
    id: 'home',
    title: 'Home',
    root: { id: 'r', type: 'dcms.page', slots: { default: [{ id: 'h', type: 'dcms.heading', props: { text: 'Welcome' } }] } },
  }),
};

async function scriptAgent(page: import('@playwright/test').Page, turns: ScriptedTurn[]) {
  let turn = 0;
  await page.route('**/api/admin/ai/messages', (route) =>
    route.fulfill({ status: 200, contentType: 'text/event-stream', body: anthropicStream(turns[Math.min(turn++, turns.length - 1)]!) }),
  );
}

test.beforeEach(({ api }) => {
  api
    .on('GET', '/api/admin/ai/conversations', [])
    .on('POST', '/api/admin/ai/conversations', { id: 'conv-1', title: 'Add a hero' })
    .on('POST', '/api/admin/ai/conversations/:id/messages', { messageCount: 2, lastSeq: 2 })
    .on('PUT', '/api/admin/ai/conversations/:id/runs/:runId', { id: 'run-1', finished: true });
});

test('the agent builds a section with components, and it lands on the canvas and in the page file', async ({ page, api }) => {
  await scriptAgent(page, [
    { tool: { id: 't0', name: 'inspect_document', input: { doc: 'page:home' } } },
    {
      tool: {
        id: 't1',
        name: 'insert_node',
        input: {
          doc: 'page:home',
          parent: 'r',
          slot: 'default',
          node: { type: 'dcms.section', slots: { default: [{ type: 'dcms.heading', props: { text: 'Built by the agent' } }] } },
        },
      },
    },
    // Refused: a page cannot go inside a page. The model is told why and the file is untouched.
    { tool: { id: 't2', name: 'insert_node', input: { doc: 'page:home', parent: 'r', slot: 'default', node: { type: 'dcms.page' } } } },
    { text: 'Added a section with a heading.' },
  ]);

  const frame = await open(page, api, HOME_PAGE);
  await page.getByRole('tab', { name: 'Agent' }).click();
  await page.getByPlaceholder(/Describe a change/i).fill('Add a section');
  await page.keyboard.press('Enter');

  await expect(frame.getByText('Built by the agent')).toBeVisible({ timeout: 20000 });
  await expect(page.getByText('Added a section with a heading.')).toBeVisible();
  await expect.poll(() => saved(api, HOME), { timeout: 15000 }).toContain('"text": "Built by the agent"');

  const home = JSON.parse(saved(api, HOME)!);
  expect(home.root.slots.default.map((n: { type: string }) => n.type)).toEqual(['dcms.heading', 'dcms.section']);
});
