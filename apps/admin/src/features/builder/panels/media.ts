import { fetchObjectUrl, mediaContentPath } from '../../../lib/api';

/**
 * Media in the canvas.
 *
 * A page stores the *published* URL for an asset — `/api/media/<id>/original`,
 * which the site host proxies to the delivery API. That is the right thing to
 * commit: the markup in the repo is the published artifact, and an editor-only
 * reference would have to be rewritten at build time.
 *
 * It is also a URL that does not resolve in the admin, where media lives behind
 * `/api/admin/media/<id>/content` and needs a bearer token. So every image an
 * author placed rendered as a broken icon in the builder and correctly on the
 * live site — the single most visible way the canvas lied about the page.
 *
 * `<img>` cannot carry an Authorization header, so the fix is the same one the
 * media library already uses: fetch the bytes and hand back a `blob:` URL. This
 * module owns the cache; `MediaBridge` applies the result to the canvas DOM.
 */

/** `/api/media/<guid>/original` — the form the builder writes and publishes. */
const MEDIA_URL = /^\/api\/media\/([0-9a-f-]{36})\/([a-z0-9_-]+)$/i;

/**
 * One in-flight fetch per asset, shared by every element that references it.
 *
 * A page with a gallery of the same asset would otherwise fetch it once per
 * `<img>`, and the object URLs are deliberately never revoked: the canvas
 * re-renders from the model constantly, and a revoked URL is a blank image that
 * only reappears on the next full reload. They are a page's worth of blobs and
 * they die with the tab.
 */
const pending = new Map<string, Promise<string>>();
const settled = new Map<string, string>();

/** The 1280px WebP: enough for a canvas at any zoom, a fraction of the bytes. */
const CANVAS_VARIANT = 'webp-1280';

/** The asset id a published media URL refers to, or null. */
export function assetIdOf(url: string): string | null {
  return MEDIA_URL.exec(url)?.[1] ?? null;
}

/**
 * The canvas URL for a published media URL, if it is already known.
 *
 * Synchronous because the renderers are: the preview and the template renderer
 * build a string of markup in one pass, and an async hook there would mean
 * painting every image twice. The first paint of an unseen asset therefore uses
 * the published URL, `resolveMedia` fetches it, and the next paint — which the
 * bridge schedules as soon as the fetch lands — has it.
 */
export function knownMediaUrl(url: string): string {
  const id = assetIdOf(url);
  if (!id) return url;
  return settled.get(id) ?? url;
}

/**
 * Fetch an asset for the canvas and remember it. Anything that is not a
 * published media URL — an external image, a data URI, a committed file — is
 * returned untouched, because it already loads.
 */
export function resolveMedia(url: string): Promise<string> {
  const id = assetIdOf(url);
  if (!id) return Promise.resolve(url);

  const inFlight = pending.get(id);
  if (inFlight) return inFlight;

  // Falling back to the original covers an asset whose variants have not been
  // produced yet, which is every asset for its first few seconds.
  const request = fetchObjectUrl(mediaContentPath(id, CANVAS_VARIANT))
    .catch(() => fetchObjectUrl(mediaContentPath(id)))
    .catch(() => url)
    .then((resolved) => {
      settled.set(id, resolved);
      return resolved;
    });
  pending.set(id, request);
  return request;
}

/** Every published media URL in a string of markup. */
const MEDIA_URL_GLOBAL = /\/api\/media\/[0-9a-f-]{36}\/[a-z0-9_-]+/gi;

/**
 * Rewrite the media URLs in generated markup, and report which ones are not
 * ready yet.
 *
 * The preview renderers build a whole component's markup as one string, so they
 * cannot await per image. They rewrite what is known, fetch what is not, and
 * repaint — which is why this returns the pending list rather than a promise:
 * the caller decides whether one more paint is worth it.
 */
export function rewriteMediaUrls(html: string): { html: string; pending: string[] } {
  const pendingUrls: string[] = [];
  const rewritten = html.replace(MEDIA_URL_GLOBAL, (url) => {
    const resolved = knownMediaUrl(url);
    if (resolved === url) pendingUrls.push(url);
    return resolved;
  });
  return { html: rewritten, pending: pendingUrls };
}
