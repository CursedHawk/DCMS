#!/usr/bin/env node
// Builds the fixture estate the scenarios run against, and writes down what it made.
//
//   node loadtest/seed/provision.mjs --env vps1
//   node loadtest/seed/provision.mjs --env vps1 -e seed.tenants=2
//
// Idempotent by design: a tenant whose slug already exists is adopted rather than
// recreated, so a run interrupted half way through can simply be repeated. That matters
// more than it sounds — provisioning touches six subsystems, and a seeder that could only
// ever run against a clean platform would be unusable exactly when something went wrong.
//
// Everything it creates carries the `seed.tenantPrefix` so teardown can find it again by
// name alone, without this file having to have been the thing that made it.

import { loadProfile, parseArgs, targets } from '../lib/config.mjs';
import { login, inspect } from '../lib/auth.mjs';
import { AdminApi, HttpError } from '../lib/api.mjs';
import { writeFixtures, fixturesPath } from '../lib/fixtures.mjs';
import { png, siteBundle } from './assets.mjs';
import { modeASource } from './sites.mjs';
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';

const log = (...args) => console.log('[seed]', ...args);

async function main() {
  const { env, overrides } = parseArgs(process.argv.slice(2));
  const profile = await loadProfile(env, { overrides });
  const t = targets(profile);

  log(`profile '${profile.name}' (mode ${t.mode}) -> ${t.admin}`);

  const tokens = await login({
    identity: t.identity,
    redirectUri: t.spaRedirectUri,
    clientId: profile.auth.clientId,
    scope: profile.auth.scope,
    username: profile.auth.username,
    password: profile.auth.password,
  });
  const claims = inspect(tokens.accessToken);
  log(`signed in as ${profile.auth.username}; token aud=${JSON.stringify(claims?.aud)} exp=${new Date(tokens.expiresAt).toISOString()}`);

  const api = new AdminApi({ base: t.admin, token: tokens.accessToken });

  // Provisioning a tenant is SuperAdmin-only, and so is listing them. Failing here with a
  // clear message beats failing later with a 403 on an endpoint that looks unrelated.
  let existing;
  try {
    existing = await api.get('/api/admin/tenants');
  } catch (error) {
    if (error instanceof HttpError && (error.status === 403 || error.status === 401)) {
      throw new Error(`${profile.auth.username} is not a platform SuperAdmin (GET /api/admin/tenants -> ${error.status}). `
        + `Seeding provisions tenants, which only a SuperAdmin may do.`);
    }
    throw error;
  }
  const bySlug = new Map(existing.map((x) => [x.slug, x]));

  const { tenantPrefix, tenants: tenantCount, contentPerTenant, mediaPerTenant } = profile.seed;
  const fixtures = { tenants: [] };

  for (let i = 1; i <= tenantCount; i++) {
    const slug = `${tenantPrefix}-${String(i).padStart(2, '0')}`;
    const tenant = await ensureTenant(api, bySlug, slug);
    log(`tenant ${slug} (${tenant.tenantId})`);

    // The slug, not the uuid: TenantStore.GetByIdentifierAsync matches Tenant.Identifier
    // (src/Shared/Dcms.Shared.Data/Tenancy/TenantStore.cs:13). A uuid here resolves to no
    // tenant and the request runs with no tenant context.
    const scoped = api.forTenant(tenant.slug);
    const instance = await ensureBlogInstance(scoped, slug);
    const items = await ensureContent(scoped, instance, contentPerTenant, slug);
    const media = await ensureMedia(scoped, mediaPerTenant, slug);
    const site = await ensureSite(scoped, slug);

    // The git-backed modes. Mode A is cheap (pure C# string assembly) so it is always
    // seeded; Mode B runs a sandboxed package install per build and is opt-in.
    const sitesByMode = { StaticFiles: site };
    sitesByMode.StaticPrerender = await ensureGitSite(scoped, slug, 'StaticPrerender', profile.seed);
    if (profile.seed.modeB) {
      sitesByMode.ReactApp = await ensureGitSite(scoped, slug, 'ReactApp', profile.seed);
    }

    fixtures.tenants.push({
      slug,
      tenantId: tenant.tenantId,
      instanceSlug: instance.slug,
      // The id as well as the slug: delivery addresses an instance by slug, but the admin
      // content list requires instanceId as a query parameter.
      instanceId: instance.id,
      contentType: 'post',
      itemSlugs: items,
      mediaIds: media,
      site,
      // Keyed by the render mode's own name so a scenario can say which pipeline it means
      // rather than which fixture it happens to have been given.
      sitesByMode,
    });
  }

  await writeUploadAssets(profile.name);

  const path = await writeFixtures(profile.name, fixtures);
  log(`wrote ${path}`);
  log(`estate: ${fixtures.tenants.length} tenants, `
    + `${fixtures.tenants.reduce((n, x) => n + x.itemSlugs.length, 0)} published items, `
    + `${fixtures.tenants.reduce((n, x) => n + x.mediaIds.length, 0)} media assets, `
    + `${fixtures.tenants.filter((x) => x.site?.domain).length} live sites, `
    + `git-backed: ${modeSummary(fixtures.tenants)}`);
}

