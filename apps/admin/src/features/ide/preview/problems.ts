/**
 * A build message, as a place in the project rather than a sentence about one.
 *
 * <p>esbuild reports errors and warnings with a location — file, line, column, and the source
 * line itself. The bundler used to join every message's `text` with newlines and hand back one
 * string, which the preview pane showed as an overlay: all the information about *where* was
 * thrown away, and the author read a file name off a paragraph and then went and found it
 * themselves.</p>
 */
export interface BuildProblem {
  severity: 'error' | 'warning';
  text: string;
  /** VFS path, when the message came from a project file rather than a dependency. */
  file?: string;
  /** 1-based, ready to hand to the editor. */
  line?: number;
  column?: number;
  /** The offending source line, for context in the list. */
  lineText?: string;
}

/** The shape esbuild's `Message` has, narrowed to what survives a worker `postMessage`. */
export interface EsbuildMessage {
  text: string;
  location?: {
    file?: string;
    line?: number;
    column?: number;
    lineText?: string;
  } | null;
}

/**
 * Turns esbuild's messages into problems the IDE can navigate to.
 *
 * <p><b>Only project files get a location.</b> esbuild resolves a dependency to a URL on the
 * CDN, and a problem that offers to open `https://esm.sh/react@19/es2022/react.mjs` in the
 * editor is offering something that does not exist in this project — the click would create an
 * empty tab. Such a message keeps its text and loses its location, which is honest: the text
 * still names the module.</p>
 *
 * <p>esbuild's columns are 0-based byte offsets; the editor's are 1-based. Off by one here is
 * invisible in a list and wrong every time it is clicked.</p>
 */
export function toProblems(
  errors: readonly EsbuildMessage[] | undefined,
  warnings: readonly EsbuildMessage[] | undefined,
  isProjectFile: (path: string) => boolean,
): BuildProblem[] {
  const map = (messages: readonly EsbuildMessage[] | undefined, severity: 'error' | 'warning') =>
    (messages ?? []).map((m): BuildProblem => {
      const file = m.location?.file;
      if (!file || !isProjectFile(file)) {
        return { severity, text: m.text };
      }
      return {
        severity,
        text: m.text,
        file,
        line: m.location?.line ?? 1,
        column: (m.location?.column ?? 0) + 1,
        lineText: m.location?.lineText,
      };
    });

  // Errors first: a build with both has failed, and the warnings are what to read afterwards.
  return [...map(errors, 'error'), ...map(warnings, 'warning')];
}

/** The single-line summary the preview overlay has always shown. */
export function problemSummary(problems: readonly BuildProblem[]): string | null {
  const errors = problems.filter((p) => p.severity === 'error');
  return errors.length === 0 ? null : errors.map((p) => p.text).join('\n');
}

export function countBySeverity(problems: readonly BuildProblem[]): { errors: number; warnings: number } {
  return {
    errors: problems.filter((p) => p.severity === 'error').length,
    warnings: problems.filter((p) => p.severity === 'warning').length,
  };
}
