import { z } from 'zod';
import { pageStateSchema, whenSchema, type When } from './state';
import { actionSchema, type Action } from './actions';
import { sourceSchema, type Source } from './data';
import { COMPONENT_TYPE, DOC_ID, NAME, NODE_ID, isInternalPath, isSafeExternalHref } from './ids';

/**
 * The documents a Mode D site is made of (ADR 0020).
 *
 * Every object is strict: an unknown key is an error, not something silently dropped on the
 * next save. These files are written by people, by the canvas and by an AI, and a typo that
 * parses — `slot` for `slots` — is a page that quietly loses its content.
 *
 * Cross-document rules (a route naming a page that does not exist, a node whose props do not
 * match its component) need the registry and the whole file map; they belong to the site
 * validator, not to these schemas.
 */

/** Per-device overrides of a node's props. The props themselves are the desktop values. */
export interface Responsive {
  tablet?: Record<string, unknown>;
  mobile?: Record<string, unknown>;
}

/** One component instance. */
export interface Node {
  id: string;
  type: string;
  /** Pins a tenant component to one version of its definition. Absent for built-ins. */
  version?: number;
  props?: Record<string, unknown>;
  slots?: Record<string, Node[]>;
  responsive?: Responsive;
  action?: Action;
  /**
   * Props read from the item in scope (a collection's item, a detail page's item): prop name →
   * field path (`title`, `values.colour`, `#slug`). Absent outside a data context.
   */
  bind?: Record<string, string>;
  /**
   * Advanced: CSS declarations for this one instance (`letter-spacing: .1em; border: 0`), applied
   * to the component's own element. Declarations only — no selectors, at-rules or url()s.
   */
  css?: string;
  /** Shown only while the page's state matches (state.ts). Always shown on the canvas. */
  when?: When;
}

const propsSchema = z.record(z.string().regex(NAME), z.unknown());

