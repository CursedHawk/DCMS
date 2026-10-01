import { describe, expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import {
  PluginHostProvider,
  PluginScreenProvider,
  instancePath,
  resolvePermission,
  useCan,
  type PluginHost,
} from './index';
import { dcmsSharedModules } from './vite';
import { SHARED_GLOBAL, SHARED_MODULES } from './shared';

describe('permissions', () => {
  it('reads a bare action as the plugin’s own key', () => {
    expect(resolvePermission('guestbook', 'moderate')).toBe('plugin:guestbook:moderate');
    expect(resolvePermission('guestbook', 'content:read')).toBe('content:read');
  });

  it('asks the host with the resolved key', () => {
    const asked: string[] = [];
    const host = { can: (p: string) => (asked.push(p), p === 'plugin:guestbook:moderate') } as unknown as PluginHost;
    function Probe() {
      return <span>{useCan('moderate') ? 'yes' : 'no'}</span>;
    }
    render(
      <PluginHostProvider host={host}>
        <PluginScreenProvider screen={{ pluginId: 'guestbook', screenId: 's', instance: null, instances: [] }}>
          <Probe />
        </PluginScreenProvider>
      </PluginHostProvider>,
    );
    expect(screen.getByText('yes')).toBeTruthy();
    expect(asked).toEqual(['plugin:guestbook:moderate']);
  });
});

it('builds instance admin paths', () => {
  expect(instancePath('press room', '/stats')).toBe('/admin/plugins/press%20room/stats');
});

describe('vite preset', () => {
  const plugin = dcmsSharedModules() as unknown as {
    resolveId: (id: string) => string | null;
    load: (id: string) => { code: string; syntheticNamedExports: string } | null;
  };

  it('points every shared module at the console’s copy and leaves the rest alone', () => {
    for (const id of SHARED_MODULES) expect(plugin.resolveId(id)).toBe(`\0dcms-shared:${id}`);
    expect(plugin.resolveId('recharts')).toBeNull();
  });

  it('reads named imports from the published namespace', () => {
    const out = plugin.load('\0dcms-shared:@dcms/ui')!;
    expect(out.syntheticNamedExports).toBe('__ns');
    expect(out.code).toContain(`globalThis[${JSON.stringify(SHARED_GLOBAL)}]`);
    expect(out.code).toContain('"@dcms/ui"');
  });
});
