import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';
import { paletteTypesPlugin } from './vite-plugin-palette-types';

export default defineConfig({
  plugins: [react(), tailwindcss(), paletteTypesPlugin()],
  // esbuild-wasm ships a prebuilt .wasm that must not be pre-bundled/optimized.
  optimizeDeps: { exclude: ['esbuild-wasm'] },
  build: {
    rollupOptions: {
      output: {
        // Split heavy, route-specific libraries so the initial bundle stays lean.
        manualChunks(id) {
          if (id.includes('@scalar')) return 'scalar';
          if (id.includes('monaco-editor')) return 'monaco';
          // GrapesJS and its Backbone/Underscore stack are only ever needed by
          // the lazily-routed Mode A builder. Without this they land in `vendor`,
          // which every page loads eagerly — over a megabyte for nothing.
          if (
            id.includes('/grapesjs/') ||
            id.includes('/backbone/') ||
            id.includes('/backbone-undo/') ||
            id.includes('/underscore/')
          ) {
            return 'grapesjs';
          }
          if (id.includes('esbuild-wasm')) return 'esbuild';
          if (id.includes('recharts') || id.includes('/d3') || id.includes('victory')) return 'charts';
          if (id.includes('@rjsf') || id.includes('/ajv')) return 'rjsf';
          if (id.includes('@microsoft/signalr')) return 'signalr';
          if (id.includes('node_modules')) return 'vendor';
          return undefined;
        },
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      // Dev: admin-api from compose (override) listens on 5002.
      '/api': {
        target: 'http://localhost:5002',
        changeOrigin: true,
      },
      // Dev: the chat SignalR hub lives on content-api (5003); ws:true upgrades.
      '/hub': {
        target: 'http://localhost:5003',
        changeOrigin: true,
        ws: true,
      },
    },
  },
});
