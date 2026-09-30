import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { describe, expect, it, vi } from 'vitest';
import type { PluginReference } from './api';
import { PluginReferencePage } from './PluginReferencePage';

vi.mock('react-i18next', () => ({
  useTranslation: () => ({ t: (key: string) => key }),
}));
vi.mock('@tanstack/react-router', () => ({
  Link: ({ children }: { children: React.ReactNode }) => <a>{children}</a>,
}));

const reference: PluginReference = {
  id: 'blog',
  name: 'Blog',
  version: '1.0.0',
  description: 'Posts.',
  source: 'builtin',
  sdkMajor: 1,
  packages: [
    { id: 'Dcms.PluginSdk.Abstractions', purpose: 'sdk' },
    { id: 'Dcms.Plugins.Blog.Api', purpose: 'api' },
  ],
  provides: [
    {
      id: 'blog.posts@1',
      description: 'Published posts.',
      clrType: 'Dcms.Plugins.Blog.Api.IBlogPosts',
      assembly: 'Dcms.Plugins.Blog.Api',
      providers: ['blog'],
      operations: [
        {
          name: 'List',
          method: 'ListAsync',
          risk: 'read',
          permission: null,
          exposed: ['plugins', 'site', 'admin', 'ai'],
          returnsExternalText: false,
          description: 'Published posts, newest first.',
          inputType: 'ContentPageRequest',
          outputType: 'Task<ContentPage<BlogPost>>',
          inputSchema: { type: 'object' },
          outputSchema: { type: 'object' },
        },
      ],
      events: [{ name: 'blog.post.published', clrType: 'BlogPostPublished', schema: {} }],
      hooks: [],
    },
  ],
  consumes: [],
  subscribes: [],
  intercepts: [],
  jobs: [],
  contentTypes: [],
  config: {},
  publicConfigKeys: [],
  permissions: [],
  cSharp: 'var blogPosts = context.Contracts.Get<IBlogPosts>();',
};

const run = vi.hoisted(() => vi.fn(() => Promise.resolve({ items: [{ slug: 'hello' }] })));
vi.mock('./api', () => ({
  usePluginReference: () => ({ data: reference, isLoading: false }),
  useAdminContractCatalog: () => ({
    data: [{ id: 'blog.posts@1', instances: [{ id: 'i', slug: 'news', name: 'News', pluginId: 'blog' }], operations: [{ name: 'List' }] }],
  }),
  runContractOperation: run,
}));

function renderPage() {
  return render(
    <QueryClientProvider client={new QueryClient()}>
      <PluginReferencePage pluginId="blog" />
    </QueryClientProvider>,
  );
}

describe('PluginReferencePage', () => {
  it('shows the packages and the C# to start from', () => {
    renderPage();
    expect(screen.getByText('Dcms.Plugins.Blog.Api')).toBeTruthy();
    expect(screen.getByTestId('plugin-reference-csharp').textContent).toContain('Get<IBlogPosts>');
  });

  it('runs an operation the admin plane serves against a chosen instance', async () => {
    renderPage();
    fireEvent.mouseDown(screen.getByRole('tab', { name: /pluginReference.tabs.contracts/ }));
    fireEvent.click(await screen.findByRole('button', { name: 'pluginReference.details' }));
    fireEvent.click(screen.getByRole('button', { name: 'pluginReference.run' }));

    await waitFor(() => expect(run).toHaveBeenCalledWith('blog.posts@1', 'List', 'news', {}));
    expect(await screen.findByText(/"hello"/)).toBeTruthy();
  });
});
