import { useEffect, useRef } from 'react';
import type * as Monaco from 'monaco-editor';
import { useTheme } from '@dcms/ui';
import type { CodeEditorProps } from '@dcms/plugin-ui';
import { applyEditorTheme, monaco, setupMonaco } from '../features/site-source/monaco-setup';

type JsonSchemaEntry = { uri: string; fileMatch?: string[]; schema?: unknown };

/**
 * Puts `schema` in force for one document without dropping anyone else's: Monaco keeps a single
 * list of JSON schemas for the whole page. Null takes the document's entry out again.
 */
function withDocumentSchema(current: readonly JsonSchemaEntry[], uri: string, schema: unknown | null): JsonSchemaEntry[] {
  const schemaUri = `${uri}#schema`;
  const others = current.filter((s) => s.uri !== schemaUri);
  return schema == null ? others : [...others, { uri: schemaUri, fileMatch: [uri], schema }];
}

function setDocumentSchema(uri: string, schema: unknown | null): void {
  const json = monaco.languages.json.jsonDefaults;
  json.setDiagnosticsOptions({
    ...json.diagnosticsOptions,
    validate: true,
    enableSchemaRequest: false,
    schemas: withDocumentSchema(json.diagnosticsOptions.schemas ?? [], uri, schema),
  });
}

/**
 * The code editor plugins embed (`PluginHost.components.CodeEditor`): one controlled document,
 * not tied to the site workspace the way `MonacoEditor` is. The console owns Monaco — its
 * workers, themes and compiler options — so a plugin gets all of it without bundling or
 * configuring a second copy (see `site-source/monaco-setup.ts`).
 */
export function CodeEditor({
  uri,
  language,
  value,
  onChange,
  readOnly,
  height = 240,
  compact,
  ariaLabel,
  markers,
  extraLibs,
  jsonSchema,
  onMonaco,
}: CodeEditorProps) {
  const hostRef = useRef<HTMLDivElement>(null);
  const editorRef = useRef<Monaco.editor.IStandaloneCodeEditor | null>(null);
  const onChangeRef = useRef(onChange);
  onChangeRef.current = onChange;
  const onMonacoRef = useRef(onMonaco);
  onMonacoRef.current = onMonaco;
  const { resolved } = useTheme();

  useEffect(() => {
    setupMonaco();
    const registered = onMonacoRef.current?.(monaco);
    const parsed = monaco.Uri.parse(uri);
    // A model another editor already has open is shared, never disposed from here.
    const existing = monaco.editor.getModel(parsed);
    const model = existing ?? monaco.editor.createModel(value, language, parsed);
    const editor = monaco.editor.create(hostRef.current!, {
      model,
      automaticLayout: true,
      readOnly,
      ariaLabel,
      fontSize: 13,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      wordWrap: 'on',
      fixedOverflowWidgets: true,
      ...(compact
        ? { lineNumbers: 'off', glyphMargin: false, folding: false, lineDecorationsWidth: 4, lineNumbersMinChars: 0, renderLineHighlight: 'none' }
        : {}),
    });
    editorRef.current = editor;
    applyEditorTheme(resolved);

    const sub = model.onDidChangeContent(() => onChangeRef.current?.(model.getValue()));
    return () => {
      sub.dispose();
      editor.dispose();
      if (!existing) model.dispose();
      registered?.dispose();
      editorRef.current = null;
    };
    // `value` is the initial content here; later values arrive through the effect below, which
    // keeps the cursor and undo stack instead of recreating the editor on every keystroke.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [uri, language]);

  useEffect(() => {
    editorRef.current?.updateOptions({ readOnly });
  }, [readOnly]);

  useEffect(() => {
    const model = editorRef.current?.getModel();
    if (model && model.getValue() !== value) {
      // An edit, not setValue: a value replaced from outside (a reload) can still be undone.
      model.pushEditOperations([], [{ range: model.getFullModelRange(), text: value }], () => null);
    }
  }, [value]);

  useEffect(() => {
    if (editorRef.current) applyEditorTheme(resolved);
  }, [resolved]);

  useEffect(() => {
    const model = editorRef.current?.getModel();
    if (!model) return;
    monaco.editor.setModelMarkers(
      model,
      'dcms-host',
      (markers ?? []).map((m) => {
        const start = model.getPositionAt(m.start);
        const end = model.getPositionAt(Math.max(m.end, m.start + 1));
        return {
          message: m.message,
          severity: m.severity === 'warning' ? monaco.MarkerSeverity.Warning : monaco.MarkerSeverity.Error,
          startLineNumber: start.lineNumber,
          startColumn: start.column,
          endLineNumber: end.lineNumber,
          endColumn: end.column,
        };
      }),
    );
  }, [markers, value, uri]);

  useEffect(() => {
    if (!extraLibs?.length) return undefined;
    const ts = monaco.languages.typescript;
    const libs = extraLibs.flatMap((lib) => [
      ts.typescriptDefaults.addExtraLib(lib.content, lib.path),
      ts.javascriptDefaults.addExtraLib(lib.content, lib.path),
    ]);
    return () => libs.forEach((l) => l.dispose());
  }, [extraLibs]);

  useEffect(() => {
    if (!jsonSchema) return undefined;
    setDocumentSchema(uri, jsonSchema);
    return () => setDocumentSchema(uri, null);
  }, [uri, jsonSchema]);

  return <div ref={hostRef} style={{ height }} className="w-full overflow-hidden rounded-md border" />;
}
