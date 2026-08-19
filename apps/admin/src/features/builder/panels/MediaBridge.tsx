import type { Editor } from 'grapesjs';
import { useEffect } from 'react';
import { assetIdOf, knownMediaUrl, resolveMedia } from './media';

/**
 * Makes the media an author placed actually appear in the canvas.
 *
 * The repo stores an asset as its published URL (`/api/media/<id>/original`),
 * which resolves on the live site and nowhere in the admin — see `media.ts`.
 * This walks the canvas document for elements pointing at one and swaps in the
 * authenticated blob URL.
 *
 * **Only the DOM is touched, never the model.** The model is what gets
 * serialized and committed, so writing a `blob:` URL into it would put a URL
 * valid for one browser tab into the site's git history and publish a page whose
 * images 404 for every visitor. This is the same discipline `PreviewBridge`
 * follows, and for the same reason — which is also why the swap has to be redone
 * after every canvas change: GrapesJS re-renders from the model and legitimately
 * restores the published URL.
 *
 * The original is kept in `data-dcms-src` so a second pass recognises an element
 * it already handled instead of treating the blob URL as a new source.
 */

const ORIGINAL_ATTR = 'data-dcms-src';

/** Attributes that hold a URL the canvas has to be able to load. */
const URL_ATTRS = ['src', 'poster'] as const;

export function MediaBridge({ editor }: { editor: Editor | null }) {
  useEffect(() => {
    if (!editor) return;
    let disposed = false;
    let timer: ReturnType<typeof setTimeout> | null = null;

    const run = () => {
      if (disposed) return;
      const doc = editor.Canvas.getDocument();
      if (!doc) return;
      // A newly resolved asset means the elements waiting on it can be painted,
      // so a fetch that lands schedules one more pass rather than each element
      // wiring up its own callback.
      void swap(doc).then((resolvedAny) => {
        if (resolvedAny && !disposed) schedule();
      });
    };

    const schedule = () => {
      if (timer) clearTimeout(timer);
      timer = setTimeout(run, 120);
    };

    schedule();
    editor.on('component:update component:add component:mount', schedule);
    editor.on('canvas:frame:load', schedule);

    return () => {
      disposed = true;
      if (timer) clearTimeout(timer);
      editor.off('component:update component:add component:mount', schedule);
      editor.off('canvas:frame:load', schedule);
    };
  }, [editor]);

  return null;
}

/**
 * One pass over the canvas. Resolves what it can synchronously, starts a fetch
 * for what it cannot, and reports whether anything new arrived.
 */
async function swap(doc: Document): Promise<boolean> {
  const wanted = new Set<string>();

  for (const el of Array.from(doc.querySelectorAll<HTMLElement>('[src],[poster]'))) {
    // A data-bound component's contents are painted by PreviewBridge, which
    // resolves its own media. Two bridges rewriting the same element would each
    // undo the other on every pass.
    if (el.closest('[data-dcms-component]')) continue;
    for (const attr of URL_ATTRS) {
      const current = el.getAttribute(attr);
      if (!current) continue;

      // `data-dcms-src` holds what the model says; `current` may already be the
      // blob we put there. Comparing against the recorded original is what stops
      // a second pass from re-resolving a blob URL.
      const original = el.getAttribute(ORIGINAL_ATTR) ?? current;
      if (!assetIdOf(original)) continue;

      const resolved = knownMediaUrl(original);
      if (resolved === original) {
        wanted.add(original);
        continue;
      }
      if (current !== resolved) {
        el.setAttribute(ORIGINAL_ATTR, original);
        el.setAttribute(attr, resolved);
      }
    }
  }

  // Inline background images, which is how a hero carries its own photo.
  for (const el of Array.from(doc.querySelectorAll<HTMLElement>('[style*="/api/media/"]'))) {
    if (el.closest('[data-dcms-component]')) continue;
    const style = el.getAttribute('style') ?? '';
    for (const match of style.matchAll(/\/api\/media\/[0-9a-f-]{36}\/[a-z0-9_-]+/gi)) {
      const original = match[0];
      const resolved = knownMediaUrl(original);
      if (resolved === original) wanted.add(original);
      else el.setAttribute('style', style.split(original).join(resolved));
    }
  }

  if (wanted.size === 0) return false;
  await Promise.all([...wanted].map((url) => resolveMedia(url)));
  return true;
}
