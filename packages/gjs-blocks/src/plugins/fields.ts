import type { CustomFieldLike, PluginContentTypeLike } from './specs';

/**
 * The fields of one content type, as the component builder needs to show them.
 *
 * The generated blocks only ever expose fields through a handful of fixed slots
 * ("title from", "image from"). A component the tenant builds themselves has no
 * fixed slots at all — any element can be bound to any field — so it needs the
 * fields as a *list*, with the path each value actually lives at and enough type
 * information to suggest what to do with it.
 *
 * The two sources are deliberately different things: `fields` are the plugin's
 * own, identical for every tenant, and `customFields` are the tenant's, defined
 * in that instance's configuration. The second is the whole reason this exists —
 * the same "blog" plugin serves `perex` for one tenant and `standfirst` for the
 * next, and a component builder that could only see the plugin's fixed fields
 * would be blind to most of what a tenant's API actually returns.
 */

/** What a bound value is, coarsely — enough to pick a sensible bind target. */
export type FieldKind = 'text' | 'richText' | 'media' | 'date' | 'number' | 'boolean' | 'tags' | 'url';

export interface ContentField {
  /** The path to read on an item — `perex`, or `values.perex` for a custom field. */
  path: string;
  label: string;
  kind: FieldKind;
  /** True when this is the tenant's own field rather than the plugin's. */
  custom: boolean;
  description?: string;
}

const PLUGIN_KINDS: Record<string, FieldKind> = {
  Text: 'text',
  RichText: 'richText',
  Markdown: 'richText',
  MediaRef: 'media',
  DateTime: 'date',
  Date: 'date',
  Number: 'number',
  Int: 'number',
  Decimal: 'number',
  Bool: 'boolean',
  Boolean: 'boolean',
  Tags: 'tags',
  Url: 'url',
  ContentRef: 'text',
};

const CUSTOM_KINDS: Record<string, FieldKind> = {
  text: 'text',
  longText: 'richText',
  number: 'number',
  boolean: 'boolean',
  date: 'date',
  tags: 'tags',
  url: 'url',
  image: 'media',
};

export function contentFields(
  contentType: PluginContentTypeLike,
  custom: readonly CustomFieldLike[] = [],
): ContentField[] {
  const fields: ContentField[] = contentType.fields.map((field) => ({
    path: field.name,
    label: humanize(field.name),
    kind: PLUGIN_KINDS[field.type] ?? 'text',
    custom: false,
    description: field.description,
  }));

  // A tenant field's values are nested under one key on the item, so its path is
  // `values.barva`, not `barva`. Binding to the bare key renders nothing — which
  // looks exactly like "this field is empty" and is the single easiest way to
  // lose an afternoon in this builder.
  const prefix = contentType.customFields?.valuesField
    ? `${contentType.customFields.valuesField}.`
    : '';
  for (const field of custom) {
    fields.push({
      path: `${prefix}${field.key}`,
      label: field.label || humanize(field.key),
      kind: CUSTOM_KINDS[field.type] ?? 'text',
      custom: true,
      description: field.description,
    });
  }

  return fields;
}

/**
 * The item envelope, which is not part of the data but is bindable.
 *
 * `#slug` is how a card links to its own detail page, and there is no way to
 * express that from the fields alone — the slug lives beside them, not in them.
 */
export const META_FIELDS: ContentField[] = [
  { path: '#slug', label: 'Item slug', kind: 'text', custom: false },
  { path: '#id', label: 'Item id', kind: 'text', custom: false },
  { path: '#index', label: 'Position in the list', kind: 'number', custom: false },
  { path: '#number', label: 'Position, counting from 1', kind: 'number', custom: false },
  { path: '#count', label: 'How many items in the list', kind: 'number', custom: false },
  // The rest are conditions: they read as true/false, so they belong on
  // `data-dcms-if` / `data-dcms-unless` rather than in a text slot. Without them
  // a template has no way to say "the first one is the big card", because
  // `:nth-child` in a stylesheet cannot reach a class name or an attribute.
  {
    path: '#first',
    label: 'Is the first item',
    kind: 'text',
    custom: false,
    description: 'True for the first item. For show/hide conditions.',
  },
  {
    path: '#last',
    label: 'Is the last item',
    kind: 'text',
    custom: false,
    description: 'True for the last item. For show/hide conditions.',
  },
  {
    path: '#parity',
    label: 'Row parity',
    kind: 'text',
    custom: false,
    description: '“even” or “odd”. Bind it to `class` to stripe a list.',
  },
  {
    path: '#value',
    label: 'The entry itself',
    kind: 'text',
    custom: false,
    description: 'Inside a repeat over a list of plain values, this is the value.',
  },
];

/** The bind target that makes sense for a field, before the author overrides it. */
export function suggestedTarget(kind: FieldKind): string {
  if (kind === 'media') return 'src';
  if (kind === 'richText') return 'html';
  if (kind === 'url') return 'href';
  return 'text';
}

function humanize(name: string): string {
  const spaced = name.replace(/([a-z0-9])([A-Z])/g, '$1 $2').replace(/[-_]+/g, ' ');
  return spaced.charAt(0).toUpperCase() + spaced.slice(1).toLowerCase();
}
