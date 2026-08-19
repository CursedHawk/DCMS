import {
  PREVIEW_CSS,
  previewHtml,
  previewNotice,
  renderTemplate,
  type PreviewItem,
} from '@dcms/gjs-blocks';
import { COMPONENT_ATTR, decodePlaceholder, nameFromComponentType } from '@dcms/gjs-schema';
import { useQueryClient } from '@tanstack/react-query';
import type { Component, Editor } from 'grapesjs';
import { useEffect, useRef } from 'react';
import { api } from '../../../lib/api';
import { usePluginInstances, type PluginInstance } from '../../plugins/api';
import { CHROME_ATTR, CHROME_EVENT } from '../panels/chrome';
import { rewriteMediaUrls, resolveMedia } from '../panels/media';
import { componentOf } from '../project';
import { useBuilder } from '../store';

/**
 * Live content inside the canvas.
 *
 * A plugin component publishes as an empty placeholder that `hydrate.js` fills
 * in at run time, which means the canvas would otherwise show a blank box where
 * the author put their blog. This bridge fetches the real tenant content and
 * paints it into the placeholder's element so the page in the canvas looks like
 * the page that will ship — including tenant-authored components, which are
 * drawn with the same template renderer the published runtime uses.
 *
 * The preview is written to the *DOM element*, never to the component's model:
 * the model is what gets serialized and committed, and a preview in the repo
 * would be stale content baked into the page. That is also why the markup is
 * re-applied after every canvas change — GrapesJS re-renders from the model and
 * legitimately wipes it.
 */

const STYLE_ID = 'dcms-preview-styles';
/**
 * The ceiling on what one placeholder pulls.
 *
 * Not a fixed sample: a block set to show twelve cards used to draw six, so the
 * canvas showed two rows where the published page had three and the author was
 * laying out a page they could not see. The cap exists only to stop a block
 * bound to a tenant's whole archive from fetching it.
 */
const MAX_ITEMS = 24;
const STALE_MS = 60_000;

interface ContentRow {
  id: string;
  slug: string;
  /** The author's unpublished edits, if any. */
  draft?: Record<string, unknown> | null;
  /** What the delivery API serves, and therefore what a visitor sees. */
  data?: Record<string, unknown> | null;
}

export function PreviewBridge({ editor }: { editor: Editor | null }) {
  const queryClient = useQueryClient();
  const instances = usePluginInstances();
  const instanceList = instances.data;
  // Tenant components live in the site's own repo, so a component edited in the
  // component builder repaints every instance of it on the canvas.
  const components = useBuilder((s) => s.project?.components);
  /** Guards against a refresh triggered by our own DOM writes. */
  const running = useRef(false);

  useEffect(() => {
    if (!editor || !instanceList) return;

    const bySlug = new Map(instanceList.filter((i) => i.enabled).map((i) => [i.slug, i]));
    let disposed = false;
    let timer: ReturnType<typeof setTimeout> | null = null;

    const refresh = () => {
      if (disposed || running.current) return;
      running.current = true;
      void paintAll(editor, bySlug, queryClient)
        .then((pendingMedia) => {
          // An asset that was not cached yet is fetched now, and one more paint
          // draws it. Without this the first view of a page shows every image as
          // the published URL, which does not resolve in the admin.
          if (pendingMedia.length === 0 || disposed) return;
          void Promise.all(pendingMedia.map(resolveMedia)).then(() => !disposed && schedule());
        })
        .catch(() => {
          /* a failed preview must never take the canvas down with it */
        })
        .finally(() => {
          running.current = false;
        });
    };

    const schedule = () => {
      if (timer) clearTimeout(timer);
      timer = setTimeout(refresh, 150);
    };

    injectStyles(editor);
    schedule();

    // `component:update` covers a trait change (which rewrites the placeholder),
    // the others cover a page load and a fresh drop from the palette.
    editor.on('component:update component:add component:mount', schedule);
    // A chrome repaint discards whatever we drew inside a shared region.
    editor.on(CHROME_EVENT, schedule);
    editor.on('canvas:frame:load', () => {
      injectStyles(editor);
      schedule();
    });

    return () => {
      disposed = true;
      if (timer) clearTimeout(timer);
      editor.off('component:update component:add component:mount', schedule);
      editor.off(CHROME_EVENT, schedule);
    };
  }, [editor, instanceList, components, queryClient]);

  return null;
}

/** The preview stylesheet lives in the canvas iframe, alongside the site's own. */
function injectStyles(editor: Editor): void {
  const doc = editor.Canvas.getDocument();
  if (!doc || doc.getElementById(STYLE_ID)) return;
  const style = doc.createElement('style');
  style.id = STYLE_ID;
  style.textContent = PREVIEW_CSS;
  doc.head.appendChild(style);
}

type QueryClient = ReturnType<typeof useQueryClient>;

