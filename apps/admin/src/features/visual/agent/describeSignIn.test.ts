import { beforeEach, describe, expect, it, vi } from 'vitest';

const get = vi.fn();
vi.mock('../../../lib/api', () => ({ api: { get: (path: string) => get(path) } }));

const { VISUAL_TOOLS } = await import('./visualTools');

const tool = VISUAL_TOOLS.find((t) => t.name === 'describe_sign_in')!;
const ctx = { siteId: 'site-1' } as never;

describe('describe_sign_in', () => {
  beforeEach(() => {
    get.mockReset();
  });

  it('tells the two kinds apart, with the site’s domain, methods and page rules', async () => {
    get.mockImplementation(async (path: string) => {
      if (path === '/admin/plugin-ui')
        return [
          { pluginId: 'user-auth', instances: [{ slug: 'users', name: 'Site users', enabled: true }] },
          { pluginId: 'visitor-auth', instances: [{ slug: 'members', name: 'Members', enabled: true }, { slug: 'old', name: 'Old', enabled: false }] },
        ];
      if (path === '/admin/plugins/users/realm') return { passwordEnabled: true, clientReady: true };
      if (path === '/admin/sites/site-1/preview/realm') return { host: 'portal.example.com' };
      if (path === '/admin/plugins/users/providers')
        return [
          { kind: 'google', displayName: 'Google', enabled: true, provisioning: 'allowedDomains', allowedDomains: ['example.com'] },
          { kind: 'oidc', displayName: 'Okta', enabled: false },
        ];
      if (path === '/admin/plugins/users/sites') return [{ id: 'site-1', rules: [{ prefix: '/portal', access: 'signedIn', groups: [] }] }, { id: 'other', rules: [] }];
      throw new Error(`unexpected ${path}`);
    });
    const out = JSON.parse((await tool.run({}, ctx)).content as string);
    expect(out.users).toMatchObject({
      instance: 'users',
      accounts: 'users',
      mainDomain: 'portal.example.com',
      methods: ['email and password (invited people)', 'Google (google, anyone at example.com)'],
      pageRules: [{ prefix: '/portal', access: 'signedIn', groups: [] }],
    });
    expect(out.users.build).toMatch(/No password fields and no registration/);
    expect(out.visitors).toMatchObject({ accounts: 'visitors', instances: [{ instance: 'members', name: 'Members' }] });
  });

  it('leaves out what it may not read, and warns of a site nobody can sign in on', async () => {
    get.mockImplementation(async (path: string) => {
      if (path === '/admin/plugin-ui') return [{ pluginId: 'user-auth', instances: [{ slug: 'users', name: 'Site users', enabled: true }] }];
      if (path === '/admin/sites/site-1/preview/realm') return { host: null };
      throw new Error('403');
    });
    const out = JSON.parse((await tool.run({}, ctx)).content as string);
    expect(out.users.notes).toEqual(['This site has no verified domain yet, so nobody can sign in on it until one is added in Settings → Domains.']);
    expect(out.users.pageRules).toBeUndefined();
    expect(out.visitors).toBeUndefined();
  });

  it('says there is nothing to sign in with, instead of a Forms form', async () => {
    get.mockResolvedValue([{ pluginId: 'forms', instances: [{ slug: 'forms', name: 'Forms', enabled: true }] }]);
    const out = await tool.run({}, ctx);
    expect(out.isError).toBe(true);
    expect(out.content).toMatch(/neither User Authentication .* nor Visitor Authentication/);
  });
});
