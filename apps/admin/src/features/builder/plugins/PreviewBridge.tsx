import { PREVIEW_CSS, previewHtml, previewNotice, type PreviewItem } from '@dcms/gjs-blocks';
import { COMPONENT_ATTR, decodePlaceholder } from '@dcms/gjs-schema';
import { useQueryClient } from '@tanstack/react-query';
import type { Component, Editor } from 'grapesjs';
import { useEffect, useRef } from 'react';
import { api } from '../../../lib/api';
import { usePluginInstances, type PluginInstance } from '../../plugins/api';

/**
 * Live content inside the canvas.
 *
 * A plugin component publishes as an empty placeholder that `hydrate.js` fills
 * in at run time, which means the canvas would otherwise show a blank box where
 * the author put their blog. This bridge fetches a sample of the real tenant
 * content and paints it into the placeholder's element so the page in the canvas
 * looks like the page that will ship.
 *
 * The preview is written to the *DOM element*, never to the component's model:
 * the model is what gets serialized and committed, and a preview in the repo
 * would be stale content baked into the page. That is also why the markup is
 * re-applied after every canvas change — GrapesJS re-renders from the model and
 * legitimately wipes it.
 */

const STYLE_ID = 'dcms-preview-styles';
/** Enough to show a layout without pulling a tenant's whole archive. */
const SAMPLE_SIZE = 6;
const STALE_MS = 60_000;

interface ContentRow {
  id: string;
  slug: string;
  draft?: Record<string, unknown> | null;
}

export function PreviewBridge({ editor }: { editor: Editor | null }) {
  const queryClient = useQueryClient();
  const instances = usePluginInstances();
  const instanceList = instances.data;
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
    editor.on('canvas:frame:load', () => {
      injectStyles(editor);
      schedule();
    });

    return () => {
      disposed = true;
      if (timer) clearTimeout(timer);
      editor.off('component:update component:add component:mount', schedule);
    };
  }, [editor, instanceList, queryClient]);

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

async function paintAll(
  editor: Editor,
  bySlug: Map<string, PluginInstance>,
  queryClient: QueryClient,
): Promise<void> {
  const wrapper = editor.getWrapper();
  if (!wrapper) return;

  const placeholders: Component[] = [];
  const visit = (component: Component) => {
    if (component.getAttributes()[COMPONENT_ATTR]) placeholders.push(component);
    component.components().forEach(visit as never);
  };
  visit(wrapper);

  await Promise.all(placeholders.map((component) => paint(component, bySlug, queryClient)));
}

async function paint(
  component: Component,
  bySlug: Map<string, PluginInstance>,
  queryClient: QueryClient,
): Promise<void> {
  const el = component.getEl();
  if (!el) return;

  const decoded = decodePlaceholder(component.getAttributes() as Record<string, string>);
  const binding = decoded?.bindings[0];
  if (!decoded || !binding) return write(el, previewNotice('unbound'));

  const instance = bySlug.get(binding.instanceSlug);
  if (!instance) return write(el, previewNotice('missing', binding.instanceSlug));

  const contentType = String(binding.query.contentType ?? '');
  if (!contentType) return write(el, previewNotice('unbound'));

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

    const limit = Number(binding.query.pageSize) || SAMPLE_SIZE;
    const items: PreviewItem[] = rows
      .slice(0, Math.min(limit, SAMPLE_SIZE))
      .map((row) => ({ id: row.id, slug: row.slug, data: row.draft ?? {} }));
    write(el, previewHtml(items, decoded.props));
  } catch {
    write(el, previewNotice('error'));
  }
}

/**
 * Paint the element, but only when the markup actually differs — GrapesJS emits
 * an update for every DOM mutation inside the canvas, so an unconditional write
 * would loop.
 */
function write(el: HTMLElement, html: string): void {
  if (el.innerHTML !== html) el.innerHTML = html;
}
