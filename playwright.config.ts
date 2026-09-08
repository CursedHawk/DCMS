import { defineConfig, devices } from '@playwright/test';

/**
 * End-to-end tests for the two consoles.
 *
 * <p><b>These run against the real SPAs and a faked server.</b> Both apps are served by their
 * own Vite dev server; every request either app makes is answered inside the browser by
 * `page.route` handlers (see `e2e/fixtures/api.ts`), and the WebSocket the live-update hubs
 * open is answered by a SignalR mock (`e2e/fixtures/hub.ts`).</p>
 *
 * <p>That is a deliberate line, not a shortcut. What these tests are for is the part no other
 * suite covers: routing, lazy chunks, permission gating, responsive layout, drag-and-drop,
 * focus and keyboard order, and whether a pushed resource change actually repaints the page.
 * None of that is exercised by jsdom, and all of it is browser behaviour. What the server does
 * with a request is covered by 337 integration tests that run against real containers — running
 * the whole compose stack here would re-test those slowly, and would make every one of these
 * specs depend on seed data that no longer describes what the test is about.</p>
 *
 * <p>The consequence to keep in mind: a fixture that drifts from the server's real shape makes
 * a test pass that should fail. So the fixtures are written from the response types the app
 * itself declares, and the contract tests that pin those types live on the server side.</p>
 *
 * <p><b>Chromium only.</b> This box has one browser installed; declaring firefox/webkit
 * projects would make `pnpm e2e` fail for everyone rather than skip. The mobile project is
 * Pixel 5, which is Chromium — Mobile Safari would need webkit, so the iOS viewport is not
 * claimed to be covered.</p>
 */

const ADMIN = 'http://127.0.0.1:5173';
const PLATFORM = 'http://127.0.0.1:5174';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  // One worker on CI: this box is 4 cores and two Vite dev servers already hold two of them.
  workers: process.env.CI ? 1 : 2,
  reporter: process.env.CI
    ? [['list'], ['html', { open: 'never' }]]
    : [['list'], ['html', { open: 'never' }]],
  use: {
    trace: 'on-first-retry',
    // Every fixture answers instantly, so a slow action means something is actually wrong.
    actionTimeout: 10_000,
  },

  projects: [
    {
      name: 'admin',
      testDir: './e2e/admin',
      use: { ...devices['Desktop Chrome'], baseURL: ADMIN },
    },
    {
      name: 'platform',
      testDir: './e2e/platform',
      use: { ...devices['Desktop Chrome'], baseURL: PLATFORM },
    },
    {
      // The responsive contract: a drawer instead of a rail, cards instead of tables, and the
      // IDE refusing to render rather than rendering badly.
      name: 'admin-mobile',
      testDir: './e2e/mobile',
      use: { ...devices['Pixel 5'], baseURL: ADMIN },
    },
  ],

  webServer: [
    {
      command: 'pnpm --filter @dcms/admin dev --port 5173 --strictPort',
      url: ADMIN,
      reuseExistingServer: !process.env.CI,
      timeout: 180_000,
      stdout: 'ignore',
    },
    {
      command: 'pnpm --filter @dcms/platform dev --port 5174 --strictPort',
      url: PLATFORM,
      reuseExistingServer: !process.env.CI,
      timeout: 180_000,
      stdout: 'ignore',
    },
  ],
});
