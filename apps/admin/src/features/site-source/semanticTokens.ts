import type * as Monaco from 'monaco-editor';

/**
 * Semantic colouring: what a name IS (a class, a method, a parameter, a constant), which
 * Monaco's grammars cannot know. TypeScript's language service (ts.worker.ts) and Roslyn
 * (csharp/client.ts) both answer in this legend, so the themes (editorThemes.ts) colour both
 * languages alike. Only the visible range is asked for: a generated SDK can be megabytes.
 */
export const LEGEND: Monaco.languages.SemanticTokensLegend = {
  tokenTypes: [
    'namespace', 'class', 'enum', 'interface', 'struct', 'typeParameter', 'type', 'parameter',
    'variable', 'property', 'enumMember', 'event', 'function', 'method', 'label',
  ],
  // Only what a theme colours differently. Monaco's standalone themes match "type.modifier…"
  // as a dotted path, so a modifier no rule names would hide the ones after it.
  tokenModifiers: ['readonly'],
};

export const TOKEN = Object.fromEntries(LEGEND.tokenTypes.map((t, i) => [t, i])) as Record<string, number>;

/**
 * `spans` as [start offset, length, token type, modifier bits]*, in document order, into
 * Monaco's relative encoding. A span that crosses a line end is dropped: names never do.
 */
export function encodeTokens(model: Monaco.editor.ITextModel, spans: ArrayLike<number>): Monaco.languages.SemanticTokens {
  const data: number[] = [];
  let prevLine = 0;
  let prevChar = 0;
  for (let i = 0; i + 3 < spans.length; i += 4) {
    const start = model.getPositionAt(spans[i]);
    const end = model.getPositionAt(spans[i] + spans[i + 1]);
    if (start.lineNumber !== end.lineNumber || spans[i + 2] < 0) continue;
    const line = start.lineNumber - 1;
    const char = start.column - 1;
    data.push(line - prevLine, line === prevLine ? char - prevChar : char, spans[i + 1], spans[i + 2], spans[i + 3]);
    prevLine = line;
    prevChar = char;
  }
  return { data: new Uint32Array(data) };
}

/** TypeScript's 2020 classification types, in its order, as legend indices. */
const TS_TYPES = ['class', 'enum', 'interface', 'namespace', 'typeParameter', 'type', 'parameter', 'variable', 'enumMember', 'property', 'function', 'method'].map((t) => TOKEN[t]);
const TS_READONLY = 1 << 3;

/** TypeScript's encoded classifications ([start, length, (type+1)<<8 | modifiers]*) into the legend. */
export function fromTypeScript(spans: number[]): number[] {
  const out: number[] = [];
  for (let i = 0; i + 2 < spans.length; i += 3) {
    const type = TS_TYPES[(spans[i + 2] >> 8) - 1];
    if (type === undefined) continue;
    out.push(spans[i], spans[i + 1], type, spans[i + 2] & TS_READONLY ? 1 : 0);
  }
  return out;
}

interface ClassifyingWorker {
  getEncodedSemanticClassifications(fileName: string, start: number, length: number): Promise<number[]>;
}

export function registerTypeScriptSemanticTokens(monaco: typeof Monaco): void {
  const ts = monaco.languages.typescript;
  for (const [language, getWorker] of [
    ['typescript', ts.getTypeScriptWorker],
    ['javascript', ts.getJavaScriptWorker],
  ] as const) {
    monaco.languages.registerDocumentRangeSemanticTokensProvider(language, {
      getLegend: () => LEGEND,
      async provideDocumentRangeSemanticTokens(model, range) {
        // The language's worker is set up just after its first model: wait for it briefly.
        for (let attempt = 0; ; attempt++) {
          try {
            const worker = (await (await getWorker())(model.uri)) as unknown as ClassifyingWorker;
            if (model.isDisposed()) return null;
            const start = model.getOffsetAt(range.getStartPosition());
            const end = model.getOffsetAt(range.getEndPosition());
            const spans = await worker.getEncodedSemanticClassifications(model.uri.toString(), start, end - start);
            return model.isDisposed() ? null : encodeTokens(model, fromTypeScript(spans));
          } catch {
            if (attempt >= 20 || model.isDisposed()) return null;
            await new Promise((resolve) => setTimeout(resolve, 250));
          }
        }
      },
    });
  }
}
