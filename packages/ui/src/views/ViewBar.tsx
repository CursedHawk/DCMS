import { Check, ChevronDown, MoreHorizontal, Star } from 'lucide-react';
import { useState } from 'react';
import { Button } from '../ui/button';
import { Checkbox } from '../ui/checkbox';
import { Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, DialogTitle } from '../ui/dialog';
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '../ui/dropdown-menu';
import { Input } from '../ui/input';
import { Label } from '../ui/label';
import { DEFAULT_VIEW_LABELS, type ViewLabels } from './labels';
import type { SavedView } from './model';

/**
 * Picks the view a table shows and saves what is on screen (#278): the table's own default, its
 * built-in views, the member's own and those shared with them. Save writes over one's own view,
 * Save as makes a new personal one; sharing, the default and deleting are in the "…" menu.
 */
export function ViewBar({
  views,
  currentId,
  defaultId,
  dirty,
  onSelect,
  onSave,
  onSaveAs,
  onDelete,
  onSetDefault,
  onShare,
  busy,
  labels: partial,
}: {
  views: readonly SavedView[];
  /** Null: the table's own default. */
  currentId: string | null;
  defaultId: string | null;
  /** What is on screen differs from the selected view. */
  dirty: boolean;
  onSelect: (id: string | null) => void;
  onSave: () => void;
  onSaveAs: (name: string) => void;
  onDelete: (id: string) => void;
  onSetDefault: (id: string | null) => void;
  onShare: (view: SavedView) => void;
  busy?: boolean;
  labels?: Partial<ViewLabels>;
}) {
  const labels = { ...DEFAULT_VIEW_LABELS, ...partial };
  const [naming, setNaming] = useState(false);
  const current = views.find((v) => v.id === currentId) ?? null;
  const groups: [string, SavedView[]][] = [
    [labels.builtInViews, views.filter((v) => v.system)],
    [labels.myViews, views.filter((v) => !v.system && v.mine)],
    [labels.sharedViews, views.filter((v) => !v.system && !v.mine)],
  ];

  const item = (id: string | null, name: string, hint?: string | null) => (
    <DropdownMenuItem key={id ?? '__default'} onSelect={() => onSelect(id)} className="min-w-0">
      <Check className={currentId === id ? 'h-4 w-4 shrink-0' : 'invisible h-4 w-4 shrink-0'} aria-hidden />
      <span className="min-w-0 flex-1 [overflow-wrap:anywhere]">
        {name}
        {hint ? <span className="block text-xs text-muted-foreground">{hint}</span> : null}
      </span>
      {defaultId === id && <Star className="h-3.5 w-3.5 shrink-0 text-amber-500" aria-label={labels.isDefault} />}
    </DropdownMenuItem>
  );

  return (
    <div className="flex min-w-0 flex-wrap items-center gap-1.5">
      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button size="sm" variant="outline" className="min-w-0 max-w-full" aria-label={`${labels.view}: ${current?.name ?? labels.defaultView}`}>
            <span className="min-w-0 truncate">{current?.name ?? labels.defaultView}</span>
            {dirty && <span className="shrink-0 text-xs text-muted-foreground">({labels.modified})</span>}
            <ChevronDown className="h-3.5 w-3.5 shrink-0" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="start" className="max-h-[70vh] w-72 overflow-y-auto">
          {item(null, labels.defaultView)}
          {groups.map(([title, list]) =>
            list.length === 0 ? null : (
              <div key={title}>
                <DropdownMenuSeparator />
                <DropdownMenuLabel className="text-xs font-normal text-muted-foreground">{title}</DropdownMenuLabel>
                {list.map((v) => item(v.id, v.name, !v.mine && v.owner ? labels.ownedBy(v.owner) : null))}
              </div>
            ),
          )}
        </DropdownMenuContent>
      </DropdownMenu>

      {current?.mine && dirty && (
        <Button size="sm" disabled={busy} onClick={onSave}>
          {labels.save}
        </Button>
      )}
      {dirty && (
        <Button size="sm" variant="ghost" disabled={busy} onClick={() => setNaming(true)}>
          {labels.saveAs}
        </Button>
      )}

      <DropdownMenu>
        <DropdownMenuTrigger asChild>
          <Button size="icon" variant="ghost" className="h-8 w-8" aria-label={labels.viewActions}>
            <MoreHorizontal className="h-4 w-4" />
          </Button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          {defaultId === currentId && currentId !== null ? (
            <DropdownMenuItem onSelect={() => onSetDefault(null)}>{labels.unsetDefault}</DropdownMenuItem>
          ) : (
            <DropdownMenuItem onSelect={() => onSetDefault(currentId)}>{labels.setDefault}</DropdownMenuItem>
          )}
          {!dirty && <DropdownMenuItem onSelect={() => setNaming(true)}>{labels.saveAs}</DropdownMenuItem>}
          {current?.mine && (
            <>
              <DropdownMenuItem onSelect={() => onShare(current)}>{labels.share}</DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem
                className="text-destructive"
                onSelect={() => {
                  if (window.confirm(labels.deleteConfirm(current.name))) onDelete(current.id);
                }}
              >
                {labels.deleteView}
              </DropdownMenuItem>
            </>
          )}
        </DropdownMenuContent>
      </DropdownMenu>

      {naming && (
        <SaveAsDialog
          labels={labels}
          initial={current && !current.system ? current.name : ''}
          onCancel={() => setNaming(false)}
          onSave={(name) => {
            setNaming(false);
            onSaveAs(name);
          }}
        />
      )}
    </div>
  );
}

