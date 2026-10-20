import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test, expect } from '../fixtures/test';
import { HOME, doc, open, saved } from '../fixtures/visualSite';

/**
 * Developer components (P7, ADR 0020): TSX behind a contract. The builder places and configures
 * them like any component, but their code never runs in the admin's own origin — the canvas
 * draws each in a sandboxed island frame — and they are edited as source.
 */

const SOURCE = `export default function Countdown({ until }: { until?: string }) {
  (window as unknown as { __devCodeRan?: boolean }).__devCodeRan = true;
  return <time>until {until}</time>;
}
`;

const SITE_FILES = {
  'dcms/app.json': doc({ schemaVersion: 1, routes: [{ id: 'home', path: '/', page: 'home' }] }),
  [HOME]: doc({
    schemaVersion: 1,
    id: 'home',
    title: 'Home',
    root: {
      id: 'r',
      type: 'dcms.page',
      slots: { default: [{ id: 'h', type: 'dcms.heading', props: { text: 'Launch' } }, { id: 'cd', type: 'code.countdown', props: { until: 'Friday' } }] },
    },
  }),
  'dcms/code/countdown.json': doc({ schemaVersion: 1, name: 'countdown', label: 'Countdown', props: [{ kind: 'text', name: 'until', label: 'Until' }] }),
  'src/components/countdown.tsx': SOURCE,
};

test('a developer component draws live on the canvas, in a sandboxed island of its own', async ({ page, api }) => {
  // The island is bundled in the browser from the site's real runtime, fetching react from
  // esm.sh like the full preview: it needs the network, which is why it is slow.
  test.slow();
  const frame = await open(page, api, { ...realSite(), ...SITE_FILES });

  /*
   * Drawn and run — but not here. The canvas shares the admin's origin, so the component runs in
   * a nested frame with `sandbox="allow-scripts"` and nothing else: an opaque origin that reaches
   * neither the canvas nor the admin.
   */
  const node = frame.locator('[data-dcms-node="cd"]');
  const island = node.locator('iframe');
  await expect(frame.frameLocator('[data-dcms-node="cd"] iframe').locator('time')).toHaveText('until Friday', { timeout: 90000 });
  await expect(island).toHaveAttribute('sandbox', 'allow-scripts');
  // Not one line of it ran in the admin's window, nor in the canvas's.
  expect(await page.evaluate(() => (window as unknown as { __devCodeRan?: boolean }).__devCodeRan)).toBeUndefined();
  expect(await frame.locator('body').evaluate(() => (window as unknown as { __devCodeRan?: boolean }).__devCodeRan)).toBeUndefined();
  await expect(frame.locator('time')).toHaveCount(0);

  // Its contract's props are edited like any other component's, and the island redraws.
  await node.click();
  const until = page.getByLabel('Until', { exact: true });
  await expect(until).toHaveValue('Friday');
  await until.fill('Monday');
  await until.press('Enter');
  await expect.poll(() => saved(api, HOME) ?? '', { timeout: 15000 }).toContain('"until": "Monday"');
  await expect(frame.frameLocator('[data-dcms-node="cd"] iframe').locator('time')).toHaveText('until Monday');

  // And its source is one click away, in the code view.
  await page.getByRole('button', { name: 'Edit source' }).click();
  await expect(page.getByRole('button', { name: 'src/components/countdown.tsx' })).toBeVisible();
  await expect(page.locator('.monaco-editor').getByText('export default function Countdown')).toBeVisible();
});

test('a new developer component starts as a contract and a source file', async ({ page, api }) => {
  await open(page, api, SITE_FILES);
  await page.getByRole('tab', { name: 'My components' }).click();
  await page.getByLabel('New component').fill('Ticket widget');
  await page.getByRole('button', { name: 'New developer component (TSX)' }).click();

  await expect.poll(() => saved(api, 'dcms/code/ticket-widget.json') ?? '', { timeout: 15000 }).toContain('"label": "Ticket widget"');
  expect(saved(api, 'src/components/ticket-widget.tsx')).toContain('export default function TicketWidget');
  await expect(page.getByRole('button', { name: 'src/components/ticket-widget.tsx' })).toBeVisible();
  // The problems list is clean: contract, source and default export all line up.
  await expect(page.getByRole('button', { name: /problems?$/ })).toHaveCount(0);
});

