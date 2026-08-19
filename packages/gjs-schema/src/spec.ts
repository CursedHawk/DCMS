/**
 * `DcmsComponentSpec` — the one description of a builder component, consumed by
 * three very different things:
 *
 *  1. `@dcms/gjs-blocks`, which turns a spec into a GrapesJS component type
 *     (traits, defaults, `isComponent`/`isParsedNode`) and a palette block;
 *  2. the code view, which turns the same spec into Monaco HTML custom data so
 *     the html worker offers tag/attribute completion and hover docs;
 *  3. the intellisense worker, which validates authored markup against it.
 *
 * Keeping it in this framework-free package is what stops those three drifting:
 * a component that exists in the palette but not in the completion list, or that
 * completes an attribute the block ignores, is exactly the bug this prevents.
 */

export type ComponentCategory =
  | 'layout'
  | 'typography'
  | 'media'
  | 'navigation'
  | 'section'
  /**
   * The repeatable pieces sections are built from — one card, one plan, one
   * step. They are their own category because the operation an author reaches
   * for most is "add another one of these", and that only works if the piece is
   * a real component with its own traits rather than anonymous markup inside a
   * section's snippet.
   */
  | 'part'
  | 'interactive'
  | 'form'
  | 'utility'
  | 'plugin'
  /**
   * A component the tenant built themselves in the component builder (see
   * ./component). It is a category rather than a flag so the palette can keep
   * "the things we shipped" and "the things you made" apart, which is the first
   * question an author has when their own list is thirty entries long.
   */
  | 'custom';

export type TraitKind =
  | 'text'
  | 'longText'
  | 'number'
  | 'checkbox'
  | 'select'
  | 'color'
  | 'date'
  | 'url'
  | 'media'
  | 'contentRef'
  | 'tags'
  | 'code';

export interface TraitOption {
  value: string;
  label: string;
}

export interface TraitSpec {
  /** Attribute or prop name this trait writes. */
  name: string;
  label: string;
  kind: TraitKind;
  /** Shown in the inspector and as Monaco hover documentation. */
  description?: string;
  options?: TraitOption[];
  default?: string | number | boolean;
  required?: boolean;
  /**
   * Where the value is written. `attribute` = a plain HTML attribute;
   * `prop` = a key inside `data-dcms-props`; `query` = a key inside the binding's
   * delivery query (page size, sort, filter). The last two are plugin components
   * only, and both end up inside the placeholder contract hydrate.js reads.
   */
  target?: 'attribute' | 'prop' | 'query';
  /** For `media`/`contentRef`: what the picker should offer. */
  accepts?: { mediaCategory?: string; pluginId?: string; contentType?: string };
}

/** How a plugin component's fetched data reaches it (see ./placeholder). */
export interface BindingSpec {
  propPath: string;
  contentType: string;
  /** Fixed when the spec came from a specific plugin instance. */
  instanceSlug?: string;
}

export interface DcmsComponentSpec {
  /** Stable type id, also the `data-dcms-component` value for plugin components. */
  type: string;
  label: string;
  category: ComponentCategory;
  /** Rendered element. Plugin components are always a `div` placeholder. */
  tag: string;
  /**
   * The class that identifies this component when its markup is read back.
   *
   * Identity lives in a class rather than a marker attribute on purpose: the
   * class is already meaningful (it is what the CSS targets), it keeps the
   * committed HTML free of editor bookkeeping, and it means an author who types
   * `<section class="dcms-hero">` by hand in the code view gets a real Hero
   * component in the canvas. Defaults to `dcms-<kebab-type>`.
   */
  identityClass?: string;
  /** Extra classes the block starts with, beyond the identity class. */
  classes?: string[];
  traits: TraitSpec[];
  acceptsChildren: boolean;
  /** One-line description for the palette tile and Monaco hover. */
  docs?: string;
  /** Markup the palette block drops into the canvas. */
  snippet?: string;
  /** Palette sub-group, e.g. the plugin instance name. */
  group?: string;
  /** Icon name for the palette tile; falls back to the category's default. */
  icon?: string;
  /** Only offered when this plugin is enabled for the tenant. */
  requiredPluginId?: string;
  /** Present on data-bound plugin components. */
  binding?: BindingSpec;
  /** Order within its category in the palette. */
  order?: number;
}

