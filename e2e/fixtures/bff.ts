import type { Page } from '@playwright/test';

/**
 * Puts the admin console into BFF mode (ADR 0014) for one page, and stands in for the edge.
 *
 * <p><b>Why the index.html is rewritten.</b> The mode is a runtime value — the container's
 * entrypoint substitutes `__DCMS_AUTH_MODE__` into the served document. Under `vite dev`
 * nothing substitutes it, so the placeholder survives and `runtime-config.ts` correctly falls
 * back to `bearer`. An `addInitScript` cannot help: the document's own inline script assigns
 * `window.__DCMS_CONFIG__` afterwards and would overwrite it. So the substitution is done the
 * same way the entrypoint does it, on the document as it is served.</p>
 *
 * <p>This is the one place the BFF path can be exercised end to end without a real edge, and it
 * is worth having precisely because the absence it asserts — no `Authorization` header — is
 * invisible in every other suite.</p>
 */

/** The session the fake edge reports at `/.edge/me`. */
export interface EdgeSession {
  sub: string;
  name: string;
  email: string;
}

export const BFF_OPERATOR: EdgeSession = {
  sub: '11111111-1111-1111-1111-111111111111',
  name: 'Ada Lovelace',
  email: 'ada@example.test',
};

/** The CSRF token the fake edge mints. Opaque to the console, which only echoes it. */
export const CSRF_TOKEN = 'e2e-csrf-token';

export interface EdgeOptions {
  /** Null stands in for a 401: no session, so the console renders its sign-in screen. */
  session?: EdgeSession | null;
  /** Off for the case where the token has not been issued yet. */
  issueCsrf?: boolean;
  /** The workspace the console sends as `X-Dcms-Tenant`. */
  tenantSlug?: string;
  /** Off for the one spec that is about the storage notice itself. */
  dismissStorageNotice?: boolean;
}

/**
 * Serves the console in BFF mode behind a stand-in for the edge's own endpoints.
 *
 * <p>Call before `page.goto`. Navigation redirects (`/.edge/signin`, `/.edge/signout`) are
 * answered with a page rather than left to fail, so a spec asserting on the redirect sees the
 * request instead of a connection error.</p>
 */
export async function useBffMode(page: Page, options: EdgeOptions = {}): Promise<void> {
  const session = options.session === undefined ? BFF_OPERATOR : options.session;
  const issueCsrf = options.issueCsrf ?? true;
  const tenantSlug = options.tenantSlug ?? 'acme';
  const dismissNotice = options.dismissStorageNotice ?? true;

  // Tenant selection stays in localStorage — it names a workspace rather than carrying a
  // credential — so it is seeded here. Written only when nothing has chosen one: an init script
  // re-runs on every navigation, and the workspace switcher reloads the page deliberately, so
  // writing unconditionally would put the original workspace back and undo the switch.
  await page.addInitScript(([slug, dismiss]) => {
    if (slug && !window.localStorage.getItem('dcms.tenant')) {
      window.localStorage.setItem('dcms.tenant', slug as string);
    }
    if (dismiss) window.localStorage.setItem('dcms.storage-notice', '1');
  }, [tenantSlug, dismissNotice ? '1' : ''] as const);

  // The entrypoint's substitution, done here.
  //
  // Every document, not just `/`: the SPA's routes are served the same index.html by the dev
  // server's history fallback, so a spec that opens /notifications directly would otherwise get
  // an unsubstituted placeholder — and fall back to bearer mode, which renders the sign-in
  // screen and looks like the session was refused.
  //
  // Registered before the /.edge/* handlers below, because Playwright runs the most recently
  // registered matching handler first: those are navigations too, and they answer for
  // themselves. Anything that is not a document is handed back to the API fixture.
  await page.route('**/*', async (route, request) => {
    if (request.resourceType() !== 'document') {
      await route.fallback();
      return;
    }
    const response = await route.fetch();
    const html = (await response.text()).replace('__DCMS_AUTH_MODE__', 'bff');
    await route.fulfill({
      response,
      body: html,
      // Dropped rather than recomputed: the substitution changed the length, and a stale one
      // truncates the document.
      headers: Object.fromEntries(
        Object.entries(response.headers()).filter(([name]) => name.toLowerCase() !== 'content-length'),
      ),
    });
  });

  await page.route('**/.edge/me', async (route) => {
    if (!session) {
      await route.fulfill({ status: 401, contentType: 'application/json', body: '{}' });
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      // Not Secure/HttpOnly here: the real edge sets both, and neither is what this suite is
      // about. The console has to be able to READ it, which is the property that matters.
      headers: issueCsrf ? { 'set-cookie': `dcms.csrf=${CSRF_TOKEN}; Path=/; SameSite=Lax` } : {},
      body: JSON.stringify(session),
    });
  });

  for (const path of ['**/.edge/signin*', '**/.edge/signout*']) {
    await page.route(path, (route) =>
      route.fulfill({ status: 200, contentType: 'text/html', body: '<title>edge</title>' }),
    );
  }
}
