import { describe, expect, it } from 'vitest';
import { BUILTIN_COMPONENTS } from './components';

/**
 * The builder explains every component and every setting in plain words — the palette, the
 * inspector, the tours and the help all read it from here. A component added without it is a
 * component nobody can learn, so this test is the gate, not a style preference.
 */
describe('component metadata', () => {
  const placeable = BUILTIN_COMPONENTS.filter((d) => d.draggable !== false && d.allowedParents?.length !== 0);

  it('every component a person can place says what it is for, and how people search for it', () => {
    for (const def of placeable) {
      expect(def.description?.length ?? 0, def.type).toBeGreaterThanOrEqual(20);
      expect(def.keywords?.length ?? 0, def.type).toBeGreaterThanOrEqual(2);
    }
  });

  it('every setting has a group and help text', () => {
    for (const def of BUILTIN_COMPONENTS) {
      for (const prop of def.props) {
        expect(prop.group, `${def.type}.${prop.name}`).toBeDefined();
        expect(prop.description?.length ?? 0, `${def.type}.${prop.name}`).toBeGreaterThanOrEqual(10);
      }
    }
  });

  it('every choice is labelled the way a person would say it, not as the stored value', () => {
    for (const def of BUILTIN_COMPONENTS) {
      for (const prop of def.props) {
        if (prop.kind !== 'select') continue;
        for (const option of prop.options) {
          expect(option.label, `${def.type}.${prop.name}=${option.value}`).not.toBe(option.value);
          expect(option.label, `${def.type}.${prop.name}=${option.value}`).toMatch(/^[A-Z0-9“]/);
        }
      }
    }
  });
});
