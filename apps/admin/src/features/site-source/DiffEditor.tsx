import { useEffect, useRef } from 'react';
import type * as Monaco from 'monaco-editor';
import { useTheme } from '@dcms/ui';
import { languageOf } from './paths';
import { applyEditorTheme, monaco, setupMonaco } from './monaco-setup';

// A read-only Monaco diff of a single file: original (branch HEAD) vs modified
// (the working draft). Models are created/disposed with the component. `null`
// content means the file is added (no original) or deleted (no modified).
export function DiffEditor({
  path,
  original,
  modified,
}: {
  path: string;
  original: string | null;
  modified: string | null;
}) {
  const hostRef = useRef<HTMLDivElement>(null);
  const editorRef = useRef<Monaco.editor.IStandaloneDiffEditor | null>(null);
  const { resolved } = useTheme();

  useEffect(() => {
    setupMonaco();
    const editor = monaco.editor.createDiffEditor(hostRef.current!, {
      automaticLayout: true,
      readOnly: true,
      renderSideBySide: true,
      fontSize: 13,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
    });
    editorRef.current = editor;
    applyEditorTheme(resolved);
    const lang = languageOf(path);
    const originalModel = monaco.editor.createModel(original ?? '', lang);
    const modifiedModel = monaco.editor.createModel(modified ?? '', lang);
    editor.setModel({ original: originalModel, modified: modifiedModel });

    return () => {
      editor.dispose();
      originalModel.dispose();
      modifiedModel.dispose();
      editorRef.current = null;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [path, original, modified]);

  useEffect(() => {
    if (editorRef.current) applyEditorTheme(resolved);
  }, [resolved]);

  return <div ref={hostRef} className="h-full w-full" />;
}
