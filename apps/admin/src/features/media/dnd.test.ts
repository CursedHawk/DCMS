import { describe, expect, it, vi } from 'vitest';
import type { DragEvent } from 'react';
import { MEDIA_DND, dragPayload, draggedIds, folderDropProps, isInternalDrag } from './dnd';

function event(types: string[], data: Record<string, string> = {}) {
  return {
    dataTransfer: {
      types,
      getData: (type: string) => data[type] ?? '',
      dropEffect: '',
      effectAllowed: '',
    },
    preventDefault: vi.fn(),
    stopPropagation: vi.fn(),
  } as unknown as DragEvent & { preventDefault: ReturnType<typeof vi.fn> };
}

describe('isInternalDrag', () => {
  it('recognises a drag that started inside the library', () => {
    expect(isInternalDrag(event([MEDIA_DND]))).toBe(true);
  });

  it('does not claim a file dragged in from the desktop', () => {
    // This is the whole reason for a custom MIME type: the uploader's drop zone has to be able
    // to tell "upload this" from "move this", and both are drops onto the same page.
    expect(isInternalDrag(event(['Files']))).toBe(false);
  });
});

describe('dragPayload', () => {
  it('drags the whole selection when the dragged asset is part of it', () => {
    expect(dragPayload('b', new Set(['a', 'b', 'c'])).sort()).toEqual(['a', 'b', 'c']);
  });

  it('drags only the asset when it is not selected', () => {
    // Every file manager behaves this way. Getting it backwards — moving one item while five
    // others sit selected — costs somebody a re-sort of their library.
    expect(dragPayload('z', new Set(['a', 'b']))).toEqual(['z']);
  });

  it('drags one asset when nothing is selected', () => {
    expect(dragPayload('a', new Set())).toEqual(['a']);
  });
});

describe('draggedIds', () => {
  it('reads the ids back', () => {
    expect(draggedIds(event([MEDIA_DND], { [MEDIA_DND]: '["a","b"]' }))).toEqual(['a', 'b']);
  });

  it('returns nothing for an external drag', () => {
    expect(draggedIds(event(['Files']))).toEqual([]);
  });

  it('survives a payload that is not what we wrote', () => {
    // Another application can put anything under any MIME type it likes.
    expect(draggedIds(event([MEDIA_DND], { [MEDIA_DND]: 'not json' }))).toEqual([]);
    expect(draggedIds(event([MEDIA_DND], { [MEDIA_DND]: '{"a":1}' }))).toEqual([]);
    expect(draggedIds(event([MEDIA_DND], { [MEDIA_DND]: '[1,2]' }))).toEqual([]);
  });
});

describe('folderDropProps', () => {
  it('permits the drop, which requires preventDefault on dragover', () => {
    // Without it the browser refuses the drop silently and the target looks broken.
    const props = folderDropProps({ onDropAssets: () => {} });
    const e = event([MEDIA_DND]);
    props.onDragOver(e);
    expect(e.preventDefault).toHaveBeenCalled();
    expect(e.dataTransfer.dropEffect).toBe('move');
  });

  it('ignores an external drag entirely, so the uploader gets it', () => {
    const onDropAssets = vi.fn();
    const props = folderDropProps({ onDropAssets });
    const e = event(['Files']);
    props.onDragOver(e);
    props.onDrop(e);
    expect(e.preventDefault).not.toHaveBeenCalled();
    expect(onDropAssets).not.toHaveBeenCalled();
  });

  it('reports the dropped ids', () => {
    const onDropAssets = vi.fn();
    folderDropProps({ onDropAssets }).onDrop(event([MEDIA_DND], { [MEDIA_DND]: '["a"]' }));
    expect(onDropAssets).toHaveBeenCalledWith(['a']);
  });

  it('does not fire for an internal drag carrying nothing', () => {
    const onDropAssets = vi.fn();
    folderDropProps({ onDropAssets }).onDrop(event([MEDIA_DND], { [MEDIA_DND]: '[]' }));
    expect(onDropAssets).not.toHaveBeenCalled();
  });

  it('clears the hover state on both leave and drop', () => {
    const onLeave = vi.fn();
    const props = folderDropProps({ onDropAssets: () => {}, onLeave });
    props.onDragLeave();
    props.onDrop(event([MEDIA_DND], { [MEDIA_DND]: '["a"]' }));
    expect(onLeave).toHaveBeenCalledTimes(2);
  });
});
