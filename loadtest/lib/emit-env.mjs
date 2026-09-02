#!/usr/bin/env node
// Prints a resolved profile as shell assignments, so run.sh does not need a JSON parser
// and there is exactly one implementation of profile resolution (variable expansion,
// mode selection, dotted overrides) rather than one per language.
//
//   eval "$(node loadtest/lib/emit-env.mjs --env vps1 -e load.vus=25)"
//
// Values are single-quoted with embedded quotes escaped, so a password or a URL with a
// shell metacharacter cannot become code when the caller evals this.

import { loadProfile, parseArgs, targets } from './config.mjs';

const quote = (v) => `'${String(v).replaceAll("'", `'\\''`)}'`;

const { env, overrides } = parseArgs(process.argv.slice(2));
const profile = await loadProfile(env, { overrides });
const t = targets(profile);

const out = {
  PROFILE: profile.name,
  MODE: t.mode,
  ADMIN_BASE: t.admin,
  IDENTITY_BASE: t.identity,
  CONTENT_BASE: t.content,
  SITEHOST_BASE: t.siteHost,
  VUS: profile.load.vus,
  DURATION: profile.load.duration,
  RAMP: profile.load.ramp,
  ERROR_RATE: profile.thresholds.errorRate,
  DELIVERY_P95: profile.thresholds.deliveryP95,
  SITEHOST_P95: profile.thresholds.siteHostP95,
  ADMIN_P95: profile.thresholds.adminP95,
  COMPOSE_FILES: profile.observability.composeFiles,
  PG_USER: profile.observability.postgresUser,
  PG_DB: profile.observability.postgresDb,
  SSH_TARGET: profile.ssh ?? '',
  DEPLOY_PATH: profile.observability.deployPath ?? '.',
  TENANT_PREFIX: profile.seed.tenantPrefix,
};

for (const [key, value] of Object.entries(out)) {
  console.log(`export ${key}=${quote(value)}`);
}
