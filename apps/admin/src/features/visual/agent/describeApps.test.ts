import { beforeEach, describe, expect, it, vi } from 'vitest';

const get = vi.fn();
vi.mock('../../../lib/api', () => ({ api: { get: (path: string) => get(path) } }));

const { VISUAL_TOOLS } = await import('./visualTools');

const tool = (name: string) => VISUAL_TOOLS.find((t) => t.name === name)!;
const ctx = { siteId: 'site-1' } as never;

const MODEL = {
  tables: [
    {
      apiName: 'deals',
      displayName: 'Deal',
      pluralName: 'Deals',
      primaryField: 'name',
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

describe('describe_apps', () => {
  beforeEach(() => {
    get.mockReset();
  });

  it('gives each app’s tables as the site reads them, through the site’s preview', async () => {
    get.mockImplementation(async (path: string) => {
      if (path === '/admin/plugin-ui') return [{ pluginId: 'dynamic-apps', instances: [{ slug: 'crm', name: 'CRM', enabled: true, aiToolsEnabled: true }] }];
      if (path === '/admin/sites/site-1/preview/api/crm/_model') return MODEL;
      if (path === '/admin/plugins/crm/_model') return { draft: { number: 3 }, published: { number: 2 } };
      throw new Error(`unexpected ${path}`);
    });
    const out = await tool('describe_apps').run({}, ctx);
    const [app] = JSON.parse(out.content as string);
    expect(app).toMatchObject({ app: 'crm', published: true, draftPending: true, aiTools: true });
    expect(app.tables[0]).toMatchObject({ table: 'deals', primaryField: 'name', access: { read: 'all', create: true } });
    expect(app.tables[0].fields).toEqual([
      { name: 'name', label: 'Name', type: 'text', required: true },
      { name: 'stage', label: 'Stage', type: 'choice', options: ['lead', 'won'] },
      { name: 'account', label: 'Account', type: 'lookup', lookupOf: 'accounts' },
    ]);
  });

  it('says where an app comes from when there is none', async () => {
    get.mockResolvedValue([{ pluginId: 'forms', instances: [] }]);
    const out = await tool('describe_apps').run({}, ctx);
    expect(out.isError).toBe(true);
    expect(out.content).toMatch(/no Dynamic Apps applications/);
  });
});

describe('list_component_types', () => {
  it('gives named types in full, with what each setting means', async () => {
    const out = await tool('list_component_types').run({ types: ['dcms.record-form'] }, ctx);
    const [form] = JSON.parse(out.content as string);
    expect(form.type).toBe('dcms.record-form');
    expect(form.props.find((p: { name: string }) => p.name === 'mode')).toMatchObject({ label: 'Does', description: expect.stringMatching(/detail page/) });
  });
});
