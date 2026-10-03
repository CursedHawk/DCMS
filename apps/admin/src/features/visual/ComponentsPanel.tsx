import type { Editor } from 'grapesjs';
import { ArrowUpCircle, Code2, FileCode, Pencil, Plus, Puzzle, Trash2, Wand2 } from 'lucide-react';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { toast } from 'sonner';
import { Button, Input, Label } from '@dcms/ui';
import { codeContractOf, codeSourcePath, tenantType } from '@dcms/site-runtime';
import { useSelected } from '../builder/panels/useEditorEvent';
import { useVfs } from '../site-source';
import { fromGrapes, SLOT_TYPE } from './canvas/tree';
import {
  componentUsage,
  createCodeComponent,
  createComponent,
  deleteComponent,
  listComponents,
  replaceNodeInFile,
  targetPath,
  updateAllInstances,
  versionToEdit,
} from './documents';
import { sameTarget, useVisual } from './store';

/**
 * The site's own components (P3): what exists, where each version is used, and the way in to
 * the composer — which is the same canvas, opened on the component's template.
 *
 * Opening a component that pages already use starts its next version (see `versionToEdit`): a
 * version in use never changes, so a page only looks different when someone updates it.
 */
export function ComponentsPanel({ editor }: { editor: Editor | null }) {
  const { t } = useTranslation();
  const files = useVfs((s) => s.files);
  const target = useVisual((s) => s.target);
  const setTarget = useVisual((s) => s.setTarget);
  const selected = useSelected(editor);
  const [label, setLabel] = useState('');
  const components = listComponents(files);
  const developer = Object.keys(files).map(codeContractOf).filter((n): n is string => n !== null).sort();

  // A developer component is code: it is edited in the code view, never on the canvas.
  const editSource = (name: string) => {
    useVisual.getState().setView('split');
    useVfs.getState().open(codeSourcePath(name));
  };
  const createCode = () => {
    if (!label.trim()) return;
    const { name } = createCodeComponent(label.trim());
    setLabel('');
    editSource(name);
  };

  const open = (name: string) => {
    const pick = versionToEdit(name);
    if (!pick) return;
    if (pick.created) toast.info(t('visual.mine.newVersion', { version: pick.version }));
    setTarget({ kind: 'component', name, version: pick.version });
  };

  const create = () => {
    if (!label.trim()) return;
    const result = createComponent(label.trim());
    if (!result.ok) return void toast.error(result.error);
    setLabel('');
    setTarget({ kind: 'component', name: result.name!, version: 1 });
  };

  // "Make component": the selected node (and everything in it) becomes v1 of a new component,
  // and the selection is replaced by an instance of it — the author keeps what they had built.
  const canMake = !!selected && selected.get('type') !== SLOT_TYPE && !!selected.parent() && target?.kind !== 'component';
  const makeFromSelection = () => {
    if (!selected) return;
    const name = window.prompt(t('visual.mine.namePrompt'), selected.getName());
    if (!name?.trim()) return;
    if (!target) return;
    // In the files, not on the canvas: creating a component re-registers the canvas and reloads
    // it, so a model swapped in now would be thrown away. Pending canvas edits go first.
    useVisual.getState().flushCanvas();
    const node = fromGrapes(selected);
    const result = createComponent(name.trim(), node);
    if (!result.ok) return void toast.error(result.error);
    replaceNodeInFile(targetPath(target), node.id, { id: node.id, type: tenantType(result.name!), version: 1 });
    toast.success(t('visual.mine.made', { label: name.trim() }));
  };

  return (
    <div className="flex h-full flex-col overflow-y-auto">
      <div className="border-b px-3 py-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">
        {t('visual.mine.title')}
      </div>

      <form
        className="space-y-2 border-b p-3"
        onSubmit={(e) => {
          e.preventDefault();
          create();
        }}
      >
        <Label htmlFor="new-component">{t('visual.mine.new')}</Label>
        <div className="flex gap-2">
          <Input id="new-component" value={label} placeholder={t('visual.mine.placeholder')} onChange={(e) => setLabel(e.target.value)} />
          <Button type="submit" size="icon" disabled={!label.trim()} aria-label={t('actions.create')}>
            <Plus className="h-4 w-4" />
          </Button>
        </div>
        <Button type="button" size="sm" variant="outline" className="w-full" disabled={!label.trim()} onClick={createCode}>
          <Code2 className="h-4 w-4" /> {t('visual.mine.newCode')}
        </Button>
        {canMake && (
          <Button type="button" size="sm" variant="outline" className="w-full" onClick={makeFromSelection}>
            <Wand2 className="h-4 w-4" /> {t('visual.mine.makeFromSelection')}
          </Button>
        )}
      </form>

      {components.length === 0 ? (
        <p className="p-4 text-sm text-muted-foreground">{t('visual.mine.none')}</p>
      ) : (
        <ul className="divide-y">
          {components.map(({ name, versions, latest }) => {
            const usage = componentUsage(name, files);
            const total = [...usage.values()].reduce((a, b) => a + b, 0);
            const outdated = [...usage.entries()].filter(([v]) => v !== latest.version).reduce((a, [, n]) => a + n, 0);
            const editing = target?.kind === 'component' && target.name === name;
            return (
              <li key={name} className={editing ? 'bg-muted' : undefined}>
                <div className="flex items-center gap-2 px-3 py-2">
                  <Puzzle className="h-4 w-4 shrink-0 text-muted-foreground" />
                  <div className="min-w-0 flex-1">
                    <div className="truncate text-sm font-medium">{latest.label}</div>
                    <div className="text-xs text-muted-foreground">
                      v{latest.version} · {t('visual.mine.used', { count: total })}
                      {versions.length > 1 ? ` · ${t('visual.mine.versions', { count: versions.length })}` : ''}
                    </div>
                  </div>
                  <Button size="icon" variant="ghost" title={t('visual.mine.edit')} onClick={() => open(name)}>
                    <Pencil className="h-4 w-4" />
                  </Button>
                  <Button
                    size="icon"
                    variant="ghost"
                    title={t('actions.delete')}
                    disabled={total > 0}
                    onClick={() => {
                      if (!window.confirm(t('visual.mine.confirmDelete', { label: latest.label }))) return;
                      const result = deleteComponent(name);
                      if (!result.ok) toast.error(result.error);
                      else if (editing) setTarget({ kind: 'shell' });
                    }}
                  >
                    <Trash2 className="h-4 w-4" />
                  </Button>
                </div>
                {outdated > 0 && (
                  <div className="flex items-center gap-2 px-3 pb-2 text-xs">
                    <span className="min-w-0 flex-1 text-amber-700 dark:text-amber-400">
                      {t('visual.mine.outdated', { count: outdated, version: latest.version })}
                    </span>
                    <Button
                      size="sm"
                      variant="outline"
                      onClick={() => {
                        // Leave the canvas on something that is not being rewritten underneath it.
                        if (sameTarget(target, { kind: 'component', name, version: latest.version })) setTarget({ kind: 'shell' });
                        const { updated, dropped } = updateAllInstances(name);
                        toast.success(
                          dropped.length
                            ? t('visual.mine.updatedDropped', { count: updated, dropped: dropped.join(', ') })
                            : t('visual.mine.updated', { count: updated }),
                        );
                      }}
                    >
                      <ArrowUpCircle className="h-4 w-4" /> {t('visual.mine.updateAll')}
                    </Button>
                  </div>
                )}
              </li>
            );
          })}
        </ul>
      )}
      {developer.length > 0 && (
        <>
          <div className="border-y px-3 py-2 text-xs font-semibold uppercase tracking-wide text-muted-foreground">{t('visual.mine.developer')}</div>
          <ul className="divide-y">
            {developer.map((name) => (
              <li key={name} className="flex items-center gap-2 px-3 py-2">
                <FileCode className="h-4 w-4 shrink-0 text-muted-foreground" />
                <span className="min-w-0 flex-1 truncate font-mono text-xs">{codeSourcePath(name)}</span>
                <Button size="icon" variant="ghost" title={t('visual.mine.editSource')} onClick={() => editSource(name)}>
                  <Pencil className="h-4 w-4" />
                </Button>
              </li>
            ))}
          </ul>
        </>
      )}
    </div>
  );
}
