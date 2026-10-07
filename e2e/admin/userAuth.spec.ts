import { test, expect } from '../fixtures/test';
import * as data from '../fixtures/data';

/**
 * The User Authentication screens (docs/adr/0022) in the real console against a mocked server:
 * inviting someone hands back the link for when mail does not arrive; a site's rules are saved
 * whole and in order; a provider's secret is sent only when typed.
 */

test.use({
  grants: [
    ...data.ALL_TENANT_PERMISSIONS,
    'plugin:user-auth:users-read',
    'plugin:user-auth:users-manage',
    'plugin:user-auth:access-manage',
    'plugin:user-auth:providers-manage',
  ],
});

const INSTANCE = {
  id: 'cccccccc-0000-0000-0000-0000000000e1', pluginId: 'user-auth', slug: 'users', name: 'Users',
  description: null, enabled: true, config: '{}', aiToolsEnabled: false,
};

const STAFF = { id: 'eeeeeeee-0000-0000-0000-000000000001', name: 'Staff', description: null, members: 1 };
const PAT = {
  id: 'eeeeeeee-0000-0000-0000-000000000002', email: 'pat@corp.test', displayName: 'Pat', status: 'active', groups: [STAFF.id],
  hasPassword: true, lockedOut: false, createdAt: '2026-10-07T10:00:00Z', lastSignInAt: null,
};
const SITE = {
  id: 'ffffffff-0000-0000-0000-000000000001', name: 'Intranet', hosts: [{ hostname: 'intranet.corp.test', verified: true }],
  rules: [{ prefix: '/portal', access: 'signedIn', groups: [] }],
};
const ENTRA = {
  key: 'entra', kind: 'entra', displayName: 'Microsoft', enabled: true, clientId: 'app-id', hasSecret: true, issuer: null,
  entraTenant: 'corp.test', hostedDomain: null, provisioning: 'inviteOnly', allowedDomains: [], defaultGroups: [], groupClaim: null,
  groupMappings: {}, callbackUrl: 'https://auth.test/realm/sso/1/callback',
};

const screen = (id: string, title: string, icon: string) => ({
  id, title, titles: {}, description: null, scope: 'instance', icon, permission: 'plugin:user-auth:users-read', nav: null, allowed: true,
});

test.beforeEach(async ({ api }) => {
  api
    .on('GET', '/api/admin/plugin-ui', [
      ...data.PLUGIN_UI,
      {
        pluginId: 'user-auth', name: 'User Authentication', icon: 'ShieldCheck', source: 'builtin',
        module: { kind: 'builtin', key: 'Dcms.Plugins.UserAuth' },
        screens: [screen('users', 'Users', 'Users'), screen('access', 'Access', 'ShieldCheck'), screen('sign-in', 'Sign-in', 'KeyRound')],
        instances: [{ id: INSTANCE.id, slug: 'users', name: 'Users', enabled: true }],
      },
    ])
    .on('GET', '/api/admin/plugins/instances', [...data.PLUGIN_INSTANCES, INSTANCE])
    .on('GET', '/api/admin/plugins/catalog', [
      ...data.PLUGIN_CATALOG,
      {
        id: 'user-auth', name: 'User Authentication', version: '1.0.0', description: 'Enterprise sign-in.', allowMultipleInstances: false,
        configJsonSchema: '{}', permissions: [], dependencies: [], publicConfigKeys: [], contentTypes: [], provides: [], consumes: [],
      },
    ])
    .on('GET', '/api/admin/plugins/users/realm', {
      tenantId: 't', slug: 'acme', name: 'Acme', hosts: ['intranet.corp.test'], clientId: 'site:t', clientReady: true, passwordEnabled: true,
    })
    .on('GET', '/api/admin/plugins/users/users', { items: [PAT], total: 1, page: 1, pageSize: 50 })
    .on('GET', '/api/admin/plugins/users/groups', [STAFF])
    .on('GET', '/api/admin/plugins/users/roles', [])
    .on('GET', '/api/admin/plugins/users/resources', [])
    .on('GET', '/api/admin/plugins/users/sites', [SITE])
    .on('GET', '/api/admin/plugins/users/providers', [ENTRA]);
});

test('an invitation goes out with its groups, and its link is offered for when mail does not arrive', async ({ page, api }) => {
  api.on('POST', '/api/admin/plugins/users/users/invite', {
    user: { ...PAT, id: 'eeeeeeee-0000-0000-0000-000000000003', email: 'sam@corp.test', status: 'invited' },
    inviteUrl: 'https://auth.test/realm/acme/invite?token=abc',
  });
  await page.goto('/plugins/users/users');

  await expect(page.getByRole('table').getByText('pat@corp.test')).toBeVisible();
  await expect(page.getByRole('table').getByText('Staff')).toBeVisible();

  await page.getByRole('button', { name: 'Invite' }).click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Email').fill('sam@corp.test');
  await dialog.getByLabel('Staff').check();
  await dialog.getByRole('button', { name: 'Send invitation' }).click();

  await expect(dialog.getByText('https://auth.test/realm/acme/invite?token=abc')).toBeVisible();
  expect(api.requestsTo('POST', '/api/admin/plugins/users/users/invite')[0]!.body).toEqual({
    email: 'sam@corp.test', displayName: null, groups: [STAFF.id],
  });
});

test("a site's rules are saved whole and in order", async ({ page, api }) => {
  api.on('PUT', `/api/admin/plugins/users/sites/${SITE.id}/rules`, {});
  await page.goto('/plugins/users/access');

  await expect(page.getByText('intranet.corp.test')).toBeVisible();
  await page.getByRole('button', { name: 'Add rule' }).click();
  const paths = page.getByLabel('Path');
  await paths.nth(1).fill('/portal/news');
  await page.getByRole('combobox').filter({ hasText: 'Any signed-in user' }).nth(1).click();
  await page.getByRole('option', { name: 'Anyone' }).click();
  // The public exception must come before the rule it carves out of.
  await page.getByRole('button', { name: 'Move up' }).nth(1).click();
  await page.getByRole('button', { name: 'Save' }).click();

  await expect.poll(() => api.requestsTo('PUT', `/api/admin/plugins/users/sites/${SITE.id}/rules`).length).toBe(1);
  expect(api.requestsTo('PUT', `/api/admin/plugins/users/sites/${SITE.id}/rules`)[0]!.body).toEqual([
    { prefix: '/portal/news', access: 'public', groups: [] },
    { prefix: '/portal', access: 'signedIn', groups: [] },
  ]);
});

test("a provider's stored secret is kept unless a new one is typed", async ({ page, api }) => {
  api.on('PUT', '/api/admin/plugins/users/providers/entra', ENTRA);
  await page.goto('/plugins/users/sign-in');

  await expect(page.getByText('https://auth.test/realm/sso/1/callback')).toBeVisible();
  await page.getByRole('button', { name: 'Edit' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByText(/A secret is stored/)).toBeVisible();
  await dialog.getByLabel('Button label').fill('Microsoft 365');
  await dialog.getByRole('button', { name: 'Save' }).click();

  await expect.poll(() => api.requestsTo('PUT', '/api/admin/plugins/users/providers/entra').length).toBe(1);
  expect(api.requestsTo('PUT', '/api/admin/plugins/users/providers/entra')[0]!.body).toMatchObject({
    kind: 'entra', displayName: 'Microsoft 365', clientId: 'app-id', clientSecret: null, entraTenant: 'corp.test',
  });
});
