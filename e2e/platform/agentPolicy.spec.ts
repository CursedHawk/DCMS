import { platformTest as test, expect, data } from '../fixtures/test';

/**
 * A tenant's AI agent policy, from the tenant's page: the site agents' turn limit and the system
 * prompts, shown as shipped and overridable for this tenant. admin-api stores and serves it; this
 * proves what the console sends.
 */
test('an operator raises the turn limit and rewrites one prompt for a tenant', async ({ page, api }) => {
  await page.goto(`/tenants/${data.TENANT.tenantId}`);

  const limit = page.getByLabel('Turn limit per site agent run');
  await expect(limit).toHaveAttribute('placeholder', '100 (default)');
  await limit.fill('150');

  // Shown as shipped, the same text the admin SPA's agent is given.
  await page.getByRole('button', { name: /Site agent — React app/ }).click();
  const prompt = page.getByLabel('Site agent — React app (Mode B) prompt');
  await expect(prompt).toHaveValue(/^You are the coding agent in the DCMS web IDE/);
  await prompt.fill('You build{{site}} for HR teams.\n\n{{rules}}');
  await expect(page.getByText('Customised')).toBeVisible();

  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText('AI agent policy saved')).toBeVisible();
  const put = api.requestsTo('PUT', `/api/platform/tenants/${data.TENANT.tenantId}/ai-agent-policy`).at(-1);
  expect(put?.body).toEqual({
    maxTurns: 150,
    assistantMaxTurns: null,
    compactAtTokens: null,
    prompts: { 'site.react': 'You build{{site}} for HR teams.\n\n{{rules}}' },
  });
});

test('a turn limit out of bounds cannot be saved', async ({ page }) => {
  await page.goto(`/tenants/${data.TENANT.tenantId}`);
  await page.getByLabel('Turn limit per site agent run').fill('501');
  await expect(page.getByRole('button', { name: 'Save', exact: true })).toBeDisabled();
});

test("the site chatbot's prompt and the assistant's limits are set here too", async ({ page, api }) => {
  await page.goto(`/tenants/${data.TENANT.tenantId}`);

  await page.getByLabel('Turn limit per assistant question').fill('25');
  await page.getByLabel('Compact conversations at (tokens)').fill('60000');
  // The chatbot's prompt is built on the server; the console shows the one it sends.
  await page.getByRole('button', { name: /^Site chatbot/ }).click();
  const prompt = page.getByLabel('Site chatbot prompt');
  await expect(prompt).toHaveValue(/^You are \{\{botName\}\}/);
  await prompt.fill('You are {{botName}}, the HR helpdesk. Answer in English.');

  await page.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(page.getByText('AI agent policy saved')).toBeVisible();
  const put = api.requestsTo('PUT', `/api/platform/tenants/${data.TENANT.tenantId}/ai-agent-policy`).at(-1);
  expect(put?.body).toEqual({
    maxTurns: null,
    assistantMaxTurns: 25,
    compactAtTokens: 60000,
    prompts: { chatbot: 'You are {{botName}}, the HR helpdesk. Answer in English.' },
  });
});
