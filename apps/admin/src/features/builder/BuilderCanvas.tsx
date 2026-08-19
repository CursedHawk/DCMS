import { applyThemeSwatches, registerSpecs } from '@dcms/gjs-blocks';
import {
  GLOBAL_CSS,
  pageCssPath,
  pageHtmlPath,
  componentPath,
  regionHtmlPath,
  renderThemeCss,
  serializeComponentDefinition,
  type DcmsComponentSpec,
} from '@dcms/gjs-schema';
import type { Editor } from 'grapesjs';
import { useEffect, useMemo, useRef } from 'react';
import { applyThemeCss, createEditor } from './grapes';
import type { Project } from './project';
import { applyCapture, captureTarget, docFor, loadTargetIntoEditor, type CapturedDoc } from './storage';
import { useBuilder, type CanvasTarget } from './store';

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
  customSpecs,
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
  /** Components the tenant built themselves, from `blocks/*.json`. */
  customSpecs: readonly DcmsComponentSpec[];
}) {
  const containerRef = useRef<HTMLDivElement>(null);
  const editorRef = useRef<Editor | null>(null);
  /** True while loadTargetIntoEditor is running; suppresses capture. */
  const loading = useRef(false);
  /** What the canvas currently holds, so we only reload on a real change. */
  const loadedSlug = useRef<string | null>(null);
  /**
   * The document the canvas is actually holding.
   *
   * Capture has to write back to *this*, not to whatever the store now points
   * at: switching target does not swap the canvas until the reload debounce has
   * run, so for a moment the store names one document while the canvas still
   * holds another. Reading the store at capture time wrote the old canvas into
   * the new document's file — opening a component right after editing a page
   * overwrote that page with the component's template.
   */
  const loadedTarget = useRef<CanvasTarget | null>(null);
  const captureTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  /** The tenant-component shape last registered, so identical specs are skipped. */
  const registeredSpecs = useRef<string | null>(null);

  const activeSlug = useBuilder((s) => s.activeSlug);
  const activeKind = useBuilder((s) => s.activeKind);
  // What an already-placed instance of a tenant component depends on: its type,
  // the traits the inspector draws, and what it fetches. Deliberately not the
  // markup — see the registration effect below.
  const specSignature = useMemo(
    () =>
      JSON.stringify(
        customSpecs.map((spec) => [spec.type, spec.label, spec.traits, spec.binding]),
      ),
    [customSpecs],
  );
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
      if (loading.current || !loadedTarget.current) return;
      if (captureTimer.current) clearTimeout(captureTimer.current);
      const target = loadedTarget.current;
      captureTimer.current = setTimeout(() => void capture(editor, target), 400);
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
      loadedTarget.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Load the active document whenever it changes, or when something outside the
  // canvas (the code view, a git restore) replaced its content.
  //
  // Debounced because a code-view edit bumps `reloadToken` on every keystroke:
  // reloading per character would fight the author's cursor and throw away the
  // selection between letters. The delay resets on each further keystroke, so
  // the canvas catches up once typing pauses.
  useEffect(() => {
    const editor = editorRef.current;
    if (!editor || !activeSlug) return;

    const target: CanvasTarget = { kind: activeKind, slug: activeSlug };
    const key = `${activeKind}:${activeSlug}#${reloadToken}`;
    if (loadedSlug.current === key) return;

    const outgoing = loadedTarget.current;
    const switching =
      outgoing !== null && (outgoing.kind !== target.kind || outgoing.slug !== target.slug);

    if (switching) {
      // Write the outgoing document out *now* rather than dropping the pending
      // capture: it holds up to a debounce worth of the author's edits, and
      // `captureTarget` serializes the canvas synchronously, so flushing before
      // the swap records exactly what is still on screen.
      if (captureTimer.current) {
        clearTimeout(captureTimer.current);
        captureTimer.current = null;
        void capture(editor, outgoing);
      }
      // Nothing more may be captured until the incoming document has loaded —
      // otherwise the outgoing content lands in the incoming file.
      loadedTarget.current = null;
    } else if (captureTimer.current) {
      // Same document, new content: this reload exists *because* the file
      // changed underneath the canvas, so what the canvas still holds is stale
      // and writing it back would undo whatever made the file change.
      clearTimeout(captureTimer.current);
      captureTimer.current = null;
    }

    const timer = setTimeout(() => {
      loadedSlug.current = key;
      const { project } = useBuilder.getState();
      if (!project || !docFor(project, target)) return;

      loading.current = true;
      void loadTargetIntoEditor(editor, project, target).finally(() => {
        loading.current = false;
        loadedTarget.current = target;
      });
    }, 350);

    return () => clearTimeout(timer);
  }, [activeKind, activeSlug, reloadToken]);

  // The tenant's own components change *during* a session — saving one in the
  // component builder is the whole point — so unlike the plugin catalogue they
  // are re-registered rather than read once at creation.
  //
  // Both halves are guarded, and both guards matter:
  //
  // - **By content, not identity.** The project is re-read from the working
  //   draft on every autosave, which hands back a new `components` array every
  //   time even when nothing about it changed.
  // - **Reload only when an existing instance would be wrong.** Re-registering
  //   a type only affects components built from then on, so a page holding one
  //   has to be re-read for it to pick up new traits — but a reload clears the
  //   canvas selection. Doing that on every autosave made a component
  //   impossible to edit: each click selected an element and the next autosave
  //   immediately deselected it, so the inspector could never be reached. The
  //   markup itself is not part of the signature, since editing a template is
  //   exactly what must *not* reload the canvas out from under the author.
  useEffect(() => {
    const editor = editorRef.current;
    if (!editor || customSpecs.length === 0) return;

    const previous = registeredSpecs.current;
    if (previous === specSignature) return;
    registeredSpecs.current = specSignature;
    registerSpecs(editor, customSpecs, { idPrefix: 'dcms-custom:' });

    // Nothing on the canvas can be stale on the first registration, and a
    // component's own template is edited on this canvas — reloading either
    // would only throw away the author's place.
    if (previous !== null && useBuilder.getState().activeKind !== 'component') {
      useBuilder.getState().requestReload();
    }
  }, [customSpecs, specSignature]);

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

/**
 * The files this capture rewrote, other than the shared stylesheet.
 *
 * A component's markup is a field inside its definition, so what lands in the
 * working draft is the serialized JSON — recording the markup alone would make
 * the store see the definition drift on the very next sync and reload the canvas
 * out from under the author.
 */
function ownFiles(
  project: Project,
  target: CanvasTarget,
  captured: CapturedDoc,
): Record<string, string> {
  if (target.kind === 'region') return { [regionHtmlPath(target.slug)]: captured.html };
  if (target.kind === 'component') {
    const definition = project.components.find((c) => c.name === target.slug);
    return definition
      ? {
          [componentPath(target.slug)]: serializeComponentDefinition({
            ...definition,
            template: captured.html,
          }),
        }
      : {};
  }
  return {
    [pageHtmlPath(target.slug)]: captured.html,
    [pageCssPath(target.slug)]: captured.css,
  };
}

/**
 * Serialize the canvas and fold the result back into the project.
 *
 * `target` is the document the canvas was loaded with, passed in rather than read
 * from the store — see `loadedTarget`. That is what makes a flush-on-switch safe:
 * the content is written back to the document it came from, whatever the store
 * has moved on to.
 */
async function capture(editor: Editor, target: CanvasTarget): Promise<void> {
  const project = useBuilder.getState().project;
  if (!project) return;
  const captured = await captureTarget(editor, target);

  // Record what the canvas produced *before* writing it, so the resulting
  // working-draft change is recognised as our own and does not bounce back as a
  // reload (see the store's drift check).
  useBuilder.getState().noteCapture({
    ...ownFiles(project, target, captured),
    [GLOBAL_CSS]: captured.globalCss,
  });

  // The project may have moved on while formatting ran in the worker; re-read it
  // so a concurrent page rename or theme edit is not thrown away.
  useBuilder.getState().update((current) => applyCapture(current, target, captured));
}
