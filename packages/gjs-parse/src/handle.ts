import { parseCss, symbolsIn } from './css';
import { formatCss, formatHtml } from './format';
import { parseHtml } from './html';
import { lintHtml, toMarkers } from './lint';
import { classUsagesIn } from './usage';
import type { ParseWorkerRequest, ParseWorkerResponse, SymbolSource } from './protocol';

/**
 * The worker's whole body, as a pure function. Keeping it here rather than
 * inside the `.worker.ts` shell is what makes the worker testable: these are the
 * expensive operations, and they are exercised directly by this package's tests
 * instead of only through a browser.
 */
export function handleRequest(request: ParseWorkerRequest): ParseWorkerResponse {
  const { id, key } = request;
  try {
    switch (request.kind) {
      case 'parse-html':
        return { kind: 'parse-html', id, key, result: parseHtml(request.input, request.options) };

      case 'parse-css':
        return { kind: 'parse-css', id, key, result: parseCss(request.input) };

      case 'format':
        return {
          kind: 'format',
          id,
          key,
          html: request.html === undefined ? undefined : formatHtml(request.html),
          css: request.css === undefined ? undefined : formatCss(request.css),
        };

      case 'index': {
        const classSources: Record<string, SymbolSource> = {};
        const propertySources: Record<string, SymbolSource> = {};
        for (const [path, source] of Object.entries(request.stylesheets)) {
          const { classes, properties } = symbolsIn(source);
          // First definition wins, so go-to-definition lands on the base rule
          // rather than on whichever override happens to be indexed last.
          for (const { name, line, column } of classes) {
            classSources[name] ??= { path, line, column };
          }
          for (const { name, line, column } of properties) {
            propertySources[name] ??= { path, line, column };
          }
        }
        const classUsages: Record<string, SymbolSource[]> = {};
        for (const [path, html] of Object.entries(request.pages ?? {})) {
          for (const { name, line, column } of classUsagesIn(html)) {
            (classUsages[name] ??= []).push({ path, line, column });
          }
        }

        return {
          kind: 'index',
          id,
          key,
          // Defined classes only. The linter's "nothing defines this class"
          // rule reads this list, so a class that merely appears in markup must
          // not be in it — that would make the rule silence itself.
          classNames: Object.keys(classSources).sort(),
          customProperties: Object.keys(propertySources).sort(),
          classSources,
          propertySources,
          classUsages,
        };
      }

      case 'lint':
        return {
          kind: 'lint',
          id,
          key,
          diagnostics: toMarkers(request.source, lintHtml(request.source, request.context)),
        };
    }
  } catch (error) {
    return { kind: 'error', id, key, message: (error as Error).message };
  }
}
