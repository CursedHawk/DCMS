import { test, expect } from '../fixtures/test';
import { anthropicStream, type ScriptedTurn } from '../fixtures/aiStream';
import { PLUGIN_CATALOG, PLUGIN_INSTANCES } from '../fixtures/data';
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

/*
 * P6: the Events vertical slice, built by the agent from an almost empty site — a design kit,
 * an Event card component (stacks on mobile), a hero, a collection of Events-plugin gigs drawn
 * with the card, a footer in the app shell, and a detail page each card links to. These are the
 * calls a model makes for "build an events home page"; what is asserted is the site they leave.
 */
const GIGS = [
  { id: 'g1', slug: 'summer-jam', contentType: 'gig', data: { title: 'Summer Jam', date: '2026-07-01', venue: 'Lucerna', photos: ['/photos/summer.jpg'], description: '<p>Open air.</p>' } },
  { id: 'g2', slug: 'winter-session', contentType: 'gig', data: { title: 'Winter Session', date: '2026-12-12', venue: 'Roxy', photos: [], description: '<p>Indoors.</p>' } },
];

const EVENT_CARD = {
  id: 'card',
  type: 'dcms.stack',
  props: { direction: 'horizontal', gap: 'md' },
  slots: {
    default: [
      { id: 'photo', type: 'dcms.image', props: { alt: 'Event photo' } },
      {
        id: 'body',
        type: 'dcms.stack',
        slots: {
          default: [
            { id: 'date', type: 'dcms.text', props: { text: 'Date' } },
            { id: 'title', type: 'dcms.heading', props: { text: 'Event', level: '3' } },
            { id: 'venue', type: 'dcms.text', props: { text: 'Venue' } },
            { id: 'more', type: 'dcms.button', props: { label: 'Details' }, action: { type: 'navigate', to: '/events/:slug' } },
          ],
        },
      },
    ],
  },
};

const tool = (id: string, name: string, input: Record<string, unknown>): ScriptedTurn => ({ tool: { id, name, input } });
const expose = (id: string, node: string, prop: string, name: string) =>
  tool(id, 'expose_setting', { component: 'component:event-card@1', node, prop, name });

test('the agent builds the events slice: kit, card component, collection, shell footer and detail page', async ({ page, api }) => {
  // An Events instance, with the plugin's real gig fields (photos is a list of image ids).
  api
    .on('GET', '/api/admin/plugins/instances', [
      ...PLUGIN_INSTANCES,
      { id: 'cccccccc-0000-0000-0000-000000000009', pluginId: 'dcms.events', slug: 'events', name: 'Events', description: '', enabled: true, config: '{}' },
    ])
    .on('GET', '/api/admin/plugins/catalog', [
      ...PLUGIN_CATALOG,
      {
        ...PLUGIN_CATALOG[0],
        id: 'dcms.events',
        name: 'Events',
        contentTypes: [
          {
            name: 'gig',
            searchable: true,
            slugField: 'title',
            customFields: null,
            fields: [
              { name: 'title', type: 'Text', required: true },
              { name: 'date', type: 'DateTime', required: true },
              { name: 'venue', type: 'Text', required: true },
              { name: 'description', type: 'RichText', required: false },
              { name: 'photos', type: 'Json', required: false, reference: { mediaCategory: 'Image' } },
            ],
          },
        ],
      },
    ])
    .on('GET', '/api/admin/sites/:id/preview/api/events/gig', { items: GIGS, totalCount: 2, page: 1, pageSize: 6 })
    .on('GET', '/api/admin/sites/:id/preview/api/events/gig/:slug', ({ params }) => GIGS.find((g) => g.slug === params.slug));
  await scriptAgent(page, [
    tool('k', 'set_design_kit', { kit: 'bold' }),
    tool('c', 'create_component', { label: 'Event card', root: EVENT_CARD }),
    tool('m', 'set_props', { doc: 'component:event-card@1', node: 'card', props: { direction: 'vertical' }, device: 'mobile' }),
    expose('e1', 'photo', 'src', 'photo'),
    expose('e2', 'date', 'text', 'date'),
    expose('e3', 'title', 'text', 'title'),
    expose('e4', 'venue', 'text', 'venue'),
    tool('f', 'insert_node', {
      doc: 'shell',
      node: { type: 'dcms.section', props: { background: 'alt' }, slots: { default: [{ type: 'dcms.text', props: { text: '© Acme Events' } }] } },
    }),
    tool('h', 'insert_node', {
      doc: 'page:home',
      node: { type: 'dcms.section', props: { background: 'soft', spacing: 'lg' }, slots: { default: [{ type: 'dcms.heading', props: { text: 'Live this season', level: '1' } }] } },
    }),
    tool('l', 'insert_node', {
      doc: 'page:home',
      node: {
        type: 'dcms.collection',
        props: { source: { instance: 'events', contentType: 'gig' }, limit: 6 },
        slots: { item: [{ type: 'tenant.event-card', bind: { photo: 'photos', date: 'date', title: 'title', venue: 'venue' } }] },
      },
    }),
    tool('p', 'create_page', { title: 'Event', path: '/events/:slug', detail_of: { instance: 'events', contentType: 'gig' } }),
    tool('d', 'insert_node', {
      doc: 'page:event',
      node: {
        type: 'dcms.section',
        slots: {
          default: [
            { type: 'dcms.heading', props: { level: '1' }, bind: { text: 'title' } },
            { type: 'dcms.text', bind: { text: 'venue' } },
            { type: 'dcms.richtext', bind: { html: 'description' } },
          ],
        },
      },
    }),
    tool('v', 'check_visual_site', {}),
    { text: 'Built the events home page and its detail page.' },
  ]);

  const frame = await open(page, api, HOME_PAGE);
  await page.getByRole('tab', { name: 'Agent' }).click();
  await page.getByPlaceholder(/Describe a change/i).fill('Build an events home page');
  await page.keyboard.press('Enter');
  await expect(page.getByText('Built the events home page and its detail page.')).toBeVisible({ timeout: 30000 });

  // Nothing the run wrote is wrong.
  await page.getByRole('tab', { name: 'Problems' }).click();
  await expect(page.getByText('No problems found.')).toBeVisible();

  // The canvas draws the card from the first gig; on mobile the card stacks.
  await expect(frame.getByText('Summer Jam')).toBeVisible({ timeout: 15000 });
  await page.getByTitle('Mobile', { exact: true }).click();
  await expect(frame.locator('[data-dcms-type="tenant.event-card"] .dcms-stack').first()).toHaveCSS('flex-direction', 'column');
  await page.getByTitle('Desktop', { exact: true }).click();

  // The site, as a visitor gets it: every gig, the shell's footer, and a card's link to its page.
  await page.getByRole('group', { name: 'View' }).getByTitle('Preview').click();
  const preview = page.frameLocator('iframe[title="Preview"]');
  await expect(preview.getByRole('heading', { level: 3 })).toHaveText(['Summer Jam', 'Winter Session']);
  await expect(preview.getByText('© Acme Events')).toBeVisible();
  await preview.getByRole('link', { name: 'Details' }).nth(1).click();
  await expect(preview.getByRole('heading', { level: 1 })).toHaveText('Winter Session');
  await expect(preview.getByText('Indoors.')).toBeVisible();
  await expect(preview.getByText('© Acme Events')).toBeVisible();
});

