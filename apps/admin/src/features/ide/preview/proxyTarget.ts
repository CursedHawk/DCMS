/**
 * Decides whether a preview-originated API request may be replayed by the parent with the admin
 * session, and returns the same-origin path to fetch — or null to refuse with a 403.
 *
 * <p>This is the confused-deputy guard for the preview API proxy. The request URL is fully
 * attacker-controlled: tenant- and CDN-authored code runs in the preview iframe, and a hostile
 * page can post a proxy-fetch envelope straight to the parent, past the injected shim's own
 * filter. So a raw <c>startsWith(prefix)</c> is a hole — <c>`${prefix}../../../tenants`</c> starts
 * with the prefix as a string, yet the browser collapses the <c>..</c> segments and would send
 * <c>/api/admin/tenants</c> carrying the admin bearer token.</p>
 *
 * <p>Resolving to a URL first collapses those segments, so the prefix test runs on the real path;
 * pinning the origin blocks an absolute or protocol-relative URL aimed at another host. The caller
 * fetches the returned path, never the raw input. Percent-encoded dot segments are deliberately
 * left intact (the browser does not collapse them either): they stay inside the preview subtree as
 * literal path characters and reach only content-api behind the tenant-scoped proxy, never a
 * different admin route.</p>
 *
 * <p>A bare <c>/api/...</c> outside <c>/api/admin/</c> is the site's own API as the live tenant
 * host serves it — site-runtime's <c>mediaUrl</c> writes <c>/api/media/{id}/{variant}</c> with no
 * base — so it is mapped into the preview subtree, which proxies to the same content-api.</p>
 */
export function previewProxyTarget(rawUrl: string, prefix: string, origin: string): string | null {
  let resolved: URL;
  try {
    resolved = new URL(rawUrl, origin);
  } catch {
    return null;
  }
  if (resolved.origin !== origin) return null;
  let path = resolved.pathname;
  if (path.startsWith('/api/') && !path.startsWith('/api/admin/')) path = `${prefix}${path.slice(1)}`;
  if (!path.startsWith(prefix)) return null;
  return `${path}${resolved.search}`;
}
