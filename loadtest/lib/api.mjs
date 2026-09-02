// A thin admin-api client for the seeder. Not used by the scenarios — k6 has its own
// http client and its own error accounting; this exists so provision/teardown can talk
// to the platform without either reimplementing auth or pretending failures did not
// happen.

/**
 * The tenant header is a *request*, not a claim: TenantStore resolves it by identifier
 * and TenantMembershipMiddleware 403s a caller who names a tenant they are not a member
 * of. A SuperAdmin passes, which is why seeding runs as one.
 */
export class AdminApi {
  #base; #token; #tenant;

  constructor({ base, token, tenant = null }) {
    this.#base = base.replace(/\/$/, '');
    this.#token = token;
    this.#tenant = tenant;
  }

  /**
   * A copy of this client scoped to one tenant.
   * @param identifier the tenant SLUG -- TenantStore resolves the header via
   *   GetByIdentifierAsync against Tenant.Identifier, so a uuid silently resolves to
   *   nothing and every tenant-scoped endpoint then behaves as if none was selected.
   */
  forTenant(identifier) {
    return new AdminApi({ base: this.#base, token: this.#token, tenant: identifier });
  }

  async request(method, path, { body, headers = {}, raw = false, expect } = {}) {
    const init = {
      method,
      headers: {
        authorization: `Bearer ${this.#token}`,
        ...(this.#tenant ? { 'X-Dcms-Tenant': this.#tenant } : {}),
        ...headers,
      },
    };
    if (body instanceof FormData) {
      init.body = body;                       // fetch sets the multipart boundary itself
    } else if (body !== undefined) {
      init.headers['content-type'] = 'application/json';
      init.body = JSON.stringify(body);
    }

    const response = await fetch(`${this.#base}${path}`, init);
    const accepted = expect ?? [200, 201, 202, 204];
    if (!accepted.includes(response.status)) {
      const text = await response.text().catch(() => '');
      throw new HttpError(method, path, response.status, text);
    }
    if (raw || response.status === 204) return null;
    const text = await response.text();
    return text ? JSON.parse(text) : null;
  }

  get(path, options) { return this.request('GET', path, options); }
  post(path, body, options) { return this.request('POST', path, { ...options, body }); }
  put(path, body, options) { return this.request('PUT', path, { ...options, body }); }
  delete(path, options) { return this.request('DELETE', path, options); }
}

export class HttpError extends Error {
  constructor(method, path, status, body) {
    super(`${method} ${path} -> ${status}${body ? `: ${body.slice(0, 300)}` : ''}`);
    this.name = 'HttpError';
    this.status = status;
    this.path = path;
  }
}
