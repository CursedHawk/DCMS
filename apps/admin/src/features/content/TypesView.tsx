import { FileText } from 'lucide-react';
import { useTranslation } from 'react-i18next';
import { Badge, CenteredSpinner, type Column, DataTable, EmptyState, Page } from '@dcms/ui';
import { type ContentFieldDef, usePluginCatalog, usePluginInstances } from '../plugins/api';

interface TypeRow {
  key: string;
  plugin: string;
  pluginId: string;
  type: string;
  fields: ContentFieldDef[];
  /** How many enabled instances of the owning plugin exist — how many collections of this type. */
  collections: number;
  /** The tenant can add fields of its own to this type, in the instance's configuration. */
  extensible: boolean;
}

/**
 * What this workspace can author, and what each thing is made of.
 *
 * <p>Every one of these facts was already in the browser — the plugin catalogue drives the
 * editors — and was visible nowhere. Answering "what fields does an event have" meant creating
 * one and looking, and answering "why can I not add a subtitle" meant reading a plugin's source.</p>
 *
 * <p><b>Read-only, and that is not a gap.</b> A content type belongs to the plugin that
 * declares it: changing one would change it for every tenant running that plugin. Where a
 * tenant can add fields of its own the row says so, and the place to do it is the instance's
 * own configuration, where it already lives.</p>
 */
export function TypesView() {
  const { t } = useTranslation();
  const catalog = usePluginCatalog();
  const instances = usePluginInstances();

  if (catalog.isLoading || instances.isLoading) return <CenteredSpinner />;

  const enabled = (instances.data ?? []).filter((i) => i.enabled);

  const rows: TypeRow[] = (catalog.data ?? []).flatMap((manifest) =>
    manifest.contentTypes.map((ct) => ({
      key: `${manifest.id}:${ct.name}`,
      plugin: manifest.name,
      pluginId: manifest.id,
      type: ct.name,
      fields: ct.fields,
      collections: enabled.filter((i) => i.pluginId === manifest.id).length,
      extensible: !!ct.customFields,
    })),
  );

  const columns: Column<TypeRow>[] = [
    {
      id: 'type',
      header: t('content.types.type'),
      primary: true,
      cell: (r) => (
        <span className="font-medium">
          {r.type}
          {r.extensible ? (
            <Badge tone="outline" className="ml-2">
              {t('content.types.extensible')}
            </Badge>
          ) : null}
        </span>
      ),
      sortValue: (r) => r.type.toLowerCase(),
    },
    {
      id: 'plugin',
      header: t('content.types.plugin'),
      cell: (r) => <span className="text-muted-foreground">{r.plugin}</span>,
      sortValue: (r) => r.plugin.toLowerCase(),
    },
    {
      id: 'collections',
      header: t('content.types.collections'),
      align: 'right',
      // Zero is the interesting value: a type nothing can hold is a plugin nobody has added.
      cell: (r) => <span className="tabular-nums text-muted-foreground">{r.collections}</span>,
      sortValue: (r) => r.collections,
    },
    {
      id: 'fields',
      header: t('content.types.fields'),
      cell: (r) => (
        <div className="flex flex-wrap gap-1">
          {r.fields.map((f) => (
            <Badge key={f.name} tone="secondary" title={f.description ?? f.type}>
              {f.name}
              <span className="ml-1 font-mono text-[0.625rem] opacity-70">{f.type}</span>
              {f.required ? <span className="ml-0.5 text-destructive">*</span> : null}
            </Badge>
          ))}
          {r.fields.length === 0 ? <span className="text-xs text-muted-foreground">—</span> : null}
        </div>
      ),
      sortValue: (r) => r.fields.length,
    },
  ];

  return (
    <Page className="max-w-7xl px-0 py-0">
      <DataTable
        rows={rows}
        columns={columns}
        rowKey={(r) => r.key}
        caption={t('content.types.title')}
        defaultSort={{ columnId: 'type' }}
        labels={{ loading: t('common.loading'), sortBy: (column) => t('common.sortBy', { column }) }}
        empty={
          <EmptyState
            icon={FileText}
            title={t('content.noCollections')}
            description={t('content.noCollectionsHint')}
          />
        }
      />
    </Page>
  );
}
