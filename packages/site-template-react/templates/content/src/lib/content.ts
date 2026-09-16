import { collections, type CollectionInfo, type ContentItem } from '../api';
import { api } from './api';

/**
 * Reading content without knowing its fields in advance.
 *
 * The generator tells each collection which field is its title, summary, image, body and date
 * (`CollectionInfo`). These helpers read those roles, so a page written with them works for
 * whatever the tenant publishes. For a page about one specific collection, prefer the typed
 * accessor instead — `api.<instance>.<contentType>.list()` — see src/api/API.md.
 */

export type Item = ContentItem<Record<string, unknown>>;

export function findCollection(instance?: string, contentType?: string): CollectionInfo | undefined {
  return collections.find((c) => c.instance === instance && c.contentType === contentType);
}

export const collectionPath = (c: CollectionInfo) => `/${c.instance}/${c.contentType}`;
export const itemPath = (c: CollectionInfo, item: Item) => `${collectionPath(c)}/${encodeURIComponent(item.slug)}`;

function text(item: Item, field?: string): string | undefined {
  const value = field ? item.data[field] : undefined;
  return typeof value === 'string' && value.trim() !== '' ? value : undefined;
}

export const titleOf = (c: CollectionInfo, item: Item) => text(item, c.titleField) ?? item.slug;
export const summaryOf = (c: CollectionInfo, item: Item) => text(item, c.summaryField);
export const bodyOf = (c: CollectionInfo, item: Item) => text(item, c.bodyField);

/** The item's image as a URL. `width` picks the nearest variant the server renders. */
export function imageOf(c: CollectionInfo, item: Item, width: 320 | 640 | 960 | 1280 | 1920 = 640) {
  const value = c.imageField ? item.data[c.imageField] : undefined;
  const id = Array.isArray(value) ? value[0] : value;
  return typeof id === 'string' && id ? api.media.url(id, `webp-${width}`) : undefined;
}

export function dateOf(c: CollectionInfo, item: Item): Date | undefined {
  const value = text(item, c.dateField) ?? item.publishedAt;
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? undefined : date;
}

export function formatDate(date: Date): string {
  return date.toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
}

/** Fields no role covers, as label/value pairs — for showing "everything else" on a detail page. */
export function otherFields(c: CollectionInfo, item: Item): [string, string][] {
  const roles = new Set([c.titleField, c.summaryField, c.imageField, c.bodyField, c.dateField]);
  return Object.entries(item.data)
    .filter(([key, value]) => !roles.has(key) && (typeof value === 'string' || typeof value === 'number') && value !== '')
    .map(([key, value]) => [labelOf(key), String(value)]);
}

function labelOf(key: string): string {
  const spaced = key.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/[_-]+/g, ' ');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}