/**
 * Binary fixtures on disk for the media scenario to upload.
 *
 * k6 cannot generate a PNG at runtime -- it has no zlib -- but it can open() a file at
 * init. Written here rather than committed so the repo holds no binaries, and written in
 * three sizes because the webp ladder skips rungs above the source width: a 240px upload
 * and a 1920px one ask the worker for very different amounts of work, and the media
 * scenario needs to be able to tell those apart.
 */
async function writeUploadAssets(profileName) {
  const dir = join(dirname(fixturesPath(profileName)), 'assets');
  await mkdir(dir, { recursive: true });
  for (const [name, width] of [['small', 240], ['medium', 1280], ['large', 1920]]) {
    await writeFile(join(dir, `${name}.png`), png(width, Math.round(width * 0.66), width));
  }
  log(`wrote upload assets to ${dir}`);
}

async function ensureTenant(api, bySlug, slug) {
  const found = bySlug.get(slug);
  if (found) return found;
  return api.post('/api/admin/tenants', { slug, name: `Load test ${slug}` });
}

async function ensureBlogInstance(api, slug) {
  const instances = await api.get('/api/admin/plugins/instances');
  const found = instances.find((x) => x.slug === 'blog');
  if (found) return found;

  const created = await api.post('/api/admin/plugins/instances', {
    pluginId: 'blog',
    slug: 'blog',
    name: `Blog (${slug})`,
    // The instance description is embedded into the per-tenant OpenAPI document, so it is
    // also part of what the openapi scenario measures the assembly cost of.
    description: 'Load-test fixture blog.',
    config: JSON.stringify({ title: `Load test ${slug}`, postsPerPage: 10 }),
  });
  await api.post(`/api/admin/plugins/instances/${created.id}/enable`);
  return { ...created, slug: 'blog' };
}

async function ensureContent(api, instance, count, tenantSlug) {
  // Only published items are visible to delivery, so the count that matters is the
  // published one. An existing item is left alone rather than republished — republishing
  // would bump the cache generation and quietly warm nothing.
  // instanceId is a required (non-nullable) query parameter on this endpoint, not an
  // optional filter -- omitting it is a 400, not an unfiltered list.
  const existing = await api.get(`/api/admin/content?instanceId=${instance.id}&contentType=post`);
  const have = new Set((existing.items ?? existing).map((x) => x.slug));

  const slugs = [];
  for (let i = 1; i <= count; i++) {
    const itemSlug = `post-${String(i).padStart(4, '0')}`;
    slugs.push(itemSlug);
    if (have.has(itemSlug)) continue;

    const created = await api.post('/api/admin/content', {
      pluginInstanceId: instance.id,
      contentType: 'post',
      slug: itemSlug,
      data: {
        title: `Post ${i} for ${tenantSlug}`,
        excerpt: `Excerpt for post ${i}.`,
        body: `<p>${'Body paragraph. '.repeat(40)}</p>`,
        tags: [`tag-${i % 12}`, 'loadtest'],
      },
    });
    await api.post(`/api/admin/content/${created.id}/publish`, {});
    if (i % 50 === 0) log(`  ${tenantSlug}: published ${i}/${count}`);
  }
  return slugs;
}

