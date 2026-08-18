import type { SymbolSource } from '@dcms/gjs-parse';
import type { DcmsComponentSpec } from '@dcms/gjs-schema';
import type * as Monaco from 'monaco-editor';
import { uriOf } from '../../site-source';
import { identityClasses } from './htmlData';

/**
 * The project-aware half of the code view's language intelligence.
 *
 * Monaco's own html/css/json workers already cover syntax, the standard tags and
 * the DCMS component catalogue (see `htmlData.ts`). What they cannot know is the
 * rest of *this* project: which classes any stylesheet actually defines, which
 * theme variables exist, and where each is declared. That index is built in the
 * parse worker; this module turns it into completions, hovers and go-to-definition.
 *
 * The providers read a module-level index rather than closing over one, so the
 * index can be replaced on every edit without re-registering anything — Monaco
 * providers are global to a language, and churning them leaks.
 */

export interface ProjectIndex {
  classNames: string[];
  customProperties: string[];
  classSources: Record<string, SymbolSource>;
  propertySources: Record<string, SymbolSource>;
  classUsages: Record<string, SymbolSource[]>;
}

const EMPTY_INDEX: ProjectIndex = {
  classNames: [],
  customProperties: [],
  classSources: {},
  propertySources: {},
  classUsages: {},
};

let index: ProjectIndex = EMPTY_INDEX;
/** Identity classes of the block catalogue, which exist even before any CSS does. */
let componentClasses: { name: string; doc: string }[] = [];

export function setProjectIndex(next: ProjectIndex): void {
  index = next;
}

export function setComponentClasses(specs: readonly DcmsComponentSpec[]): void {
  componentClasses = identityClasses(specs);
}

/** Reset to the empty state, so a second site does not inherit the first's symbols. */
export function clearProjectIndex(): void {
  index = EMPTY_INDEX;
  componentClasses = [];
}

