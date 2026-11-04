import type * as Monaco from 'monaco-editor';

/**
 * C# in the editor (ADR 0030). Monaco's own `csharp` language gives the highlighting; this wires
 * completion, hover, signature help, go to definition, references, formatting and diagnostics to
 * Roslyn, running in csharp.worker.ts. The worker starts with the first C# model, so a repository
 * without any never downloads it.
 *
 * Positions cross as character offsets into a model's text; each question is preceded by that
 * model's text when it changed, and the worker answers in order.
 */

/** Where the workspace's generated SDK sits, as a read-only model (IdePage creates it). */
export const SDK_URI = 'file:///.dcms/Sdk.g.cs';

export interface CSharpDiagnostic {
  message: string;
  severity: 'error' | 'warning';
  code: string;
  start: number;
  end: number;
}

interface Location {
  path: string;
  start: number;
  end: number;
}

let worker: Worker | undefined;
let nextId = 0;
const pending = new Map<number, { resolve(value: unknown): void; reject(error: Error): void }>();

function call<T>(method: string, ...args: unknown[]): Promise<T> {
  worker ??= createWorker();
  const id = ++nextId;
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve: resolve as (value: unknown) => void, reject });
    worker!.postMessage({ id, base: import.meta.env.BASE_URL, method, args });
  });
}

let ready = false;
const readyListeners = new Set<() => void>();

/**
 * Calls `listener` once Roslyn has answered for the first time: .NET's first boot takes longer
 * than a type check waits, so whatever asked too early asks again then.
 */
export function onCSharpReady(listener: () => void): () => void {
  if (ready) return () => {};
  readyListeners.add(listener);
  return () => readyListeners.delete(listener);
}

function createWorker(): Worker {
  const created = new Worker(new URL('./csharp.worker.ts', import.meta.url), { type: 'module' });
  created.onmessage = (event: MessageEvent<{ id: number; result?: unknown; error?: string }>) => {
    if (!ready && event.data.error === undefined) {
      ready = true;
      for (const listener of readyListeners) listener();
      readyListeners.clear();
    }
    const waiting = pending.get(event.data.id);
    pending.delete(event.data.id);
    if (event.data.error !== undefined) waiting?.reject(new Error(event.data.error));
    else waiting?.resolve(event.data.result);
  };
  return created;
}

const pathOf = (model: Monaco.editor.ITextModel) => model.uri.path.replace(/^\/+/, '');

const synced = new Map<string, number>();

/** Sends the model's text if Roslyn has an older one. Queued before whatever is asked next. */
function sync(model: Monaco.editor.ITextModel): void {
  const key = model.uri.toString();
  const version = model.getAlternativeVersionId();
  if (synced.get(key) === version) return;
  synced.set(key, version);
  void call('Set', pathOf(model), model.getValue());
}

const parse = <T>(json: string): T => JSON.parse(json) as T;

/** Errors and warnings of one C# model, for the Problems panel. */
export async function csharpDiagnostics(model: Monaco.editor.ITextModel): Promise<CSharpDiagnostic[]> {
  sync(model);
  return parse<CSharpDiagnostic[]>(await call<string>('Diagnostics', pathOf(model)));
}

let registered = false;

