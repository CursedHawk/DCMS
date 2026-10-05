import {
  APP_JSON,
  THEME_JSON,
  appSchema,
  componentPath,
  pagePath,
  pageSchema,
  tenantComponentSchema,
  themeTokensSchema,
  type Node,
  type Registry,
} from '@dcms/site-runtime';
import type { Editor } from 'grapesjs';
import { useEffect, useRef, useState } from 'react';
import { useVfs } from '../site-source';
import { applyTheme, createVisualEditor } from './canvas/editor';
import { findNode } from './canvas/operations';
import { registerVisualTypes } from './canvas/types';
import { ID, fromGrapes, toGrapes } from './canvas/tree';
import { defaultShell, pruneComponent } from './documents';
import { serializeDoc } from './starter';
import { useVisual, type CanvasTarget } from './store';

/**
 * One document the canvas can edit: where its tree lives in a file, and how to put an edited
 * tree back. A page is a whole file; the app shell is one field of `app.json`, so writing it
 * back must keep the rest of that file exactly as it was.
 */
interface Doc {
  root: Node;
  /** The file's new text with `root` in place of the tree that was loaded. */
  write: (root: Node) => string;
}

/** What the canvas currently holds, and the exact file text it came from or last wrote. */
interface Loaded {
  path: string;
  doc: Doc;
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
  target,
  registry,
  onReady,
  onTeardown,
  onPageError,
}: {
  target: CanvasTarget;
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

  const path =
    target.kind === 'page' ? pagePath(target.id) : target.kind === 'component' ? componentPath(target.name, target.version) : APP_JSON;
  const [registered, setRegistered] = useState(registry);
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
    useVisual.setState({
      selectedNodeId: () => (editor.getSelected()?.get(ID) as string | undefined) ?? null,
      flushCanvas: () => {
        if (!captureTimer.current) return;
        clearTimeout(captureTimer.current);
        captureTimer.current = null;
        capture(editor, loaded);
      },
    });
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
      useVisual.setState({ flushCanvas: () => {}, selectedNodeId: () => null });
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
    const doc = target.kind === 'page' ? pageDoc(fileText) : target.kind === 'component' ? componentDoc(fileText) : shellDoc(fileText);
    if (typeof doc === 'string') {
      onPageError(doc);
      editor.setComponents([] as never);
      return;
    }
    onPageError(null);

    // Whatever was selected stays selected across a reload of the same document (an inspector
    // edit that rewrote the file, a code-view change), so the author does not lose their place.
    const keep = current?.path === path ? (editor.getSelected()?.get(ID) as string | undefined) : undefined;

    // Replacing the document is not an undoable edit — it is the starting point.
    loading.current = true;
    editor.UndoManager.stop();
    try {
      editor.setComponents(toGrapes(doc.root, registry) as never);
    } finally {
      editor.UndoManager.start();
    }
    editor.UndoManager.clear();
    const pending = useVisual.getState().pendingSelect;
    if (pending) useVisual.setState({ pendingSelect: null });
    editor.select(keep || pending ? findNode(editor, (keep ?? pending)!) : undefined);
    // A component's template root stays put: it is what the component *is*.
    if (target.kind === 'component') editor.getWrapper()?.components().at(0)?.set({ removable: false, draggable: false, copyable: false });
    loaded.current = { path, doc, text: fileText!, generation };
    // GrapesJS reports the load itself as an update; let that pass before listening again.
    setTimeout(() => {
      loading.current = false;
    }, 0);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [path, fileText, generation, target.kind, registered]);

  // The site's own components changed (one created, edited or deleted): re-register the canvas
  // types and reload, so instances draw with — and the palette offers — what now exists.
  useEffect(() => {
    const editor = editorRef.current;
    if (!editor || registry === registered) return;
    if (captureTimer.current) {
      clearTimeout(captureTimer.current);
      captureTimer.current = null;
      capture(editor, loaded);
    }
    registerVisualTypes(editor, registry);
    loaded.current = null;
    setRegistered(registry);
  }, [registry, registered]);

  useEffect(() => {
    themeText.current = theme;
    if (editorRef.current) applyThemeText(editorRef.current, theme);
  }, [theme]);

  return <div ref={containerRef} className="h-full w-full" />;
}

function parseJson(text: string | undefined, missing: string): unknown {
  if (text === undefined) return missing;
  try {
    return { json: JSON.parse(text) as unknown };
  } catch (e) {
    return `Not valid JSON: ${(e as Error).message}`;
  }
}

function pageDoc(text: string | undefined): Doc | string {
  const read = parseJson(text, 'This page has no file.');
  if (typeof read === 'string') return read;
  const parsed = pageSchema.safeParse((read as { json: unknown }).json);
  if (!parsed.success) {
    const issue = parsed.error.issues[0];
    return `${issue?.path.join('.') || 'page'}: ${issue?.message ?? 'invalid'}`;
  }
  const page = parsed.data;
  return { root: page.root, write: (root) => serializeDoc({ ...page, root }) };
}

function shellDoc(text: string | undefined): Doc | string {
  const read = parseJson(text, 'This site has no dcms/app.json.');
  if (typeof read === 'string') return read;
  const parsed = appSchema.safeParse((read as { json: unknown }).json);
  if (!parsed.success) {
    const issue = parsed.error.issues[0];
    return `app.json ${issue?.path.join('.') || ''}: ${issue?.message ?? 'invalid'}`;
  }
  const app = parsed.data;
  // A site that has never had a shell is shown the default; it is only written once edited.
  // The rest of app.json is re-read at write time, not taken from this load: the Pages panel
  // edits routes and menus in the same file, and a shell capture must never put back the routes
  // as they were when the shell was opened.
  return {
    root: app.shell ?? defaultShell(),
    write: (shell) => {
      const now = appSchema.safeParse(safeJson(useVfs.getState().files[APP_JSON]));
      return serializeDoc({ ...(now.success ? now.data : app), shell });
    },
  };
}

function componentDoc(text: string | undefined): Doc | string {
  const read = parseJson(text, 'This component has no file.');
  if (typeof read === 'string') return read;
  const parsed = tenantComponentSchema.safeParse((read as { json: unknown }).json);
  if (!parsed.success) {
    const issue = parsed.error.issues[0];
    return `${issue?.path.join('.') || 'component'}: ${issue?.message ?? 'invalid'}`;
  }
  const doc = parsed.data;
  const path = componentPath(doc.name, doc.version);
  return {
    root: doc.root,
    // Re-read at write time: the inspector edits this file's settings and slots while the
    // template is open, and a template capture must not put back an older list of them.
    write: (root) => {
      const now = tenantComponentSchema.safeParse(safeJson(useVfs.getState().files[path]));
      return serializeDoc(pruneComponent({ ...(now.success ? now.data : doc), root }));
    },
  };
}

function safeJson(text: string | undefined): unknown {
  try {
    return JSON.parse(text ?? '');
  } catch {
    return undefined;
  }
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

  const tree = fromGrapes(root);
  const text = current.doc.write(tree);
  if (text === useVfs.getState().files[current.path]) return;

  // Recorded before writing, so the file change it causes is recognised as our own. The doc is
  // re-derived from what was written, so the next capture writes on top of it.
  const doc: Doc = { root: tree, write: (next) => current.doc.write(next) };
  loaded.current = { ...current, doc, text };
  useVfs.getState().writeFile(current.path, text);
}
