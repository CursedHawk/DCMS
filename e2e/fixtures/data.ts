/**
 * The dataset every admin spec starts from.
 *
 * <p>Written from the response types the SPA declares (each feature's `api.ts`), because that is
 * the contract these tests are actually about: what the console does with a shape. A spec that
 * needs different data overrides one route rather than editing this — see `MockApi.on`.</p>
 */

export const TENANT = { tenantId: 'aaaaaaaa-0000-0000-0000-000000000001', slug: 'acme', name: 'Acme Studio' };
export const OTHER_TENANT = { tenantId: 'aaaaaaaa-0000-0000-0000-000000000002', slug: 'globex', name: 'Globex' };

/** Every tenant permission key the console knows. The default operator holds all of them. */
export const ALL_TENANT_PERMISSIONS = [
  'tenant:settings', 'members:manage', 'roles:manage', 'domains:manage', 'plugins:manage',
  'media:read', 'media:write', 'site:edit', 'site:publish', 'ai:settings', 'analytics:read',
  'content:read', 'content:write', 'content:publish', 'chat:read', 'chat:manage',
  'audit:read', 'audit:export',
];

export const NAVIGATION = {
  items: [
    { to: '/', labelKey: 'nav.dashboard', icon: 'LayoutDashboard', group: 'main', label: null },
    { to: '/forms', labelKey: 'nav.forms', icon: 'Inbox', group: 'main', label: null },
    { to: '/analytics', labelKey: 'nav.analytics', icon: 'TrendingUp', group: 'main', label: null },
    { to: '/content', labelKey: 'nav.content', icon: 'FileText', group: 'build', label: null },
    { to: '/media', labelKey: 'nav.media', icon: 'Image', group: 'build', label: null },
    { to: '/sites', labelKey: 'nav.sites', icon: 'PanelsTopLeft', group: 'build', label: null },
    { to: '/marketplace', labelKey: 'nav.marketplace', icon: 'Store', group: 'build', label: null },
    // One Settings entry over seven sections; the SPA draws that sub-navigation itself.
    { to: '/settings', labelKey: 'nav.settings', icon: 'Settings', group: 'admin', label: null },
    // One plugin instance's own entry. `label` is the tenant's name for it and is deliberately
    // not an i18n key — this is the case the hard-coded nav array could not express, so it is
    // the case the shell spec asserts on.
    {
      to: '/content?instance=cccccccc-0000-0000-0000-000000000001',
      labelKey: 'nav.content', icon: 'Newspaper', group: 'build', label: 'Press room',
    },
  ],
};

export const FOLDER_BRAND = 'bbbbbbbb-0000-0000-0000-000000000001';
export const FOLDER_PRESS = 'bbbbbbbb-0000-0000-0000-000000000002';

export const MEDIA_FOLDERS = [
  { id: FOLDER_BRAND, name: 'Brand', parentId: null, createdAt: '2026-08-01T09:00:00Z', assetCount: 1 },
  { id: FOLDER_PRESS, name: 'Press kit', parentId: null, createdAt: '2026-08-02T09:00:00Z', assetCount: 0 },
];

export const ASSET_LOGO = 'dddddddd-0000-0000-0000-000000000001';
export const ASSET_LOOSE = 'dddddddd-0000-0000-0000-000000000002';

export const MEDIA_ASSETS = [
  {
    id: ASSET_LOGO, category: 'Image', fileName: 'logo.png', status: 'Ready',
    sizeBytes: 20_480, folderId: FOLDER_BRAND, createdAt: '2026-08-01T10:00:00Z',
    variantBytes: 4_096, variantCount: 2, width: 512, height: 512,
  },
  {
    id: ASSET_LOOSE, category: 'Image', fileName: 'unfiled-shot.png', status: 'Ready',
    sizeBytes: 51_200, folderId: null, createdAt: '2026-08-03T10:00:00Z',
    variantBytes: 8_192, variantCount: 2, width: 1024, height: 768,
  },
];

export const MEDIA_USAGE = {
  originalBytes: 71_680, variantBytes: 12_288, totalBytes: 83_968,
  assetCount: 2, folderCount: 2,
  byCategory: [{ category: 'Image', count: 2, originalBytes: 71_680 }],
};

export const INSTANCE_BLOG = 'cccccccc-0000-0000-0000-000000000001';

export const PLUGIN_INSTANCES = [
  {
    id: INSTANCE_BLOG, pluginId: 'dcms.blog', slug: 'press-room', name: 'Press room',
    description: 'Announcements and releases', enabled: true, config: '{}',
  },
];

export const PLUGIN_CATALOG = [
  {
    id: 'dcms.blog', name: 'Blog', version: '1.4.0', description: 'Posts with tags and scheduling.',
    allowMultipleInstances: true, configJsonSchema: '{"type":"object","properties":{}}',
    permissions: [{ action: 'content:write', displayName: 'Write content' }],
    dependencies: [], publicConfigKeys: [],
    contentTypes: [
      {
        name: 'post', searchable: true, slugField: 'slug', customFields: null,
        fields: [
          { name: 'title', type: 'Text', required: true },
          { name: 'body', type: 'RichText', required: false },
          { name: 'tags', type: 'Tags', required: false },
        ],
      },
    ],
  },
];