export function registerCSharp(monaco: typeof Monaco): void {
  if (registered) return;
  registered = true;

  const range = (model: Monaco.editor.ITextModel, start: number, end: number) => {
    const from = model.getPositionAt(start);
    const to = model.getPositionAt(end);
    return new monaco.Range(from.lineNumber, from.column, to.lineNumber, to.column);
  };

  const locations = (found: Location[]): Monaco.languages.Location[] =>
    found.flatMap((l) => {
      const uri = monaco.Uri.parse(`file:///${l.path}`);
      const model = monaco.editor.getModel(uri);
      return model ? [{ uri, range: range(model, l.start, l.end) }] : [];
    });

  const kinds: Record<string, Monaco.languages.CompletionItemKind> = {
    Class: monaco.languages.CompletionItemKind.Class,
    Record: monaco.languages.CompletionItemKind.Class,
    Structure: monaco.languages.CompletionItemKind.Struct,
    Interface: monaco.languages.CompletionItemKind.Interface,
    Enum: monaco.languages.CompletionItemKind.Enum,
    EnumMember: monaco.languages.CompletionItemKind.EnumMember,
    Delegate: monaco.languages.CompletionItemKind.Function,
    Method: monaco.languages.CompletionItemKind.Method,
    ExtensionMethod: monaco.languages.CompletionItemKind.Method,
    Property: monaco.languages.CompletionItemKind.Property,
    Field: monaco.languages.CompletionItemKind.Field,
    Event: monaco.languages.CompletionItemKind.Event,
    Constant: monaco.languages.CompletionItemKind.Constant,
    Local: monaco.languages.CompletionItemKind.Variable,
    Parameter: monaco.languages.CompletionItemKind.Variable,
    RangeVariable: monaco.languages.CompletionItemKind.Variable,
    Namespace: monaco.languages.CompletionItemKind.Module,
    Keyword: monaco.languages.CompletionItemKind.Keyword,
    Snippet: monaco.languages.CompletionItemKind.Snippet,
    TypeParameter: monaco.languages.CompletionItemKind.TypeParameter,
  };

  // Diagnostics as squiggles, a moment after typing stops: half-typed code is all errors.
  monaco.editor.onDidCreateModel((model) => {
    if (model.getLanguageId() !== 'csharp') return;
    const key = model.uri.toString();
    if (key === SDK_URI) {
      sync(model);
      const changed = model.onDidChangeContent(() => sync(model));
      model.onWillDispose(() => changed.dispose());
      return;
    }
    let timer: ReturnType<typeof setTimeout> | undefined;
    const check = () => {
      clearTimeout(timer);
      timer = setTimeout(() => {
        void csharpDiagnostics(model)
          .then((found) => {
            if (model.isDisposed()) return;
            monaco.editor.setModelMarkers(model, 'csharp', found.map((d) => {
              const r = range(model, d.start, Math.max(d.end, d.start + 1));
              return {
                severity: d.severity === 'error' ? monaco.MarkerSeverity.Error : monaco.MarkerSeverity.Warning,
                message: d.message,
                code: d.code,
                startLineNumber: r.startLineNumber,
                startColumn: r.startColumn,
                endLineNumber: r.endLineNumber,
                endColumn: r.endColumn,
              };
            }));
          })
          .catch(() => undefined);
      }, 600);
    };
    const changed = model.onDidChangeContent(check);
    model.onWillDispose(() => {
      changed.dispose();
      clearTimeout(timer);
      synced.delete(key);
      void call('Remove', pathOf(model)).catch(() => undefined);
    });
    check();
  });

  monaco.languages.registerCompletionItemProvider('csharp', {
    triggerCharacters: ['.', ' ', '<'],
    async provideCompletionItems(model, position) {
      sync(model);
      const list = parse<{
        start: number;
        end: number;
        items: { index: number; label: string; insert: string; filter: string; sort: string; kind: string; detail?: string }[];
      } | null>(await call<string>('Complete', pathOf(model), model.getOffsetAt(position)));
      if (!list) return { suggestions: [] };
      const replace = range(model, list.start, list.end);
      return {
        suggestions: list.items.map((item) => ({
          label: item.label,
          insertText: item.insert,
          filterText: item.filter,
          sortText: item.sort,
          detail: item.detail || undefined,
          kind: kinds[item.kind] ?? monaco.languages.CompletionItemKind.Text,
          range: replace,
          index: item.index,
        })),
      };
    },
    async resolveCompletionItem(item) {
      const description = await call<string>('Resolve', (item as { index?: number }).index ?? -1);
      return description ? { ...item, documentation: { value: '```csharp\n' + description + '\n```' } } : item;
    },
  });

  monaco.languages.registerHoverProvider('csharp', {
    async provideHover(model, position) {
      sync(model);
      const hover = parse<{ text: string; start: number; end: number } | null>(
        await call<string>('Hover', pathOf(model), model.getOffsetAt(position)),
      );
      return hover ? { contents: [{ value: hover.text }], range: range(model, hover.start, hover.end) } : null;
    },
  });

  monaco.languages.registerSignatureHelpProvider('csharp', {
    signatureHelpTriggerCharacters: ['(', ','],
    signatureHelpRetriggerCharacters: [','],
    async provideSignatureHelp(model, position) {
      sync(model);
      const help = parse<{
        active: number;
        parameter: number;
        signatures: { label: string; documentation: string; parameters: string[] }[];
      } | null>(await call<string>('Signature', pathOf(model), model.getOffsetAt(position)));
      if (!help) return null;
      return {
        value: {
          activeSignature: help.active,
          activeParameter: help.parameter,
          signatures: help.signatures.map((s) => ({
            label: s.label,
            documentation: s.documentation ? { value: s.documentation } : undefined,
            parameters: s.parameters.map((label) => ({ label })),
          })),
        },
        dispose() {},
      };
    },
  });

  monaco.languages.registerDefinitionProvider('csharp', {
    async provideDefinition(model, position) {
      sync(model);
      return locations(parse<Location[]>(await call<string>('Definition', pathOf(model), model.getOffsetAt(position))));
    },
  });

  monaco.languages.registerReferenceProvider('csharp', {
    async provideReferences(model, position) {
      sync(model);
      return locations(parse<Location[]>(await call<string>('References', pathOf(model), model.getOffsetAt(position))));
    },
  });

  monaco.languages.registerDocumentFormattingEditProvider('csharp', {
    async provideDocumentFormattingEdits(model) {
      sync(model);
      const text = await call<string>('Format', pathOf(model));
      return text ? [{ range: model.getFullModelRange(), text }] : [];
    },
  });
}
