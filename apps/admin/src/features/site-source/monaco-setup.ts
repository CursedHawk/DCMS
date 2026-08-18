import * as monaco from 'monaco-editor';
import editorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker';
import cssWorker from 'monaco-editor/esm/vs/language/css/css.worker?worker';
import htmlWorker from 'monaco-editor/esm/vs/language/html/html.worker?worker';
import jsonWorker from 'monaco-editor/esm/vs/language/json/json.worker?worker';
import tsWorker from 'monaco-editor/esm/vs/language/typescript/ts.worker?worker';

// One-time Monaco configuration shared by every DCMS source editor — the Mode B
// IDE and the Mode A builder's code view. All language intelligence
// (TypeScript/JS/HTML/CSS/JSON) runs in these browser web workers; there is no
// server-side language service.
//
// Surface-specific type acquisition is NOT done here. The Mode B dependency
// palette's typings are large and meaningless to the Mode A builder, and the
// builder's HTML component data is meaningless to the IDE, so each surface seeds
// its own on mount (see ide/types/palette.ts, builder/code/htmlData.ts). This
// function is idempotent, so those callers can safely call it first.

let configured = false;

export function setupMonaco(): typeof monaco {
  if (configured) return monaco;
  configured = true;

  // Required: tell Monaco which worker bundle backs each language label.
  (self as unknown as { MonacoEnvironment: monaco.Environment }).MonacoEnvironment = {
    getWorker(_moduleId: string, label: string) {
      switch (label) {
        case 'json':
          return new jsonWorker();
        case 'css':
        case 'scss':
        case 'less':
          return new cssWorker();
        case 'html':
        case 'handlebars':
        case 'razor':
          return new htmlWorker();
        case 'typescript':
        case 'javascript':
          return new tsWorker();
        default:
          return new editorWorker();
      }
    },
  };

  const ts = monaco.languages.typescript;

  // Mirror packages/site-template-react/tsconfig.json so in-browser diagnostics
  // match what the offline site-builder compiles. moduleResolution NodeJs (2)
  // resolves the @types we seed under file:///node_modules/@types.
  const compilerOptions: monaco.languages.typescript.CompilerOptions = {
    target: ts.ScriptTarget.ESNext,
    module: ts.ModuleKind.ESNext,
    moduleResolution: ts.ModuleResolutionKind.NodeJs,
    jsx: ts.JsxEmit.ReactJSX,
    jsxImportSource: 'react',
    allowJs: true,
    allowNonTsExtensions: true,
    esModuleInterop: true,
    allowSyntheticDefaultImports: true,
    isolatedModules: true,
    skipLibCheck: true,
    strict: true,
    resolveJsonModule: true,
    typeRoots: ['file:///node_modules/@types'],
  };
  ts.typescriptDefaults.setCompilerOptions(compilerOptions);
  ts.javascriptDefaults.setCompilerOptions(compilerOptions);

  // Sync every open model into the worker so cross-file type-checking, rename
  // and go-to-definition work across the whole project.
  ts.typescriptDefaults.setEagerModelSync(true);
  ts.javascriptDefaults.setEagerModelSync(true);

  ts.typescriptDefaults.setDiagnosticsOptions({
    noSemanticValidation: false,
    noSyntaxValidation: false,
    // 2792: cannot find module (covered by ambient palette stubs, but be lenient).
    diagnosticCodesToIgnore: [],
  });

  defineThemes();

  return monaco;
}

function defineThemes(): void {
  // Thin wrappers over Monaco's built-ins so the surrounding admin chrome
  // background matches the editor gutter in both themes.
  monaco.editor.defineTheme('dcms-dark', {
    base: 'vs-dark',
    inherit: true,
    rules: [],
    colors: { 'editor.background': '#0b0e14' },
  });
  monaco.editor.defineTheme('dcms-light', {
    base: 'vs',
    inherit: true,
    rules: [],
    colors: {},
  });
}

export function applyEditorTheme(resolved: 'light' | 'dark'): void {
  monaco.editor.setTheme(resolved === 'dark' ? 'dcms-dark' : 'dcms-light');
}

export { monaco };
