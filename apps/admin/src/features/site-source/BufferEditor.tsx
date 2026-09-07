import { useEffect, useRef } from 'react';
import type * as Monaco from 'monaco-editor';
import { useTheme } from '@dcms/ui';
import { languageOf } from './paths';
import { applyEditorTheme, monaco, setupMonaco } from './monaco-setup';

/**
 * A standalone editor over a buffer that is not a file in the workspace.
 *
 * <p>`MonacoEditor` is bound to the VFS — it owns a model per open file and writes every
 * keystroke back into the workspace, which is exactly right for editing a site and exactly
 * wrong for a merge buffer that must not touch the working tree until the merge is committed.
 * This one holds a single throwaway model and hands changes to its caller.</p>
 *
 * <p>Syntax highlighting is the whole reason this is Monaco rather than a textarea: the text
 * being merged is source, and merging it without highlighting is materially harder.</p>
 */
export function BufferEditor({
  path,
  value,
  onChange,
  readOnly,
}: {
  /** Only used to choose a language. No model of this path is created. */
  path: string;
  value: string;
  onChange?: (value: string) => void;
  readOnly?: boolean;
}) {
  const hostRef = useRef<HTMLDivElement>(null);
  const editorRef = useRef<Monaco.editor.IStandaloneCodeEditor | null>(null);
  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;
  const { resolved } = useTheme();

  useEffect(() => {
    setupMonaco();
    const model = monaco.editor.createModel(value, languageOf(path));
    const editor = monaco.editor.create(hostRef.current!, {
      model,
      automaticLayout: true,
      readOnly,
      fontSize: 13,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      // Conflict markers are long lines of punctuation; wrapping keeps them readable in a
      // panel that is narrower than an editor pane.
      wordWrap: 'on',
    });
    editorRef.current = editor;
    applyEditorTheme(resolved);

    const sub = model.onDidChangeContent(() => onChangeRef.current?.(model.getValue()));

    return () => {
      sub.dispose();
      editor.dispose();
      model.dispose();
      editorRef.current = null;
    };
    // `value` is deliberately not a dependency: it is the INITIAL content. Recreating the
    // editor on every keystroke would lose the cursor, the selection and the undo stack.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [path, readOnly]);

  useEffect(() => {
    if (editorRef.current) applyEditorTheme(resolved);
  }, [resolved]);

  return <div ref={hostRef} className="h-full w-full" />;
}
