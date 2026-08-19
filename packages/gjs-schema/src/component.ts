import { z } from 'zod';
import { BLOCKS_DIR } from './paths';
import type { DcmsComponentSpec, TraitSpec } from './spec';

/**
 * A **tenant-authored component**: a piece of markup the author laid out
 * themselves, with places in it wired to data.
 *
 * The built-in catalogue can only ever be a guess at what a tenant's content
 * looks like. A plugin's content types are configured per instance — the same
 * "blog" plugin can serve `title`/`perex`/`obrazek` for one tenant and
 * `headline`/`standfirst`/`hero` for the next — so a fixed set of card layouts
 * with fixed slots runs out exactly when a site starts being specific. This is
 * the escape hatch that is not "write a plugin": lay out the markup with the
 * ordinary blocks, point each part of it at a field, save it, and it is a block
 * in the palette like any other.
 *
 * The definition is a file in the site's own repo (`blocks/<name>.json`), so it
 * is versioned, diffed, branched and published with the site rather than living
 * in a database the git history knows nothing about.
 *
 * ## The template
 *
 * A template is **ordinary HTML** carrying binding attributes, not a string with
 * `{{ }}` holes in it. That is what lets the component builder be a canvas: the
 * author drags real blocks, and the binding is an attribute on the element they
 * selected. It also means a template can be read, diffed and hand-edited in the
 * code view, and that rendering can never inject markup an author did not write
 * — values land as text nodes and attribute values, never as HTML, unless the
 * binding explicitly says `html`.
 *
 *   <article class="dcms-card">
 *     <img data-dcms-bind="src:cover" data-dcms-if="cover" />
 *     <h3 data-dcms-bind="text:nadpis"></h3>
 *     <p data-dcms-bind="text:perex" data-dcms-truncate="140"></p>
 *     <a data-dcms-bind="href:slug" data-dcms-prefix="/blog/">Read more</a>
 *   </article>
 *
 * `@name` reads a component prop (what the author set in the inspector) instead
 * of an item field, and `#slug` / `#id` / `#index` read the item's envelope
 * rather than its data.
 *
 * ## Two kinds, derived rather than declared
 *
 * A definition with a `source` is **dynamic**: it publishes as the same inert
 * placeholder every plugin component uses and is filled in from the delivery API
 * at run time. A definition without one is a **snippet**: it expands into the
 * page as real markup the moment it is dropped, and is edited afterwards like
 * anything else the author typed. Deriving the kind from the source is the whole
 * distinction — a component with nothing to fetch has no reason to be a
 * placeholder, and making it one would cost a static page its content.
 */

/** Attribute naming the field (or prop) that fills an element. */
export const BIND_ATTR = 'data-dcms-bind';
/** Marks the element repeated once per fetched item. */
export const REPEAT_ATTR = 'data-dcms-repeat';
/** Drop the element when the named source is empty. */
export const IF_ATTR = 'data-dcms-if';
/** Drop the element when the named source has a value. */
export const UNLESS_ATTR = 'data-dcms-unless';
/** `date` renders the value for a reader; `media` resolves an asset reference. */
export const FORMAT_ATTR = 'data-dcms-format';
/** Trim bound text to N characters, cut at a word. */
export const TRUNCATE_ATTR = 'data-dcms-truncate';
/** Wrapped around a bound value — for building `/blog/<slug>` out of a slug. */
export const PREFIX_ATTR = 'data-dcms-prefix';
export const SUFFIX_ATTR = 'data-dcms-suffix';
/** Used when the bound source resolves to nothing. */
export const FALLBACK_ATTR = 'data-dcms-fallback';
/** Kept only when the component has no items at all. */
export const EMPTY_ATTR = 'data-dcms-empty';

