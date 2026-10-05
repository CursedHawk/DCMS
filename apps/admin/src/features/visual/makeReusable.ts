import { tenantType } from '@dcms/site-runtime';
import type { Component } from 'grapesjs';
import type { TFunction } from 'i18next';
import { toast } from 'sonner';
import { SLOT_TYPE, fromGrapes } from './canvas/tree';
import { createComponent, replaceNodeInFile, targetPath } from './documents';
import { useVisual } from './store';

/** Can this selection become one of the site's own components? Not inside a component's own template. */
export function canMakeReusable(selected: Component | undefined): selected is Component {
  return !!selected && selected.get('type') !== SLOT_TYPE && !!selected.parent() && useVisual.getState().target?.kind !== 'component';
}

/**
 * Make the selection a component: v1 is written, the selection replaced by an instance of it,
 * and the studio opened on it.
 */
export function makeReusable(selected: Component, t: TFunction): void {
  const target = useVisual.getState().target;
  if (!target) return;
  const name = window.prompt(t('visual.mine.namePrompt'), selected.getName());
  if (!name?.trim()) return;
  // In the files, not on the canvas: creating a component re-registers the canvas and reloads
  // it, so a model swapped in now would be thrown away. Pending canvas edits go first.
  useVisual.getState().flushCanvas();
  const node = fromGrapes(selected);
  const result = createComponent(name.trim(), node);
  if (!result.ok) return void toast.error(result.error);
  replaceNodeInFile(targetPath(target), node.id, { id: node.id, type: tenantType(result.name!), version: 1 });
  toast.success(t('visual.mine.made', { label: name.trim() }));
  // Straight into the studio, to decide what pages may change; Done comes back to the instance.
  useVisual.getState().setTarget({ kind: 'component', name: result.name!, version: 1 });
}
