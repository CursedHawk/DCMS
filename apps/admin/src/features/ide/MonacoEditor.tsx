import { useEffect, useRef } from 'react';
import type * as Monaco from 'monaco-editor';
import { useTheme } from '../../lib/theme';
import { isBinaryPath } from './binary';
import { isToolchainFile, languageOf, pathOfUri, uriOf } from './paths';
import { applyEditorTheme, monaco, setupMonaco } from './monaco-setup';
import { useVfs } from './vfs';

// The editing surface. Every file in the vfs is a Monaco model (file:/// URI);
// edits flow model -> store. The parent remounts this per site (key=siteId) so
// the model set is created fresh, and create/delete/rename reconcile in place.

export function MonacoEditor() {
  const hostRef = useRef<HTMLDivElement>(null);
  const editorRef = useRef<Monaco.editor.IStandaloneCodeEditor | null>(null);
  const modelsRef = useRef<Map<string, Monaco.editor.ITextModel>>(new Map());
  const { resolved } = useTheme();

  const activePath = useVfs((s) => s.activePath);
  const fileKeys = useVfs((s) => Object.keys(s.files).sort().join('\n'));

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
      const model = editor.getModel();
      if (!model) return;
      const path = pathOfUri(model.uri.toString());
      if (isToolchainFile(path)) return;
      useVfs.getState().writeFile(path, model.getValue());
    });

    return () => {
      sub.dispose();
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
      editor.updateOptions({ readOnly: isToolchainFile(activePath) });
    }
  }, [activePath, fileKeys]);

  // Follow the admin light/dark theme.
  useEffect(() => {
    if (editorRef.current) applyEditorTheme(resolved);
  }, [resolved]);

  return <div ref={hostRef} className="h-full w-full" />;
}
