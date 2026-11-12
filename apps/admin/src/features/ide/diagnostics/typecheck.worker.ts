/// <reference lib="webworker" />
import ts from 'typescript';
import type { MessageChain, TsDiagnostic } from './typeCheck';

/**
 * A TypeScript language service over an agent run's own files (ADR 0033).
 *
 * <p>The IDE's checker is Monaco's worker, which only knows the files the open editor has
 * models for — one site, and only while its editor is mounted. An agent run that outlives the
 * page, or works on another site than the one on screen, got an empty program from it and a
 * silent "no type errors". This one is given the run's files and checks exactly those.</p>
 *
 * <p>Options mirror `site-source/monaco-setup.ts`, which mirrors the site template's tsconfig,
 * so this answers what the editor answers.</p>
 */

export interface TypeCheckRequest {
  id: number;
  /** Project files by path (no leading slash), as the run holds them. */
  files: Record<string, string>;
  /** Paths to report diagnostics for. */
  paths: string[];
  /** Declarations the editor also loads: the palette's React types and ambient stubs. */
  extraLibs: { path: string; content: string }[];
  /** A Functions repository runs without a DOM (ADR 0029). */
  lib?: string[];
}

export interface TypeCheckResponse {
  id: number;
  diagnostics?: Record<string, TsDiagnostic[]>;
  error?: string;
}

const LIB_DIR = '/lib/';
const libSources = import.meta.glob('/node_modules/typescript/lib/lib.*.d.ts', {
  query: '?raw',
  import: 'default',
  eager: true,
}) as Record<string, string>;
const LIBS = new Map(Object.entries(libSources).map(([path, text]) => [LIB_DIR + path.split('/').pop(), text]));

/** `file:///node_modules/x/index.d.ts` and `node_modules/x` alike become `/node_modules/x`. */
function toAbsolute(path: string): string {
  return `/${path.replace(/^file:\/\/\//, '').replace(/^\/+/, '')}`;
}

let project = new Map<string, string>();
let extra = new Map<string, string>();
const versions = new Map<string, number>();
let roots: string[] = [];
let options: ts.CompilerOptions = {};

function read(path: string): string | undefined {
  return project.get(path) ?? extra.get(path) ?? LIBS.get(path);
}

function directoryExists(dir: string): boolean {
  const prefix = dir.endsWith('/') ? dir : `${dir}/`;
  for (const map of [project, extra]) {
    for (const key of map.keys()) if (key.startsWith(prefix)) return true;
  }
  return prefix === LIB_DIR;
}

const host: ts.LanguageServiceHost = {
  getScriptFileNames: () => roots,
  getScriptVersion: (path) => String(versions.get(path) ?? 0),
  getScriptSnapshot: (path) => {
    const text = read(path);
    return text === undefined ? undefined : ts.ScriptSnapshot.fromString(text);
  },
  getCurrentDirectory: () => '/',
  getCompilationSettings: () => options,
  getDefaultLibFileName: (o) => LIB_DIR + ts.getDefaultLibFileName(o),
  fileExists: (path) => read(path) !== undefined,
  readFile: read,
  directoryExists,
  getDirectories: () => [],
};

const service = ts.createLanguageService(host, ts.createDocumentRegistry());

function chain(message: string | ts.DiagnosticMessageChain): string | MessageChain {
  if (typeof message === 'string') return message;
  return { messageText: message.messageText, next: message.next?.map((m) => chain(m) as MessageChain) };
}

/** Plain data only: a `ts.Diagnostic` holds its SourceFile, which postMessage cannot clone. */
function plain(diagnostic: ts.Diagnostic): TsDiagnostic {
  return {
    category: diagnostic.category,
    code: diagnostic.code,
    start: diagnostic.start,
    length: diagnostic.length,
    messageText: chain(diagnostic.messageText),
  };
}

function update(request: TypeCheckRequest): void {
  const next = new Map(Object.entries(request.files).map(([path, text]) => [toAbsolute(path), text]));
  for (const [path, text] of next) {
    if (project.get(path) !== text) versions.set(path, (versions.get(path) ?? 0) + 1);
  }
  for (const path of project.keys()) {
    if (!next.has(path)) versions.set(path, (versions.get(path) ?? 0) + 1);
  }
  project = next;
  extra = new Map(request.extraLibs.map((lib) => [toAbsolute(lib.path), lib.content]));
  roots = [...project.keys()].filter((path) => /\.(tsx?|jsx?|mts|cts)$/.test(path) || path.endsWith('.d.ts'));
  roots.push(...[...extra.keys()].filter((path) => !path.includes('/node_modules/@types/')));
  options = {
    target: ts.ScriptTarget.ESNext,
    module: ts.ModuleKind.ESNext,
    moduleResolution: ts.ModuleResolutionKind.Node10,
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
    noEmit: true,
    typeRoots: ['/node_modules/@types'],
    ...(request.lib ? { lib: request.lib.map((l) => `lib.${l}.d.ts`) } : {}),
  };
}

self.onmessage = (event: MessageEvent<TypeCheckRequest>) => {
  const request = event.data;
  try {
    update(request);
    const diagnostics: Record<string, TsDiagnostic[]> = {};
    for (const path of request.paths) {
      const file = toAbsolute(path);
      if (!project.has(file)) continue;
      diagnostics[path] = [
        ...service.getSyntacticDiagnostics(file),
        ...service.getSemanticDiagnostics(file),
      ].map(plain);
    }
    (self as unknown as Worker).postMessage({ id: request.id, diagnostics } satisfies TypeCheckResponse);
  } catch (error) {
    (self as unknown as Worker).postMessage({
      id: request.id,
      error: (error as Error)?.message ?? 'The type checker failed.',
    } satisfies TypeCheckResponse);
  }
};
