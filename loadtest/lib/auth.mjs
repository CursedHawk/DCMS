// Headless sign-in through the edge BFF (ADR 0014).
//
// The admin plane no longer accepts a bearer token from a client. Since BFF phase 5:
//   - the edge STRIPS any client-supplied Authorization on /api of the admin host, and
//     attaches the session's own token instead;
//   - dcms-admin-spa, the public client this harness used to mint tokens with, no longer
//     holds dcms.admin, so there is no token a client could mint for admin-api anyway.
// So the harness signs in exactly the way the console does and then carries the edge's
// session cookie. A side effect worth having: the edge refreshes the tokens behind that
// session itself, so an authenticated run is no longer capped at one 10-minute token.
//
//   1. GET  {admin}/.edge/signin          -> 302 to identity's /connect/authorize
//   2. that authorize request             -> 302 to /account/login (not signed in yet)
//   3. GET  /account/login                -> the form: antiforgery cookie + hidden token
//   4. POST /account/login                -> identity cookie, 302 back to /connect/authorize
//   5. /connect/authorize                 -> an auto-submitting form (response_mode=form_post)
//   6. POST that form to /.edge/signin-oidc -> the edge session cookie (dcms.edge)
//   7. GET  {admin}/.edge/me              -> the CSRF cookie (dcms.csrf) every write echoes
//
// Nothing here knows identity's hostname: every hop is whatever the previous one pointed at.

/** Cookies per host. Node's fetch has none, and this flow crosses two hosts. */
class Jar {
  #byHost = new Map();

  store(url, response) {
    const host = new URL(url).host;
    const cookies = this.#byHost.get(host) ?? new Map();
    for (const line of response.headers.getSetCookie?.() ?? []) {
      const [pair, ...attrs] = line.split(';');
      const eq = pair.indexOf('=');
      if (eq <= 0) continue;
      const name = pair.slice(0, eq).trim();
      const value = pair.slice(eq + 1).trim();
      // A deletion is an expired Set-Cookie; keeping it would send a cookie the server
      // just told us to drop.
      const expired = attrs.some((a) => /^\s*expires=.*1970/i.test(a) || /^\s*max-age=0\b/i.test(a));
      if (expired || value === '') cookies.delete(name); else cookies.set(name, value);
    }
    this.#byHost.set(host, cookies);
  }

  header(url, filter = () => true) {
    const cookies = this.#byHost.get(new URL(url).host) ?? new Map();
    return [...cookies].filter(([k]) => filter(k)).map(([k, v]) => `${k}=${v}`).join('; ');
  }

  get(url, name) { return this.#byHost.get(new URL(url).host)?.get(name); }
}

const decode = (s) => s.replaceAll('&amp;', '&').replaceAll('&quot;', '"')
  .replaceAll('&#x2B;', '+').replaceAll('&#x2F;', '/').replaceAll('&#x3D;', '=')
  .replaceAll('&lt;', '<').replaceAll('&gt;', '>');

/** The first <form>'s action and its inputs. Both forms in this flow are server-rendered and flat. */
function parseForm(html, pageUrl) {
  const form = /<form[^>]*action="([^"]*)"[^>]*>([\s\S]*?)<\/form>/i.exec(html);
  if (!form) return null;
  const fields = {};
  for (const input of form[2].matchAll(/<input[^>]*>/gi)) {
    const name = /name="([^"]*)"/i.exec(input[0])?.[1];
    const value = /value="([^"]*)"/i.exec(input[0])?.[1] ?? '';
    if (name) fields[decode(name)] = decode(value);
  }
  return { action: new URL(decode(form[1]), pageUrl).toString(), fields };
}

async function send(jar, url, init = {}) {
  const response = await fetch(url, {
    ...init,
    redirect: 'manual',
    headers: { ...(init.headers ?? {}), cookie: jar.header(url) },
  });
  jar.store(url, response);
  return response;
}