/*
 * The CRM slice: the calls a model makes for "build a site for our CRM" over a Dynamic Apps app —
 * describe_apps first, then a deals list linking to each deal, the deal's page (its account by
 * name, an edit form), and the account's page listing its deals. Records are not plugin content:
 * every source is { app, table }, and the check that closes the run finds nothing wrong.
 */
const ACCOUNT = 'bbbbbbbb-0000-0000-0000-000000000001';
const DEAL = 'aaaaaaaa-0000-0000-0000-000000000001';
const CRM_MODEL = {
  revision: 3,
  hash: 'h3',
  tables: [
    {
      apiName: 'accounts', displayName: 'Account', pluralName: 'Accounts', primaryField: 'name',
      access: { read: 'all', create: false, updateOwn: false, deleteOwn: false, rowRules: false },
      fields: [{ apiName: 'name', displayName: 'Name', type: 'text', required: true, readOnly: false }],
      relationships: [{ apiName: 'deals', kind: 'oneToMany', target: 'deals' }],
    },
    {
      apiName: 'deals', displayName: 'Deal', pluralName: 'Deals', primaryField: 'name',
      access: { read: 'all', create: true, updateOwn: false, deleteOwn: false, rowRules: false },
      fields: [
        { apiName: 'name', displayName: 'Name', type: 'text', required: true, readOnly: false },
        { apiName: 'stage', displayName: 'Stage', type: 'choice', required: false, readOnly: false, options: ['lead', 'won'] },
        { apiName: 'account', displayName: 'Account', type: 'lookup', required: false, readOnly: false, target: 'accounts' },
      ],
      relationships: [],
    },
  ],
};
const DEAL_ROW = { id: DEAL, version: 1, created_at: '2026-10-01T08:00:00Z', updated_at: '2026-10-01T08:00:00Z', name: 'Big one', stage: 'won', account: { id: ACCOUNT, name: 'Acme' } };
const PAGE_OF = (items: unknown[]) => ({ items, total: items.length, page: 1, pageSize: 25, totalCapped: false });

