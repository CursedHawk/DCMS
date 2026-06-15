import { z } from 'zod';

/**
 * Component-tree schema — the contract between the drag-and-drop editor, the
 * AI generation flows (responses are constrained to this shape), the Mode A
 * prerenderer and the site-host runtime. Version-gated for evolution.
 */

export const dataBindingSchema = z.object({
  /** JSON-path-ish location of the prop to fill, e.g. "items". */
  propPath: z.string(),
  source: z.object({
    /** Plugin instance slug on the tenant's content API. */
    instanceSlug: z.string(),
    /** Query against the instance, e.g. { contentType: "post", pageSize: 10 }. */
    query: z.record(z.string(), z.unknown()),
  }),
});

export interface ComponentNode {
  id: string;
  type: string;
  props: Record<string, unknown>;
  bindings?: z.infer<typeof dataBindingSchema>[];
  children?: ComponentNode[];
}

export const componentNodeSchema: z.ZodType<ComponentNode> = z.lazy(() =>
  z.object({
    id: z.string(),
    type: z.string(),
    props: z.record(z.string(), z.unknown()),
    bindings: z.array(dataBindingSchema).optional(),
    children: z.array(componentNodeSchema).optional(),
  }),
);

export const seoMetaSchema = z.object({
  title: z.string(),
  description: z.string().optional(),
  ogImage: z.string().optional(),
});

export const pageSchema = z.object({
  id: z.string(),
  path: z.string().regex(/^\//, 'page path must start with /'),
  title: z.string(),
  seo: seoMetaSchema,
  root: componentNodeSchema,
});

export const themeTokensSchema = z.object({
  colors: z.record(z.string(), z.string()).default({}),
  fonts: z.record(z.string(), z.string()).default({}),
  radius: z.string().optional(),
});

export const navItemSchema = z.object({
  label: z.string(),
  path: z.string(),
});

export const siteDefinitionSchema = z.object({
  version: z.literal(1),
  theme: themeTokensSchema,
  pages: z.array(pageSchema).min(1),
  nav: z.array(navItemSchema).default([]),
});

export type DataBinding = z.infer<typeof dataBindingSchema>;
export type SeoMeta = z.infer<typeof seoMetaSchema>;
export type Page = z.infer<typeof pageSchema>;
export type ThemeTokens = z.infer<typeof themeTokensSchema>;
export type SiteDefinition = z.infer<typeof siteDefinitionSchema>;

export function parseSiteDefinition(input: unknown): SiteDefinition {
  return siteDefinitionSchema.parse(input);
}
