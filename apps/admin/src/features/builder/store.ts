import { GLOBAL_CSS, THEME_CSS, homePage, pageCssPath, pageHtmlPath, type SiteManifest } from '@dcms/gjs-schema';
import { create } from 'zustand';
import { useVfs } from '../site-source';
import { projectFiles, readProject, type Project } from './project';

/**
 * Builder state that is *not* file content: which page is open, which view is
 * showing, and whether the project could be read at all.
 *
 * The files themselves stay in the shared working-draft store (`useVfs`) so the
 * builder, the code view and the Source Control panel all see one set of
 * changes, and the Mode B autosave carries them to the server unchanged.
 */

export type ViewMode = 'design' | 'split' | 'code';

interface BuilderState {
  project: Project | null;
  /** Why the file map is not a readable project, if it is not. */
  error: string | null;
  activeSlug: string | null;
  view: ViewMode;
  /** Bumped whenever the active page's content changes from outside the canvas. */
  reloadToken: number;
  /**
   * The exact file contents the canvas last wrote.
   *
   * This is how a code-view edit is told apart from the canvas's own autosave:
   * both land in the same working draft, so without a record of what the canvas
   * itself produced, every capture would look like an external change and
   * reload the canvas in a loop.
   */
  capturedFiles: Record<string, string>;

  /** Re-read the project from the working-draft file map. */
  syncFromVfs: () => void;
  /** Record what the canvas just wrote, so it is not mistaken for an edit. */
  noteCapture: (files: Record<string, string>) => void;
  setActiveSlug: (slug: string) => void;
  setView: (view: ViewMode) => void;
  /** Apply a change to the project and write the affected files back. */
  update: (mutate: (project: Project) => Project) => void;
  updateManifest: (mutate: (manifest: SiteManifest) => SiteManifest) => void;
  /** Force the canvas to reload the active page (after an out-of-band edit). */
  requestReload: () => void;

  activePage: () => Project['pages'][number] | null;
}

export const useBuilder = create<BuilderState>((set, get) => ({
  project: null,
  error: null,
  activeSlug: null,
  view: 'design',
  reloadToken: 0,
  capturedFiles: {},

  syncFromVfs: () => {
    const files = useVfs.getState().files;
    const { project, error } = readProject(files);
    const current = get().activeSlug;
    const stillThere = project?.pages.some((p) => p.entry.slug === current) ?? false;
    const activeSlug = project ? (stillThere ? current : homePage(project.manifest).slug) : null;

    // If the active page's source no longer matches what the canvas last wrote,
    // something else changed it — the code view, a git restore, an AI insert —
    // and the canvas has to re-read it.
    const captured = get().capturedFiles;
    const drifted =
      activeSlug !== null &&
      [pageHtmlPath(activeSlug), pageCssPath(activeSlug), GLOBAL_CSS].some(
        (path) => captured[path] !== undefined && files[path] !== captured[path],
      );

    set((s) => ({
      project,
      error,
      activeSlug,
      reloadToken: drifted ? s.reloadToken + 1 : s.reloadToken,
    }));
  },

  noteCapture: (files) =>
    set((s) => ({ capturedFiles: { ...s.capturedFiles, ...files } })),

  setActiveSlug: (slug) => set({ activeSlug: slug }),
  setView: (view) => set({ view }),
  requestReload: () => set((s) => ({ reloadToken: s.reloadToken + 1 })),

  update: (mutate) => {
    const project = get().project;
    if (!project) return;
    const next = mutate(project);
    set({ project: next });
    writeProject(project, next);
  },

  updateManifest: (mutate) =>
    get().update((project) => ({ ...project, manifest: mutate(project.manifest) })),

  activePage: () => {
    const { project, activeSlug } = get();
    return project?.pages.find((p) => p.entry.slug === activeSlug) ?? null;
  },
}));

/**
 * Write only what actually changed into the working draft.
 *
 * Writing every file on every keystroke would mark the whole project dirty and
 * make the Source Control panel claim edits the author never made, so each file
 * is compared before being written and files that disappeared are deleted.
 */
function writeProject(before: Project, after: Project): void {
  const previous = projectFiles(before);
  const next = projectFiles(after);
  const vfs = useVfs.getState();

  for (const [path, content] of Object.entries(next)) {
    if (previous[path] !== content) vfs.writeFile(path, content);
  }
  for (const path of Object.keys(previous)) {
    if (!(path in next)) vfs.deleteFile(path);
  }
}

/**
 * Push a whole project into the draft, including files that are byte-identical.
 * Used once after seeding a starter, where nothing exists server-side yet.
 */
export function writeWholeProject(project: Project): void {
  const vfs = useVfs.getState();
  for (const [path, content] of Object.entries(projectFiles(project))) {
    vfs.writeFile(path, content);
  }
}

/** The generated theme stylesheet is derived from site.json; never hand-edited. */
export function isGeneratedBuilderFile(path: string): boolean {
  return path === THEME_CSS;
}
