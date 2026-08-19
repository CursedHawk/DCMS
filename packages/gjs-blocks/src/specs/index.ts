import type { DcmsComponentSpec } from '@dcms/gjs-schema';
import { formSpecs } from './forms';
import { interactiveSpecs } from './interactive';
import { layoutSpecs } from './layout';
import { mediaSpecs } from './media';
import { navigationSpecs } from './navigation';
import { partSpecs } from './parts';
import { sectionSpecs } from './sections';
import { typographySpecs } from './typography';
import { utilitySpecs } from './utility';

export {
  formSpecs,
  interactiveSpecs,
  layoutSpecs,
  mediaSpecs,
  navigationSpecs,
  partSpecs,
  sectionSpecs,
  typographySpecs,
  utilitySpecs,
};

/**
 * The built-in catalogue. Tenant-plugin components are generated at runtime and
 * appended to this (see plugins/plugins.ts), so this list is everything a site
 * can use before any plugin is enabled.
 */
export const BUILTIN_SPECS: DcmsComponentSpec[] = [
  ...layoutSpecs,
  ...typographySpecs,
  ...mediaSpecs,
  ...navigationSpecs,
  ...sectionSpecs,
  ...partSpecs,
  ...interactiveSpecs,
  ...formSpecs,
  ...utilitySpecs,
];
