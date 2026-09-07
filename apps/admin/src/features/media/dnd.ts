import type { DragEvent } from 'react';

/**
 * Dragging assets onto folders.
 *
 * <p>Moving an asset used to mean selecting it, opening a dropdown and picking a folder from a
 * list — in a view that is otherwise a file manager, with folder tiles sitting right there.</p>
 *
 * <p>Native HTML5 drag and drop, not a library. The IDE's file tree already does exactly this
 * (`FileTree.tsx`), including the part that is easy to get wrong: a **custom MIME type** that
 * marks a drag as internal, so the uploader's drop zone can stand aside instead of trying to
 * upload the thing you are merely moving. A drag-and-drop library would be ~40 KB to re-solve a
 * problem this codebase has already solved once.</p>
 */
export const MEDIA_DND = 'application/x-dcms-media';

/** Whether a drag came from inside the library, rather than from the operating system. */
export function isInternalDrag(event: DragEvent): boolean {
  return event.dataTransfer.types.includes(MEDIA_DND);
}

/**
 * The asset ids a drop is carrying.
 *
 * Returns an empty array for an external drag — files from the desktop — which the caller
 * should hand to the uploader instead.
 */
export function draggedIds(event: DragEvent): string[] {
  const raw = event.dataTransfer.getData(MEDIA_DND);
  if (!raw) return [];
  try {
    const parsed: unknown = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.filter((id): id is string => typeof id === 'string') : [];
  } catch {
    return [];
  }
}

/**
 * What a drag of `id` should carry, given the current selection.
 *
 * <p>Dragging one of several selected assets moves the whole selection; dragging an unselected
 * one moves only it. That is what every file manager does, and getting it backwards — moving
 * only the dragged item while five others sit selected — is the kind of surprise that costs
 * somebody a re-sort of their library.</p>
 */
export function dragPayload(id: string, selected: ReadonlySet<string>): string[] {
  return selected.has(id) ? [...selected] : [id];
}

export function assetDragProps(id: string, selected: ReadonlySet<string>) {
  return {
    draggable: true,
    onDragStart: (event: DragEvent) => {
      const ids = dragPayload(id, selected);
      event.dataTransfer.effectAllowed = 'move';
      event.dataTransfer.setData(MEDIA_DND, JSON.stringify(ids));
    },
  };
}

/**
 * Drop-target props for a folder.
 *
 * `preventDefault` on dragover is what actually permits a drop — without it the browser
 * refuses, silently, and the target looks broken rather than unsupported.
 */
export function folderDropProps({
  onDropAssets,
  onOver,
  onLeave,
}: {
  onDropAssets: (ids: string[]) => void;
  onOver?: () => void;
  onLeave?: () => void;
}) {
  return {
    onDragOver: (event: DragEvent) => {
      if (!isInternalDrag(event)) return;
      event.preventDefault();
      event.dataTransfer.dropEffect = 'move';
      onOver?.();
    },
    onDragLeave: () => onLeave?.(),
    onDrop: (event: DragEvent) => {
      if (!isInternalDrag(event)) return;
      event.preventDefault();
      event.stopPropagation();
      onLeave?.();
      const ids = draggedIds(event);
      if (ids.length > 0) onDropAssets(ids);
    },
  };
}