/** Every attribute the renderer consumes. Stripped from the rendered output. */
export const TEMPLATE_ATTRS = [
  BIND_ATTR,
  REPEAT_ATTR,
  IF_ATTR,
  UNLESS_ATTR,
  FORMAT_ATTR,
  TRUNCATE_ATTR,
  PREFIX_ATTR,
  SUFFIX_ATTR,
  FALLBACK_ATTR,
  EMPTY_ATTR,
] as const;

/**
 * Where a bound value lands on its element.
 *
 * `text` is the default and by far the common case. `html` exists because a
 * RichText field is markup and rendering it as text would show its tags to the
 * reader — it is opt-in precisely because it is the one target that trusts the
 * value.
 */
export const BIND_TARGETS = [
  'text',
  'html',
  'src',
  'href',
  'alt',
  'title',
  'style:background-image',
  'class',
] as const;

export type BindTarget = (typeof BIND_TARGETS)[number];

/** `text:title` → what it fills and what fills it. Malformed reads as text. */
export function parseBind(value: string): { target: BindTarget; source: string } | null {
  const trimmed = value.trim();
  if (!trimmed) return null;
  const colon = trimmed.indexOf(':');
  // `style:background-image` contains the separator itself, so the split is on
  // the *last* colon that still leaves a known target on the left.
  for (const target of BIND_TARGETS) {
    if (trimmed.startsWith(`${target}:`)) {
      return { target, source: trimmed.slice(target.length + 1).trim() };
    }
  }
  return colon === -1 ? { target: 'text', source: trimmed } : null;
}

/** The prop a `@name` source names, or null when it reads item data. */
export function propSource(source: string): string | null {
  return source.startsWith('@') ? source.slice(1) : null;
}

/** The envelope key a `#slug`/`#id`/`#index` source names, or null. */
export function metaSource(source: string): string | null {
  return source.startsWith('#') ? source.slice(1) : null;
}

export const componentPropSchema = z.object({
  name: z.string().regex(/^[A-Za-z][A-Za-z0-9_]*$/, 'a prop name must be a plain identifier'),
  label: z.string(),
  kind: z.enum(['text', 'longText', 'number', 'checkbox', 'select', 'color', 'url', 'media']),
  description: z.string().optional(),
  options: z.array(z.object({ value: z.string(), label: z.string() })).optional(),
  default: z.union([z.string(), z.number(), z.boolean()]).optional(),
});

/**
 * Where a dynamic component's items come from.
 *
 * `mode` is the same distinction the generated plugin blocks draw: a list is a
 * page of items, a detail is the single item the page's URL names. It is stored
 * rather than inferred from the template because a template that happens to have
 * no repeat is still a list of one, and guessing would make adding a repeat
 * silently change what gets fetched.
 */
export const componentSourceSchema = z.object({
  instanceSlug: z.string(),
  contentType: z.string(),
  mode: z.enum(['list', 'detail']).default('list'),
  /** Default page size for a list. The author can override it per instance. */
  pageSize: z.number().int().positive().optional(),
});

export const COMPONENT_DEFINITION_VERSION = 1;

export const componentDefinitionSchema = z.object({
  version: z.literal(COMPONENT_DEFINITION_VERSION),
  /** File-name stem and identity: `blocks/<name>.json`, `custom:<name>`. */
  name: z.string().regex(/^[a-z0-9][a-z0-9-]*$/, 'name must be kebab-case'),
  label: z.string(),
  docs: z.string().optional(),
  /** Palette group. Tenant components get their own so they are findable. */
  category: z.enum(['section', 'part', 'layout', 'media', 'custom']).default('custom'),
  icon: z.string().optional(),
  props: z.array(componentPropSchema).default([]),
  source: componentSourceSchema.optional(),
  /** The markup, with binding attributes. */
  template: z.string(),
});

export type ComponentProp = z.infer<typeof componentPropSchema>;
export type ComponentSource = z.infer<typeof componentSourceSchema>;
export type ComponentDefinition = z.infer<typeof componentDefinitionSchema>;

