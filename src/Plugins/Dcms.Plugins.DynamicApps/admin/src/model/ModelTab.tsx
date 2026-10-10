import { useEffect, useMemo, useState } from 'react';
import { Pencil, Plus, Trash2 } from 'lucide-react';
import { Badge, Button, DataTable, EmptyState, type Column } from '@dcms/ui';
import { ConfirmDialog } from '../ConfirmDialog';
import { usePluginT } from '@dcms/plugin-ui';
import {
  useApplyChanges,
  usePublished,
  type AppConfig,
  type ChangeOperation,
  type ChoiceSetDef,
  type FieldDef,
  type IndexDef,
  type RelationshipDef,
  type TableDef,
  type ViewDef,
} from '../api';
import { ChoiceSetDialog, FieldDialog, IndexDialog, RelationshipDialog, TableDialog, ViewDialog } from './dialogs';

type Editing =
  | { kind: 'table'; table?: TableDef }
  | { kind: 'field'; table: TableDef; field?: FieldDef }
  | { kind: 'index'; table: TableDef; index?: IndexDef }
  | { kind: 'view'; table: TableDef; view?: ViewDef }
  | { kind: 'relationship'; relationship?: RelationshipDef }
  | { kind: 'choiceSet'; set?: ChoiceSetDef };

type Deleting = { op: ChangeOperation; name: string } | null;