async function ensureMedia(api, count, tenantSlug) {
  const existing = await api.get('/api/admin/media');
  const have = new Map((existing.items ?? existing).map((x) => [x.fileName ?? x.name, x.id]));

  const ids = [];
  for (let i = 1; i <= count; i++) {
    const name = `loadtest-${String(i).padStart(3, '0')}.png`;
    if (have.has(name)) { ids.push(have.get(name)); continue; }

    // Sizes spread across the webp ladder's rungs so the worker does a different amount
    // of work per asset: below 320 skips every rung, above 1280 generates the full set.
    const width = [240, 640, 1280, 1920][i % 4];
    const body = new FormData();
    body.append('file', new Blob([png(width, Math.round(width * 0.66), i)], { type: 'image/png' }), name);
    const created = await api.post('/api/admin/media', body);
    ids.push(created.id);
    if (i % 10 === 0) log(`  ${tenantSlug}: uploaded ${i}/${count}`);
  }
  return ids;
}

/**
 * A Mode C (StaticFiles) site: uploaded pre-built files, published, served by site-host.
 *
 * Mode C on purpose. Modes A and B publish through git — a Forgejo repo, a release branch
 * and, for Mode B, a sandboxed container build — which is a different subsystem with its
 * own scenario. Seeding through it would make every site-host measurement depend on the
 * build pipeline being healthy, and would put a multi-minute build in the middle of a
 * seed. Mode C reaches the same place (artifacts in the sites bucket, a succeeded build,
 * a resolvable domain) through the shortest path that is still the real one.
 */
async function ensureSite(api, tenantSlug) {
  const sites = await api.get('/api/admin/sites');
  const name = `loadtest-site-${tenantSlug}`;
  let site = (sites.items ?? sites).find((s) => s.name === name);

  if (!site) {
    site = await api.post('/api/admin/sites', { name, renderMode: 'StaticFiles' });
  }
  const siteId = site.id;

  const domains = await api.get('/api/admin/domains');
  let domain = (domains.items ?? domains).find((d) => d.siteId === siteId);
  if (!domain) {
    // The managed zone is platform-owned (wildcard DNS + on-demand TLS) so this is
    // verified on creation. The alternative -- POST /api/admin/domains -- issues a TXT
    // challenge that Domains:AutoVerify=false would never let us satisfy without
    // touching real DNS.
    domain = await api.post('/api/admin/domains/provisioned', {});
    await api.post(`/api/admin/domains/${domain.id}/site`, { siteId });
  }

  const builds = await api.get(`/api/admin/sites/${siteId}/builds?limit=5`);
  const live = (builds.items ?? builds).find((b) => b.status === 'Succeeded');
  if (live) {
    return { siteId, domain: domain.hostname, buildId: live.id, pages: 8 };
  }

  const bundle = siteBundle();
  const body = new FormData();
  for (const [path, bytes] of bundle) {
    body.append('file', new Blob([bytes]), path);
  }
  await api.post(`/api/admin/sites/${siteId}/upload`, body);

  const { buildId } = await api.post(`/api/admin/sites/${siteId}/publish`, {}, { expect: [200, 202] });
  const built = await waitForBuild(api, siteId, buildId);
  return { siteId, domain: domain.hostname, buildId, pages: 8, buildSeconds: built.seconds };
}

/**
 * A git-backed fixture site: Mode A (StaticPrerender) or Mode B (ReactApp).
 *
 * These publish through Forgejo rather than through an upload, so seeding one is four
 * steps rather than two: write the source into the draft, provision the repo (which
 * commits that source and creates the `release` branch), then build the release head.
 *
 * `?branch=release` matters. Publishing from the default branch MERGES it into release and
 * returns no build id — the build is then triggered by Forgejo's push webhook, so a seeder
 * that took that path would have to guess which build was its own and would fail wherever
 * the webhook could not reach admin-api. Publishing while already on release skips the
 * merge and enqueues the build directly (SiteEndpoints.cs, the `sourceBranch == release`
 * arm), which is the same code path the scenario uses and returns the id to wait on.
 *
 * Idempotent like everything else here: an existing site with a succeeded build is adopted
 * as-is. A Mode B build costs minutes, and re-running the seeder must not spend them again.
 */
