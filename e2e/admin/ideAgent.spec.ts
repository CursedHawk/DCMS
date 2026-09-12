import { test, expect } from '../fixtures/test';
import { anthropicStream, type ScriptedTurn } from '../fixtures/aiStream';

/**
 * The IDE agent, end to end in a real browser.
 *
 * <p>Nothing else covers this. The loop runs in the browser, is driven by a streamed wire
 * format, and what it does with that stream is <b>edit the user's files</b> — so the unit tests
 * can prove the pieces and only this can prove they are wired together. The two paths here are
 * the ones that matter: a run that changes a file, and a run that is refused.</p>
 */

const SITE = '11111111-1111-1111-1111-111111111111';

const FILES = {
  'src/App.tsx': 'export default function App() {\n  return <main>Hello</main>;\n}\n',
  'src/main.tsx': "import App from './App';\n",
};

/** Queue one scripted model turn per call to the proxy, in order. */
async function scriptAgent(page: import('@playwright/test').Page, turns: ScriptedTurn[]) {
  let turn = 0;
  await page.route('**/api/admin/ai/messages', async (route) => {
    const script = turns[Math.min(turn++, turns.length - 1)];
    await route.fulfill({
      status: 200,
      contentType: 'text/event-stream',
      body: anthropicStream(script),
    });
  });
}

test.beforeEach(async ({ api }) => {
  api
    .on('GET', '/api/admin/sites/:id', {
      id: SITE,
      name: 'Acme site',
      renderMode: 'ReactApp',
      slug: 'acme-site',
    })
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
    .on('POST', '/api/admin/ai/conversations', { id: 'conv-1', title: 'Change the copy' })
    .on('POST', '/api/admin/ai/conversations/:id/messages', { messageCount: 2, lastSeq: 2 })
    .on('PUT', '/api/admin/ai/conversations/:id/runs/:runId', { id: 'run-1', finished: true })
    // Autosave. The agent writes through the same draft path the editor does, so a run that
    // changes a file produces one of these.
    .on('PATCH', '/api/admin/sites/:id/ide/files', {
      version: 4,
      hashes: Object.fromEntries(Object.keys(FILES).map((p) => [p, `h2-${p}`])),
    });
});

/**
 * Open the IDE with the preview pane closed, then open the agent panel.
 *
 * <p><b>The preview is hidden on purpose.</b> It bundles with esbuild-wasm, which cannot
 * initialise under the mocked harness — every build fails with `esbuild.initialize is not a
 * function`, and the agent's build gate then does exactly what it should: retries the fix three
 * times and gives up. That is correct behaviour and it is not what these tests are about.</p>
 *
 * <p>With the pane closed, `previewAvailable()` is false and the gate reports the run as
 * unverified rather than broken — a real configuration, not a test-only mode. The gate's own
 * behaviour is covered in `buildBroker.test.ts` and in the runtime's unit tests.</p>
 */