/** A directory's text files, keyed relative to it. */
function tree(dir: string, keep: (path: string) => boolean = () => true): Record<string, string> {
  const out: Record<string, string> = {};
  const walk = (d: string) => {
    for (const name of readdirSync(d)) {
      const full = join(d, name);
      if (statSync(full).isDirectory()) walk(full);
      else if (keep(full)) out[relative(dir, full)] = readFileSync(full, 'utf8');
    }
  };
  walk(dir);
  return out;
}

/**
 * The site as admin-api scaffolds it — the shared template files, the visual entry and package
 * pins, the vendored runtime — so the sandbox bundles what a real Mode D draft holds.
 */
function realSite(): Record<string, string> {
  const root = join(dirname(fileURLToPath(import.meta.url)), '../..');
  const template = join(root, 'packages/site-template-react');
  const files: Record<string, string> = {};
  for (const [p, text] of Object.entries(tree(join(template, 'shared/src')))) files[`src/${p}`] = text;
  files['src/main.tsx'] = readFileSync(join(template, 'templates/visual/src/main.tsx'), 'utf8');
  files['package.json'] = readFileSync(join(template, 'templates/visual/package.json'), 'utf8');
  const runtime = tree(join(root, 'packages/site-runtime/src'), (p) => /\.tsx?$/.test(p) && !/\.test\.tsx?$/.test(p) && !p.endsWith('vite.ts'));
  for (const [p, text] of Object.entries(runtime)) files[`src/dcms/runtime/${p}`] = text;
  return files;
}

test('the full preview runs the developer’s code in the opaque-origin sandbox', async ({ page, api }) => {
  // Bundles in the browser and fetches react, react-router, zod and dompurify from esm.sh, like
  // the Mode B preview: it needs the network, which is why it is slow.
  test.slow();
  // The site's analytics asks whether it is on, and which Google Analytics ID it has (none);
  // through the preview proxy, like any site call.
  api.on('GET', '/api/admin/sites/:id/preview/api/analytics/status', { enabled: false });
  api.on('GET', '/api/admin/sites/:id/preview/api/ga/config', {});
  await open(page, api, { ...realSite(), ...SITE_FILES });
  await page.getByRole('group', { name: 'View' }).getByTitle('Preview').click();

  const site = page.frameLocator('iframe[srcdoc]');
  await expect(site.locator('time')).toHaveText('until Friday', { timeout: 90000 });
  // Opaque origin: scripts, but no same-origin access to the admin.
  const sandbox = page.locator('iframe[srcdoc]');
  await expect(sandbox).toHaveAttribute('sandbox', /allow-scripts/);
  expect(await sandbox.getAttribute('sandbox')).not.toContain('allow-same-origin');
  await expect(site.getByRole('heading', { name: 'Launch' })).toBeVisible();
  // Still never in the admin's own window.
  expect(await page.evaluate(() => (window as unknown as { __devCodeRan?: boolean }).__devCodeRan)).toBeUndefined();
});