/** The Press room instance's data sets (GET /api/admin/plugins/press-room/_data). */
export const PLUGIN_DATA_SETS = [
  {
    id: 'subscribers', title: 'Subscribers', description: 'People who asked for new posts by email.',
    icon: 'Users', platform: false, canWrite: true,
    columns: [
      { key: 'email', label: 'Email', kind: 'email', sortable: true, primary: true },
      { key: 'name', label: 'Name', kind: 'text', sortable: true, primary: false },
      { key: 'confirmed', label: 'Confirmed', kind: 'boolean', sortable: false, primary: false },
    ],
    itemSchema: { type: 'object', properties: { name: { type: 'string', title: 'Name' } } },
    filters: [{ key: 'confirmed', label: 'Status', options: [{ value: 'yes', label: 'Confirmed' }, { value: 'no', label: 'Pending' }] }],
    actions: [{ id: 'confirm', label: 'Confirm', risk: 'safe', bulk: true, description: null, inputSchema: null }],
    searchable: true, canCreate: false, canUpdate: true, canDelete: true, canDownload: false,
    defaultSort: 'email', defaultDescending: false,
  },
];

export const PLUGIN_DATA_ROWS = [
  { key: 's1', title: null, values: { email: 'ada@example.test', name: 'Ada', confirmed: true } },
  { key: 's2', title: null, values: { email: 'max@example.test', name: 'Max', confirmed: false } },
];

export const MARKETPLACE = {
  items: [
    {
      id: 'dcms.blog', name: 'Blog', version: '1.4.0', summary: 'Posts with tags and scheduling.',
      description: 'A blog with drafts, scheduled publishing and a tag vocabulary.',
      category: 'Content', tags: ['content', 'writing'], icon: 'Newspaper', allowMultiple: true,
      permissions: [{ key: 'content:write', displayName: 'Write content' }],
      contentTypes: ['post'], dependencies: [], addsNavEntry: true,
      instanceCount: 1, enabledCount: 1, installed: true, source: 'builtin',
    },
    {
      id: 'dcms.forms', name: 'Forms', version: '2.0.1', summary: 'Collect submissions from a site.',
      description: 'Public forms with spam control and optional notification email.',
      category: 'Engagement', tags: ['forms'], icon: 'ClipboardList', allowMultiple: true,
      permissions: [
        { key: 'forms:read', displayName: 'Read submissions' },
        { key: 'forms:manage', displayName: 'Manage forms' },
      ],
      contentTypes: [], dependencies: [], addsNavEntry: true,
      instanceCount: 0, enabledCount: 0, installed: false, source: 'builtin',
    },
  ],
};

export const CONTENT_ROWS = [
  {
    id: 'eeeeeeee-0000-0000-0000-000000000001', contentType: 'post', slug: 'hello-world',
    title: 'Hello, world', status: 'Published', updatedAt: '2026-09-01T08:00:00Z',
    publishedAt: '2026-09-01T09:00:00Z', scheduledPublishAt: null,
  },
  {
    id: 'eeeeeeee-0000-0000-0000-000000000002', contentType: 'post', slug: 'launch-notes',
    title: 'Launch notes', status: 'Draft', updatedAt: '2026-09-05T08:00:00Z',
    publishedAt: null, scheduledPublishAt: '2026-09-20T08:00:00Z',
  },
];

/** `GET /admin/tenant` — the workspace, as its own settings page reads it. */
export const WORKSPACE = {
  tenantId: TENANT.tenantId,
  slug: TENANT.slug,
  name: TENANT.name,
  status: 'Active',
  createdAt: '2026-01-04T09:00:00Z',
  owners: [{ userId: '11111111-1111-1111-1111-111111111111', email: 'ada@example.test' }],
  isOwner: true,
  members: [
    { membershipId: '88888888-0000-0000-0000-000000000001', userId: '11111111-1111-1111-1111-111111111111', email: 'ada@example.test' },
  ],
  counts: { members: 1, sites: 0, domains: 0, mediaAssets: 2, contentItems: 2 },
};

export const SCHEDULED = [
  {
    scheduleId: '77777777-0000-0000-0000-000000000001',
    itemId: CONTENT_ROWS[1].id,
    instanceId: INSTANCE_BLOG,
    instanceName: 'Press room',
    pluginId: 'dcms.blog',
    contentType: 'post',
    slug: 'launch-notes',
    // Deliberately not the row's own title: the queue shows the version that is QUEUED, and an
    // author who has kept writing since scheduling should see the headline that is going out.
    title: 'Launch notes, as approved',
    status: 'Draft',
    publishAt: '2026-09-20T08:00:00Z',
  },
];

