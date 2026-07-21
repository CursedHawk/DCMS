import { createTenantClient } from '../api';
import { config } from '../config';

/** The shared client instance for the app. */
export const api = createTenantClient({ baseUrl: config.apiBaseUrl });
