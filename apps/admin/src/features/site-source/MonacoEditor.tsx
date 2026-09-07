import { useEffect, useRef } from 'react';
import type * as Monaco from 'monaco-editor';
import { useTheme } from '@dcms/ui';
import { isBinaryPath } from './binary';
import { isGeneratedFile, isToolchainFile, languageOf, pathOfUri, uriOf } from './paths';
import { applyEditorTheme, monaco, setupMonaco } from './monaco-setup';
import { useVfs } from './vfs';

// The editing surface. Every file in the vfs is a Monaco model (file:/// URI);
// edits flow model -> store. The parent remounts this per site (key=siteId) so
// the model set is created fresh, and create/delete/rename reconcile in place.

/**
 * Push new content into a model without throwing the editor's state away.
 *
 * `setValue` resets the undo stack, the scroll position, folding and any open
 * find — which matters here because the Mode A builder writes the page back on
 * every canvas change, so this runs constantly while the author is reading the
 * code beside the canvas. A single edit over the full range is the same result
 * to the model and leaves all of that intact.
 */
function applyContent(model: Monaco.editor.ITextModel, content: string): void {
  model.pushEditOperations([], [{ range: model.getFullModelRange(), text: content }], () => null);
}

export function MonacoEditor() {
  const hostRef = useRef<HTMLDivElement>(null);
  const editorRef = useRef<Monaco.editor.IStandaloneCodeEditor | null>(null);
  const modelsRef = useRef<Map<string, Monaco.editor.ITextModel>>(new Map());
  // True while we're pushing store content into models (restore / conflict reload),
  // so the resulting model-change events don't get written back as user edits.
  const syncingRef = useRef(false);
  const { resolved } = useTheme();

  const activePath = useVfs((s) => s.activePath);
  const fileKeys = useVfs((s) => Object.keys(s.files).sort().join('\n'));
  const generation = useVfs((s) => s.generation);
  const rev = useVfs((s) => s.rev);

  // Create the editor once.
  useEffect(() => {
    setupMonaco();
    // Overflowing widgets — the find box, hovers, the suggest list — are hosted
    // outside the editor's own DOM. `fixedOverflowWidgets` alone positions them
    // `fixed`, which is measured against the nearest transformed or clipping
    // ancestor; the builder's split view has several, so the find widget was
    // being placed outside the visible area and Ctrl+F looked like it did
    // nothing at all. Giving Monaco a host on <body> puts it back on screen.
    const overflowHost = document.createElement('div');
    overflowHost.className = 'monaco-editor dcms-monaco-overflow';
    document.body.appendChild(overflowHost);

    const editor = monaco.editor.create(hostRef.current!, {
      automaticLayout: true,
      fontSize: 13,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      tabSize: 2,
      fixedOverflowWidgets: true,
      overflowWidgetsDomNode: overflowHost,
    });
    editorRef.current = editor;
    applyEditorTheme(resolved);

    const sub = editor.onDidChangeModelContent(() => {
      if (syncingRef.current) return;
      const model = editor.getModel();
      if (!model) return;
      const path = pathOfUri(model.uri.toString());
      // A generated file (the builder's styles/theme.css) is rewritten from its
      // source on the next save, so an edit here would silently vanish; the
      // model is read-only, and this is the belt to that braces.
      if (isToolchainFile(path) || isGeneratedFile(path)) return;
      useVfs.getState().writeFile(path, model.getValue());
    });

    // Cross-file Ctrl+Click "go to definition". The standalone editor has no way to
    // open a definition that lives in a *different* model, so navigation silently
    // did nothing across files. Register a global opener: map the target file:/// URI
    // back to a vfs file, open its tab, swap it into this editor and reveal the target
    // location. Return false for URIs we don't own (e.g. bundled lib .d.ts) so Monaco
    // keeps its default handling.
    const opener = monaco.editor.registerEditorOpener({
      openCodeEditor(source, resource, selectionOrPosition) {
        const path = pathOfUri(resource.toString());
        const model = modelsRef.current.get(path);
        if (!model) return false;
        useVfs.getState().open(path);
        source.setModel(model);
        source.updateOptions({ readOnly: isToolchainFile(path) || isGeneratedFile(path) });
        if (selectionOrPosition) {
          if ('startLineNumber' in selectionOrPosition) {
            source.setSelection(selectionOrPosition);
            source.revealRangeInCenterIfOutsideViewport(selectionOrPosition, monaco.editor.ScrollType.Smooth);
          } else {
            source.setPosition(selectionOrPosition);
            source.revealPositionInCenterIfOutsideViewport(selectionOrPosition, monaco.editor.ScrollType.Smooth);
          }
        }
        source.focus();
        return true;
      },
    });

    return () => {
      sub.dispose();
      opener.dispose();
      editor.dispose();
      overflowHost.remove();
      editorRef.current = null;
      for (const model of modelsRef.current.values()) model.dispose();
      modelsRef.current.clear();
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Reconcile models with the file map (handles create / delete / rename / load).
  useEffect(() => {
    const { files } = useVfs.getState();
    const models = modelsRef.current;
    // Add missing models. Binary files (base64 in the map) are never editable
    // text — they get no model and render in the binary viewer instead.
    for (const [path, content] of Object.entries(files)) {
      if (!models.has(path) && !isBinaryPath(path)) {
        const model = monaco.editor.createModel(content, languageOf(path), monaco.Uri.parse(uriOf(path)));
        models.set(path, model);
      }
    }
    // Remove models whose file is gone.
    for (const [path, model] of models) {
      if (files[path] == null) {
        model.dispose();
        models.delete(path);
      }
    }
  }, [fileKeys]);

  // On a full (re)load — restore, or the conflict "reload latest" — the store
  // replaces file contents in place. Existing models keep their own (now stale)
  // text, so push the store's content back into each model. Guarded so these
  // programmatic edits are not written back to the store as user edits.
  useEffect(() => {
    if (generation === 0) return;
    const { files } = useVfs.getState();
    syncingRef.current = true;
    try {
      for (const [path, model] of modelsRef.current) {
        const content = files[path];
        if (content != null && model.getValue() !== content) applyContent(model, content);
      }
    } finally {
      syncingRef.current = false;
    }
  }, [generation]);

  // Follow writes that did not come from this editor.
  //
  // In the Mode B IDE nothing else writes, so this is a no-op there. In the Mode
  // A builder the canvas writes the page back on every change, and in split view
  // those must show up in the code immediately — otherwise the two halves of the
  // same screen disagree about the same file.
  //
  // The model the author is working in is deliberately skipped: overwriting text
  // under a typing cursor would move it, and content they just typed already
  // matches the store anyway.
  //
  // "Working in" means `hasWidgetFocus`, not `hasTextFocus`. The find widget is
  // part of the editor but is not the text area, so with the narrower check a
  // Ctrl+F in the builder's split view was cancelled by the very next canvas
  // autosave: the model under the open find widget was replaced wholesale, which
  // drops its matches and pulls focus out of the search box. Typing a second
  // character then did it again — the find box was unusable rather than merely
  // flickering.
  useEffect(() => {
    const editor = editorRef.current;
    if (!editor) return;
    const busyUri = editor.hasWidgetFocus() ? editor.getModel()?.uri.toString() : undefined;
    const { files } = useVfs.getState();

    syncingRef.current = true;
    try {
      for (const [path, model] of modelsRef.current) {
        if (model.uri.toString() === busyUri) continue;
        const content = files[path];
        if (content != null && model.getValue() !== content) applyContent(model, content);
      }
    } finally {
      syncingRef.current = false;
    }
  }, [rev]);

  // Swap the active model into the editor.
  useEffect(() => {
    const editor = editorRef.current;
    if (!editor) return;
    if (!activePath || isBinaryPath(activePath)) {
      editor.setModel(null);
      return;
    }
    const model = modelsRef.current.get(activePath);
    if (model) {
      // Only when it actually changes: re-attaching the model already showing
      // resets the view state and closes the find widget, and this effect also
      // runs whenever a file is created or deleted anywhere in the project.
      if (editor.getModel() !== model) editor.setModel(model);
      editor.updateOptions({ readOnly: isToolchainFile(activePath) || isGeneratedFile(activePath) });
    }
  }, [activePath, fileKeys]);

  // Follow the admin light/dark theme.
  useEffect(() => {
    if (editorRef.current) applyEditorTheme(resolved);
  }, [resolved]);

  return <div ref={hostRef} className="h-full w-full" />;
}
