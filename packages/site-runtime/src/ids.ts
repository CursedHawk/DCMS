/** A page or route id: kebab-case, so it is also a safe file name. */
export const DOC_ID = /^[a-z0-9][a-z0-9-]{0,63}$/;

/** A component instance id. Unique within its document; what selection, scroll-to and the AI tools address. */
export const NODE_ID = /^[A-Za-z0-9_-]{1,64}$/;

/** `namespace.name` — `dcms.heading` for the built-ins, `tenant.event-card` for a site's own. */
export const COMPONENT_TYPE = /^[a-z][a-z0-9-]*\.[a-z][a-z0-9-]*$/;

/** A slot, prop or token name: an identifier a person types and a JSON key. */
export const NAME = /^[a-zA-Z][a-zA-Z0-9_-]{0,63}$/;

/**
 * A same-site path: `/`, `/events`, `/events/summer?tab=2#top`.
 *
 * `//host` is refused because a browser reads it as another site, and a backslash because some
 * browsers normalise `/\host` to the same thing — both would turn "a link to one of our pages"
 * into an open redirect.
 */
export function isInternalPath(value: string): boolean {
  return /^\/(?![/\\])[^\s\\]*$/.test(value);
}

const EXTERNAL_PROTOCOLS = new Set(['http:', 'https:', 'mailto:', 'tel:']);

/**
 * A link that leaves the site. Allow-listed by protocol, so `javascript:`, `data:` and
 * `vbscript:` can never become an `href` — the canvas runs in the admin's origin.
 */
export function isSafeExternalHref(value: string): boolean {
  if (value.length > 2048) return false;
  try {
    return EXTERNAL_PROTOCOLS.has(new URL(value).protocol);
  } catch {
    return false;
  }
}
