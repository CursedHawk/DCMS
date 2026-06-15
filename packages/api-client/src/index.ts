/**
 * Typed API client. Generated from the admin-api and content-api OpenAPI
 * documents via openapi-typescript once those specs exist (Phase 7). Until
 * then this exposes a minimal fetch wrapper used by the admin SPA.
 */
export interface ApiClientOptions {
  baseUrl: string;
  getAccessToken?: () => string | undefined;
}

export function createApiClient({ baseUrl, getAccessToken }: ApiClientOptions) {
  return {
    async get<T>(path: string): Promise<T> {
      const token = getAccessToken?.();
      const response = await fetch(`${baseUrl}${path}`, {
        headers: token ? { Authorization: `Bearer ${token}` } : undefined,
      });
      if (!response.ok) {
        throw new Error(`GET ${path} failed: ${response.status}`);
      }
      return (await response.json()) as T;
    },
  };
}
