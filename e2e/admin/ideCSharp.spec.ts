import { existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { test, expect } from '../fixtures/test';

/**
 * C# in the IDE (ADR 0030): a Functions repository's .cs files get Roslyn's completion,
 * diagnostics and go to definition into the generated SDK, from the language service running in
 * the browser. Nothing here is mocked but the server: the worker is the real one, published into
 * the dev server's public/ by `pnpm --filter @dcms/admin csharp:worker`.
 */

const WORKER = fileURLToPath(new URL('../../apps/admin/public/csharp/_framework/dotnet.js', import.meta.url));
const REPO = '44444444-4444-4444-4444-444444444444';

const FILES = {
  'functions/Hello.cs': [
    'namespace Demo;',
    '',
    '[Function("hello")]',
    'public sealed class Hello : Function<JsonNode, object>',
    '{',
    '    public override async Task<object> Run(JsonNode input, Context ctx)',
    '    {',
    '        var page = await ctx.Db.Crm.Companies.Find();',
    '        return missingName;',
    '    }',
    '}',
    '',
  ].join('\n'),
};

const SDK = [
  '#nullable enable',
  'namespace Dcms.Functions;',
  'public static class WorkspaceSdk { extension(Context ctx) { public Database Db => new(ctx); } }',
  'public sealed class Database(Context ctx) { public CrmApp Crm => new(ctx); }',
  'public sealed class CrmApp(Context ctx)',
  '{',
  '    /// <summary>Companies.</summary>',
  '    public Table<CrmCompaniesRow> Companies => ctx.Table<CrmCompaniesRow>("crm", "companies");',
  '}',
  'public sealed record CrmCompaniesRow : Row { public string? Name { get; init; } }',
].join('\n');

test.skip(!existsSync(WORKER), 'Publish the C# language service first: pnpm --filter @dcms/admin csharp:worker');

test('a C# function gets completion, diagnostics and go to definition into the SDK', async ({ page, api }) => {
  test.setTimeout(120_000);
  api
    .on('GET', '/api/admin/repos/:id/ide', {
      branch: 'main',
      baseSha: 'abc1234',
      version: 1,
      files: FILES,
      hashes: Object.fromEntries(Object.keys(FILES).map((p) => [p, `h-${p}`])),
    })
    .on('PATCH', '/api/admin/repos/:id/ide/files', { version: 2, hashes: {} })
    .on('GET', '/api/admin/repos/:id/git/changes', [])
    .on('GET', '/api/admin/repos/:id/git/branches', [{ name: 'main', isDefault: true }])
    .on('GET', '/api/admin/repos/:id/git/history', [])
    .on('GET', '/api/admin/plugins/:slug/_functions/sdk', { typings: 'export {};', csharp: SDK, etag: '"e"' })
    .on('GET', '/api/admin/ai/conversations', []);

  await page.setViewportSize({ width: 1600, height: 950 });
  await page.goto(`/repos/${REPO}/code?name=Automations&slug=automations&kind=functions`);
  await expect(page.getByRole('tab', { name: /Problems/i })).toBeVisible({ timeout: 25_000 });
  await page.getByText('Hello.cs', { exact: true }).first().click();
  const editor = page.locator('.monaco-editor .view-lines');
  await expect(editor).toContainText('missingName');

  // Diagnostics: Roslyn's, in the Problems panel. The first answer waits for .NET to boot.
  await page.getByRole('tab', { name: /Problems/i }).click();
  await expect(page.getByText(/CS0103: The name 'missingName' does not exist/)).toBeVisible({ timeout: 60_000 });

  // Completion on the generated SDK, through the C# 14 extension property ctx.Db.
  await page.locator('.monaco-editor').getByText('return missingName;').click();
  await page.keyboard.press('End');
  await page.keyboard.press('Enter');
  await page.keyboard.type('ctx.Db.Crm.');
  await expect(page.locator('.monaco-editor .suggest-widget')).toContainText('Companies', { timeout: 30_000 });
  await page.keyboard.press('Escape');

  // Go to definition lands in the read-only Sdk.g.cs.
  await page.locator('.monaco-editor').getByText('Companies.Find').first().click();
  await page.keyboard.press('F12');
  await expect(editor).toContainText('public Table<CrmCompaniesRow> Companies', { timeout: 30_000 });
});