test('the sandboxed preview signs a visitor in, keeps them across a reload, and opens the site sign-in popup', async ({ page, api }) => {
  // A site with developer components previews as its real build in the opaque-origin sandbox:
  // no storage of its own and no route to the API but the console. Sign-in must still work.
  test.slow();
  const token = `h.${Buffer.from(JSON.stringify({ exp: Math.floor(Date.now() / 1000) + 3600, email: 'ann@example.com' })).toString('base64url')}.s`;
  await page.route('**/.edge/preview/**', async (route, request) => {
    const url = new URL(request.url());
    if (url.pathname.endsWith('/me')) return route.fulfill({ json: { available: true, signedIn: false, name: null, email: null, groups: [], viewAs: false } });
    if (url.pathname.endsWith('/access')) return route.fulfill({ json: { [url.searchParams.get('path')!]: 'allow' } });
    return route.fulfill({ status: 404, json: {} });
  });
  api
    .on('GET', '/api/admin/sites/:id/preview/api/analytics/status', { enabled: false })
    .on('GET', '/api/admin/sites/:id/preview/api/ga/config', {})
    .on('GET', '/api/admin/plugin-ui', [
      { pluginId: 'user-auth', name: 'User Authentication', icon: null, source: 'builtin', module: null, screens: [], instances: [{ id: 'ua', slug: 'users', name: 'Site users', enabled: true }] },
      { pluginId: 'visitor-auth', name: 'Visitor Authentication', icon: null, source: 'builtin', module: null, screens: [], instances: [{ id: 'va', slug: 'members', name: 'Members', enabled: true }] },
    ])
    .on('POST', '/api/admin/sites/:id/preview/api/members/login', { accessToken: token, refreshToken: 'r1' })
    .on('GET', '/api/admin/sites/:id/preview/api/members/me', ({ request, route }: { request: { headers(): Record<string, string> }; route: { fulfill(o: unknown): Promise<void> } }) =>
      request.headers()['x-dcms-visitor-token'] === token ? { id: 'v1', email: 'ann@example.com', displayName: 'Ann' } : route.fulfill({ status: 401, json: {} }).then(() => undefined),
    );
  await open(page, api, {
    ...realSite(),
    ...SITE_FILES,
    [HOME]: doc({
      schemaVersion: 1,
      id: 'home',
      title: 'Home',
      root: {
        id: 'r',
        type: 'dcms.page',
        slots: {
          default: [
            { id: 'h', type: 'dcms.heading', props: { text: 'Launch' } },
            { id: 'cd', type: 'code.countdown', props: { until: 'Friday' } },
            { id: 'staff', type: 'dcms.sign-in', props: { label: 'Staff sign-in' } },
            { id: 'out', type: 'dcms.signed-in', props: { accounts: 'visitors', show: 'signed-out' }, slots: { default: [{ id: 'in', type: 'dcms.sign-in', props: { accounts: 'visitors', instance: 'members' } }] } },
            { id: 'mem', type: 'dcms.signed-in', props: { accounts: 'visitors', show: 'signed-in' }, slots: { default: [{ id: 'm', type: 'dcms.text', props: { text: 'Members area' } }] } },
          ],
        },
      },
    }),
  });
  await page.getByRole('group', { name: 'View' }).getByTitle('Preview').click();
  const site = page.frameLocator('iframe[srcdoc]');
  await expect(site.locator('time')).toHaveText('until Friday', { timeout: 90000 });

  const visitor = page.getByRole('button', { name: 'Which visitor the preview is signed in as' });
  await expect(visitor).toContainText('No visitor');
  await site.getByLabel('Email').fill('ann@example.com');
  await site.getByLabel('Password').fill('secret');
  await site.getByRole('button', { name: 'Sign in', exact: true }).click();
  await expect(site.getByText('Members area')).toBeVisible();
  await expect(visitor).toContainText('Visitor: ann@example.com');

  // A reload is a new document: the visitor is still signed in, from the store the console lends.
  await page.getByRole('button', { name: 'Reload' }).click();
  await expect(site.locator('time')).toHaveText('until Friday', { timeout: 30000 });
  await expect(site.getByText('Members area')).toBeVisible();

  await visitor.click();
  await page.getByRole('menuitem', { name: 'Sign the visitor out' }).click();
  await expect(site.getByLabel('Password')).toBeVisible({ timeout: 30000 });
  await expect(visitor).toContainText('No visitor');

  // A site user's sign-in leaves through the preview: its popup, not the sandboxed frame.
  const popup = page.waitForEvent('popup');
  await site.getByRole('button', { name: 'Staff sign-in' }).click();
  expect((await popup).url()).toMatch(/\/\.edge\/preview\/[^/]+\/[^/]+\/signin$/);
});
