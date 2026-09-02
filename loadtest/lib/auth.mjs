// Headless sign-in against DCMS Identity.
//
// There is no shortcut available here and the alternatives are worse than they look.
// Identity allows only authorization_code + PKCE and client_credentials
// (src/Services/Dcms.Identity/Program.cs:95) — there is no password grant. The service
// client `dcms-admin-api` holds dcms.ai/dcms.social but NOT dcms.admin, and
// ServicePrincipalGuard refuses a client-credentials token everywhere it was not
// explicitly invited (src/Services/Dcms.AdminApi/Tenancy/ServicePrincipalGuard.cs), so
// minting a service token would get a load test a 403 on every admin endpoint.
//
// So this replays exactly what the browser does. Two things make that cheap:
// POST /account/login is DisableAntiforgery() (AccountEndpoints.cs:88), so no CSRF token
// has to be scraped; and dcms-admin-spa is ConsentTypes.Implicit (IdentitySeeder.cs:167),
// so /connect/authorize returns the code without an interactive consent screen.
//
//   1. POST /account/login       form-encoded email+password   -> identity cookie
//   2. GET  /connect/authorize   with that cookie, no redirect -> 302 carrying ?code=
//   3. POST /connect/token       code + verifier               -> access + refresh token

import { createHash, randomBytes } from 'node:crypto';

const base64url = (buf) => buf.toString('base64')
  .replaceAll('+', '-').replaceAll('/', '_').replaceAll('=', '');

function pkce() {
  const verifier = base64url(randomBytes(32));
  const challenge = base64url(createHash('sha256').update(verifier).digest());
  return { verifier, challenge };
}

/**
 * Node's fetch has no cookie jar, and we need exactly one cookie across two requests.
 * Collecting the name=value pairs by hand is less machinery than pulling in a jar
 * library for a single session.
 */
function collectCookies(response, jar = new Map()) {
  for (const line of response.headers.getSetCookie?.() ?? []) {
    const [pair] = line.split(';');
    const eq = pair.indexOf('=');
    if (eq > 0) jar.set(pair.slice(0, eq).trim(), pair.slice(eq + 1).trim());
  }
  return jar;
}

const cookieHeader = (jar) => [...jar].map(([k, v]) => `${k}=${v}`).join('; ');

/**
 * Sign in and exchange for tokens.
 * @returns {Promise<{accessToken:string, refreshToken:string, expiresAt:number}>}
 */
export async function login({ identity, redirectUri, clientId, scope, username, password }) {
  const { verifier, challenge } = pkce();

  const loginResponse = await fetch(`${identity}/account/login`, {
    method: 'POST',
    redirect: 'manual',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ email: username, password, returnUrl: '/' }),
  });

  // The handler redirects on both outcomes; failure is a redirect back to the form
  // carrying ?error=1. Reading only the status would treat a rejected password as a
  // successful sign-in and fail later with an unexplained 401 at /connect/authorize.
  const loginLocation = loginResponse.headers.get('location') ?? '';
  if (loginLocation.includes('/account/login')) {
    throw new Error(`Sign-in rejected for ${username} (identity redirected back to the login form). `
      + `Check DCMS_LOADTEST_USER / DCMS_LOADTEST_PASSWORD, and that the account is not locked out.`);
  }
  if (loginResponse.status >= 400) {
    throw new Error(`POST /account/login returned ${loginResponse.status}`);
  }

  const jar = collectCookies(loginResponse);
  if (jar.size === 0) throw new Error('Sign-in returned no cookie; cannot continue to /connect/authorize.');

  const authorizeUrl = `${identity}/connect/authorize?` + new URLSearchParams({
    client_id: clientId,
    response_type: 'code',
    redirect_uri: redirectUri,
    scope,
    code_challenge: challenge,
    code_challenge_method: 'S256',
    state: base64url(randomBytes(16)),
    nonce: base64url(randomBytes(16)),
  });

  const authorizeResponse = await fetch(authorizeUrl, {
    method: 'GET',
    redirect: 'manual',
    headers: { cookie: cookieHeader(jar) },
  });

  const location = authorizeResponse.headers.get('location');
  if (!location) {
    throw new Error(`/connect/authorize did not redirect (status ${authorizeResponse.status}). `
      + `Body: ${(await authorizeResponse.text()).slice(0, 400)}`);
  }
  // A bounce back to the login form means the cookie was not accepted — most often
  // because `identity` here is not the host that issued it.
  if (location.includes('/account/login')) {
    throw new Error('/connect/authorize bounced to the login form: the session cookie was not accepted. '
      + `Is '${identity}' the same host the cookie was issued for?`);
  }

  const redirected = new URL(location, redirectUri);
  const error = redirected.searchParams.get('error');
  if (error) {
    throw new Error(`/connect/authorize refused: ${error} — `
      + `${redirected.searchParams.get('error_description') ?? 'no description'}. `
      + `Is '${redirectUri}' registered for client '${clientId}'?`);
  }
  const code = redirected.searchParams.get('code');
  if (!code) throw new Error(`No authorization code in redirect: ${location}`);

  return exchange(identity, {
    grant_type: 'authorization_code',
    client_id: clientId,
    redirect_uri: redirectUri,
    code,
    code_verifier: verifier,
  });
}

/** Trade a refresh token for a fresh access token. Rotating: the response carries the next one. */
export async function refresh({ identity, clientId, refreshToken }) {
  return exchange(identity, {
    grant_type: 'refresh_token',
    client_id: clientId,
    refresh_token: refreshToken,
  });
}

async function exchange(identity, form) {
  const response = await fetch(`${identity}/connect/token`, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams(form),
  });
  const body = await response.text();
  if (!response.ok) {
    throw new Error(`POST /connect/token (${form.grant_type}) returned ${response.status}: ${body.slice(0, 400)}`);
  }
  const token = JSON.parse(body);
  return {
    accessToken: token.access_token,
    refreshToken: token.refresh_token,
    // Renew early rather than on expiry: a token that expires mid-flight shows up as a
    // burst of 401s attributed to whatever endpoint happened to be called.
    expiresAt: Date.now() + (token.expires_in ?? 600) * 1000,
  };
}

/** Decode a JWT payload without verifying it — for asserting `aud`/`exp` in diagnostics only. */
export function inspect(accessToken) {
  const [, payload] = accessToken.split('.');
  if (!payload) return null;
  return JSON.parse(Buffer.from(payload, 'base64url').toString('utf8'));
}
