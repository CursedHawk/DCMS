/**
 * The vocabulary the server uses to say "this class of data changed".
 *
 * Mirrors `ResourceTags` in admin-api. The two lists are duplicated across the boundary on
 * purpose: a tag either side does not recognise degrades to "nothing refetches", which is the
 * right failure for a console that may be older or newer than the server it is talking to.
 * The alternative — a generated contract — would make a routine SPA deploy able to break on a
 * server it had not been rebuilt against.
 */
export const RESOURCE_TAGS = [
  'content',
  'media',
  'sites',
  'builds',
  'plugins',
  'domains',
  'members',
  'invitations',
  'forms',
  'chat',
  'analytics',
] as const;

export type ResourceTag = (typeof RESOURCE_TAGS)[number];

/** What the server pushes on the `ResourceChanged` hub method. */
export interface ResourceChange {
  tag: string;
  /** The row that changed, where the producer knew it. Consoles may ignore it. */
  id?: string | null;
}

export function isResourceTag(value: string): value is ResourceTag {
  return (RESOURCE_TAGS as readonly string[]).includes(value);
}
