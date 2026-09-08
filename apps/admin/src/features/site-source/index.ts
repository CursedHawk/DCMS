/**
 * Shared site-source editing surface.
 *
 * Both git-backed render modes author their source as the same flat file map and
 * therefore share everything around it: the working-draft store and its granular
 * autosave, the git API and its Source Control / Deployments panels, the Monaco
 * host and diff viewer, the file tree and tab strip. Only what is *inside* the
 * editor differs — a React project in the Mode B IDE, a visual page tree in the
 * Mode A builder.
 *
 * Keeping these here rather than under `features/ide` is what lets the builder
 * inherit branches, diffs, merges and build-on-push for free.
 */

export * from './binary';
export * from './constants';
export * from './fileio';
export * from './git';
export * from './ide';
export * from './openDocs';
export * from './paths';
export * from './search';
export * from './useDiffStats';
export * from './useDraftSession';
export * from './useRelativeTime';
export * from './useSiteLiveUpdates';
export * from './vfs';

export { BinaryFileView } from './BinaryFileView';
export { DeploymentsView } from './DeploymentsView';
export { DiffEditor } from './DiffEditor';
export { EditorTabs } from './EditorTabs';
export { FileTree } from './FileTree';
export { MergeDialog } from './MergeDialog';
export { MonacoEditor } from './MonacoEditor';
export { Resizer, useStoredWidth } from './Resizer';
export { SearchView } from './SearchView';
export { SourceControlView } from './SourceControlView';
export { StatusBar } from './StatusBar';
export { applyEditorTheme, monaco, setupMonaco } from './monaco-setup';
