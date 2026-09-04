import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    // 5173 belongs to the admin SPA; both run at once during development.
    port: 5174,
    proxy: {
      // Same-origin in every deployed environment (the edge routes all three by prefix), so the
      // dev server has to reproduce that or the SPA would need CORS it never needs in production.
      '/api/platform': { target: 'http://localhost:5008', changeOrigin: true },
      '/api/identity': { target: 'http://localhost:5001', changeOrigin: true },
      '/api': { target: 'http://localhost:5002', changeOrigin: true },
    },
  },
  // No manualChunks. The admin SPA needs them because Monaco, GrapesJS and Scalar are each
  // megabytes on one route; this console loads none of those, and hand-tuning a chunk graph
  // that Rollup already gets right would be maintenance for nothing.
});