function SaveAsDialog({
  labels,
  initial,
  onSave,
  onCancel,
}: {
  labels: ViewLabels;
  initial: string;
  onSave: (name: string) => void;
  onCancel: () => void;
}) {
  const [name, setName] = useState(initial);
  const ok = name.trim().length > 0 && name.trim().length <= 128;
  return (
    <Dialog open onOpenChange={(o) => !o && onCancel()}>
      <DialogContent>
        <form
          onSubmit={(e) => {
            e.preventDefault();
            if (ok) onSave(name.trim());
          }}
        >
          <DialogHeader>
            <DialogTitle>{labels.saveAsTitle}</DialogTitle>
          </DialogHeader>
          <DialogBody className="space-y-2">
            <Label htmlFor="view-name">{labels.name}</Label>
            <Input id="view-name" value={name} maxLength={128} autoFocus onChange={(e) => setName(e.target.value)} />
          </DialogBody>
          <DialogFooter>
            <Button type="button" variant="ghost" onClick={onCancel}>
              {labels.cancel}
            </Button>
            <Button type="submit" disabled={!ok}>
              {labels.save}
            </Button>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  );
}

export interface ViewSharing {
  workspace: boolean;
  userIds: string[];
  roleIds: string[];
}

/** Who else sees a view: the workspace, or the people and roles ticked. Read-only to them. */
export function ShareViewDialog({
  people,
  roles,
  value,
  onSave,
  onClose,
  busy,
  labels: partial,
}: {
  people: readonly { id: string; label: string }[];
  roles: readonly { id: string; label: string }[];
  value: ViewSharing;
  onSave: (next: ViewSharing) => void;
  onClose: () => void;
  busy?: boolean;
  labels?: Partial<ViewLabels>;
}) {
  const labels = { ...DEFAULT_VIEW_LABELS, ...partial };
  const [draft, setDraft] = useState(value);
  const [search, setSearch] = useState('');
  const match = (s: string) => s.toLowerCase().includes(search.trim().toLowerCase());
  const toggle = (key: 'userIds' | 'roleIds', id: string, on: boolean) =>
    setDraft((d) => ({ ...d, [key]: on ? [...d[key], id] : d[key].filter((x) => x !== id) }));

  const list = (title: string, key: 'userIds' | 'roleIds', items: readonly { id: string; label: string }[]) => (
    <fieldset className="min-w-0 space-y-1" disabled={draft.workspace}>
      <legend className="mb-1 text-sm font-medium">{title}</legend>
      {items.filter((x) => match(x.label)).map((x) => (
        <label key={x.id} className="flex min-w-0 items-center gap-2 text-sm">
          <Checkbox checked={draft.workspace || draft[key].includes(x.id)} onCheckedChange={(on) => toggle(key, x.id, on === true)} />
          <span className="min-w-0 [overflow-wrap:anywhere]">{x.label}</span>
        </label>
      ))}
    </fieldset>
  );

  return (
    <Dialog open onOpenChange={(o) => !o && onClose()}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>{labels.shareTitle}</DialogTitle>
        </DialogHeader>
        <DialogBody className="space-y-3">
          <p className="text-sm text-muted-foreground">{labels.shareWorkspaceHint}</p>
          <label className="flex items-center gap-2 text-sm font-medium">
            <Checkbox checked={draft.workspace} onCheckedChange={(on) => setDraft((d) => ({ ...d, workspace: on === true }))} />
            {labels.shareWorkspace}
          </label>
          <Input value={search} onChange={(e) => setSearch(e.target.value)} placeholder={labels.search} aria-label={labels.search} disabled={draft.workspace} />
          <div className="grid max-h-72 grid-cols-1 gap-4 overflow-y-auto sm:grid-cols-2">
            {list(labels.sharePeople, 'userIds', people)}
            {list(labels.shareRoles, 'roleIds', roles)}
          </div>
        </DialogBody>
        <DialogFooter>
          <Button variant="ghost" onClick={onClose}>
            {labels.cancel}
          </Button>
          <Button disabled={busy} onClick={() => onSave(draft)}>
            {labels.save}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
