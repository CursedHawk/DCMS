import type { BuildProblem } from '../ide/preview/problems';
import type { TypeCheckRequest, TypeCheckResponse } from '../ide/diagnostics/typecheck.worker';

/**
 * The deterministic checks for one agent run, over the run's own files (ADR 0033).
 *
 * <p>These used to borrow the page: `check_build` asked the open preview pane to build, and
 * `check_types` asked Monaco about the models the open editor held. A run whose page had gone —
 * or which worked on another site than the one on screen — got "the preview is closed" from one
 * and an empty, clean-looking program from the other. Each run now owns a bundler worker and a
 * type-checker worker, created on first use and ended with the run.</p>
 *
 * <p>Every answer is bounded in time and `null` means "could not check" — never "clean".</p>
 */
export interface RunChecks {
  /** Bundle the run's files as the preview does. */
  build(): Promise<{ ok: boolean; problems: BuildProblem[] } | null>;
  /** The last build this run made, without starting one. */
  lastBuild(): { ok: boolean; problems: BuildProblem[] } | null;
  /** TypeScript diagnostics for these paths, in the context of every file. */
  types(paths: readonly string[]): Promise<BuildProblem[] | null>;
  dispose(): void;
}

export interface RunChecksOptions {
  siteId: string;
  files: () => Readonly<Record<string, string>>;
  /** A Functions repository type-checks without the DOM (ADR 0029). */
  lib?: string[];
}

const BUILD_TIMEOUT_MS = 60_000;
const TYPES_TIMEOUT_MS = 30_000;

function timeout<T>(work: Promise<T>, ms: number): Promise<T | null> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  return Promise.race([
    work,
    new Promise<null>((resolve) => {
      timer = setTimeout(() => resolve(null), ms);
    }),
  ]).finally(() => clearTimeout(timer));
}

/** One request at a time per worker; each answer is matched to its request by id. */
function ask<Req extends { id: number }, Res extends { id: number }>(worker: Worker, request: Req): Promise<Res> {
  return new Promise((resolve, reject) => {
    const onMessage = (event: MessageEvent<Res>) => {
      if (event.data.id !== request.id) return;
      worker.removeEventListener('message', onMessage);
      worker.removeEventListener('error', onError);
      resolve(event.data);
    };
    const onError = (event: ErrorEvent) => {
      worker.removeEventListener('message', onMessage);
      worker.removeEventListener('error', onError);
      reject(new Error(event.message || 'The worker failed.'));
    };
    worker.addEventListener('message', onMessage);
    worker.addEventListener('error', onError);
    worker.postMessage(request);
  });
}

export function createRunChecks(options: RunChecksOptions): RunChecks {
  let bundler: Worker | null = null;
  let checker: Worker | null = null;
  let seq = 0;
  let last: { ok: boolean; problems: BuildProblem[] } | null = null;

  async function build() {
    try {
      // Imported here, not at the top: these pull the preview's bundler and the palette's
      // generated module, which a test of the host or of a tool has no business loading.
      const [{ default: BundlerWorker }, { previewVersions }, { previewEntry }, { toProblems }] = await Promise.all([
        import('../ide/preview/bundler.worker?worker'),
        import('../ide/preview/usePreview'),
        import('../ide/paths'),
        import('../ide/preview/problems'),
      ]);
      const files = { ...options.files() };
      const entry = previewEntry(files);
      if (!entry) {
        return (last = { ok: false, problems: [{ severity: 'error', text: 'No entry point (expected src/main.tsx).' }] });
      }
      bundler ??= new BundlerWorker();
      const response = await timeout(
        ask<import('../ide/preview/bundler.worker').BuildRequest, import('../ide/preview/bundler.worker').BuildResponse>(bundler, {
          id: ++seq,
          files,
          entry,
          versions: previewVersions(files),
          previewBaseUrl: `/api/admin/sites/${options.siteId}/preview`,
        }),
        BUILD_TIMEOUT_MS,
      );
      // A bundler that could not start says nothing about the code.
      if (!response || response.unavailable) return null;
      const problems = toProblems(response.messages, response.warnings, (path) => files[path] != null);
      return (last = { ok: response.ok, problems });
    } catch {
      return null;
    }
  }

  async function types(paths: readonly string[]): Promise<BuildProblem[] | null> {
    try {
      const [{ default: TypeCheckWorker }, { toTypeProblems }, palette] = await Promise.all([
        import('../ide/diagnostics/typecheck.worker?worker'),
        import('../ide/diagnostics/typeCheck'),
        import('../ide/types/paletteLibs'),
      ]);
      const files = { ...options.files() };
      checker ??= new TypeCheckWorker();
      const response = await timeout(
        ask<TypeCheckRequest, TypeCheckResponse>(checker, {
          id: ++seq,
          files,
          paths: [...paths],
          extraLibs: await palette.paletteLibs(),
          lib: options.lib,
        }),
        TYPES_TIMEOUT_MS,
      );
      if (!response || response.error || !response.diagnostics) return null;
      return Object.entries(response.diagnostics).flatMap(([path, diagnostics]) =>
        toTypeProblems(path, files[path] ?? '', diagnostics),
      );
    } catch {
      return null;
    }
  }

  return {
    build,
    lastBuild: () => last,
    types,
    dispose: () => {
      bundler?.terminate();
      checker?.terminate();
      bundler = null;
      checker = null;
    },
  };
}
