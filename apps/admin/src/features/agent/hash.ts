/**
 * Content hashing for the agent's workspace.
 *
 * <p>This is <b>not</b> the server's hash. The draft API hashes with SHA-256 to decide whether a
 * save collides with what another tab already stored; those hashes arrive in `baseHashes` and
 * are about the <i>server's</i> copy. What the agent needs is a different question — "has this
 * file changed in this browser since I read it?" — between the moment it reads a file and the
 * moment it patches one, while the human may be typing into the same buffer.</p>
 *
 * <p>That makes a cryptographic hash the wrong tool twice over: it answers a question nobody is
 * asking here, and `crypto.subtle.digest` is asynchronous, which would push `await` into every
 * read of a store the editor touches on each keystroke. cyrb53 is synchronous, fast enough to
 * run over a whole project without being noticed, and has ~53 bits of output — collision odds
 * around one in nine quadrillion for a pair of file versions, against a consequence of "one
 * patch applies that should have been refused". Both sides of that trade are small; the
 * synchronous one is what makes the store usable.</p>
 */

/**
 * cyrb53 — a well-known non-cryptographic 53-bit string hash.
 *
 * <p>Returned as base-36 to keep it short in tool results the model pays for by the character.</p>
 */
export function hashContent(text: string): string {
  let h1 = 0xdeadbeef;
  let h2 = 0x41c6ce57;
  for (let i = 0; i < text.length; i++) {
    const ch = text.charCodeAt(i);
    h1 = Math.imul(h1 ^ ch, 2654435761);
    h2 = Math.imul(h2 ^ ch, 1597334677);
  }
  h1 = Math.imul(h1 ^ (h1 >>> 16), 2246822507) ^ Math.imul(h2 ^ (h2 >>> 13), 3266489909);
  h2 = Math.imul(h2 ^ (h2 >>> 16), 2246822507) ^ Math.imul(h1 ^ (h1 >>> 13), 3266489909);
  return (4294967296 * (2097151 & h2) + (h1 >>> 0)).toString(36);
}

/**
 * Memoised hashing, keyed on the string itself.
 *
 * <p>Hashing every file on every revision would be wasted work: a run reads a handful of files
 * and the project has hundreds, and a keystroke bumps the revision without touching any file but
 * one. A `WeakRef`-free `Map` keyed by content is safe because the VFS holds those exact strings
 * anyway — the cache adds a reference to something already retained, and is cleared wholesale
 * when it grows past the bound rather than evicting cleverly.</p>
 */
const cache = new Map<string, string>();

/** Bounded so a long session cannot grow it without limit; see {@link cachedHash}. */
const MAX_CACHE = 2000;

export function cachedHash(text: string): string {
  const hit = cache.get(text);
  if (hit !== undefined) return hit;
  const value = hashContent(text);
  // Cleared wholesale rather than LRU-evicted: the next few reads re-hash in microseconds, and
  // an eviction policy here would be more code than the thing it optimises.
  if (cache.size >= MAX_CACHE) cache.clear();
  cache.set(text, value);
  return value;
}
