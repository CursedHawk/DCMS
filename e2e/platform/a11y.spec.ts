import AxeBuilder from '@axe-core/playwright';
import { platformTest as test, expect } from '../fixtures/test';

/**
 * The same axe pass on the operations console.
 *
 * <p>It matters more here than in the admin, not less: this is the console somebody opens at
 * three in the morning on whatever screen is nearest, and it is the one that renders while the
 * platform it describes is broken.</p>
 */
const SERIOUS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'];

const PAGES: [string, string][] = [
  ['the overview', '/'],
  ['the tenant list', '/tenants'],
  ['the audit log', '/audit'],
];

for (const [name, path] of PAGES) {
  test(`${name} has no serious axe violations`, async ({ page }) => {
    await page.goto(path);
    await expect(page.getByRole('navigation', { name: 'Platform sections' })).toBeVisible();

    const result = await new AxeBuilder({ page }).withTags(SERIOUS).analyze();
    const serious = result.violations
      .filter((v) => v.impact === 'serious' || v.impact === 'critical')
      .map((v) => `${v.id}: ${v.help} (${v.nodes.length})`);

    expect(serious).toEqual([]);
  });
}
