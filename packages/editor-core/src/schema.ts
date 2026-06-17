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

/** A single responsive override (any subset of the base layout box). */
export const breakpointLayoutSchema = z
  .object({
    x: z.number(),
    y: z.number(),
    w: z.number(),
    h: z.number(),
    z: z.number().int(),
  })
  .partial();

/** Absolute layout box (px) for free-canvas pages, with optional per-breakpoint overrides. */
export const nodeLayoutSchema = z.object({
  x: z.number(),
  y: z.number(),
  w: z.number(),
  h: z.number(),
  z: z.number().int().optional(),
  breakpoints: z.record(z.string(), breakpointLayoutSchema).optional(),
});

export type BreakpointLayout = z.infer<typeof breakpointLayoutSchema>;
export type NodeLayout = z.infer<typeof nodeLayoutSchema>;
export type Breakpoint = 'desktop' | 'tablet' | 'mobile';

export interface ComponentNode {
  id: string;
  type: string;
  props: Record<string, unknown>;
  bindings?: z.infer<typeof dataBindingSchema>[];
  children?: ComponentNode[];
  layout?: NodeLayout;
}

export const componentNodeSchema: z.ZodType<ComponentNode> = z.lazy(() =>
  z.object({
    id: z.string(),
    type: z.string(),
    props: z.record(z.string(), z.unknown()),
    bindings: z.array(dataBindingSchema).optional(),
    children: z.array(componentNodeSchema).optional(),
    layout: nodeLayoutSchema.optional(),
  }),
);

/** Resolve a node's effective box for a breakpoint (base merged with overrides). */
export function effectiveLayout(node: ComponentNode, breakpoint: Breakpoint): NodeLayout | undefined {
  if (!node.layout) return undefined;
  if (breakpoint === 'desktop') return node.layout;
  const override = node.layout.breakpoints?.[breakpoint] ?? {};
  return { ...node.layout, ...override };
}

export const seoMetaSchema = z.object({
  title: z.string(),
  description: z.string().optional(),
  ogImage: z.string().optional(),
});

export const canvasConfigSchema = z.object({
  width: z.number().default(1200),
  minHeight: z.number().default(800),
});

export const pageSchema = z.object({
  id: z.string(),
  path: z.string().regex(/^\//, 'page path must start with /'),
  title: z.string(),
  seo: seoMetaSchema,
  root: componentNodeSchema,
  /** Present on free-canvas pages: nodes use absolute layout against this stage. */
  canvas: canvasConfigSchema.optional(),
});

export type CanvasConfig = z.infer<typeof canvasConfigSchema>;

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
