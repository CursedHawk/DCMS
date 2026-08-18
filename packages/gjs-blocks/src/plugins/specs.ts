import type { DcmsComponentSpec, TraitOption, TraitSpec } from '@dcms/gjs-schema';

/**
 * Tenant plugin instances → builder components.
 *
 * Every enabled plugin instance publishes content types, and each content type
 * is something an author should be able to drop onto a page: a list of posts, a
 * gallery, one product's detail view. Rather than hand-writing a component per
 * plugin — which would mean shipping admin code every time a tenant installs
 * one — the palette is generated from the plugin manifests the tenant already
 * has, joined with their instances.
 *
 * What comes out is an ordinary `DcmsComponentSpec`, so a generated component is
 * indistinguishable from a built-in one everywhere downstream: same palette,
 * same inspector, same code-view completions, same round-trip test. And what it
 * publishes is the placeholder contract `hydrate.js` already reads, so none of
 * this needs a single line of backend plugin code.
 *
 * The input types are structural on purpose — the admin passes its API DTOs
 * straight in, and this package stays independent of them.
 */

export interface PluginFieldLike {
  name: string;
  type: string;
  required?: boolean;
  description?: string;
}

export interface PluginContentTypeLike {
  name: string;
  searchable?: boolean;
  slugField?: string;
  fields: PluginFieldLike[];
}

export interface PluginManifestLike {
  id: string;
  name: string;
  description?: string;
  contentTypes: PluginContentTypeLike[];
}

export interface PluginInstanceLike {
  id: string;
  pluginId: string;
  slug: string;
  name: string;
  enabled: boolean;
}

/** A tenant-defined field, from an instance's own configuration. */
export interface CustomFieldLike {
  key: string;
  label: string;
  type: string;
  description?: string;
}

export interface PluginSpecOptions {
  /**
   * Tenant-defined fields for one instance/content type, read from the
   * instance's config by the caller (only it can parse the config JSON).
   */
  customFields?: (instanceSlug: string, contentType: string) => CustomFieldLike[];
}

/** How the published page should lay a collection out. Understood by hydrate.js. */
const LAYOUTS: TraitOption[] = [
  { value: 'cards', label: 'Cards' },
  { value: 'list', label: 'List' },
  { value: 'article', label: 'Article' },
  { value: 'video', label: 'Video players' },
  { value: 'audio', label: 'Audio players' },
  { value: 'downloads', label: 'Download links' },
];

/**
 * A layout guess from the content type's own shape. A gallery of images should
 * not default to a text-card grid, and getting this right means most blocks are
 * usable the moment they are dropped.
 */
function defaultLayout(contentType: PluginContentTypeLike): string {
  const types = new Set(contentType.fields.map((f) => f.type));
  const names = contentType.name.toLowerCase();
  if (/video|clip|film/.test(names)) return 'video';
  if (/audio|track|song|podcast|episode/.test(names)) return 'audio';
  if (/file|download|document|attachment/.test(names)) return 'downloads';
  if (/gallery|photo|image|slide/.test(names)) return 'cards';
  return types.has('MediaRef') ? 'cards' : 'list';
}

/** Fields that could plausibly fill each slot of a card, for the mapping traits. */
function fieldOptions(
  contentType: PluginContentTypeLike,
  custom: CustomFieldLike[],
  predicate: (type: string) => boolean,
): TraitOption[] {
  const options: TraitOption[] = [{ value: '', label: 'Auto' }];
  for (const field of contentType.fields) {
    if (predicate(field.type)) options.push({ value: field.name, label: humanize(field.name) });
  }
  for (const field of custom) {
    if (predicate(field.type)) options.push({ value: field.key, label: field.label });
  }
  return options;
}

const TEXTUAL = (type: string) =>
  ['Text', 'RichText', 'Markdown', 'text', 'longText'].includes(type);
const MEDIA = (type: string) => ['MediaRef', 'image'].includes(type);
const LINKY = (type: string) => ['Text', 'text', 'url'].includes(type);

/**
 * Traits shared by every generated component: what it says, how it looks, and
 * which field fills which slot. The field mappings matter because `hydrate.js`
 * otherwise guesses from key names — fine for a `title`, useless for a content
 * type whose headline field is called `nadpis`.
 */
function presentationTraits(
  contentType: PluginContentTypeLike,
  custom: CustomFieldLike[],
  layout: string,
): TraitSpec[] {
  return [
    {
      name: 'heading',
      label: 'Heading',
      kind: 'text',
      target: 'prop',
      description: 'Shown above the content. Leave empty for none.',
    },
    {
      name: 'layout',
      label: 'Layout',
      kind: 'select',
      target: 'prop',
      options: LAYOUTS,
      default: layout,
    },
    {
      name: 'emptyText',
      label: 'Empty message',
      kind: 'text',
      target: 'prop',
      description: 'Shown when the plugin has no published content yet.',
    },
    {
      name: 'titleField',
      label: 'Title from',
      kind: 'select',
      target: 'prop',
      options: fieldOptions(contentType, custom, TEXTUAL),
      description: 'Which field supplies each item’s title.',
    },
    {
      name: 'bodyField',
      label: 'Text from',
      kind: 'select',
      target: 'prop',
      options: fieldOptions(contentType, custom, TEXTUAL),
    },
    {
      name: 'imageField',
      label: 'Image from',
      kind: 'select',
      target: 'prop',
      options: fieldOptions(contentType, custom, MEDIA),
    },
    {
      name: 'linkField',
      label: 'Link from',
      kind: 'select',
      target: 'prop',
      options: fieldOptions(contentType, custom, LINKY),
      description: 'Which field each card links to, if any.',
    },
  ];
}