async function openAgent(page: import('@playwright/test').Page) {
  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/sites/${SITE}`);
  await expect(page.getByRole('tab', { name: /Problems/i })).toBeVisible({ timeout: 25000 });
  await page.getByRole('button', { name: /Hide preview/i }).click();
  await page.locator('button[title="Agent"]').first().click();
}

test('the agent edits a file, and the change is reviewable', async ({ page }) => {
  await scriptAgent(page, [
    {
      thinking: 'The heading is in App.tsx.',
      tool: {
        id: 't1',
        name: 'edit_file',
        input: { path: 'src/App.tsx', old_text: 'Hello', new_text: 'Get started' },
      },
    },
    { text: 'Changed the heading to "Get started".' },
  ]);

  await openAgent(page);
  await page.getByPlaceholder(/Describe a change/i).fill('Change the heading to Get started');
  await page.keyboard.press('Enter');

  // The tool card, with the call the model actually made rather than a grey line saying
  // "edit_file" and nothing else.
  const card = page.getByRole('button', { name: /edit_file/ });
  await expect(card).toBeVisible({ timeout: 15000 });

  // The model's own arguments are one click away.
  await card.click();
  await expect(page.getByText(/Get started/).first()).toBeVisible();

  // The edit actually reached the workspace, which is the only claim that matters.
  await expect(page.getByText('Changed the heading')).toBeVisible();

  // Generous, because the change list appears only once the run is over, and the run ends by
  // waiting on the build gate. Both halves of that gate come back "not checked" here — the
  // preview is closed, and Monaco's type worker never answers under the mocked harness — so the
  // run finishes when the type check gives up rather than immediately. That the gate HAS a
  // deadline is the point: before it did not, and a silent worker parked the run forever.
  await expect(page.getByRole('heading', { name: /file changed/i })).toBeVisible({
    timeout: 20000,
  });
});

test('Careful mode shows the change itself, and takes it on the keyboard', async ({ page }) => {
  /*
   * The approval gate, which is the one screen where a person decides what the agent may do to
   * their site. It used to list `edit_file src/App.tsx` over a JSON blob — a change you approve
   * by reading the name of the function that makes it. This asserts the two things that replaced
   * that: the diff is on the card, and Enter is enough to answer it.
   */
  await scriptAgent(page, [
    {
      tool: {
        id: 't1',
        name: 'edit_file',
        input: { path: 'src/App.tsx', old_text: 'Hello', new_text: 'Get started' },
      },
    },
    { text: 'Done.' },
  ]);

  await openAgent(page);
  // Careful gates every write, not just the dangerous ones.
  await page.getByLabel(/Agent mode/i).selectOption('careful');
  await page.getByPlaceholder(/Describe a change/i).fill('Change the heading');
  await page.keyboard.press('Enter');

  const card = page.getByRole('dialog', { name: /Apply these changes/i });
  await expect(card).toBeVisible({ timeout: 15000 });

  // The change, not the tool call: the line leaving and the line arriving, and where.
  await expect(card.getByText('Hello', { exact: true })).toBeVisible();
  await expect(card.getByText('Get started', { exact: true })).toBeVisible();
  await expect(card.getByText(/src\/App\.tsx:2/)).toBeVisible();
  await expect(card.getByText('+1')).toBeVisible();

  // Three answers, not two.
  await expect(card.getByRole('button', { name: /Allow for this run/i })).toBeVisible();

  // The card has focus already, so the run is answerable without reaching for the mouse.
  await page.keyboard.press('Enter');
  await expect(card).toBeHidden();
  await expect(page.getByRole('heading', { name: /file changed/i })).toBeVisible({
    timeout: 20000,
  });
});

test('a refused change is not applied', async ({ page }) => {
  await scriptAgent(page, [
    {
      tool: {
        id: 't1',
        name: 'edit_file',
        input: { path: 'src/App.tsx', old_text: 'Hello', new_text: 'Get started' },
      },
    },
    { text: 'Understood.' },
  ]);

  await openAgent(page);
  await page.getByLabel(/Agent mode/i).selectOption('careful');
  await page.getByPlaceholder(/Describe a change/i).fill('Change the heading');
  await page.keyboard.press('Enter');

  const card = page.getByRole('dialog', { name: /Apply these changes/i });
  await expect(card).toBeVisible({ timeout: 15000 });
  await page.keyboard.press('Escape');

  // Nothing was written, so there is nothing to review — and the run says so rather than
  // carrying on as though it had made the change.
  await expect(card).toBeHidden();
  await expect(page.getByRole('heading', { name: /file changed/i })).toHaveCount(0);
});

test('an edit the model gets wrong is reported, not swallowed', async ({ page }) => {
  // `old_text` that is not in the file. The tool must fail loudly enough that the transcript
  // shows it — a silent no-op would leave the model believing it had made a change.
  await scriptAgent(page, [
    {
      tool: {
        id: 't1',
        name: 'edit_file',
        input: { path: 'src/App.tsx', old_text: 'nothing like this', new_text: 'x' },
      },
    },
    { text: 'That anchor was not in the file.' },
  ]);

  await openAgent(page);
  await page.getByPlaceholder(/Describe a change/i).fill('rename the thing');
  await page.keyboard.press('Enter');

  await expect(page.getByRole('button', { name: /edit_file/ })).toBeVisible({ timeout: 15000 });
  await expect(page.getByText('That anchor was not in the file.')).toBeVisible();
  // Nothing was changed, so there is nothing to review.
  await expect(page.getByRole('heading', { name: /file changed/i })).toHaveCount(0);
});

test('a usage limit stops the run and says when to come back', async ({ page }) => {
  // The wall from 11.4, seen from the browser. A limit the panel reports as a generic failure
  // is one the user retries into.
  await page.route('**/api/admin/ai/messages', (route) =>
    route.fulfill({
      status: 429,
      contentType: 'application/json',
      headers: { 'Retry-After': '60' },
      body: JSON.stringify({
        error: 'rate_limited',
        message: 'Too many AI requests. The limit is 60 per minute; try again shortly.',
      }),
    }),
  );

  await openAgent(page);
  await page.getByPlaceholder(/Describe a change/i).fill('do something');
  await page.keyboard.press('Enter');

  await expect(page.getByText(/Too many AI requests/)).toBeVisible({ timeout: 15000 });
});

test('a conflicting save is not overwritten', async ({ page, api }) => {
  /*
   * The conflict path. Two tabs, or a colleague, or the agent — the editor must notice that the
   * server's copy moved rather than flattening it, and must say which files.
   */
  api.on('PATCH', '/api/admin/sites/:id/ide/files', ({ route }) =>
    route.fulfill({
      status: 409,
      contentType: 'application/json',
      // The server's own shape — `{ path, current }`, not a bare string. See
      // `SiteEndpoints.cs:386`; a fixture that simplifies it tests a client that does not exist.
      body: JSON.stringify({
        error: 'conflict',
        version: 4,
        conflicts: [{ path: 'src/App.tsx', current: FILES['src/App.tsx'] }],
      }),
    }),
  );

  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/sites/${SITE}`);
  await expect(page.getByRole('tab', { name: /Problems/i })).toBeVisible({ timeout: 25000 });

  // Type into the editor so there is something to save; autosave carries it from there.
  await page.locator('.monaco-editor').first().click();
  await page.keyboard.type('// edited');

  // Named files, not a generic "save failed". Which file moved under you is the whole of what
  // makes a conflict actionable, and it is the one thing a toast usually leaves out.
  await expect(page.getByText(/Someone else changed these files/)).toBeVisible({ timeout: 20000 });
  await expect(page.getByText(/src\/App\.tsx/).first()).toBeVisible();
});
