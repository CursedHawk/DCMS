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
        /*
         * Split heavy, route-specific libraries out of the shared `vendor` chunk.
         *
         * `vendor` is the catch-all and is downloaded on first paint, so anything
         * left in it is paid for on every page including the dashboard. Every rule
         * above it names a library that only one lazily-routed page needs (see
         * routes.tsx, where all pages but the dashboard are React.lazy).
         *
         * Keeping the catch-all matters: without it Rollup folds shared modules —
         * React itself, in practice — into whichever named chunk it likes, which
         * gives the entry a static edge to that chunk and drags the whole thing
         * eagerly back in. One predictable shared chunk is the point.
         */
        manualChunks(id) {
          // Vite's dynamic-import preload helper is a tiny synthetic module the entry
          // always imports. Left unassigned, Rollup folds it into whichever manual
          // chunk happens to need it — it picked `monaco`, which gave the entry a
          // static edge to a 3.8 MB chunk and had every page, dashboard included,
          // modulepreload the whole editor. Pinning it to the always-loaded shared
          // chunk keeps that edge harmless.
          if (id.includes('vite/preload-helper')) return 'vendor';
          if (!id.includes('node_modules')) return undefined;

          if (id.includes('@scalar')) return 'scalar';
          if (id.includes('monaco-editor')) return 'monaco';
          // GrapesJS and its Backbone/Underscore stack are only ever needed by the
          // lazily-routed Mode A builder.
          if (
            id.includes('/grapesjs/') ||
            id.includes('/backbone/') ||
            id.includes('/backbone-undo/') ||
            id.includes('/underscore/')
          ) {
            return 'grapesjs';
          }
          // Source parsing/formatting for the builder and IDE, reached only through
          // @dcms/gjs-parse and @dcms/gjs-schema.
          if (
            id.includes('/css-tree/') ||
            id.includes('/htmlparser2/') ||
            id.includes('/js-beautify/') ||
            id.includes('/zod/')
          ) {
            return 'editor-libs';
          }
          if (id.includes('esbuild-wasm')) return 'esbuild';
          // TipTap and the ProseMirror stack under it, reached only through the lazily
          // imported RichTextEditor on the content route. `prosemirror-` catches the dozen
          // transitive packages @tiptap/pm re-exports, which would otherwise land in `vendor`
          // and be downloaded on first paint by every page including the dashboard.
          if (
            id.includes('@tiptap') ||
            id.includes('prosemirror') ||
            id.includes('tiptap-markdown') ||
            id.includes('/markdown-it')
          ) {
            return 'tiptap';
          }
          if (id.includes('recharts') || id.includes('/d3') || id.includes('victory')) return 'charts';
          if (id.includes('@rjsf') || id.includes('/ajv')) return 'rjsf';
          if (id.includes('@microsoft/signalr')) return 'signalr';
          return 'vendor';
        },
      },
    },
  },
  server: {
    port: 5173,
    proxy: {
      // Dev: admin-api from compose (override) listens on 5002. ws:true because the
      // notification hub is mounted under /api/hub/notifications -- in production that lets
      // it ride the existing /api edge route instead of needing its own.
      '/api': {
        target: 'http://localhost:5002',
        changeOrigin: true,
        ws: true,
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
