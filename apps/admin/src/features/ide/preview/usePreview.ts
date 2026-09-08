import { useCallback, useEffect, useRef, useState } from 'react';
import { versions as paletteVersions } from 'virtual:dcms-palette-types';
import { useVfs } from '../../site-source';
import { previewEntry } from '../paths';
import type { BuildRequest, BuildResponse } from './bundler.worker';
import { toProblems, type BuildProblem } from './problems';
import BundlerWorker from './bundler.worker?worker';

// Drives the esbuild-wasm worker: it rebuilds the preview once the user has
// stopped editing for a few seconds (continuous "build on idle"), and also on
// demand via the returned `refresh()`. Hands back an <iframe> srcdoc plus any
// build error.

export interface PreviewState {
  srcdoc: string | null;
  error: string | null;
  building: boolean;
  /**
   * Every message the last build reported, with its location — what the Problems view lists.
   * Errors and warnings both: a build that succeeds can still have something to say, and those
   * warnings used to be discarded in the worker.
   */
  problems: BuildProblem[];
}

// How long the file map must sit unchanged before we auto-rebuild. Kept
// generous so a burst of keystrokes results in a single build once the user
// pauses, rather than one build per change.
const DEBOUNCE_MS = 3000;

// Tailwind v4's in-browser compiler: reads the <style type="text/tailwindcss">
// block, scans the rendered DOM for class names, and injects the utilities —
// the client-side stand-in for the offline build's Tailwind Vite plugin. This
// must be the standalone IIFE bundle (unpkg serves it as a classic script);
// esm.sh would rewrite it to an ES module that a classic <script> can't run.
const TAILWIND_BROWSER_CDN = 'https://unpkg.com/@tailwindcss/browser@4';

function shell(js: string, css: string, usesTailwind: boolean): string {
  const styles = usesTailwind
    ? `<style type="text/tailwindcss">${css}</style>
    <script src="${TAILWIND_BROWSER_CDN}"></script>`
    : `<style>${css}</style>`;
  return `<!doctype html>
<html>
  <head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1" />
    <style>html,body{margin:0}</style>
    ${styles}
  </head>
  <body>
    <div id="root"></div>
    <script type="module">${js}</script>
  </body>
</html>`;
}

export interface PreviewControls extends PreviewState {
  refresh: () => void;
}

export function usePreview(enabled: boolean, siteId: string, refreshKey = 0): PreviewControls {
  const rev = useVfs((s) => s.rev);
  const loaded = useVfs((s) => s.loaded);
  const [state, setState] = useState<PreviewState>({
    srcdoc: null,
    error: null,
    building: false,
    problems: [],
  });
  const workerRef = useRef<Worker | null>(null);
  const reqId = useRef(0);
  const didInitialBuild = useRef(false);

  // Lazily create the worker the first time a preview is requested.
  useEffect(() => {
    if (!enabled) return;
    if (!workerRef.current) workerRef.current = new BundlerWorker();
    const worker = workerRef.current;
    const onMessage = (e: MessageEvent<BuildResponse>) => {
      if (e.data.id !== reqId.current) return; // ignore stale builds
      // A path is a project file exactly when the VFS has it. That is also what keeps a
      // problem in a CDN dependency from offering to open a tab on a file that does not exist.
      const files = useVfs.getState().files;
      const problems = toProblems(e.data.messages, e.data.warnings, (path) => files[path] != null);

      if (e.data.ok) {
        setState({
          srcdoc: shell(e.data.js ?? '', e.data.css ?? '', e.data.usesTailwind ?? false),
          error: null,
          building: false,
          problems,
        });
      } else {
        setState((s) => ({
          ...s,
          error: e.data.error ?? 'Build failed',
          building: false,
          problems,
        }));
      }
    };
    worker.addEventListener('message', onMessage);
    return () => worker.removeEventListener('message', onMessage);
  }, [enabled]);

  // Dispose the worker on unmount.
  useEffect(() => {
    return () => {
      workerRef.current?.terminate();
      workerRef.current = null;
    };
  }, []);

  // Kick off a build right now (used by the initial render and by refresh()).
  const build = useCallback(() => {
    const files = useVfs.getState().snapshot();
    const entry = previewEntry(files);
    if (!entry) {
      setState({
        srcdoc: null,
        error: 'No preview entry (expected src/main.tsx).',
        building: false,
        problems: [{ severity: 'error', text: 'No preview entry (expected src/main.tsx).' }],
      });
      return;
    }
    const id = ++reqId.current;
    setState((s) => ({ ...s, building: true }));
    const req: BuildRequest = {
      id,
      files,
      entry,
      versions: paletteVersions,
      previewBaseUrl: `/api/admin/sites/${siteId}/preview`,
    };
    workerRef.current?.postMessage(req);
  }, [siteId]);

  // Auto-rebuild: show the first preview as soon as the project loads, then
  // rebuild only once edits have settled for DEBOUNCE_MS.
  useEffect(() => {
    if (!enabled || !loaded) return;
    if (!didInitialBuild.current) {
      didInitialBuild.current = true;
      build();
      return;
    }
    const handle = setTimeout(build, DEBOUNCE_MS);
    return () => clearTimeout(handle);
  }, [enabled, loaded, rev, build]);

  // Force an immediate rebuild when the caller bumps refreshKey (e.g. after a
  // sandbox reset). Skips the initial 0 so it doesn't double-build on mount.
  useEffect(() => {
    if (!enabled || !loaded || refreshKey === 0) return;
    build();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [refreshKey]);

  return { ...state, refresh: build };
}