/**
 * Traits that change *what* is fetched.
 *
 * Deliberately short: the delivery API pages (`page`, `pageSize`) and fetches one
 * item by slug, and that is all. Offering a "sort by" or "filter where" control
 * that the server cannot honour would be a setting that silently does nothing —
 * worse than no setting at all. When delivery grows those parameters, this is
 * where they belong.
 */
function listQueryTraits(): TraitSpec[] {
  return [
    {
      name: 'pageSize',
      label: 'How many',
      kind: 'number',
      target: 'query',
      default: 10,
      description: 'Maximum number of items to show.',
    },
    {
      name: 'page',
      label: 'Page',
      kind: 'number',
      target: 'query',
      description: 'Which page of results to show. Leave empty for the first.',
    },
  ];
}

function detailQueryTraits(contentType: PluginContentTypeLike): TraitSpec[] {
  return [
    {
      name: 'itemSlug',
      label: 'Show item',
      kind: 'text',
      target: 'query',
      description: `Leave empty to show whichever ${humanize(contentType.name).toLowerCase()} the page URL names.`,
    },
  ];
}

/**
 * Every component the tenant's enabled plugin instances imply.
 *
 * Disabled instances are skipped rather than greyed out: a block whose data
 * source is switched off publishes an empty div, which is worse than not being
 * offered at all.
 */
export function pluginSpecs(
  manifests: readonly PluginManifestLike[],
  instances: readonly PluginInstanceLike[],
  options: PluginSpecOptions = {},
): DcmsComponentSpec[] {
  const byId = new Map(manifests.map((m) => [m.id, m]));
  const specs: DcmsComponentSpec[] = [];

  for (const instance of instances) {
    if (!instance.enabled) continue;
    const manifest = byId.get(instance.pluginId);
    if (!manifest) continue;

    for (const [index, contentType] of manifest.contentTypes.entries()) {
      const custom = options.customFields?.(instance.slug, contentType.name) ?? [];
      const layout = defaultLayout(contentType);
      const label = humanize(contentType.name);
      const presentation = presentationTraits(contentType, custom, layout);

      specs.push({
        type: listType(instance.slug, contentType.name),
        label: `${label} list`,
        category: 'plugin',
        tag: 'div',
        group: instance.name,
        acceptsChildren: false,
        order: index * 2,
        requiredPluginId: manifest.id,
        icon: 'list',
        docs: `Shows ${label.toLowerCase()} items from “${instance.name}”.`,
        traits: [...presentation, ...listQueryTraits()],
        binding: { propPath: 'items', contentType: contentType.name, instanceSlug: instance.slug },
      });

      // A detail view only makes sense where the content type has a slug: that
      // is what a published page routes on to pick which item to show.
      if (contentType.slugField) {
        specs.push({
          type: detailType(instance.slug, contentType.name),
          label: `${label} detail`,
          category: 'plugin',
          tag: 'div',
          group: instance.name,
          acceptsChildren: false,
          order: index * 2 + 1,
          requiredPluginId: manifest.id,
          icon: 'article',
          docs: `Shows one ${label.toLowerCase()} from “${instance.name}”, chosen by the page’s slug.`,
          traits: [
            ...presentation.map((trait) =>
              trait.name === 'layout' ? { ...trait, default: 'article' } : trait,
            ),
            ...detailQueryTraits(contentType),
          ],
          binding: { propPath: 'item', contentType: contentType.name, instanceSlug: instance.slug },
        });
      }
    }
  }

  return specs;
}

/**
 * Component type ids. The instance slug is part of the id because two instances
 * of the same plugin are genuinely different components — "news" and "blog" both
 * publish `post`, and a block has to know which one it belongs to.
 */
export function listType(instanceSlug: string, contentType: string): string {
  return `plugin:${instanceSlug}:${contentType}:list`;
}

export function detailType(instanceSlug: string, contentType: string): string {
  return `plugin:${instanceSlug}:${contentType}:detail`;
}

/**
 * `productVariant` → `Product variant`. Sentence case, not title case: these are
 * labels in a form, and only the first word is capitalized. An all-caps word is
 * left alone so an acronym stays an acronym.
 */
function humanize(value: string): string {
  const words = value
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[_-]+/g, ' ')
    .trim()
    .split(/\s+/)
    .filter(Boolean);
  if (words.length === 0) return value;
  return words
    .map((word, i) => {
      if (word.toUpperCase() === word && word.length > 1) return word;
      return i === 0 ? word.charAt(0).toUpperCase() + word.slice(1) : word.toLowerCase();
    })
    .join(' ');
}
