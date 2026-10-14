import type { TFunction } from 'i18next';
import type { ViewField, ViewFieldKind } from '@dcms/ui';
import { customFieldsOf, parseInstanceConfig, type ContentTypeDef, type FieldType, type PluginInstance } from '../plugins/api';

/** Turn a machine name ("blogPost", "gig_line-up") into a readable label. */
export function humanize(name: string): string {
  const spaced = name
    .replace(/[_-]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .trim();
  return spaced.charAt(0).toUpperCase() + spaced.slice(1);
}

/** The columns every collection shows, by the ids the list and the server's sort know. */
export const BUILT_IN_COLUMNS = ['title', 'slug', 'status', 'updated'] as const;

const KIND: Partial<Record<FieldType, ViewFieldKind>> = {
  Text: 'text',
  RichText: 'text',
  Markdown: 'text',
  Number: 'number',
  Boolean: 'boolean',
  DateTime: 'date',
  Tags: 'tags',
  MediaRef: 'reference',
  ContentRef: 'reference',
};

const CUSTOM_KIND: Record<string, ViewFieldKind> = {
  text: 'text',
  longText: 'text',
  url: 'text',
  number: 'number',
  boolean: 'boolean',
  date: 'date',
  tags: 'tags',
  image: 'reference',
};

/**
 * What a saved view may use in one collection (#278): the type's own fields by name and its
 * tenant-defined ones as `custom.{key}` — the keys the server's filter and columns take.
 */
export function collectionDataFields(instance: PluginInstance, contentType: ContentTypeDef): ViewField[] {
  const own = contentType.fields.flatMap((f): ViewField[] => {
    const kind = KIND[f.type];
    return kind ? [{ key: f.name, label: humanize(f.name), kind, sortable: kind !== 'tags' }] : [];
  });
  const custom = customFieldsOf(contentType, parseInstanceConfig(instance.config)).flatMap((f): ViewField[] => {
    const kind = CUSTOM_KIND[f.type];
    return kind ? [{ key: `custom.${f.key}`, label: f.label, kind, sortable: kind !== 'tags' }] : [];
  });
  return [...own, ...custom];
}

/** Filterable: the collection's fields, plus the item's own slug, status and dates. */
export function collectionFilterFields(data: ViewField[], t: TFunction): ViewField[] {
  return [
    { key: '_slug', label: t('common.slug'), kind: 'text' },
    {
      key: '_status',
      label: t('common.status'),
      kind: 'choice',
      options: [
        { value: 'Draft', label: t('content.draft') },
        { value: 'Published', label: t('content.published') },
        { value: 'Archived', label: t('content.archived') },
      ],
    },
    { key: '_updatedAt', label: t('common.updated'), kind: 'date' },
    { key: '_publishedAt', label: t('content.publishedAt'), kind: 'date' },
    ...data,
  ];
}

/**
 * The collection's fields that can be a column of their own: not one the built-in columns
 * already are (a type with a field called `title` has it as the Title column).
 */
export function columnDataFields(data: ViewField[]): ViewField[] {
  return data.filter((f) => !(BUILT_IN_COLUMNS as readonly string[]).includes(f.key));
}

/** Showable: the built-in columns, then the collection's fields. */
export function collectionColumnFields(data: ViewField[], t: TFunction): ViewField[] {
  return [
    { key: 'title', label: t('content.itemTitle'), kind: 'text' },
    { key: 'slug', label: t('common.slug'), kind: 'text' },
    { key: 'status', label: t('common.status'), kind: 'text' },
    { key: 'updated', label: t('common.updated'), kind: 'date' },
    ...columnDataFields(data),
  ];
}

/** A field's value in a list cell: short, plain, and never markup. */
export function CellValue({ kind, value }: { kind: ViewFieldKind; value: unknown }) {
  if (value === null || value === undefined || value === '') return <span className="text-muted-foreground">—</span>;
  if (kind === 'boolean') return <span>{value === true || value === 'true' ? '✓' : '—'}</span>;
  if (kind === 'tags' && Array.isArray(value)) return <span className="[overflow-wrap:anywhere]">{value.join(', ')}</span>;
  if (kind === 'date' && typeof value === 'string') {
    const d = new Date(value);
    return <span className="tabular-nums">{Number.isNaN(d.getTime()) ? value : d.toLocaleDateString()}</span>;
  }
  if (kind === 'number') return <span className="tabular-nums">{String(value)}</span>;
  const text = typeof value === 'string' ? value.replace(/<[^>]*>/g, ' ').replace(/\s+/g, ' ').trim() : JSON.stringify(value);
  return <span className="line-clamp-2 [overflow-wrap:anywhere]">{text}</span>;
}
