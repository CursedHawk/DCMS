import type { Page, Route, Request } from '@playwright/test';
import * as data from './data';
import { OPERATOR_SUB } from './oidc';

export interface RecordedRequest {
  method: string;
  /** Path only, no query. `/api/admin/media`. */
  path: string;
  query: URLSearchParams;
  headers: Record<string, string>;
  /** Parsed JSON body when the request sent one, otherwise the raw string or null. */
  body: unknown;
}

type Responder = (ctx: {
  route: Route;
  request: Request;
  params: Record<string, string>;
  query: URLSearchParams;
  body: unknown;
}) => unknown | Promise<unknown>;

interface Entry {
  method: string;
  segments: string[];
  respond: Responder;
}

/** A 1x1 transparent PNG, for the routes that answer with bytes rather than JSON. */
const PNG = Buffer.from(
  'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==',
  'base64',
);

/**
 * The server, as far as the browser is concerned.
 *
 * <p>Installed with `page.route`, so it intercepts inside the browser: the Vite dev server's
 * proxy is never reached and no backend needs to exist. Three things it does that a plain
 * `page.route` call does not:</p>
 *
 * <ul>
 *   <li><b>Records every request</b>, so a spec can assert on what the console sent — the
 *   `X-Dcms-Tenant` header after a workspace switch, the body of a move, the fact that a
 *   forbidden screen never issued its query at all.</li>
 *   <li><b>Fails loudly on an unmocked path.</b> An unknown route answers 501 and lands in
 *   `missing`, and `expectNoMissingRoutes()` turns that into a test failure. Silently answering
 *   404 would make a fixture gap look like an application bug, which is the failure mode that
 *   makes mocked E2E worthless.</li>
 *   <li><b>Lets one spec override one route</b> without restating the dataset.</li>
 * </ul>
 */
export class MockApi {
  readonly requests: RecordedRequest[] = [];
  readonly missing: string[] = [];
  private readonly entries: Entry[] = [];

  /**
   * Registers a handler. `pattern` may contain `:name` segments and a trailing `*`.
   * Later registrations win, so a spec's override beats the default set.
   */
  on(method: string, pattern: string, respond: Responder | unknown): this {
    this.entries.unshift({
      method: method.toUpperCase(),
      segments: pattern.split('/').filter(Boolean),
      respond: typeof respond === 'function' ? (respond as Responder) : () => respond,
    });
    return this;
  }

  /** Every request the console made to `path` (exact, query ignored). */
  requestsTo(method: string, path: string): RecordedRequest[] {
    return this.requests.filter((r) => r.method === method.toUpperCase() && r.path === path);
  }

  expectNoMissingRoutes(): void {
    if (this.missing.length > 0) {
      throw new Error(
        `The console called routes with no fixture:\n  ${this.missing.join('\n  ')}\n` +
          'Add them in e2e/fixtures/api.ts, or override them in the spec.',
      );
    }
  }

  async install(page: Page): Promise<void> {
    await page.route('**/api/**', async (route, request) => {
      const url = new URL(request.url());
      const method = request.method();
      const body = parseBody(request);

      this.requests.push({
        method,
        path: url.pathname,
        query: url.searchParams,
        headers: request.headers(),
        body,
      });

      // A preflight never carries an app-level answer; letting it fall through to the 501
      // below would make every cross-origin fixture look broken.
      if (method === 'OPTIONS') return route.fulfill({ status: 204, body: '' });

      const segments = url.pathname.split('/').filter(Boolean);
      const hit = best(this.entries, method, segments);
      if (hit) {
        const result = await hit.entry.respond({
          route,
          request,
          params: hit.params,
          query: url.searchParams,
          body,
        });
        // A responder that returns nothing has fulfilled the route itself — that is how the
        // byte-serving and status-code cases are written.
        if (result === undefined) return;
        return route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify(result),
        });
      }

      this.missing.push(`${method} ${url.pathname}`);
      await route.fulfill({
        status: 501,
        contentType: 'application/json',
        body: JSON.stringify({ error: 'e2e: no fixture for this route', method, path: url.pathname }),
      });
    });
  }
}

/**
 * The matching entry, preferring the one with the most literal segments.
 *
 * <p>Registration order alone is not enough: `/media/:id` and `/media/folders` both match
 * `GET /api/admin/media/folders`, and whichever happened to be registered later would win —
 * which is how a fixture set starts answering the folder list with an asset. Literal segments
 * beat parameters; among equally specific patterns the most recently registered wins, so a
 * spec's override of the exact same path still takes precedence.</p>
 */
