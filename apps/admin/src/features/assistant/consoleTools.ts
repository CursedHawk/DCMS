import { Perm } from '../../lib/permissions';
import { api } from '../../lib/api';
import type { AssistantTool } from './tools';
import { obj, required, str } from './toolArgs';

/**
 * The rest of the console as assistant tools (epic #624): sites, domains, environments, API
 * connections and the workspace's operations — each a call to the route its screen uses, with the
 * operator's own token, like every built-in tool.
 *
 * <p><b>Never here:</b> the workspace's own access model — members, workspace roles, invitations,
 * ownership — and the assistant's own settings. Environment links carry grants, so they are out
 * too, and a deployment or lock that would change workspace roles is refused before it is sent.
 * Secrets go in only: no tool returns one.</p>
 */

const enc = encodeURIComponent;
const json = async (call: Promise<unknown>) => JSON.stringify((await call) ?? { ok: true });
const closed = (properties: Record<string, unknown>, required: string[] = []) => ({
  type: 'object',
  properties,
  ...(required.length ? { required } : {}),
  additionalProperties: false,
});
const id = (what: string) => ({ type: 'string', description: `The ${what} id.` });

/** The kind of environment configuration that is the workspace's roles: out of the assistant's reach. */
const ROLES_KIND = 'roles';

