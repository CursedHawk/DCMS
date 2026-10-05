import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// The client bundle only hydrates: every route's HTML is rendered at build time by
// scripts/prerender.mjs from the SSR bundle, so crawlers get the full page without running JS.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  // 5173 is admin, 5174 the platform console.
  server: { port: 5175 },
});