function best(
  entries: Entry[],
  method: string,
  segments: string[],
): { entry: Entry; params: Record<string, string> } | null {
  let winner: { entry: Entry; params: Record<string, string>; score: number } | null = null;
  for (const entry of entries) {
    if (entry.method !== method) continue;
    const params = match(entry.segments, segments);
    if (!params) continue;
    const score = entry.segments.filter((s) => !s.startsWith(':') && s !== '*').length;
    if (!winner || score > winner.score) winner = { entry, params, score };
  }
  return winner ? { entry: winner.entry, params: winner.params } : null;
}

function match(pattern: string[], actual: string[]): Record<string, string> | null {
  const params: Record<string, string> = {};
  for (let i = 0; i < pattern.length; i++) {
    const p = pattern[i];
    if (p === '*') return params;
    if (i >= actual.length) return null;
    if (p.startsWith(':')) {
      params[p.slice(1)] = actual[i];
      continue;
    }
    if (p !== actual[i]) return null;
  }
  return pattern.length === actual.length ? params : null;
}

function parseBody(request: Request): unknown {
  const raw = request.postData();
  if (raw == null) return null;
  try {
    return JSON.parse(raw);
  } catch {
    return raw;
  }
}

/** The admin SPA's default world: one workspace, an operator who may do everything in it. */
export function adminApi(
  options: { permissions?: string[]; isSuperAdmin?: boolean; tenants?: typeof data.PLATFORM_TENANTS } = {},
): MockApi {
  const api = new MockApi();
  const me = {
    isSuperAdmin: options.isSuperAdmin ?? false,
    permissions: options.permissions ?? data.ALL_TENANT_PERMISSIONS,
  };

  // Registration order does not matter for correctness (later wins, and none of these overlap),
  // but it reads as the shell first and then one block per feature.
  api
    .on('GET', '/api/admin/me/permissions', me)
    .on('GET', '/api/admin/me/tenants', [data.TENANT, data.OTHER_TENANT])
    .on('GET', '/api/admin/navigation', data.NAVIGATION)
    .on('GET', '/api/admin/notifications', data.NOTIFICATIONS)
    .on('POST', '/api/admin/notifications/read-all', { updated: 1 })
    .on('POST', '/api/admin/notifications/:id/read', {})
    .on('POST', '/api/admin/notifications/:id/dismiss', {})

    .on('GET', '/api/admin/media', ({ query }) => {
      const folderId = query.get('folderId');
      if (folderId == null) return data.MEDIA_ASSETS;
      if (folderId === '00000000-0000-0000-0000-000000000000') {
        return data.MEDIA_ASSETS.filter((a) => a.folderId == null);
      }
      return data.MEDIA_ASSETS.filter((a) => a.folderId === folderId);
    })
    .on('GET', '/api/admin/media/folders', data.MEDIA_FOLDERS)
    .on('GET', '/api/admin/media/usage', data.MEDIA_USAGE)
    .on('GET', '/api/admin/media/:id', ({ params }) => {
      const asset = data.MEDIA_ASSETS.find((a) => a.id === params.id);
      return { ...asset, contentType: 'image/png', variants: [] };
    })
    .on('GET', '/api/admin/media/:id/content', async ({ route }) => {
      await route.fulfill({ status: 200, contentType: 'image/png', body: PNG });
      return undefined;
    })
    .on('POST', '/api/admin/media/move', { moved: 1 })
    .on('POST', '/api/admin/media/delete', { deleted: 1 })
    .on('POST', '/api/admin/media/folders', ({ body }) => ({
      id: 'bbbbbbbb-0000-0000-0000-00000000000f',
      name: (body as { name: string }).name,
      parentId: (body as { parentId?: string | null }).parentId ?? null,
      createdAt: new Date().toISOString(),
      assetCount: 0,
    }))
    .on('PATCH', '/api/admin/media/folders/:id', {})
    .on('DELETE', '/api/admin/media/folders/:id', {})

    .on('GET', '/api/admin/plugins/instances', data.PLUGIN_INSTANCES)
    .on('GET', '/api/admin/plugins/catalog', data.PLUGIN_CATALOG)
    .on('GET', '/api/admin/marketplace', data.MARKETPLACE)
    .on('POST', '/api/admin/plugins/instances', ({ body }) => ({
      id: 'cccccccc-0000-0000-0000-00000000000f',
      pluginId: (body as { pluginId: string }).pluginId,
      slug: (body as { slug?: string }).slug ?? 'new-instance',
      name: (body as { name?: string }).name ?? 'New instance',
      description: '',
      enabled: true,
      config: '{}',
    }))

    .on('GET', '/api/admin/content/page', ({ query }) => {
      const search = (query.get('search') ?? '').toLowerCase();
      const status = query.get('status');
      let items = data.CONTENT_ROWS;
      if (search) items = items.filter((r) => r.title.toLowerCase().includes(search));
      if (status && status !== 'all') items = items.filter((r) => r.status === status);
      return { items, nextCursor: null, total: items.length };
    })
    .on('GET', '/api/admin/content/counts', { post: data.CONTENT_ROWS.length })
    .on('GET', '/api/admin/content/tags', [{ tag: 'launch', count: 1 }])
    .on('GET', '/api/admin/content', [])

    .on('GET', '/api/admin/tenant', data.WORKSPACE)
    // The SuperAdmin-only cross-tenant list, which is a different route from `/me/tenants`.
    .on('GET', '/api/admin/tenants', data.PLATFORM_TENANTS)
    .on('GET', '/api/admin/content/scheduled', { items: data.SCHEDULED })
    .on('GET', '/api/admin/audit', { items: [], nextCursor: null })
    .on('GET', '/api/admin/ai/settings', { provider: 'anthropic', model: null, hasKey: false })
    .on('GET', '/api/admin/ai/user-credentials', [])
    .on('GET', '/api/admin/openapi.json', { openapi: '3.1.0', info: { title: 'DCMS', version: '1' }, paths: {} })
    .on('POST', '/api/admin/content/:id/publish', { status: 'published' })
    .on('POST', '/api/admin/content/:id/unpublish', { status: 'draft' })
    .on('DELETE', '/api/admin/content/:id/schedule', { cancelled: 1 })
    .on('DELETE', '/api/admin/content/:id', {})
    .on('GET', '/api/admin/members', [])
    .on('GET', '/api/admin/invitations', [])
    .on('GET', '/api/admin/roles', [])
    .on('GET', '/api/admin/permissions/catalog', [])
    .on('GET', '/api/admin/sites', [])
    .on('GET', '/api/admin/sites/:id', {
      id: '11111111-1111-1111-1111-111111111111',
      name: 'Acme site',
      mode: 'ReactApp',
      slug: 'acme-site',
    })
    .on('GET', '/api/admin/domains', [])
    .on('GET', '/api/admin/domains/certificates', [])
    .on('GET', '/api/admin/domains/provisioned', []);

  return api;
}