async function ensureGitSite(api, tenantSlug, renderMode, seed) {
  const label = renderMode === 'ReactApp' ? 'b' : 'a';
  const name = `loadtest-site-${label}-${tenantSlug}`;

  const sites = await api.get('/api/admin/sites');
  let site = (sites.items ?? sites).find((s) => s.name === name);
  if (!site) {
    site = await api.post('/api/admin/sites', { name, renderMode });
  }
  const siteId = site.id;

  const existingBuilds = await api.get(`/api/admin/sites/${siteId}/builds?limit=5`);
  const live = (existingBuilds.items ?? existingBuilds).find((b) => b.status === 'Succeeded');
  if (live) {
    const current = await api.get(`/api/admin/sites/${siteId}`);
    log(`  ${tenantSlug}: ${renderMode} site already built (${Object.keys(current.definition?.files ?? {}).length} files)`);
    return { siteId, renderMode, buildId: live.id, fileCount: Object.keys(current.definition?.files ?? {}).length };
  }

  // Mode B's source is fetched from the platform, not written here: `starter-files` returns
  // the scaffold the IDE seeds a new React app with, generated against THIS tenant's
  // OpenAPI. Anything else would be measuring a project no tenant actually has.
  const source = renderMode === 'ReactApp'
    ? (await api.get(`/api/admin/sites/${siteId}/starter-files?flavor=starter`)).files
    : modeASource(name, seed.modeAPages ?? 12).files;

  const current = await api.get(`/api/admin/sites/${siteId}`);
  if (Object.keys(current.definition?.files ?? {}).length === 0) {
    await api.put(`/api/admin/sites/${siteId}/definition`, { files: source });
  }

  // Commits the draft as the repo's initial history and creates `release` off it.
  const repo = await api.post(`/api/admin/sites/${siteId}/git/provision`, {});

  const { buildId } = await api.post(
    `/api/admin/sites/${siteId}/publish?branch=release`, {}, { expect: [200, 202] });
  const built = await waitForBuild(api, siteId, buildId, renderMode === 'ReactApp' ? 900_000 : 300_000);
  log(`  ${tenantSlug}: ${renderMode} site built in ${built.seconds.toFixed(1)}s (${repo.repo})`);

  return {
    siteId,
    renderMode,
    repo: repo.repo,
    buildId,
    fileCount: Object.keys(source).length,
    buildSeconds: built.seconds,
  };
}

function modeSummary(tenants) {
  const counts = new Map();
  for (const t of tenants) {
    for (const mode of Object.keys(t.sitesByMode ?? {})) {
      counts.set(mode, (counts.get(mode) ?? 0) + 1);
    }
  }
  return [...counts].map(([mode, n]) => `${n}x ${mode}`).join(', ') || 'none';
}

async function waitForBuild(api, siteId, buildId, timeoutMs = 180_000) {
  const started = Date.now();
  for (;;) {
    const builds = await api.get(`/api/admin/sites/${siteId}/builds?limit=10`);
    const build = (builds.items ?? builds).find((b) => b.id === buildId);
    if (build?.status === 'Succeeded') return { seconds: (Date.now() - started) / 1000 };
    if (build?.status === 'Failed') {
      // The build log is stored for Mode B (the IDE shows it), so point at it rather than
      // leaving "it failed" as the whole report -- a failing pnpm install is the single
      // most likely thing to go wrong here and its reason is in that log.
      const log = await api.get(`/api/admin/sites/${siteId}/builds/${buildId}/log`, { raw: true })
        .catch(() => null);
      throw new Error(`Site build ${buildId} failed.`
        + (log ? ` Build log: /api/admin/sites/${siteId}/builds/${buildId}/log` : '')
        + ` A Mode C build only extracts an uploaded bundle and a Mode A build is pure string`
        + ` assembly, so a failure in either is infrastructure. A Mode B failure is usually the`
        + ` sandbox: check that ${'$DCMS_BUILD_SANDBOX_IMAGE'} exists on the host.`);
    }
    if (Date.now() - started > timeoutMs) {
      throw new Error(`Site build ${buildId} still '${build?.status ?? 'unknown'}' after ${timeoutMs / 1000}s. `
        + `Is site-builder consuming the SITES stream?`);
    }
    await new Promise((r) => setTimeout(r, 2000));
  }
}

main().catch((error) => {
  console.error('[seed] FAILED:', error.message);
  if (error.cause) console.error('[seed] cause:', error.cause.message ?? error.cause);
  if (process.env.DCMS_LOADTEST_DEBUG) console.error(error);
  process.exit(1);
});
