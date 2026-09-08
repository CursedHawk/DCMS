import AxeBuilder from '@axe-core/playwright';
import { test, expect } from '../fixtures/test';

/**
 * Accessibility, checked by axe on the pages people actually spend time on.
 *
 * <p>axe finds a specific class of defect — contrast, names, roles, landmark structure — and
 * says nothing about whether a screen is usable. It is worth running anyway because the things
 * it does catch are the ones that are invisible to somebody who is not affected by them, and
 * because a console whose primary controls have no accessible name is one nobody can drive from
 * a keyboard either.</p>
 *
 * <p>Scoped to serious and critical. The moderate rules include judgement calls (heading order
 * in a card grid, for one) that would make this a list of things to argue about rather than a
 * gate — and a gate everyone learns to ignore is worse than no gate.</p>
 */
const SERIOUS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'];

async function violations(page: import('@playwright/test').Page) {
  const result = await new AxeBuilder({ page }).withTags(SERIOUS).analyze();
  return result.violations
    .filter((v) => v.impact === 'serious' || v.impact === 'critical')
    .map((v) => `${v.id}: ${v.help} (${v.nodes.length})`);
}

const PAGES: [string, string][] = [
  ['the dashboard', '/'],
  ['the content list', '/content'],
  ['the media library', '/media'],
  ['the marketplace', '/marketplace'],
  ['settings', '/settings/general'],
];

for (const [name, path] of PAGES) {
  test(`${name} has no serious axe violations`, async ({ page }) => {
    await page.goto(path);
    // Wait for the shell rather than a timer: axe on a skeleton finds the skeleton's problems.
    await expect(page.getByRole('navigation', { name: 'Sections' })).toBeVisible();

    expect(await violations(page)).toEqual([]);
  });
}

test('the sign-in screen has no serious axe violations', async ({ page }) => {
  await page.context().addInitScript(() => window.localStorage.clear());
  await page.goto('/');
  await expect(page.getByRole('button', { name: 'Sign in' })).toBeVisible();

  expect(await violations(page)).toEqual([]);
});
