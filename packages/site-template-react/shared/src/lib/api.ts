import { createTenantClient } from '../api';
import { config } from '../config';

/**
 * The site's one API client. Import `api` from here rather than creating another.
 *
 * `../api` is generated from the tenant's plugins — read `src/api/API.md` for every call it
 * offers. It is rewritten when those plugins change, so never edit it; this file is yours.
 */
export const api = createTenantClient({ baseUrl: config.apiBaseUrl });
