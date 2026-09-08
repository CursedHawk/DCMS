import { Tags } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import {
  Badge,
  CenteredSpinner,
  type Column,
  DataTable,
  EmptyState,
  FilterBar,
  useDebounced,
} from '@dcms/ui';
import { type TagSuggestion, useTenantTags } from './api';

/**
 * The tenant's whole tag vocabulary, and where each word is used.
 *
 * <p>Tags are not a table — a plugin declares a `Tags` field and the values land in the item's
 * JSON, which is the right storage. The cost is that nobody could see the vocabulary, and a
 * vocabulary nobody can see is one that splits: "live", "Live" and "live-music" all get typed
 * because there was no way to notice the first one already existed.</p>
 *
 * <p>The occurrences are the point. A tag used once, in one field, on one collection, is either
 * a typo or something that has not caught on — and both are worth seeing next to the tag that
 * is used ninety times.</p>
 *
 * <p><b>Read-only.</b> Renaming a tag means rewriting it inside every item that carries it,
 * across drafts and published versions; that is a content migration, not a list edit, and it
 * does not belong behind a pencil icon on a page whose job is to show you what you have.</p>
 */
export function TagsView() {
  const { t } = useTranslation();
  const tags = useTenantTags();
  const [query, setQuery] = useState('');
  const search = useDebounced(query).trim().toLowerCase();

  const rows = (tags.data ?? []).filter(
    (row) =>
      search.length === 0 ||
      row.tag.toLowerCase().includes(search) ||
      (row.occurrences ?? []).some(
        (o) =>
          o.instanceSlug.toLowerCase().includes(search) ||
          o.contentType.toLowerCase().includes(search) ||
          o.field.toLowerCase().includes(search),
      ),
  );

  const columns: Column<TagSuggestion>[] = [
    {
      id: 'tag',
      header: t('content.tags.tag'),
      primary: true,
      cell: (row) => <span className="font-medium">{row.tag}</span>,
      sortValue: (row) => row.tag.toLowerCase(),
    },
    {
      id: 'uses',
      header: t('content.tags.uses'),
      align: 'right',
      width: 'w-24',
      cell: (row) => <span className="tabular-nums text-muted-foreground">{row.count}</span>,
      sortValue: (row) => row.count,
    },
    {
      id: 'where',
      header: t('content.tags.where'),
      cell: (row) => (
        <div className="flex flex-wrap gap-1">
          {(row.occurrences ?? []).map((o) => (
            <Badge
              key={`${o.instanceSlug}:${o.contentType}:${o.field}`}
              tone="outline"
              // The field matters: an event can have genres AND tags, and a word that is a
              // genre on one collection and a tag on another is two different vocabularies
              // wearing the same spelling.
              title={t('content.tags.occurrence', {
                instance: o.instanceSlug,
                type: o.contentType,
                field: o.field,
                count: o.count,
              })}
            >
              {o.instanceSlug} · {o.field}
              <span className="ml-1 tabular-nums opacity-70">{o.count}</span>
            </Badge>
          ))}
          {(row.occurrences ?? []).length === 0 ? (
            <span className="text-xs text-muted-foreground">—</span>
          ) : null}
        </div>
      ),
      sortValue: (row) => row.occurrences?.length ?? 0,
    },
  ];

  if (tags.isLoading) return <CenteredSpinner />;

  return (
    <div>
      <FilterBar
        search={query}
        onSearchChange={setQuery}
        searchPlaceholder={t('content.tags.searchPlaceholder')}
      />
      <DataTable
        rows={rows}
        columns={columns}
        rowKey={(row) => row.tag}
        caption={t('content.tags.title')}
        defaultSort={{ columnId: 'uses', direction: 'desc' }}
        labels={{ loading: t('common.loading'), sortBy: (column) => t('common.sortBy', { column }) }}
        empty={
          <EmptyState
            icon={Tags}
            title={search ? t('common.noResults') : t('content.tags.none')}
            description={search ? undefined : t('content.tags.noneHint')}
          />
        }
      />
    </div>
  );
}
