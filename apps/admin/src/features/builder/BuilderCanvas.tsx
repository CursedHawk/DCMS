import { applyThemeSwatches } from '@dcms/gjs-blocks';
import {
  GLOBAL_CSS,
  pageCssPath,
  pageHtmlPath,
  renderThemeCss,
  type DcmsComponentSpec,
} from '@dcms/gjs-schema';
import type { Editor } from 'grapesjs';
import { useEffect, useRef } from 'react';
import { applyThemeCss, createEditor } from './grapes';
import { applyCapture, capturePage, loadPageIntoEditor } from './storage';
import { useBuilder } from './store';

/**
 * Owns the GrapesJS instance and keeps it in sync with the project.
 *
 * The sync is one-directional per trigger, guarded by `loading`: while a page is
 * being loaded into the canvas GrapesJS fires its own `update` events, and
 * capturing those would immediately write the just-loaded content back as if the
 * author had edited it — turning a page switch into a spurious change in the
 * Source Control panel.
 */
export function BuilderCanvas({
  onReady,
  onTeardown,
  enabledPluginIds,
  pluginSpecs,
}: {
  onReady?: (editor: Editor) => void;
  /**
   * Called just before the editor is destroyed. The panels hold the same editor
   * object and would keep calling into it — `editor.getSelected()` on a
   * destroyed instance throws, which took the whole page down.
   */
  onTeardown?: () => void;
  /** Plugin ids enabled for this tenant; gates plugin-backed blocks. */
  enabledPluginIds: readonly string[];
  /** Components generated from the tenant's plugin instances. */
  pluginSpecs: readonly DcmsComponentSpec[];
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const editorRef = useRef<Editor | null>(null);
  /** True while loadPageIntoEditor is running; suppresses capture. */
  const loading = useRef(false);
  /** Which page the canvas currently holds, so we only reload on a real change. */
  const loadedSlug = useRef<string | null>(null);
  const captureTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const activeSlug = useBuilder((s) => s.activeSlug);
  const reloadToken = useBuilder((s) => s.reloadToken);
  const theme = useBuilder((s) => s.project?.manifest.theme);

  // Create once; GrapesJS is expensive to build and re-creating it would drop
  // the undo history and the canvas scroll position on every re-render.
  useEffect(() => {
    if (!containerRef.current || editorRef.current) return;
    const editor = createEditor({
      container: containerRef.current,
      // Read once at creation: the editor is built here and never rebuilt, so a
      // later plugin change is picked up on the next open rather than by
      // tearing down the canvas (and the undo history) underneath the author.
      enabledPluginIds,
      pluginSpecs,
      theme: useBuilder.getState().project?.manifest.theme,
    });
    editorRef.current = editor;

    const onUpdate = () => {
      if (loading.current) return;
      if (captureTimer.current) clearTimeout(captureTimer.current);
      captureTimer.current = setTimeout(() => void capture(editor), 400);
    };
    editor.on('update', onUpdate);
    onReady?.(editor);

    return () => {
      if (captureTimer.current) clearTimeout(captureTimer.current);
      editor.off('update', onUpdate);
      // Drop every outside reference *before* destroying, so nothing renders
      // against a half-torn-down editor.
      onTeardown?.();
      editor.destroy();
      editorRef.current = null;
      loadedSlug.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Load the active page whenever it changes, or when something outside the
  // canvas (the code view, a git restore) replaced its content.
  //
  // Debounced because a code-view edit bumps `reloadToken` on every keystroke:
  // reloading per character would fight the author's cursor and throw away the
  // selection between letters. The delay resets on each further keystroke, so
  // the canvas catches up once typing pauses.
  useEffect(() => {
    const editor = editorRef.current;
    if (!editor || !activeSlug) return;

    const key = `${activeSlug}#${reloadToken}`;
    if (loadedSlug.current === key) return;

    const timer = setTimeout(() => {
      loadedSlug.current = key;
      const { project } = useBuilder.getState();
      const page = project?.pages.find((p) => p.entry.slug === activeSlug);
      if (!project || !page) return;

      loading.current = true;
      void loadPageIntoEditor(editor, project, page).finally(() => {
        loading.current = false;
      });
    }, 350);

    return () => clearTimeout(timer);
  }, [activeSlug, reloadToken]);

  // Theme changes are not component changes, so they need their own push into
  // the canvas iframe (see applyThemeCss for why the theme is not a CssRule).
  useEffect(() => {
    const editor = editorRef.current;
    if (!editor || !theme) return;
    applyThemeCss(editor, renderThemeCss(theme));
    // The Style panel's colour presets come from the theme too, so they have to
    // be refreshed alongside the canvas or the palette silently goes stale.
    applyThemeSwatches(editor, theme);
  }, [theme]);

  return <div ref={containerRef} className="h-full w-full" />;
}

/** Serialize the canvas and fold the result back into the project. */
async function capture(editor: Editor): Promise<void> {
  const { activeSlug, project } = useBuilder.getState();
  if (!activeSlug || !project) return;
  const captured = await capturePage(editor, activeSlug);

  // Record what the canvas produced *before* writing it, so the resulting
  // working-draft change is recognised as our own and does not bounce back as a
  // reload (see the store's drift check).
  useBuilder.getState().noteCapture({
    [pageHtmlPath(activeSlug)]: captured.html,
    [pageCssPath(activeSlug)]: captured.css,
    [GLOBAL_CSS]: captured.globalCss,
  });

  // The project may have moved on while formatting ran in the worker; re-read it
  // so a concurrent page rename or theme edit is not thrown away.
  useBuilder.getState().update((current) => applyCapture(current, activeSlug, captured));
}
