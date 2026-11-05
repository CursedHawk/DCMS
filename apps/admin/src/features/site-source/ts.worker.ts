/// <reference lib="webworker" />
import { initialize, TypeScriptWorker } from 'monaco-editor/esm/vs/language/typescript/ts.worker.js';

// Monaco's TypeScript worker plus the one question it does not expose: TypeScript's semantic
// classification (classes, functions, parameters, constants…), which semanticTokens.ts turns
// into colours. Monaco's client proxies every method of the worker object, this one included.
class DcmsTypeScriptWorker extends TypeScriptWorker {
  async getEncodedSemanticClassifications(fileName: string, start: number, length: number): Promise<number[]> {
    return this.getLanguageService().getEncodedSemanticClassifications(fileName, { start, length }, '2020').spans;
  }
}

// What Monaco's own ts.worker.js does on its first message, with the subclass.
self.onmessage = () => {
  initialize((ctx, createData) => new DcmsTypeScriptWorker(ctx, createData));
};
