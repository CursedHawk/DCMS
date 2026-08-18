import { availableSpecs, type DcmsComponentSpec } from '@dcms/gjs-schema';
import type { Editor } from 'grapesjs';
import { registerSpecs } from '../register';
import { BUILTIN_SPECS } from '../specs';

export interface DcmsCoreOptions {
  /** Plugin ids enabled for this tenant; gates the blocks that require one. */
  enabledPluginIds?: Iterable<string>;
  /** Extra specs to register alongside the built-ins. */
  extraSpecs?: readonly DcmsComponentSpec[];
}

/**
 * Registers the built-in component catalogue.
 *
 * Blocks that require a plugin (the Forms blocks, for instance) are filtered out
 * when that plugin is not enabled, rather than offered and failing later — a
 * form that silently posts nowhere is worse than a form that was never offered.
 */
export function dcmsCore(editor: Editor, options: DcmsCoreOptions = {}): void {
  const enabled = new Set(options.enabledPluginIds ?? []);
  const specs = availableSpecs([...BUILTIN_SPECS, ...(options.extraSpecs ?? [])], enabled);
  registerSpecs(editor, specs);
}

/** The specs `dcmsCore` would register for a given set of enabled plugins. */
export function coreSpecsFor(enabledPluginIds: Iterable<string> = []): DcmsComponentSpec[] {
  return availableSpecs(BUILTIN_SPECS, new Set(enabledPluginIds));
}
