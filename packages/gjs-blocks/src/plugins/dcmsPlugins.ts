import type { DcmsComponentSpec } from '@dcms/gjs-schema';
import type { Editor } from 'grapesjs';
import { registerSpecs } from '../register';

export interface DcmsPluginsOptions {
  /** Specs generated from the tenant's enabled plugin instances (see specs.ts). */
  specs?: readonly DcmsComponentSpec[];
}

/**
 * Registers the tenant's generated plugin components.
 *
 * Separate from `dcmsCore` because these arrive asynchronously — the plugin
 * catalogue and the instance list are two queries — and because they change
 * without a deploy: enabling a plugin adds blocks to the palette on the next
 * load, with no build step and no admin code.
 *
 * The block ids are prefixed so a generated block can never collide with a
 * built-in one, however a tenant names their instances.
 */
export function dcmsPlugins(editor: Editor, options: DcmsPluginsOptions = {}): void {
  const specs = options.specs ?? [];
  if (specs.length === 0) return;
  registerSpecs(editor, specs, { idPrefix: 'dcms-plugin:' });
}
