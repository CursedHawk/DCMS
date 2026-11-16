import { test, expect } from '../fixtures/test';
import { anthropicStream } from '../fixtures/aiStream';

/**
 * A workspace's several AI providers: managed on Settings → AI, picked when a conversation is
 * started, and charted on Settings → AI usage. ai-gateway resolves and records; this proves what
 * the pages send and draw.
 */
const PROVIDERS = [
  { id: 'p-main', name: 'Anthropic', provider: 'Anthropic', model: 'claude-opus-5-5', baseUrl: null, hasApiKey: true, isDefault: true },
  { id: 'p-local', name: 'Local Llama', provider: 'Ollama', model: 'llama3', baseUrl: 'http://10.0.0.5:11434/v1', hasApiKey: false, isDefault: false },
];
const CHOICES = {
  providers: PROVIDERS.map(({ id, name, provider, model, isDefault }) => ({ id, name, provider, model, isDefault })),
  own: null,
};

test.beforeEach(async ({ api }) => {
  api
    .on('GET', '/api/admin/ai/providers', PROVIDERS)
    .on('GET', '/api/admin/ai/provider-choices', CHOICES)
    .on('POST', '/api/admin/ai/providers', { id: 'p-new' })
    .on('PUT', '/api/admin/ai/providers/:id', {})
    .on('DELETE', '/api/admin/ai/providers/:id', {});
});

test('a workspace keeps several providers and switches its default', async ({ page, api }) => {
  await page.goto('/settings/ai');
  await expect(page.getByText('Workspace AI providers')).toBeVisible();
  await expect(page.getByText('Local Llama')).toBeVisible();

  await page.getByRole('button', { name: 'Make default' }).click();
  await expect.poll(() => api.requestsTo('PUT', '/api/admin/ai/providers/p-local').length).toBe(1);
  expect(api.requestsTo('PUT', '/api/admin/ai/providers/p-local')[0].body).toMatchObject({ name: 'Local Llama', isDefault: true });

  await page.getByRole('button', { name: 'Add provider' }).first().click();
  const dialog = page.getByRole('dialog');
  await dialog.getByLabel('Name').fill('OpenRouter');
  await dialog.getByLabel('Base URL').fill('https://openrouter.ai/api/v1');
  await dialog.getByLabel('API key').fill('sk-or');
  await dialog.getByRole('button', { name: 'Save' }).click();
  await expect.poll(() => api.requestsTo('POST', '/api/admin/ai/providers').length).toBe(1);
  expect(api.requestsTo('POST', '/api/admin/ai/providers')[0].body).toMatchObject({
    name: 'OpenRouter',
    provider: 'Anthropic',
    baseUrl: 'https://openrouter.ai/api/v1',
    apiKey: 'sk-or',
  });
});

test('the usage page charts every provider, compares them, and shows your own key', async ({ page, api }) => {
  const row = (day: string, providerId: string | null, providerName: string, source: string, input: number) => ({
    day, source, providerId, providerName, providerKind: 'anthropic', model: 'claude-opus-5-5',
    calls: 3, inputTokens: input, outputTokens: 100, cacheReadTokens: 2000, cacheWriteTokens: 50,
  });
  api.on('GET', '/api/admin/ai/usage', {
    from: '2026-11-10',
    to: '2026-11-16',
    workspace: [row('2026-11-15', 'p-main', 'Anthropic', 'workspace', 1000), row('2026-11-16', 'p-local', 'Local Llama', 'workspace', 400)],
    mine: [row('2026-11-16', null, 'Own key', 'user', 10)],
    own: { provider: 'Anthropic', model: 'claude-sonnet-5-5', hasApiKey: true },
  });

  await page.goto('/settings/ai-usage');
  await expect(page.getByRole('heading', { name: 'AI usage', level: 1 })).toBeVisible();
  await expect(page.getByText('You use your own AI settings')).toBeVisible();
  await expect(page.getByText('All providers, tokens per day')).toBeVisible();

  // The comparison's table: one row per provider, your own key included.
  const table = page.getByRole('table');
  await expect(table.getByRole('row')).toHaveCount(4);
  await expect(table.getByRole('row', { name: /Anthropic/ })).toContainText('3,150');
  await expect(table.getByRole('row', { name: /Your own key/ })).toBeVisible();

  // One chart per provider.
  await expect(page.getByRole('heading', { name: 'Local Llama', level: 3 })).toBeVisible();

  await page.getByLabel('Range').selectOption('90');
  await expect.poll(() => api.requestsTo('GET', '/api/admin/ai/usage').some((r) => r.query.get('days') === '90')).toBe(true);
});

test('a conversation runs on the provider picked when it was started', async ({ page, api }) => {
  const sent: string[] = [];
  api
    .on('GET', '/api/admin/ai/conversations', [])
    .on('POST', '/api/admin/ai/conversations', { id: 'conv-1', title: 'Hi' })
    .on('POST', '/api/admin/ai/conversations/:id/messages', { messageCount: 2, lastSeq: 2 })
    .on('GET', '/api/admin/ai/conversations/:id', {
      id: 'conv-1', title: 'Hi', visibility: 'Private', mode: 'agent', surface: 'console', mine: true,
      providerId: 'p-local', messageCount: 0, messages: [], runs: [],
    });
  await page.route('**/api/admin/ai/messages**', async (route) => {
    sent.push(route.request().url());
    await route.fulfill({ status: 200, contentType: 'text/event-stream', body: anthropicStream({ text: 'Hello from the local model.' }) });
  });

  await page.goto('/assistant');
  const picker = page.getByLabel('AI provider');
  await expect(picker).toHaveValue('');
  await picker.selectOption({ label: 'Local Llama' });
  await page.getByPlaceholder('Ask about this workspace, or say what to change').fill('Hi');
  await page.keyboard.press('Enter');
  await expect(page.getByText('Hello from the local model.')).toBeVisible();

  expect(api.requestsTo('POST', '/api/admin/ai/conversations')[0].body).toMatchObject({ providerId: 'p-local' });
  expect(sent.every((url) => url.includes('provider=p-local'))).toBe(true);
  await expect(picker).toBeDisabled();
});