export const CONSOLE_TOOLS: AssistantTool[] = [
  // ---- Content and media, beyond the basics ----
  {
    name: 'delete_content',
    description: 'Delete a content item for good, every version of it. Prefer unpublish_content when it should only stop being public.',
    permission: Perm.ContentWrite,
    risk: 'dangerous',
    invalidates: ['content'],
    input_schema: closed({ id: id('content item') }, ['id']),
    summarize: (input) => `Delete content ${str(input, 'id') ?? '?'} for good`,
    describe: () => 'Deleted content',
    run: (input) => json(api.del(`/admin/content/${enc(required(input, 'id'))}`)),
  },
  {
    name: 'cancel_content_schedule',
    description: 'Cancel a scheduled publish. The item stays as it is.',
    permission: Perm.ContentPublish,
    risk: 'safe',
    invalidates: ['content'],
    input_schema: closed({ id: id('content item') }, ['id']),
    summarize: (input) => `Cancel the scheduled publish of ${str(input, 'id') ?? '?'}`,
    describe: () => 'Cancelled the schedule',
    run: (input) => json(api.del(`/admin/content/${enc(required(input, 'id'))}/schedule`)),
  },
  {
    name: 'get_media_usage',
    description: "The media library's storage use against the workspace's quota.",
    permission: Perm.MediaRead,
    input_schema: closed({}),
    run: () => json(api.get('/admin/media/usage')),
  },
  {
    name: 'rename_media',
    description: "Rename a media asset's file name. Its URL and every use of it stay as they are.",
    permission: Perm.MediaWrite,
    risk: 'safe',
    invalidates: ['media', 'media-detail'],
    input_schema: closed({ id: id('asset'), fileName: { type: 'string', description: 'The new file name.' } }, ['id', 'fileName']),
    summarize: (input) => `Rename asset ${str(input, 'id') ?? '?'} to "${str(input, 'fileName') ?? '?'}"`,
    describe: (input) => `Renamed to ${str(input, 'fileName') ?? '?'}`,
    run: (input) => json(api.patch(`/admin/media/${enc(required(input, 'id'))}`, { fileName: required(input, 'fileName') })),
  },
  {
    name: 'rename_media_folder',
    description: 'Rename a media folder; what is in it stays where it is.',
    permission: Perm.MediaWrite,
    risk: 'safe',
    invalidates: ['media-folders'],
    input_schema: closed({ id: id('folder'), name: { type: 'string', description: 'The new name.' } }, ['id', 'name']),
    summarize: (input) => `Rename folder ${str(input, 'id') ?? '?'} to "${str(input, 'name') ?? '?'}"`,
    describe: (input) => `Renamed folder to ${str(input, 'name') ?? '?'}`,
    run: (input) => json(api.patch(`/admin/media/folders/${enc(required(input, 'id'))}`, { name: required(input, 'name') })),
  },
  {
    name: 'delete_media_folder',
    description: 'Delete a media folder. Move or delete what is in it first if the server refuses.',
    permission: Perm.MediaWrite,
    risk: 'dangerous',
    invalidates: ['media-folders', 'media'],
    input_schema: closed({ id: id('folder') }, ['id']),
    summarize: (input) => `Delete media folder ${str(input, 'id') ?? '?'}`,
    describe: () => 'Deleted the folder',
    run: (input) => json(api.del(`/admin/media/folders/${enc(required(input, 'id'))}`)),
  },

  // ---- Sites ----
  {
    name: 'get_site',
    description: "One site: its render mode, live build and publishing state. Its pages and files are the site builder's and IDE's to edit.",
    permission: Perm.SiteEdit,
    input_schema: closed({ id: id('site') }, ['id']),
    run: (input) => json(api.get(`/admin/sites/${enc(required(input, 'id'))}`)),
  },
  {
    name: 'create_site',
    description:
      'Create a site. renderMode: ReactBuilder (the visual React builder), ReactApp (code in the IDE), StaticPrerender (the HTML/CSS builder) or StaticFiles (uploaded files); the console\'s default when omitted.',
    permission: Perm.SiteEdit,
    risk: 'safe',
    invalidates: ['sites'],
    input_schema: closed(
      {
        name: { type: 'string' },
        renderMode: { type: 'string', enum: ['ReactBuilder', 'ReactApp', 'StaticPrerender', 'StaticFiles'] },
      },
      ['name'],
    ),
    summarize: (input) => `Create the site "${str(input, 'name') ?? '?'}"`,
    describe: (input) => `Created site ${str(input, 'name') ?? ''}`.trim(),
    run: (input) =>
      json(api.post('/admin/sites', { name: required(input, 'name'), renderMode: str(input, 'renderMode') })),
  },
  {
    name: 'list_site_builds',
    description: "A site's builds, newest first: status, commit and which one is live.",
    permission: Perm.SiteEdit,
    maxResultChars: 16_000,
    input_schema: closed({ siteId: id('site') }, ['siteId']),
    run: (input) => json(api.get(`/admin/sites/${enc(required(input, 'siteId'))}/builds`)),
  },
  {
    name: 'get_site_build_log',
    description: "One build's log from list_site_builds, to find out why it failed.",
    permission: Perm.SiteEdit,
    maxResultChars: 16_000,
    untrustedSource: 'a site build log: output of the site’s own code',
    input_schema: closed({ siteId: id('site'), buildId: id('build') }, ['siteId', 'buildId']),
    run: (input) => json(api.get(`/admin/sites/${enc(required(input, 'siteId'))}/builds/${enc(required(input, 'buildId'))}/log`)),
  },
  {
    name: 'publish_site',
    description: 'Build and publish a site: what is on its release branch goes live on its domains.',
    permission: Perm.SitePublish,
    risk: 'dangerous',
    invalidates: ['site', 'site-builds', 'sites'],
    input_schema: closed({ siteId: id('site') }, ['siteId']),
    summarize: (input) => `Publish site ${str(input, 'siteId') ?? '?'} — it goes live`,
    describe: () => 'Published the site',
    run: (input) => json(api.post(`/admin/sites/${enc(required(input, 'siteId'))}/publish`)),
  },
  {
    name: 'activate_site_build',
    description: 'Make an earlier (or another) build of a site the live one: a rollback.',
    permission: Perm.SitePublish,
    risk: 'dangerous',
    invalidates: ['site', 'site-builds'],
    input_schema: closed({ siteId: id('site'), buildId: id('build') }, ['siteId', 'buildId']),
    summarize: (input) => `Make build ${str(input, 'buildId') ?? '?'} live on site ${str(input, 'siteId') ?? '?'}`,
    describe: () => 'Switched the live build',
    run: (input) =>
      json(api.post(`/admin/sites/${enc(required(input, 'siteId'))}/builds/${enc(required(input, 'buildId'))}/activate`)),
  },
  {
    name: 'delete_site',
    description: 'Delete a site for good: its builds, files and repository. Its domains stop serving it.',
    permission: Perm.SitePublish,
    risk: 'dangerous',
    invalidates: ['sites', 'domains'],
    input_schema: closed({ siteId: id('site') }, ['siteId']),
    summarize: (input) => `Delete site ${str(input, 'siteId') ?? '?'} and everything in it, for good`,
    describe: () => 'Deleted the site',
    run: (input) => json(api.del(`/admin/sites/${enc(required(input, 'siteId'))}`)),
  },

  // ---- Domains and certificates ----
  {
    name: 'list_domains',
    description: "The workspace's domains: verification state, the site each serves and which is primary.",
    permission: Perm.DomainsManage,
    input_schema: closed({}),
    run: () => json(api.get('/admin/domains')),
  },
  {
    name: 'list_certificates',
    description: "The domains' TLS certificates and their expiry.",
    permission: Perm.DomainsManage,
    input_schema: closed({}),
    run: () => json(api.get('/admin/domains/certificates')),
  },
  {
    name: 'add_domain',
    description: 'Add a custom domain. The answer says which DNS record proves it is the workspace’s; then verify_domain.',
    permission: Perm.DomainsManage,
    risk: 'safe',
    invalidates: ['domains'],
    input_schema: closed({ hostname: { type: 'string', description: 'e.g. www.example.com' } }, ['hostname']),
    summarize: (input) => `Add the domain ${str(input, 'hostname') ?? '?'}`,
    describe: (input) => `Added ${str(input, 'hostname') ?? 'the domain'}`,
    run: (input) => json(api.post('/admin/domains', { hostname: required(input, 'hostname') })),
  },
  {
    name: 'verify_domain',
    description: "Check a domain's DNS verification record now, after the operator added it.",
    permission: Perm.DomainsManage,
    risk: 'safe',
    invalidates: ['domains'],
    input_schema: closed({ id: id('domain') }, ['id']),
    summarize: (input) => `Verify domain ${str(input, 'id') ?? '?'}`,
    describe: () => 'Checked the domain',
    run: (input) => json(api.post(`/admin/domains/${enc(required(input, 'id'))}/verify`)),
  },
  {
    name: 'assign_domain_site',
    description: 'Point a domain at a site (optionally one of its publish branches), or at none with siteId null.',
    permission: Perm.DomainsManage,
    risk: 'dangerous',
    invalidates: ['domains'],
    input_schema: closed(
      {
        id: id('domain'),
        siteId: { type: ['string', 'null'], description: 'The site to serve; null for none.' },
        branch: { type: 'string', description: 'A publish branch of the site; its release when omitted.' },
      },
      ['id', 'siteId'],
    ),
    summarize: (input) => `Point domain ${str(input, 'id') ?? '?'} at ${str(input, 'siteId') ?? 'no site'}`,
    describe: () => 'Repointed the domain',
    run: (input) =>
      json(api.post(`/admin/domains/${enc(required(input, 'id'))}/site`, { siteId: str(input, 'siteId') ?? null, branch: str(input, 'branch') ?? null })),
  },
  {
    name: 'set_primary_domain',
    description: 'Make a domain its site’s primary address; the others redirect to it.',
    permission: Perm.DomainsManage,
    risk: 'dangerous',
    invalidates: ['domains'],
    input_schema: closed({ id: id('domain') }, ['id']),
    summarize: (input) => `Make domain ${str(input, 'id') ?? '?'} primary`,
    describe: () => 'Changed the primary domain',
    run: (input) => json(api.post(`/admin/domains/${enc(required(input, 'id'))}/primary`)),
  },
  {
    name: 'reissue_certificate',
    description: "Ask for a domain's TLS certificate to be issued again.",
    permission: Perm.DomainsManage,
    risk: 'safe',
    invalidates: ['domain-certificates'],
    input_schema: closed({ id: id('domain') }, ['id']),
    summarize: (input) => `Reissue the certificate of domain ${str(input, 'id') ?? '?'}`,
    describe: () => 'Asked for a new certificate',
    run: (input) => json(api.post(`/admin/domains/${enc(required(input, 'id'))}/certificate/reissue`)),
  },
  {
    name: 'remove_domain',
    description: 'Remove a domain: it stops serving anything here.',
    permission: Perm.DomainsManage,
    risk: 'dangerous',
    invalidates: ['domains', 'domain-certificates'],
    input_schema: closed({ id: id('domain') }, ['id']),
    summarize: (input) => `Remove domain ${str(input, 'id') ?? '?'}`,
    describe: () => 'Removed the domain',
    run: (input) => json(api.del(`/admin/domains/${enc(required(input, 'id'))}`)),
  },

  // ---- Environments and deployments (links excluded: they carry grants) ----
  {
    name: 'list_environments',
    description: "The workspace's environments: which promote to which, and whether each is ready.",
    permission: Perm.EnvironmentsManage,
    input_schema: closed({}),
    run: () => json(api.get('/admin/environments')),
  },
  {
    name: 'create_environment',
    description:
      "Create an environment as a copy of another's configuration (production's by default). feature: a short-lived branch environment; empty: no configuration copied.",
    permission: Perm.EnvironmentsManage,
    risk: 'safe',
    invalidates: ['environments', 'me-tenants'],
    input_schema: closed(
      {
        name: { type: 'string' },
        from: { type: 'string', description: 'The environment id to copy; production when omitted.' },
        feature: { type: 'boolean' },
        empty: { type: 'boolean' },
      },
      ['name'],
    ),
    summarize: (input) => `Create the environment "${str(input, 'name') ?? '?'}"`,
    describe: (input) => `Created environment ${str(input, 'name') ?? ''}`.trim(),
    run: (input) =>
      json(api.post('/admin/environments', {
        name: required(input, 'name'),
        from: str(input, 'from'),
        feature: input.feature === true,
        empty: input.empty === true,
      })),
  },
  {
    name: 'update_environment',
    description: 'Rename an environment, reorder it, or change which environments may deploy into it.',
    permission: Perm.EnvironmentsManage,
    risk: 'safe',
    invalidates: ['environments', 'me-tenants'],
    input_schema: closed(
      {
        id: id('environment'),
        name: { type: 'string' },
        order: { type: 'number' },
        promotesFrom: { type: 'array', items: { type: 'string' }, description: 'Environment ids allowed to deploy into this one.' },
      },
      ['id'],
    ),
    summarize: (input) => `Change environment ${str(input, 'id') ?? '?'}`,
    describe: () => 'Changed the environment',
    run: (input) => {
      const { id: target, ...body } = input;
      return json(api.patch(`/admin/environments/${enc(String(target ?? ''))}`, body));
    },
  },
  {
    name: 'delete_environment',
    description: 'Delete an environment and everything in it, for good.',
    permission: Perm.EnvironmentsManage,
    risk: 'dangerous',
    invalidates: ['environments', 'me-tenants'],
    input_schema: closed({ id: id('environment') }, ['id']),
    summarize: (input) => `Delete environment ${str(input, 'id') ?? '?'} and everything in it`,
    describe: () => 'Deleted the environment',
    run: (input) => json(api.del(`/admin/environments/${enc(required(input, 'id'))}`)),
  },
  {
    name: 'get_environment_locks',
    description:
      "An environment's locks: per kind of configuration (or one item of it), Mixed (edited and deployed), Local (edited only) or Managed (deployed only).",
    permission: Perm.EnvironmentsManage,
    input_schema: closed({ id: id('environment') }, ['id']),
    run: (input) => json(api.get(`/admin/environments/${enc(required(input, 'id'))}/policies`)),
  },
  {
    name: 'set_environment_locks',
    description:
      "Replace an environment's locks (get them first and send the whole list back changed). Locks on workspace roles are not yours to change and are kept as they are.",
    permission: Perm.EnvironmentsManage,
    risk: 'dangerous',
    invalidates: ['environments'],
    input_schema: closed(
      {
        id: id('environment'),
        policies: {
          type: 'array',
          items: closed(
            {
              kind: { type: 'string' },
              itemKey: { type: ['string', 'null'] },
              mode: { type: 'string', enum: ['Mixed', 'Local', 'Managed'] },
            },
            ['kind', 'mode'],
          ),
        },
      },
      ['id', 'policies'],
    ),
    summarize: (input) => `Change the locks of environment ${str(input, 'id') ?? '?'}`,
    describe: () => 'Changed the locks',
    run: async (input) => {
      const environment = required(input, 'id');
      const given = (Array.isArray(input.policies) ? input.policies : []) as { kind?: string }[];
      if (given.some((p) => p.kind === ROLES_KIND)) {
        throw new Error('Locks on workspace roles are changed in the console only.');
      }
      // The roles' locks as they are: the PUT replaces the whole list.
      const current = await api.get<{ kind: string }[]>(`/admin/environments/${enc(environment)}/policies`);
      return json(api.put(`/admin/environments/${enc(environment)}/policies`, [...current.filter((p) => p.kind === ROLES_KIND), ...given]));
    },
  },
  {
    name: 'list_pending_deployments',
    description: 'What is ready to deploy into this environment from another: each migration with its changes and any conflicts.',
    permission: Perm.EnvironmentsPromote,
    maxResultChars: 16_000,
    input_schema: closed({ from: { type: 'string', description: 'The environment id to deploy from.' } }, ['from']),
    run: (input) => json(api.get(`/admin/environments/pending?from=${enc(required(input, 'from'))}`)),
  },
  {
    name: 'apply_pending_deployments',
    description:
      'Deploy pending migrations from another environment into this one, up to and including one of them. Conflicts need resolutions keyed "{migrationId}/{changeId}" (overwrite | skip) or "…#{path}" (ours | theirs). Deployments that change workspace roles are the console’s to apply.',
    permission: Perm.EnvironmentsPromote,
    risk: 'dangerous',
    invalidates: ['environments'],
    input_schema: closed(
      {
        from: { type: 'string', description: 'The environment id to deploy from.' },
        upTo: { type: 'string', description: 'The last migration id to apply.' },
        resolutions: { type: 'object', additionalProperties: { type: 'string', enum: ['overwrite', 'skip', 'ours', 'theirs'] } },
      },
      ['from', 'upTo'],
    ),
    summarize: (input) => `Deploy from environment ${str(input, 'from') ?? '?'} up to migration ${str(input, 'upTo') ?? '?'}`,
    describe: () => 'Started the deployment',
    run: async (input) => {
      const from = required(input, 'from');
      const upTo = required(input, 'upTo');
      const pending = await api.get<{ migration: { migrationId: string }; changes: { kind: string }[] }[]>(
        `/admin/environments/pending?from=${enc(from)}`,
      );
      const last = pending.findIndex((p) => p.migration.migrationId === upTo);
      if (last < 0) throw new Error(`Migration ${upTo} is not pending from that environment; list them again.`);
      if (pending.slice(0, last + 1).some((p) => p.changes.some((c) => c.kind === ROLES_KIND))) {
        throw new Error('This deployment changes workspace roles: the operator applies it in the console (Deployments).');
      }
      return json(api.post('/admin/environments/pending/apply', { from, upTo, resolutions: obj(input, 'resolutions') }));
    },
  },

  // ---- External API connections (keys write-only) ----
  {
    name: 'list_api_connections',
    description: 'The external APIs sites read data from: base URL, auth kind, operations, whether a key is stored (never the key).',
    permission: Perm.PluginsManage,
    input_schema: closed({}),
    run: () => json(api.get('/admin/connections')),
  },
  {
    name: 'save_connection',
    description:
      'Add or change an external API connection. authKind: none, bearer, header or query (authName names the header or parameter). The key is set separately with set_connection_secret.',
    permission: Perm.PluginsManage,
    risk: 'safe',
    invalidates: ['api-connections'],
    input_schema: closed(
      {
        slug: { type: 'string', description: 'Lowercase letters, digits and hyphens.' },
        name: { type: 'string' },
        baseUrl: { type: 'string' },
        authKind: { type: 'string', enum: ['none', 'bearer', 'header', 'query'] },
        authName: { type: 'string' },
        operations: { type: 'array', items: { type: 'string' }, description: 'Paths under baseUrl sites may read.' },
        refreshMinutes: { type: 'number' },
      },
      ['slug', 'name', 'baseUrl', 'authKind', 'operations'],
    ),
    summarize: (input) => `Save the API connection ${str(input, 'slug') ?? '?'} (${str(input, 'baseUrl') ?? '?'})`,
    describe: (input) => `Saved connection ${str(input, 'slug') ?? ''}`.trim(),
    run: (input) => {
      const { slug, ...body } = input;
      // A null key keeps whatever is stored: keys only ever go in through set_connection_secret.
      return json(api.put(`/admin/connections/${enc(String(slug ?? ''))}`, { ...body, secret: null }));
    },
  },
  {
    name: 'refresh_connection',
    description: "Fetch a connection's operations now, to see their shapes and errors.",
    permission: Perm.PluginsManage,
    risk: 'safe',
    invalidates: ['api-connections'],
    untrustedSource: 'an external API’s answer',
    input_schema: closed({ slug: { type: 'string' } }, ['slug']),
    summarize: (input) => `Refresh connection ${str(input, 'slug') ?? '?'}`,
    describe: () => 'Refreshed the connection',
    run: (input) => json(api.post(`/admin/connections/${enc(required(input, 'slug'))}/refresh`)),
  },
  {
    name: 'set_connection_secret',
    description: "Set or replace a connection's API key. Write-only: it can never be read back, so take it from the operator, never invent one.",
    permission: Perm.PluginsManage,
    risk: 'dangerous',
    invalidates: ['api-connections'],
    input_schema: closed({ slug: { type: 'string' }, secret: { type: 'string' } }, ['slug', 'secret']),
    // The card names the connection, never the key.
    summarize: (input) => `Set the API key of connection ${str(input, 'slug') ?? '?'}`,
    describe: () => 'Set the key',
    run: (input) => json(api.put(`/admin/connections/${enc(required(input, 'slug'))}/secret`, { secret: required(input, 'secret') })),
  },
  {
    name: 'delete_connection',
    description: 'Delete an external API connection: sites reading it get nothing.',
    permission: Perm.PluginsManage,
    risk: 'dangerous',
    invalidates: ['api-connections'],
    input_schema: closed({ slug: { type: 'string' } }, ['slug']),
    summarize: (input) => `Delete connection ${str(input, 'slug') ?? '?'}`,
    describe: () => 'Deleted the connection',
    run: (input) => json(api.del(`/admin/connections/${enc(required(input, 'slug'))}`)),
  },

  // ---- Operations: jobs, audit, usage ----
  {
    name: 'list_jobs',
    description: 'Background jobs (imports, builds, deployments), newest first. status: active, finished, failed or all.',
    // Every member sees the jobs they may; the assistant's own gate (site:edit) is the floor.
    permission: Perm.SiteEdit,
    maxResultChars: 16_000,
    input_schema: closed({ status: { type: 'string', enum: ['active', 'finished', 'failed', 'all'] } }),
    run: (input) => {
      const status = str(input, 'status') ?? 'all';
      return json(api.get(`/admin/jobs?limit=25${status === 'all' ? '' : `&status=${enc(status)}`}`));
    },
  },
  {
    name: 'get_job',
    description: 'One background job in full: its progress, its result or its error.',
    permission: Perm.SiteEdit,
    input_schema: closed({ id: id('job') }, ['id']),
    run: (input) => json(api.get(`/admin/jobs/${enc(required(input, 'id'))}`)),
  },
  {
    name: 'retry_job',
    description: 'Run a failed background job again, from the start.',
    permission: Perm.SiteEdit,
    risk: 'safe',
    invalidates: ['jobs'],
    input_schema: closed({ id: id('job') }, ['id']),
    summarize: (input) => `Retry job ${str(input, 'id') ?? '?'}`,
    describe: () => 'Retried the job',
    run: (input) => json(api.post(`/admin/jobs/${enc(required(input, 'id'))}/retry`, {})),
  },
  {
    name: 'cancel_job',
    description: 'Cancel a running job; what it already did stays done.',
    permission: Perm.SiteEdit,
    risk: 'dangerous',
    invalidates: ['jobs'],
    input_schema: closed({ id: id('job') }, ['id']),
    summarize: (input) => `Cancel job ${str(input, 'id') ?? '?'}`,
    describe: () => 'Cancelled the job',
    run: (input) => json(api.post(`/admin/jobs/${enc(required(input, 'id'))}/cancel`, {})),
  },
  {
    name: 'search_audit',
    description: 'The audit log, newest first, optionally by action (e.g. "plugin_instance.updated"), category or outcome.',
    permission: Perm.AuditRead,
    maxResultChars: 16_000,
    untrustedSource: 'the audit log: it records what people and visitors sent',
    input_schema: closed({
      action: { type: 'string' },
      category: { type: 'string' },
      outcome: { type: 'string', enum: ['success', 'failure', 'denied'] },
    }),
    run: (input) => {
      const params = new URLSearchParams();
      for (const key of ['action', 'category', 'outcome']) {
        const value = str(input, key);
        if (value) params.set(key, value);
      }
      return json(api.get(`/admin/audit?${params}`));
    },
  },
  {
    name: 'get_usage',
    description: "The workspace's data and storage use per area, against its quotas.",
    permission: Perm.TenantSettings,
    input_schema: closed({}),
    run: () => json(api.get('/admin/usage')),
  },
  {
    name: 'recalculate_usage',
    description: "Count the workspace's data and storage usage again now, when it looks stale.",
    permission: Perm.TenantSettings,
    risk: 'safe',
    invalidates: ['usage'],
    input_schema: closed({}),
    summarize: () => 'Recalculate the workspace’s usage',
    describe: () => 'Recalculated usage',
    run: () => json(api.post('/admin/usage/recalculate')),
  },
];
