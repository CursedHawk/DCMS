import { test, expect } from '../fixtures/test';

/**
 * Ctrl+F in the Mode B IDE opens the editor's find, wherever focus happens to be.
 *
 * <p>Monaco only hears keys pressed inside its own DOM, and opening a file from the tree or a
 * tab leaves focus on that button — so the next Ctrl+F went to the browser's find bar instead,
 * which read as "Ctrl+F sometimes stops working".</p>
 */

const SITE = '33333333-3333-3333-3333-333333333333';
const FILES = {
  'src/App.tsx': 'export default function App() {\n  return <main>Hello</main>;\n}\n',
  'src/main.tsx': "import App from './App';\n",
};

test('Ctrl+F after opening a file from the tree opens the editor find', async ({ page, api }) => {
  api
    .on('GET', '/api/admin/sites/:id', { id: SITE, name: 'Acme site', renderMode: 'ReactApp', slug: 'acme-site' })
    .on('GET', '/api/admin/sites/:id/ide', {
      branch: 'main',
      baseSha: 'abc1234',
      version: 1,
      files: FILES,
      hashes: Object.fromEntries(Object.keys(FILES).map((p) => [p, `h-${p}`])),
    })
    .on('GET', '/api/admin/sites/:id/git/changes', [])
    .on('GET', '/api/admin/sites/:id/git/branches', [{ name: 'main', isDefault: true }])
    .on('GET', '/api/admin/sites/:id/git/history', [])
    .on('GET', '/api/admin/sites/:id/builds', [])
    .on('GET', '/api/admin/ai/conversations', []);

  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/sites/${SITE}`);
  await expect(page.getByRole('tab', { name: /Problems/i })).toBeVisible({ timeout: 25000 });
  await page.getByRole('button', { name: /Hide preview/i }).click();

  await page.getByText('App.tsx', { exact: true }).first().click();
  await expect(page.locator('.monaco-editor .view-lines')).toContainText('Hello');

  await page.keyboard.press('ControlOrMeta+f');
  await expect(page.locator('.monaco-editor .find-widget.visible')).toBeVisible();
  await expect(page.locator('.monaco-editor .find-widget textarea, .monaco-editor .find-widget input').first()).toBeFocused();
});
