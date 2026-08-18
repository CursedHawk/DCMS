import { useEffect, useRef } from 'react';
import type * as Monaco from 'monaco-editor';
import { useTheme } from '../../lib/theme';
import { isBinaryPath } from './binary';
import { isGeneratedFile, isToolchainFile, languageOf, pathOfUri, uriOf } from './paths';
import { applyEditorTheme, monaco, setupMonaco } from './monaco-setup';
import { useVfs } from './vfs';

// The editing surface. Every file in the vfs is a Monaco model (file:/// URI);
// edits flow model -> store. The parent remounts this per site (key=siteId) so
// the model set is created fresh, and create/delete/rename reconcile in place.

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
    const editor = monaco.editor.create(hostRef.current!, {
      automaticLayout: true,
      fontSize: 13,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      tabSize: 2,
      fixedOverflowWidgets: true,
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
        if (content != null && model.getValue() !== content) {
          model.setValue(content);
        }
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
  // The focused model is deliberately skipped: overwriting text under a typing
  // cursor would move it, and content the user just typed already matches the
  // store anyway.
  useEffect(() => {
    const editor = editorRef.current;
    if (!editor) return;
    const focusedUri = editor.hasTextFocus() ? editor.getModel()?.uri.toString() : undefined;
    const { files } = useVfs.getState();

    syncingRef.current = true;
    try {
      for (const [path, model] of modelsRef.current) {
        if (model.uri.toString() === focusedUri) continue;
        const content = files[path];
        if (content != null && model.getValue() !== content) model.setValue(content);
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
      editor.setModel(model);
      editor.updateOptions({ readOnly: isToolchainFile(activePath) || isGeneratedFile(activePath) });
    }
  }, [activePath, fileKeys]);

  // Follow the admin light/dark theme.
  useEffect(() => {
    if (editorRef.current) applyEditorTheme(resolved);
  }, [resolved]);

  return <div ref={hostRef} className="h-full w-full" />;
}