/** Paints every placeholder, and reports the media URLs still to be fetched. */
async function paintAll(
  editor: Editor,
  bySlug: Map<string, PluginInstance>,
  queryClient: QueryClient,
): Promise<string[]> {
  const wrapper = editor.getWrapper();
  if (!wrapper) return [];

  const targets: PaintTarget[] = [];
  const visit = (component: Component) => {
    const attributes = component.getAttributes() as Record<string, string>;
    const el = component.getEl();
    if (attributes[COMPONENT_ATTR] && el) targets.push({ el, attributes });
    component.components().forEach(visit as never);
  };
  visit(wrapper);

  const doc = editor.Canvas.getDocument();
  // The shared regions RegionChrome paints around the page are plain DOM, not
  // components, so the walk above never reaches them — and a navigation built
  // from a plugin component would sit empty in every page's header.
  for (const el of Array.from(
    doc?.querySelectorAll<HTMLElement>(`[${CHROME_ATTR}] [${COMPONENT_ATTR}]`) ?? [],
  )) {
    targets.push({ el, attributes: attributesOf(el) });
  }

  const results = await Promise.all(
    targets.map((target) => paint(doc, target, bySlug, queryClient)),
  );
  return results.flat();
}

/** An element to paint, with the placeholder attributes that describe it. */
interface PaintTarget {
  el: HTMLElement;
  attributes: Record<string, string>;
}

function attributesOf(el: HTMLElement): Record<string, string> {
  const attributes: Record<string, string> = {};
  for (const attr of Array.from(el.attributes)) attributes[attr.name] = attr.value;
  return attributes;
}

async function paint(
  doc: Document | null | undefined,
  { el, attributes }: PaintTarget,
  bySlug: Map<string, PluginInstance>,
  queryClient: QueryClient,
): Promise<string[]> {
  const decoded = decodePlaceholder(attributes);
  const binding = decoded?.bindings[0];
  const definition = tenantComponent(attributes[COMPONENT_ATTR]);

  const draw = (items: PreviewItem[]) => {
    const html = definition
      ? renderTemplate(doc ?? document, definition.template, items, decoded?.props ?? {})
      : previewHtml(items, decoded?.props ?? {});
    return write(el, html);
  };

  if (!decoded || !binding) {
    // A tenant component with no data source is a snippet: it expanded into the
    // page as real markup when it was dropped, so there is nothing to paint.
    if (definition && !definition.source) return [];
    write(el, previewNotice('unbound'));
    return [];
  }

  const instance = bySlug.get(binding.instanceSlug);
  if (!instance) {
    write(el, previewNotice('missing', binding.instanceSlug));
    return [];
  }

  const contentType = String(binding.query.contentType ?? '');
  if (!contentType) {
    write(el, previewNotice('unbound'));
    return [];
  }

  try {
    // The same key `useContentItemsOfType` uses, so the builder and the content
    // screens share one cache rather than each fetching the tenant's items.
    const rows = await queryClient.fetchQuery({
      queryKey: ['content', instance.id, contentType, 'with-draft'],
      staleTime: STALE_MS,
      queryFn: () =>
        api.get<ContentRow[]>(
          `/admin/content?instanceId=${instance.id}&contentType=${encodeURIComponent(contentType)}&includeDraft=true`,
        ),
    });

    return draw(itemsFor(rows, binding));
  } catch {
    write(el, previewNotice('error'));
    return [];
  }
}

/**
 * The rows this binding would fetch, in the shape the renderers expect.
 *
 * Two things here match the published page rather than being convenient:
 *
 * - **The draft wins, but published content is the fallback.** The canvas showed
 *   `draft` alone, so an item that was published and never edited since arrived
 *   as an empty object and rendered as a blank card — while the live site showed
 *   it perfectly. Preferring the draft is right (an author is looking at their
 *   own unpublished edits); ignoring `data` was not.
 * - **A detail binding resolves one item**, by the slug the author pinned, or the
 *   first — because that is the shape `hydrate.js` gives it from the page URL,
 *   and a detail view handed a list would draw the wrong thing.
 */
function itemsFor(rows: ContentRow[], binding: { propPath: string; query: Record<string, unknown> }): PreviewItem[] {
  const toItem = (row: ContentRow): PreviewItem => ({
    id: row.id,
    slug: row.slug,
    data: row.draft ?? row.data ?? {},
  });

  if (binding.propPath === 'item') {
    const wanted = String(binding.query.itemSlug ?? '');
    const row = (wanted && rows.find((r) => r.slug === wanted)) || rows[0];
    return row ? [toItem(row)] : [];
  }

  const page = Math.max(1, Number(binding.query.page) || 1);
  const pageSize = Math.min(Number(binding.query.pageSize) || 10, MAX_ITEMS);
  return rows.slice((page - 1) * pageSize, (page - 1) * pageSize + pageSize).map(toItem);
}

/** The tenant component a placeholder names, if it names one. */
function tenantComponent(type: string | undefined) {
  const name = type ? nameFromComponentType(type) : null;
  const project = useBuilder.getState().project;
  return name && project ? componentOf(project, name) : undefined;
}

/**
 * Paint the element, rewriting media URLs to something the admin can load, and
 * report the assets that still need fetching.
 *
 * Only written when the markup actually differs — GrapesJS emits an update for
 * every DOM mutation inside the canvas, so an unconditional write would loop.
 */
function write(el: HTMLElement, html: string): string[] {
  const { html: painted, pending } = rewriteMediaUrls(html);
  if (el.innerHTML !== painted) el.innerHTML = painted;
  return pending;
}
