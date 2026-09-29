import { render, screen } from '@testing-library/react';
import type { WidgetProps } from '@rjsf/utils';
import { describe, expect, it, vi } from 'vitest';
import { ContractBindingWidget } from './ContractBindingWidget';

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string, o?: Record<string, string>) => (o ? `${key}:${Object.values(o).join(',')}` : key) }),
}));

const catalog = vi.hoisted(() => ({
  data: [
    { id: 'roster', provides: ['roster.members@1'] },
    { id: 'blog', provides: [] },
  ],
  isLoading: false,
}));
const instances = vi.hoisted(() => ({
  data: [
    { id: '1', pluginId: 'roster', slug: 'crew', name: 'Crew', enabled: true },
    { id: '2', pluginId: 'roster', slug: 'old', name: 'Old', enabled: false },
    { id: '3', pluginId: 'blog', slug: 'news', name: 'News', enabled: true },
  ],
  isLoading: false,
}));
vi.mock('./api', () => ({ usePluginCatalog: () => catalog, usePluginInstances: () => instances }));

function props(value: unknown): WidgetProps {
  return { id: 'f', value, onChange: vi.fn(), options: { contract: 'roster.members@1' } } as unknown as WidgetProps;
}

describe('ContractBindingWidget', () => {
  it('lists only instances of plugins that provide the contract', () => {
    render(<ContractBindingWidget {...props('crew')} />);
    const labels = screen.getAllByRole('option').map((o) => o.textContent);
    expect(labels).toEqual(['plugins.binding.unset', 'Crew (crew)', 'Old (old) — plugins.binding.disabled']);
    expect((screen.getByRole('combobox') as HTMLSelectElement).value).toBe('crew');
  });

  it('keeps a value naming an instance that is gone instead of rewriting it', () => {
    render(<ContractBindingWidget {...props('deleted')} />);
    expect(screen.getByRole('option', { name: 'plugins.binding.missing:deleted' })).toBeTruthy();
    expect((screen.getByRole('combobox') as HTMLSelectElement).value).toBe('deleted');
  });
});