/** `title`, `values.colour`, or a meta field: `#slug`, `#id`, `#publishedAt`, `#index`, `#number`, `#count`. */
/** `title`, `values.colour`, `photos.0` (an item of a list), or one of the item's own `#` fields. */
export const FIELD_PATH = /^(?:#(?:slug|id|publishedAt|index|number|count)|[A-Za-z_][A-Za-z0-9_]*(?:\.(?:[A-Za-z_][A-Za-z0-9_]*|\d{1,4}))*)$/;

/**
 * What an instance's CSS may not contain. Braces would end the rule it is scoped into and style
 * the rest of the page (or the admin's canvas); `<` could end the style element; at-rules and
 * escapes hide the rest; url() and friends fetch from anywhere. What is left is declarations.
 */
const UNSAFE_CSS = /[{}<>@\\]|url\s*\(|image-set\s*\(|expression\s*\(/i;

export const nodeCssSchema = z
  .string()
  .max(2000)
  .refine((css) => !UNSAFE_CSS.test(css), 'may hold declarations only: no { } < > @ \\, url(), image-set() or expression()');

export const nodeSchema: z.ZodType<Node> = z.strictObject({
  id: z.string().regex(NODE_ID, 'must be 1–64 letters, digits, - or _'),
  type: z.string().regex(COMPONENT_TYPE, 'must be namespace.name, like dcms.heading'),
  version: z.number().int().positive().optional(),
  props: propsSchema.optional(),
  get slots() {
    return z.record(z.string().regex(NAME), z.array(nodeSchema)).optional();
  },
  responsive: z
    .strictObject({ tablet: propsSchema.optional(), mobile: propsSchema.optional() })
    .optional(),
  action: actionSchema.optional(),
  bind: z.record(z.string().regex(NAME), z.string().regex(FIELD_PATH, 'must be a field path')).optional(),
  css: nodeCssSchema.optional(),
  when: whenSchema.optional(),
});

/** Every node in a tree, depth first, root included. */
export function* walk(node: Node): Generator<Node> {
  yield node;
  for (const children of Object.values(node.slots ?? {})) {
    for (const child of children) yield* walk(child);
  }
}

/** Ids are what selection and the AI tools address, so two nodes sharing one is ambiguity, not style. */
function uniqueNodeIds(root: Node, ctx: z.RefinementCtx, path: (string | number)[]) {
  const seen = new Set<string>();
  for (const node of walk(root)) {
    if (seen.has(node.id)) {
      ctx.addIssue({ code: 'custom', path, message: `node id “${node.id}” is used more than once` });
    }
    seen.add(node.id);
  }
}

export const seoSchema = z.strictObject({
  title: z.string().max(200).optional(),
  description: z.string().max(500).optional(),
  ogImage: z.string().max(2048).optional(),
  canonical: z.string().refine(isSafeExternalHref, 'must be an absolute URL').optional(),
  noIndex: z.boolean().optional(),
});

export const PAGE_SCHEMA_VERSION = 1;

/** `dcms/pages/<id>.json`. */
export const pageSchema = z
  .strictObject({
    schemaVersion: z.literal(PAGE_SCHEMA_VERSION),
    id: z.string().regex(DOC_ID, 'must be kebab-case'),
    title: z.string().min(1).max(120),
    seo: seoSchema.optional(),
    /**
     * A detail page: it shows one item of `source`, the one whose slug is the route parameter
     * `param` (`/events/:slug` → `slug`). Its nodes bind to that item like a collection's do.
     */
    data: z.strictObject({ source: sourceSchema, param: z.string().regex(/^[a-zA-Z][a-zA-Z0-9]*$/) }).optional(),
    /** Named values the page's actions set and its nodes' `when` reads, with their defaults. */
    state: pageStateSchema.optional(),
    root: nodeSchema,
  })
  .superRefine((page, ctx) => uniqueNodeIds(page.root, ctx, ['root']));

/**
 * `/`, `/events`, `/events/:slug`. Lower-case literal segments; `:name` captures one segment.
 * Nothing else — no wildcards, no optional segments — because every route has to be something
 * the publisher can name a file after and an author can read.
 */
const ROUTE_PATH = /^\/(?:(?:[a-z0-9][a-z0-9-]*|:[a-zA-Z][a-zA-Z0-9]*)(?:\/(?:[a-z0-9][a-z0-9-]*|:[a-zA-Z][a-zA-Z0-9]*))*)?$/;

export const routeSchema = z.strictObject({
  id: z.string().regex(DOC_ID, 'must be kebab-case'),
  path: z.string().regex(ROUTE_PATH, 'must look like /, /events or /events/:slug'),
  page: z.string().regex(DOC_ID, 'must be a page id'),
});

export interface NavItem {
  label: string;
  to: string;
  children?: NavItem[];
}

export const navItemSchema: z.ZodType<NavItem> = z.strictObject({
  label: z.string().min(1).max(80),
  to: z
    .string()
    .refine((v) => isInternalPath(v) || isSafeExternalHref(v), 'must be a path on this site or an http(s) link'),
  get children() {
    return z.array(navItemSchema).optional();
  },
});

/**
 * Two routes that differ only in a parameter's name (`/e/:slug`, `/e/:id`) match exactly the
 * same addresses, so whichever is declared second can never be reached.
 */
export function routeShape(path: string): string {
  return path.replace(/:[a-zA-Z][a-zA-Z0-9]*/g, ':');
}

export const APP_SCHEMA_VERSION = 1;

/** `dcms/app.json`. */
export const appSchema = z
  .strictObject({
    schemaVersion: z.literal(APP_SCHEMA_VERSION),
    routes: z.array(routeSchema).min(1, 'an app needs at least one route'),
    /** Named menus — `main`, `footer` — each drawn by a Navigation component that names it. */
    navigation: z.record(z.string().regex(NAME), z.array(navItemSchema)).optional(),
    /** The tree every route renders inside: header, footer and the outlet the page goes in. */
    shell: nodeSchema.optional(),
    /** The site's language (BCP 47: `cs`, `en-GB`): `<html lang>` and how bound dates read. */
    locale: z.string().regex(/^[a-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$/, 'must be a language tag like cs or en-GB').optional(),
    seo: z
      .strictObject({
        /** `%s` is replaced with the page's title: `%s · Acme`. */
        titleTemplate: z.string().max(200).optional(),
        description: z.string().max(500).optional(),
        ogImage: z.string().max(2048).optional(),
      })
      .optional(),
  })
  .superRefine((app, ctx) => {
    const ids = new Set<string>();
    const shapes = new Map<string, string>();
    app.routes.forEach((route, i) => {
      if (ids.has(route.id)) {
        ctx.addIssue({ code: 'custom', path: ['routes', i, 'id'], message: `route id “${route.id}” is used more than once` });
      }
      ids.add(route.id);
      const shape = routeShape(route.path);
      const clash = shapes.get(shape);
      if (clash !== undefined) {
        ctx.addIssue({
          code: 'custom',
          path: ['routes', i, 'path'],
          message: `“${route.path}” matches the same addresses as “${clash}”, so one of them can never be reached`,
        });
      } else {
        shapes.set(shape, route.path);
      }
    });
    if (app.shell) uniqueNodeIds(app.shell, ctx, ['shell']);
  });

export type Page = z.infer<typeof pageSchema>;
export type Route = z.infer<typeof routeSchema>;
export type App = z.infer<typeof appSchema>;
export type Seo = z.infer<typeof seoSchema>;
export type { Source };
