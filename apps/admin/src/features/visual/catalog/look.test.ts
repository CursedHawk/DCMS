import { BUILTIN_COMPONENTS } from '@dcms/site-runtime';
import { THUMBNAIL_ARCHETYPES } from '@dcms/gjs-blocks';
import { describe, expect, it } from 'vitest';
import { COMPONENT_LOOKS } from './look';

describe('component looks', () => {
  it('every built-in an author can place has an icon and a wireframe that exists', () => {
    for (const def of BUILTIN_COMPONENTS) {
      if (def.draggable === false || def.allowedParents?.length === 0) continue;
      const look = COMPONENT_LOOKS[def.type];
      expect(look, def.type).toBeDefined();
      expect(THUMBNAIL_ARCHETYPES, def.type).toContain(look!.archetype);
    }
  });
});
