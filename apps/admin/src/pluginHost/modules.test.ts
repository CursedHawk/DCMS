import { readdirSync, readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

const plugins = join(__dirname, '../../../../src/Plugins');

/*
 * Every built-in plugin's app has a Settings page, and the plugin provides it (#337): the server
 * checks each manifest declares the screen (MarketplaceMetadataTests); this checks each module
 * the console compiles in supplies it, or the page would say the screen is unavailable.
 */
describe('built-in plugin admin modules', () => {
  const withManifest = readdirSync(plugins).filter(
    (dir) => !dir.endsWith('.Api') && readdirSync(join(plugins, dir)).some((f) => /Plugin\.cs$/.test(f)),
  );

  it.each(withManifest)('%s provides its settings screen', (dir) => {
    const entry = join(plugins, dir, 'admin/src/index.tsx');
    expect(existsSync(entry), `${dir} has no admin/src/index.tsx`).toBe(true);
    expect(readFileSync(entry, 'utf8')).toMatch(/\bsettings\s*:/);
  });
});
