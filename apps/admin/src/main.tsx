import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { RouterProvider, createRouter } from '@tanstack/react-router';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { ThemeProvider, Toaster, TooltipProvider } from '@dcms/admin-ui';
import './lib/i18n';
import { routeTree } from './routes';
import { adoptTenantFromUrl } from './tenants';
import './index.css';

// Before the first render, and before any query runs: a ?tenant= link from the platform
// console has to be in effect by the time the first request builds its X-Dcms-Tenant header,
// or the page loads the previously selected workspace and looks perfectly fine doing it.
adoptTenantFromUrl();

const queryClient = new QueryClient({
  defaultOptions: { queries: { retry: 1, refetchOnWindowFocus: false } },
});
const router = createRouter({ routeTree });

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router;
  }
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <TooltipProvider delayDuration={200}>
          <RouterProvider router={router} />
          <Toaster />
        </TooltipProvider>
      </QueryClientProvider>
    </ThemeProvider>
  </StrictMode>,
);