/** The palette group every tenant-built component appears in. */
export const OWN_COMPONENTS_GROUP = 'Your components';

export function componentPath(name: string): string {
  return `${BLOCKS_DIR}/${name}.json`;
}

/** The `data-dcms-component` value (and spec type) for a tenant component. */
export function componentTypeOf(name: string): string {
  return `custom:${name}`;
}

/** The component name a `custom:<name>` type refers to, or null. */
export function nameFromComponentType(type: string): string | null {
  return type.startsWith('custom:') ? type.slice('custom:'.length) : null;
}

/** The identity class a tenant component's root markup carries. */
export function componentClassOf(name: string): string {
  return `dcms-c-${name}`;
}

/** True when this definition fetches content rather than expanding in place. */
export function isDynamic(definition: ComponentDefinition): boolean {
  return definition.source !== undefined;
}

export function parseComponentDefinition(input: unknown): ComponentDefinition {
  return componentDefinitionSchema.parse(input);
}

export function safeParseComponentDefinition(input: unknown) {
  return componentDefinitionSchema.safeParse(input);
}

export function serializeComponentDefinition(definition: ComponentDefinition): string {
  return `${JSON.stringify(definition, null, 2)}\n`;
}

/** An empty definition to start the component builder from. */
export function emptyComponentDefinition(name: string, label: string): ComponentDefinition {
  return {
    version: COMPONENT_DEFINITION_VERSION,
    name,
    label,
    category: 'custom',
    props: [],
    template: `<div class="${componentClassOf(name)}"></div>`,
  };
}

/**
 * A definition, as the rest of the builder sees every component.
 *
 * Turning it into a `DcmsComponentSpec` rather than teaching the palette, the
 * canvas, the inspector and the code view about a second kind of component is
 * the point: a tenant component gets the block tile, the trait panel, the
 * completions and the diagnostics for free, and cannot drift from them.
 */
export function specForComponent(definition: ComponentDefinition): DcmsComponentSpec {
  const traits: TraitSpec[] = definition.props.map((prop) => ({
    name: prop.name,
    label: prop.label,
    kind: prop.kind,
    description: prop.description,
    options: prop.options,
    default: prop.default,
    // A dynamic component reads its props out of `data-dcms-props` at render
    // time; a snippet has already had them substituted into the markup it
    // dropped, so its traits are plain attributes and there is nothing to read.
    target: definition.source ? 'prop' : 'attribute',
  }));

  if (definition.source?.mode === 'list') {
    traits.push({
      name: 'pageSize',
      label: 'How many',
      kind: 'number',
      target: 'query',
      default: definition.source.pageSize ?? 10,
      description: 'Maximum number of items to show.',
    });
  }
  if (definition.source?.mode === 'detail') {
    traits.push({
      name: 'itemSlug',
      label: 'Show item',
      kind: 'text',
      target: 'query',
      description: 'Leave empty to show whichever item the page URL names.',
    });
  }

  return {
    type: componentTypeOf(definition.name),
    label: definition.label,
    category: definition.source ? 'plugin' : definition.category,
    tag: 'div',
    identityClass: componentClassOf(definition.name),
    // Both kinds share one palette group. A dynamic component is a *plugin*
    // component by category — that is what decides how it publishes — but an
    // author looking for the card they built themselves should not have to know
    // that, or hunt for it among the generated blocks.
    group: OWN_COMPONENTS_GROUP,
    acceptsChildren: false,
    docs: definition.docs,
    icon: definition.icon,
    traits,
    // A snippet drops its template verbatim; a dynamic component drops the
    // placeholder `defaultSnippet` builds from the binding below.
    snippet: definition.source ? undefined : definition.template,
    binding: definition.source
      ? {
          propPath: definition.source.mode === 'detail' ? 'item' : 'items',
          contentType: definition.source.contentType,
          instanceSlug: definition.source.instanceSlug,
        }
      : undefined,
  };
}