/** Tables and what hangs off them, as the draft (or, before one is opened, the live app) has them. */
export function ModelTab({ slug, config, editable, onSelect }: {
  slug: string;
  config: AppConfig | null;
  editable: boolean;
  onSelect: (id: string | null) => void;
}) {
  const { t } = usePluginT();
  const apply = useApplyChanges(slug);
  // What is live, for "copy values from" when a new field or lookup replaces a live field.
  const liveTables = usePublished(slug).data?.config.tables ?? [];
  const [selected, setSelected] = useState<string | null>(null);
  const [editing, setEditing] = useState<Editing | null>(null);
  const [deleting, setDeleting] = useState<Deleting>(null);

  const tables = config?.tables ?? [];
  const table = tables.find((x) => x.id === selected) ?? tables[0] ?? null;
  useEffect(() => onSelect(table?.id ?? null), [table?.id, onSelect]);

  const tableName = (id: string) => tables.find((x) => x.id === id)?.apiName ?? id;
  const choiceName = (id?: string) => config?.choiceSets.find((c) => c.id === id)?.apiName;

  const save = (operations: ChangeOperation[]) =>
    apply.mutate({ operations }, { onSuccess: () => setEditing(null) });

  const fieldColumns = useMemo<Column<FieldDef>[]>(() => [
    {
      id: 'name',
      header: t('fields.name'),
      primary: true,
      cell: (f) => (
        <div>
          <div className="font-medium">{f.displayName}</div>
          <div className="font-mono text-xs text-muted-foreground">{f.apiName}</div>
        </div>
      ),
    },
    {
      id: 'type',
      header: t('fields.type'),
      cell: (f) => (
        <span>
          {t(`types.${f.type}`)}
          {f.choiceSetId ? <span className="text-muted-foreground"> ({choiceName(f.choiceSetId)})</span> : null}
        </span>
      ),
    },
    {
      id: 'flags',
      header: t('fields.flags'),
      cell: (f) => (
        <div className="flex flex-wrap gap-1">
          {f.id === table?.primaryFieldId ? <Badge tone="secondary">{t('flags.primary')}</Badge> : null}
          {f.required ? <Badge tone="outline">{t('flags.required')}</Badge> : null}
          {f.unique ? <Badge tone="outline">{t('flags.unique')}</Badge> : null}
          {f.readOnly ? <Badge tone="outline">{t('flags.readOnly')}</Badge> : null}
          {f.hiddenFromPublic ? <Badge tone="outline">{t('flags.hidden')}</Badge> : null}
          {f.deprecated ? <Badge tone="outline">{t('flags.deprecated')}</Badge> : null}
        </div>
      ),
    },
    {
      id: 'actions',
      header: '',
      align: 'right',
      hideOnCard: true,
      cell: (f) => editable && table ? (
        <div className="flex justify-end gap-1">
          <Button variant="ghost" size="icon" aria-label={t('actions.edit')} onClick={() => setEditing({ kind: 'field', table, field: f })}>
            <Pencil className="size-4" />
          </Button>
          <Button variant="ghost" size="icon" aria-label={t('actions.delete')}
            onClick={() => setDeleting({ op: { op: 'delete', type: 'field', target: f.id }, name: `${table.apiName}.${f.apiName}` })}>
            <Trash2 className="size-4" />
          </Button>
        </div>
      ) : null,
    },
  ], [t, table, editable, config]); // eslint-disable-line react-hooks/exhaustive-deps

  // A value, not a component defined in here: a component type re-created on every render
  // would remount the open dialog and lose what the operator typed.
  const dialogs = (
      <>
        {editing?.kind === 'table' ? (
          <TableDialog table={editing.table} pending={apply.isPending} onCancel={() => setEditing(null)} onSave={save} />
        ) : null}
        {editing?.kind === 'field' && config ? (
          <FieldDialog table={editing.table} field={editing.field} choiceSets={config.choiceSets} liveTables={liveTables} pending={apply.isPending}
            onCancel={() => setEditing(null)} onSave={save} />
        ) : null}
        {editing?.kind === 'index' ? (
          <IndexDialog table={editing.table} index={editing.index} pending={apply.isPending} onCancel={() => setEditing(null)} onSave={save} />
        ) : null}
        {editing?.kind === 'view' && config ? (
          <ViewDialog table={editing.table} view={editing.view} relationships={config.relationships} pending={apply.isPending}
            onCancel={() => setEditing(null)} onSave={save} />
        ) : null}
        {editing?.kind === 'relationship' && config ? (
          <RelationshipDialog relationship={editing.relationship} tables={config.tables} liveTables={liveTables} pending={apply.isPending}
            onCancel={() => setEditing(null)} onSave={save} />
        ) : null}
        {editing?.kind === 'choiceSet' ? (
          <ChoiceSetDialog set={editing.set} pending={apply.isPending} onCancel={() => setEditing(null)} onSave={save} />
        ) : null}
        <ConfirmDialog
          open={deleting !== null}
          onOpenChange={(open) => !open && setDeleting(null)}
          title={t('model.deleteTitle', { name: deleting?.name })}
          description={t('model.deleteHint')}
          confirmLabel={t('actions.delete')}
          pending={apply.isPending}
          onConfirm={() => deleting && apply.mutate({ operations: [deleting.op] }, { onSuccess: () => setDeleting(null) })}
        />
      </>
  );

  if (!config || tables.length === 0) {
    return (
      <div className="space-y-6 pt-2">
        <EmptyState
          title={t('model.emptyTitle')}
          description={t('model.emptyHint')}
          action={editable ? <Button onClick={() => setEditing({ kind: 'table' })}><Plus className="size-4" /> {t('model.newTable')}</Button> : undefined}
        />
        {dialogs}
      </div>
    );
  }

  const relationships = config.relationships;
  const views = config.views.filter((v) => v.tableId === table?.id);

  return (
    <div className="grid grid-cols-1 gap-6 pt-2 lg:grid-cols-[14rem_minmax(0,1fr)]">
      <nav aria-label={t('model.tables')} className="space-y-1">
        <div className="flex items-center justify-between px-1 pb-1">
          <h3 className="text-sm font-semibold">{t('model.tables')}</h3>
          {editable ? (
            <Button variant="ghost" size="icon" aria-label={t('model.newTable')} onClick={() => setEditing({ kind: 'table' })}>
              <Plus className="size-4" />
            </Button>
          ) : null}
        </div>
        {tables.map((x) => (
          <button
            key={x.id}
            type="button"
            onClick={() => setSelected(x.id)}
            aria-current={x.id === table?.id ? 'true' : undefined}
            className={`w-full rounded-md px-2 py-1.5 text-left text-sm hover:bg-muted ${x.id === table?.id ? 'bg-muted font-medium' : ''}`}
          >
            <div>{x.pluralName ?? x.displayName}</div>
            <div className="font-mono text-xs text-muted-foreground">{x.apiName} · {x.fields.length}</div>
          </button>
        ))}
      </nav>

      {table ? (
        <div className="min-w-0 space-y-6">
          <section className="space-y-2">
            <div className="flex flex-wrap items-center gap-2">
              <h3 className="text-lg font-semibold">{table.displayName}</h3>
              <span className="font-mono text-sm text-muted-foreground">{table.apiName}</span>
              {!table.enabled ? <Badge tone="outline">{t('model.disabled')}</Badge> : null}
              {editable ? (
                <div className="ml-auto flex gap-2">
                  <Button variant="outline" size="sm" onClick={() => setEditing({ kind: 'table', table })}>
                    <Pencil className="size-4" /> {t('actions.edit')}
                  </Button>
                  <Button variant="outline" size="sm"
                    onClick={() => setDeleting({ op: { op: 'delete', type: 'table', target: table.id }, name: table.apiName })}>
                    <Trash2 className="size-4" /> {t('actions.delete')}
                  </Button>
                  <Button size="sm" onClick={() => setEditing({ kind: 'field', table })}>
                    <Plus className="size-4" /> {t('model.newField')}
                  </Button>
                </div>
              ) : null}
            </div>
            {table.description ? <p className="text-sm text-muted-foreground">{table.description}</p> : null}
            <DataTable rows={table.fields} columns={fieldColumns} rowKey={(f) => f.id} empty={t('model.noFields')} />
          </section>

          <Section title={t('model.indexes')} onAdd={editable ? () => setEditing({ kind: 'index', table }) : undefined}>
            {table.indexes.length === 0 ? <Muted>{t('model.noIndexes')}</Muted> : table.indexes.map((index) => (
              <Item key={index.id}
                title={index.apiName}
                detail={`${index.fieldIds.map((id) => table.fields.find((f) => f.id === id)?.apiName ?? id).join(', ')}${index.unique ? ` · ${t('flags.unique')}` : ''}`}
                onEdit={editable ? () => setEditing({ kind: 'index', table, index }) : undefined}
                onDelete={editable ? () => setDeleting({ op: { op: 'delete', type: 'index', target: index.id }, name: `${table.apiName}.${index.apiName}` }) : undefined}
              />
            ))}
          </Section>

          <Section title={t('model.views')} onAdd={editable ? () => setEditing({ kind: 'view', table }) : undefined}>
            {views.length === 0 ? <Muted>{t('model.noViews')}</Muted> : views.map((view) => (
              <Item key={view.id}
                title={`${view.displayName}${view.isDefault ? ` · ${t('model.defaultView')}` : ''}`}
                detail={view.apiName}
                onEdit={editable ? () => setEditing({ kind: 'view', table, view }) : undefined}
                onDelete={editable ? () => setDeleting({ op: { op: 'delete', type: 'view', target: view.id }, name: `${table.apiName}.${view.apiName}` }) : undefined}
              />
            ))}
          </Section>
        </div>
      ) : null}

      <div className="space-y-6 lg:col-span-2">
        <Section title={t('model.relationships')} onAdd={editable ? () => setEditing({ kind: 'relationship' }) : undefined}>
          {relationships.length === 0 ? <Muted>{t('model.noRelationships')}</Muted> : relationships.map((r) => (
            <Item key={r.id}
              title={`${tableName(r.sourceTableId)}.${r.apiName} → ${tableName(r.targetTableId)}`}
              detail={`${t(`kinds.rel.${r.kind}`)}${r.inverseApiName ? ` · ${tableName(r.targetTableId)}.${r.inverseApiName}` : ''}${r.required ? ` · ${t('flags.required')}` : ''} · ${t(`onDelete.${r.onDelete}`)}`}
              onEdit={editable ? () => setEditing({ kind: 'relationship', relationship: r }) : undefined}
              onDelete={editable ? () => setDeleting({ op: { op: 'delete', type: 'relationship', target: r.id }, name: `${tableName(r.sourceTableId)}.${r.apiName}` }) : undefined}
            />
          ))}
        </Section>

        <Section title={t('model.choiceSets')} onAdd={editable ? () => setEditing({ kind: 'choiceSet' }) : undefined}>
          {config.choiceSets.length === 0 ? <Muted>{t('model.noChoiceSets')}</Muted> : config.choiceSets.map((c) => (
            <Item key={c.id}
              title={`${c.displayName} (${c.apiName})`}
              detail={c.options.map((o) => o.label).join(', ')}
              onEdit={editable ? () => setEditing({ kind: 'choiceSet', set: c }) : undefined}
              onDelete={editable ? () => setDeleting({ op: { op: 'delete', type: 'choiceSet', target: c.id }, name: c.apiName }) : undefined}
            />
          ))}
        </Section>
      </div>

      {dialogs}
    </div>
  );
}

