import type { Editor } from 'grapesjs';

/**
 * Absolute positioning, scoped to an opt-in container.
 *
 * GrapesJS's `dragMode` is an editor-wide setting, so switching it globally
 * would make every page absolutely positioned — which is what the old Mode A
 * editor did and why its pages did not reflow. Instead the mode is toggled per
 * drag, based on whether the component being moved sits inside a Free canvas:
 * everything outside one keeps normal flow behaviour.
 */
export const FREE_CANVAS_CLASS = 'dcms-free-canvas';

export function dcmsFreeCanvas(editor: Editor): void {
  const insideFreeCanvas = (component: unknown): boolean => {
    let current = component as { parent?: () => unknown; getClasses?: () => string[] } | undefined;
    // Walk up rather than checking only the parent: a component nested three
    // levels inside a Free canvas is still positioned freely.
    while (current) {
      if (current.getClasses?.().includes(FREE_CANVAS_CLASS)) return true;
      current = current.parent?.() as typeof current;
    }
    return false;
  };

  const apply = (component: unknown) => {
    editor.setDragMode(insideFreeCanvas(component) ? 'absolute' : '');
  };

  editor.on('component:selected', apply);
  // A drag can start on a component that was never selected (drop from the
  // palette straight into a Free canvas), so the mode is set again on drag start.
  editor.on('sorter:drag:start', apply);
}