/** The class that identifies a component in committed markup. */
export function identityClassOf(spec: DcmsComponentSpec): string {
  return spec.identityClass ?? `dcms-${kebab(spec.type)}`;
}

/** Every class a fresh instance of this component starts with. */
export function classesOf(spec: DcmsComponentSpec): string[] {
  return [identityClassOf(spec), ...(spec.classes ?? [])];
}

function kebab(value: string): string {
  return value
    .replace(/([a-z0-9])([A-Z])/g, '$1-$2')
    .replace(/[^a-zA-Z0-9]+/g, '-')
    .toLowerCase()
    .replace(/^-+|-+$/g, '');
}

/** Specs available given the tenant's enabled plugin ids. */
export function availableSpecs(
  specs: readonly DcmsComponentSpec[],
  enabledPluginIds: ReadonlySet<string>,
): DcmsComponentSpec[] {
  return specs.filter((s) => !s.requiredPluginId || enabledPluginIds.has(s.requiredPluginId));
}

export function findSpec(
  specs: readonly DcmsComponentSpec[],
  type: string,
): DcmsComponentSpec | undefined {
  return specs.find((s) => s.type === type);
}

/** Group specs by category, then by `group`, preserving `order` then label. */
export function groupSpecs(
  specs: readonly DcmsComponentSpec[],
): { category: ComponentCategory; groups: { group: string | null; specs: DcmsComponentSpec[] }[] }[] {
  const byCategory = new Map<ComponentCategory, DcmsComponentSpec[]>();
  for (const spec of specs) {
    const list = byCategory.get(spec.category) ?? [];
    list.push(spec);
    byCategory.set(spec.category, list);
  }

  const sort = (a: DcmsComponentSpec, b: DcmsComponentSpec) =>
    (a.order ?? 0) - (b.order ?? 0) || a.label.localeCompare(b.label);

  return [...byCategory.entries()].map(([category, list]) => {
    const byGroup = new Map<string | null, DcmsComponentSpec[]>();
    for (const spec of list) {
      const key = spec.group ?? null;
      const g = byGroup.get(key) ?? [];
      g.push(spec);
      byGroup.set(key, g);
    }
    return {
      category,
      groups: [...byGroup.entries()].map(([group, groupSpecsList]) => ({
        group,
        specs: [...groupSpecsList].sort(sort),
      })),
    };
  });
}

/** The default attribute set a block starts with, from its traits' defaults. */
export function defaultAttributes(spec: DcmsComponentSpec): Record<string, string> {
  const attrs: Record<string, string> = {};
  for (const trait of spec.traits) {
    if (trait.target === 'prop' || trait.target === 'query') continue;
    if (trait.default === undefined) continue;
    attrs[trait.name] = String(trait.default);
  }
  return attrs;
}

/** The default `data-dcms-props` object, from traits targeting props. */
export function defaultProps(spec: DcmsComponentSpec): Record<string, unknown> {
  return defaultsForTarget(spec, 'prop');
}

/**
 * The default delivery query, from traits targeting the query plus the spec's
 * own `contentType` — which hydrate.js needs to build the fetch URL at all.
 */
export function defaultQuery(spec: DcmsComponentSpec): Record<string, unknown> {
  const query = defaultsForTarget(spec, 'query');
  if (spec.binding?.contentType) query.contentType = spec.binding.contentType;
  return query;
}

function defaultsForTarget(
  spec: DcmsComponentSpec,
  target: TraitSpec['target'],
): Record<string, unknown> {
  const out: Record<string, unknown> = {};
  for (const trait of spec.traits) {
    if (trait.target !== target) continue;
    if (trait.default === undefined) continue;
    out[trait.name] = trait.default;
  }
  return out;
}
