import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    // 5173 belongs to the admin SPA; both run at once during development.
    port: 5174,
    proxy: {
      // Same-origin in every deployed environment (the edge routes both by prefix), so the dev
      // server has to reproduce that or the SPA would need CORS it never needs in production.
      //
      // Two entries, not three: the edge serves no general /api on the console host any more, so
      // a stray call to admin-api must fail HERE too. A dev proxy that is more permissive than
      // the edge is worse than no proxy — the call works all the way to review and then returns
      // the SPA's index.html in production, which the http client hands to the query as a string.
      '/api/platform': { target: 'http://localhost:5008', changeOrigin: true },
      '/api/identity': { target: 'http://localhost:5001', changeOrigin: true },
    },
  },
  // No manualChunks. The admin SPA needs them because Monaco, GrapesJS and Scalar are each
  // megabytes on one route; this console loads none of those, and hand-tuning a chunk graph
  // that Rollup already gets right would be maintenance for nothing.
});