test('the agent builds a CRM slice over a Dynamic Apps app, with records as records', async ({ page, api }) => {
  test.slow();
  const preview = '/api/admin/sites/:id/preview/api/crm';
  api
    .on('GET', '/api/admin/plugins/instances', [
      ...PLUGIN_INSTANCES,
      { id: 'cccccccc-0000-0000-0000-0000000000d1', pluginId: 'dynamic-apps', slug: 'crm', name: 'CRM', description: '', enabled: true, config: '{}' },
    ])
    .on('GET', '/api/admin/plugin-ui', [{ pluginId: 'dynamic-apps', name: 'Dynamic Apps', icon: null, source: 'builtin', module: null, screens: [], instances: [{ id: 'd1', slug: 'crm', name: 'CRM', enabled: true, aiToolsEnabled: false }] }])
    .on('GET', '/api/admin/plugins/crm/_model', { draft: null, published: { number: 3 }, hash: 'h3' })
    .on('GET', `${preview}/_model`, CRM_MODEL)
    .on('GET', `${preview}/data/deals`, PAGE_OF([DEAL_ROW]))
    .on('GET', `${preview}/data/accounts`, PAGE_OF([{ id: ACCOUNT, version: 1, name: 'Acme' }]))
    .on('POST', `${preview}/data/deals/query`, PAGE_OF([DEAL_ROW]))
    .on('GET', `${preview}/views/deals`, { views: [], defaultView: null })
    .on('GET', `${preview}/data/deals/:deal`, DEAL_ROW)
    .on('GET', `${preview}/data/accounts/:account`, { id: ACCOUNT, version: 1, name: 'Acme' })
    .on('GET', `${preview}/data/accounts/:account/deals`, PAGE_OF([DEAL_ROW]));
  await scriptAgent(page, [
    tool('a', 'describe_apps', {}),
    tool('t', 'insert_node', {
      doc: 'page:home',
      node: { type: 'dcms.data-table', props: { instance: 'crm', table: 'deals', columns: 'name, stage, account', rowLink: '/deals/:id' } },
    }),
    tool('dp', 'create_page', { title: 'Deal', path: '/deals/:id', detail_of: { app: 'crm', table: 'deals', expand: ['account'] } }),
    tool('dn', 'insert_node', {
      doc: 'page:deal',
      node: {
        type: 'dcms.section',
        slots: {
          default: [
            { type: 'dcms.heading', props: { level: '1' }, bind: { text: 'name' } },
            { type: 'dcms.text', bind: { text: 'account.name' } },
            { type: 'dcms.record-form', props: { instance: 'crm', table: 'deals', mode: 'edit', fields: 'name, stage' } },
          ],
        },
      },
    }),
    tool('ap', 'create_page', { title: 'Account', path: '/accounts/:id', detail_of: { app: 'crm', table: 'accounts' } }),
    tool('an', 'insert_node', {
      doc: 'page:account',
      node: {
        type: 'dcms.section',
        slots: {
          default: [
            { type: 'dcms.heading', props: { level: '1' }, bind: { text: 'name' } },
            { type: 'dcms.collection', props: { source: { app: 'crm', table: 'accounts', related: 'deals' } }, slots: { item: [{ type: 'dcms.text', bind: { text: 'name' } }] } },
          ],
        },
      },
    }),
    tool('v', 'check_visual_site', {}),
    { text: 'Built the CRM: deals, a page per deal and per account.' },
  ]);

  await open(page, api, HOME_PAGE);
  await page.getByRole('tab', { name: 'Agent' }).click();
  await page.getByPlaceholder(/Describe a change/i).fill('Build a site for our CRM');
  await page.keyboard.press('Enter');
  await expect(page.getByText('Built the CRM: deals, a page per deal and per account.')).toBeVisible({ timeout: 30000 });

  // Records as records: every source names an app and a table, and nothing the run wrote is wrong.
  await page.getByRole('tab', { name: 'Problems' }).click();
  await expect(page.getByText('No problems found.')).toBeVisible();
  await expect.poll(() => saved(api, 'dcms/pages/deal.json'), { timeout: 15000 }).toBeTruthy();
  const deal = JSON.parse(saved(api, 'dcms/pages/deal.json')!);
  expect(deal.data).toEqual({ source: { app: 'crm', table: 'deals', expand: ['account'] }, param: 'id' });
  expect(deal.seo).toEqual({ title: '{name}' });

  // As a visitor: the deals list links to the deal, whose page names its account.
  await page.getByRole('group', { name: 'View' }).getByTitle('Preview').click();
  const site = page.frameLocator('iframe[title="Preview"]');
  await site.getByRole('link', { name: 'Big one' }).click();
  await expect(site.getByRole('heading', { level: 1 })).toHaveText('Big one');
  await expect(site.getByText('Acme')).toBeVisible();
  await expect(site.locator('form.dcms-form input[name="name"]')).toHaveValue('Big one');
});
