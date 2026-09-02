#!/usr/bin/env node
// Prints one access token for the authenticated scenarios to use.
//
//   ACCESS_TOKEN=$(node loadtest/lib/mint-token.mjs --env vps1)
//
// Called immediately before k6 starts, not at seed time, because Identity issues access
// tokens with a TEN MINUTE lifetime (src/Services/Dcms.Identity/Program.cs:113). A token
// minted during a seeding run that took four minutes would expire in the middle of the
// measurement, and the resulting burst of 401s would be charged to whichever endpoint
// happened to be called at the time.
//
// That lifetime is also why run.sh refuses authenticated runs longer than eight minutes.
// Refreshing mid-run is possible but not free to do correctly: OpenIddict rotates refresh
// tokens by default, so concurrent VUs sharing one would race and invalidate each other.
// The fix, when a soak run needs one, is a single-writer token daemon that holds the
// refresh chain and hands out the current access token -- deliberately not built until a
// run actually needs to outlive one token.

import { loadProfile, parseArgs, targets } from './config.mjs';
import { login, inspect } from './auth.mjs';

const { env, overrides } = parseArgs(process.argv.slice(2));
const profile = await loadProfile(env, { overrides });
const t = targets(profile);

let tokens;
try {
  tokens = await login({
    identity: t.identity,
    redirectUri: t.spaRedirectUri,
    clientId: profile.auth.clientId,
    scope: profile.auth.scope,
    username: profile.auth.username,
    password: profile.auth.password,
  });
} catch (error) {
  // A stack trace here is noise: the caller is a shell script that prints its own
  // diagnosis, and every realistic failure is a wrong credential or an unreachable host.
  // 'fetch failed' on its own says nothing; naming the host it could not reach does.
  console.error(`[mint-token] ${error.message} (identity: ${t.identity})`);
  if (process.env.DCMS_LOADTEST_DEBUG) console.error(error);
  process.exit(1);
}

if (process.argv.includes('--inspect')) {
  const claims = inspect(tokens.accessToken);
  console.error(`aud=${JSON.stringify(claims?.aud)} sub=${claims?.sub} `
    + `exp=${new Date((claims?.exp ?? 0) * 1000).toISOString()}`);
}

process.stdout.write(tokens.accessToken);
