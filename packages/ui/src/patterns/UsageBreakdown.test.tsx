import { describe, expect, it } from 'vitest';
import { render as rtlRender, screen } from '@testing-library/react';
import i18next from 'i18next';
import { I18nextProvider, initReactI18next } from 'react-i18next';
import { user } from '../test/user';
import { UsageBreakdown, type UsageCategory } from './UsageBreakdown';

// A real instance with no resources: what the platform console is, reading the English defaults.
const i18n = i18next.createInstance();
await i18n.use(initReactI18next).init({ lng: 'en', resources: {}, interpolation: { escapeValue: false } });
const render = (ui: React.ReactElement) => rtlRender(<I18nextProvider i18n={i18n}>{ui}</I18nextProvider>);

const categories: UsageCategory[] = [
  {
    key: 'dynamicApps',
    bytes: 3 * 1024 * 1024,
    items: [{ key: 'records', label: 'Records', kind: 'database', bytes: 3 * 1024 * 1024, rows: 1200 }],
  },
  {
    key: 'media',
    bytes: 1024 * 1024,
    items: [{ key: 'originals', label: 'Original files', kind: 'files', bytes: 1024 * 1024 }],
  },
];

describe('UsageBreakdown', () => {
  it('lists each category with its share of the whole and its size', () => {
    render(<UsageBreakdown categories={categories} totalBytes={4 * 1024 * 1024} />);
    const row = screen.getByRole('button', { name: /Dynamic Apps/ });
    expect(row).toHaveTextContent('75%');
    expect(row).toHaveTextContent('3.0 MB');
  });

  it('opens a category to its items, one at a time', async () => {
    render(<UsageBreakdown categories={categories} totalBytes={4 * 1024 * 1024} />);
    expect(screen.queryByText('Records')).not.toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Dynamic Apps/ }));
    expect(screen.getByRole('button', { name: /Dynamic Apps/ })).toHaveAttribute('aria-expanded', 'true');
    expect(screen.getByText('Records')).toBeInTheDocument();
    expect(screen.getByText('1,200 rows')).toBeInTheDocument();
    expect(screen.getByLabelText('Database')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: /Media/ }));
    expect(screen.queryByText('Records')).not.toBeInTheDocument();
    expect(screen.getByText('Original files')).toBeInTheDocument();
  });

  it('names a site part with the site name the snapshot carries', async () => {
    render(
      <UsageBreakdown
        totalBytes={10}
        categories={[{ key: 'sites', bytes: 10, items: [{ key: 'site:abc:history', label: 'Shop', kind: 'files', bytes: 10 }] }]}
      />,
    );
    await user.click(screen.getByRole('button', { name: /Sites/ }));
    expect(screen.getByText('Shop (earlier builds)')).toBeInTheDocument();
  });
});
