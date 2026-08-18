import type { LintComponent, LintContext, LintInstance, LintMarker } from '@dcms/gjs-parse';
import { pageHtmlPath, type DcmsComponentSpec } from '@dcms/gjs-schema';
import { useEffect, useMemo, useRef } from 'react';
import { monaco, setupMonaco, uriOf, useVfs } from '../../site-source';
import { usePluginCatalog, usePluginInstances } from '../../plugins/api';
import { stylesheets } from '../project';
import { useBuilder } from '../store';
import { parseClient } from '../workers/parseClient';
import {
  clearProjectIndex,
  registerBuilderIntellisense,
  setComponentClasses,
  setProjectIndex,
} from './intellisense';

/**
 * Keeps the code view's project-aware language features in step with the files.
 *
 * Two things are maintained here, both computed in the parse worker: the symbol
 * index behind class/variable completion and go-to-definition, and the cross-file
 * diagnostics that Monaco's own workers cannot produce because they only ever see
 * one file at a time — an unknown component, a binding naming a plugin instance
 * this tenant does not have, a class nothing defines.
 */

/** Markers we own. Scoped so Monaco's html/css diagnostics are left alone. */
const MARKER_OWNER = 'dcms-builder';

/** Long enough that a burst of typing produces one lint, short enough to feel live. */
const DEBOUNCE_MS = 400;

/**
 * Utility-framework prefixes that are never defined in the project's own CSS.
 * Without this, pasting in markup from a Tailwind-styled design would bury the
 * real diagnostics under hundreds of "undefined class" warnings.
 */
const IGNORED_CLASS_PREFIXES = ['tw-', 'sm:', 'md:', 'lg:', 'xl:', 'dark:', 'hover:', 'focus:'];

export function useIntellisense(specs: readonly DcmsComponentSpec[]): void {
  const project = useBuilder((s) => s.project);
  const rev = useVfs((s) => s.rev);
  const catalog = usePluginCatalog();
  const instances = usePluginInstances();

  // Register the providers once, for as long as the code view is mounted.
  useEffect(() => {
    setupMonaco();
    const registration = registerBuilderIntellisense(monaco);
    return () => {
      registration.dispose();
      clearProjectIndex();
      // Markers outlive their provider otherwise, leaving squiggles on files the
      // next site happens to open at the same paths.
      for (const model of monaco.editor.getModels()) {
        monaco.editor.setModelMarkers(model, MARKER_OWNER, []);
      }
    };
  }, []);

  useEffect(() => {
    setComponentClasses(specs);
  }, [specs]);

  /** Which `data-dcms-component` values exist, and which of them read content. */
  const components = useMemo<LintComponent[]>(
    () =>
      specs
        .filter((s) => s.category === 'plugin')
        .map((s) => ({ type: s.type, bindable: s.binding !== undefined })),
    [specs],
  );

  /** Enabled plugin instances and the content types their manifest offers. */
  const lintInstances = useMemo<LintInstance[]>(() => {
    const manifests = new Map((catalog.data ?? []).map((m) => [m.id, m]));
    return (instances.data ?? [])
      .filter((i) => i.enabled)
      .map((i) => ({
        slug: i.slug,
        contentTypes: (manifests.get(i.pluginId)?.contentTypes ?? []).map((c) => c.name),
      }));
  }, [catalog.data, instances.data]);

  // Re-index and re-lint on a pause in editing. The dependency is the whole
  // project object, so this covers a canvas edit, a code edit, a branch switch
  // and a git restore through the one path.
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => {
    if (!project) return;
    if (timer.current) clearTimeout(timer.current);

    timer.current = setTimeout(() => {
      void refresh(project, { components, instances: lintInstances });
    }, DEBOUNCE_MS);

    return () => {
      if (timer.current) clearTimeout(timer.current);
    };
    // `rev` is what actually changes on every keystroke; `project` is re-read
    // from it, so both are listed to make the intent explicit.
  }, [project, rev, components, lintInstances]);
}

async function refresh(
  project: NonNullable<ReturnType<typeof useBuilder.getState>['project']>,
  context: { components: LintComponent[]; instances: LintInstance[] },
): Promise<void> {
  const pages = Object.fromEntries(
    project.pages.map((page) => [pageHtmlPath(page.entry.slug), page.html]),
  );
  const index = await parseClient.index(stylesheets(project), pages);
  setProjectIndex(index);

  const lintContext: LintContext = {
    components: context.components,
    instances: context.instances,
    knownClasses: index.classNames,
    ignoreClassPrefixes: IGNORED_CLASS_PREFIXES,
  };

  // Every page, not just the open one: a diagnostic that only appears once you
  // happen to open the file is a diagnostic nobody acts on. There are a handful
  // of pages, and the work is in the worker.
  await Promise.all(
    Object.entries(pages).map(async ([path, html]) => {
      const diagnostics = await parseClient.lintLatest(path, html, lintContext);
      if (diagnostics) publish(path, diagnostics);
    }),
  );
}

function publish(path: string, diagnostics: LintMarker[]): void {
  const model = monaco.editor.getModel(monaco.Uri.parse(uriOf(path)));
  if (!model) return;
  monaco.editor.setModelMarkers(
    model,
    MARKER_OWNER,
    diagnostics.map((d) => ({
      severity: severityOf(d.severity),
      message: d.message,
      // The rule id shows in the Problems hover, so a warning can be looked up
      // (and, later, suppressed) by name rather than by its prose.
      source: `dcms(${d.rule})`,
      startLineNumber: d.startLine,
      startColumn: d.startColumn,
      endLineNumber: d.endLine,
      endColumn: d.endColumn,
    })),
  );
}

function severityOf(severity: LintMarker['severity']): number {
  switch (severity) {
    case 'error':
      return monaco.MarkerSeverity.Error;
    case 'warning':
      return monaco.MarkerSeverity.Warning;
    default:
      return monaco.MarkerSeverity.Info;
  }
}
