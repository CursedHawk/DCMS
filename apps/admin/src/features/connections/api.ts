/** An external API connection as the console sees it: everything but the key. */
export interface ApiConnection {
  slug: string;
  name: string;
  baseUrl: string;
  authKind: string;
  authName: string | null;
  hasSecret: boolean;
  operations: string[];
  refreshMinutes: number;
  refreshedAt: string | null;
  lastError: string | null;
  /** Per operation: where its list sits and what an item has — what the builder binds to. */
  shapes?: { operation: string; items: string | null; fields: string[]; fetchedAt: string }[];
}

export const CONNECTIONS_KEY = ['api-connections'] as const;