/** The platform console's default world: an operator holding every platform permission. */
export function platformApi(options: { permissions?: string[]; isSuperAdmin?: boolean } = {}): MockApi {
  const api = new MockApi();
  // The whole shape `PlatformMe` declares, not just the two fields the permission checks read:
  // the topbar renders the operator's roles and email, and a fixture missing them crashes the
  // shell in a way that looks like a bug in the app.
  const me = {
    userId: OPERATOR_SUB,
    name: 'Ada Lovelace',
    email: 'ada@example.test',
    roles: options.isSuperAdmin ? ['SuperAdmin'] : ['PlatformOperator'],
    isSuperAdmin: options.isSuperAdmin ?? false,
    permissions: options.permissions ?? data.PLATFORM_PERMISSIONS,
  };

  api
    .on('GET', '/api/platform/me', me)
    .on('GET', '/api/platform/tenants', data.PLATFORM_TENANTS)
    .on('GET', '/api/platform/audit', data.PLATFORM_AUDIT)
    .on('GET', '/api/platform/notifications', { items: [], unreadCount: 0, nextCursor: null })
    .on('GET', '/api/platform/certificates', [])
    .on('GET', '/api/platform/overview', data.PLATFORM_OVERVIEW)
    .on('GET', '/api/platform/growth', data.PLATFORM_GROWTH)
    .on('GET', '/api/platform/stores', { stores: [] })
    .on('GET', '/api/platform/purge/loki', [])
    .on('GET', '/api/platform/roles', [])
    .on('GET', '/api/platform/permissions/catalog', { permissions: [] })
    .on('GET', '/api/platform/health/signals', {
      reachable: false, requestsPerSecond: null, errorRatio: null, latencyP95: null, targetsDown: null,
    })
    .on('POST', '/api/platform/tenants/:id/suspend', {})
    .on('POST', '/api/platform/tenants/:id/resume', {});

  return api;
}

export { data };