/** The word being typed inside `class="…"`, or null when the cursor is elsewhere. */
function classContext(line: string): { prefix: string; startColumn: number } | null {
  const attr = /class\s*=\s*(["'])([^"']*)$/.exec(line);
  if (!attr) return null;
  const typed = attr[2]!;
  const lastSpace = typed.lastIndexOf(' ');
  const prefix = typed.slice(lastSpace + 1);
  // +1 for the quote, +1 because Monaco columns are 1-based.
  const startColumn = line.length - prefix.length + 1;
  return { prefix, startColumn };
}

/** The custom property being typed inside `var(…)`, or null. */
function varContext(line: string): { prefix: string; startColumn: number } | null {
  const call = /var\(\s*(-{0,2}[\w-]*)$/.exec(line);
  if (!call) return null;
  const prefix = call[1]!;
  return { prefix, startColumn: line.length - prefix.length + 1 };
}

function classDoc(name: string, source: SymbolSource | undefined): string {
  const component = componentClasses.find((c) => c.name === name);
  if (component) return component.doc;
  return source ? `Defined in \`${source.path}\`` : 'Used in this project';
}

/**
 * Everything offerable in `class="…"`: the classes the project's CSS defines,
 * plus the identity classes of the block catalogue. A block's identity class is
 * what turns hand-written markup into a real component on the canvas, so it has
 * to be discoverable from the code view — that is the whole point of typing
 * markup by hand and having it come back as an editable component.
 */
function classCompletions(): { name: string; doc: string }[] {
  const seen = new Set<string>();
  const out: { name: string; doc: string }[] = [];
  const add = (name: string, doc: string) => {
    if (seen.has(name)) return;
    seen.add(name);
    out.push({ name, doc });
  };

  for (const name of index.classNames) add(name, classDoc(name, index.classSources[name]));
  for (const { name, doc } of componentClasses) add(name, doc);
  // A class the markup already uses but no stylesheet defines still belongs
  // here: it is real in this project, and offering it prevents a second spelling
  // of the same name. The linter separately points out that it styles nothing.
  for (const name of Object.keys(index.classUsages)) add(name, 'Used in this project');
  return out;
}

function locationOf(
  monaco: typeof Monaco,
  source: SymbolSource,
  length: number,
): Monaco.languages.Location {
  return {
    uri: monaco.Uri.parse(uriOf(source.path)),
    range: new monaco.Range(source.line, source.column, source.line, source.column + length),
  };
}

/**
 * Install the project-aware providers. Returns a disposable; call it once per
 * mounted code view.
 */
export function registerBuilderIntellisense(monaco: typeof Monaco): Monaco.IDisposable {
  const disposables: Monaco.IDisposable[] = [];

  // class="…" completion, in HTML.
  disposables.push(
    monaco.languages.registerCompletionItemProvider('html', {
      // A space starts the next class in the same attribute; a quote starts the
      // first. Without these, completion only appears on an explicit Ctrl+Space.
      triggerCharacters: ['"', "'", ' ', '-'],
      provideCompletionItems(model, position) {
        const line = model.getValueInRange({
          startLineNumber: position.lineNumber,
          startColumn: 1,
          endLineNumber: position.lineNumber,
          endColumn: position.column,
        });
        const context = classContext(line);
        if (!context) return { suggestions: [] };

        const range = new monaco.Range(
          position.lineNumber,
          context.startColumn,
          position.lineNumber,
          position.column,
        );
        return {
          suggestions: classCompletions().map(({ name, doc }) => ({
            label: name,
            kind: monaco.languages.CompletionItemKind.Value,
            insertText: name,
            documentation: { value: doc },
            range,
          })),
        };
      },
    }),
  );

  // var(--…) completion, in CSS and in inline styles inside HTML.
  for (const language of ['css', 'html'] as const) {
    disposables.push(
      monaco.languages.registerCompletionItemProvider(language, {
        triggerCharacters: ['(', '-'],
        provideCompletionItems(model, position) {
          const line = model.getValueInRange({
            startLineNumber: position.lineNumber,
            startColumn: 1,
            endLineNumber: position.lineNumber,
            endColumn: position.column,
          });
          const context = varContext(line);
          if (!context) return { suggestions: [] };

          const range = new monaco.Range(
            position.lineNumber,
            context.startColumn,
            position.lineNumber,
            position.column,
          );
          return {
            suggestions: index.customProperties.map((name) => ({
              label: name,
              kind: monaco.languages.CompletionItemKind.Variable,
              insertText: name,
              documentation: {
                value: `Theme variable, defined in \`${index.propertySources[name]?.path ?? 'this project'}\``,
              },
              range,
            })),
          };
        },
      }),
    );
  }

  // Ctrl+Click a class in markup to open the rule that styles it. Cross-model
  // navigation is handled by the editor opener registered in MonacoEditor.
  disposables.push(
    monaco.languages.registerDefinitionProvider('html', {
      provideDefinition(model, position) {
        const word = model.getWordAtPosition(position);
        if (!word) return null;
        const line = model.getLineContent(position.lineNumber);
        // Only inside a class attribute: the same word elsewhere (a tag name, a
        // stray identifier) has nothing to do with the stylesheet.
        if (!/class\s*=\s*["'][^"']*$/.test(line.slice(0, word.startColumn - 1))) return null;
        const source = index.classSources[word.word];
        return source ? locationOf(monaco, source, word.word.length + 1) : null;
      },
    }),
  );

  // Ctrl+Click a var(--x) usage to open its declaration.
  disposables.push(
    monaco.languages.registerDefinitionProvider('css', {
      provideDefinition(model, position) {
        const name = customPropertyAt(model, position);
        if (!name) return null;
        const source = index.propertySources[name];
        return source ? locationOf(monaco, source, name.length) : null;
      },
    }),
  );

  // "Find all references" on a CSS class selector: every page that uses it.
  // This is the question worth answering before changing or deleting a rule, and
  // no per-file language service can answer it.
  disposables.push(
    monaco.languages.registerReferenceProvider('css', {
      provideReferences(model, position) {
        const line = model.getLineContent(position.lineNumber);
        const name = selectorClassAt(line, position.column);
        if (!name) return null;
        return (index.classUsages[name] ?? []).map((usage) =>
          locationOf(monaco, usage, name.length),
        );
      },
    }),
  );

  return { dispose: () => disposables.forEach((d) => d.dispose()) };
}

/** The class selector (`.hero`) under the cursor in a stylesheet, without the dot. */
function selectorClassAt(line: string, column: number): string | null {
  const offset = column - 1;
  for (const match of line.matchAll(/\.([A-Za-z_][\w-]*)/g)) {
    const start = match.index;
    if (offset >= start && offset <= start + match[0].length) return match[1]!;
  }
  return null;
}

/**
 * The custom property under the cursor. Monaco's word definition stops at `-`,
 * so `--dcms-color-brand` reads as a fragment; the name is recovered from the
 * raw line instead.
 */
function customPropertyAt(model: Monaco.editor.ITextModel, position: Monaco.Position): string | null {
  const line = model.getLineContent(position.lineNumber);
  const offset = position.column - 1;
  for (const match of line.matchAll(/--[\w-]+/g)) {
    const start = match.index;
    if (offset >= start && offset <= start + match[0].length) return match[0];
  }
  return null;
}
