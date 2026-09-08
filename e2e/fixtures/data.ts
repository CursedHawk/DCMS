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
    { to: '/members', labelKey: 'nav.members', icon: 'Users', group: 'admin', label: null },
    { to: '/roles', labelKey: 'nav.roles', icon: 'ShieldCheck', group: 'admin', label: null },
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
  'platform:logs:read', 'platform:roles:manage', 'platform:certificates:manage',
  'platform:notifications:read', 'platform:ops:act',
];

export const PLATFORM_TENANTS = [
  { tenantId: TENANT.tenantId, slug: TENANT.slug, name: TENANT.name, status: 'Active', createdAt: '2026-01-04T09:00:00Z' },
  { tenantId: OTHER_TENANT.tenantId, slug: OTHER_TENANT.slug, name: OTHER_TENANT.name, status: 'Suspended', createdAt: '2026-02-11T09:00:00Z' },
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
