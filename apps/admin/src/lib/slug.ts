import { useState } from 'react';
import { ApiError } from '@dcms/core';

// Unicode combining marks, written from a string so the range stays legible in source.
const COMBINING_MARKS = new RegExp('[\\u0300-\\u036f]', 'g');

/**
 * A URL-safe slug: lower-case ASCII letters, digits and single hyphens. Diacritics are folded
 * rather than dropped, so "Příspěvky" becomes "prispevky", not "p-sp-vky". Empty when nothing
 * usable is left — the caller's placeholder says what to type, not a made-up "page".
 */
export function slugify(input: string, max = 64): string {
  return input
    .normalize('NFKD')
    .replace(COMBINING_MARKS, '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .slice(0, max)
    .replace(/^-+|-+$/g, '');
}

const ID_ALPHABET = 'abcdefghijklmnopqrstuvwxyz0123456789';

/** Eight random slug-safe characters: what keeps two items both titled "News" apart. */
export function randomSlugId(): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(8)), (b) => ID_ALPHABET[b % 36]).join('');
}

/** A file name as slug material: "Hero shot_02.final.JPG" → "Hero shot_02.final". */
export function fileStem(fileName: string): string {
  return fileName.replace(/\.[a-z0-9]{1,8}$/i, '');
}

/**
 * A slug that follows `source` (a name, a title, a file name) until the user types their own.
 * Once they have, it is theirs — even emptied: retitling the item afterwards never overwrites
 * what they typed, and backspacing over it does not refill the field under their cursor.
 * Setting `null` hands it back to the source (a dialog reset for the next entry).
 */
export function useAutoSlug(source: string, max?: number): [string, (typed: string | null) => void] {
  const [typed, setTyped] = useState<string | null>(null);
  const slug = typed ?? slugify(source, max);
  return [slug, setTyped];
}

/**
 * The server refused a create because the slug is already used: content in the same collection,
 * a plugin instance, a tenant. Every one of those answers 409 with a message naming the slug.
 */
// ponytail: matches the server's English message; give the 409s a `code` if a second slug-less 409 says "slug".
export function isSlugTaken(error: unknown): boolean {
  return error instanceof ApiError && error.status === 409 && /slug/i.test(error.message);
}