export function Section({ title, onAdd, children }: { title: string; onAdd?: () => void; children: React.ReactNode }) {
  const { t } = usePluginT();
  return (
    <section className="space-y-2">
      <div className="flex items-center justify-between">
        <h3 className="text-sm font-semibold">{title}</h3>
        {onAdd ? (
          <Button variant="outline" size="sm" onClick={onAdd}>
            <Plus className="size-4" /> {t('actions.add')}
          </Button>
        ) : null}
      </div>
      <div className="divide-y rounded-md border">{children}</div>
    </section>
  );
}

export function Item({ title, detail, onEdit, onDelete }: { title: string; detail?: string; onEdit?: () => void; onDelete?: () => void }) {
  const { t } = usePluginT();
  return (
    <div className="flex items-center gap-2 px-3 py-2 text-sm">
      <div className="min-w-0 flex-1">
        <div className="truncate font-medium">{title}</div>
        {detail ? <div className="truncate text-xs text-muted-foreground">{detail}</div> : null}
      </div>
      {onEdit ? (
        <Button variant="ghost" size="icon" aria-label={t('actions.edit')} onClick={onEdit}><Pencil className="size-4" /></Button>
      ) : null}
      {onDelete ? (
        <Button variant="ghost" size="icon" aria-label={t('actions.delete')} onClick={onDelete}><Trash2 className="size-4" /></Button>
      ) : null}
    </div>
  );
}

export function Muted({ children }: { children: React.ReactNode }) {
  return <p className="px-3 py-2 text-sm text-muted-foreground">{children}</p>;
}
