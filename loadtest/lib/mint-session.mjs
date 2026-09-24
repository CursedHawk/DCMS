#!/usr/bin/env node
// Prints one edge session for the authenticated scenarios, as JSON {cookie, csrf, origin}.
//
//   ADMIN_SESSION=$(node loadtest/lib/mint-session.mjs --env vps1)
//
// A session, not a token: the admin plane is behind the edge BFF (ADR 0014), which strips a
// client's Authorization header and attaches its own. The edge refreshes the tokens behind
// the session as they expire, so one session serves a run of any length -- the old
// eight-minute ceiling on authenticated runs was the ten-minute token lifetime, and is gone.
// All VUs share it; the edge serialises the refresh per session, which is exactly what makes
// that safe.

import { loadProfile, parseArgs, targets } from './config.mjs';
import { edgeSession } from './auth.mjs';

const { env, overrides } = parseArgs(process.argv.slice(2));
const profile = await loadProfile(env, { overrides });
const t = targets(profile);

try {
  const session = await edgeSession({
    admin: t.admin,
    username: profile.auth.username,
    password: profile.auth.password,
  });
  process.stdout.write(JSON.stringify(session));
} catch (error) {
  // A stack trace here is noise: the caller is a shell script that prints its own
  // diagnosis, and every realistic failure is a wrong credential or an unreachable host.
  console.error(`[mint-session] ${error.message} (admin: ${t.admin})`);
  if (process.env.DCMS_LOADTEST_DEBUG) console.error(error);
  process.exit(1);
}