export const NOTIFICATIONS = {
  items: [
    {
      id: 'ffffffff-0000-0000-0000-000000000001', kind: 'site.published', severity: 'Success',
      titleKey: 'notifications.kinds.site.published.title',
      bodyKey: 'notifications.kinds.site.published.body',
      paramsJson: '{"site":"acme-site"}', linkPath: '/sites',
      resourceType: 'site', resourceId: null, actorUserId: null,
      createdAt: '2026-09-07T11:00:00Z', readAt: null, dismissedAt: null,
    },
  ],
  unreadCount: 1,
  nextCursor: null,
};

/** Platform console fixtures. */
export const PLATFORM_PERMISSIONS = [
  'platform:overview:read', 'platform:tenants:read', 'platform:tenants:lifecycle',
  'platform:users:read', 'platform:audit:read', 'platform:observability:read',
  'platform:logs:read', 'platform:roles:manage', 'platform:certificates:manage', 'platform:ratelimits:manage',
  'platform:notifications:read', 'platform:ops:act',
];

/** The console's tenant row carries a count per thing a tenant has; nothing is optional. */
const tenantRow = (t: typeof TENANT, status: string, createdAt: string) => ({
  tenantId: t.tenantId,
  slug: t.slug,
  name: t.name,
  status,
  createdAt,
  members: 3,
  domains: 1,
  verifiedDomains: 1,
  sites: 2,
  contentItems: 12,
  publishedItems: 9,
  mediaAssets: 2,
  enabledPlugins: 1,
  visitorAccounts: 0,
  formSubmissions: 4,
  storageBytes: 83_968,
});

export const PLATFORM_TENANTS = [
  tenantRow(TENANT, 'Active', '2026-01-04T09:00:00Z'),
  tenantRow(OTHER_TENANT, 'Suspended', '2026-02-11T09:00:00Z'),
];

export const PLATFORM_OVERVIEW = {
  tenants: 2,
  activeTenants: 1,
  suspendedTenants: 1,
  sites: 3,
  contentItems: 24,
  publishedItems: 18,
  mediaAssets: 2,
  storageBytes: 83_968,
  members: 7,
};

export const PLATFORM_GROWTH = [
  { day: '2026-09-06', newTenants: 0, newUsers: 1, newSites: 0, newContent: 3 },
  { day: '2026-09-07', newTenants: 1, newUsers: 2, newSites: 1, newContent: 5 },
];

export const PLATFORM_AUDIT = {
  items: [
    {
      id: '99999999-0000-0000-0000-000000000001', occurredAt: '2026-09-07T10:00:00Z',
      action: 'tenant.suspend', category: 'tenancy', outcome: 'success', severity: 2,
      actorKind: 'User', actorDisplay: 'Ada Lovelace', actorAttribution: 'propagated',
      resourceType: 'tenant', resourceLabel: 'globex', service: 'admin-api',
      statusCode: 200, traceId: 'abcdef0123456789abcdef0123456789',
      correlationId: 'corr-1', seq: 42,
    },
  ],
  hasMore: false,
  nextCursor: null,
};

/** GET /api/admin/marketplace/:id/reference — a plugin's developer reference. */
export const PLUGIN_REFERENCE = {
  id: 'forms', name: 'Forms', version: '1.0.0', description: 'Visitor forms and their submissions.',
  source: 'builtin', sdkMajor: 1,
  packages: [
    { id: 'Dcms.PluginSdk.Abstractions', purpose: 'The plugin SDK: every plugin builds against it.' },
    { id: 'Dcms.Plugins.Forms.Api', purpose: 'Reference this to call the plugin, handle its events or intercept its hooks.' },
  ],
  provides: [{
    id: 'forms.submissions@1', description: 'Stored submissions of a Forms instance.',
    clrType: 'Dcms.Plugins.Forms.Api.IFormSubmissions', assembly: 'Dcms.Plugins.Forms.Api', providers: ['forms'],
    operations: [{
      name: 'List', method: 'ListAsync', risk: 'read', permission: 'content:read',
      exposed: ['plugins', 'admin', 'ai'], returnsExternalText: true, description: 'Newest-first page of submissions.',
      inputType: 'SubmissionQuery', outputType: 'Task<SubmissionPage>',
      inputSchema: { type: 'object', properties: { formName: { type: ['string', 'null'] }, page: { type: 'integer' } } },
      outputSchema: { type: 'object' },
    }],
    events: [{ name: 'form.submitted', clrType: 'FormSubmitted', schema: { type: 'object' } }],
    hooks: [{ name: 'forms.submitting', clrType: 'FormSubmitting', schema: { type: 'object' } }],
  }],
  consumes: [{ contractId: 'visitors.identity@1', optional: true, bindingConfigKey: null, providers: ['visitor-auth'] }],
  subscribes: [], intercepts: [], jobs: [], contentTypes: [],
  config: { type: 'object', properties: { forms: { type: 'array' } } },
  publicConfigKeys: [], permissions: [{ key: 'plugin:forms:manage', displayName: 'Manage forms' }],
  cSharp: '// dotnet add package Dcms.Plugins.Forms.Api\nusing Dcms.Plugins.Forms.Api;\n\nconsumes: [ContractRequirement.Of<IFormSubmissions>()],\nintercepts: [HookSubscription.Of<FormSubmitting, MyInterceptor>(priority: 0)],',
};
