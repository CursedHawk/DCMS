#!/usr/bin/env node
// Removes the fixture estate.
//
//   node loadtest/seed/teardown.mjs --env vps1
//   node loadtest/seed/teardown.mjs --env vps1 -e seed.tenantPrefix=loadtest
//
// Finds tenants by slug prefix rather than by reading fixtures.json, so it can clean up
// after a provisioning run that died before it wrote one — which is precisely the run
// that leaves a mess. DELETE /api/admin/tenant purges the tenant across every schema and
// takes its sites, git repos, build artifacts and stored objects with it, so one call per
// tenant is the whole job.
//
// Idempotent: running it twice is a no-op, and running it against a platform that never
// had fixtures reports zero rather than failing. Teardown that errors when there is
// nothing to tear down cannot be put in a trap handler, which is where this belongs.

import { loadProfile, parseArgs, targets } from '../lib/config.mjs';
import { login } from '../lib/auth.mjs';
import { AdminApi, HttpError } from '../lib/api.mjs';
import { fixturesPath } from '../lib/fixtures.mjs';
import { rm } from 'node:fs/promises';

const log = (...args) => console.log('[teardown]', ...args);

async function main() {
  const { env, overrides } = parseArgs(process.argv.slice(2));
  const profile = await loadProfile(env, { overrides });
  const t = targets(profile);
  const prefix = profile.seed.tenantPrefix;

  // A prefix that matched everything would purge the platform. The seeder only ever
  // creates `<prefix>-NN`, so an empty or suspiciously short prefix is a configuration
  // mistake, and the cost of being wrong here is unrecoverable.
  if (!prefix || prefix.length < 4) {
    throw new Error(`Refusing to run with tenantPrefix '${prefix}': too short to be safe. `
      + `Teardown deletes every tenant whose slug starts with it.`);
  }

  const tokens = await login({
    identity: t.identity,
    redirectUri: t.spaRedirectUri,
    clientId: profile.auth.clientId,
    scope: profile.auth.scope,
    username: profile.auth.username,
    password: profile.auth.password,
  });
  const api = new AdminApi({ base: t.admin, token: tokens.accessToken });

  const all = await api.get('/api/admin/tenants');
  const doomed = all.filter((x) => x.slug.startsWith(`${prefix}-`));

  if (doomed.length === 0) {
    log(`nothing to remove (no tenant slug starts with '${prefix}-')`);
  }

  let removed = 0;
  for (const tenant of doomed) {
    try {
      const result = await api.forTenant(tenant.slug).delete('/api/admin/tenant');
      removed++;
      log(`removed ${tenant.slug}`, result ? JSON.stringify(result) : '');
    } catch (error) {
      // One tenant failing must not strand the rest: the next run would then find a
      // partially-cleaned estate and adopt whatever survived.
      if (error instanceof HttpError && error.status === 404) {
        log(`${tenant.slug} was already gone`);
        continue;
      }
      console.error(`[teardown] could NOT remove ${tenant.slug}: ${error.message}`);
    }
  }

  await rm(fixturesPath(profile.name), { force: true });
  log(`done: ${removed}/${doomed.length} tenants removed, fixtures file cleared`);

  if (removed < doomed.length) {
    process.exitCode = 1;   // surfaced by run.sh, which must not report a clean run
  }
}

main().catch((error) => {
  console.error('[teardown] FAILED:', error.message);
  if (process.env.DCMS_LOADTEST_DEBUG) console.error(error);
  process.exit(1);
});