/** Follow redirects by hand so every hop's cookies land in the jar. */
async function follow(jar, url, init) {
  let response = await send(jar, url, init);
  for (let hops = 0; hops < 10 && response.status >= 300 && response.status < 400; hops++) {
    url = new URL(response.headers.get('location'), url).toString();
    response = await send(jar, url);
  }
  return { url, response };
}

/**
 * Sign in through the edge and return what a load generator needs to act as the console.
 * @returns {Promise<{cookie:string, csrf:string, origin:string}>}
 */
export async function edgeSession({ admin, username, password }) {
  const jar = new Jar();
  admin = admin.replace(/\/$/, '');

  // 1-3. Land on identity's login form, wherever identity is.
  let { url, response } = await follow(jar, `${admin}/.edge/signin?returnUrl=%2F`);
  if (response.status === 404) {
    throw new Error(`${admin}/.edge/signin is 404: the edge has auth disabled (no Edge__Auth client secret).`);
  }
  let form = parseForm(await response.text(), url);
  if (!form || !('password' in form.fields)) {
    throw new Error(`Expected identity's login form after /.edge/signin, got ${response.status} at ${url}.`);
  }

  // 4-5. Sign in; identity sends us back to /connect/authorize, which answers with a form.
  ({ url, response } = await follow(jar, form.action, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams({ ...form.fields, email: username, password }),
  }));
  // A wrong password is a redirect back to the form carrying ?error=1. The form also POSTs
  // to /account/login, so a refused antiforgery token (400 at that same URL) must not be
  // mistaken for a wrong password.
  if (new URL(url).searchParams.get('error') === '1') {
    throw new Error(`Sign-in rejected for ${username} (identity returned to the login form). `
      + 'Check DCMS_LOADTEST_USER / DCMS_LOADTEST_PASSWORD, and that the account is not locked out.');
  }
  if (response.status >= 400) {
    throw new Error(`POST ${form.action} returned ${response.status} -- antiforgery token refused, or the form changed.`);
  }
  form = parseForm(await response.text(), url);
  if (!form?.fields.code) {
    throw new Error(`Expected the form_post back to the edge after sign-in, got ${response.status} at ${url}. `
      + `Error in URL, if any: ${new URL(url).searchParams.get('error') ?? 'none'}.`);
  }

  // 6. Hand the code to the edge, which sets its session cookie.
  ({ url, response } = await follow(jar, form.action, {
    method: 'POST',
    headers: { 'content-type': 'application/x-www-form-urlencoded' },
    body: new URLSearchParams(form.fields),
  }));
  if (url.includes('/.edge/denied')) {
    throw new Error(`The edge denied ${username} after sign-in (/.edge/denied) -- the account lacks the role the admin host requires.`);
  }

  // 7. The CSRF token. /.edge/me also proves the session row exists.
  const me = await send(jar, `${admin}/.edge/me`);
  if (me.status !== 200) throw new Error(`GET /.edge/me returned ${me.status} after sign-in.`);
  const csrf = jar.get(admin, 'dcms.csrf');
  if (!csrf) throw new Error('/.edge/me set no dcms.csrf cookie -- is the BFF enabled on this edge?');

  // Only the edge's own cookies: the OIDC correlation/nonce ones are spent, and dcms.csrf is
  // echoed as a header, not needed as a cookie.
  const cookie = jar.header(admin, (name) => name.startsWith('dcms.edge'));
  if (!cookie) throw new Error('Sign-in completed but the edge set no dcms.edge session cookie.');
  return { cookie, csrf: decodeURIComponent(csrf), origin: new URL(admin).origin };
}

/**
 * Headers that make a request look like the console's own. The edge checks, on every
 * non-GET: Origin equal to the admin host (or Sec-Fetch-Site: same-origin), and X-Dcms-Csrf
 * bound to this session. Sending both on reads too costs nothing and keeps one header set.
 */
export const sessionHeaders = ({ cookie, csrf, origin }) =>
  ({ cookie, 'X-Dcms-Csrf': csrf, origin });
