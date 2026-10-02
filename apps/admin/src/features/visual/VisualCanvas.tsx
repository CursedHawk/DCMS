import { THEME_JSON, pagePath, pageSchema, themeTokensSchema, type Page, type Registry } from '@dcms/site-runtime';
import type { Editor } from 'grapesjs';
import { useEffect, useRef } from 'react';
import { useVfs } from '../site-source';
import { applyTheme, createVisualEditor } from './canvas/editor';
import { fromGrapes, toGrapes } from './canvas/tree';
import { serializeDoc } from './starter';

/** What the canvas currently holds, and the exact file text it came from or last wrote. */
interface Loaded {
  path: string;
  page: Page;
  text: string;
  generation: number;
}

/**
 * Owns the GrapesJS instance and keeps one page file and the canvas in step.
 *
 * - **Load** whenever the page's file changes for a reason that is not this canvas: a page
 *   switch, the code view, an AI edit, a branch switch or restore (`generation`).
 * - **Capture** on GrapesJS's `update`, debounced, back into the file the canvas was loaded
 *   from — never the one the page selector names by then (see Mode A's `loadedTarget` for the
 *   bug that rule prevents).
 *
 * A file that does not parse is reported and left alone; capturing over it would replace
 * whatever the author was in the middle of typing in the code view with an empty page.
 */
export function VisualCanvas({
  pageId,
  registry,
  onReady,
  onTeardown,
  onPageError,
}: {
  pageId: string;
  registry: Registry;
  onReady: (editor: Editor) => void;
  onTeardown: () => void;
  onPageError: (message: string | null) => void;
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const editorRef = useRef<Editor | null>(null);
  const loaded = useRef<Loaded | null>(null);
  const loading = useRef(false);
  const captureTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const themeText = useRef<string | undefined>(undefined);

  const path = pagePath(pageId);
  const fileText = useVfs((s) => s.files[path]);
  const generation = useVfs((s) => s.generation);
  const theme = useVfs((s) => s.files[THEME_JSON]);

  // Created once: rebuilding GrapesJS drops the undo history and the scroll position.
  useEffect(() => {
    if (!containerRef.current || editorRef.current) return;
    const editor = createVisualEditor(containerRef.current, registry);
    editorRef.current = editor;

    const onUpdate = () => {
      if (loading.current || !loaded.current) return;
      if (captureTimer.current) clearTimeout(captureTimer.current);
      captureTimer.current = setTimeout(() => {
        captureTimer.current = null;
        capture(editor, loaded);
      }, 300);
    };
    editor.on('update', onUpdate);
    // The theme lives in the frame's document, which exists only once the frame has loaded.
    editor.on('load', () => applyThemeText(editor, themeText.current));
    onReady(editor);

    return () => {
      if (captureTimer.current) {
        clearTimeout(captureTimer.current);
        // Leaving with edits still in the debounce: write them rather than lose them.
        capture(editor, loaded);
      }
      editor.off('update', onUpdate);
      onTeardown();
      editor.destroy();
      editorRef.current = null;
      loaded.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  useEffect(() => {
    const editor = editorRef.current;
    if (!editor) return;
    const current = loaded.current;
    // Our own capture coming back through the file map, or nothing new.
    if (current && current.path === path && current.text === fileText && current.generation === generation) return;

    if (current && current.path !== path && captureTimer.current) {
      // Switching pages with an edit still pending: it belongs to the outgoing page.
      clearTimeout(captureTimer.current);
      captureTimer.current = null;
      capture(editor, loaded);
    }

    loaded.current = null;
    const page = parsePage(fileText);
    if (typeof page === 'string') {
      onPageError(page);
      editor.setComponents([] as never);
      return;
    }
    onPageError(null);

    // Replacing the document is not an undoable edit — it is the starting point.
    loading.current = true;
    editor.UndoManager.stop();
    try {
      editor.setComponents(toGrapes(page.root, registry) as never);
    } finally {
      editor.UndoManager.start();
    }
    editor.UndoManager.clear();
    editor.select(undefined);
    loaded.current = { path, page, text: fileText!, generation };
    // GrapesJS reports the load itself as an update; let that pass before listening again.
    setTimeout(() => {
      loading.current = false;
    }, 0);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [path, fileText, generation]);

  useEffect(() => {
    themeText.current = theme;
    if (editorRef.current) applyThemeText(editorRef.current, theme);
  }, [theme]);

  return <div ref={containerRef} className="h-full w-full" />;
}

function parsePage(text: string | undefined): Page | string {
  if (text === undefined) return 'This page has no file.';
  let json: unknown;
  try {
    json = JSON.parse(text);
  } catch (e) {
    return `Not valid JSON: ${(e as Error).message}`;
  }
  const parsed = pageSchema.safeParse(json);
  if (parsed.success) return parsed.data;
  const issue = parsed.error.issues[0];
  return `${issue?.path.join('.') || 'page'}: ${issue?.message ?? 'invalid'}`;
}

function applyThemeText(editor: Editor, text: string | undefined): void {
  if (!text) return;
  try {
    const theme = themeTokensSchema.safeParse(JSON.parse(text));
    if (theme.success) applyTheme(editor, theme.data);
  } catch {
    // A theme file mid-edit in the code view; the canvas keeps the last good one.
  }
}

/** Serialize the canvas into the file it was loaded from, if that changed anything. */
function capture(editor: Editor, loaded: { current: Loaded | null }): void {
  const current = loaded.current;
  const root = editor.getWrapper()?.components().at(0);
  if (!current || !root) return;

  const page: Page = { ...current.page, root: fromGrapes(root) };
  const text = serializeDoc(page);
  if (text === current.text) return;

  // Recorded before writing, so the file change it causes is recognised as our own.
  loaded.current = { ...current, page, text };
  useVfs.getState().writeFile(current.path, text);
}
