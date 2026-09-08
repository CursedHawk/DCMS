import type { Locator } from '@playwright/test';

/**
 * Drags one element onto another using real HTML5 drag events.
 *
 * <p>`page.dragAndDrop` moves the mouse, which is right for a library that reimplements
 * dragging with pointer events but is flaky against native HTML5 DnD: the browser decides when
 * a press-and-move becomes a drag, and headless Chromium makes that decision differently often
 * enough to produce a test that fails once a week for no reason.</p>
 *
 * <p>The application's own contract is the `DragEvent` sequence and the `dataTransfer` payload
 * — `assetDragProps` sets `application/x-dcms-media`, `folderDropProps` reads it — so that is
 * what this dispatches. One `DataTransfer` is shared across the sequence, exactly as a browser
 * does, which is what makes the drop able to read what the drag start wrote.</p>
 */
export async function dragOnto(source: Locator, target: Locator): Promise<void> {
  const sourceHandle = await source.elementHandle();
  const targetHandle = await target.elementHandle();
  if (!sourceHandle || !targetHandle) throw new Error('dragOnto: source or target not attached');

  await source.page().evaluate(
    ([from, to]) => {
      const transfer = new DataTransfer();
      const fire = (element: Element, type: string) =>
        element.dispatchEvent(
          new DragEvent(type, { dataTransfer: transfer, bubbles: true, cancelable: true }),
        );

      fire(from as Element, 'dragstart');
      fire(to as Element, 'dragenter');
      fire(to as Element, 'dragover');
      fire(to as Element, 'drop');
      fire(from as Element, 'dragend');
    },
    [sourceHandle, targetHandle],
  );
}
