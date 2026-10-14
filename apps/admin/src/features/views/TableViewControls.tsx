import { useQuery } from '@tanstack/react-query';
import { Filter } from 'lucide-react';
import { useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import {
  Badge,
  Button,
  ColumnChooser,
  FilterBuilder,
  ShareViewDialog,
  ViewBar,
  countConditions,
  toastApiError,
  type SavedView,
  type UiOp,
  type ViewField,
  type ViewLabels,
} from '@dcms/ui';
import { viewsApi, type TableViews } from './useTableViews';

/** The view kit's words, from the console's locale. */
export function useViewLabels(): ViewLabels {
  const { t } = useTranslation();
  return useMemo(() => {
    const ops: UiOp[] = ['eq', 'ne', 'gt', 'gte', 'lt', 'lte', 'in', 'nin', 'contains', 'startsWith', 'isEmpty', 'isNotEmpty'];
    const w = (k: string) => t(`views.${k}`);
    return {
      matchAll: w('matchAll'),
      matchAny: w('matchAny'),
      matchPrefix: w('matchPrefix'),
      addCondition: w('addCondition'),
      addGroup: w('addGroup'),
      removeCondition: w('removeCondition'),
      removeGroup: w('removeGroup'),
      field: w('field'),
      operator: w('operator'),
      value: w('value'),
      yes: w('yes'),
      no: w('no'),
      noConditions: w('noConditions'),
      op: Object.fromEntries(ops.map((o) => [o, t(`views.op.${o}`)])) as Record<UiOp, string>,
      columns: w('columns'),
      showColumn: (label) => t('views.showColumn', { label }),
      moveUp: (label) => t('views.moveUp', { label }),
      moveDown: (label) => t('views.moveDown', { label }),
      filters: w('filters'),
      clear: w('clear'),
      view: w('view'),
      defaultView: w('defaultView'),
      myViews: w('myViews'),
      sharedViews: w('sharedViews'),
      builtInViews: w('builtInViews'),
      modified: w('modified'),
      save: w('save'),
      saveAs: w('saveAs'),
      saveAsTitle: w('saveAsTitle'),
      name: w('name'),
      cancel: w('cancel'),
      share: w('share'),
      shareTitle: w('shareTitle'),
      shareWorkspace: w('shareWorkspace'),
      shareWorkspaceHint: w('shareWorkspaceHint'),
      sharePeople: w('sharePeople'),
      shareRoles: w('shareRoles'),
      search: w('search'),
      setDefault: w('setDefault'),
      unsetDefault: w('unsetDefault'),
      isDefault: w('isDefault'),
      deleteView: w('deleteView'),
      deleteConfirm: (name) => t('views.deleteConfirm', { name }),
      ownedBy: (owner) => t('views.ownedBy', { owner }),
      viewActions: w('viewActions'),
    };
  }, [t]);
}

/**
 * A table's view controls (#278): which view, its filters (in a panel under the bar), its
 * columns, saving and sharing. The table itself reads `views.definition`.
 */
export function TableViewControls({ views, fields }: { views: TableViews; fields: ViewField[] }) {
  const { t } = useTranslation();
  const labels = useViewLabels();
  const [filtering, setFiltering] = useState(false);
  const [sharing, setSharing] = useState<SavedView | null>(null);
  const count = countConditions(views.definition.filter);
  const done = (message: string) => () => toast.success(message);
  const failed = (e: unknown) => toastApiError(e, t);

  return (
    <div className="mb-3 space-y-2">
      <div className="flex min-w-0 flex-wrap items-center gap-2">
        <ViewBar
          views={views.views}
          currentId={views.currentId}
          defaultId={views.defaultId}
          dirty={views.dirty}
          busy={views.save.isPending || views.saveAs.isPending}
          labels={labels}
          onSelect={views.select}
          onSave={() => views.save.mutate(undefined, { onSuccess: done(t('views.saved')), onError: failed })}
          onSaveAs={(name) => views.saveAs.mutate(name, { onSuccess: done(t('views.saved')), onError: failed })}
          onDelete={(id) => views.remove.mutate(id, { onError: failed })}
          onSetDefault={(id) => views.setDefault.mutate(id, { onSuccess: done(t('views.defaultSet')), onError: failed })}
          onShare={setSharing}
        />
        <Button size="sm" variant={filtering ? 'secondary' : 'outline'} aria-expanded={filtering} onClick={() => setFiltering((f) => !f)}>
          <Filter className="h-4 w-4" /> {labels.filters}
          {count > 0 && <Badge tone="default">{count}</Badge>}
        </Button>
        <ColumnChooser
          fields={fields}
          value={views.definition.columns}
          labels={labels}
          onChange={(columns) => views.setDefinition((d) => ({ ...d, columns }))}
        />
      </div>
      {filtering && (
        <section aria-label={labels.filters} className="rounded-md border bg-muted/20 p-3">
          <FilterBuilder
            fields={fields}
            value={views.definition.filter}
            labels={labels}
            onChange={(filter) => views.setDefinition((d) => ({ ...d, filter }))}
          />
          {count > 0 && (
            <Button size="sm" variant="ghost" className="mt-2" onClick={() => views.setDefinition((d) => ({ ...d, filter: null }))}>
              {labels.clear}
            </Button>
          )}
        </section>
      )}
      {sharing && (
        <ShareDialog
          view={sharing}
          labels={labels}
          busy={views.share.isPending}
          onClose={() => setSharing(null)}
          onSave={(next) =>
            views.share.mutate(
              { id: sharing.id, sharing: next },
              { onSuccess: () => { setSharing(null); toast.success(t('views.shared')); }, onError: failed },
            )
          }
        />
      )}
    </div>
  );
}

function ShareDialog({
  view,
  labels,
  busy,
  onClose,
  onSave,
}: {
  view: SavedView;
  labels: ViewLabels;
  busy: boolean;
  onClose: () => void;
  onSave: (next: { workspace: boolean; userIds: string[]; roleIds: string[] }) => void;
}) {
  const audience = useQuery({ queryKey: ['view-audience'], queryFn: viewsApi.audience });
  if (!audience.data) return null;
  return (
    <ShareViewDialog
      people={audience.data.people}
      roles={audience.data.roles}
      value={{
        workspace: view.sharedWithWorkspace ?? false,
        userIds: view.sharedWith?.userIds ?? [],
        roleIds: view.sharedWith?.roleIds ?? [],
      }}
      labels={labels}
      busy={busy}
      onClose={onClose}
      onSave={onSave}
    />
  );
}
